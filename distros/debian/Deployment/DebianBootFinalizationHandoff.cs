using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;

namespace Igloo.Distro.Debian.Deployment;

// Desired finalizer input, NOT permission, a restore journal, or a supported loader profile.
// Source package versions identify authenticated input archives, not installed signed files.
public sealed record DebianBootFinalizationHandoffV1(Guid GenerationId, string PlanSha256,
    RootFileSystemReceiptV1 Root, CanonicalVolumeIdentityV1 LinuxEsp, CanonicalVolumeIdentityV1 WindowsEsp,
    Guid PackageBuildId, DebianOfflinePackageV1 ShimArchive, DebianOfflinePackageV1 GrubArchive,
    string ShimPath, string GrubPath,
    Observation<ImmutableArray<DebianCheckEvidenceV1>> PackageHookEvidence,
    Observation<ImmutableDictionary<DebianFirmwareSlotV1, FirmwareVariableV1>> FirmwareBefore);

public static class DebianBootFinalizationHandoff
{
    public static Observation<DebianBootFinalizationHandoffV1> Declare(DebianDeploymentRunV1 run,
        DebianRuntimeContextV1 current, Observation<ImmutableArray<DebianCheckEvidenceV1>> hooks,
        Observation<ImmutableDictionary<DebianFirmwareSlotV1, FirmwareVariableV1>> firmware)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(firmware);
        DebianDeploymentArtifacts.RequireStructure(run);
        var plan = run.Plan;
        if (run.State != DebianDeploymentState.PreBootStagesVerified || plan.Source.OfflinePackageSet is null)
            return Observations.Failure<DebianBootFinalizationHandoffV1>(ObservationAvailability.Unavailable, "DebianContentOrSourceIncomplete");
        var ownership = DebianTargetBoundary.Verify(plan, DebianStage.ConfigureSignedPackages, current);
        if (ownership.Availability != ObservationAvailability.Available)
            return Observations.Failure<DebianBootFinalizationHandoffV1>(ownership.Availability, ownership.Code!);
        var source = plan.Source.OfflinePackageSet;
        return Observations.Available(new DebianBootFinalizationHandoffV1(plan.Root.GenerationId, run.PlanSha256,
            plan.Root, plan.Ownership.Esp.LinuxEsp.Volume, plan.Ownership.Esp.WindowsEsp.Volume, source.BuildId,
            source.Packages.Single(p => p.Name == "shim-signed"), source.Packages.Single(p => p.Name == "grub-efi-amd64-signed"),
            "/EFI/debian/shimx64.efi", "/EFI/debian/grubx64.efi", hooks, firmware));
    }

    public static Observation<bool> FinalizerSupport => Observations.Failure<bool>(ObservationAvailability.Unsupported,
        "DebianSignedHookLoaderAndFirmwareFinalizerUnqualified");
}
