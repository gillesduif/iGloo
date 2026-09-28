using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;
using static Igloo.Migration.Tests.DebianDeploymentFixture;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Migration.Tests;

public sealed class DebianContentFoundationTests
{
    internal static OfflineDebianPackageSetV1 PackageSet(DebianDeploymentPlanV1 plan) => new(1, plan.Root.GenerationId, Id(980),
        "trixie", "amd64", DebianWorkstationPolicy.Fingerprint, Hash, "3.0.3-fixture", Hash, true, false,
        ["adduser"], ["sudo"], plan.Source.Archives.Select(a => new DebianOfflineRepositoryV1(
            a.Suite == "trixie" ? "debian" : a.Suite == "trixie-updates" ? "debian-updates" : "debian-security", a.OriginUri,
            a.Suite, new("dists/" + a.Suite + "/InRelease", 99, a.InReleaseSha256), Hash, a.SigningFingerprint,
            [new("dists/" + a.Suite + "/main/binary-amd64/Packages.xz", 99, Hash)])).ToImmutableArray(),
        plan.Source.Packages.Select(p => new DebianOfflinePackageV1("debian", p.Name, p.Version, p.Architecture,
            new("pool/main/" + p.Name + ".deb", p.Length, p.Sha256))).ToImmutableArray())
        { BootstrapArchives = [new("debian", "adduser", "1.fixture", "amd64", new("pool/main/adduser.deb", 99, Hash))], RuntimeProfileSha256 = Hash };

    [Fact]
    public void OfflineSetIsGenerationBoundAndReopensIndependently()
    {
        var plan = Plan(TargetRootFixture.Create()); var set = PackageSet(plan);
        var bytes = OfflineDebianPackageSets.Serialize(set);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var reopened = OfflineDebianPackageSets.Reopen(bytes.ToArray(), hash, plan.Root.GenerationId);
        Assert.Equal(bytes, OfflineDebianPackageSets.Serialize(reopened));
        Assert.Equal(ObservationAvailability.Available, OfflineDebianPackageSets.Bind(plan, reopened).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, OfflineDebianPackageSets.Validate(reopened, Id(981)).Availability);
        bytes[^2] ^= 1;
        Assert.Throws<InvalidDataException>(() => OfflineDebianPackageSets.Reopen(bytes, hash, plan.Root.GenerationId));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("architecture")]
    [InlineData("policy")]
    [InlineData("keyring")]
    [InlineData("recommends")]
    [InlineData("suggests")]
    [InlineData("missing-package")]
    [InlineData("package-architecture")]
    [InlineData("duplicate")]
    [InlineData("filename")]
    [InlineData("repository")]
    [InlineData("index")]
    public void OfflineMalformedOrIncompleteSetRejected(string change)
    {
        var plan = Plan(TargetRootFixture.Create()); var set = PackageSet(plan);
        set = change switch
        {
            "release" => set with { Release = "forky" },
            "architecture" => set with { Architecture = "arm64" },
            "policy" => set with { PolicySha256 = Hash },
            "keyring" => set with { KeyringSha256 = "absent" },
            "recommends" => set with { InstallRecommends = false },
            "suggests" => set with { InstallSuggests = true },
            "missing-package" => set with { Packages = set.Packages.RemoveAt(0) },
            "package-architecture" => set with { Packages = set.Packages.SetItem(0, set.Packages[0] with { Architecture = "i386" }) },
            "duplicate" => set with { Packages = set.Packages.Add(set.Packages[0]) },
            "filename" => set with { Packages = set.Packages.SetItem(0, set.Packages[0] with { File = set.Packages[0].File with { Path = "../escape" } }) },
            "repository" => set with { Repositories = set.Repositories.RemoveAt(0) },
            "index" => set with { Repositories = set.Repositories.SetItem(0, set.Repositories[0] with { Indexes = [] }) },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Equal(ObservationAvailability.Ambiguous, OfflineDebianPackageSets.Validate(set, plan.Root.GenerationId).Availability);
    }

    [Fact]
    public void PackageSetCannotSubstitutePlanPackageOrArchiveIdentity()
    {
        var plan = Plan(TargetRootFixture.Create()); var set = PackageSet(plan);
        Assert.Equal(ObservationAvailability.Ambiguous, OfflineDebianPackageSets.Bind(plan, set with {
            Packages = set.Packages.SetItem(0, set.Packages[0] with { File = set.Packages[0].File with { Sha256 = new string('B', 64) } }) }).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, OfflineDebianPackageSets.Bind(plan, set with {
            Repositories = set.Repositories.SetItem(0, set.Repositories[0] with { SigningFingerprint = new string('C', 40) }) }).Availability);
    }

    [Fact]
    public void BootstrapArchiveVersionsRemainSeparateFromFinalSecuritySolution()
    {
        var plan = Plan(TargetRootFixture.Create()); var set = PackageSet(plan);
        set = set with { BootstrapArchives = set.BootstrapArchives.SetItem(0, set.BootstrapArchives[0] with {
            Version = "0.fixture", File = new("pool/main/adduser-old.deb", 98, new string('B', 64)) }) };
        Assert.Equal(ObservationAvailability.Available, OfflineDebianPackageSets.Validate(set, plan.Root.GenerationId).Availability);
        Assert.Equal("1.fixture", set.Packages.Single(p => p.Name == "adduser").Version);
        Assert.Equal("0.fixture", set.BootstrapArchives[0].Version);
    }

    [Theory]
    [InlineData("task-gnome-desktop")]
    [InlineData("gdm3")]
    [InlineData("network-manager")]
    [InlineData("sudo")]
    [InlineData("linux-image-amd64")]
    [InlineData("initramfs-tools")]
    [InlineData("firmware-linux")]
    [InlineData("python3")]
    [InlineData("shim-signed")]
    [InlineData("grub-efi-amd64-signed")]
    public void ProductPackagePolicyIncludesMandatoryWorkstationPackages(string package) => Assert.Contains(package, DebianWorkstationPolicy.Trixie.Packages);

    [Fact]
    public void TaskAndRecommendsPolicyProducesOnlyPinnedDeterministicAptOperations()
    {
        var policy = DebianWorkstationPolicy.Trixie;
        Assert.Equal(["standard"], policy.Tasks.ToArray());
        Assert.DoesNotContain("standard", policy.Packages);
        Assert.DoesNotContain("task-standard", policy.Packages);
        Assert.True(policy.InstallRecommends); Assert.False(policy.InstallSuggests);
        var packages = Plan(TargetRootFixture.Create()).Source.Packages;
        var command = DebianWorkstationPolicy.InstallPinned(packages);
        Assert.Equal(command.Arguments.ToArray(), DebianWorkstationPolicy.InstallPinned(packages.Reverse().ToImmutableArray()).Arguments.ToArray());
        Assert.DoesNotContain(command.Arguments, a => a.Contains("http", StringComparison.Ordinal));
        Assert.DoesNotContain(command.Arguments, a => a is "--allow-unauthenticated" or "--ignore-missing" or "--force-yes");
        Assert.Contains("APT::Install-Recommends=true", command.Arguments);
    }

    [Fact]
    public void NativePackageStageCannotConsumeOldUnresolvedSourceContract()
    {
        var plan = Plan(TargetRootFixture.Create());
        Assert.Throws<NotSupportedException>(() => DebianNativeInstructions.Command(plan, DebianStage.InstallDesktop));
        plan = plan with { Source = plan.Source with { OfflinePackageSet = PackageSet(plan) } };
        Assert.Equal("/usr/bin/apt-get", DebianNativeInstructions.Command(plan, DebianStage.InstallDesktop).Command.Executable);
        Assert.Throws<NotSupportedException>(() => DebianNativeInstructions.Command(plan, DebianStage.FinalizeFirmware));
        Assert.Throws<NotSupportedException>(() => DebianNativeInstructions.Command(plan, DebianStage.ConfigureSignedPackages));
    }

    [Theory]
    [InlineData(DebianStage.ConfigureHostname, "/etc/hostname")]
    [InlineData(DebianStage.ConfigureLocale, "/etc/locale.gen")]
    [InlineData(DebianStage.ConfigureTimezoneKeyboard, "/etc/localtime")]
    [InlineData(DebianStage.ConfigureNetwork, "/etc/resolv.conf")]
    [InlineData(DebianStage.InitializeMachineIdentity, "/etc/machine-id")]
    [InlineData(DebianStage.GenerateFstab, "/etc/fstab")]
    public void ConfigurationInstructionsBindExactRootFiles(DebianStage stage, string path)
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        var files = DebianNativeInstructions.Configuration(plan, stage, Context(state, plan, stage));
        Assert.Contains(files, f => f.Path == path);
        Assert.DoesNotContain(files, f => f.Path.Contains("/dev/", StringComparison.Ordinal));
        if (stage == DebianStage.GenerateFstab)
        {
            Assert.Contains("UUID=" + plan.Root.FileSystemUuid.ToString("D"), files[0].ContentOrTarget, StringComparison.Ordinal);
            Assert.DoesNotContain(state.Ownership.Esp.WindowsEsp.FileSystemUuid, files[0].ContentOrTarget, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CredentialCommandNeverContainsCredentialInArgumentsOrEvidence()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        var instruction = DebianNativeInstructions.Password;
        Assert.Equal(DebianInputKind.EncryptedPassword, instruction.Input);
        Assert.Equal(["--encrypted"], instruction.Command.Arguments.ToArray());
        var evidence = new DebianCommandEvidenceV1(plan.Root.GenerationId, DebianDeploymentPlanning.Fingerprint(plan), instruction.Stage,
            instruction.Command.Executable, Hash, DebianNativeInstructions.Fingerprint(instruction), DebianCommandState.Exited, 0);
        Assert.True(DebianNativeInstructions.ValidEvidence(plan, instruction, evidence));
        Assert.False(DebianNativeInstructions.ValidEvidence(plan, instruction, evidence with { GenerationId = Id(982) }));
        Assert.DoesNotContain("stdin", JsonSerializer.Serialize(evidence), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(plan.Identity.CredentialArtifactSha256, JsonSerializer.Serialize(instruction), StringComparison.Ordinal);
    }

    private static DebianKernelReadbackV1 Kernel(DebianDeploymentPlanV1 plan) => new(plan.Root.GenerationId, "6.fixture",
        new("linux-image-6.fixture", "1.fixture", "amd64", "install ok installed"),
        new("/boot/vmlinuz-6.fixture", 99, Hash, true, false),
        new("/usr/lib/modules/6.fixture/modules.dep", 99, Hash, true, false),
        new("/boot/initrd.img-6.fixture", 99, Hash, true, false), ["6.fixture"]);

    [Fact]
    public void KernelRequiresIndependentPackageModulesAndInitramfsEvidence()
    {
        var plan = Plan(TargetRootFixture.Create()); var kernel = Kernel(plan);
        Assert.Equal(ObservationAvailability.Available, DebianKernelEvidence.Verify(plan, A(kernel), true).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianKernelEvidence.Verify(plan, A(kernel with { Initramfs = null }), true).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianKernelEvidence.Verify(plan, A(kernel with { InitramfsKernelReleases = ["installer-kernel"] }), true).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianKernelEvidence.Verify(plan, A(kernel with { Package = kernel.Package with { DpkgStatus = "unpacked" } }), true).Availability);
        Assert.Equal(ObservationAvailability.Unavailable, DebianKernelEvidence.Verify(plan, Observations.Failure<DebianKernelReadbackV1>(ObservationAvailability.Unavailable, "Unknown"), true).Availability);
    }

    [Fact]
    public void SafeAgentProfileIsAdditiveSecretFreeAndDoesNotStartDuringDeployment()
    {
        var profile = DebianAgentProfiles.Declare(Plan(TargetRootFixture.Create()));
        Assert.False(profile.StartDuringDeployment);
        Assert.All(profile.Files, f => Assert.DoesNotContain("/boot/", f.Destination, StringComparison.Ordinal));
        Assert.Contains("PrivateDevices=yes", profile.UnitContent, StringComparison.Ordinal);
        Assert.Contains("InaccessiblePaths=/boot /sys/firmware", profile.UnitContent, StringComparison.Ordinal);
        Assert.DoesNotContain("manifest", profile.ConfigurationContent, StringComparison.OrdinalIgnoreCase);
        var actual = new DebianAgentReadbackV1(profile.GenerationId, profile.Files, profile.UnitContent, profile.EnableLink, profile.EnableTarget, false);
        Assert.Equal(DebianAgentCompletion.InstalledFirstBootPending, DebianAgentProfiles.Verify(profile, A(actual)).Value);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianAgentProfiles.Verify(profile, A(actual with { StartedDuringDeployment = true })).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianAgentProfiles.Verify(profile, A(actual with { Files = actual.Files.SetItem(0, actual.Files[0] with { Sha256 = new string('B', 64) }) })).Availability);
        Assert.Equal(ObservationAvailability.Unsupported, DebianAgentProfiles.WorkerQualification.Availability);
    }

    [Fact]
    public async Task LegacyAgentPayloadCannotBeReturnedByDebianPlugin()
    {
        var plugin = new Igloo.Distro.Debian.DebianPlugin();
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.GetAgentPayloadAsync());
        Assert.Throws<NotSupportedException>(() => plugin.GetInstallerBootSpec());
    }

    [Fact]
    public async Task ContentCheckpointIsNotCompleteAndUnresolvedBootloaderCannotBePromoted()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state); var runtime = new Runtime(state);
        var complete = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(plan);
        var prefix = complete with { Stages = complete.Stages.Take((int)DebianStage.ConfigureSignedPackages).ToImmutableArray() };
        Assert.Equal(DebianDeploymentState.PreBootStagesVerified, prefix.State);
        var bytes = DebianDeploymentArtifacts.Serialize(prefix);
        var reopened = DebianDeploymentArtifacts.Reopen(bytes, Convert.ToHexString(SHA256.HashData(bytes)), plan.Root.GenerationId, prefix.PlanSha256);
        Assert.Equal(prefix.State, reopened.State);
        Assert.NotEqual(DebianDeploymentState.EvidenceComplete, reopened.State);
    }

    private sealed class StageHost(TargetRootFixture.State state) : IDebianIsolatedStageHost
    {
        internal int Calls { get; private set; }
        internal bool ChangeMount { get; init; }
        internal bool Unavailable { get; init; }
        internal bool UnknownCommand { get; init; }
        internal int CommandExit { get; init; }
        internal List<DebianNativeInstructionV1> Instructions { get; } = [];
        public Task<Observation<DebianRuntimeContextV1>> ReinspectAsync(DebianDeploymentPlanV1 plan, DebianStage stage, CancellationToken ct)
        {
            if (Unavailable) return Task.FromResult(Observations.Failure<DebianRuntimeContextV1>(ObservationAvailability.Unsupported, "NoNativeEnforcement"));
            var context = Context(state, plan, stage);
            if (ChangeMount) context = context with { Mounts = A(context.Mounts.Value with {
                Mounts = context.Mounts.Value.Mounts.Select(m => m.MountPoint == state.MountPlan.Esp ? m with { Minor = 999 } : m).ToImmutableArray() }) };
            return Task.FromResult(A(context));
        }
        public Task<DebianCommandEvidenceV1> ExecuteAsync(DebianDeploymentPlanV1 plan, DebianNativeInstructionV1 instruction, CancellationToken ct)
        {
            Calls++; Instructions.Add(instruction);
            return Task.FromResult(new DebianCommandEvidenceV1(plan.Root.GenerationId, DebianDeploymentPlanning.Fingerprint(plan), instruction.Stage,
                instruction.Command.Executable, Hash, DebianNativeInstructions.Fingerprint(instruction),
                UnknownCommand ? DebianCommandState.StartedOutcomeUnknown : DebianCommandState.Exited, UnknownCommand ? null : CommandExit));
        }
        public Task<int> ApplyConfigurationAsync(DebianDeploymentPlanV1 plan, DebianStage stage, ImmutableArray<DebianConfigurationV1> exactFiles, CancellationToken ct)
        { Calls++; return Task.FromResult(0); }
        public Task<int> ApplyMountStageAsync(DebianDeploymentPlanV1 plan, DebianStage stage, ResolvedInstallationV1 freshlyResolvedDevices, CancellationToken ct)
        { Calls++; return Task.FromResult(0); }
        public Task<int> InstallAgentAsync(DebianDeploymentPlanV1 plan, DebianAgentInstallProfileV1 profile, CancellationToken ct)
        { Calls++; return Task.FromResult(0); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeDispatcherRejectsUnavailableIsolationOrMountSubstitutionBeforeEffect(bool unavailable)
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        plan = plan with { Source = plan.Source with { OfflinePackageSet = PackageSet(plan) } };
        var host = new StageHost(state) { Unavailable = unavailable, ChangeMount = !unavailable };
        var devices = InstallationOwnership.ResolveFormattedRoot(plan.Ownership, plan.Root.GenerationId, plan.Root, A(state.Inventory)).Value;
        await Assert.ThrowsAsync<IOException>(() => new DebianNativeDeploymentOperations(host).PerformAsync(plan, DebianStage.InstallKernel, devices, default));
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task AuthenticatedBundleAndAvailableHostCannotDispatchUnqualifiedBootstrap()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        plan = plan with { Source = plan.Source with { OfflinePackageSet = PackageSet(plan) } };
        var host = new StageHost(state); var operations = new DebianNativeDeploymentOperations(host);
        var devices = InstallationOwnership.ResolveFormattedRoot(plan.Ownership, plan.Root.GenerationId, plan.Root, A(state.Inventory)).Value;
        var failure = await Assert.ThrowsAsync<NotSupportedException>(() => operations.PerformAsync(plan, DebianStage.Bootstrap, devices, default));
        Assert.Equal("DebianBootstrapPrimitiveUnqualified", failure.Message);
        Assert.Equal(0, host.Calls);
        Assert.Empty(host.Instructions);
        Assert.Empty(operations.Evidence(DebianStage.Bootstrap));
    }

    [Fact]
    public async Task NativeDispatcherRetainsCommandOutcomeWithoutAssumingArtifactSuccess()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        plan = plan with { Source = plan.Source with { OfflinePackageSet = PackageSet(plan) } };
        var devices = InstallationOwnership.ResolveFormattedRoot(plan.Ownership, plan.Root.GenerationId, plan.Root, A(state.Inventory)).Value;
        var host = new StageHost(state) { CommandExit = 9 }; var operations = new DebianNativeDeploymentOperations(host);
        Assert.Equal(9, await operations.PerformAsync(plan, DebianStage.GenerateInitramfs, devices, default));
        Assert.Equal(9, Assert.Single(operations.Evidence(DebianStage.GenerateInitramfs)).ExitCode);
        Assert.Equal(new[] { "-u", "-k", "6.fixture" }, Assert.Single(host.Instructions).Command.Arguments.ToArray());
        var unknown = new DebianNativeDeploymentOperations(new StageHost(state) { UnknownCommand = true });
        await Assert.ThrowsAsync<IOException>(() => unknown.PerformAsync(plan, DebianStage.GenerateInitramfs, devices, default));
        Assert.Equal(DebianCommandState.StartedOutcomeUnknown, Assert.Single(unknown.Evidence(DebianStage.GenerateInitramfs)).State);
    }

    [Fact]
    public async Task NativeDispatcherCannotInstallUnqualifiedLegacyAgentOrInvokeFirmware()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        plan = plan with { Source = plan.Source with { OfflinePackageSet = PackageSet(plan) } };
        var host = new StageHost(state); var operations = new DebianNativeDeploymentOperations(host);
        var devices = InstallationOwnership.ResolveFormattedRoot(plan.Ownership, plan.Root.GenerationId, plan.Root, A(state.Inventory)).Value;
        await Assert.ThrowsAsync<NotSupportedException>(() => operations.PerformAsync(plan, DebianStage.InstallAgent, devices, default));
        await Assert.ThrowsAsync<NotSupportedException>(() => operations.PerformAsync(plan, DebianStage.FinalizeFirmware, devices, default));
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task ExactFinalizerHandoffRetainsUnsupportedHookAndFirmwareObservations()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        plan = plan with { Source = plan.Source with { OfflinePackageSet = PackageSet(plan) } };
        var runtime = new Runtime(state);
        var complete = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(plan);
        var prefix = complete with { Stages = complete.Stages.Take((int)DebianStage.ConfigureSignedPackages).ToImmutableArray() };
        var handoff = DebianBootFinalizationHandoff.Declare(prefix, Context(state, plan, DebianStage.ConfigureSignedPackages),
            Observations.Failure<ImmutableArray<DebianCheckEvidenceV1>>(ObservationAvailability.Unsupported, "HookNotQualified"),
            Observations.Failure<ImmutableDictionary<DebianFirmwareSlotV1, Igloo.Core.Recovery.FirmwareVariableV1>>(ObservationAvailability.Unavailable, "NoNativeInventory"));
        Assert.Equal(plan.Ownership.Esp.LinuxEsp.Volume, handoff.Value.LinuxEsp);
        Assert.NotEqual(handoff.Value.WindowsEsp.PartitionGuid, handoff.Value.LinuxEsp.PartitionGuid);
        Assert.Equal("/EFI/debian/shimx64.efi", handoff.Value.ShimPath);
        Assert.Equal(ObservationAvailability.Unsupported, handoff.Value.PackageHookEvidence.Availability);
        Assert.Equal(ObservationAvailability.Unavailable, handoff.Value.FirmwareBefore.Availability);
        Assert.Equal(ObservationAvailability.Unsupported, DebianBootFinalizationHandoff.FinalizerSupport.Availability);
    }
}
