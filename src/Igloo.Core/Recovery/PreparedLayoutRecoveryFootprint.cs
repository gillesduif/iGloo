using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Core.Recovery;

public enum PreparedMutationSurface
{
    NtfsResize,
    PartitionCreate,
    FileSystemCreate,
    EspFiles,
    BootConfiguration,
    FirmwareVariables,
    Rtc,
    StagingHandoff,
}

public sealed record PreparedMutationV1(PreparedMutationSurface Surface, PreparationRole? Role,
    Guid? PartitionGuid, ulong? OffsetBytes, ulong? SizeBytes);

// An exact inventory of intended mutation surfaces is not a recovery snapshot. No element
// becomes restorable merely because the pre-state can be observed or a receipt can be issued.
public sealed record PreparedLayoutRecoveryFootprintV1(Guid GenerationId,
    ImmutableArray<PreparedMutationV1> Mutations, ImmutableArray<string> MissingProofCodes);

public static class PreparedLayoutRecoveryFootprint
{
    public static Observation<PreparedLayoutRecoveryFootprintV1> Derive(PreparationPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var reviewed = PreparedLayoutRules.VerifyReviewedIntent(plan);
        if (reviewed.Availability != ObservationAvailability.Available)
            return Observations.Failure<PreparedLayoutRecoveryFootprintV1>(reviewed.Availability, reviewed.Code!);
        var source = plan.ReviewedIntent!.ShrinkSource;
        var mutations = ImmutableArray.CreateBuilder<PreparedMutationV1>();
        mutations.Add(new(PreparedMutationSurface.NtfsResize, null, source.PartitionGuid,
            source.OffsetBytes, plan.ReviewedIntent.ResultingSourceBytes));
        foreach (var allocation in plan.Space.Allocations)
        {
            mutations.Add(new(PreparedMutationSurface.PartitionCreate, allocation.Role, null,
                allocation.OffsetBytes, allocation.SizeBytes));
            if (allocation.FileSystem.Length != 0 || allocation.Role == PreparationRole.LinuxRoot)
                mutations.Add(new(PreparedMutationSurface.FileSystemCreate, allocation.Role, null,
                    allocation.OffsetBytes, allocation.SizeBytes));
        }
        mutations.Add(new(PreparedMutationSurface.EspFiles, PreparationRole.LinuxEsp, null, null, null));
        mutations.Add(new(PreparedMutationSurface.BootConfiguration, null, null, null, null));
        mutations.Add(new(PreparedMutationSurface.FirmwareVariables, null, null, null, null));
        mutations.Add(new(PreparedMutationSurface.Rtc, null, null, null, null));
        mutations.Add(new(PreparedMutationSurface.StagingHandoff, PreparationRole.Payload, null, null, null));
        return Observations.Available(new PreparedLayoutRecoveryFootprintV1(plan.GenerationId,
            mutations.ToImmutable(), [
                "NtfsResizeRestorationUnproven", "PartitionTableRestorationUnproven",
                "CreatedFilesystemRestorationUnproven", "EspFileRestorationUnproven",
                "BootConfigurationRestorationUnproven", "FirmwareRestorationUnproven",
                "RtcRestorationUnproven", "StagingHandoffRestorationUnproven",
            ]));
    }
}
