using Igloo.Core.Abstractions;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Agent.Targets;

// Pure comparison only: no readers, journal, approval persistence or mutation dependencies.
public static class TargetRevalidator
{
    public static TargetRevalidation Compare(PreparedMigrationPlan plan, ExactTargetBinding? binding,
        Observation<WindowsStorageSnapshot> fresh, Observation<BitLockerVolumeObservation> bitLocker)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(bitLocker);
        if (binding is null) return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.BindingRequired);
        if (binding.SchemaVersion != ExactTargetBinding.CurrentSchemaVersion)
            return Result(TargetMatch.Unsupported, TargetMismatchReason.BindingSchemaUnsupported);
        if (binding.PlanId != plan.PlanId || binding.ApprovalId != plan.ApprovalId || binding.Endpoint != plan.Identity ||
            binding.ProfileRevisionId != plan.ProfileRevisionId || binding.EvidenceHash != plan.EvidenceHash)
            return Result(TargetMatch.Changed, TargetMismatchReason.PlanBindingChanged);
        var stable = binding.Stable;
        var structure = binding.Structure;
        if (stable is null || structure is null || binding.Informational is null ||
            string.IsNullOrWhiteSpace(stable.DiskUniqueId) || stable.DiskGuid == Guid.Empty || stable.PartitionGuid == Guid.Empty ||
            stable.VolumeGuid == Guid.Empty || stable.UniqueIdFormat is not (2 or 3 or 8) ||
            structure.DiskSize == 0 || structure.PartitionSize == 0 || structure.LogicalSectorSize == 0 || structure.PhysicalSectorSize == 0 ||
            structure.PartitionOffset > structure.DiskSize || structure.PartitionSize > structure.DiskSize - structure.PartitionOffset ||
            structure.GptType == Guid.Empty || string.IsNullOrWhiteSpace(structure.FileSystem) || structure.Label is null ||
            binding.PlanId == Guid.Empty || binding.ApprovalId == Guid.Empty || binding.ProfileRevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(binding.EvidenceHash))
            return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.InvalidBinding);
        if (structure.PartitionStyle != 2) return Result(TargetMatch.Unsupported, TargetMismatchReason.PartitionStyleUnsupported);
        if (fresh.Availability != ObservationAvailability.Available)
            return Result(MapAvailability(fresh.Availability), TargetMismatchReason.InventoryUnavailable);
        var inventory = fresh.Value;
        if (inventory.Disks.IsDefault || inventory.Partitions.IsDefault || inventory.Volumes.IsDefault)
            return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.InventoryUnavailable);
        var disks = inventory.Disks.Where(d => EqualsFact(d.UniqueId, stable.DiskUniqueId)).ToArray();
        if (disks.Length > 1) return Result(TargetMatch.Ambiguous, TargetMismatchReason.DuplicateDiskIdentity);
        if (disks.Length == 0)
        {
            if (inventory.Disks.Any(d => d.IdentityStrength == StorageIdentityStrength.Unavailable)) return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.IdentityUnavailable);
            // Locators are negative diagnostic hints only; they can never establish a match.
            return inventory.Disks.Any(d => EqualsFact(d.Number, binding.Informational.DiskNumber))
                ? Result(TargetMatch.Changed, TargetMismatchReason.DiskIdentityChanged)
                : Result(TargetMatch.Missing, TargetMismatchReason.DiskMissing);
        }
        var disk = disks[0];
        if (!Available(disk.PartitionStyle)) return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.IdentityUnavailable);
        if (disk.PartitionStyle.Value != 2) return Result(TargetMatch.Unsupported, TargetMismatchReason.PartitionStyleUnsupported);
        if (disk.IdentityStrength != StorageIdentityStrength.ProviderUniqueIdentity)
            return Result(TargetMatch.Unsupported, TargetMismatchReason.ReducedDiskIdentity);
        if (!Available(disk.GptGuid) || !Available(disk.UniqueIdFormat) || !Available(disk.Number) ||
            !Available(disk.Size) || !Available(disk.LogicalSectorSize) || !Available(disk.PhysicalSectorSize))
            return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.IdentityUnavailable);
        if (inventory.Disks.Count(d => EqualsFact(d.Number, disk.Number.Value)) != 1)
            return Result(TargetMatch.Ambiguous, TargetMismatchReason.DuplicateDiskIdentity);
        if (!EqualsFact(disk.GptGuid, stable.DiskGuid) || !EqualsFact(disk.UniqueIdFormat, stable.UniqueIdFormat))
            return Result(TargetMatch.Changed, TargetMismatchReason.DiskIdentityChanged);
        if (disk.Size.Value != structure.DiskSize || disk.LogicalSectorSize.Value != structure.LogicalSectorSize ||
            disk.PhysicalSectorSize.Value != structure.PhysicalSectorSize)
            return Result(TargetMatch.Changed, TargetMismatchReason.DiskStructureChanged);
        var partitions = inventory.Partitions.Where(p => EqualsFact(p.PartitionGuid, stable.PartitionGuid)).ToArray();
        if (partitions.Length > 1) return Result(TargetMatch.Ambiguous, TargetMismatchReason.DuplicatePartitionIdentity);
        if (partitions.Length == 0)
        {
            if (inventory.Partitions.Any(p => !Available(p.DiskNumber)))
                return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.IdentityUnavailable);
            var onDisk = inventory.Partitions.Where(p => EqualsFact(p.DiskNumber, disk.Number.Value)).ToArray();
            if (onDisk.Any(p => !Available(p.PartitionGuid))) return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.IdentityUnavailable);
            return onDisk.Any(p => EqualsFact(p.Number, binding.Informational.PartitionNumber))
                ? Result(TargetMatch.Changed, TargetMismatchReason.PartitionIdentityChanged)
                : Result(TargetMatch.Missing, TargetMismatchReason.PartitionMissing);
        }
        var partition = partitions[0];
        if (!Available(partition.DiskNumber) || !Available(partition.Offset) || !Available(partition.Size) || !Available(partition.GptType))
            return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.IdentityUnavailable);
        if (partition.DiskNumber.Value != disk.Number.Value) return Result(TargetMatch.Changed, TargetMismatchReason.PartitionIdentityChanged);
        if (partition.Offset.Value != structure.PartitionOffset || partition.Size.Value != structure.PartitionSize || partition.GptType.Value != structure.GptType)
            return Result(TargetMatch.Changed, TargetMismatchReason.PartitionGeometryChanged);
        var volumes = inventory.Volumes.Where(v => EqualsFact(v.VolumeGuid, stable.VolumeGuid)).ToArray();
        if (volumes.Length > 1) return Result(TargetMatch.Ambiguous, TargetMismatchReason.DuplicateVolumeIdentity);
        if (volumes.Length == 0)
        {
            if (inventory.Volumes.Any(v => !Available(v.VolumeGuid))) return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.IdentityUnavailable);
            return inventory.Volumes.Any(v => EqualsFact(v.PartitionGuid, stable.PartitionGuid))
                ? Result(TargetMatch.Changed, TargetMismatchReason.VolumeIdentityChanged)
                : Result(TargetMatch.Missing, TargetMismatchReason.VolumeMissing);
        }
        if (inventory.Volumes.Count(v => EqualsFact(v.PartitionGuid, stable.PartitionGuid)) > 1)
            return Result(TargetMatch.Ambiguous, TargetMismatchReason.DuplicateVolumeIdentity);
        var volume = volumes[0];
        if (!Available(volume.PartitionGuid)) return Result(MapAvailability(volume.PartitionGuid.Availability), TargetMismatchReason.VolumeOwnerUnproven);
        if (volume.PartitionGuid.Value != stable.PartitionGuid) return Result(TargetMatch.Changed, TargetMismatchReason.VolumeIdentityChanged);
        if (!Available(volume.FileSystem) || !Available(volume.Label)) return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.IdentityUnavailable);
        if (volume.FileSystem.Value != structure.FileSystem || volume.Label.Value != structure.Label)
            return Result(TargetMatch.Changed, TargetMismatchReason.FileSystemChanged);
        if (bitLocker.Availability != ObservationAvailability.Available)
            return Result(MapAvailability(bitLocker.Availability), TargetMismatchReason.BitLockerUnavailable);
        var encryption = bitLocker.Value;
        if (encryption.VolumeGuid != stable.VolumeGuid) return Result(TargetMatch.Changed, TargetMismatchReason.BitLockerWrongVolume);
        if (!Available(encryption.ConversionStatus) || !Available(encryption.ProtectionStatus) || !Available(encryption.LockStatus) ||
            encryption.ConversionStatus.Value > 5 || encryption.ProtectionStatus.Value > 1 || encryption.LockStatus.Value > 1)
            return Result(TargetMatch.ObservationUnavailable, TargetMismatchReason.BitLockerUnavailable);
        return new(TargetMatch.ExactMatch, null);
    }

    private static bool Available<T>(Observation<T> fact) => fact.Availability == ObservationAvailability.Available;
    private static bool EqualsFact<T>(Observation<T> fact, T value) => Available(fact) && EqualityComparer<T>.Default.Equals(fact.Value, value);
    private static TargetMatch MapAvailability(ObservationAvailability availability) => availability switch
    {
        ObservationAvailability.Unsupported => TargetMatch.Unsupported,
        ObservationAvailability.Ambiguous => TargetMatch.Ambiguous,
        _ => TargetMatch.ObservationUnavailable,
    };
    private static TargetRevalidation Result(TargetMatch outcome, TargetMismatchReason reason) => new(outcome, reason);
}
