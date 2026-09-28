using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Core.Preparation;

// A created GPT partition can exist without a filesystem or Windows volume. Keep that fact
// separate from CanonicalVolumeIdentityV1; no synthetic volume GUID/filesystem is introduced.
public sealed record PreparedGptPartitionV1(CanonicalDiskIdentityV1 Disk, Guid PartitionGuid,
    Guid PartitionType, ulong OffsetBytes, ulong SizeBytes);
public sealed record PartitionCreationReceiptV1(Guid GenerationId, PreparationRole Role,
    PreparedGptPartitionV1 Identity);
public sealed record PreparedStorageOwnershipV1(Guid GenerationId,
    ImmutableArray<PreparedGptPartitionV1> PreservedPartitions,
    ImmutableArray<PartitionCreationReceiptV1> CreatedPartitions);

public static class PreparedStorageOwnership
{
    // Persist successful individual creation/readback receipts before attempting another write.
    // This validates evidence structure only; it never qualifies an installation or retry.
    public static Observation<bool> VerifyPartialStructure(PreparationPlanV1 plan, PreparedStorageOwnershipV1 ownership,
        ImmutableArray<PreparedPartitionV1> formatted)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ownership);
        if (!PreparedLayoutRules.IsValidPlan(plan) || ownership.GenerationId != plan.GenerationId ||
            ownership.PreservedPartitions.IsDefaultOrEmpty || ownership.CreatedPartitions.IsDefaultOrEmpty || formatted.IsDefault)
            return Fail<bool>(ObservationAvailability.Ambiguous, "PartialPartitionReceiptInvalid");
        var created = ownership.CreatedPartitions;
        if (!ValidInventory(ownership.PreservedPartitions.Concat(created.Select(r => r.Identity)).ToImmutableArray()) ||
            !ownership.PreservedPartitions.Select(p => p.PartitionGuid).Order().SequenceEqual(plan.BeforePartitionIds.Order()) ||
            !ownership.PreservedPartitions.Contains(PartitionOf(plan.WindowsEsp)) ||
            created.Select(r => r.Role).Distinct().Count() != created.Length ||
            created.Any(r => r.GenerationId != plan.GenerationId ||
                VerifyCreation(plan, r.Role, Observations.Available(r.Identity), Observations.Available(r.Identity)).Availability != ObservationAvailability.Available) ||
            formatted.Select(r => r.Role).Distinct().Count() != formatted.Length ||
            formatted.Select(r => r.Identity.VolumeGuid).Distinct().Count() != formatted.Length ||
            formatted.Any(r => !created.Any(c => c.Role == r.Role && c.Identity == PartitionOf(r.Identity)) ||
                PreparedLayoutRules.VerifyCreation(plan, r.Role, Observations.Available(r.Identity), Observations.Available(r.Identity)).Availability != ObservationAvailability.Available))
            return Fail<bool>(ObservationAvailability.Ambiguous, "PartialPartitionReceiptInvalid");
        return Observations.Available(true);
    }

    public static PreparedGptPartitionV1 PartitionOf(CanonicalVolumeIdentityV1 volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return new(volume.Disk, volume.PartitionGuid, volume.PartitionType, volume.OffsetBytes, volume.SizeBytes);
    }

    public static Observation<PartitionCreationReceiptV1> VerifyCreation(PreparationPlanV1 plan, PreparationRole role,
        Observation<PreparedGptPartitionV1> providerCreated, Observation<PreparedGptPartitionV1> reopened)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(providerCreated);
        ArgumentNullException.ThrowIfNull(reopened);
        if (!PreparedLayoutRules.IsValidPlan(plan)) return Fail<PartitionCreationReceiptV1>(ObservationAvailability.Ambiguous, "PreparationPlanInvalid");
        if (providerCreated.Availability != ObservationAvailability.Available) return Fail<PartitionCreationReceiptV1>(providerCreated.Availability, "PartitionCreationUnavailable");
        if (reopened.Availability != ObservationAvailability.Available) return Fail<PartitionCreationReceiptV1>(reopened.Availability, "PartitionReadbackUnavailable");
        var identity = providerCreated.Value;
        var allocation = plan.Space.Allocations.SingleOrDefault(a => a.Role == role && a.CreateInWindows);
        if (identity != reopened.Value || allocation is null || !Valid(identity) || identity.Disk != plan.TargetDisk ||
            identity.PartitionType != allocation.PartitionType || identity.OffsetBytes != allocation.OffsetBytes ||
            identity.SizeBytes != allocation.SizeBytes || plan.BeforePartitionIds.Contains(identity.PartitionGuid))
            return Fail<PartitionCreationReceiptV1>(ObservationAvailability.Ambiguous, "PartitionCreationNotOwned");
        return Observations.Available(new PartitionCreationReceiptV1(plan.GenerationId, role, identity));
    }

    // Validate the complete receipt structure AND compare to an independently acquired inventory.
    // Passing the stored receipts as the inventory only validates structure, never fresh ownership.
    public static Observation<bool> Verify(PreparedLayoutV1 layout, Guid expectedGeneration,
        Observation<ImmutableArray<PreparedGptPartitionV1>> current)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(current);
        var plan = layout.Plan;
        var ownership = layout.StorageOwnership;
        if (!PreparedLayoutRules.IsValidPlan(plan) || layout.SchemaVersion != 1 || expectedGeneration != plan.GenerationId ||
            ownership is null || ownership.GenerationId != expectedGeneration || ownership.PreservedPartitions.IsDefaultOrEmpty ||
            ownership.CreatedPartitions.IsDefault || layout.Partitions.IsDefault)
            return Fail<bool>(ObservationAvailability.Unavailable, "CompletePartitionOwnershipUnavailable");
        if (plan.Space.Allocations.Any(a => !a.CreateInWindows))
            return Fail<bool>(ObservationAvailability.Unsupported, "InstallationRequiresPrecreatedPartitions");
        var preserved = ownership.PreservedPartitions;
        var created = ownership.CreatedPartitions;
        var expected = preserved.Concat(created.Select(r => r.Identity)).ToImmutableArray();
        if (!ValidInventory(expected) ||
            !preserved.Select(p => p.PartitionGuid).Order().SequenceEqual(plan.BeforePartitionIds.Order()) ||
            !preserved.Contains(PartitionOf(plan.WindowsEsp)) ||
            created.Length != plan.Space.Allocations.Length || created.Select(r => r.Role).Distinct().Count() != created.Length ||
            created.Any(r => r.GenerationId != expectedGeneration ||
                VerifyCreation(plan, r.Role, Observations.Available(r.Identity), Observations.Available(r.Identity)).Availability != ObservationAvailability.Available))
            return Fail<bool>(ObservationAvailability.Ambiguous, "CompletePartitionOwnershipInvalid");

        var formatted = plan.Space.Allocations.Where(a => a.FileSystem.Length != 0).ToArray();
        if (layout.Partitions.Length != formatted.Length || layout.Partitions.Select(r => r.Role).Distinct().Count() != formatted.Length ||
            layout.Partitions.Select(r => r.Identity.VolumeGuid).Distinct().Count() != formatted.Length ||
            layout.Partitions.Any(r => !created.Any(c => c.Role == r.Role && c.Identity == PartitionOf(r.Identity)) ||
                PreparedLayoutRules.VerifyCreation(plan, r.Role, Observations.Available(r.Identity), Observations.Available(r.Identity)).Availability != ObservationAvailability.Available))
            return Fail<bool>(ObservationAvailability.Ambiguous, "FormattedReceiptMismatch");
        if (current.Availability != ObservationAvailability.Available) return Fail<bool>(current.Availability, "PartitionInventoryUnavailable");
        if (!ValidInventory(current.Value)) return Fail<bool>(ObservationAvailability.Ambiguous, "PartitionInventoryInvalid");
        foreach (var identity in expected)
        {
            var matches = current.Value.Where(p => p.PartitionGuid == identity.PartitionGuid).ToArray();
            if (matches.Length == 0) return Fail<bool>(ObservationAvailability.Absent, "OwnedOrPreservedPartitionMissing");
            if (matches.Length != 1 || matches[0] != identity) return Fail<bool>(ObservationAvailability.Ambiguous, "OwnedOrPreservedPartitionChanged");
        }
        // Any extra partition on either protected disk changes this planned installation layout.
        if (current.Value.Any(p => expected.Any(e => e.Disk.GptDiskGuid == p.Disk.GptDiskGuid) && !expected.Contains(p)))
            return Fail<bool>(ObservationAvailability.Ambiguous, "InstallationPartitionSetChanged");
        return Observations.Available(true);
    }

    public static Observation<bool> VerifyStructure(PreparedLayoutV1 layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var ownership = layout.StorageOwnership;
        return Verify(layout, layout.Plan.GenerationId, Observations.Available(ownership is null ||
            ownership.PreservedPartitions.IsDefault || ownership.CreatedPartitions.IsDefault ? ImmutableArray<PreparedGptPartitionV1>.Empty :
            ownership.PreservedPartitions.Concat(ownership.CreatedPartitions.Select(r => r.Identity)).ToImmutableArray()));
    }

    private static bool Valid(PreparedGptPartitionV1 partition)
    {
        var disk = partition.Disk;
        return !string.IsNullOrWhiteSpace(disk.UniqueId) && disk.UniqueIdFormat is 2 or 3 or 8 && disk.BusType is not (0 or 14 or 15) &&
            disk.GptDiskGuid != Guid.Empty && disk.SizeBytes > 0 && disk.LogicalSectorSize > 0 &&
            disk.PhysicalSectorSize >= disk.LogicalSectorSize && disk.PhysicalSectorSize % disk.LogicalSectorSize == 0 &&
            partition.PartitionGuid != Guid.Empty && partition.PartitionType != Guid.Empty && partition.OffsetBytes > 0 &&
            partition.SizeBytes > 0 && partition.OffsetBytes <= disk.SizeBytes && partition.SizeBytes <= disk.SizeBytes - partition.OffsetBytes &&
            partition.OffsetBytes % disk.LogicalSectorSize == 0 && partition.SizeBytes % disk.LogicalSectorSize == 0;
    }

    private static bool ValidInventory(ImmutableArray<PreparedGptPartitionV1> partitions) =>
        !partitions.IsDefault && partitions.All(Valid) && partitions.Select(p => p.PartitionGuid).Distinct().Count() == partitions.Length &&
        !partitions.Any(p => partitions.Any(other => p != other && p.Disk.GptDiskGuid == other.Disk.GptDiskGuid &&
            (p.Disk != other.Disk || (p.OffsetBytes <= other.OffsetBytes ? other.OffsetBytes - p.OffsetBytes < p.SizeBytes : p.OffsetBytes - other.OffsetBytes < other.SizeBytes))));

    private static Observation<T> Fail<T>(ObservationAvailability state, string code) => Observations.Failure<T>(state, code);
}
