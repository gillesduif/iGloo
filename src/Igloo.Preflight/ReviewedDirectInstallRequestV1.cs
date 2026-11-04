using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;

namespace Igloo.Preflight;

// The persisted plan and current facts are both required at the reviewed
// boundary. This request records the intended inputs; it grants no write.
public sealed record ReviewedDirectInstallRequestV1(
    string DistroId,
    PreparationCheckpointReference PlannedCheckpoint,
    PreparationPlanV1 ExpectedPlan,
    CanonicalDiskIdentityV1 TargetDisk,
    InstallerBootSpec BootSpec,
    long RequestedLinuxBytes,
    string PinnedIsoPath,
    string? PinnedKernelPath,
    string? PinnedRawInitrdPath,
    string StagingDirectory,
    long ExpectedStagedBytes,
    string ExpectedStagedInventorySha256,
    PreparedLayoutRecoveryFootprintV1 ExpectedRecoveryFootprint);

public interface IReviewedDirectInstallService
{
    Task<PreparationCheckpointReference> PrepareReviewedAsync(
        ReviewedDirectInstallRequestV1 request,
        IDistroPlugin provider,
        Func<CancellationToken, Task<PreparationPlanV1>> resolveCurrentPlan,
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken ct = default);
}
