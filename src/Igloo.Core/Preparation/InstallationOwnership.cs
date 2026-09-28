using System.Collections.Immutable;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Preparation;

public sealed record InstallationOwnershipV1(PreparedLayoutV1 Layout, InstallerEspBindingV1 Esp,
    string? IsoFileSystemUuid);
public sealed record ResolvedInstallationV1(Guid GenerationId, string DiskDevice, string RootDevice,
    string LinuxEspDevice, string WindowsEspDevice, string PayloadDevice, string? IsoDevice,
    Guid RootPartitionGuid, Guid LinuxEspPartitionGuid);
// Produced only after the owned-root format transition and independent readback, then persisted.
// It is not a CanonicalVolumeIdentityV1: Linux filesystem UUID and Windows volume GUID differ.
public sealed record RootFileSystemReceiptV1(Guid GenerationId, PreparedGptPartitionV1 Partition,
    string FileSystem, Guid FileSystemUuid);

// Common ownership/translation, not a common installer engine. This performs no mounts or writes.
public static class InstallationOwnership
{
    public static Observation<ResolvedInstallationV1> Resolve(InstallationOwnershipV1 binding, Guid expectedGeneration,
        Observation<InstallerRuntimeInventoryV1> inventory) => ResolveCore(binding, expectedGeneration, inventory, null);

    public static Observation<ResolvedInstallationV1> ResolveFormattedRoot(InstallationOwnershipV1 binding,
        Guid expectedGeneration, RootFileSystemReceiptV1 receipt, Observation<InstallerRuntimeInventoryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return ResolveCore(binding, expectedGeneration, inventory, receipt);
    }

    public static Observation<RootFileSystemReceiptV1> VerifyRootFormat(InstallationOwnershipV1 binding,
        Guid expectedGeneration, Observation<InstallerRuntimeInventoryV1> before,
        Observation<RootFileSystemReceiptV1> providerResult, Observation<InstallerRuntimeInventoryV1> independentReadback)
    {
        ArgumentNullException.ThrowIfNull(providerResult);
        var fresh = Resolve(binding, expectedGeneration, before);
        if (fresh.Availability != ObservationAvailability.Available)
            return Observations.Failure<RootFileSystemReceiptV1>(fresh.Availability, fresh.Code!);
        if (providerResult.Availability != ObservationAvailability.Available) return providerResult;
        var verified = ResolveFormattedRoot(binding, expectedGeneration, providerResult.Value, independentReadback);
        return verified.Availability == ObservationAvailability.Available ? providerResult :
            Observations.Failure<RootFileSystemReceiptV1>(verified.Availability, verified.Code!);
    }

    private static Observation<ResolvedInstallationV1> ResolveCore(InstallationOwnershipV1 binding, Guid expectedGeneration,
        Observation<InstallerRuntimeInventoryV1> inventory, RootFileSystemReceiptV1? formattedRoot)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(inventory);
        var layout = binding.Layout;
        if (expectedGeneration != layout.Plan.GenerationId || binding.Esp.GenerationId != expectedGeneration ||
            binding.Esp.TargetDisk != layout.Plan.TargetDisk || binding.Esp.WindowsEsp.Volume != layout.Plan.WindowsEsp ||
            !layout.Partitions.Any(p => p.Role == PreparationRole.LinuxEsp && p.Identity == binding.Esp.LinuxEsp.Volume) ||
            !layout.Partitions.Any(p => p.Role == PreparationRole.Payload && p.Identity == binding.Esp.Payload.Volume) ||
            !string.Equals(binding.Esp.Payload.FileSystemUuid, layout.PayloadFileSystemUuid, StringComparison.OrdinalIgnoreCase))
            return Fail(ObservationAvailability.Ambiguous, "InstallationGenerationOrBindingChanged");
        var structure = PreparedStorageOwnership.VerifyStructure(layout);
        if (structure.Availability != ObservationAvailability.Available) return Fail(structure.Availability, structure.Code!);
        var esp = InstallerEspBinding.Resolve(binding.Esp, inventory);
        if (esp.Availability != ObservationAvailability.Available) return Fail(esp.Availability, esp.Code!);
        var observed = inventory.Value;
        var ownership = layout.StorageOwnership!;
        var expected = ownership.PreservedPartitions.Concat(ownership.CreatedPartitions.Select(r => r.Identity)).ToImmutableArray();
        foreach (var identity in expected)
        {
            var match = observed.Partitions.SingleOrDefault(p => p.PartitionGuid == identity.PartitionGuid);
            if (match is null) return Fail(ObservationAvailability.Absent, "InstallationPartitionMissing");
            var disk = observed.Disks.Single(d => d.DevicePath == match.DiskDevicePath);
            if (disk.GptDiskGuid != identity.Disk.GptDiskGuid || disk.SizeBytes != identity.Disk.SizeBytes ||
                disk.LogicalSectorSize != identity.Disk.LogicalSectorSize || match.PartitionType != identity.PartitionType ||
                match.OffsetBytes != identity.OffsetBytes || match.SizeBytes != identity.SizeBytes)
                return Fail(ObservationAvailability.Ambiguous, "InstallationPartitionChanged");
        }
        if (observed.Partitions.Any(p => expected.Any(e => e.Disk.GptDiskGuid == observed.Disks.Single(d => d.DevicePath == p.DiskDevicePath).GptDiskGuid) &&
            !expected.Any(e => e.PartitionGuid == p.PartitionGuid)))
            return Fail(ObservationAvailability.Ambiguous, "InstallationPartitionSetChanged");
        var root = ownership.CreatedPartitions.Single(r => r.Role == PreparationRole.LinuxRoot).Identity;
        var rootObserved = observed.Partitions.Single(p => p.PartitionGuid == root.PartitionGuid);
        // A retry with a now-formatted root needs an explicit resume protocol, not fresh-install permission.
        if (formattedRoot is null && rootObserved.FileSystem.Availability != ObservationAvailability.Absent)
            return Fail(ObservationAvailability.Ambiguous, "OwnedRootAlreadyHasFilesystem");
        if (formattedRoot is not null)
        {
            if (formattedRoot.GenerationId != expectedGeneration || formattedRoot.Partition != root ||
                formattedRoot.FileSystem != "EXT4" || formattedRoot.FileSystemUuid == Guid.Empty)
                return Fail(ObservationAvailability.Ambiguous, "RootFilesystemReceiptInvalid");
            if (rootObserved.FileSystem.Availability != ObservationAvailability.Available)
                return Fail(rootObserved.FileSystem.Availability, "RootFilesystemReadbackUnavailable");
            if (rootObserved.FileSystem.Value.Type != "EXT4" ||
                !Guid.TryParseExact(rootObserved.FileSystem.Value.Uuid, "D", out var uuid) || uuid != formattedRoot.FileSystemUuid)
                return Fail(ObservationAvailability.Ambiguous, "RootFilesystemChanged");
        }
        var iso = ownership.CreatedPartitions.SingleOrDefault(r => r.Role == PreparationRole.Iso);
        string? isoDevice = null;
        if (iso is not null)
        {
            var match = observed.Partitions.Single(p => p.PartitionGuid == iso.Identity.PartitionGuid);
            if (match.FileSystem.Availability != ObservationAvailability.Available)
                return Fail(match.FileSystem.Availability, "IsoFilesystemUnavailable");
            if (string.IsNullOrWhiteSpace(binding.IsoFileSystemUuid) || match.FileSystem.Value.Type != "NTFS" ||
                !string.Equals(match.FileSystem.Value.Uuid, binding.IsoFileSystemUuid, StringComparison.OrdinalIgnoreCase))
                return Fail(ObservationAvailability.Ambiguous, "IsoFilesystemChanged");
            isoDevice = match.DevicePath;
        }
        else if (binding.IsoFileSystemUuid is not null) return Fail(ObservationAvailability.Ambiguous, "UnplannedIsoBinding");
        return Observations.Available(new ResolvedInstallationV1(expectedGeneration, rootObserved.DiskDevicePath,
            rootObserved.DevicePath, esp.Value.LinuxEspDevice, esp.Value.WindowsEspDevice, esp.Value.PayloadDevice,
            isoDevice, root.PartitionGuid, binding.Esp.LinuxEsp.Volume.PartitionGuid));
    }

    private static Observation<ResolvedInstallationV1> Fail(ObservationAvailability state, string code) => Observations.Failure<ResolvedInstallationV1>(state, code);
}
