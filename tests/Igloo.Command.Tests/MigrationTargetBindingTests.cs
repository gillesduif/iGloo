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

public sealed class MigrationTargetBindingTests
{
    [Fact]
    public void WindowsSourceRequiresProviderUniqueBootVolumeIdentity()
    {
        var diskGuid = Guid.NewGuid();
        var partitionGuid = Guid.NewGuid();
        var volumeGuid = Guid.NewGuid();
        var disk = new DiskObservation(Observations.Available(7U),
            Observations.Available("PRIVATE_UNIQUE_ID"), Observations.Available(2U),
            Observations.Available("PRIVATE_SERIAL"), Observations.Available(7U),
            Observations.Available("PRIVATE_MODEL"), Observations.Available(200UL << 30),
            Observations.Available(2U), Observations.Available(diskGuid),
            Observations.Available(512U), Observations.Available(4096U));
        var partition = new PartitionObservation(Observations.Available(7U),
            Observations.Available(1U), Observations.Available(partitionGuid),
            Observations.Available(new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")),
            Observations.Available(1UL << 30), Observations.Available(100UL << 30),
            Observations.Available(false), Observations.Available(true),
            Observations.Available(false), Observations.Available('C'),
            Observations.Available(ImmutableArray.Create("C:\\")));
        var volume = new VolumeObservation(Observations.Available(volumeGuid),
            Observations.Available(partitionGuid), Observations.Available("NTFS"),
            Observations.Available("PRIVATE_LABEL"), Observations.Available('C'),
            Observations.Available("Healthy"), Observations.Available(false));
        var storage = new FakeStorage(new([disk], [partition], [volume]));
        var source = new WindowsCanonicalMigrationTargetSource(storage,
            () => Observations.Available(volumeGuid));
        Assert.Equal(diskGuid, source.Observe()!.WindowsVolume.Disk.GptDiskGuid);
        storage.Snapshot = storage.Snapshot with { Disks =
            [disk with { UniqueIdFormat = Observations.Available(1U) }] };
        Assert.Null(source.Observe());
        storage.Snapshot = storage.Snapshot with { Disks = [disk], Partitions =
            [partition with { IsBoot = Observations.Available(false) }] };
        Assert.Null(source.Observe());
        storage.Snapshot = storage.Snapshot with { Partitions = [partition],
            Disks = [disk, disk with { Number = Observations.Available(8U) }] };
        Assert.Null(source.Observe());
    }

    [Fact]
    public async Task TargetIdPersistsOnlyCanonicalHashAndRejectsReplacementOrWrongPlan()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-target-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (dispatcher, registry, source, checker, plan) = await FixtureAsync(directory);
            Assert.Contains(dispatcher.Capabilities, capability =>
                capability.Id == "migration.target.bind" && capability.Kind == CommandKind.Plan);
            var result = await dispatcher.ExecuteAsync(Request(plan.PlanId));
            Assert.True(result.Succeeded);
            var binding = result.Data!.Value.Deserialize<MigrationTargetBinding>(CommandProtocol.JsonOptions)!;
            Assert.Equal(plan.PlanId, binding.PlanId);
            Assert.NotEqual(Guid.Empty, binding.TargetId);
            Assert.False(binding.Executable);
            using var output = new StringWriter();
            Assert.Equal(0, await CliApplication.RunAsync(["migration", "target", "bind",
                plan.PlanId.ToString(), "--json"], dispatcher, new StringReader(""), output,
                new StringWriter()));
            using var cliResult = JsonDocument.Parse(output.ToString());
            Assert.False(cliResult.RootElement.GetProperty("data").GetProperty("executable").GetBoolean());
            var record = await File.ReadAllTextAsync(Path.Join(directory, "targets",
                binding.TargetId.ToString("N") + ".json"));
            Assert.DoesNotContain("PRIVATE_UNIQUE_ID", record, StringComparison.Ordinal);
            Assert.DoesNotContain(source.Target!.WindowsVolume.PartitionGuid.ToString("D"), record,
                StringComparison.OrdinalIgnoreCase);

            var reopened = new FileMigrationTargetRegistry(Path.Join(directory, "targets"), source);
            Assert.Equal(source.Target.WindowsVolume,
                await reopened.ResolveAsync(binding.TargetId, plan.PlanId,
                    plan.ObservedStateFingerprint));
            source.Target = source.Target with { ObservedDiskNumber = 9 };
            Assert.Equal(source.Target.WindowsVolume,
                await reopened.ResolveAsync(binding.TargetId, plan.PlanId,
                    plan.ObservedStateFingerprint));
            source.Target = source.Target with { ObservedDiskNumber = 7 };
            await Assert.ThrowsAsync<StateChangedException>(() => reopened.ResolveAsync(
                binding.TargetId, Guid.NewGuid(), plan.ObservedStateFingerprint));
            await Assert.ThrowsAsync<StateChangedException>(() => reopened.ResolveAsync(
                binding.TargetId, plan.PlanId, new string('A', 64)));

            source.Target = source.Target with
            { WindowsVolume = source.Target.WindowsVolume with
                { Disk = source.Target.WindowsVolume.Disk with { GptDiskGuid = Guid.NewGuid() } } };
            await Assert.ThrowsAsync<StateChangedException>(() => reopened.ResolveAsync(
                binding.TargetId, plan.PlanId, plan.ObservedStateFingerprint));

            var issuedCount = Directory.GetFiles(Path.Join(directory, "targets"), "*.json").Length;
            checker.Report = checker.Report with { TotalRamBytes = checker.Report.TotalRamBytes + 1 };
            var stale = await dispatcher.ExecuteAsync(Request(plan.PlanId));
            Assert.Equal(CommandErrorCode.StateChanged, stale.Error!.Code);
            Assert.Equal(issuedCount, Directory.GetFiles(Path.Join(directory, "targets"), "*.json").Length);
        }
        finally { DeleteFixture(directory); }
    }

    [Fact]
    public async Task MissingExactTargetAndBlockedPlanNeverIssueTargetId()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-target-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (dispatcher, registry, source, _, plan) = await FixtureAsync(directory);
            source.Target = null;
            var missing = await dispatcher.ExecuteAsync(Request(plan.PlanId));
            Assert.Equal(CommandErrorCode.PreconditionFailed, missing.Error!.Code);
            Assert.False(Directory.Exists(Path.Join(directory, "targets")));

            var blocked = plan with { PlanId = Guid.NewGuid(), BlockerCodes = ["CapacityUnknown"] };
            await new FileAdvisoryPlanJournal(Path.Join(directory, "plans")).SaveAsync(blocked,
                Request(blocked.PlanId));
            var refused = await dispatcher.ExecuteAsync(Request(blocked.PlanId));
            Assert.Equal(CommandErrorCode.PreconditionFailed, refused.Error!.Code);
        }
        finally { DeleteFixture(directory); }
    }

    [Fact]
    public async Task CorruptTargetRecordCannotBeResolved()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-target-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (dispatcher, registry, source, _, plan) = await FixtureAsync(directory);
            var issued = await dispatcher.ExecuteAsync(Request(plan.PlanId));
            var binding = issued.Data!.Value.Deserialize<MigrationTargetBinding>(CommandProtocol.JsonOptions)!;
            var path = Path.Join(directory, "targets", binding.TargetId.ToString("N") + ".json");
            var record = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            record["canonicalIdentitySha256"] = new string('A', 64);
            await File.WriteAllTextAsync(path, record.ToJsonString());
            await Assert.ThrowsAsync<CommandEvidenceException>(() => registry.ResolveAsync(
                binding.TargetId, plan.PlanId, plan.ObservedStateFingerprint));
        }
        finally { DeleteFixture(directory); }
    }

    [Fact]
    public async Task ProductSpecificTargetBindingInputIsRejected()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-target-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (dispatcher, _, _, _, plan) = await FixtureAsync(directory);
            foreach (var product in new[] { "community", "one", "fleet" })
            {
                var input = JsonSerializer.SerializeToElement(new
                {
                    planId = plan.PlanId,
                    client = product,
                }, CommandProtocol.JsonOptions);
                var request = Request(plan.PlanId) with { Input = input };
                var result = await dispatcher.ExecuteAsync(request);
                Assert.Equal(CommandErrorCode.InvalidRequest, result.Error!.Code);
            }
            Assert.False(Directory.Exists(Path.Join(directory, "targets")));
        }
        finally { DeleteFixture(directory); }
    }

    private static async Task<(CommandDispatcher Dispatcher, FileMigrationTargetRegistry Registry,
        FakeSource Source, FakeChecker Checker, MigrationPlanResultV2 Plan)> FixtureAsync(string directory)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Igloo.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
        loader.Load(Path.Join(root.FullName, "distros"));
        var providers = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
        await providers.LoadAsync(Path.Join(root.FullName, "distros"));
        Assert.True(providers.TryGet("fedora-kde", out var plugin));
        var manifest = loader.LoadedDistros.Single(d => d.Id == "fedora-kde");
        var checker = new FakeChecker();
        var machineRevision = ObservedStateRevision.Fingerprint(checker.Report);
        var providerRevision = await ProviderRevision.FingerprintAsync(plugin, manifest);
        var plan = new MigrationPlanResultV2(Guid.NewGuid(), "fedora-kde", 50L << 30,
            100L << 30, MigrationCapacityDisposition.KnownAvailable, [],
            MigrationStateRevision.Bind(machineRevision, providerRevision, "fedora-kde"),
            providerRevision, false, "AdvisoryOnly", machineRevision);
        var plans = new FileAdvisoryPlanJournal(Path.Join(directory, "plans"));
        await plans.SaveAsync(plan, Request(plan.PlanId));
        var source = new FakeSource();
        var targets = new FileMigrationTargetRegistry(Path.Join(directory, "targets"), source);
        var handler = new MigrationTargetBindHandler(plans, checker, loader, providers,
            source, targets);
        return (new CommandDispatcher(checker, loader, providers, [handler], plans),
            targets, source, checker, plan);
    }

    private static CommandRequest Request(Guid planId) => new(CommandProtocol.Version,
        "migration.target.bind", 1, Guid.NewGuid(), Guid.NewGuid(),
        JsonSerializer.SerializeToElement(new PlanSelection(planId), CommandProtocol.JsonOptions));

    private static void DeleteFixture(string directory)
    {
        if (directory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(directory).StartsWith("igloo-target-test-", StringComparison.Ordinal) &&
            Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeSource : ICanonicalMigrationTargetSource
    {
        public CanonicalMigrationTarget? Target { get; set; } = new(new(
            new("PRIVATE_UNIQUE_ID", 2, 7, Guid.NewGuid(), 200UL << 30, 512, 4096),
            Guid.NewGuid(), Guid.NewGuid(),
            new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),
            1UL << 30, 100UL << 30, "NTFS"), 7);
        public CanonicalMigrationTarget? Observe() => Target;
    }

    private sealed class FakeChecker : IPreflightChecker
    {
        public PreflightReport Report { get; set; } = new()
        {
            IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, TotalRamBytes = 8L << 30,
            GpuVendor = "unknown", Findings = [],
            Disks = [new DiskInfo(@"\\.\PHYSICALDRIVE7", "PRIVATE_MODEL",
                200L << 30, 100L << 30, "GPT", [new PartitionInfo(1, "NTFS",
                    100L << 30, null, false, true, 100L << 30, 1L << 30)])],
        };
        public Task<PreflightReport> RunAsync(CancellationToken ct = default) =>
            Task.FromResult(Report);
    }

    private sealed class FakeStorage(WindowsStorageSnapshot snapshot) : IWindowsStorageReader
    {
        public WindowsStorageSnapshot Snapshot { get; set; } = snapshot;
        public Observation<WindowsStorageSnapshot> ReadIdentitySnapshot() =>
            Observations.Available(Snapshot);
        public WindowsStorageBatch ReadDisks() => throw new NotSupportedException();
        public WindowsStorageBatch ReadPartitions(int? diskNumber = null,
            int? partitionNumber = null, bool efiOnly = false) => throw new NotSupportedException();
        public WindowsStorageBatch ReadVolumes(char driveLetter) => throw new NotSupportedException();
        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition,
            bool explicitParameters = false) => throw new NotSupportedException();
    }
}
