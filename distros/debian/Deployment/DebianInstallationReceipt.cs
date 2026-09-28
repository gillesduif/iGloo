using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// Debian detail extends, rather than replaces, the shared installation evidence. Credentials
// are external content references. Kernel/initramfs, fstab, EFI, agent and NVRAM use shared types.
public sealed record DebianInstallationReceiptV1(int SchemaVersion, DebianDeploymentRunV1 Deployment,
    ImmutableArray<DebianInstalledPackageV1> Packages, InstallationReceiptV1 Installation)
{
    public DebianAgentReceiptV1? Agent { get; init; }
}

public static class DebianInstallationReceipts
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static DebianInstallationReceiptV1 Produce(DebianDeploymentRunV1 run,
        ImmutableArray<DebianInstalledPackageV1> packages, ImmutableArray<InstalledFileEvidenceV1> files,
        InstalledFirmwareEvidenceV1? firmware, DebianAgentReceiptV1? agent = null)
    {
        DebianDeploymentArtifacts.RequireStructure(run);
        var generic = Enum.GetValues<DeploymentStage>().Select(stage =>
        {
            var required = Required(stage);
            var results = run.Stages.Where(s => required.Contains(s.Stage)).ToArray();
            var outcome = results.Any(s => s.Outcome is DebianStageOutcome.OutcomeUnknown or DebianStageOutcome.IntentDurable)
                ? DeploymentStageOutcome.OutcomeUnknown : results.Any(s => s.Outcome == DebianStageOutcome.Failed)
                ? DeploymentStageOutcome.Failed : results.Length == required.Length && results.All(s => s.Outcome == DebianStageOutcome.AppliedAndVerified)
                ? DeploymentStageOutcome.ReadbackVerified : DeploymentStageOutcome.NotStarted;
            var digest = outcome == DeploymentStageOutcome.NotStarted ? null :
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(results)));
            return new DeploymentStageEvidenceV1(stage, outcome, null, digest);
        }).ToImmutableArray();
        var shared = new InstallationReceiptV1(1, run.Plan.Ownership, run.Plan.Root, "debian", "trixie",
            DebianDeploymentPlanning.StrategyVersion, run.Plan.Source.Artifact, generic, files, firmware);
        var receipt = new DebianInstallationReceiptV1(1, run, packages, shared) { Agent = agent };
        RequireStructure(receipt);
        return receipt;
    }

    public static InstallationReceiptAssessment Assess(DebianInstallationReceiptV1 receipt,
        Observation<InstallerRuntimeInventoryV1> inventory, Observation<ImmutableArray<InstalledFileEvidenceV1>> files,
        Observation<InstalledFirmwareEvidenceV1> firmware, Observation<ImmutableArray<DebianInstalledPackageV1>> packages)
    {
        RequireStructure(receipt);
        var run = receipt.Deployment;
        if (run.Plan.Agent.FirstBoot is not null && receipt.Agent?.State != DebianFirstBootState.FirstBootSucceeded)
            return new(receipt.Agent?.State == DebianFirstBootState.FirstBootFailed ? InstallationEvidenceState.Failed :
                receipt.Agent?.State == DebianFirstBootState.FirstBootOutcomeUnknown ? InstallationEvidenceState.OutcomeUnknown : InstallationEvidenceState.Incomplete,
                ObservationAvailability.Unavailable, "DebianMandatoryFirstBootWorkIncomplete");
        if (run.State != DebianDeploymentState.EvidenceComplete)
            return new(run.State == DebianDeploymentState.OutcomeUnknown ? InstallationEvidenceState.OutcomeUnknown :
                run.State == DebianDeploymentState.Failed ? InstallationEvidenceState.Failed : InstallationEvidenceState.Incomplete,
                ObservationAvailability.Unavailable, "DebianMandatoryStagesIncomplete");
        var checkedPackages = DebianReadbackRules.Packages(run.Plan, packages);
        if (checkedPackages.Availability != ObservationAvailability.Available)
            return new(InstallationEvidenceState.Incomplete, checkedPackages.Availability, checkedPackages.Code!);
        if (!receipt.Packages.OrderBy(p => p.Name, StringComparer.Ordinal).SequenceEqual(packages.Value.OrderBy(p => p.Name, StringComparer.Ordinal)))
            return new(InstallationEvidenceState.Incomplete, ObservationAvailability.Ambiguous, "DebianReceiptPackagesChanged");
        var shared = receipt.Installation;
        var kernel = shared.Files.SingleOrDefault(f => f.Role == InstalledFileRole.Kernel);
        if (kernel is null || !receipt.Packages.Any(p => p.Name == "linux-image-" + kernel.Path["/boot/vmlinuz-".Length..]) ||
            !shared.Files.Any(f => f.Role == InstalledFileRole.Shim && f.Path == "/EFI/debian/shimx64.efi") ||
            !shared.Files.Any(f => f.Role == InstalledFileRole.Grub && f.Path == "/EFI/debian/grubx64.efi") ||
            !shared.Files.Any(f => f.Role == InstalledFileRole.AgentService && f.Path == "/etc/systemd/system/igloo-deployment.service"))
            return new(InstallationEvidenceState.Incomplete, ObservationAvailability.Ambiguous, "DebianKernelOrBootNamespaceMismatch");
        if (run.Plan.Agent.FirstBoot is not null)
        {
            var profile = DebianAgentProfiles.Declare(run.Plan);
            foreach (var (role, path) in new[] { (InstalledFileRole.Agent, DebianAgentProfiles.AgentPath),
                (InstalledFileRole.AgentConfiguration, DebianAgentProfiles.ConfigurationPath), (InstalledFileRole.AgentService, DebianAgentProfiles.UnitPath) })
            {
                var expected = profile.Files.Single(f => f.Destination == path);
                if (!shared.Files.Any(f => f.Role == role && f.Path == path && f.Length == expected.Length && f.Sha256 == expected.Sha256))
                    return new(InstallationEvidenceState.Incomplete, ObservationAvailability.Ambiguous, "DebianDedicatedAgentEvidenceMismatch");
            }
        }
        else foreach (var (role, source) in new[] { (InstalledFileRole.Agent, "\\igloo-agent\\agent.py"),
            (InstalledFileRole.AgentConfiguration, "\\migration-manifest.json"), (InstalledFileRole.AgentService, "\\igloo-agent\\igloo-deployment.service") })
        {
            var expected = run.Plan.Agent.Payload.Files.Single(p => string.Equals(p.File.RelativePath, source, StringComparison.OrdinalIgnoreCase)).File;
            if (!shared.Files.Any(f => f.Role == role && f.Length == expected.Length && f.Sha256 == expected.Sha256))
                return new(InstallationEvidenceState.Incomplete, ObservationAvailability.Ambiguous, "DebianAgentPayloadMismatch");
        }
        return InstallationReceiptRules.AssessEvidence(shared, inventory, files, firmware);
    }

    public static byte[] Serialize(DebianInstallationReceiptV1 receipt)
    {
        RequireStructure(receipt);
        return JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
    }

    public static DebianInstallationReceiptV1 Reopen(ReadOnlySpan<byte> bytes, string sha256, Guid generation, string planSha256)
    {
        if (bytes.Length is <= 0 or > 16 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)) != sha256)
            throw new InvalidDataException("Debian installation receipt integrity mismatch.");
        var receipt = JsonSerializer.Deserialize<DebianInstallationReceiptV1>(bytes, Options) ?? throw new InvalidDataException("Missing Debian installation receipt.");
        RequireStructure(receipt);
        if (receipt.Deployment.Plan.Root.GenerationId != generation || receipt.Deployment.PlanSha256 != planSha256)
            throw new InvalidDataException("Debian installation receipt generation or plan changed.");
        return receipt;
    }

    private static void RequireStructure(DebianInstallationReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        DebianDeploymentArtifacts.RequireStructure(receipt.Deployment);
        var shared = receipt.Installation;
        var plan = receipt.Deployment.Plan;
        if (receipt.Agent is not null)
        {
            var agent = receipt.Agent;
            var profile = DebianAgentProfiles.Declare(plan);
            if (plan.Agent.FirstBoot is null || DebianAgentProfiles.VerifyWorkerPayload(plan).Availability != ObservationAvailability.Available ||
                agent.GenerationId != plan.Root.GenerationId || agent.Profile != DebianFirstBootEvidence.Profile ||
                !agent.Enabled || !Enum.IsDefined(agent.State) || agent.WorkerSha256 != DebianFirstBootEvidence.WorkerSha256 ||
                agent.ConfigurationSha256 != DebianDeploymentPlanning.TextHash(profile.ConfigurationContent) ||
                agent.UnitSha256 != DebianDeploymentPlanning.TextHash(profile.UnitContent) || agent.InstalledFiles.IsDefault ||
                !agent.InstalledFiles.OrderBy(f => f.Destination, StringComparer.Ordinal).SequenceEqual(profile.Files.OrderBy(f => f.Destination, StringComparer.Ordinal)) ||
                (agent.State == DebianFirstBootState.InstalledFirstBootPending ? agent.FirstBootEvidenceSha256 is not null : !DebianDeploymentPlanning.Hash(agent.FirstBootEvidenceSha256)) ||
                !receipt.Deployment.Stages.Any(s => s.Stage == DebianStage.InstallAgent && s.Outcome == DebianStageOutcome.AppliedAndVerified))
                throw new InvalidDataException("Invalid dedicated Debian agent evidence.");
            if (agent.State == DebianFirstBootState.InstalledFirstBootPending && agent.Service is not null ||
                agent.State is DebianFirstBootState.FirstBootSucceeded or DebianFirstBootState.FirstBootFailed &&
                (agent.Service is null || agent.Service.InvocationId == Guid.Empty || agent.Service.UnitName != "igloo-deployment.service" ||
                    agent.Service.ExecMainCode != 1 || (agent.State == DebianFirstBootState.FirstBootSucceeded
                        ? agent.Service.ExecMainStatus != 0 || agent.Service.Result != "success"
                        : agent.Service.ExecMainStatus == 0 || agent.Service.Result != "exit-code")))
                throw new InvalidDataException("First-boot service outcome is not retained.");
        }
        if (receipt.SchemaVersion != 1 || receipt.Packages.IsDefault || !InstallationReceiptRules.IsStructurallyValid(shared) ||
            shared.DistroId != "debian" || shared.DistroVersion != "trixie" || shared.StrategyVersion != DebianDeploymentPlanning.StrategyVersion ||
            JsonSerializer.Serialize(shared.Ownership) != JsonSerializer.Serialize(plan.Ownership) || shared.Root != plan.Root || shared.Source != plan.Source.Artifact)
            throw new InvalidDataException("Invalid Debian installation evidence.");
        // Shared completion flags must not be forged independently of the detailed stage trace.
        foreach (var stage in shared.Stages.Where(s => s.Outcome == DeploymentStageOutcome.ReadbackVerified))
            if (Required(stage.Stage).Any(r => !receipt.Deployment.Stages.Any(s => s.Stage == r && s.Outcome == DebianStageOutcome.AppliedAndVerified)))
                throw new InvalidDataException("Shared installation stage has no Debian evidence.");
    }

    private static ImmutableArray<DebianStage> Required(DeploymentStage stage) => stage switch
    {
        DeploymentStage.LayoutResolved or DeploymentStage.RootFormatted => [DebianStage.ValidateOwnership, DebianStage.ResolveRuntimeDevices],
        DeploymentStage.MountsVerified => [DebianStage.PrepareNamespace, DebianStage.MountRoot, DebianStage.MountLinuxEsp, DebianStage.MountPayload, DebianStage.ExcludeWindowsEsp, DebianStage.MountHelpers],
        DeploymentStage.SourceVerified => [DebianStage.VerifySourceTrust],
        DeploymentStage.FilesystemDeployed => [DebianStage.Bootstrap],
        DeploymentStage.AptConfigured => [DebianStage.ConfigureApt, DebianStage.ConfigureArchiveKeyring, DebianStage.ConfigurePackagePolicy],
        DeploymentStage.KernelInstalled => [DebianStage.InstallKernel],
        DeploymentStage.InitramfsGenerated => [DebianStage.GenerateInitramfs],
        DeploymentStage.SystemConfigured => [DebianStage.ConfigureHostname, DebianStage.ConfigureLocale, DebianStage.ConfigureTimezoneKeyboard, DebianStage.ConfigureNetwork, DebianStage.GenerateFstab],
        DeploymentStage.AccountsConfigured => [DebianStage.ConfigureUser, DebianStage.ConfigureSudo],
        DeploymentStage.DesktopFirmwareConfigured => [DebianStage.InstallDesktop, DebianStage.InstallFirmware],
        DeploymentStage.SystemIdentityFinalized => [DebianStage.InitializeMachineIdentity, DebianStage.FinalizeMachineIdentity],
        DeploymentStage.SignedBootPackagesConfigured => [DebianStage.ConfigureSignedPackages, DebianStage.DrainPackageTriggers],
        DeploymentStage.BootloaderFilesFinalized => [DebianStage.FinalizeLoaderFiles, DebianStage.InspectBootFiles],
        DeploymentStage.BootConfigurationGenerated => [DebianStage.GenerateGrubConfiguration],
        DeploymentStage.FirmwareFinalized => [DebianStage.ObserveFirmwareBeforePackages, DebianStage.ObserveFirmwareBeforeFinalization, DebianStage.FinalizeFirmware, DebianStage.InspectFirmwareAfterFinalization],
        DeploymentStage.AgentInstalled => [DebianStage.InstallAgent],
        DeploymentStage.WindowsPreservationVerified => [DebianStage.VerifyWindowsPreservation],
        DeploymentStage.TargetUnmounted => [DebianStage.UnmountHelpers, DebianStage.UnmountLinuxEsp, DebianStage.UnmountPayload, DebianStage.UnmountRoot, DebianStage.PersistCompletionEvidence],
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };
}
