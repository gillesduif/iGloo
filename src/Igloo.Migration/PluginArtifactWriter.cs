using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging;

namespace Igloo.Migration;

/// <summary>Writes the existing local installer and first-boot artifacts. Never used by Fleet Phase 0.</summary>
public static class PluginArtifactWriter
{
    private static readonly string[] RequiredAgentFiles =
        ["agent.py", "igloo_boot.py", "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz"];

    public static async Task WriteAsync(IDistroPlugin plugin, MigrationManifest manifest,
        string stagingDirectory, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(logger);
        // Kickstart (or preseed / Calamares config, depending on the distro).
        var installerConfig = await plugin.RenderInstallerConfigAsync(manifest, ct).ConfigureAwait(false);
        var ksPath = Path.Join(stagingDirectory, installerConfig.FileName);
        await File.WriteAllBytesAsync(ksPath, installerConfig.Contents.ToArray(), ct).ConfigureAwait(false);
        logger.LogInformation("Installer config written to {Path}", ksPath);

        foreach (var extra in installerConfig.Extras)
        {
            var extraPath = Path.Join(stagingDirectory, extra.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
            await File.WriteAllBytesAsync(extraPath, extra.Contents.ToArray(), ct).ConfigureAwait(false);
        }

        // First-boot agent files.
        var agentPayload = await plugin.GetAgentPayloadAsync(ct).ConfigureAwait(false);
        var agentDir = Path.Join(stagingDirectory, "igloo-agent");
        Directory.CreateDirectory(agentDir);

        foreach (var agentFile in agentPayload.Files)
        {
            var filePath = Path.Join(agentDir, agentFile.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllBytesAsync(filePath, agentFile.Contents.ToArray(), ct).ConfigureAwait(false);
        }

        var staged = agentPayload.Files.Select(f => f.RelativePath).ToList();
        logger.LogInformation("Agent payload written to {Dir}: {Files}",
            agentDir, string.Join(", ", staged));

        foreach (var required in RequiredAgentFiles
                     .Where(f => !staged.Contains(f, StringComparer.OrdinalIgnoreCase)))
        {
                logger.LogError(
                    "Agent payload is missing {File} - the plugin could not find it. " +
                    "The first boot will run without it.", required);
        }
    }
}
