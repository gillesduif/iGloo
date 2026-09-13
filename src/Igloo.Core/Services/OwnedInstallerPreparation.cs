using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Core.Services;

/// <summary>Configuration rendered only from the durable, verified creation receipt.</summary>
public sealed record PreparedOwnedInstaller(
    InstallationTargetClaim Claim, string ManifestJson, InstallerBootSpec BootSpec, InstallerConfig Config);

/// <summary>Orders owned target creation before configuration; does not use legacy disk staging.</summary>
public sealed class OwnedInstallerPreparation(IInstallationTargetPreparer preparer)
{
    private readonly IInstallationTargetPreparer _preparer = preparer
        ?? throw new ArgumentNullException(nameof(preparer));
    private const int MaximumManifestBytes = 16 * 1024 * 1024;

    public async Task<PreparedOwnedInstaller> PrepareAsync(
        IDistroPlugin plugin, InstallationTargetRequest request, string manifestPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        InstallationTargetValidation.ValidateRequest(request);
        request = request with
        {
            ExpectedLayout = request.ExpectedLayout with { Partitions = request.ExpectedLayout.Partitions.ToArray() },
        };
        if (plugin is not IInstallationTargetConsumer consumer
            || consumer.TargetRequirement is not (InstallationTargetRequirement.CreatedRoot
                or InstallationTargetRequirement.CreatedRootAndEsp))
            throw new InvalidOperationException("The plugin must explicitly require an owned installation target.");
        var requireEsp = consumer.TargetRequirement == InstallationTargetRequirement.CreatedRootAndEsp;
        InstallationTargetValidation.ValidateRequest(request);
        if (requireEsp && request.EspPartitionGuid is null)
            throw new InvalidDataException("The plugin requires an explicitly selected ESP.");

        // Unsupported boot capability or invalid migration input must fail before
        // preparation, which can create a partition even if later staging fails.
        var bootSpec = plugin.GetInstallerBootSpec();
        var pendingJson = await ReadAsync(manifestPath, ct).ConfigureAwait(false);
        var pending = JsonSerializer.Deserialize<MigrationManifest>(pendingJson)
            ?? throw new InvalidDataException("The pending migration manifest is missing.");
        if (!string.Equals(pending.DistroId, plugin.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The pending manifest names a different distribution.");
        await InstallationTargetManifest.ValidatePendingAsync(manifestPath, request.InstallationId, ct)
            .ConfigureAwait(false);

        var returned = await _preparer.PrepareAsync(request, manifestPath, ct).ConfigureAwait(false);
        var json = await ReadAsync(manifestPath, ct).ConfigureAwait(false);
        var claim = InstallationTargetManifest.ReadClaim(json, request.InstallationId, requireEsp);
        _ = InstallationTargetValidation.CaptureCreated(request, claim.Disk, claim.RootPartitionGuid);
        if (claim.EspPartitionGuid != request.EspPartitionGuid)
            throw new InvalidDataException("The serialized receipt changed the authorized ESP.");
        InstallationTargetValidation.ValidateClaim(returned, requireEsp);
        InstallationTargetValidation.ValidateUnchangedLayout(returned.Disk, claim.Disk);
        if (returned.InstallationId != claim.InstallationId
            || returned.RootPartitionGuid != claim.RootPartitionGuid
            || returned.EspPartitionGuid != claim.EspPartitionGuid)
            throw new InvalidDataException("The serialized receipt differs from the preparation result.");
        var actual = await _preparer.ReadLayoutAsync(claim.Disk.DiskGuid, ct).ConfigureAwait(false);
        InstallationTargetValidation.ValidateUnchangedLayout(claim.Disk, actual);
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(json)
            ?? throw new InvalidDataException("The prepared migration manifest is missing.");
        if (!string.Equals(manifest.DistroId, plugin.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The distribution changed during preparation.");
        var config = await plugin.RenderInstallerConfigAsync(manifest, ct).ConfigureAwait(false);
        if (!string.Equals(json, await ReadAsync(manifestPath, ct).ConfigureAwait(false), StringComparison.Ordinal))
            throw new InvalidDataException("Installer state changed during config rendering.");
        InstallationTargetValidation.ValidateUnchangedLayout(claim.Disk,
            await _preparer.ReadLayoutAsync(claim.Disk.DiskGuid, ct).ConfigureAwait(false));
        return new PreparedOwnedInstaller(claim, json, bootSpec, config);
    }

    private static async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            if (stream.Length > MaximumManifestBytes)
                throw new InvalidDataException("Installer manifest is too large.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
    }
}
