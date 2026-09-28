using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public enum DebianConfiguredRootResponsibility { TargetSpecific, ArtifactReadback, ImportConfiguredRoot, FirmwareDeferred }

// The v1 stage enum/receipts retain their numeric and historical meaning. This is a candidate
// strategy map, not a renamed successful Bootstrap stage or a second executable dispatcher.
public static class DebianConfiguredRootStages
{
    public static DebianConfiguredRootResponsibility Responsibility(DebianStage stage) => stage switch
    {
        DebianStage.Bootstrap => DebianConfiguredRootResponsibility.ImportConfiguredRoot,
        DebianStage.ConfigureArchiveKeyring or DebianStage.ConfigurePackagePolicy or DebianStage.InstallKernel or
        DebianStage.InstallFirmware or DebianStage.InstallDesktop => DebianConfiguredRootResponsibility.ArtifactReadback,
        DebianStage.ObserveFirmwareBeforePackages or DebianStage.ConfigureSignedPackages or DebianStage.DrainPackageTriggers or
        DebianStage.FinalizeLoaderFiles or DebianStage.GenerateGrubConfiguration or DebianStage.InspectBootFiles or
        DebianStage.ObserveFirmwareBeforeFinalization or DebianStage.FinalizeFirmware or DebianStage.InspectFirmwareAfterFinalization =>
            DebianConfiguredRootResponsibility.FirmwareDeferred,
        _ when Enum.IsDefined(stage) => DebianConfiguredRootResponsibility.TargetSpecific,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    // The closed development action now composes the canonical session/view/importer and
    // separate journals. Real GPT/source transport and persistent-store qualification are still
    // outstanding; neither fixtures nor development authentication enable production support.
    public static Observation<bool> ImportSupport(DebianNativeMountSession session, InstallerBlockLeaseSet leases)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(leases);
        return Observations.Failure<bool>(ObservationAvailability.Unsupported, "DebianConfiguredRootImportSessionNotQualified");
    }
}
