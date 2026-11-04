using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Core.Runtime;
using Igloo.Preflight;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Igloo.Command.Tests;

[Collection("RecoveryState")]
public sealed class ReviewedOwnedInspectionDispatchTests
{
    private const ulong MiB = PreparationSpacePlanning.Alignment;

    [Fact]
    public async Task FedoraFirstPartyInspectionDispatchesOnlyTheOwnedRootObserver()
    {
        var directory = Path.Join(Path.GetTempPath(),
            "igloo-fedora-reviewed-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var observer = new OwnedRootObserver();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalBackend();
            services.Replace(ServiceDescriptor.Singleton(new PreparedLayoutStore(
                new CommunityRecoveryArtifactStore(directory))));
            services.Replace(ServiceDescriptor.Singleton<IReviewedInstallerInspection>(observer));
            using var host = services.BuildServiceProvider();
            var distros = FindRepoRoot();
            var loader = host.GetRequiredService<DistroLoader>();
            loader.Load(Path.Join(distros, "distros"));
            var registry = host.GetRequiredService<DistroRegistry>();
            await registry.LoadAsync(Path.Join(distros, "distros"));
            Assert.True(registry.TryGet("fedora-kde", out var plugin));
            Assert.IsAssignableFrom<IReviewedInstallerStorageProvider>(plugin);
            var declared = Assert.Single(loader.LoadedDistros.Where(d => d.Id == "fedora-kde"));
            var revision = await ProviderRevision.FingerprintAsync(plugin, declared);
            var pin = Assert.IsType<string>(plugin.Metadata.IsoSha256);
            var plan = OwnedPlan(pin, revision);
            Assert.Equal(ObservationAvailability.Available,
                PreparedLayoutRules.VerifyReviewedIntent(plan).Availability);
            var checkpoint = CreatedCheckpoint(plan);
            var reference = host.GetRequiredService<PreparedLayoutStore>()
                .PersistAndReopen(checkpoint);
            var manifest = new MigrationManifest
            {
                DistroId = "fedora-kde",
                User = new MigrationUser { WindowsUsername = "fixture",
                    PreferredLinuxUsername = "fixture", LinuxPasswordCrypted = "$6$fixture$hash" },
                Files = new FileMigrationPlan { StagingPath = "files" },
                Hardware = new HardwareProfile { InstallMode = "dual-boot",
                    TargetDiskGuid = plan.TargetDisk.GptDiskGuid,
                    TargetDiskBytes = checked((long)plan.TargetDisk.SizeBytes),
                    LinuxPartitionSizeGb = 24 },
            };
            await File.WriteAllBytesAsync(Path.Join(directory, "migration-manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(manifest));
            var acquired = new IsoAcquisitionResult(Path.Join(directory, "synthetic.iso"),
                true, true, 1024, pin);

            var reached = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.GetRequiredService<IReviewedInstallationInspection>().InspectAsync(
                    "fedora-kde", reference, 24L << 30, acquired, directory));
            Assert.Equal("Owned root observer reached.", reached.Message);
            Assert.Equal(1, observer.OwnedCalls);
            Assert.Equal(0, observer.FreeExtentCalls);
        }
        finally
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                Path.GetFullPath(Path.GetDirectoryName(directory)!));
            Assert.StartsWith("igloo-fedora-reviewed-dispatch-", Path.GetFileName(directory),
                StringComparison.Ordinal);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PreparationPlanV1 OwnedPlan(string artifactPin, string providerRevision)
    {
        var disk = new CanonicalDiskIdentityV1("eui.synthetic-fedora", 8, 17,
            Guid.NewGuid(), 100000 * MiB, 512, 4096);
        var windowsEsp = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(),
            PreparationSpacePlanning.EspType, MiB, 260 * MiB, "FAT32");
        var source = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(),
            PreparationSpacePlanning.BasicDataType, 261 * MiB, 59739 * MiB, "NTFS");
        var space = PreparationSpacePlanning.Plan(new(20000 * MiB, 40000 * MiB,
            24UL << 30, 1000 * MiB, 0, 4096, true)).Value;
        return new PreparationPlanV1(1, Guid.NewGuid(), disk, windowsEsp,
            [windowsEsp.PartitionGuid, source.PartitionGuid], space)
        {
            ReviewedIntent = new ReviewedPreparationIntentV1(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), artifactPin, providerRevision,
                new(1024 * MiB, 100 * MiB, 100 * MiB, 100 * MiB), source,
                [PreparedStorageOwnership.PartitionOf(windowsEsp),
                 PreparedStorageOwnership.PartitionOf(source)],
                40000 * MiB, 19739 * MiB, 20000 * MiB, 40000 * MiB),
        };
    }

    private static PreparationCheckpointV1 CreatedCheckpoint(PreparationPlanV1 plan)
    {
        var after = plan.ReviewedIntent!.ShrinkSource with
        { SizeBytes = plan.ReviewedIntent.ResultingSourceBytes };
        var shrink = PreparedLayoutRules.VerifyShrink(plan, Observations.Available(after),
            Observations.Available(after)).Value;
        var created = plan.Space.Allocations.Select(allocation =>
        {
            var partition = new PreparedGptPartitionV1(plan.TargetDisk, Guid.NewGuid(),
                allocation.PartitionType, allocation.OffsetBytes, allocation.SizeBytes);
            var ownership = PreparedStorageOwnership.VerifyCreation(plan, allocation.Role,
                Observations.Available(partition), Observations.Available(partition)).Value;
            return ownership;
        }).ToArray();
        // Each formatted receipt must describe the same independently identified volume.
        var formattedReceipts = plan.Space.Allocations.Where(a => a.FileSystem.Length != 0)
            .Select(a =>
            {
                var identity = created.Single(c => c.Role == a.Role).Identity;
                var volume = new CanonicalVolumeIdentityV1(plan.TargetDisk,
                    identity.PartitionGuid, Guid.NewGuid(), a.PartitionType,
                    a.OffsetBytes, a.SizeBytes, a.FileSystem);
                return PreparedLayoutRules.VerifyCreation(plan, a.Role,
                    Observations.Available(volume), Observations.Available(volume)).Value;
            }).ToImmutableArray();
        return new PreparationCheckpointV1(1, PreparationState.CreatedAndVerified,
            plan, formattedReceipts, null)
        {
            ShrinkReceipt = shrink,
            StorageOwnership = new PreparedStorageOwnershipV1(plan.GenerationId,
                [PreparedStorageOwnership.PartitionOf(plan.WindowsEsp),
                 PreparedStorageOwnership.PartitionOf(after)],
                created.ToImmutableArray()),
        };
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Join(directory.FullName, "Igloo.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The iGloo provider fixture is unavailable.");
    }

    private sealed class OwnedRootObserver : IReviewedInstallerInspection
    {
        public int FreeExtentCalls { get; private set; }
        public int OwnedCalls { get; private set; }

        public ReviewedInstallerInspectionV1 Inspect(PreparationCheckpointReference checkpoint,
            InstallerBootSpec bootSpec, long requestedLinuxBytes, string stagingDirectory,
            string pinnedIsoPath, long pinnedIsoBytes, CancellationToken cancellationToken = default)
        {
            FreeExtentCalls++;
            throw new Xunit.Sdk.XunitException("Fedora reached the installer-created root observer.");
        }

        public ReviewedOwnedInstallerInspectionV1 InspectOwned(PreparationCheckpointReference checkpoint,
            InstallerBootSpec bootSpec, long requestedLinuxBytes, string stagingDirectory,
            string pinnedIsoPath, long pinnedIsoBytes, CancellationToken cancellationToken = default)
        {
            OwnedCalls++;
            throw new InvalidOperationException("Owned root observer reached.");
        }
    }
}
