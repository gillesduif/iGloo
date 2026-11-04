using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Core.Recovery;

// Shared storage binding, not a storage mutation/rollback contract or Fleet approval.
public sealed record CanonicalDiskIdentityV1(string UniqueId, uint UniqueIdFormat, uint BusType,
    Guid GptDiskGuid, ulong SizeBytes, uint LogicalSectorSize, uint PhysicalSectorSize);

public sealed record CanonicalVolumeIdentityV1(CanonicalDiskIdentityV1 Disk, Guid PartitionGuid,
    Guid VolumeGuid, Guid PartitionType, ulong OffsetBytes, ulong SizeBytes, string FileSystem);

public sealed record RecoveryTargetBindingV1(CanonicalVolumeIdentityV1 WindowsVolume,
    CanonicalVolumeIdentityV1 TargetVolume);

public sealed record RecoveryVolumeLocatorV1(Guid VolumeGuid, uint? DiskNumber, uint? PartitionNumber,
    char? DriveLetter);

public sealed record CanonicalFileIdentityV1(Guid VolumeGuid, string RelativePath, long Length,
    string Sha256);

public static class CanonicalRecoveryIdentity
{
    public static bool IsValid(CanonicalVolumeIdentityV1? volume) => volume?.Disk is { } disk &&
        !string.IsNullOrWhiteSpace(disk.UniqueId) && disk.UniqueIdFormat is 2 or 3 or 8 &&
        disk.BusType is not (0 or 14 or 15) && IsValidForInstallation(volume);

    // Installation binds GPT disk/partition/volume identities and geometry.
    // Missing hardware metadata does not become a fabricated provider identity,
    // and IsValid/Bind still require that stronger identity for recovery.
    public static bool IsValidForInstallation(CanonicalVolumeIdentityV1? volume) =>
        volume?.Disk is { } disk && disk.GptDiskGuid != Guid.Empty && disk.SizeBytes > 0 &&
        disk.LogicalSectorSize > 0 && disk.PhysicalSectorSize >= disk.LogicalSectorSize &&
        disk.PhysicalSectorSize % disk.LogicalSectorSize == 0 && volume.PartitionGuid != Guid.Empty &&
        volume.VolumeGuid != Guid.Empty && volume.PartitionType != Guid.Empty && volume.SizeBytes > 0 &&
        volume.OffsetBytes <= disk.SizeBytes && volume.SizeBytes <= disk.SizeBytes - volume.OffsetBytes &&
        volume.OffsetBytes % disk.LogicalSectorSize == 0 && volume.SizeBytes % disk.LogicalSectorSize == 0 &&
        !string.IsNullOrWhiteSpace(volume.FileSystem);

    // Ordinals join rows within one successful inventory only; they never enter authority.
    public static Observation<CanonicalVolumeIdentityV1> Bind(Observation<WindowsStorageSnapshot> observation, Guid volumeId)
        => BindCore(observation, volumeId, requireProviderIdentity: true);

    public static Observation<CanonicalVolumeIdentityV1> BindForInstallation(
        Observation<WindowsStorageSnapshot> observation, Guid volumeId)
        => BindCore(observation, volumeId, requireProviderIdentity: false);

    private static Observation<CanonicalVolumeIdentityV1> BindCore(
        Observation<WindowsStorageSnapshot> observation, Guid volumeId, bool requireProviderIdentity)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Availability != ObservationAvailability.Available)
            return Observations.Failure<CanonicalVolumeIdentityV1>(observation.Availability, "StorageInventoryUnavailable");
        var inventory = observation.Value;
        if (volumeId == Guid.Empty || inventory.Disks.IsDefault || inventory.Partitions.IsDefault || inventory.Volumes.IsDefault)
            return Failure(ObservationAvailability.Unavailable, "InvalidInventory");
        var volumes = inventory.Volumes.Where(v => Equal(v.VolumeGuid, volumeId)).ToArray();
        if (volumes.Length > 1) return Failure(ObservationAvailability.Ambiguous, "DuplicateVolume");
        if (volumes.Length == 0) return Failure(inventory.Volumes.Any(v => !Available(v.VolumeGuid))
            ? ObservationAvailability.Unavailable : ObservationAvailability.Absent, "VolumeMissing");
        var volume = volumes[0];
        if (!Available(volume.PartitionGuid)) return Failure(volume.PartitionGuid.Availability, "VolumeOwnerUnavailable");
        if (inventory.Volumes.Count(v => Equal(v.PartitionGuid, volume.PartitionGuid.Value)) != 1)
            return Failure(ObservationAvailability.Ambiguous, "DuplicateVolumeOwner");
        var partitions = inventory.Partitions.Where(p => Equal(p.PartitionGuid, volume.PartitionGuid.Value)).ToArray();
        if (partitions.Length != 1) return Failure(partitions.Length > 1 ? ObservationAvailability.Ambiguous :
            ObservationAvailability.Unavailable, "PartitionOwnerNotUnique");
        var partition = partitions[0];
        if (!Available(partition.DiskNumber)) return Failure(partition.DiskNumber.Availability, "DiskOwnerUnavailable");
        var disks = inventory.Disks.Where(d => Equal(d.Number, partition.DiskNumber.Value)).ToArray();
        if (disks.Length != 1) return Failure(disks.Length > 1 ? ObservationAvailability.Ambiguous :
            ObservationAvailability.Unavailable, "DiskOwnerNotUnique");
        var disk = disks[0];
        if (requireProviderIdentity && disk.IdentityStrength != StorageIdentityStrength.ProviderUniqueIdentity)
            return Failure(ObservationAvailability.Unsupported, "DiskIdentityReduced");
        if (!requireProviderIdentity)
        {
            var installerDisk = InstallerDiskIdentity.Observe(inventory.Disks, partition.DiskNumber.Value);
            if (installerDisk.Availability != ObservationAvailability.Available)
                return Failure(installerDisk.Availability, installerDisk.Code!);
        }
        if (!Available(disk.PartitionStyle)) return Failure(disk.PartitionStyle.Availability, "PartitionStyleUnavailable");
        if (disk.PartitionStyle.Value != 2) return Failure(ObservationAvailability.Unsupported, "GptRequired");
        if (requireProviderIdentity)
            foreach (var state in new[] { disk.UniqueId.Availability, disk.UniqueIdFormat.Availability, disk.BusType.Availability })
                if (state != ObservationAvailability.Available) return Failure(state, "IdentityFactUnavailable");
        foreach (var state in new[] { disk.GptGuid.Availability, disk.Size.Availability, disk.LogicalSectorSize.Availability, disk.PhysicalSectorSize.Availability,
            partition.GptType.Availability, partition.Offset.Availability, partition.Size.Availability, volume.FileSystem.Availability })
            if (state != ObservationAvailability.Available) return Failure(state, "IdentityFactUnavailable");
        if (inventory.Disks.Count(d => Equal(d.GptGuid, disk.GptGuid.Value)) != 1 ||
            (requireProviderIdentity && inventory.Disks.Count(d => Equal(d.UniqueId, disk.UniqueId.Value)) != 1))
            return Failure(ObservationAvailability.Ambiguous, "DuplicateDiskIdentity");
        var identity = new CanonicalVolumeIdentityV1(new(
            Available(disk.UniqueId) ? disk.UniqueId.Value : string.Empty,
            Available(disk.UniqueIdFormat) ? disk.UniqueIdFormat.Value : 0,
            Available(disk.BusType) ? disk.BusType.Value : 0,
            disk.GptGuid.Value, disk.Size.Value, disk.LogicalSectorSize.Value, disk.PhysicalSectorSize.Value),
            volume.PartitionGuid.Value, volumeId, partition.GptType.Value, partition.Offset.Value, partition.Size.Value, volume.FileSystem.Value);
        return (requireProviderIdentity ? IsValid(identity) : IsValidForInstallation(identity))
            ? Observations.Available(identity) : Failure(ObservationAvailability.Unavailable, "InvalidCanonicalIdentity");
    }

    private static bool Available<T>(Observation<T> value) => value.Availability == ObservationAvailability.Available;
    private static bool Equal<T>(Observation<T> value, T expected) => Available(value) && EqualityComparer<T>.Default.Equals(value.Value, expected);
    private static Observation<CanonicalVolumeIdentityV1> Failure(ObservationAvailability state, string code) => Observations.Failure<CanonicalVolumeIdentityV1>(state, code);
}
