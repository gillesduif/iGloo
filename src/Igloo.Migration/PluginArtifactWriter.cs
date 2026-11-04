using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging;

namespace Igloo.Migration;

/// <summary>Writes the existing local installer and first-boot artifacts. Never used by Fleet Phase 0.</summary>
public static class PluginArtifactWriter
{
    private static readonly string[] FedoraAgentFiles =
        ["first-boot.sh", "agent.py", "display-apply.py", "igloo_boot.py"];
    private static readonly string[] DebianAgentFiles =
        ["first-boot.sh", "agent.py", "display-apply.py", "display-apply-gnome.py",
            "igloo_boot.py", "igloo-first-boot.service",
            "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz"];
    private static readonly string[] MintAgentFiles =
        ["first-boot.sh", "agent.py", "display-apply.py", "display-apply-gnome.py",
            "igloo_boot.py", "igloo-first-boot.service", "igloo-verify-seed.py",
            "igloo-bootstrap.sh", "igloo-bootstrap.service",
            "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz"];

    public static async Task WriteAsync(IDistroPlugin plugin, MigrationManifest manifest,
        string stagingDirectory, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(logger);
        // Obtain and validate both artifacts before writing any part of the seed.
        // A missing first-boot agent must fail staging, not leave an apparently
        // complete installer config that silently omits migration after reboot.
        var installerConfig = await plugin.RenderInstallerConfigAsync(manifest, ct).ConfigureAwait(false);
        var agentPayload = await plugin.GetAgentPayloadAsync(ct).ConfigureAwait(false);
        var configName = SafeRelativePath(installerConfig.FileName);
        if (configName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || installerConfig.Contents.IsEmpty)
            throw new InvalidDataException("Installer config must be a nonempty root-level file.");
        var extraPaths = installerConfig.Extras.Select(extra =>
            SafeRelativePath(extra.RelativePath)).ToArray();
        if (extraPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != extraPaths.Length ||
            extraPaths.Contains(configName, StringComparer.OrdinalIgnoreCase) ||
            installerConfig.Extras.Any(extra => extra.Contents.IsEmpty))
            throw new InvalidDataException("Installer extras contain an empty or duplicate file.");
        var staged = agentPayload.Files.Select(f => SafeRelativePath(f.RelativePath)).ToArray();
        if (staged.Distinct(StringComparer.OrdinalIgnoreCase).Count() != staged.Length ||
            agentPayload.Files.Any(f => f.Contents.IsEmpty))
            throw new InvalidDataException("Agent payload has an empty or duplicate file.");
        var required = plugin.Id switch
        {
            "fedora-kde" => FedoraAgentFiles,
            "debian" => DebianAgentFiles,
            "linuxmint-cinnamon" => MintAgentFiles,
            _ => [],
        };
        foreach (var name in required)
            if (!staged.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Required {plugin.Id} first-boot file is missing: {name}");

        var ksPath = Path.Join(stagingDirectory, configName);
        await File.WriteAllBytesAsync(ksPath, installerConfig.Contents.ToArray(), ct).ConfigureAwait(false);
        logger.LogInformation("Installer config written to {Path}", ksPath);

        for (var index = 0; index < installerConfig.Extras.Count; index++)
        {
            var extra = installerConfig.Extras[index];
            var extraPath = Path.Join(stagingDirectory, extraPaths[index]);
            Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
            await File.WriteAllBytesAsync(extraPath, extra.Contents.ToArray(), ct).ConfigureAwait(false);
        }

        // First-boot agent files.
        var agentDir = Path.Join(stagingDirectory, "igloo-agent");
        Directory.CreateDirectory(agentDir);

        foreach (var agentFile in agentPayload.Files)
        {
            var filePath = Path.Join(agentDir, agentFile.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllBytesAsync(filePath, agentFile.Contents.ToArray(), ct).ConfigureAwait(false);
        }

        logger.LogInformation("Agent payload written to {Dir}: {Files}",
            agentDir, string.Join(", ", staged));
    }

    private static string SafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException("Artifact path must be relative to its staging root.");
        var segments = value.Split(['/', '\\']);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Artifact path contains an invalid component.");
        return Path.Join(segments);
    }
}
