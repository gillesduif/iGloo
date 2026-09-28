using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public enum DebianInputKind { None, EncryptedPassword, PublicDebconf }
public enum DebianCommandState { NotStarted, StartedOutcomeUnknown, Exited }
// No stdin, argv text, stdout/stderr or exception messages are stored in this evidence.
// The operation hash identifies the public immutable instruction. Credentials travel separately.
public sealed record DebianCommandEvidenceV1(Guid GenerationId, string PlanSha256, DebianStage Stage,
    string Executable, string ToolSha256, string OperationSha256, DebianCommandState State, int? ExitCode);
public sealed record DebianNativeInstructionV1(DebianStage Stage, DebianCommandV1 Command, DebianInputKind Input);

// The host is an isolated Linux broker, NOT a generic host Process.Start implementation.
// It must re-read effective namespaces, cgroup/device policy, capabilities and mounts before
// every call. An implementation without that enforcement is unavailable, never a local fallback.
public interface IDebianIsolatedStageHost
{
    Task<Observation<DebianRuntimeContextV1>> ReinspectAsync(DebianDeploymentPlanV1 plan, DebianStage stage, CancellationToken ct);
    Task<DebianCommandEvidenceV1> ExecuteAsync(DebianDeploymentPlanV1 plan, DebianNativeInstructionV1 instruction,
        CancellationToken ct);
    Task<int> ApplyConfigurationAsync(DebianDeploymentPlanV1 plan, DebianStage stage,
        ImmutableArray<DebianConfigurationV1> exactFiles, CancellationToken ct);
    Task<int> ApplyMountStageAsync(DebianDeploymentPlanV1 plan, DebianStage stage,
        ResolvedInstallationV1 freshlyResolvedDevices, CancellationToken ct);
    Task<int> InstallAgentAsync(DebianDeploymentPlanV1 plan, DebianAgentInstallProfileV1 profile, CancellationToken ct);
}

// Secret handles are not serializable deployment input. The broker must fetch the protected
// credential, verify its artifact hash, send it only via stdin, zero/dispose its buffer, and
// disable output capture for credential commands. This interface provides no logging method.
public interface IDebianCredentialInput
{
    Task SendEncryptedPasswordAsync(string expectedArtifactSha256, string username, Stream processInput, CancellationToken ct);
}

public static class DebianNativeInstructions
{
    public static DebianNativeInstructionV1 Command(DebianDeploymentPlanV1 plan, DebianStage stage)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = DebianDeploymentPlanning.Fingerprint(plan);
        var command = stage switch
        {
            // Authenticated package acquisition is not a qualified bootstrap. Stock
            // debootstrap requires denied setup operations; no replacement has a
            // complete session-bound lifecycle. Never emit a candidate launch here.
            DebianStage.Bootstrap => throw new NotSupportedException("DebianBootstrapPrimitiveUnqualified"),
            DebianStage.ConfigureLocale => new DebianCommandV1("/usr/sbin/locale-gen", []),
            DebianStage.ConfigureUser => new DebianCommandV1("/usr/sbin/useradd",
                ["--create-home", "--uid", "1000", "--shell", "/bin/bash", "--", plan.Identity.Username]),
            DebianStage.ConfigureSudo => new DebianCommandV1("/usr/sbin/usermod", ["--append", "--groups", "sudo", "--", plan.Identity.Username]),
            DebianStage.ConfigurePackagePolicy => Packages(plan, ["adduser", "locales", "tzdata", "keyboard-configuration", "network-manager", "sudo"]),
            DebianStage.InstallKernel => Packages(plan, ["linux-image-amd64", "initramfs-tools"]),
            DebianStage.InstallFirmware => Packages(plan, DebianWorkstationPolicy.Trixie.FirmwarePackages),
            DebianStage.InstallDesktop => Packages(plan, plan.Source.Packages.Select(p => p.Name).Where(p =>
                p is not ("shim-signed" or "shim-signed-common" or "grub-efi-amd64-signed" or "grub-efi-amd64")).ToImmutableArray()),
            DebianStage.GenerateInitramfs => new DebianCommandV1("/usr/sbin/update-initramfs", ["-u", "-k", KernelRelease(plan)]),
            // Signed packages and GRUB are deliberately NOT dispatched until the hook/loader
            // profile is qualified. Desktop dependencies can also bring them in: isolation is
            // required for ALL dpkg stages, not only ConfigureSignedPackages.
            _ => throw new NotSupportedException("No qualified command for this Debian stage."),
        };
        return new(stage, command, DebianInputKind.None);
    }

    public static DebianNativeInstructionV1 Password => new(DebianStage.ConfigureUser,
        new("/usr/sbin/chpasswd", ["--encrypted"]), DebianInputKind.EncryptedPassword);
    public static DebianNativeInstructionV1 LockRoot => new(DebianStage.ConfigureUser,
        new("/usr/sbin/usermod", ["--lock", "root"]), DebianInputKind.None);
    public static DebianNativeInstructionV1 BootPackagePolicy => new(DebianStage.ConfigurePackagePolicy,
        new("/usr/bin/debconf-set-selections", []), DebianInputKind.PublicDebconf);

    public static ImmutableArray<DebianConfigurationV1> Configuration(DebianDeploymentPlanV1 plan, DebianStage stage,
        DebianRuntimeContextV1 context)
    {
        var files = DebianTargetConfiguration.Generate(plan);
        if (stage is DebianStage.InitializeMachineIdentity or DebianStage.FinalizeMachineIdentity &&
            plan.Source.Packages.Any(p => p.Name == "openssh-server"))
            throw new NotSupportedException("An SSH server requires a qualified target host-key lifecycle.");
        return stage switch
        {
            DebianStage.ConfigureHostname => files.Where(f => f.Path is "/etc/hostname" or "/etc/hosts").ToImmutableArray(),
            DebianStage.ConfigureLocale => files.Where(f => f.Path is "/etc/locale.gen" or "/etc/default/locale").ToImmutableArray(),
            DebianStage.ConfigureTimezoneKeyboard => files.Where(f => f.Path is "/etc/localtime" or "/etc/default/keyboard").ToImmutableArray(),
            DebianStage.ConfigureNetwork => files.Where(f => f.Path is "/etc/network/interfaces" or "/etc/resolv.conf").ToImmutableArray(),
            DebianStage.GenerateFstab => [new("/etc/fstab", DebianConfigurationKind.Utf8File,
                Fstab(plan, context), 0x1a4)],
            DebianStage.InitializeMachineIdentity => files.Where(f =>
                f.Path is "/etc/machine-id" or "/var/lib/dbus/machine-id" or "/var/lib/NetworkManager/secret_key").ToImmutableArray(),
            DebianStage.FinalizeMachineIdentity => files.Where(f =>
                f.Path is "/etc/machine-id" or "/var/lib/dbus/machine-id" or "/var/lib/NetworkManager/secret_key" or
                    "/etc/apt/sources.list" or "/etc/apt/sources.list.d/debian.sources" or "/etc/apt/igloo-offline.sources" or
                    "/usr/sbin/policy-rc.d" or "/etc/igloo-installer-resolv.conf" or "/etc/resolv.conf").ToImmutableArray(),
            DebianStage.ConfigureApt => [new("/etc/apt/igloo-offline.sources", DebianConfigurationKind.Utf8File,
                DebianTargetConfiguration.OfflineAptSources(plan), 0x1a4)],
            DebianStage.ConfigurePackagePolicy => [new("/usr/sbin/policy-rc.d", DebianConfigurationKind.Utf8File,
                DebianTargetConfiguration.TemporaryServicePolicy, 0x1ed), .. files.Where(f => f.Path == "/etc/default/grub.d/60-igloo.cfg")],
            _ => throw new NotSupportedException("No qualified file operation for this Debian stage."),
        };
    }

    public static string Fingerprint(DebianNativeInstructionV1 instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return DebianDeploymentPlanning.TextHash(System.Text.Json.JsonSerializer.Serialize(instruction));
    }

    public static bool ValidEvidence(DebianDeploymentPlanV1 plan, DebianNativeInstructionV1 instruction, DebianCommandEvidenceV1 result)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(result);
        return result.GenerationId == plan.Root.GenerationId && result.PlanSha256 == DebianDeploymentPlanning.Fingerprint(plan) &&
            result.Stage == instruction.Stage && result.Executable == instruction.Command.Executable &&
            result.OperationSha256 == Fingerprint(instruction) && DebianDeploymentPlanning.Hash(result.ToolSha256) &&
            Enum.IsDefined(result.State) && (result.State == DebianCommandState.Exited ? result.ExitCode is not null : result.ExitCode is null);
    }

    private static DebianCommandV1 Packages(DebianDeploymentPlanV1 plan, ImmutableArray<string> names)
    {
        if (plan.Source.OfflinePackageSet is null || OfflineDebianPackageSets.Bind(plan, plan.Source.OfflinePackageSet).Availability != ObservationAvailability.Available)
            throw new NotSupportedException("Authenticated offline package set required.");
        // Names come from the closed product policy, versions from the bound solver result.
        return DebianWorkstationPolicy.InstallPinned(names.Select(n => plan.Source.Packages.Single(p => p.Name == n)).ToImmutableArray());
    }

    private static string KernelRelease(DebianDeploymentPlanV1 plan)
    {
        var kernels = plan.Source.Packages.Where(p => p.Name.StartsWith("linux-image-", StringComparison.Ordinal) &&
            p.Name.Length > 12 && char.IsAsciiDigit(p.Name[12])).ToArray();
        if (kernels.Length != 1) throw new NotSupportedException("An exact single installed kernel release is required.");
        return kernels[0].Name[12..];
    }

    private static string Fstab(DebianDeploymentPlanV1 plan, DebianRuntimeContextV1 context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (DebianTargetBoundary.Verify(plan, DebianStage.GenerateFstab, context).Availability != ObservationAvailability.Available)
            throw new InvalidDataException("Cannot generate fstab without verified target mounts.");
        var mounts = TargetRootMounts.Declare(plan.Root.GenerationId);
        var helpers = DebianTargetBoundary.Helpers.Select(h => mounts.Root + h.RelativePath).ToHashSet(StringComparer.Ordinal);
        var coreMounts = context.Mounts.Value with { Mounts = context.Mounts.Value.Mounts.Where(m => !helpers.Contains(m.MountPoint)).ToImmutableArray(),
            Paths = context.Mounts.Value.Paths.Where(p => !helpers.Contains(p.RequestedPath)).ToImmutableArray() };
        return TargetRootMounts.GenerateFstab(plan.Ownership, plan.Root, mounts, context.Inventory, Observations.Available(coreMounts)).Value;
    }
}

public interface IDebianCommandEvidenceSource
{
    ImmutableArray<DebianCommandEvidenceV1> Evidence(DebianStage stage);
}

public sealed class DebianNativeDeploymentOperations(IDebianIsolatedStageHost host) : IDebianDeploymentOperations, IDebianCommandEvidenceSource
{
    private readonly List<DebianCommandEvidenceV1> _evidence = [];
    public ImmutableArray<DebianCommandEvidenceV1> Evidence(DebianStage stage) => _evidence.Where(e => e.Stage == stage).ToImmutableArray();
    public async Task<int> PerformAsync(DebianDeploymentPlanV1 plan, DebianStage stage, ResolvedInstallationV1 currentDevices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentDevices);
        if (plan.Source.OfflinePackageSet is null) throw new NotSupportedException("Offline bundle is mandatory for native deployment.");
        if (plan.Source.OfflinePackageSet.RuntimeProfileSha256 is null)
            throw new NotSupportedException("An independently verified runtime profile is mandatory for native deployment.");
        var context = await host.ReinspectAsync(plan, stage, ct).ConfigureAwait(false);
        if (context.Availability != ObservationAvailability.Available) throw new IOException("Effective Linux isolation unavailable.");
        var resolved = DebianTargetBoundary.Verify(plan, stage, context.Value);
        if (resolved.Availability != ObservationAvailability.Available || resolved.Value != currentDevices)
            throw new IOException("Deployment target or namespace changed.");
        if (stage is DebianStage.PrepareNamespace or DebianStage.MountRoot or DebianStage.MountLinuxEsp or DebianStage.MountPayload or
            DebianStage.MountHelpers or DebianStage.UnmountHelpers or DebianStage.UnmountLinuxEsp or DebianStage.UnmountPayload or DebianStage.UnmountRoot)
            return await host.ApplyMountStageAsync(plan, stage, currentDevices, ct).ConfigureAwait(false);
        if (stage == DebianStage.InstallAgent)
        {
            if (DebianAgentProfiles.WorkerQualification.Availability != ObservationAvailability.Available)
                throw new NotSupportedException(DebianAgentProfiles.WorkerQualification.Code);
            return await host.InstallAgentAsync(plan, DebianAgentProfiles.Declare(plan), ct).ConfigureAwait(false);
        }
        if (stage is DebianStage.ConfigureHostname or DebianStage.ConfigureLocale or DebianStage.ConfigureTimezoneKeyboard or DebianStage.ConfigureNetwork or
            DebianStage.GenerateFstab or DebianStage.ConfigureApt or DebianStage.ConfigurePackagePolicy or DebianStage.InitializeMachineIdentity or DebianStage.FinalizeMachineIdentity)
        {
            var result = await host.ApplyConfigurationAsync(plan, stage, DebianNativeInstructions.Configuration(plan, stage, context.Value), ct).ConfigureAwait(false);
            if (result != 0 || stage is not (DebianStage.ConfigureLocale or DebianStage.ConfigurePackagePolicy)) return result;
        }
        if (stage == DebianStage.ConfigurePackagePolicy)
        {
            var policyEvidence = await host.ExecuteAsync(plan, DebianNativeInstructions.BootPackagePolicy, ct).ConfigureAwait(false);
            _evidence.Add(policyEvidence);
            if (!DebianNativeInstructions.ValidEvidence(plan, DebianNativeInstructions.BootPackagePolicy, policyEvidence) || policyEvidence.State != DebianCommandState.Exited)
                throw new IOException("Debian package policy outcome unknown.");
            if (policyEvidence.ExitCode != 0) return policyEvidence.ExitCode!.Value;
        }
        var instruction = DebianNativeInstructions.Command(plan, stage);
        var evidence = await host.ExecuteAsync(plan, instruction, ct).ConfigureAwait(false);
        _evidence.Add(evidence);
        if (!DebianNativeInstructions.ValidEvidence(plan, instruction, evidence) || evidence.State != DebianCommandState.Exited)
            throw new IOException("Debian command outcome unknown.");
        if (evidence.ExitCode != 0 || stage != DebianStage.ConfigureUser) return evidence.ExitCode!.Value;
        evidence = await host.ExecuteAsync(plan, DebianNativeInstructions.Password, ct).ConfigureAwait(false);
        _evidence.Add(evidence);
        if (!DebianNativeInstructions.ValidEvidence(plan, DebianNativeInstructions.Password, evidence) || evidence.State != DebianCommandState.Exited)
            throw new IOException("Debian credential command outcome unknown.");
        if (evidence.ExitCode != 0) return evidence.ExitCode!.Value;
        evidence = await host.ExecuteAsync(plan, DebianNativeInstructions.LockRoot, ct).ConfigureAwait(false);
        _evidence.Add(evidence);
        if (!DebianNativeInstructions.ValidEvidence(plan, DebianNativeInstructions.LockRoot, evidence) || evidence.State != DebianCommandState.Exited)
            throw new IOException("Debian root-account command outcome unknown.");
        return evidence.ExitCode!.Value;
    }
}
