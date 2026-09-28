using System.Collections.Immutable;
using Igloo.Core.Abstractions;

namespace Igloo.Distro.Debian.Deployment;

// Ordering is a versioned deployment contract. Root formatting belongs to preparation and must
// already have an independently verified receipt; this engine never formats or retries it.
public enum DebianStage
{
    ValidateOwnership, ResolveRuntimeDevices, PrepareNamespace, MountRoot, MountLinuxEsp,
    MountPayload, ExcludeWindowsEsp, VerifySourceTrust, ObserveFirmwareBeforePackages, Bootstrap, ConfigureApt,
    ConfigureArchiveKeyring, MountHelpers, ConfigurePackagePolicy, ConfigureHostname, ConfigureLocale,
    ConfigureTimezoneKeyboard, InitializeMachineIdentity, ConfigureNetwork,
    GenerateFstab, ConfigureUser, ConfigureSudo, InstallKernel, InstallFirmware,
    InstallDesktop, InstallAgent, ConfigureSignedPackages,
    DrainPackageTriggers, FinalizeLoaderFiles, GenerateGrubConfiguration,
    FinalizeMachineIdentity, GenerateInitramfs, InspectBootFiles, ObserveFirmwareBeforeFinalization,
    FinalizeFirmware, InspectFirmwareAfterFinalization, VerifyWindowsPreservation,
    PersistDeploymentEvidence, UnmountHelpers, UnmountLinuxEsp, UnmountPayload,
    UnmountRoot, PersistCompletionEvidence,
}

public enum DebianStageOutcome { NotStarted, IntentDurable, AppliedAndVerified, Failed, OutcomeUnknown }
public enum DebianDeploymentState { NotStarted, InProgress, Failed, OutcomeUnknown, EvidenceComplete, PreBootStagesVerified }
public enum DebianCheckVerdict { Unassessed, Satisfied, Mismatch }
public enum DebianCheck
{
    Ownership, NamespaceIsolation, RootMount, EspMount, PayloadMount, WindowsExcluded,
    SourceAuthentication, DependencyClosure, DpkgState, TargetConfiguration,
    HelperMounts, Accounts, Kernel, FirmwarePackages, Desktop, Agent,
    FirmwareUnchanged, SignedPackagePolicy, BootFiles, GrubReferences, MachineIdentity,
    Initramfs, FirmwareDelta, WindowsPreserved, ArtifactReopen, MountAbsent,
}

// Each digest refers to retained independent readback, not stdout or a tool success flag.
// The native observer must implement each check. No native observer exists yet; these contracts
// must not be substituted with a provider which fabricates Available from command exit status.
public sealed record DebianCheckEvidenceV1(DebianCheck Check, ObservationAvailability Availability,
    string Code, string? ReadbackSha256, DebianCheckVerdict Verdict);
public sealed record DebianStageReadbackV1(Guid GenerationId, string PlanSha256, DebianStage Stage,
    ImmutableArray<DebianCheckEvidenceV1> Checks);
public sealed record DebianStageResultV1(DebianStage Stage, DebianStageOutcome Outcome,
    int? ExitCode, string Code, ImmutableArray<DebianCheckEvidenceV1> Checks)
{
    public ImmutableArray<DebianCommandEvidenceV1> Commands { get; init; } = [];
}

public static class DebianDeploymentStages
{
    public static ImmutableArray<DebianStage> Ordered { get; } = Enum.GetValues<DebianStage>().ToImmutableArray();

    public static ImmutableArray<DebianCheck> RequiredChecks(DebianStage stage) => stage switch
    {
        DebianStage.ValidateOwnership or DebianStage.ResolveRuntimeDevices => [DebianCheck.Ownership],
        DebianStage.PrepareNamespace => [DebianCheck.NamespaceIsolation],
        DebianStage.MountRoot => [DebianCheck.RootMount],
        DebianStage.MountLinuxEsp => [DebianCheck.EspMount],
        DebianStage.MountPayload => [DebianCheck.PayloadMount],
        DebianStage.ExcludeWindowsEsp => [DebianCheck.WindowsExcluded],
        DebianStage.VerifySourceTrust => [DebianCheck.SourceAuthentication, DebianCheck.DependencyClosure],
        DebianStage.Bootstrap => [DebianCheck.DpkgState, DebianCheck.SourceAuthentication, DebianCheck.FirmwareUnchanged],
        DebianStage.ConfigureApt or DebianStage.ConfigureArchiveKeyring or DebianStage.ConfigureHostname or
        DebianStage.ConfigureLocale or DebianStage.ConfigureTimezoneKeyboard or DebianStage.ConfigureNetwork or
        DebianStage.GenerateFstab or DebianStage.ConfigurePackagePolicy => [DebianCheck.TargetConfiguration],
        DebianStage.MountHelpers => [DebianCheck.HelperMounts],
        DebianStage.InitializeMachineIdentity or DebianStage.FinalizeMachineIdentity => [DebianCheck.MachineIdentity],
        DebianStage.ConfigureUser or DebianStage.ConfigureSudo => [DebianCheck.Accounts],
        DebianStage.InstallKernel => [DebianCheck.DpkgState, DebianCheck.Kernel, DebianCheck.FirmwareUnchanged],
        DebianStage.InstallFirmware => [DebianCheck.DpkgState, DebianCheck.FirmwarePackages, DebianCheck.FirmwareUnchanged],
        DebianStage.InstallDesktop => [DebianCheck.DpkgState, DebianCheck.Desktop, DebianCheck.FirmwareUnchanged],
        DebianStage.InstallAgent => [DebianCheck.Agent],
        DebianStage.ObserveFirmwareBeforePackages or DebianStage.ObserveFirmwareBeforeFinalization => [DebianCheck.FirmwareUnchanged],
        DebianStage.ConfigureSignedPackages or DebianStage.DrainPackageTriggers =>
            [DebianCheck.DpkgState, DebianCheck.SignedPackagePolicy, DebianCheck.FirmwareUnchanged],
        DebianStage.FinalizeLoaderFiles => [DebianCheck.BootFiles, DebianCheck.FirmwareUnchanged],
        DebianStage.GenerateGrubConfiguration => [DebianCheck.GrubReferences, DebianCheck.FirmwareUnchanged],
        DebianStage.GenerateInitramfs => [DebianCheck.Kernel, DebianCheck.Initramfs, DebianCheck.FirmwareUnchanged],
        DebianStage.InspectBootFiles => [DebianCheck.BootFiles, DebianCheck.GrubReferences, DebianCheck.Initramfs],
        DebianStage.FinalizeFirmware or DebianStage.InspectFirmwareAfterFinalization => [DebianCheck.FirmwareDelta],
        DebianStage.VerifyWindowsPreservation => [DebianCheck.WindowsPreserved],
        DebianStage.PersistDeploymentEvidence or DebianStage.PersistCompletionEvidence => [DebianCheck.ArtifactReopen],
        DebianStage.UnmountHelpers or DebianStage.UnmountLinuxEsp or DebianStage.UnmountPayload or DebianStage.UnmountRoot => [DebianCheck.MountAbsent],
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    public static bool IsReadOnly(DebianStage stage) => stage is DebianStage.ValidateOwnership or DebianStage.ResolveRuntimeDevices or
        DebianStage.ExcludeWindowsEsp or DebianStage.VerifySourceTrust or DebianStage.ObserveFirmwareBeforePackages or
        DebianStage.InspectBootFiles or DebianStage.ObserveFirmwareBeforeFinalization or DebianStage.InspectFirmwareAfterFinalization or
        DebianStage.VerifyWindowsPreservation;

    public static bool RequiresCompleteMounts(DebianStage stage) => stage >= DebianStage.ExcludeWindowsEsp && stage <= DebianStage.UnmountHelpers;
    public static bool RequiresHelpers(DebianStage stage) => stage > DebianStage.MountHelpers && stage <= DebianStage.UnmountHelpers;

    public static bool ValidReadback(DebianStageReadbackV1 readback, Guid generation, string planHash, DebianStage stage)
    {
        ArgumentNullException.ThrowIfNull(readback);
        return readback.GenerationId == generation && readback.PlanSha256 == planHash && readback.Stage == stage &&
            !readback.Checks.IsDefault && readback.Checks.Select(c => c.Check).Order().SequenceEqual(RequiredChecks(stage).Order()) &&
            readback.Checks.All(c => Enum.IsDefined(c.Availability) && Enum.IsDefined(c.Verdict) && !string.IsNullOrWhiteSpace(c.Code) &&
                (c.Availability == ObservationAvailability.Available
                    ? c.Verdict != DebianCheckVerdict.Unassessed && DebianDeploymentPlanning.Hash(c.ReadbackSha256)
                    : c.Verdict == DebianCheckVerdict.Unassessed));
    }
}
