using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Igloo.Command.Tests;

[Collection("RecoveryState")]
public sealed class UsbPreparationSafetyTests
{
    [Fact]
    public async Task Direct_preparation_availability_reports_incomplete_provider_bindings_before_io()
    {
        using var fixture = await SpaceMeasurementFixture.CreateAsync();

        // Every shipped provider installs through the legacy preparation path (alpha flow).
        foreach (var distroId in new[] { "fedora-kde", "linuxmint-cinnamon" })
        {
            var availability = fixture.Preparation.GetDistroPreparationAvailability(distroId);
            Assert.Equal(ObservationAvailability.Available, availability.Availability);
            Assert.True(availability.Value);
        }
        var supported = fixture.Preparation.GetDistroPreparationAvailability("debian");
        Assert.Equal(ObservationAvailability.Available, supported.Availability);
        Assert.True(supported.Value);
        Assert.Equal(0, fixture.Storage.ReadCount);
        Assert.Equal(0, fixture.Preflight.ReadCount);
        Assert.Empty(fixture.Events);
    }

    [Theory]
    [InlineData(DiskInstallMode.ReplaceDisk)]
    [InlineData((DiskInstallMode)99)]
    public async Task Community_rejects_replacement_and_invalid_modes_before_staging_or_usb_io(
        DiskInstallMode mode)
    {
        var events = new List<string>();
        var storage = new RecordingStorage(Guid.NewGuid());
        var preflight = new RecordingPreflight();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommunityBackend();
        services.Replace(ServiceDescriptor.Singleton<IWindowsStorageReader>(storage));
        services.Replace(ServiceDescriptor.Singleton<IPreflightChecker>(preflight));
        services.Replace(ServiceDescriptor.Singleton<IUsbWriterService>(new RecordingWriter(events)));
        services.Replace(ServiceDescriptor.Singleton<IFileStagingService>(new RecordingStaging(events)));
        using var host = services.BuildServiceProvider();
        var observed = new PreflightReport
        {
            IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "",
            TotalRamBytes = 8L << 30, Findings = [],
        };
        var stagedInput = new LocalMigrationStagingInput(new FileStagingRequest("fedora-kde", []),
            new Igloo.Core.Services.UserSetup { WindowsUsername = "tester", LinuxUsername = "tester" },
            observed, null, mode, 20);
        var stagingError = await Assert.ThrowsAsync<NotSupportedException>(() =>
            host.GetRequiredService<IMigrationStagingWorkflow>().StageAsync(stagedInput, null));
        Assert.Contains("always preserves Windows", stagingError.Message, StringComparison.Ordinal);

        var usbInput = new UsbPreparationInput(new UsbDriveInfo(3, "USB", 8L << 30,
            @"\\.\PHYSICALDRIVE3"), "missing.iso", "missing-staging", null,
            mode, 20, new string('a', 64)) { DistroId = "fedora-kde" };
        var usbError = await Assert.ThrowsAsync<NotSupportedException>(() =>
            host.GetRequiredService<IUsbPreparation>().WriteAsync(usbInput, null, null));
        Assert.Contains("always preserves Windows", usbError.Message, StringComparison.Ordinal);
        Assert.Equal(0, storage.ReadCount);
        Assert.Equal(0, preflight.ReadCount);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Space_measurement_rejects_unverified_image_and_changed_disk_before_scanning_sources()
    {
        using var fixture = await SpaceMeasurementFixture.CreateAsync();
        var missingSource = new FileStagingRequest("debian", [Path.Join(fixture.Root, "missing")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparation.MeasureSpaceAsync(
            "debian", fixture.Target, fixture.Iso with { Sha256Verified = false }, missingSource));
        Assert.Equal(0, fixture.Storage.ReadCount);
        Assert.Empty(fixture.Events);

        fixture.Storage.CurrentGuid = Guid.NewGuid();
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Preparation.MeasureSpaceAsync("debian", fixture.Target, fixture.Iso, missingSource));
        Assert.Contains("revalidate", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Storage.ReadCount);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Preflight.ReadCount);
    }

    [Fact]
    public async Task Space_measurement_forwards_selected_files_agent_bytes_and_generated_reserve()
    {
        using var fixture = await SpaceMeasurementFixture.CreateAsync();
        var result = await fixture.Preparation.MeasureSpaceAsync("debian", fixture.Target,
            fixture.Iso, fixture.Files);

        Assert.Equal(["measure"], fixture.Events);
        Assert.Equal(123L + fixture.AgentBytes + (64L << 20), fixture.Native.MeasuredStagedBytes);
        Assert.Same(fixture.Native.SpaceEstimate, result);
        Assert.Equal(29L << 30, result.ShrinkableBytes);
        Assert.Equal(2, fixture.Storage.ReadCount);
        Assert.Equal(0, fixture.Preflight.ReadCount);
        Assert.Equal(["document.bin"], Directory.GetFiles(fixture.Source).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Space_measurement_does_not_return_a_result_after_the_disk_identity_changes()
    {
        using var fixture = await SpaceMeasurementFixture.CreateAsync();
        fixture.Native.OnMeasure = () => fixture.Storage.CurrentGuid = Guid.NewGuid();

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Preparation.MeasureSpaceAsync("debian", fixture.Target, fixture.Iso, fixture.Files));

        Assert.Contains("revalidate", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(["measure"], fixture.Events);
        Assert.Equal(2, fixture.Storage.ReadCount);
        Assert.Equal(0, fixture.Preflight.ReadCount);
    }

    private sealed class SpaceMeasurementFixture : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(),
            "igloo-space-host-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Join(Root, "Documents");
        public List<string> Events { get; } = [];
        public RecordingStorage Storage { get; } = new(Guid.NewGuid());
        public RecordingPreflight Preflight { get; } = new();
        public RecordingDirectInstall Native { get; private set; } = null!;
        public IInstallationPreparation Preparation => _host!.GetRequiredService<IInstallationPreparation>();
        public IsoAcquisitionResult Iso { get; private set; } = null!;
        public DiskInfo Target => new(@"\\.\PHYSICALDRIVE2", "Disposable target", 40L << 30,
            20L << 30, "GPT", []) { GptDiskGuid = _targetGuid };
        public FileStagingRequest Files => new("debian", [Source]);
        public long AgentBytes { get; private set; }
        private Guid _targetGuid;
        private ServiceProvider? _host;

        public static async Task<SpaceMeasurementFixture> CreateAsync()
        {
            var fixture = new SpaceMeasurementFixture();
            try
            {
                fixture._targetGuid = fixture.Storage.CurrentGuid;
                fixture.Native = new RecordingDirectInstall(fixture.Events);
                Directory.CreateDirectory(fixture.Source);
                await File.WriteAllBytesAsync(Path.Join(fixture.Source, "document.bin"), new byte[123]);
                var isoPath = Path.Join(fixture.Root, "synthetic.iso");
                await File.WriteAllBytesAsync(isoPath, new byte[1024]);
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddLocalBackend();
                services.Replace(ServiceDescriptor.Singleton<IWindowsStorageReader>(fixture.Storage));
                services.Replace(ServiceDescriptor.Singleton<IPreflightChecker>(fixture.Preflight));
                services.Replace(ServiceDescriptor.Singleton<IDirectInstallService>(fixture.Native));
                fixture._host = services.BuildServiceProvider();
                var directory = Path.Join(FindRepoRoot(), "distros");
                var loader = fixture._host.GetRequiredService<DistroLoader>();
                loader.Load(directory);
                var registry = fixture._host.GetRequiredService<DistroRegistry>();
                await registry.LoadAsync(directory);
                Assert.True(registry.TryGet("debian", out var plugin));
                var agent = await plugin.GetAgentPayloadAsync();
                fixture.AgentBytes = agent.Files.Sum(file => (long)file.Contents.Length);
                var declared = Assert.Single(loader.LoadedDistros.Where(d => d.Id == "debian"));
                var digest = string.IsNullOrWhiteSpace(declared.Iso.Sha256)
                    ? new string('a', 64) : declared.Iso.Sha256;
                fixture.Iso = new IsoAcquisitionResult(isoPath, true, true, 1024, digest);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _host?.Dispose();
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                Path.GetFullPath(Path.GetDirectoryName(Root)!));
            Assert.StartsWith("igloo-space-host-", Path.GetFileName(Root), StringComparison.Ordinal);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public void Installer_target_revalidation_uses_stable_identity_not_display_model()
    {
        var diskGuid = Guid.NewGuid();
        var selected = new DiskInfo(@"\\.\PHYSICALDRIVE2", "NVMe drive", 40L << 30,
            20L << 30, "GPT", []) { GptDiskGuid = diskGuid };
        var observed = new DiskObservation(
            Observations.Available(2U), Observations.Available("unique-device-id"),
            Observations.Available(3U), Observations.Available("serial"), Observations.Available(17U),
            Observations.Available("Windows Storage Device"), Observations.Available(40UL << 30),
            Observations.Available(2U), Observations.Available(diskGuid),
            Observations.Available(512U), Observations.Available(4096U));

        Assert.True(UsbPreparation.SameInstallerTarget(selected, 2, [observed]));
        Assert.True(UsbPreparation.SameInstallerTarget(selected, 2,
            [observed with
            {
                UniqueId = Observations.Failure<string>(ObservationAvailability.Unavailable, "FixtureNoUniqueId"),
                UniqueIdFormat = Observations.Failure<uint>(ObservationAvailability.Unavailable, "FixtureNoUniqueIdFormat"),
                BusType = Observations.Failure<uint>(ObservationAvailability.Unavailable, "FixtureNoBusType"),
            }]));
        Assert.False(UsbPreparation.SameInstallerTarget(selected, 2,
            [observed with { GptGuid = Observations.Available(Guid.NewGuid()) }]));
        Assert.False(UsbPreparation.SameInstallerTarget(selected, 2, [observed,
            observed with { Number = Observations.Available(3U) }]));
    }

    [Theory]
    [InlineData("debian", "igloo-initrd/igloo-bound-free-setup.sh")]
    [InlineData("linuxmint-cinnamon", "igloo-partman.sh")]
    public async Task Reviewed_artifacts_use_the_exact_root_config_instead_of_the_unbound_preseed(
        string distroId, string reviewedHelper)
    {
        var stage = Path.Join(Path.GetTempPath(),
            "igloo-reviewed-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalBackend();
            using var host = services.BuildServiceProvider();
            var distrosDirectory = Path.Join(FindRepoRoot(), "distros");
            await host.GetRequiredService<DistroRegistry>().LoadAsync(distrosDirectory);
            Assert.True(host.GetRequiredService<DistroRegistry>().TryGet(distroId, out var plugin));
            var target = Guid.NewGuid();
            var manifest = new MigrationManifest
            {
                DistroId = distroId,
                User = new MigrationUser { WindowsUsername = "tester",
                    PreferredLinuxUsername = "tester", LinuxPasswordCrypted = "$6$test$hash" },
                Files = new FileMigrationPlan { StagingPath = "files" },
                Hardware = new HardwareProfile { InstallMode = "dual-boot",
                    TargetDiskGuid = target, TargetDiskBytes = 40L << 30,
                    LinuxPartitionSizeGb = 20 },
            };
            var root = new ReviewedInstallerFreeExtentV1(Guid.NewGuid(), target,
                Guid.NewGuid(), 12UL << 30, 24UL << 30, 20UL << 30);
            var reviewed = await ((IReviewedInstallerConfigProvider)plugin)
                .RenderReviewedInstallerConfigAsync(manifest, root);
            await WriteProviderArtifactsAsync(plugin, manifest, stage, reviewed);

            var verified = await UsbPreparation.RequireProviderArtifactsAsync(stage,
                reviewed, await plugin.GetAgentPayloadAsync(), CancellationToken.None);
            Assert.Equal("preseed.cfg", verified.Config.FileName);
            Assert.Contains(verified.ProviderArtifacts,
                artifact => artifact.RelativePath == reviewedHelper);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                UsbPreparation.RequireProviderArtifactsAsync(stage, plugin, manifest,
                    CancellationToken.None));
        }
        finally
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                Path.GetFullPath(Path.GetDirectoryName(stage)!));
            Assert.StartsWith("igloo-reviewed-artifacts-", Path.GetFileName(stage),
                StringComparison.Ordinal);
            Directory.Delete(stage, recursive: true);
        }
    }

    [Fact]
    public async Task Reviewed_inspection_requires_a_caller_pinned_created_checkpoint()
    {
        var artifactDirectory = Path.Join(Path.GetTempPath(),
            "igloo-reviewed-inspection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDirectory);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalBackend();
            services.Replace(ServiceDescriptor.Singleton(new PreparedLayoutStore(
                new CommunityRecoveryArtifactStore(artifactDirectory))));
            using var provider = services.BuildServiceProvider();
            var distrosDirectory = Path.Join(FindRepoRoot(), "distros");
            provider.GetRequiredService<DistroLoader>().Load(distrosDirectory);
            await provider.GetRequiredService<DistroRegistry>().LoadAsync(distrosDirectory);
            var inspection = provider.GetRequiredService<IReviewedInstallationInspection>();
            var acquired = new IsoAcquisitionResult(Path.Join(artifactDirectory, "debian.iso"),
                true, true, 1024, new string('a', 64));
            await Assert.ThrowsAsync<FileNotFoundException>(() => inspection.InspectAsync(
                "debian", new PreparationCheckpointReference(Guid.NewGuid(), Guid.NewGuid(),
                    new string('b', 64)), 20L << 30, acquired, artifactDirectory));
            Assert.Empty(Directory.EnumerateFiles(artifactDirectory));
        }
        finally
        {
            Directory.Delete(artifactDirectory);
        }
    }

    [Theory]
    [InlineData("debian")]
    [InlineData("linuxmint-cinnamon")]
    [InlineData("fedora-kde")]
    public async Task Reviewed_inspection_rejects_unsigned_result_before_checkpoint_read(
        string distroId)
    {
        var artifactDirectory = Path.Join(Path.GetTempPath(),
            "igloo-reviewed-signature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDirectory);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalBackend();
            services.Replace(ServiceDescriptor.Singleton(new PreparedLayoutStore(
                new CommunityRecoveryArtifactStore(artifactDirectory))));
            using var host = services.BuildServiceProvider();
            var distrosDirectory = Path.Join(FindRepoRoot(), "distros");
            var loader = host.GetRequiredService<DistroLoader>();
            loader.Load(distrosDirectory);
            await host.GetRequiredService<DistroRegistry>().LoadAsync(distrosDirectory);
            var declared = Assert.Single(loader.LoadedDistros.Where(d => d.Id == distroId));
            Assert.NotNull(declared.Iso.GpgSignatureUrl);
            var digest = string.IsNullOrWhiteSpace(declared.Iso.Sha256)
                ? new string('a', 64) : declared.Iso.Sha256;
            var acquired = new IsoAcquisitionResult(Path.Join(artifactDirectory, "synthetic.iso"),
                true, false, 1024, digest);
            var checkpoint = new PreparationCheckpointReference(Guid.NewGuid(),
                Guid.NewGuid(), new string('b', 64));
            var inspection = host.GetRequiredService<IReviewedInstallationInspection>();

            var blocked = await Assert.ThrowsAsync<InvalidDataException>(() =>
                inspection.InspectAsync(distroId, checkpoint, 50L << 30,
                    acquired, artifactDirectory));
            Assert.Contains("signature", blocked.Message, StringComparison.OrdinalIgnoreCase);
            if (distroId == "fedora-kde")
            {
                var ownedBlocked = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    inspection.InspectOwnedAsync(distroId, checkpoint, 50L << 30,
                        acquired, artifactDirectory));
                Assert.Contains("signature", ownedBlocked.Message,
                    StringComparison.OrdinalIgnoreCase);
            }
            Assert.Empty(Directory.EnumerateFiles(artifactDirectory));
        }
        finally
        {
            Directory.Delete(artifactDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("debian")]
    public async Task Direct_preparation_rejects_unsigned_result_before_storage_read(
        string distroId)
    {
        var events = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalBackend();
        var targetGuid = Guid.NewGuid();
        services.Replace(ServiceDescriptor.Singleton<IWindowsStorageReader>(
            new RecordingStorage(targetGuid)));
        var preflight = new RecordingPreflight();
        services.Replace(ServiceDescriptor.Singleton<IPreflightChecker>(preflight));
        services.Replace(ServiceDescriptor.Singleton<IDirectInstallService>(
            new RecordingDirectInstall(events)));
        using var host = services.BuildServiceProvider();
        var distrosDirectory = Path.Join(FindRepoRoot(), "distros");
        var loader = host.GetRequiredService<DistroLoader>();
        loader.Load(distrosDirectory);
        await host.GetRequiredService<DistroRegistry>().LoadAsync(distrosDirectory);
        var declared = Assert.Single(loader.LoadedDistros.Where(d => d.Id == distroId));
        Assert.NotNull(declared.Iso.GpgSignatureUrl);
        var digest = string.IsNullOrWhiteSpace(declared.Iso.Sha256)
            ? new string('a', 64) : declared.Iso.Sha256;
        var acquired = new IsoAcquisitionResult(Path.Join(Path.GetTempPath(), "synthetic.iso"),
            true, false, 1024, digest);
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE2", "Disposable target", 40L << 30,
            20L << 30, "GPT", []) { GptDiskGuid = targetGuid };

        var blocked = await Assert.ThrowsAsync<InvalidDataException>(() =>
            host.GetRequiredService<IInstallationPreparation>().PrepareAsync(distroId,
                target, 20L << 30, acquired, Path.GetTempPath(), null, null));
        Assert.Contains("signature", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
        Assert.Equal(0, preflight.ReadCount);
    }

    [Fact]
    public async Task Direct_preparation_carries_selected_Gpt_identity_and_rejects_stale_staging()
    {
        var events = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommunityBackend();
        var targetGuid = Guid.NewGuid();
        var storage = new RecordingStorage(targetGuid);
        services.Replace(ServiceDescriptor.Singleton<IWindowsStorageReader>(storage));
        var preflight = new RecordingPreflight();
        services.Replace(ServiceDescriptor.Singleton<IPreflightChecker>(preflight));
        services.Replace(ServiceDescriptor.Singleton<IDirectInstallService>(
            new RecordingDirectInstall(events)));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DistroRegistry>();
        var distrosDirectory = Path.Join(FindRepoRoot(), "distros");
        provider.GetRequiredService<DistroLoader>().Load(distrosDirectory);
        await registry.LoadAsync(distrosDirectory);
        Assert.True(registry.TryGet("debian", out var debian));
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE2", "Disposable target", 40L << 30,
            20L << 30, "GPT", []) { GptDiskGuid = targetGuid };
        var stage = Path.Join(Path.GetTempPath(), "igloo-direct-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            async Task WriteManifestAsync(Guid guid, int linuxSizeGb, string installMode = "dual-boot")
            {
                var manifest = new MigrationManifest
                {
                    DistroId = "debian",
                    User = new MigrationUser { WindowsUsername = "tester",
                        PreferredLinuxUsername = "tester",
                        LinuxPasswordCrypted = "$6$test$hash" },
                    Files = new FileMigrationPlan { StagingPath = "files" },
                    Hardware = new HardwareProfile { InstallMode = installMode,
                        TargetDiskGuid = guid, TargetDiskBytes = target.TotalBytes,
                        TargetDiskModel = target.Model, LinuxPartitionSizeGb = linuxSizeGb },
                };
                await File.WriteAllBytesAsync(Path.Join(stage, "migration-manifest.json"),
                    JsonSerializer.SerializeToUtf8Bytes(manifest));
                if (installMode == "dual-boot")
                    await WriteProviderArtifactsAsync(debian, manifest, stage);
            }
            await WriteManifestAsync(targetGuid, 20);
            var acquired = new IsoAcquisitionResult(Path.Join(stage, "debian.iso"), true,
                true, 1024, new string('a', 64));
            var preparation = provider.GetRequiredService<IInstallationPreparation>();
            Assert.True(preparation.PreparationAvailability.Value);
            await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareAsync(
                "debian", target, 10L << 30, acquired, stage, null, null));
            Assert.Empty(events);
            await preparation.PrepareAsync("debian", target, 20L << 30, acquired,
                stage, null, null);
            Assert.Equal(["direct"], events);

            events.Clear();
            var readsBeforeRejectedMode = preflight.ReadCount;
            await WriteManifestAsync(targetGuid, 20, "replace");
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.PrepareAsync(
                "debian", target, 20L << 30, acquired, stage, null, null));
            Assert.Empty(events);
            Assert.Equal(readsBeforeRejectedMode, preflight.ReadCount);
            await WriteManifestAsync(targetGuid, 20);

            var fedoraBlocked = await Assert.ThrowsAsync<NotSupportedException>(() =>
                preparation.PrepareAsync("fedora-kde", target, 20L << 30,
                    acquired, stage, null, null));
            Assert.Contains("installer-created root binding", fedoraBlocked.Message,
                StringComparison.Ordinal);
            Assert.Empty(events);

            await File.AppendAllTextAsync(Path.Join(stage, "preseed.cfg"), "\n# altered\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.PrepareAsync(
                "debian", target, 20L << 30, acquired, stage, null, null));
            Assert.Empty(events);

            await WriteManifestAsync(targetGuid, 20);
            storage.CurrentGuid = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareAsync(
                "debian", target, 20L << 30, acquired, stage, null, null));
            Assert.Empty(events);

            storage.CurrentGuid = targetGuid;
            await WriteManifestAsync(Guid.NewGuid(), 20);
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.PrepareAsync(
                "debian", target, 20L << 30, acquired, stage, null, null));
            Assert.Empty(events);

            await WriteManifestAsync(targetGuid, 21);
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.PrepareAsync(
                "debian", target, 20L << 30, acquired, stage, null, null));
            Assert.Empty(events);

            await WriteManifestAsync(targetGuid, 20);
            preflight.BitLocker = BitLockerState.EncryptedAndUnlocked;
            await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareAsync(
                "debian", target, 20L << 30, acquired, stage, null, null));
            Assert.Empty(events);
        }
        finally
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                Path.GetFullPath(Path.GetDirectoryName(stage)!));
            Assert.StartsWith("igloo-direct-target-", Path.GetFileName(stage), StringComparison.Ordinal);
            Directory.Delete(stage, recursive: true);
        }
    }

    [Fact]
    public async Task Fedora_product_usb_route_uses_loaded_provider_and_exact_staged_target()
    {
        var events = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalBackend();
        services.Replace(ServiceDescriptor.Singleton<IUsbWriterService>(new RecordingWriter(events)));
        var targetGuid = Guid.NewGuid();
        services.Replace(ServiceDescriptor.Singleton<IWindowsStorageReader>(new RecordingStorage(targetGuid)));
        var preflight = new RecordingPreflight();
        services.Replace(ServiceDescriptor.Singleton<IPreflightChecker>(preflight));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DistroRegistry>();
        var distrosDirectory = Path.Join(FindRepoRoot(), "distros");
        provider.GetRequiredService<DistroLoader>().Load(distrosDirectory);
        await registry.LoadAsync(distrosDirectory);
        Assert.True(registry.TryGet("fedora-kde", out var fedora));
        var digest = fedora.Metadata.IsoSha256;
        Assert.Equal(64, digest.Length);

        var stage = Path.Join(Path.GetTempPath(), "igloo-usb-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var disk = new DiskInfo(@"\\.\PHYSICALDRIVE2", "Disposable target", 40L << 30,
                20L << 30, "GPT", []) { GptDiskGuid = targetGuid };
            async Task WriteManifestAsync(Guid guid,
                IReadOnlyList<WifiNetwork>? wifiNetworks = null)
            {
                var manifest = new MigrationManifest
                {
                    DistroId = "fedora-kde",
                    User = new MigrationUser { WindowsUsername = "tester",
                        PreferredLinuxUsername = "tester" },
                    Files = new FileMigrationPlan { StagingPath = "files" },
                    Hardware = new HardwareProfile { InstallMode = "replace",
                        TargetDiskGuid = guid, TargetDiskBytes = disk.TotalBytes,
                        TargetDiskModel = disk.Model },
                    WifiNetworks = wifiNetworks ?? [],
                };
                await File.WriteAllBytesAsync(Path.Join(stage, "migration-manifest.json"),
                    JsonSerializer.SerializeToUtf8Bytes(manifest));
                if (wifiNetworks is null)
                    await WriteProviderArtifactsAsync(fedora, manifest, stage);
            }
            await WriteManifestAsync(targetGuid);
            var isoPath = Path.Join(stage, "fedora.iso");
            var acquired = new IsoAcquisitionResult(isoPath, true, true, 1024, digest);
            var input = new UsbPreparationInput(new UsbDriveInfo(3, "USB", 8L << 30,
                @"\\.\PHYSICALDRIVE3"), isoPath, stage, disk, DiskInstallMode.ReplaceDisk,
                0, digest) { DistroId = "fedora-kde", VerifiedIso = acquired };
            var preparation = provider.GetRequiredService<IUsbPreparation>();

            await preparation.WriteAsync(input, null, null);
            Assert.Equal(["fat-seed"], events);

            events.Clear();
            var unsigned = await Assert.ThrowsAsync<InvalidDataException>(() =>
                preparation.WriteAsync(input with
                    { VerifiedIso = acquired with { GpgVerified = false } },
                    null, null));
            Assert.Contains("signature", unsigned.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(events);

            events.Clear();
            await WriteManifestAsync(targetGuid,
                [new WifiNetwork { Ssid = "synthetic-test", Psk = "synthetic-password" }]);
            var wifiBlocked = await Assert.ThrowsAsync<NotSupportedException>(() =>
                preparation.WriteAsync(input, null, null));
            Assert.Contains("Turn off saved Wi-Fi migration", wifiBlocked.Message,
                StringComparison.Ordinal);
            Assert.Empty(events);
            await WriteManifestAsync(targetGuid);

            await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.WriteAsync(
                input with { TargetDisk = disk with { TotalBytes = 10L << 30 } },
                null, null));
            Assert.Empty(events);

            await File.AppendAllTextAsync(Path.Join(stage, "ks.cfg"), "\n# altered\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.WriteAsync(input, null, null));
            Assert.Empty(events);

            await WriteManifestAsync(targetGuid);
            await File.AppendAllTextAsync(Path.Join(stage, "igloo-agent", "first-boot.sh"),
                "\n# altered\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.WriteAsync(input, null, null));
            Assert.Empty(events);

            await WriteManifestAsync(targetGuid);
            await File.WriteAllTextAsync(Path.Join(stage, "igloo-agent", "rogue.sh"),
                "#!/bin/sh\nexit 0\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.WriteAsync(input, null, null));
            Assert.Empty(events);
            File.Delete(Path.Join(stage, "igloo-agent", "rogue.sh"));

            await File.WriteAllTextAsync(Path.Join(stage, "migration-manifest.json"),
                "{\"distroId\":\"fedora-kde\"}");
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.WriteAsync(input, null, null));
            Assert.Empty(events);

            await WriteManifestAsync(Guid.NewGuid());
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.WriteAsync(input, null, null));
            Assert.Empty(events);

            await WriteManifestAsync(targetGuid);
            await Assert.ThrowsAsync<InvalidDataException>(() => preparation.WriteAsync(
                input with { VerifiedIso = acquired with { VerifiedSha256 = new string('f', 64) } },
                null, null));
            Assert.Empty(events);

            preflight.BitLocker = BitLockerState.DecryptionInProgress;
            await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.WriteAsync(input, null, null));
            Assert.Empty(events);
        }
        finally
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                Path.GetFullPath(Path.GetDirectoryName(stage)!));
            Assert.StartsWith("igloo-usb-route-", Path.GetFileName(stage), StringComparison.Ordinal);
            Directory.Delete(stage, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Join(dir.FullName, "Igloo.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("The iGloo provider fixture is unavailable.");
    }

    private static async Task WriteProviderArtifactsAsync(IDistroPlugin provider,
        MigrationManifest manifest, string stage, InstallerConfig? config = null)
    {
        var rendered = config ?? await provider.RenderInstallerConfigAsync(manifest);
        await File.WriteAllBytesAsync(Path.Join(stage, rendered.FileName), rendered.Contents.ToArray());
        foreach (var extra in rendered.Extras)
        {
            var path = Path.Join(stage, extra.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, extra.Contents.ToArray());
        }
        var payload = await provider.GetAgentPayloadAsync();
        foreach (var agent in payload.Files)
        {
            var path = Path.Join(stage, "igloo-agent", agent.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, agent.Contents.ToArray());
        }
    }

    [Fact]
    public async Task Public_usb_preparation_rejects_unselected_or_unsupported_provider_before_write()
    {
        var events = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalBackend();
        services.Replace(ServiceDescriptor.Singleton<IUsbWriterService>(new RecordingWriter(events)));
        using var provider = services.BuildServiceProvider();
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE2", "Disposable target", 40L << 30,
            20L << 30, "GPT", []) { GptDiskGuid = Guid.NewGuid() };
        var input = new UsbPreparationInput(new UsbDriveInfo(3, "USB", 8L << 30,
            @"\\.\PHYSICALDRIVE3"), "verified.iso", "staging", target,
            DiskInstallMode.ReplaceDisk, 0, new string('a', 64));
        var preparation = provider.GetRequiredService<IUsbPreparation>();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            preparation.WriteAsync(input, null, null));
        foreach (var distroId in new[] { "debian", "linuxmint-cinnamon" })
        {
            var replaceError = await Assert.ThrowsAsync<NotSupportedException>(() =>
                preparation.WriteAsync(input with { DistroId = distroId }, null, null));
            Assert.Contains("Choose dual boot on the Windows system disk", replaceError.Message,
                StringComparison.Ordinal);
            var dualBootError = await Assert.ThrowsAsync<NotSupportedException>(() =>
                preparation.WriteAsync(input with
                {
                    DistroId = distroId,
                    InstallMode = DiskInstallMode.DualBoot,
                    LinuxSizeGb = 10,
                }, null, null));
            Assert.Contains("Continue with the direct installation workflow",
                dualBootError.Message, StringComparison.Ordinal);
        }
        Assert.Empty(events);
    }


    private sealed class RecordingStaging(List<string> events) : IFileStagingService
    {
        public Task<FileStagingResult> StageAsync(FileStagingRequest request,
            IProgress<FileStagingProgress>? progress, CancellationToken ct = default)
        {
            events.Add("stage");
            throw new InvalidOperationException("Rejected Community modes must never stage files.");
        }
    }

    private sealed class RecordingStorage(Guid guid) : IWindowsStorageReader
    {
        public Guid CurrentGuid { get; set; } = guid;
        public int ReadCount { get; private set; }

        public WindowsStorageBatch ReadDisks()
        {
            ReadCount++;
            return new([new WindowsStorageRow(
            new Dictionary<string, object?>
            {
                ["Number"] = 2U,
                ["FriendlyName"] = "Disposable target",
                ["Size"] = 40UL << 30,
                ["PartitionStyle"] = 2U,
                ["UniqueId"] = "disposable-target-serial",
                ["UniqueIdFormat"] = 3U,
                ["BusType"] = 17U,
                ["Guid"] = CurrentGuid.ToString("D"),
            }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase))]);
        }

        public WindowsStorageBatch ReadPartitions(int? diskNumber = null,
            int? partitionNumber = null, bool efiOnly = false) => new([]);
        public WindowsStorageBatch ReadVolumes(char driveLetter) => new([]);
        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition,
            bool explicitParameters = false) => new(null);
    }

    private sealed class RecordingPreflight : IPreflightChecker
    {
        public BitLockerState BitLocker { get; set; } = BitLockerState.NotEncrypted;
        public int ReadCount { get; private set; }
        public int? BlockOnReadNumber { get; set; }

        public Task<PreflightReport> RunAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(new PreflightReport
            {
                IsUefi = true,
                SecureBootEnabled = false,
                TpmPresent = true,
                BitLocker = ReadCount == BlockOnReadNumber
                    ? BitLockerState.EncryptedAndUnlocked : BitLocker,
                Disks = [],
                GpuVendor = "",
                TotalRamBytes = 8L << 30,
                Findings = [],
            });
        }
    }

    private sealed class RecordingWriter(List<string> events) : IUsbWriterService
    {
        public bool Fail { get; set; }
        public Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UsbDriveInfo>>([]);
        public Task WriteAsync(UsbDriveInfo drive, string isoPath, string stagingDirectory,
            string expectedIsoSha256, IProgress<UsbWriteProgress>? progress,
            CancellationToken ct = default)
        {
            Assert.Equal(new string('a', 64), expectedIsoSha256);
            events.Add("write");
            return Fail ? Task.FromException(new InvalidDataException("ISO changed.")) : Task.CompletedTask;
        }

        public async Task WriteBootableFatSeedAsync(UsbDriveInfo drive, string isoPath,
            string stagingDirectory, string expectedIsoSha256,
            UsbStagingIdentity stagingIdentity, InstallerBootSpec bootSpec,
            IProgress<UsbWriteProgress>? progress, CancellationToken ct = default)
        {
            Assert.Equal(expectedIsoSha256, bootSpec.IsoSha256);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                Path.Join(stagingDirectory, "migration-manifest.json"), ct))),
                stagingIdentity.ManifestSha256);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                Path.Join(stagingDirectory, "ks.cfg"), ct))),
                stagingIdentity.KickstartSha256);
            Assert.Contains(stagingIdentity.ProviderArtifacts, artifact =>
                artifact.RelativePath == "igloo-agent/first-boot.sh");
            foreach (var artifact in stagingIdentity.ProviderArtifacts)
                Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                    Path.Join(stagingDirectory,
                        artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)), ct))),
                    artifact.Sha256);
            events.Add("fat-seed");
            if (Fail) throw new InvalidDataException("ISO changed.");
        }
    }

    private sealed class RecordingDirectInstall(List<string> events) : IDirectInstallService
    {
        public Observation<bool> PreparationAvailability => Observations.Available(true);
        public long MeasuredStagedBytes { get; private set; }
        public DirectInstallSpaceEstimate? SpaceEstimate { get; private set; }
        public Action? OnMeasure { get; set; }

        public Task<DirectInstallSpaceEstimate> MeasureSpaceAsync(int diskNumber, string isoPath,
            InstallerBootSpec bootSpec, long stagedBytes, CancellationToken ct = default)
        {
            Assert.Equal(2, diskNumber);
            Assert.True(File.Exists(isoPath));
            Assert.NotNull(bootSpec.IsoSha256);
            ct.ThrowIfCancellationRequested();
            events.Add("measure");
            MeasuredStagedBytes = stagedBytes;
            var estimate = new DirectInstallSpaceEstimate(29L << 30,
                stagedBytes + (4L << 30), stagedBytes);
            SpaceEstimate = estimate;
            OnMeasure?.Invoke();
            return Task.FromResult(estimate);
        }

        public Task PrepareAsync(int diskNumber, long linuxSizeBytes, string isoPath,
            string stagingDirectory, InstallerBootSpec bootSpec, Uri? stage2Url = null,
            IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default)
        {
            Assert.Equal(2, diskNumber);
            Assert.Equal(20L << 30, linuxSizeBytes);
            events.Add("direct");
            return Task.CompletedTask;
        }

        public Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

}
