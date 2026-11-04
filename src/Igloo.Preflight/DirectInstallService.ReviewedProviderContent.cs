using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;

namespace Igloo.Preflight;

public sealed partial class DirectInstallService
{
    // This only refreshes local staging after the reviewed Windows-created
    // partitions have exact receipts. It cannot prepare a disk or boot entry.
    internal static async Task<ReviewedContentHandoffV1> StageReviewedProviderContentAsync(
        ReviewedPartitionCreationService reviewed,
        PreparationCheckpointReference checkpoint,
        IDistroPlugin provider,
        InstallerBootSpec bootSpec,
        long requestedLinuxBytes,
        string stagingDirectory,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(bootSpec);
        if (provider is not IReviewedInstallerConfigProvider boundProvider)
            throw new NotSupportedException("The selected provider has no reviewed installer-created-root config.");
        if (!Path.IsPathFullyQualified(stagingDirectory))
            throw new InvalidDataException("The reviewed installer staging directory is not absolute.");
        var stage = Path.GetFullPath(stagingDirectory);
        RequirePlainDirectoryTree(stage);
        InspectStagingFiles(stage);

        var storage = reviewed.ObserveInstallerHandoff(checkpoint, ct);
        var root = storage.InstallerRoot;
        if (root.GenerationId != storage.GenerationId ||
            root.TargetDiskGuid != storage.TargetDisk.GptDiskGuid ||
            root.LinuxEspPartitionGuid != storage.LinuxEsp.PartitionGuid ||
            root.RootMinimumBytes != (ulong)requestedLinuxBytes ||
            requestedLinuxBytes <= 0)
            throw new InvalidDataException("The reviewed installer free extent differs from its storage handoff.");

        var manifestPath = Path.Join(stage, "migration-manifest.json");
        RequirePlainFile(manifestPath);
        if (new FileInfo(manifestPath).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("The reviewed migration manifest has an invalid size.");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
        if (manifestBytes.Length is <= 0 or > 8 * 1024 * 1024)
            throw new InvalidDataException("The reviewed migration manifest has an invalid size.");
        MigrationManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MigrationManifest>(manifestBytes) ??
                throw new JsonException("The reviewed migration manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The reviewed migration manifest is invalid.", error);
        }
        if (manifest.SchemaVersion != 1 || manifest.Hardware is null ||
            manifest.User is null || manifest.Files is null ||
            !string.Equals(manifest.DistroId, provider.Id, StringComparison.Ordinal) ||
            !string.Equals(manifest.Hardware.InstallMode, "dual-boot", StringComparison.Ordinal) ||
            manifest.Hardware.TargetDiskGuid != storage.TargetDisk.GptDiskGuid ||
            storage.TargetDisk.SizeBytes > long.MaxValue ||
            manifest.Hardware.TargetDiskBytes != (long)storage.TargetDisk.SizeBytes ||
            manifest.Hardware.LinuxPartitionSizeGb <= 0 ||
            (long)manifest.Hardware.LinuxPartitionSizeGb * 1024 * 1024 * 1024 != requestedLinuxBytes)
            throw new InvalidDataException("The staged manifest differs from the reviewed installer target.");

        var declaredBoot = provider.GetInstallerBootSpec();
        if (!string.Equals(JsonSerializer.Serialize(bootSpec with { IsoSha256 = declaredBoot.IsoSha256 }),
                JsonSerializer.Serialize(declaredBoot), StringComparison.Ordinal) ||
            bootSpec.ConfigDelivery != ConfigDelivery.InjectIntoInitrd ||
            string.IsNullOrWhiteSpace(bootSpec.InitrdConfigPath))
            throw new InvalidDataException("The reviewed installer boot recipe differs from the selected provider.");

        var config = await boundProvider.RenderReviewedInstallerConfigAsync(manifest, root, ct)
            .ConfigureAwait(false);
        var expectedExtras = bootSpec.InitrdExtraPaths ??
            throw new InvalidDataException("The reviewed initrd extra list is unavailable.");
        if (config.Extras is null || config.Extras.Any(extra =>
                extra is null || string.IsNullOrWhiteSpace(extra.RelativePath)))
            throw new InvalidDataException("The reviewed provider extra list is incomplete.");
        var returnedExtras = config.Extras.Select(extra => extra.RelativePath.Replace('\\', '/'))
            .ToArray();
        if (!string.Equals(config.FileName, bootSpec.InitrdConfigPath, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(config.FileName), config.FileName, StringComparison.Ordinal) ||
            config.Contents.IsEmpty || config.Contents.Length > 8 * 1024 * 1024 ||
            expectedExtras.Any(path => !returnedExtras.Contains(path.Replace('\\', '/'),
                StringComparer.Ordinal)) ||
            config.Extras.Any(extra => extra.Contents.IsEmpty ||
                extra.Contents.Length > 8 * 1024 * 1024))
            throw new InvalidDataException("The reviewed provider output omits a declared initrd file or is invalid.");
        // Provider buffers can be backed by mutable arrays. Own a snapshot before
        // comparing, replacing or hashing any staged file.
        var artifacts = new List<(string RelativePath, byte[] Contents)>
        {
            (config.FileName, config.Contents.ToArray()),
        };
        artifacts.AddRange(config.Extras.Select(extra =>
            (extra.RelativePath, extra.Contents.ToArray())));
        if (artifacts.Select(item => item.RelativePath.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != artifacts.Count)
            throw new InvalidDataException("The reviewed provider output contains duplicate paths.");
        foreach (var extra in returnedExtras)
        {
            if (!ReviewedSeedPathRules.TrySegments("\\" + extra.Replace('/', '\\'), out var parts) ||
                !(parts.Length == 1 && parts[0].StartsWith("igloo-", StringComparison.Ordinal) &&
                    parts[0].EndsWith(".sh", StringComparison.Ordinal) ||
                  parts.Length == 2 && parts[0] == "igloo-initrd" &&
                    expectedExtras.Contains(extra, StringComparer.Ordinal) &&
                    (parts[1].EndsWith(".sh", StringComparison.Ordinal) ||
                     parts[1].EndsWith(".py", StringComparison.Ordinal))))
                throw new InvalidDataException("The reviewed provider extra is outside declared helper paths.");
        }

        // Validate the current media pin, boot recipe and sizing before touching
        // even the local stage. The final observation repeats this after replacement.
        var before = reviewed.ObserveContentHandoff(checkpoint, storage, bootSpec,
            requestedLinuxBytes, stage, ct);
        var previousPaths = before.StagedFiles.Select(file => file.RelativePath.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        if (artifacts.Any(item => !previousPaths.Contains(item.RelativePath.Replace('\\', '/'))))
            throw new InvalidDataException("The reviewed provider output has no preexisting staged file receipt.");
        // A provider call is read-only; verify the same exact storage before
        // replacing the ordinary staged preseed and initrd extras.
        if (reviewed.ObserveInstallerHandoff(checkpoint, ct) != storage)
            throw new InvalidOperationException("The reviewed installer storage changed before config staging.");
        var destinations = artifacts.Select(item =>
            (Destination: RequireStagedArtifact(stage, item.RelativePath), item.Contents)).ToArray();
        foreach (var (destination, contents) in destinations)
        {
            ct.ThrowIfCancellationRequested();
            await ReplaceStagedFileAsync(destination, contents, ct).ConfigureAwait(false);
        }
        if (reviewed.ObserveInstallerHandoff(checkpoint, ct) != storage)
            throw new InvalidOperationException("The reviewed installer storage changed during config staging.");
        if (!(await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false))
            .AsSpan().SequenceEqual(manifestBytes))
            throw new InvalidDataException("The staged migration manifest changed during config rendering.");

        var content = reviewed.ObserveContentHandoff(checkpoint, storage, bootSpec,
            requestedLinuxBytes, stage, ct);
        if (!(await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false))
            .AsSpan().SequenceEqual(manifestBytes))
            throw new InvalidDataException("The staged migration manifest changed during content inspection.");
        foreach (var (relative, bytes) in artifacts)
        {
            var portable = relative.Replace('\\', '/');
            var matches = content.StagedFiles.Where(file =>
                string.Equals(file.RelativePath.Replace('\\', '/'), portable,
                    StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].SizeBytes != bytes.Length ||
                !string.Equals(matches[0].Sha256,
                    Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal))
                throw new InvalidDataException("The reviewed installer config changed after staging.");
        }
        return content;
    }

    private static string RequireStagedArtifact(string stage, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) ||
            !ReviewedSeedPathRules.TrySegments("\\" + relative.Replace('/', '\\'),
                out var parts))
            throw new InvalidDataException("The reviewed installer artifact path is unsafe.");
        var destination = Path.GetFullPath(Path.Join(stage, Path.Join(parts)));
        if (!destination.StartsWith(stage + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed installer artifact escaped staging.");
        RequirePlainDirectoryTree(Path.GetDirectoryName(destination)!);
        RequirePlainFile(destination);
        return destination;
    }

    private static void RequirePlainDirectoryTree(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The reviewed installer staging directory is unavailable or redirected.");
    }

    private static void RequirePlainFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length <= 0)
            throw new InvalidDataException("A reviewed installer artifact is unavailable or redirected.");
    }

    private static async Task ReplaceStagedFileAsync(string destination,
        ReadOnlyMemory<byte> contents, CancellationToken ct)
    {
        var temporary = destination + ".reviewed-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
            {
                await stream.WriteAsync(contents, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            RequirePlainFile(destination);
            File.Move(temporary, destination, overwrite: true);
            RequirePlainFile(destination);
            if (new FileInfo(destination).Length != contents.Length)
                throw new InvalidDataException("The reviewed installer artifact changed size after replacement.");
            if (!(await File.ReadAllBytesAsync(destination, ct).ConfigureAwait(false))
                .AsSpan().SequenceEqual(contents.Span))
                throw new InvalidDataException("The reviewed installer artifact changed after replacement.");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
