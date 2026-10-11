using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Cli;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Recovery;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class MigrationAllocationTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void WindowsAllocationSourceRequiresExactCurrentPartitionAndSupportedSizeEvidence()
    {
        var targetSource = new FakeTargetSource();
        var target = targetSource.Target!.WindowsVolume;
        var disk = target.Disk;
        var diskRow = new DiskObservation(Observations.Available(7U),
            Observations.Available(disk.UniqueId), Observations.Available(disk.UniqueIdFormat),
            Observations.Available("PRIVATE_SERIAL"), Observations.Available(disk.BusType),
            Observations.Available("PRIVATE_MODEL"), Observations.Available(disk.SizeBytes),
            Observations.Available(2U), Observations.Available(disk.GptDiskGuid),
            Observations.Available(disk.LogicalSectorSize),
            Observations.Available(disk.PhysicalSectorSize));
        var partition = new PartitionObservation(Observations.Available(7U),
            Observations.Available(1U), Observations.Available(target.PartitionGuid),
            Observations.Available(target.PartitionType), Observations.Available(target.OffsetBytes),
            Observations.Available(target.SizeBytes), Observations.Available(false),
            Observations.Available(true), Observations.Available(false), Observations.Available('C'),
            Observations.Available(ImmutableArray<string>.Empty));
        var volume = new VolumeObservation(Observations.Available(target.VolumeGuid),
            Observations.Available(target.PartitionGuid), Observations.Available("NTFS"),
            Observations.Available("PRIVATE_LABEL"), Observations.Available('C'),
            Observations.Available("Healthy"), Observations.Available(false));
        var storage = new FakeStorage(new([diskRow], [partition], [volume]), target);
        var source = new WindowsAllocationObservationSource(storage, targetSource);
        Assert.Equal(target.PartitionGuid,
            Assert.Single(source.Observe(target)!.Partitions).PartitionGuid);
        storage.Supported = new WindowsObservation<WindowsStorageRow>(null);
        Assert.Null(source.Observe(target));
        storage.Supported = FakeStorage.SupportedRow(target);
        storage.Partition = storage.Partition with { ObjectPath = null };
        Assert.Null(source.Observe(target));
        storage.Partition = FakeStorage.PartitionRow(target);
        storage.Snapshot = storage.Snapshot with { Partitions = [partition,
            partition with { Number = Observations.Available(2U) }] };
        Assert.Null(source.Observe(target));
    }

    [Fact]
    public async Task AdvisoryAllocationReopensAfterDiskRenumberingButNeverAuthorizesExecution()
    {
        var directory = NewFixtureDirectory();
        try
        {
            var (plans, targets, allocations, targetSource, _, plan, targetId) =
                await FixtureAsync(directory);
            var observed = new FakeAllocationSource(targetSource.Target!.WindowsVolume);
            allocations = new(Path.Join(directory, "allocations"), targets, observed);
            var issued = await allocations.RecordAsync(plan.PlanId, targetId,
                plan.ObservedStateFingerprint, observed.Current!, 60 * GiB);
            Assert.NotEqual(Guid.Empty, issued.AllocationId);
            Assert.Equal("WindowsBootNtfsShrinkIntentV1", issued.Scope);
            Assert.Equal(60 * GiB, issued.PlannedShrinkBytes);
            Assert.Equal(MigrationCapacityDisposition.KnownAvailable, issued.CapacityState);
            Assert.Contains("InstallerLayoutNotBound", issued.WarningCodes);
            Assert.False(issued.Executable);
            targetSource.Target = targetSource.Target with { ObservedDiskNumber = 9 };
            var reopened = new FileMigrationAllocationRegistry(Path.Join(directory, "allocations"),
                new FileMigrationTargetRegistry(Path.Join(directory, "targets"), targetSource), observed);
            var resolved = await reopened.ResolveAsync(issued.AllocationId, plan.PlanId, targetId);
            Assert.Equal(JsonSerializer.Serialize(issued, CommandProtocol.JsonOptions),
                JsonSerializer.Serialize(resolved, CommandProtocol.JsonOptions));
            await Assert.ThrowsAsync<StateChangedException>(() => reopened.ResolveAsync(
                issued.AllocationId, Guid.NewGuid(), targetId));
            await Assert.ThrowsAsync<StateChangedException>(() => reopened.ResolveAsync(
                issued.AllocationId, plan.PlanId, Guid.NewGuid()));
            var record = await File.ReadAllTextAsync(Path.Join(directory, "allocations",
                issued.AllocationId.ToString("N") + ".json"));
            Assert.DoesNotContain("PRIVATE_DISK_ID", record, StringComparison.Ordinal);
            Assert.DoesNotContain(observed.Current!.WindowsVolume.PartitionGuid.ToString("D"),
                record, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LinuxPassword", record, StringComparison.Ordinal);
        }
        finally { DeleteFixture(directory); }
    }

    [Fact]
    public async Task ChangedTargetPartitionGeometryOrShrinkLimitRejectsSavedAllocation()
    {
        var directory = NewFixtureDirectory();
        try
        {
            var (_, targets, _, targetSource, _, plan, targetId) = await FixtureAsync(directory);
            var observed = new FakeAllocationSource(targetSource.Target!.WindowsVolume);
            var allocations = new FileMigrationAllocationRegistry(Path.Join(directory, "allocations"),
                targets, observed);
            var issued = await allocations.RecordAsync(plan.PlanId, targetId,
                plan.ObservedStateFingerprint, observed.Current!, 60 * GiB);
            var original = observed.Current!;
            observed.Current = original with { Partitions = original.Partitions.Select(p =>
                p.PartitionGuid == original.WindowsVolume.PartitionGuid
                    ? p with { PartitionGuid = Guid.NewGuid() } : p).ToArray() };
            await Assert.ThrowsAsync<CommandPreconditionException>(() => allocations.ResolveAsync(
                issued.AllocationId, plan.PlanId, targetId));
            observed.Current = original with { Partitions = original.Partitions.Select(p =>
                p.PartitionGuid == original.WindowsVolume.PartitionGuid
                    ? p with { SizeBytes = p.SizeBytes - 1024 * 1024 } : p).ToArray() };
            await Assert.ThrowsAsync<CommandPreconditionException>(() => allocations.ResolveAsync(
                issued.AllocationId, plan.PlanId, targetId));
            observed.Current = original with { SupportedSize = new(45UL * (ulong)GiB,
                100UL * (ulong)GiB) };
            await Assert.ThrowsAsync<StateChangedException>(() => allocations.ResolveAsync(
                issued.AllocationId, plan.PlanId, targetId));
            observed.Current = original with { Partitions = [.. original.Partitions,
                new AllocationPartitionIdentity(Guid.NewGuid(),
                    new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),
                    150UL * (ulong)GiB, 10UL * (ulong)GiB)] };
            await Assert.ThrowsAsync<StateChangedException>(() => allocations.ResolveAsync(
                issued.AllocationId, plan.PlanId, targetId));
            observed.Current = original;
            targetSource.Target = targetSource.Target with
            { WindowsVolume = targetSource.Target.WindowsVolume with
                { Disk = targetSource.Target.WindowsVolume.Disk with { GptDiskGuid = Guid.NewGuid() } } };
            await Assert.ThrowsAsync<StateChangedException>(() => allocations.ResolveAsync(
                issued.AllocationId, plan.PlanId, targetId));
        }
        finally { DeleteFixture(directory); }
    }

    [Fact]
    public async Task InsufficientOrUnobservedShrinkCannotIssueAllocationAndCorruptionFailsClosed()
    {
        var directory = NewFixtureDirectory();
        try
        {
            var (_, targets, _, targetSource, _, plan, targetId) = await FixtureAsync(directory);
            var observed = new FakeAllocationSource(targetSource.Target!.WindowsVolume);
            var allocations = new FileMigrationAllocationRegistry(Path.Join(directory, "allocations"),
                targets, observed);
            await Assert.ThrowsAsync<CommandPreconditionException>(() => allocations.RecordAsync(
                plan.PlanId, targetId, plan.ObservedStateFingerprint, observed.Current!, 70 * GiB));
            Assert.False(Directory.Exists(Path.Join(directory, "allocations")));
            var issued = await allocations.RecordAsync(plan.PlanId, targetId,
                plan.ObservedStateFingerprint, observed.Current!, 60 * GiB);
            var path = Path.Join(directory, "allocations", issued.AllocationId.ToString("N") + ".json");
            var record = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            record["plannedShrinkBytes"] = 1;
            await File.WriteAllTextAsync(path, record.ToJsonString());
            await Assert.ThrowsAsync<CommandEvidenceException>(() => allocations.ResolveAsync(
                issued.AllocationId, plan.PlanId, targetId));
        }
        finally { DeleteFixture(directory); }
    }

    [Fact]
    public async Task PublicHandlerAndCliUseSameAdvisorySemantics()
    {
        var directory = NewFixtureDirectory();
        try
        {
            var (plans, targets, _, targetSource, checker, plan, targetId) =
                await FixtureAsync(directory);
            var source = new FakeAllocationSource(targetSource.Target!.WindowsVolume);
            var allocations = new FileMigrationAllocationRegistry(Path.Join(directory, "allocations"),
                targets, source);
            var (loader, providers) = await ProvidersAsync();
            var handler = new MigrationTargetAllocateHandler(plans, targets, allocations,
                source, checker, loader, providers);
            var dispatcher = new CommandDispatcher(checker, loader, providers,
                [handler], plans);
            Assert.Contains(dispatcher.Capabilities, c => c.Id == "migration.target.allocate" &&
                c.Kind == CommandKind.Plan && c.Risk == RiskClass.Low);
            var input = new MigrationAllocationInput(plan.PlanId, targetId, 60 * GiB);
            var request = new CommandRequest(1, "migration.target.allocate", 1,
                Guid.NewGuid(), Guid.NewGuid(),
                JsonSerializer.SerializeToElement(input, CommandProtocol.JsonOptions));
            var direct = await dispatcher.ExecuteAsync(request);
            Assert.True(direct.Succeeded);
            Assert.False(direct.Data!.Value.Deserialize<MigrationAllocationResult>(
                CommandProtocol.JsonOptions)!.Executable);
            using var output = new StringWriter();
            var exit = await CliApplication.RunAsync(["migration", "target", "allocate",
                plan.PlanId.ToString(), targetId.ToString(),
                (60 * GiB).ToString(System.Globalization.CultureInfo.InvariantCulture), "--json"],
                dispatcher, new StringReader(""), output, new StringWriter());
            Assert.Equal(0, exit);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("migration.target.allocate",
                json.RootElement.GetProperty("capabilityId").GetString());
            Assert.False(json.RootElement.GetProperty("data").GetProperty("executable").GetBoolean());
            var wrong = await dispatcher.ExecuteAsync(request with { Input =
                JsonSerializer.SerializeToElement(input with { TargetId = Guid.NewGuid() },
                    CommandProtocol.JsonOptions) });
            Assert.Equal(CommandErrorCode.NotSupported, wrong.Error!.Code);
            checker.Report = checker.Report with { TotalRamBytes = checker.Report.TotalRamBytes + 1 };
            var stale = await dispatcher.ExecuteAsync(request);
            Assert.Equal(CommandErrorCode.StateChanged, stale.Error!.Code);
        }
        finally { DeleteFixture(directory); }
    }

    [Fact]
    public void PublicAllocationDtosContainNoPrivateExecutionOrProductTypes()
    {
        foreach (var dto in new[] { typeof(MigrationAllocationInput),
                     typeof(MigrationAllocationResult), typeof(MigrationTargetBinding) })
        {
            var signature = string.Join(" ", dto.GetProperties().Select(p =>
                p.PropertyType.ToString()));
            foreach (var forbidden in new[] { "MigrationManifest", "PreflightReport",
                         "Igloo.Community", "Igloo.Fleet", "Igloo.Core.Runtime.Execution",
                         "DirectInstallService" })
                Assert.DoesNotContain(forbidden, signature, StringComparison.Ordinal);
        }
    }

    private static async Task<(FileAdvisoryPlanJournal Plans,
        FileMigrationTargetRegistry Targets, FileMigrationAllocationRegistry Allocations,
        FakeTargetSource TargetSource, FakeChecker Checker, MigrationPlanResultV2 Plan,
        Guid TargetId)> FixtureAsync(string directory)
    {
        var (loader, providers) = await ProvidersAsync();
        Assert.True(providers.TryGet("fedora-kde", out var plugin));
        var manifest = loader.LoadedDistros.Single(d => d.Id == "fedora-kde");
        var checker = new FakeChecker();
        var machineRevision = ObservedStateRevision.Fingerprint(checker.Report);
        var providerRevision = await ProviderRevision.FingerprintAsync(plugin, manifest);
        var plan = new MigrationPlanResultV2(Guid.NewGuid(), "fedora-kde", 50 * GiB,
            60 * GiB, MigrationCapacityDisposition.KnownAvailable, [],
            MigrationStateRevision.Bind(machineRevision, providerRevision, "fedora-kde"),
            providerRevision, false, "AdvisoryOnly", machineRevision);
        var plans = new FileAdvisoryPlanJournal(Path.Join(directory, "plans"));
        await plans.SaveAsync(plan, new CommandRequest(1, "migration.plan", 2,
            Guid.NewGuid(), Guid.NewGuid(), JsonSerializer.SerializeToElement(
                new MigrationPlanInput("fedora-kde", 50 * GiB), CommandProtocol.JsonOptions)));
        var targetSource = new FakeTargetSource();
        var targets = new FileMigrationTargetRegistry(Path.Join(directory, "targets"), targetSource);
        var target = await targets.RecordAsync(plan, targetSource.Target!);
        var allocations = new FileMigrationAllocationRegistry(Path.Join(directory, "allocations"),
            targets, new FakeAllocationSource(targetSource.Target!.WindowsVolume));
        return (plans, targets, allocations, targetSource, checker, plan, target.TargetId);
    }

    private static async Task<(DistroLoader Loader, DistroRegistry Providers)> ProvidersAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Igloo.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
        loader.Load(Path.Join(root.FullName, "distros"));
        var providers = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
        await providers.LoadAsync(Path.Join(root.FullName, "distros"));
        return (loader, providers);
    }

    private static string NewFixtureDirectory() => Path.Join(Path.GetTempPath(),
        "igloo-allocation-test-" + Guid.NewGuid().ToString("N"));
    private static void DeleteFixture(string directory)
    {
        if (directory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(directory).StartsWith("igloo-allocation-test-", StringComparison.Ordinal) &&
            Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeTargetSource : ICanonicalMigrationTargetSource
    {
        public CanonicalMigrationTarget? Target { get; set; } = new(new(
            new("PRIVATE_DISK_ID", 2, 7, Guid.NewGuid(), 200UL * (ulong)GiB, 512, 4096),
            Guid.NewGuid(), Guid.NewGuid(),
            new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),
            1UL * (ulong)GiB, 100UL * (ulong)GiB, "NTFS"), 7);
        public CanonicalMigrationTarget? Observe() => Target;
    }

    private sealed class FakeAllocationSource : IAllocationObservationSource
    {
        public CanonicalAllocationObservation? Current { get; set; }
        public FakeAllocationSource(CanonicalVolumeIdentityV1 windows)
        {
            Current = new(windows, [
                new(Guid.NewGuid(), new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b"),
                    1UL * 1024 * 1024, 100UL * 1024 * 1024),
                new(windows.PartitionGuid, windows.PartitionType,
                    windows.OffsetBytes, windows.SizeBytes),
            ], new(40UL * (ulong)GiB, 100UL * (ulong)GiB));
        }
        public CanonicalAllocationObservation? Observe(CanonicalVolumeIdentityV1 target) =>
            Current?.WindowsVolume == target ? Current : null;
    }

    private sealed class FakeChecker : IPreflightChecker
    {
        public PreflightReport Report { get; set; } = new()
        {
            IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, GpuVendor = "unknown",
            TotalRamBytes = 8 * GiB, Disks = [new DiskInfo(@"\\.\PHYSICALDRIVE7",
                "PRIVATE_MODEL", 200 * GiB, 60 * GiB, "GPT", [new PartitionInfo(1,
                    "NTFS", 100 * GiB, null, false, true, 60 * GiB, 1 * GiB)])],
            Findings = [],
        };
        public Task<PreflightReport> RunAsync(CancellationToken ct = default) =>
            Task.FromResult(Report);
    }

    private sealed class FakeStorage(WindowsStorageSnapshot snapshot,
        CanonicalVolumeIdentityV1 target) : IWindowsStorageReader
    {
        public WindowsStorageSnapshot Snapshot { get; set; } = snapshot;
        public WindowsStorageRow Partition { get; set; } = PartitionRow(target);
        public WindowsObservation<WindowsStorageRow> Supported { get; set; } = SupportedRow(target);
        public Observation<WindowsStorageSnapshot> ReadIdentitySnapshot() =>
            Observations.Available(Snapshot);
        public WindowsStorageBatch ReadDisks() => throw new NotSupportedException();
        public WindowsStorageBatch ReadPartitions(int? diskNumber = null,
            int? partitionNumber = null, bool efiOnly = false) => new([Partition]);
        public WindowsStorageBatch ReadVolumes(char driveLetter) => throw new NotSupportedException();
        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition,
            bool explicitParameters = false) => Supported;

        public static WindowsStorageRow PartitionRow(CanonicalVolumeIdentityV1 volume) => new(
            new Dictionary<string, object?>
            {
                ["DiskNumber"] = 7U, ["PartitionNumber"] = 1U,
                ["Guid"] = volume.PartitionGuid.ToString("D"),
                ["GptType"] = volume.PartitionType.ToString("D"),
                ["Offset"] = volume.OffsetBytes, ["Size"] = volume.SizeBytes,
            }.ToImmutableDictionary(StringComparer.Ordinal), "fixture-path");
        public static WindowsObservation<WindowsStorageRow> SupportedRow(
            CanonicalVolumeIdentityV1 volume) => new(new WindowsStorageRow(
                new Dictionary<string, object?>
                {
                    ["ReturnValue"] = 0U,
                    ["SizeMin"] = 40UL * (ulong)GiB,
                    ["SizeMax"] = volume.SizeBytes,
                }.ToImmutableDictionary(StringComparer.Ordinal)));
    }
}
