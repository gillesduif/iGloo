using System.Collections.Immutable;
using System.Security.Cryptography;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;
using static Igloo.Migration.Tests.DebianDeploymentFixture;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Migration.Tests;

public sealed class DebianDeploymentTests
{
    public static TheoryData<DebianStage> AllStages => new(DebianDeploymentStages.Ordered);
    public static TheoryData<DebianStage> WritingStages => new(DebianDeploymentStages.Ordered.Where(s => !DebianDeploymentStages.IsReadOnly(s)));

    [Fact]
    public async Task SyntheticCompletePathRecordsEveryStageWithoutEnablingProduction()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state); var runtime = new Runtime(state); var journal = new Journal();
        var result = await new DebianDeploymentEngine(runtime, runtime, journal).RunAsync(plan);
        Assert.Equal(DebianDeploymentState.EvidenceComplete, result.State);
        Assert.Equal(43, result.Stages.Length);
        Assert.Equal(DebianDeploymentStages.Ordered.Where(s => !DebianDeploymentStages.IsReadOnly(s)), runtime.Invoked);
        Assert.Equal(86, journal.Checkpoints.Count);
        Assert.Equal(ObservationAvailability.Unsupported, DebianDeploymentSupport.Production.Availability);
        Assert.DoesNotContain(runtime.Invoked, s => s.ToString().Contains("Reboot", StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(AllStages))]
    public async Task FailureAtEveryStageRetainsPartialEvidenceAndNeverAdvances(DebianStage failure)
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state); var runtime = new Runtime(state) { FailAt = failure };
        var result = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(plan);
        Assert.Equal(DebianDeploymentState.Failed, result.State);
        Assert.Equal(failure, result.Stages[^1].Stage);
        Assert.All(result.Stages[..^1], s => Assert.Equal(DebianStageOutcome.AppliedAndVerified, s.Outcome));
        Assert.DoesNotContain(runtime.Invoked, s => s > failure);
        Assert.NotEmpty(result.Stages[^1].Checks); // Partial artifacts remain evidence, never success.
    }

    [Theory, MemberData(nameof(WritingStages))]
    public async Task ExceptionAfterPossibleWriteRemainsUnknownEvenWhenFilesExist(DebianStage failure)
    {
        var state = TargetRootFixture.Create(); var runtime = new Runtime(state) { ThrowAt = failure };
        var result = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(Plan(state));
        Assert.Equal(DebianDeploymentState.OutcomeUnknown, result.State);
        Assert.Equal(DebianStageOutcome.OutcomeUnknown, result.Stages[^1].Outcome);
        Assert.NotEmpty(result.Stages[^1].Checks);
        Assert.DoesNotContain(runtime.Invoked, s => s > failure);
    }

    [Theory]
    [InlineData(DebianStage.Bootstrap)]
    [InlineData(DebianStage.InstallKernel)]
    [InlineData(DebianStage.GenerateInitramfs)]
    [InlineData(DebianStage.ConfigureSignedPackages)]
    [InlineData(DebianStage.FinalizeFirmware)]
    [InlineData(DebianStage.InstallAgent)]
    [InlineData(DebianStage.UnmountRoot)]
    public async Task UnavailableIndependentReadbackDoesNotPromoteCommandSuccess(DebianStage failure)
    {
        var state = TargetRootFixture.Create(); var runtime = new Runtime(state) { ReadUnavailableAt = failure };
        var result = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(Plan(state));
        Assert.Equal(DebianStageOutcome.OutcomeUnknown, result.Stages[^1].Outcome);
        Assert.DoesNotContain(runtime.Invoked, s => s > failure);
    }

    [Theory, MemberData(nameof(WritingStages))]
    public async Task GuardFailurePreventsThatOperation(DebianStage failure)
    {
        var state = TargetRootFixture.Create(); var runtime = new Runtime(state) { GuardUnavailableAt = failure };
        var run = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(Plan(state));
        Assert.Equal(DebianStageOutcome.NotStarted, run.Stages[^1].Outcome);
        Assert.DoesNotContain(failure, runtime.Invoked);
    }

    [Fact]
    public async Task ChangedMountAfterIntentReopenIsCaughtBeforeCall()
    {
        var state = TargetRootFixture.Create(); var reads = 0; var runtime = new Runtime(state)
        {
            ChangeContext = (stage, context) => stage == DebianStage.ConfigureSignedPackages && ++reads == 2
                ? context with { Mounts = A(context.Mounts.Value with { Mounts = context.Mounts.Value.Mounts.Select(m =>
                    m.MountPoint == state.MountPlan.Esp ? m with { Minor = 900 } : m).ToImmutableArray() }) } : context,
        };
        var run = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(Plan(state));
        Assert.Equal(DebianStageOutcome.NotStarted, run.Stages[^1].Outcome);
        Assert.DoesNotContain(DebianStage.ConfigureSignedPackages, runtime.Invoked);
    }

    [Fact]
    public async Task ReopenFailurePreventsAnyOperationAndGenerationCannotBeRetried()
    {
        var state = TargetRootFixture.Create(); var runtime = new Runtime(state); var journal = new Journal { FailReopen = true };
        var engine = new DebianDeploymentEngine(runtime, runtime, journal);
        await Assert.ThrowsAsync<IOException>(() => engine.RunAsync(Plan(state)));
        Assert.Empty(runtime.Invoked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(Plan(state)));
        await Assert.ThrowsAsync<IOException>(() => new DebianDeploymentEngine(runtime, runtime, journal).RunAsync(Plan(state)));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("plan")]
    [InlineData("missing-check")]
    [InlineData("extra-check")]
    [InlineData("missing-digest")]
    public async Task SubstitutedReadbackCannotCompleteStage(string change)
    {
        var state = TargetRootFixture.Create(); var runtime = new Runtime(state) { ChangeReadback = r => change switch
        {
            "generation" => r with { GenerationId = Id(999) },
            "plan" => r with { PlanSha256 = new string('F', 64) },
            "missing-check" => r with { Checks = [] },
            "extra-check" => r with { Checks = r.Checks.Add(r.Checks[0]) },
            _ => r with { Checks = r.Checks.Select(c => c with { ReadbackSha256 = null }).ToImmutableArray() },
        } };
        var run = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(Plan(state));
        Assert.Equal(DebianDeploymentState.OutcomeUnknown, run.State);
        Assert.Empty(runtime.Invoked);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("duplicate-partuuid")]
    [InlineData("duplicate-fsuuid")]
    [InlineData("disk")]
    [InlineData("root-type")]
    [InlineData("esp-type")]
    [InlineData("geometry")]
    [InlineData("filesystem")]
    [InlineData("missing")]
    public async Task OwnershipFailureStopsBeforeNamespaceOrMount(string change)
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state); var root = state.Root.Partition.PartitionGuid;
        var esp = state.Ownership.Esp.LinuxEsp.Volume.PartitionGuid;
        var inventory = state.Inventory;
        inventory = change switch
        {
            "duplicate-partuuid" => inventory with { Partitions = inventory.Partitions.Add(inventory.Partitions[0] with { DevicePath = "/dev/another1" }) },
            "duplicate-fsuuid" => inventory with { Partitions = inventory.Partitions.Select(p => p.PartitionGuid == esp ? p with { FileSystem = Fs("FAT32", state.Ownership.Esp.WindowsEsp.FileSystemUuid) } : p).ToImmutableArray() },
            "disk" => inventory with { Disks = inventory.Disks.Select(d => d with { GptDiskGuid = Id(555) }).ToImmutableArray() },
            "root-type" => inventory with { Partitions = inventory.Partitions.Select(p => p.PartitionGuid == root ? p with { PartitionType = PreparationSpacePlanning.BasicDataType } : p).ToImmutableArray() },
            "esp-type" => inventory with { Partitions = inventory.Partitions.Select(p => p.PartitionGuid == esp ? p with { PartitionType = PreparationSpacePlanning.BasicDataType } : p).ToImmutableArray() },
            "geometry" => inventory with { Partitions = inventory.Partitions.Select(p => p.PartitionGuid == root ? p with { SizeBytes = p.SizeBytes - 512 } : p).ToImmutableArray() },
            "filesystem" => inventory with { Partitions = inventory.Partitions.Select(p => p.PartitionGuid == root ? p with { FileSystem = Fs("EXT4", Id(600).ToString("D")) } : p).ToImmutableArray() },
            "missing" => inventory with { Partitions = inventory.Partitions.Where(p => p.PartitionGuid != esp).ToImmutableArray() },
            _ => inventory,
        };
        var runtime = new Runtime(state) { ChangeContext = (_, c) => c with { Inventory = A(inventory), GenerationId = change == "generation" ? Id(900) : c.GenerationId } };
        var run = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(plan);
        Assert.Equal(DebianStageOutcome.NotStarted, run.Stages[^1].Outcome);
        Assert.Empty(runtime.Invoked);
    }

    [Fact]
    public void PreservedWindowsRootAndEspCannotAliasOwnedTargets()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        var wrongRoot = plan.Root with { Partition = PreparedStorageOwnership.PartitionOf(plan.Ownership.Esp.WindowsEsp.Volume) };
        Assert.False(DebianDeploymentPlanning.ValidStructure(plan with { Root = wrongRoot }));
        var wrongEsp = plan.Ownership with { Esp = plan.Ownership.Esp with { LinuxEsp = plan.Ownership.Esp.WindowsEsp } };
        Assert.NotEqual(ObservationAvailability.Available,
            DebianDeploymentPlanning.Validate(plan with { Ownership = wrongEsp }, plan.Root.GenerationId, A(state.Inventory)).Availability);
    }

    [Fact]
    public async Task ReversedInventoryAndMountOrderDoNotAffectResolution()
    {
        var state = TargetRootFixture.Create(); var runtime = new Runtime(state) { ChangeContext = (_, c) => c with
        {
            Inventory = A(c.Inventory.Value with { Partitions = c.Inventory.Value.Partitions.Reverse().ToImmutableArray() }),
            Mounts = A(c.Mounts.Value with { Mounts = c.Mounts.Value.Mounts.Reverse().ToImmutableArray() }),
        } };
        var run = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(Plan(state));
        Assert.Equal(DebianDeploymentState.EvidenceComplete, run.State);
    }

    [Theory]
    [InlineData("windows-mounted")]
    [InlineData("second-esp")]
    [InlineData("host-run")]
    [InlineData("helper-substitution")]
    [InlineData("isolation")]
    public void PackageNamespaceRejectsExposureOrSubstitution(string change)
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        var context = Context(state, plan, DebianStage.ConfigureSignedPackages);
        var raw = context.Mounts.Value;
        var windows = raw.DeviceNumbers.Single(d => d.DevicePath == state.Inventory.Partitions.Single(p => p.PartitionGuid == state.Ownership.Esp.WindowsEsp.Volume.PartitionGuid).DevicePath);
        context = change switch
        {
            "windows-mounted" => context with { Mounts = A(raw with { Mounts = raw.Mounts.Add(new(100, 1, windows.Major, windows.Minor, "/", "/windows-esp", "vfat", ["ro"], [], ["rw"])) }) },
            "second-esp" => context with { Mounts = A(raw with { Mounts = raw.Mounts.Add(new(100, 10, windows.Major, windows.Minor, "/", state.MountPlan.Root + "/second-esp", "vfat", ["rw"], [], ["rw"])) }) },
            "host-run" => context with { Mounts = A(raw with { Mounts = raw.Mounts.Select(m => m.MountPoint.EndsWith("/run", StringComparison.Ordinal) ? m with { FileSystemRoot = "/host/run" } : m).ToImmutableArray() }) },
            "helper-substitution" => context with { Mounts = A(raw with { Paths = raw.Paths.Select(p => p.RequestedPath.EndsWith("/dev", StringComparison.Ordinal) ? p with { ContainsSymlink = true } : p).ToImmutableArray() }) },
            _ => context with { Isolation = A(context.Isolation.Value with { Enforced = context.Isolation.Value.Enforced.Remove(DebianIsolationRequirement.FirmwareWritesDenied) }) },
        };
        Assert.NotEqual(ObservationAvailability.Available, DebianTargetBoundary.Verify(plan, DebianStage.ConfigureSignedPackages, context).Availability);
    }

    [Fact]
    public void HelpersAndCoreMountsHaveExplicitReverseTeardownAndNoRawHostBind()
    {
        Assert.Equal(new[] { "/run/igloo-source", "/run", "/sys", "/proc", "/dev/pts", "/dev" }, DebianTargetBoundary.ReverseHelperUnmountOrder);
        Assert.Equal(new[] { DebianStage.UnmountHelpers, DebianStage.UnmountLinuxEsp, DebianStage.UnmountPayload, DebianStage.UnmountRoot },
            DebianDeploymentStages.Ordered.Where(s => s.ToString().StartsWith("Unmount", StringComparison.Ordinal)));
        Assert.Contains(DebianTargetBoundary.Helpers, h => h.RelativePath == "/sys" && h.Mode == "ro");
    }

    [Fact]
    public void FstabUsesOnlyExactOwnedFilesystemUuidsAndIsDeterministic()
    {
        var state = TargetRootFixture.Create();
        var fstab = TargetRootMounts.GenerateFstab(state.Ownership, state.Root, state.MountPlan, A(state.Inventory), A(state.Mounts)).Value;
        Assert.Equal($"UUID={state.Root.FileSystemUuid:D} / ext4 defaults,errors=remount-ro 0 1\nUUID=2222-BBBB /boot/efi vfat umask=0077 0 1\n", fstab);
        Assert.DoesNotContain("/dev/", fstab, StringComparison.Ordinal);
        Assert.DoesNotContain("1111-AAAA", fstab, StringComparison.Ordinal);
        var reversed = state.Inventory with { Partitions = state.Inventory.Partitions.Reverse().ToImmutableArray() };
        var recaptured = TargetRootMounts.GenerateFstab(state.Ownership, state.Root, state.MountPlan, A(reversed), A(state.Mounts)).Value;
        Assert.Equal(DebianDeploymentPlanning.TextHash(fstab), DebianDeploymentPlanning.TextHash(recaptured));
    }

    [Fact]
    public void SourceIdentityDoesNotQualifyAnExecutableBootstrap()
    {
        var plan = Plan(TargetRootFixture.Create());
        Assert.True(DebianDeploymentPlanning.ValidStructure(plan));
        Assert.Throws<NotSupportedException>(() => DebianNativeInstructions.Command(plan, DebianStage.Bootstrap));
        Assert.False(DebianDeploymentPlanning.ValidStructure(plan with { Source = plan.Source with { Release = "stable" } }));
        Assert.False(DebianDeploymentPlanning.ValidStructure(plan with { Source = plan.Source with { Architecture = "arm64" } }));
        Assert.False(DebianDeploymentPlanning.ValidStructure(plan with { Source = plan.Source with { KeyringSha256 = "" } }));
        Assert.False(DebianDeploymentPlanning.ValidStructure(plan with { Source = plan.Source with { DependencySolutionSha256 = "" } }));
    }

    [Theory]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Ambiguous)]
    [InlineData(ObservationAvailability.Absent)]
    public void SourceAuthenticationFailureIsNotMissingDefaults(ObservationAvailability availability)
    {
        var plan = Plan(TargetRootFixture.Create());
        Assert.Equal(availability, DebianReadbackRules.Source(plan, Observations.Failure<DebianSourceV1>(availability, "SignatureReadFailed"), Now).Availability);
    }

    [Fact]
    public void ExpiredOrChangedRepositoryMetadataFailsEvenWithSameCodename()
    {
        var plan = Plan(TargetRootFixture.Create());
        Assert.Equal(ObservationAvailability.Available, DebianReadbackRules.Source(plan, A(plan.Source), Now).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianReadbackRules.Source(plan, A(plan.Source), Now.AddDays(8)).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianReadbackRules.Source(plan, A(plan.Source with { Packages = plan.Source.Packages.SetItem(0, plan.Source.Packages[0] with { Sha256 = new string('B', 64) }) }), Now).Availability);
    }

    [Fact]
    public void WorkstationPolicyIncludesDesktopAndBootChainAndCannotBecomeMinimalBase()
    {
        var plan = Plan(TargetRootFixture.Create());
        foreach (var package in new[] { "task-gnome-desktop", "gdm3", "network-manager", "sudo", "firmware-linux", "linux-image-amd64", "initramfs-tools", "shim-signed", "grub-efi-amd64-signed", "python3", "ntfs-3g" })
        {
            Assert.Contains(package, DebianDeploymentPlanning.WorkstationPackages);
            Assert.False(DebianDeploymentPlanning.ValidStructure(plan with { Source = plan.Source with { Packages = plan.Source.Packages.Where(p => p.Name != package).ToImmutableArray() } }));
        }
        Assert.DoesNotContain("openssh-server", DebianDeploymentPlanning.WorkstationPackages);
        Assert.DoesNotContain("partman-auto", DebianDeploymentPlanning.WorkstationPackages);
        Assert.DoesNotContain("task-standard", DebianDeploymentPlanning.WorkstationPackages);
        Assert.Equal("standard", DebianDeploymentPlanning.StandardTask);
        var installed = plan.Source.Packages.Select(p => new DebianInstalledPackageV1(p.Name, p.Version, p.Architecture, "install ok installed")).ToImmutableArray();
        Assert.True(DebianReadbackRules.Packages(plan, A(installed)).Value);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianReadbackRules.Packages(plan, A(installed.SetItem(0, installed[0] with { DpkgStatus = "install ok unpacked" }))).Availability);
    }

    [Fact]
    public void MachineIdentityNeverCopiesRuntimeStateAndConfigurationRejectsInjection()
    {
        var plan = Plan(TargetRootFixture.Create());
        Assert.Equal("/etc/machine-id", DebianDeploymentPlanning.IdentityPathRequiredEmpty);
        Assert.Contains("/var/lib/NetworkManager/secret_key", DebianDeploymentPlanning.IdentityPathsRequiredAbsent);
        Assert.Contains("/etc/ssh/ssh_host_*", DebianDeploymentPlanning.IdentityPathsRequiredAbsent);
        Assert.Equal("/etc/machine-id", DebianDeploymentPlanning.DbusMachineIdLink);
        Assert.Equal("/run/NetworkManager/resolv.conf", DebianDeploymentPlanning.InstalledResolverLink);
        Assert.False(DebianDeploymentPlanning.ValidStructure(plan with { Identity = plan.Identity with { Hostname = "valid\nextra" } }));
        Assert.False(DebianDeploymentPlanning.ValidStructure(plan with { Identity = plan.Identity with { Timezone = "../../etc" } }));
    }

    [Fact]
    public void ConfigurationAndAccountsAreDeterministicTargetOnlyPolicies()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        var config = DebianTargetConfiguration.Generate(plan);
        Assert.Equal(config.ToArray(), DebianTargetConfiguration.Generate(plan).ToArray());
        Assert.Equal("debian-workstation\n", config.Single(c => c.Path == "/etc/hostname").ContentOrTarget);
        Assert.Equal(DebianConfigurationKind.Utf8File, config.Single(c => c.Path == "/etc/machine-id").Kind);
        Assert.Equal("", config.Single(c => c.Path == "/etc/machine-id").ContentOrTarget);
        Assert.Equal("/etc/machine-id", config.Single(c => c.Path == "/var/lib/dbus/machine-id").ContentOrTarget);
        Assert.Contains(config, c => c.Path == "/usr/sbin/policy-rc.d" && c.Kind == DebianConfigurationKind.MustBeAbsent);
        Assert.Contains(config, c => c.Path == "/etc/apt/igloo-offline.sources" && c.Kind == DebianConfigurationKind.MustBeAbsent);
        Assert.Equal(1000U, DebianTargetConfiguration.Accounts(plan).UserId);
        Assert.Equal(new[] { "sudo" }, DebianTargetConfiguration.Accounts(plan).SupplementaryGroups);
        Assert.True(DebianTargetConfiguration.Accounts(plan).RootLoginLocked);
        var offline = DebianTargetConfiguration.OfflineAptSources(plan);
        Assert.Contains("file:/run/igloo-source/debian-bundle/debian", offline, StringComparison.Ordinal);
        Assert.DoesNotContain("trusted=yes", offline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyPartmanConfigCannotBeExportedAsFallback()
    {
        var plugin = new Igloo.Distro.Debian.DebianPlugin();
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.RenderInstallerConfigAsync(new Igloo.Core.Models.MigrationManifest
        {
            DistroId = "debian", User = new() { WindowsUsername = "fixture", PreferredLinuxUsername = "fixture" },
            Files = new() { StagingPath = "fixture" }, Hardware = new(),
        }));
    }

    [Fact]
    public void SignedFinalizerIsExplicitNoNvramNoRemovableNoUnsignedFallback()
    {
        var args = DebianReadbackRules.LoaderFilesCommand.Arguments;
        Assert.Contains("--efi-directory=/boot/efi", args);
        Assert.Contains("--bootloader-id=debian", args);
        Assert.Contains("--uefi-secure-boot", args);
        Assert.Contains("--no-nvram", args);
        Assert.DoesNotContain("--removable", args);
        Assert.DoesNotContain("--force", args);
        Assert.Contains("GRUB_DISABLE_OS_PROBER=true", DebianDeploymentPlanning.GrubDefaults, StringComparison.Ordinal);
        Assert.Equal(ObservationAvailability.Unsupported, DebianDeploymentSupport.Production.Availability);
    }

    [Fact]
    public void FirmwareComparisonRejectsUndeclaredChangesAndMissingObservations()
    {
        var slot = new DebianFirmwareSlotV1(Guid.Parse("8be4df61-93ca-11d2-aa0d-00e098032b8c"), "BootOrder");
        var beforeValue = new FirmwareVariableV1(A(ImmutableArray.Create<byte>(0, 0)), null, A(7U));
        var afterValue = beforeValue with { Bytes = A(ImmutableArray.Create<byte>(9, 0, 0, 0)) };
        var before = ImmutableDictionary<DebianFirmwareSlotV1, FirmwareVariableV1>.Empty.Add(slot, beforeValue);
        var after = before.SetItem(slot, afterValue);
        Assert.True(DebianReadbackRules.FirmwareDelta([new(slot, beforeValue, afterValue)], A(before), A(after)).Value);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianReadbackRules.FirmwareDelta([], A(before), A(after)).Availability);
        Assert.Equal(ObservationAvailability.Unavailable, DebianReadbackRules.FirmwareDelta([], A(before.Clear()), A(after)).Availability);
        var denied = after.SetItem(slot, afterValue with { VariableAttributes = Observations.Failure<uint>(ObservationAvailability.AccessDenied, "Denied") });
        Assert.Equal(ObservationAvailability.AccessDenied, DebianReadbackRules.FirmwareDelta([], A(before), A(denied)).Availability);
        var unknown = slot with { Name = "MokNew" };
        var moreBefore = before.Add(unknown, beforeValue); var moreAfter = after.Add(unknown, afterValue);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianReadbackRules.FirmwareDelta([new(slot, beforeValue, afterValue)], A(moreBefore), A(moreAfter)).Availability);
    }

    [Fact]
    public void AgentPayloadMismatchOrChangedGenerationIsNotSuccessfulInstallation()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state);
        var files = plan.Agent.Payload.Files.Select(f => new InstallerPayloadReadbackV1(state.Ownership.Esp.Payload.Volume.PartitionGuid, f.File.RelativePath, f.File.Length, f.File.Sha256)).ToImmutableArray();
        Assert.True(DebianReadbackRules.Agent(plan, A(state.Inventory), A(files)).Value);
        Assert.Equal(ObservationAvailability.Ambiguous, DebianReadbackRules.Agent(plan, A(state.Inventory), A(files.SetItem(0, files[0] with { Sha256 = new string('F', 64) }))).Availability);
        Assert.False(DebianDeploymentPlanning.ValidStructure(plan with { Agent = plan.Agent with { Payload = plan.Agent.Payload with { GenerationId = Id(900) } } }));
    }

    [Fact]
    public async Task CompleteReceiptReopensIndependentlyButDoesNotCertifyProductionOrBootability()
    {
        var state = TargetRootFixture.Create(); var plan = Plan(state); var runtime = new Runtime(state);
        var run = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(plan);
        var fixture = TargetRootFixture.Receipt(state);
        fixture = fixture with { Files = fixture.Files.Select(f => f.Role == InstalledFileRole.AgentService
            ? f with { Path = "/etc/systemd/system/igloo-deployment.service" } : f).ToImmutableArray() };
        var packages = plan.Source.Packages.Select(p => new DebianInstalledPackageV1(p.Name, p.Version, p.Architecture, "install ok installed")).ToImmutableArray();
        var receipt = DebianInstallationReceipts.Produce(run, packages, fixture.Files, fixture.Firmware);
        var bytes = DebianInstallationReceipts.Serialize(receipt); var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = Path.Combine(Path.GetTempPath(), "igloo-debian-receipt-" + Guid.NewGuid().ToString("D") + ".json");
        try
        {
            PersistFixture(path, bytes);
            var reopened = DebianInstallationReceipts.Reopen(await File.ReadAllBytesAsync(path), hash, plan.Root.GenerationId, run.PlanSha256);
            Assert.Equal(InstallationEvidenceState.EvidenceComplete, DebianInstallationReceipts.Assess(reopened, A(state.Inventory), A(fixture.Files), A(fixture.Firmware!), A(packages)).State);
            Assert.Throws<InvalidDataException>(() => DebianInstallationReceipts.Reopen(bytes, hash, Id(999), run.PlanSha256));
            var corrupted = bytes.ToArray(); corrupted[0] ^= 1;
            Assert.Throws<InvalidDataException>(() => DebianInstallationReceipts.Reopen(corrupted, hash, plan.Root.GenerationId, run.PlanSha256));
            Assert.Equal(ObservationAvailability.Unsupported, DebianDeploymentSupport.Production.Availability);
        }
        finally { File.Delete(path); }
    }

    private static void PersistFixture(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialReceiptCannotBePromotedByBootableFiles(bool ambiguous)
    {
        var state = TargetRootFixture.Create(); var runtime = new Runtime(state) { FailAt = ambiguous ? null : DebianStage.InstallAgent, ThrowAt = ambiguous ? DebianStage.InstallAgent : null };
        var run = await new DebianDeploymentEngine(runtime, runtime, new Journal()).RunAsync(Plan(state));
        var fixture = TargetRootFixture.Receipt(state);
        var receipt = DebianInstallationReceipts.Produce(run, [], fixture.Files, fixture.Firmware);
        var bytes = DebianInstallationReceipts.Serialize(receipt);
        var reopened = DebianInstallationReceipts.Reopen(bytes, Convert.ToHexString(SHA256.HashData(bytes)), state.Root.GenerationId, run.PlanSha256);
        Assert.Equal(ambiguous ? InstallationEvidenceState.OutcomeUnknown : InstallationEvidenceState.Failed,
            DebianInstallationReceipts.Assess(reopened, A(state.Inventory), A(fixture.Files), A(fixture.Firmware!), A(ImmutableArray<DebianInstalledPackageV1>.Empty)).State);
    }
}
