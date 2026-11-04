using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Cli;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;
using Igloo.Preflight;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Command.Tests;

[Collection("RecoveryState")]
public sealed class ReviewedInstallationPlanningTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    [Theory]
    [InlineData("linuxmint-cinnamon")]
    [InlineData("fedora-kde")]
    public async Task ReviewedMediaRequiresDeclaredSignatureBeforeInspectingPinnedImage(
        string distroId)
    {
        using var fixture = await Fixture.CreateAsync(distroId);
        var declared = fixture.Loader.LoadedDistros.Single(d => d.Id == distroId);
        Assert.NotNull(declared.Iso.GpgSignatureUrl);
        Assert.False(string.IsNullOrWhiteSpace(declared.Iso.Sha256));
        var acquisition = new UnexpectedAcquisition();
        var media = new ReviewedInstallationMedia(fixture.Providers, fixture.Loader,
            acquisition);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            media.AcquireAndInspectAsync(distroId, fixture.VerifiedIso));

        Assert.Contains("signature", failure.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, acquisition.Calls);

        // The signed result clears this gate and reaches the separate pinned
        // media inspection. This fixture intentionally has no ISO bytes.
        var signed = fixture.VerifiedIso with { GpgVerified = true };
        var missingImage = await Assert.ThrowsAsync<InvalidDataException>(() =>
            media.AcquireAndInspectAsync(distroId, signed));
        Assert.Contains("pinned reviewed installer image", missingImage.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DurableAllocationComposesReviewedPlannedCheckpointWithoutStorageWrites()
    {
        using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Planner.PlanAsync("linuxmint-cinnamon", fixture.Plan.PlanId,
            fixture.TargetId, fixture.AllocationId, 50 * GiB,
            fixture.VerifiedIso, fixture.StagingDirectory);

        var reopened = fixture.Checkpoints.Reopen(result.Checkpoint);
        Assert.Equal(PreparationState.Planned, reopened.State);
        Assert.Equal(JsonSerializer.Serialize(result.Plan),
            JsonSerializer.Serialize(reopened.Plan));
        Assert.Empty(reopened.CreationReceipts);
        Assert.Null(reopened.ShrinkReceipt);
        Assert.Null(reopened.StorageOwnership);
        Assert.Equal(ObservationAvailability.Available,
            PreparedLayoutRules.VerifyReviewedIntent(result.Plan).Availability);
        Assert.Equal(fixture.TargetId, result.Plan.ReviewedIntent!.TargetId);
        Assert.Equal(fixture.AllocationId, result.Plan.ReviewedIntent.AllocationId);
        Assert.Equal(fixture.WindowsEsp, result.Plan.WindowsEsp);
        Assert.Equal(60UL * (ulong)GiB, result.Plan.ReviewedIntent.ShrinkBytes);
        Assert.Equal(result.StagedInventorySha256,
            result.Plan.ReviewedIntent.StagedInventorySha256);
        Assert.True(result.Plan.Space.RequiredBytes <= result.Plan.ReviewedIntent.ShrinkBytes);
    }

    [Fact]
    public async Task FedoraOwnedRootReachesOnlyTheReviewedPreparationBoundary()
    {
        using var fixture = await Fixture.CreateAsync("fedora-kde");
        Assert.True(fixture.Providers.TryGet("fedora-kde", out var provider));
        Assert.False(provider.GetInstallerBootSpec().PreCreateRootPartition);
        Assert.True(ReviewedInstallationProviderSupport.ReviewedBootSpec(provider)
            .PreCreateRootPartition);
        var sizing = new ReviewedInstallationSizing(fixture.Media, fixture.Providers);
        var measured = await sizing.MeasureAsync("fedora-kde", 50 * GiB,
            fixture.VerifiedIso, fixture.StagingDirectory);
        Assert.True(measured.MinimumShrinkBytes > 50 * GiB);
        var planned = await fixture.Planner.PlanAsync(
            "fedora-kde", fixture.Plan.PlanId, fixture.TargetId,
            fixture.AllocationId, 50 * GiB, fixture.VerifiedIso,
            fixture.StagingDirectory);
        var root = Assert.Single(planned.Plan.Space.Allocations,
            allocation => allocation.Role == PreparationRole.LinuxRoot);
        Assert.True(root.CreateInWindows);
        Assert.Equal(PreparationState.Planned,
            fixture.Checkpoints.Reopen(planned.Checkpoint).State);
        Assert.Null(fixture.Checkpoints.Reopen(planned.Checkpoint).StorageOwnership);
        Assert.Equal(JsonSerializer.Serialize(planned.Plan), JsonSerializer.Serialize(
            await fixture.Planner.ResolveCurrentPlanAsync(planned.Plan,
                "fedora-kde", 50 * GiB, fixture.VerifiedIso,
                fixture.StagingDirectory, planned.StagedInventorySha256)));

        var handler = new MigrationLayoutPlanHandler(fixture.Planner,
            new FileArtifactRegistry(Path.Join(fixture.DirectoryPath, "artifacts"),
                Path.Join(fixture.DirectoryPath, "cache")), fixture.Loader,
            fixture.Providers, fixture.ManagedStagingRoot);
        Assert.Contains("fedora-kde", handler.Descriptor.SupportedProviders);
        Assert.Equal(CommandKind.Plan, handler.Descriptor.Kind);
        var reviewed = new CapturingReviewedService();
        var host = new ReviewedInstallationPreparation(fixture.Planner, fixture.Media,
            reviewed, fixture.Providers, fixture.Loader);
        var checkpoint = await host.PrepareAsync("fedora-kde", planned,
            50 * GiB, fixture.VerifiedIso, fixture.StagingDirectory);
        Assert.Equal(planned.Checkpoint, checkpoint);
        Assert.NotNull(reviewed.Request);
        Assert.True(reviewed.Request.BootSpec.PreCreateRootPartition);
        Assert.Equal(planned.Plan, reviewed.Request.ExpectedPlan);
        Assert.Equal(PreparationState.Planned,
            fixture.Checkpoints.Reopen(checkpoint).State);
    }

    [Fact]
    public async Task CurrentPlanResolutionRechecksDurableFactsWithoutCreatingAnotherCheckpoint()
    {
        using var fixture = await Fixture.CreateAsync();
        var planned = await fixture.Planner.PlanAsync("linuxmint-cinnamon", fixture.Plan.PlanId,
            fixture.TargetId, fixture.AllocationId, 50 * GiB,
            fixture.VerifiedIso, fixture.StagingDirectory);
        var existingFiles = Directory.GetFiles(fixture.CheckpointDirectory, "*",
            SearchOption.AllDirectories);

        var current = await fixture.Planner.ResolveCurrentPlanAsync(planned.Plan,
            "linuxmint-cinnamon", 50 * GiB, fixture.VerifiedIso,
            fixture.StagingDirectory, planned.StagedInventorySha256);
        Assert.Equal(JsonSerializer.Serialize(planned.Plan), JsonSerializer.Serialize(current));
        Assert.Equal(existingFiles, Directory.GetFiles(fixture.CheckpointDirectory, "*",
            SearchOption.AllDirectories));

        fixture.AllocationSource.Current = fixture.AllocationSource.Current! with
        {
            Partitions = [.. fixture.AllocationSource.Current!.Partitions,
                new AllocationPartitionIdentity(Guid.NewGuid(),
                    PreparationSpacePlanning.BasicDataType, 180UL * (ulong)GiB,
                    1UL * (ulong)GiB)],
        };
        await Assert.ThrowsAsync<StateChangedException>(() =>
            fixture.Planner.ResolveCurrentPlanAsync(planned.Plan, "linuxmint-cinnamon",
                50 * GiB, fixture.VerifiedIso, fixture.StagingDirectory,
                planned.StagedInventorySha256));
        Assert.Equal(existingFiles, Directory.GetFiles(fixture.CheckpointDirectory, "*",
            SearchOption.AllDirectories));

        fixture.AllocationSource.Current = fixture.OriginalAllocation;
        var launcher = Path.Join(fixture.StagingDirectory, "igloo-agent", "first-boot.sh");
        await File.WriteAllTextAsync(launcher, "#!/bin/sh\nexit 1\n");
        await Assert.ThrowsAsync<StateChangedException>(() =>
            fixture.Planner.ResolveCurrentPlanAsync(planned.Plan, "linuxmint-cinnamon",
                50 * GiB, fixture.VerifiedIso, fixture.StagingDirectory,
                planned.StagedInventorySha256));
    }

    [Fact]
    public async Task HostPreparationPassesPinnedPlanThroughTheReviewedPreflightBoundary()
    {
        using var fixture = await Fixture.CreateAsync();
        var planned = await fixture.Planner.PlanAsync("linuxmint-cinnamon", fixture.Plan.PlanId,
            fixture.TargetId, fixture.AllocationId, 50 * GiB,
            fixture.VerifiedIso, fixture.StagingDirectory);
        var reviewed = new CapturingReviewedService();
        var host = new ReviewedInstallationPreparation(fixture.Planner, fixture.Media,
            reviewed, fixture.Providers, fixture.Loader);

        var checkpoint = await host.PrepareAsync("linuxmint-cinnamon", planned,
            50 * GiB, fixture.VerifiedIso, fixture.StagingDirectory);

        Assert.Equal(planned.Checkpoint, checkpoint);
        Assert.NotNull(reviewed.Request);
        Assert.Equal(planned.Plan, reviewed.Request.ExpectedPlan);
        Assert.Equal(planned.Checkpoint, reviewed.Request.PlannedCheckpoint);
        Assert.Equal(planned.StagedBytes, reviewed.Request.ExpectedStagedBytes);
        Assert.Equal(planned.StagedInventorySha256,
            reviewed.Request.ExpectedStagedInventorySha256);
        Assert.Equal(fixture.VerifiedIso.LocalPath, reviewed.Request.PinnedIsoPath);
        Assert.Equal("linuxmint-cinnamon", reviewed.Request.DistroId);
    }

    [Fact]
    public async Task ReviewedShrinkSizingIncludesInstallerVolumesAndAlignmentAllowance()
    {
        using var fixture = await Fixture.CreateAsync();
        var sizing = new ReviewedInstallationSizing(fixture.Media,
            fixture.Providers);
        var measured = await sizing.MeasureAsync("linuxmint-cinnamon", 50 * GiB,
            fixture.VerifiedIso, fixture.StagingDirectory);

        Assert.Equal(checked(measured.Sizing.TotalShrinkBytes +
            (long)PreparationSpacePlanning.LinuxEspBytes +
            (long)PreparationSpacePlanning.Alignment), measured.MinimumShrinkBytes);
        Assert.True(measured.MinimumShrinkBytes > 50 * GiB);
        Assert.Equal(ReviewedMediaInspector.InspectStagedContent(
            fixture.StagingDirectory).InventorySha256,
            measured.StagedInventorySha256);
    }

    private sealed class CapturingReviewedService : IReviewedDirectInstallService
    {
        public ReviewedDirectInstallRequestV1? Request { get; private set; }

        public async Task<PreparationCheckpointReference> PrepareReviewedAsync(
            ReviewedDirectInstallRequestV1 request, IDistroPlugin provider,
            Func<CancellationToken, Task<PreparationPlanV1>> resolveCurrentPlan,
            IProgress<DirectInstallProgress>? progress = null,
            CancellationToken ct = default)
        {
            Request = request;
            Assert.Equal(request.DistroId, provider.Id);
            Assert.Equal(JsonSerializer.Serialize(request.ExpectedPlan),
                JsonSerializer.Serialize(await resolveCurrentPlan(ct)));
            return request.PlannedCheckpoint;
        }
    }

    [Fact]
    public async Task ChangedLayoutOrAmbiguousSystemEspDoesNotPersistCheckpoint()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.AllocationSource.Current = fixture.AllocationSource.Current! with
        {
            Partitions = [.. fixture.AllocationSource.Current!.Partitions,
                new AllocationPartitionIdentity(Guid.NewGuid(),
                    PreparationSpacePlanning.BasicDataType, 180UL * (ulong)GiB,
                    1UL * (ulong)GiB)],
        };
        await Assert.ThrowsAsync<StateChangedException>(() => fixture.Planner.PlanAsync(
            "linuxmint-cinnamon", fixture.Plan.PlanId, fixture.TargetId, fixture.AllocationId,
            50 * GiB, fixture.VerifiedIso, fixture.StagingDirectory));
        Assert.False(Directory.Exists(fixture.CheckpointDirectory));

        fixture.AllocationSource.Current = fixture.OriginalAllocation;
        fixture.Storage.Snapshot = fixture.Storage.Snapshot with
        {
            Partitions = fixture.Storage.Snapshot.Partitions.Select(p =>
                p.PartitionGuid.Value == fixture.WindowsEsp.PartitionGuid
                    ? p with { IsSystem = Observations.Available(false) } : p).ToImmutableArray(),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Planner.PlanAsync(
            "linuxmint-cinnamon", fixture.Plan.PlanId, fixture.TargetId, fixture.AllocationId,
            50 * GiB, fixture.VerifiedIso, fixture.StagingDirectory));
        Assert.False(Directory.Exists(fixture.CheckpointDirectory));
    }

    [Fact]
    public async Task MeasuredPayloadBeyondReviewedShrinkIsRejectedBeforeCheckpoint()
    {
        using var fixture = await Fixture.CreateAsync();
        // A measured 20 GiB stage2 plus the 50 GiB root cannot fit the
        // fixture's 60 GiB shrink, regardless of available host disk space.
        fixture.Media.Result = fixture.Media.Result with { Measurements =
            fixture.Media.Result.Measurements with { Stage2Bytes = 20UL * (ulong)GiB } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Planner.PlanAsync(
            "linuxmint-cinnamon", fixture.Plan.PlanId, fixture.TargetId, fixture.AllocationId,
            50 * GiB, fixture.VerifiedIso, fixture.StagingDirectory));
        Assert.False(Directory.Exists(fixture.CheckpointDirectory));
    }

    [Theory]
    [InlineData("linuxmint-cinnamon", false)]
    [InlineData("fedora-kde", true)]
    public async Task CliLayoutPlanRehashesRegisteredArtifactAndReturnsOnlyAdvisorySummary(
        string distroId, bool rootCreatedInWindows)
    {
        using var fixture = await Fixture.CreateAsync(distroId);
        var cache = Path.Join(fixture.DirectoryPath, "cache");
        Directory.CreateDirectory(cache);
        var isoPath = Path.Join(cache, "fixture.iso");
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await File.WriteAllBytesAsync(isoPath, bytes);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var artifacts = new FileArtifactRegistry(Path.Join(fixture.DirectoryPath,
            "artifacts"), cache);
        var acquired = await artifacts.RecordAsync(distroId,
            new IsoAcquisitionResult(isoPath, true, false, bytes.Length, digest));
        fixture.Media.Result = fixture.Media.Result with
        {
            IsoPath = isoPath, IsoSha256 = digest, IsoBytes = bytes.Length,
            Measurements = fixture.Media.Result.Measurements with
            { FullIsoBytes = (ulong)bytes.Length },
        };
        var handler = new MigrationLayoutPlanHandler(fixture.Planner, artifacts,
            fixture.Loader, fixture.Providers, fixture.ManagedStagingRoot);
        var dispatcher = new CommandDispatcher(fixture.Checker, fixture.Loader,
            fixture.Providers, [handler]);
        Assert.Contains(dispatcher.Capabilities, c => c.Id == "migration.layout.plan" &&
            c.Kind == CommandKind.Plan && c.Risk == RiskClass.Low);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["migration", "layout", "plan",
            distroId, fixture.Plan.PlanId.ToString(),
            fixture.TargetId.ToString(), fixture.AllocationId.ToString(),
            acquired.ArtifactId, (50 * GiB).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            fixture.StagingDirectory, "--json"], dispatcher,
            new StringReader(""), output, error);
        Assert.True(exit == 0,
            $"CLI returned {exit}; result: {output}; error: {error}");
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("migration.layout.plan", document.RootElement
            .GetProperty("capabilityId").GetString());
        Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("Planned", data.GetProperty("state").GetString());
        Assert.False(data.GetProperty("executable").GetBoolean());
        Assert.Equal("AdvisoryOnly", data.GetProperty("disposition").GetString());
        var root = data.GetProperty("extents").EnumerateArray().Single(extent =>
            extent.GetProperty("role").GetString() == "LinuxRoot");
        Assert.Equal(rootCreatedInWindows,
            root.GetProperty("createInWindows").GetBoolean());
        Assert.False(data.TryGetProperty("checkpoint", out _));
        Assert.True(Directory.Exists(fixture.CheckpointDirectory));

        using var help = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["--help"], dispatcher,
            new StringReader(""), help, error));
        Assert.Contains("migration layout plan", help.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("advisory", help.ToString(), StringComparison.OrdinalIgnoreCase);
        using var separatedHelp = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["--", "help"], dispatcher,
            new StringReader(""), separatedHelp, error));
        Assert.Equal(help.ToString(), separatedHelp.ToString());
        Assert.Contains("iso acquire", separatedHelp.ToString(), StringComparison.Ordinal);

        await File.WriteAllBytesAsync(isoPath, [9, 9, 9, 9, 9, 9, 9, 9]);
        await Assert.ThrowsAsync<ArtifactVerificationException>(() =>
            artifacts.ResolveVerifiedAsync(acquired.ArtifactId,
                distroId));
    }

    private sealed class Fixture : IDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string StagingDirectory { get; init; }
        public required string ManagedStagingRoot { get; init; }
        public required string CheckpointDirectory { get; init; }
        public required MigrationPlanResultV2 Plan { get; init; }
        public required Guid TargetId { get; init; }
        public required Guid AllocationId { get; init; }
        public required CanonicalVolumeIdentityV1 WindowsEsp { get; init; }
        public required IsoAcquisitionResult VerifiedIso { get; init; }
        public required FakeMedia Media { get; init; }
        public required CanonicalAllocationObservation OriginalAllocation { get; init; }
        public required FakeAllocationSource AllocationSource { get; init; }
        public required FakeStorage Storage { get; init; }
        public required PreparedLayoutStore Checkpoints { get; init; }
        public required ReviewedInstallationPlanning Planner { get; init; }
        public required DistroLoader Loader { get; init; }
        public required DistroRegistry Providers { get; init; }
        public required FakeChecker Checker { get; init; }

        public static async Task<Fixture> CreateAsync(string distroId = "linuxmint-cinnamon")
        {
            var directory = Path.Join(Path.GetTempPath(),
                "igloo-reviewed-plan-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var managedStagingRoot = Path.Join(directory, "managed-staging");
            var staging = Path.Join(managedStagingRoot, distroId,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Join(staging, "igloo-agent"));
            await File.WriteAllTextAsync(Path.Join(staging, "igloo-agent", "first-boot.sh"),
                "#!/bin/sh\nexit 0\n");
            var disk = new CanonicalDiskIdentityV1("PRIVATE_DISK_ID", 2, 7,
                Guid.NewGuid(), 200UL * (ulong)GiB, 512, 4096);
            var esp = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(),
                PreparationSpacePlanning.EspType, 1UL * (ulong)MiB,
                512UL * (ulong)MiB, "FAT32");
            var windows = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(),
                PreparationSpacePlanning.BasicDataType, 1UL * (ulong)GiB,
                100UL * (ulong)GiB, "NTFS");
            var manifest = new MigrationManifest
            {
                DistroId = distroId,
                User = new MigrationUser { WindowsUsername = "fixture",
                    PreferredLinuxUsername = "fixture" },
                Files = new FileMigrationPlan { StagingPath = staging },
                Hardware = new HardwareProfile { TargetDiskGuid = disk.GptDiskGuid,
                    TargetDiskBytes = 200 * GiB, InstallMode = "dual-boot",
                    LinuxPartitionSizeGb = 50 },
            };
            await File.WriteAllTextAsync(Path.Join(staging, "migration-manifest.json"),
                JsonSerializer.Serialize(manifest));

            var distroRoot = new DirectoryInfo(AppContext.BaseDirectory);
            while (distroRoot is not null && !File.Exists(Path.Join(distroRoot.FullName,
                       "Igloo.sln"))) distroRoot = distroRoot.Parent;
            Assert.NotNull(distroRoot);
            var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
            loader.Load(Path.Join(distroRoot.FullName, "distros"));
            var providers = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
            await providers.LoadAsync(Path.Join(distroRoot.FullName, "distros"));
            Assert.True(providers.TryGet(distroId, out var plugin));
            var declared = loader.LoadedDistros.Single(d => d.Id == distroId);
            var revision = await ProviderRevision.FingerprintAsync(plugin, declared);
            var digest = plugin.Metadata.IsoSha256!.ToUpperInvariant();
            var checker = new FakeChecker();
            var machineRevision = ObservedStateRevision.Fingerprint(checker.Report);
            var plan = new MigrationPlanResultV2(Guid.NewGuid(), distroId,
                50 * GiB, 60 * GiB, MigrationCapacityDisposition.KnownAvailable, [],
                MigrationStateRevision.Bind(machineRevision, revision, distroId),
                revision, false, "AdvisoryOnly", machineRevision);
            var journal = new FileAdvisoryPlanJournal(Path.Join(directory, "plans"));
            await journal.SaveAsync(plan, new CommandRequest(1, "migration.plan", 2,
                Guid.NewGuid(), Guid.NewGuid(), JsonSerializer.SerializeToElement(
                    new MigrationPlanInput(distroId, 50 * GiB),
                    CommandProtocol.JsonOptions)));
            var targetSource = new FakeTargetSource(new(windows, 7));
            var targets = new FileMigrationTargetRegistry(Path.Join(directory, "targets"),
                targetSource);
            var targetId = (await targets.RecordAsync(plan, targetSource.Target)).TargetId;
            var allocation = new CanonicalAllocationObservation(windows,
                [new(esp.PartitionGuid, esp.PartitionType, esp.OffsetBytes, esp.SizeBytes),
                 new(windows.PartitionGuid, windows.PartitionType, windows.OffsetBytes,
                     windows.SizeBytes)], new(40UL * (ulong)GiB,
                    100UL * (ulong)GiB));
            var allocationSource = new FakeAllocationSource(allocation);
            var allocations = new FileMigrationAllocationRegistry(
                Path.Join(directory, "allocations"), targets, allocationSource);
            var allocationId = (await allocations.RecordAsync(plan.PlanId, targetId,
                plan.ObservedStateFingerprint, allocation, 60 * GiB)).AllocationId;
            var storage = new FakeStorage(Snapshot(disk, esp, windows));
            var checkpointDirectory = Path.Join(directory, "checkpoints");
            var checkpoints = new PreparedLayoutStore(
                new CommunityRecoveryArtifactStore(checkpointDirectory));
            var media = new FakeMedia(new(distroId, Path.Join(directory, "iso.fixture"),
                digest, 1 * GiB, null, null,
                new ReviewedMediaMeasurementsV1((ulong)GiB, 30UL * (ulong)MiB,
                    100UL * (ulong)MiB, 500UL * (ulong)MiB), revision));
            var planner = new ReviewedInstallationPlanning(journal, targets,
                allocations, allocationSource, storage, checker, providers, loader,
                media, checkpoints);
            return new Fixture { DirectoryPath = directory, StagingDirectory = staging,
                ManagedStagingRoot = managedStagingRoot,
                CheckpointDirectory = checkpointDirectory, Plan = plan,
                TargetId = targetId, AllocationId = allocationId, WindowsEsp = esp,
                VerifiedIso = new IsoAcquisitionResult(media.Result.IsoPath, true,
                    false, media.Result.IsoBytes, digest), OriginalAllocation = allocation,
                Media = media, AllocationSource = allocationSource, Storage = storage,
                Checkpoints = checkpoints, Planner = planner, Loader = loader,
                Providers = providers, Checker = checker };
        }

        public void Dispose()
        {
            if (DirectoryPath.StartsWith(Path.GetTempPath(),
                    StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(DirectoryPath).StartsWith("igloo-reviewed-plan-test-",
                    StringComparison.Ordinal) && Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }

        private static WindowsStorageSnapshot Snapshot(CanonicalDiskIdentityV1 disk,
            CanonicalVolumeIdentityV1 esp, CanonicalVolumeIdentityV1 windows)
        {
            var row = new DiskObservation(Observations.Available(7U),
                Observations.Available(disk.UniqueId),
                Observations.Available(disk.UniqueIdFormat),
                Observations.Available("PRIVATE_SERIAL"),
                Observations.Available(disk.BusType),
                Observations.Available("PRIVATE_MODEL"),
                Observations.Available(disk.SizeBytes), Observations.Available(2U),
                Observations.Available(disk.GptDiskGuid),
                Observations.Available(disk.LogicalSectorSize),
                Observations.Available(disk.PhysicalSectorSize));
            static PartitionObservation Part(CanonicalVolumeIdentityV1 value, uint number,
                bool system, bool boot) => new(Observations.Available(7U),
                    Observations.Available(number),
                    Observations.Available(value.PartitionGuid),
                    Observations.Available(value.PartitionType),
                    Observations.Available(value.OffsetBytes),
                    Observations.Available(value.SizeBytes),
                    Observations.Available(system), Observations.Available(boot),
                    Observations.Available(false), Observations.Available(' '),
                    Observations.Available(ImmutableArray<string>.Empty));
            static VolumeObservation Volume(CanonicalVolumeIdentityV1 value) => new(
                Observations.Available(value.VolumeGuid),
                Observations.Available(value.PartitionGuid),
                Observations.Available(value.FileSystem), Observations.Available(""),
                Observations.Available(' '), Observations.Available("Healthy"),
                Observations.Available(false));
            return new([row], [Part(esp, 1, true, false),
                Part(windows, 2, false, true)], [Volume(esp), Volume(windows)]);
        }
    }

    private sealed class FakeTargetSource(CanonicalMigrationTarget target)
        : ICanonicalMigrationTargetSource
    {
        public CanonicalMigrationTarget Target { get; } = target;
        public CanonicalMigrationTarget? Observe() => Target;
    }

    private sealed class FakeAllocationSource(CanonicalAllocationObservation current)
        : IAllocationObservationSource
    {
        public CanonicalAllocationObservation? Current { get; set; } = current;
        public CanonicalAllocationObservation? Observe(CanonicalVolumeIdentityV1 target) =>
            Current?.WindowsVolume == target ? Current : null;
    }

    private sealed class FakeStorage(WindowsStorageSnapshot snapshot) : IWindowsStorageReader
    {
        public WindowsStorageSnapshot Snapshot { get; set; } = snapshot;
        public Observation<WindowsStorageSnapshot> ReadIdentitySnapshot() =>
            Observations.Available(Snapshot);
        public WindowsStorageBatch ReadDisks() => throw new NotSupportedException();
        public WindowsStorageBatch ReadPartitions(int? diskNumber = null,
            int? partitionNumber = null, bool efiOnly = false) =>
            throw new NotSupportedException();
        public WindowsStorageBatch ReadVolumes(char driveLetter) =>
            throw new NotSupportedException();
        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(
            WindowsStorageRow partition, bool explicitParameters = false) =>
            throw new NotSupportedException();
    }

    private sealed class FakeChecker : IPreflightChecker
    {
        public PreflightReport Report { get; } = new()
        {
            IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, GpuVendor = "unknown",
            TotalRamBytes = 8 * GiB,
            Disks = [new DiskInfo(@"\\.\PHYSICALDRIVE7", "PRIVATE_MODEL",
                200 * GiB, 60 * GiB, "GPT", [new PartitionInfo(2, "NTFS",
                    100 * GiB, null, false, true, 60 * GiB, 1 * GiB)])],
            Findings = [],
        };
        public Task<PreflightReport> RunAsync(CancellationToken ct = default) =>
            Task.FromResult(Report);
    }

    private sealed class FakeMedia(ReviewedInstallationMediaV1 result)
        : IReviewedInstallationMedia
    {
        public ReviewedInstallationMediaV1 Result { get; set; } = result;
        public Task<ReviewedInstallationMediaV1> AcquireAndInspectAsync(string distroId,
            IsoAcquisitionResult verifiedIso, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    private sealed class UnexpectedAcquisition : IIsoAcquisitionService
    {
        public int Calls { get; private set; }
        public Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification spec,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("No acquisition is expected before the signature check.");
        }
    }
}
