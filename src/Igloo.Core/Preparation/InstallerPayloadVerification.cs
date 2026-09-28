using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Core.Preparation;

public sealed record PreparedInstallerPayloadFileV1(PreparationRole Role, CanonicalFileIdentityV1 File);
public sealed record InstallerPayloadManifestV1(Guid GenerationId, ImmutableArray<PreparedInstallerPayloadFileV1> Files);
public sealed record InstallerPayloadReadbackV1(Guid PartitionGuid, string RelativePath, long Length, string Sha256);

// Content verification after an independently verified mount/read. This does not perform the
// mount, make the transport trustworthy, or replace the durable generation/manifest binding.
public static class InstallerPayloadVerification
{
    public static Observation<bool> Verify(InstallationOwnershipV1 ownership, Guid expectedGeneration,
        InstallerPayloadManifestV1 manifest, Observation<InstallerRuntimeInventoryV1> inventory,
        Observation<ImmutableArray<InstallerPayloadReadbackV1>> independentlyReadFiles) =>
        VerifyCore(ownership, expectedGeneration, manifest, inventory, independentlyReadFiles, null);

    public static Observation<bool> VerifyForFormattedRoot(InstallationOwnershipV1 ownership, Guid expectedGeneration,
        RootFileSystemReceiptV1 root, InstallerPayloadManifestV1 manifest, Observation<InstallerRuntimeInventoryV1> inventory,
        Observation<ImmutableArray<InstallerPayloadReadbackV1>> independentlyReadFiles)
    {
        ArgumentNullException.ThrowIfNull(root);
        return VerifyCore(ownership, expectedGeneration, manifest, inventory, independentlyReadFiles, root);
    }

    private static Observation<bool> VerifyCore(InstallationOwnershipV1 ownership, Guid expectedGeneration,
        InstallerPayloadManifestV1 manifest, Observation<InstallerRuntimeInventoryV1> inventory,
        Observation<ImmutableArray<InstallerPayloadReadbackV1>> independentlyReadFiles, RootFileSystemReceiptV1? root)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(independentlyReadFiles);
        var resolved = root is null ? InstallationOwnership.Resolve(ownership, expectedGeneration, inventory) :
            InstallationOwnership.ResolveFormattedRoot(ownership, expectedGeneration, root, inventory);
        if (resolved.Availability != ObservationAvailability.Available) return Fail(resolved.Availability, resolved.Code!);
        if (manifest.GenerationId != expectedGeneration || manifest.Files.IsDefaultOrEmpty ||
            manifest.Files.Any(f => f.Role is not (PreparationRole.Payload or PreparationRole.Iso) || !ValidFile(f.File)))
            return Fail(ObservationAvailability.Ambiguous, "InstallerPayloadManifestInvalid");
        if (independentlyReadFiles.Availability != ObservationAvailability.Available)
            return Fail(independentlyReadFiles.Availability, "InstallerPayloadReadUnavailable");
        var files = independentlyReadFiles.Value;
        if (files.IsDefault || files.Length != manifest.Files.Length ||
            manifest.Files.Select(f => (f.Role, PathKey(f.File.RelativePath))).Distinct().Count() != manifest.Files.Length ||
            files.Select(f => (f.PartitionGuid, PathKey(f.RelativePath))).Distinct().Count() != files.Length)
            return Fail(ObservationAvailability.Ambiguous, "InstallerPayloadSetMismatch");
        foreach (var expected in manifest.Files)
        {
            var receipt = ownership.Layout.Partitions.SingleOrDefault(r => r.Role == expected.Role);
            if (receipt is null || receipt.Identity.VolumeGuid != expected.File.VolumeGuid)
                return Fail(ObservationAvailability.Ambiguous, "InstallerPayloadVolumeMismatch");
            var actual = files.SingleOrDefault(f => f.PartitionGuid == receipt.Identity.PartitionGuid && PathKey(f.RelativePath) == PathKey(expected.File.RelativePath));
            if (actual is null) return Fail(ObservationAvailability.Absent, "InstallerPayloadMissing");
            if (actual.Length != expected.File.Length || !string.Equals(actual.Sha256, expected.File.Sha256, StringComparison.OrdinalIgnoreCase))
                return Fail(ObservationAvailability.Ambiguous, "InstallerPayloadContentChanged");
        }
        return Observations.Available(true);
    }

    private static bool ValidFile(CanonicalFileIdentityV1 file) => file.VolumeGuid != Guid.Empty && file.Length > 0 &&
        file.Sha256 is { Length: 64 } && file.Sha256.All(Uri.IsHexDigit) && file.RelativePath.StartsWith('\\') &&
        !file.RelativePath.Contains("..", StringComparison.Ordinal) && !file.RelativePath.Contains(':', StringComparison.Ordinal) &&
        !file.RelativePath.Contains('/', StringComparison.Ordinal);
    private static string PathKey(string path) => path.Replace('/', '\\').ToUpperInvariant();
    private static Observation<bool> Fail(ObservationAvailability state, string code) => Observations.Failure<bool>(state, code);
}
