using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Core.Preparation;

public enum PreparationRole { LinuxEsp, Payload, Iso, LinuxRoot }
public enum PreparationState { Planned, CreatedAndVerified, ContentStagedAndVerified, AmbiguousLeftover, OwnershipUnavailable, CreationInProgress }

// Storage preparation evidence is deliberately separate from RecoverySnapshotV1. None of these
// records authorizes a write, implements rollback, or certifies an installer/firmware boot path.
public sealed record PreparationAllocationV1(PreparationRole Role, ulong OffsetBytes, ulong SizeBytes,
    Guid PartitionType, string FileSystem, bool CreateInWindows);
public sealed record PreparationSpaceRequestV1(ulong FreeOffsetBytes, ulong FreeSizeBytes,
    ulong LinuxBytes, ulong PayloadBytes, ulong IsoBytes, uint PhysicalSectorSize, bool PreCreateRoot);
public sealed record PreparationSpacePlanV1(ulong AlignmentPaddingBytes, ulong RequiredBytes,
    ImmutableArray<PreparationAllocationV1> Allocations);
public sealed record PreparationPlanV1(int SchemaVersion, Guid GenerationId, CanonicalDiskIdentityV1 TargetDisk,
    CanonicalVolumeIdentityV1 WindowsEsp, ImmutableArray<Guid> BeforePartitionIds,
    PreparationSpacePlanV1 Space);

// This receipt must come from a successful provider CreatePartition result, fresh canonical
// readback and durable persistence. A newly discovered partition or matching label is NOT a receipt.
public sealed record PreparedPartitionV1(PreparationRole Role, CanonicalVolumeIdentityV1 Identity);
public sealed record PreparedLayoutV1(int SchemaVersion, PreparationPlanV1 Plan,
    ImmutableArray<PreparedPartitionV1> Partitions, string PayloadFileSystemUuid,
    ImmutableArray<CanonicalFileIdentityV1> BootFiles)
{
    // Additive preparation evidence, not a RecoverySnapshotV1 schema change. Older checkpoints
    // without partition-only receipts cannot qualify for the complete installation contract.
    public PreparedStorageOwnershipV1? StorageOwnership { get; init; }
}
public sealed record PreparationAssessment(PreparationState State, ObservationAvailability Availability, string Code);

public static class PreparationSpacePlanning
{
    public const ulong Alignment = 1024 * 1024;
    // Policy for an ESP that will also serve the installed Linux system; not just two loader files.
    public const ulong LinuxEspBytes = 1024 * Alignment;
    public static readonly Guid EspType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid BasicDataType = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid LinuxDataType = new("0fc63daf-8483-4772-8e79-3d69d8477de4");

    // FreeOffset/Size describe ONE independently verified contiguous extent after the proposed
    // shrink. Do not add unrelated free extents or subtract label-discovered leftover partitions.
    public static Observation<PreparationSpacePlanV1> Plan(PreparationSpaceRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PhysicalSectorSize == 0 || Alignment % request.PhysicalSectorSize != 0 ||
            request.LinuxBytes == 0 || request.PayloadBytes == 0)
            return Failure(ObservationAvailability.Ambiguous, "PreparationSpaceInputInvalid");
        try
        {
            checked
            {
                var offset = Align(request.FreeOffsetBytes);
                if (offset == 0) return Failure(ObservationAvailability.Ambiguous, "PreparationExtentIncludesDiskHeader");
                var padding = offset - request.FreeOffsetBytes;
                var end = request.FreeOffsetBytes + request.FreeSizeBytes;
                var allocations = ImmutableArray.CreateBuilder<PreparationAllocationV1>();
                Add(PreparationRole.LinuxEsp, LinuxEspBytes, EspType, "FAT32", true);
                Add(PreparationRole.Payload, Align(request.PayloadBytes), BasicDataType, "FAT32", true);
                if (request.IsoBytes != 0) Add(PreparationRole.Iso, Align(request.IsoBytes), BasicDataType, "NTFS", true);
                Add(PreparationRole.LinuxRoot, Align(request.LinuxBytes), LinuxDataType, "", request.PreCreateRoot);
                if (offset > end) return Failure(ObservationAvailability.Unavailable, "PreparationSpaceInsufficient");
                return Observations.Available(new PreparationSpacePlanV1(padding, offset - request.FreeOffsetBytes, allocations.ToImmutable()));

                void Add(PreparationRole role, ulong size, Guid type, string fs, bool create)
                {
                    allocations.Add(new(role, offset, size, type, fs, create));
                    offset = checked(offset + size);
                }
            }
        }
        catch (OverflowException) { return Failure(ObservationAvailability.Ambiguous, "PreparationSpaceOverflow"); }
    }

    private static ulong Align(ulong bytes) => checked(bytes + Alignment - 1) / Alignment * Alignment;
    private static Observation<PreparationSpacePlanV1> Failure(ObservationAvailability state, string code) =>
        Observations.Failure<PreparationSpacePlanV1>(state, code);
}

public static class PreparedLayoutRules
{
    public const string ShimPath = @"\EFI\iGloo\shimx64.efi";
    public const string GrubPath = @"\EFI\iGloo\grubx64.efi";

    public static bool IsValidPlan(PreparationPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != 1 || plan.GenerationId == Guid.Empty ||
            !CanonicalRecoveryIdentity.IsValid(plan.WindowsEsp) || plan.WindowsEsp.PartitionType != PreparationSpacePlanning.EspType ||
            plan.WindowsEsp.FileSystem != "FAT32" || plan.BeforePartitionIds.IsDefaultOrEmpty ||
            plan.BeforePartitionIds.Contains(Guid.Empty) || plan.BeforePartitionIds.Distinct().Count() != plan.BeforePartitionIds.Length ||
            !plan.BeforePartitionIds.Contains(plan.WindowsEsp.PartitionGuid) || plan.Space.Allocations.IsDefaultOrEmpty)
            return false;
        var allocations = plan.Space.Allocations;
        if (allocations.Select(a => a.Role).Distinct().Count() != allocations.Length ||
            !allocations.Any(a => a.Role == PreparationRole.LinuxEsp) ||
            !allocations.Any(a => a.Role == PreparationRole.Payload) ||
            !allocations.Any(a => a.Role == PreparationRole.LinuxRoot)) return false;
        foreach (var a in allocations)
        {
            if (!Enum.IsDefined(a.Role) || a.OffsetBytes < PreparationSpacePlanning.Alignment || a.SizeBytes == 0 || a.OffsetBytes % PreparationSpacePlanning.Alignment != 0 ||
                a.SizeBytes % PreparationSpacePlanning.Alignment != 0 ||
                a.OffsetBytes > plan.TargetDisk.SizeBytes || a.SizeBytes > plan.TargetDisk.SizeBytes - a.OffsetBytes)
                return false;
            if (a.Role == PreparationRole.LinuxEsp && (a.PartitionType != PreparationSpacePlanning.EspType ||
                a.FileSystem != "FAT32" || a.SizeBytes != PreparationSpacePlanning.LinuxEspBytes || !a.CreateInWindows)) return false;
            if (a.Role is PreparationRole.Payload or PreparationRole.Iso && (a.PartitionType != PreparationSpacePlanning.BasicDataType ||
                a.FileSystem != (a.Role == PreparationRole.Payload ? "FAT32" : "NTFS") || !a.CreateInWindows)) return false;
            if (a.Role == PreparationRole.LinuxRoot && (a.PartitionType != PreparationSpacePlanning.LinuxDataType || a.FileSystem.Length != 0)) return false;
            if (plan.TargetDisk == plan.WindowsEsp.Disk && Overlaps(a.OffsetBytes, a.SizeBytes, plan.WindowsEsp.OffsetBytes, plan.WindowsEsp.SizeBytes)) return false;
        }
        var ordered = allocations.OrderBy(a => a.OffsetBytes).ToArray();
        if (ordered.Zip(ordered.Skip(1)).Any(pair => Overlaps(pair.First.OffsetBytes, pair.First.SizeBytes, pair.Second.OffsetBytes, pair.Second.SizeBytes))) return false;
        if (plan.Space.AlignmentPaddingBytes >= PreparationSpacePlanning.Alignment ||
            ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.OffsetBytes + pair.First.SizeBytes != pair.Second.OffsetBytes)) return false;
        try
        {
            var required = allocations.Aggregate(plan.Space.AlignmentPaddingBytes, (sum, a) => checked(sum + a.SizeBytes));
            if (required != plan.Space.RequiredBytes) return false;
        }
        catch (OverflowException) { return false; }
        // Reuse canonical disk validation instead of inventing a second strong-disk definition.
        return CanonicalRecoveryIdentity.IsValid(plan.WindowsEsp with { Disk = plan.TargetDisk,
            OffsetBytes = ordered[0].OffsetBytes, SizeBytes = ordered[0].SizeBytes });
    }

    public static Observation<PreparedPartitionV1> VerifyCreation(PreparationPlanV1 plan, PreparationRole role,
        Observation<CanonicalVolumeIdentityV1> providerCreated, Observation<CanonicalVolumeIdentityV1> independentReadback)
    {
        ArgumentNullException.ThrowIfNull(providerCreated);
        ArgumentNullException.ThrowIfNull(independentReadback);
        if (!IsValidPlan(plan)) return Failure(ObservationAvailability.Ambiguous, "PreparationPlanInvalid");
        if (providerCreated.Availability != ObservationAvailability.Available)
            return Failure(providerCreated.Availability, "CreationOwnershipUnavailable");
        if (independentReadback.Availability != ObservationAvailability.Available)
            return Failure(independentReadback.Availability, "CreationReadbackUnavailable");
        var identity = providerCreated.Value;
        if (identity != independentReadback.Value || !CanonicalRecoveryIdentity.IsValid(identity))
            return Failure(ObservationAvailability.Ambiguous, "CreatedPartitionChanged");
        var allocation = plan.Space.Allocations.SingleOrDefault(a => a.Role == role && a.CreateInWindows);
        if (allocation is null || identity.Disk != plan.TargetDisk || identity.PartitionType != allocation.PartitionType ||
            identity.OffsetBytes != allocation.OffsetBytes || identity.SizeBytes != allocation.SizeBytes ||
            identity.FileSystem != allocation.FileSystem || identity.VolumeGuid == plan.WindowsEsp.VolumeGuid ||
            plan.BeforePartitionIds.Contains(identity.PartitionGuid))
            return Failure(ObservationAvailability.Ambiguous, "CreatedPartitionNotOwnedAllocation");
        return Observations.Available(new PreparedPartitionV1(role, identity));
    }

    // No generation discovery or label fallback: the caller supplies the independently reopened,
    // trusted attempt record. Losing the create result before it was persisted is an ambiguous
    // leftover, even if an identically sized partition is later found at the planned offset.
    public static PreparationAssessment AssessOwnership(PreparationPlanV1 plan, Guid reopenedGeneration,
        ImmutableArray<PreparedPartitionV1> receipts, ImmutableArray<CanonicalVolumeIdentityV1> current,
        CanonicalVolumeIdentityV1 currentWindowsEsp)
    {
        if (!IsValidPlan(plan) || reopenedGeneration != plan.GenerationId || receipts.IsDefault || current.IsDefault)
            return new(PreparationState.OwnershipUnavailable, ObservationAvailability.Unavailable, "PreparationReceiptUnavailable");
        if (currentWindowsEsp != plan.WindowsEsp)
            return new(PreparationState.AmbiguousLeftover, ObservationAvailability.Ambiguous, "WindowsEspChanged");
        if (current.Select(v => v.PartitionGuid).Distinct().Count() != current.Length ||
            current.Select(v => v.VolumeGuid).Distinct().Count() != current.Length ||
            receipts.Select(v => v.Role).Distinct().Count() != receipts.Length ||
            receipts.Select(v => v.Identity.PartitionGuid).Distinct().Count() != receipts.Length ||
            receipts.Select(v => v.Identity.VolumeGuid).Distinct().Count() != receipts.Length)
            return new(PreparationState.AmbiguousLeftover, ObservationAvailability.Ambiguous, "PreparationDuplicateOwnership");
        foreach (var allocation in plan.Space.Allocations.Where(a => a.CreateInWindows))
        {
            var receipt = receipts.SingleOrDefault(r => r.Role == allocation.Role);
            var occupying = current.Where(v => v.Disk == plan.TargetDisk &&
                Overlaps(allocation.OffsetBytes, allocation.SizeBytes, v.OffsetBytes, v.SizeBytes)).ToArray();
            if (receipt is null && occupying.Length != 0)
                return new(PreparationState.AmbiguousLeftover, ObservationAvailability.Ambiguous, "UnreceiptedPreparationPartition");
            if (receipt is null) continue;
            if (occupying.Length != 1 || VerifyCreation(plan, allocation.Role, Observations.Available(receipt.Identity),
                Observations.Available(occupying[0])).Availability != ObservationAvailability.Available)
                return new(PreparationState.AmbiguousLeftover, ObservationAvailability.Ambiguous, "PreparationIdentityChanged");
        }
        if (receipts.Any(r => !plan.Space.Allocations.Any(a => a.CreateInWindows && a.Role == r.Role)))
            return new(PreparationState.AmbiguousLeftover, ObservationAvailability.Ambiguous, "UnexpectedPreparationReceipt");
        return receipts.Length == plan.Space.Allocations.Count(a => a.CreateInWindows)
            ? new(PreparationState.CreatedAndVerified, ObservationAvailability.Available, "PreparationIdentitiesVerified")
            : new(PreparationState.Planned, ObservationAvailability.Unavailable, "PreparationIncomplete");
    }

    public static Observation<bool> VerifyBootFiles(PreparedLayoutV1 layout, ImmutableArray<CanonicalFileIdentityV1> independentlyReadFiles)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.SchemaVersion != 1 || !IsValidPlan(layout.Plan) || layout.Partitions.IsDefault ||
            layout.Partitions.Count(p => p.Role == PreparationRole.LinuxEsp) != 1 || layout.BootFiles.IsDefaultOrEmpty ||
            independentlyReadFiles.IsDefault || !IsFatUuid(layout.PayloadFileSystemUuid))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous, "PreparedContentInvalid");
        var esp = layout.Partitions.Single(p => p.Role == PreparationRole.LinuxEsp).Identity;
        if (VerifyCreation(layout.Plan, PreparationRole.LinuxEsp, Observations.Available(esp), Observations.Available(esp)).Availability != ObservationAvailability.Available ||
            layout.BootFiles.Any(f => f.VolumeGuid != esp.VolumeGuid || !IsBootFile(f)) ||
            layout.BootFiles.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != layout.BootFiles.Length ||
            !layout.BootFiles.Any(f => f.RelativePath == ShimPath) || !layout.BootFiles.Any(f => f.RelativePath == GrubPath) ||
            !layout.BootFiles.Any(f => f.RelativePath.EndsWith("\\grub.cfg", StringComparison.Ordinal)))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous, "PreparedBootChainIncomplete");
        if (independentlyReadFiles.Length != layout.BootFiles.Length ||
            !layout.BootFiles.OrderBy(f => f.RelativePath, StringComparer.Ordinal).SequenceEqual(independentlyReadFiles.OrderBy(f => f.RelativePath, StringComparer.Ordinal)))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous, "PreparedBootContentChanged");
        // Content identity only. Loader/config semantics, UUID uniqueness and installer support
        // require separate evidence; this result must not unlock preparation or registration.
        return Observations.Available(true);
    }

    private static bool IsFatUuid(string value) => value is { Length: 9 } && value[4] == '-' &&
        value.Where((_, i) => i != 4).All(Uri.IsHexDigit);
    private static bool IsBootFile(CanonicalFileIdentityV1 file) => file.Length > 0 &&
        file.Sha256 is { Length: 64 } && file.Sha256.All(Uri.IsHexDigit) &&
        file.RelativePath.StartsWith(@"\EFI\iGloo\", StringComparison.Ordinal) &&
        !file.RelativePath.Contains("..", StringComparison.Ordinal) && !file.RelativePath.Contains(':', StringComparison.Ordinal) && !file.RelativePath.Contains('/', StringComparison.Ordinal);
    private static bool Overlaps(ulong offset, ulong size, ulong otherOffset, ulong otherSize) =>
        offset >= otherOffset ? offset - otherOffset < otherSize : otherOffset - offset < size;
    private static Observation<PreparedPartitionV1> Failure(ObservationAvailability state, string code) => Observations.Failure<PreparedPartitionV1>(state, code);
}
