using System.IO;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class MainWindowIsoNavigationTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private sealed class SuccessfulPreparation : IInstallationPreparation
    {
        public int Measurements { get; private set; }
        public Task<DirectInstallSpaceEstimate>? PendingMeasurement { get; set; }
        public DirectInstallSpaceEstimate Space { get; set; } = new(80 * GiB, 5 * GiB, GiB);
        public Observation<bool> PreparationAvailability => Observations.Available(true);
        public Task<DirectInstallSpaceEstimate> MeasureSpaceAsync(string distroId, DiskInfo targetDisk,
            IsoAcquisitionResult verifiedIso, FileStagingRequest selectedFiles,
            CancellationToken cancellationToken = default)
        {
            Measurements++;
            Assert.Equal(distroId, selectedFiles.DistroId);
            Assert.EndsWith(distroId + ".iso", verifiedIso.LocalPath, StringComparison.Ordinal);
            return PendingMeasurement ?? Task.FromResult(Space);
        }
        public Task PrepareAsync(string distroId, DiskInfo targetDisk, long linuxSizeBytes,
            IsoAcquisitionResult verifiedIso, string stagingDirectory, Uri? stage2Url,
            IProgress<DirectInstallProgress>? progress, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Wait_cursor_state_tracks_automatic_selection_recheck_and_leaving_the_disk_page()
    {
        var catalog = new Catalog([]);
        var setup = new MigrationSetupViewModel(new Profile(), []);
        var preparation = new SuccessfulPreparation();
        var pending = new TaskCompletionSource<DirectInstallSpaceEstimate>();
        preparation.PendingMeasurement = pending.Task;
        var disk = new DiskSelectionViewModel(preparation);
        var wizard = new MainWindowViewModel(new WelcomeViewModel(catalog),
            new PreflightViewModel(null!, null!, NullLogger<PreflightViewModel>.Instance),
            new DistroSelectionViewModel(catalog, new Compatibility(), NullLogger<DistroSelectionViewModel>.Instance),
            new IsoAcquisitionViewModel(null!, NullLogger<IsoAcquisitionViewModel>.Instance),
            setup, disk, new FileStagingViewModel(null!, NullLogger<FileStagingViewModel>.Instance),
            new DirectInstallViewModel(null!, null!, NullLogger<DirectInstallViewModel>.Instance),
            new UsbWriterViewModel(null!, NullLogger<UsbWriterViewModel>.Instance));
        var cursorStates = new List<bool>();
        wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsCheckingTargetDisk))
                cursorStates.Add(wizard.IsCheckingTargetDisk);
        };
        wizard.CurrentPage = disk;
        var target = new DiskInfo("virtual", "Only disk", 120 * GiB, 0, "GPT",
            [new PartitionInfo(1, "NTFS", 119 * GiB, "Windows", IsSystem: true, IsBoot: true)])
        { GptDiskGuid = Guid.NewGuid() };
        disk.Prepare(Report([target]), Manifest("debian"),
            new IsoAcquisitionResult(@"C:\fixture\debian.iso", true, true, GiB, new string('a', 64)),
            new FileStagingRequest("debian", []));

        Assert.Same(Assert.Single(disk.DiskItems), disk.SelectedItem);
        Assert.True(wizard.IsCheckingTargetDisk);
        Assert.False(wizard.CanGoNext);
        pending.SetResult(preparation.Space);
        Assert.False(wizard.IsCheckingTargetDisk);
        Assert.True(wizard.CanGoNext);

        pending = new();
        preparation.PendingMeasurement = pending.Task;
        var recheck = disk.RefreshSpaceAsync(preserveSelection: true);
        Assert.True(wizard.IsCheckingTargetDisk);
        Assert.False(wizard.CanGoNext);
        wizard.CurrentPage = setup;
        Assert.False(wizard.IsCheckingTargetDisk);
        disk.CancelSpaceCheck();
        pending.SetResult(preparation.Space);
        await recheck;
        Assert.False(wizard.IsCheckingTargetDisk);
        Assert.Contains(true, cursorStates);
        Assert.False(cursorStates[^1]);
        Assert.Equal(2, preparation.Measurements);
    }

    [Fact]
    public void Window_close_waits_for_download_and_private_staging_even_if_page_changes()
    {
        var catalog = new Catalog([]);
        var preflight = new PreflightViewModel(null!, null!,
            NullLogger<PreflightViewModel>.Instance);
        var iso = new IsoAcquisitionViewModel(null!,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        var setup = new MigrationSetupViewModel(new Profile(), []);
        var staging = new FileStagingViewModel(null!,
            NullLogger<FileStagingViewModel>.Instance);
        var wizard = new MainWindowViewModel(new WelcomeViewModel(catalog), preflight,
            new DistroSelectionViewModel(catalog, new Compatibility(),
                NullLogger<DistroSelectionViewModel>.Instance),
            iso, setup, new DiskSelectionViewModel(), staging,
            new DirectInstallViewModel(null!, null!,
                NullLogger<DirectInstallViewModel>.Instance),
            new UsbWriterViewModel(null!, NullLogger<UsbWriterViewModel>.Instance));

        Assert.False(wizard.IsCloseBlocked);
        iso.IsRunning = true;
        wizard.CurrentPage = setup;
        Assert.True(wizard.IsCloseBlocked);

        iso.IsRunning = false;
        staging.IsRunning = true;
        Assert.True(wizard.IsCloseBlocked);
        staging.IsRunning = false;
        Assert.False(wizard.IsCloseBlocked);
    }

    [Fact]
    public async Task Returning_to_the_same_verified_distribution_skips_the_download_receipt()
    {
        var distro = new DistroManifest
        {
            Id = "debian",
            DisplayName = "Debian",
            Description = "Fixture",
            Iso = new DistroIsoSpec
            {
                DownloadUrl = new Uri("https://example.invalid/debian.iso"),
                Sha256 = new string('a', 64),
            },
        };
        var catalog = new Catalog([distro]);
        var preflight = new PreflightViewModel(null!, null!,
            NullLogger<PreflightViewModel>.Instance)
        {
            Report = new PreflightReport
            {
                IsUefi = true,
                SecureBootEnabled = false,
                TpmPresent = true,
                BitLocker = BitLockerState.NotEncrypted,
                GpuVendor = "intel",
                TotalRamBytes = 8L * 1024 * 1024 * 1024,
                Findings = [],
                Disks = [],
            },
        };
        var picker = new DistroSelectionViewModel(catalog, new Compatibility(),
            NullLogger<DistroSelectionViewModel>.Instance);
        var acquisition = new CachedAcquisition();
        var iso = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        iso.Prepare(distro);
        await iso.AcquireCommand.ExecuteAsync(null);
        Assert.True(iso.IsComplete);

        var setup = new MigrationSetupViewModel(new Profile(), []);
        var wizard = new MainWindowViewModel(new WelcomeViewModel(catalog), preflight,
            picker, iso, setup, new DiskSelectionViewModel(),
            new FileStagingViewModel(null!, NullLogger<FileStagingViewModel>.Instance),
            new DirectInstallViewModel(null!, null!,
                NullLogger<DirectInstallViewModel>.Instance),
            new UsbWriterViewModel(null!, NullLogger<UsbWriterViewModel>.Instance));

        await wizard.NextCommand.ExecuteAsync(null); // system check already observed
        await wizard.NextCommand.ExecuteAsync(null); // choose distribution
        picker.SelectedItem = Assert.Single(picker.DistroItems);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Same(setup, wizard.CurrentPage);
        Assert.Equal(1, acquisition.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_distribution_reacquires_and_checks_space_before_staging(bool spaceDrops)
    {
        var debian = Manifest("debian");
        var mint = Manifest("linuxmint-cinnamon");
        var catalog = new Catalog([debian, mint]);
        var target = new DiskInfo(@"\\.\PHYSICALDRIVE7", "Disposable target",
            120 * GiB, 0, "GPT",
            [
                new PartitionInfo(1, "FAT32", GiB, "EFI", IsSystem: true, IsBoot: false),
                new PartitionInfo(2, "NTFS", 119 * GiB, "Windows", IsSystem: false, IsBoot: true,
                    ShrinkableBytes: 80 * GiB),
            ])
        { GptDiskGuid = Guid.Parse("0fcc16b4-f462-4251-975c-d67a142d4511") };
        var preflight = new PreflightViewModel(null!, null!,
            NullLogger<PreflightViewModel>.Instance)
        {
            Report = Report([target]),
        };
        var picker = new DistroSelectionViewModel(catalog, new Compatibility(),
            NullLogger<DistroSelectionViewModel>.Instance);
        var acquisition = new RecordingAcquisition();
        var setup = new MigrationSetupViewModel(new Profile(), []);
        var preparation = new SuccessfulPreparation();
        var disk = new DiskSelectionViewModel(preparation);
        var staging = new RecordingStaging();
        var stagedPage = new FileStagingViewModel(staging,
            NullLogger<FileStagingViewModel>.Instance,
            Path.Join(Path.GetTempPath(), "igloo-nav-absent-" + Guid.NewGuid().ToString("N")));
        var install = new DirectInstallViewModel(preparation, new StubMaintenance(),
            NullLogger<DirectInstallViewModel>.Instance);
        var wizard = new MainWindowViewModel(new WelcomeViewModel(catalog), preflight,
            picker, new IsoAcquisitionViewModel(acquisition,
                NullLogger<IsoAcquisitionViewModel>.Instance),
            setup, disk, stagedPage, install,
            new UsbWriterViewModel(null!, NullLogger<UsbWriterViewModel>.Instance));

        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        picker.SelectedCategory = DistroSelectionViewModel.AllCategory;
        picker.SelectedItem = picker.DistroItems.Single(d => d.Manifest.Id == debian.Id);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Same(setup, wizard.CurrentPage);

        wizard.BackCommand.Execute(null);
        wizard.BackCommand.Execute(null);
        picker.SelectedItem = picker.DistroItems.Single(d => d.Manifest.Id == mint.Id);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Same(setup, wizard.CurrentPage);
        Assert.Equal([debian.Id, mint.Id], acquisition.DistroIds);

        setup.SetPasswords("temporary-passphrase", "temporary-passphrase");
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Same(disk, wizard.CurrentPage);
        disk.SelectedItem = Assert.Single(disk.DiskItems);
        Assert.True(disk.CanDualBoot);
        if (spaceDrops)
            preparation.Space = new(28954L << 20, 4710L << 20, 0);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(2, preparation.Measurements);
        if (spaceDrops)
        {
            Assert.Same(disk, wizard.CurrentPage);
            Assert.Null(staging.LastInput);
            Assert.False(install.IsComplete);
            Assert.False(disk.CanProceed);
            Assert.Equal(23, disk.LinuxSliderMaximumGb);
            return;
        }

        Assert.Same(install, wizard.CurrentPage);
        Assert.True(install.IsComplete);
        Assert.False(install.HasError);
        Assert.Equal("Restart", wizard.PrimaryActionLabel);
        Assert.NotNull(staging.LastInput);
        Assert.Equal(mint.Id, staging.LastInput.Files.DistroId);
        Assert.Equal(target.GptDiskGuid, staging.LastInput.TargetDisk?.GptDiskGuid);
        Assert.Equal(DiskInstallMode.DualBoot, staging.LastInput.InstallMode);
        Assert.Equal(disk.LinuxSizeGb, staging.LastInput.LinuxSizeGb);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_the_disk_or_allocation_during_next_requires_a_new_next_click(bool changeSize)
    {
        var distro = Manifest("debian") with { SupportedInstallModes = ["dual-boot", "replace"] };
        var catalog = new Catalog([distro]);
        var first = new DiskInfo("first", "First disk", 120 * GiB, 0, "GPT",
            [new PartitionInfo(1, "NTFS", 119 * GiB, "Windows", IsSystem: true, IsBoot: true)])
        { GptDiskGuid = Guid.NewGuid() };
        var second = first with { DeviceId = "second", GptDiskGuid = Guid.NewGuid() };
        var preflight = new PreflightViewModel(null!, null!, NullLogger<PreflightViewModel>.Instance)
        { Report = Report([first, second]) };
        var picker = new DistroSelectionViewModel(catalog, new Compatibility(),
            NullLogger<DistroSelectionViewModel>.Instance);
        var setup = new MigrationSetupViewModel(new Profile(), []);
        var preparation = new SuccessfulPreparation();
        var disk = new DiskSelectionViewModel(preparation);
        var staging = new RecordingStaging();
        var install = new DirectInstallViewModel(preparation, new StubMaintenance(),
            NullLogger<DirectInstallViewModel>.Instance);
        var wizard = new MainWindowViewModel(new WelcomeViewModel(catalog), preflight, picker,
            new IsoAcquisitionViewModel(new RecordingAcquisition(),
                NullLogger<IsoAcquisitionViewModel>.Instance), setup, disk,
            new FileStagingViewModel(staging, NullLogger<FileStagingViewModel>.Instance,
                Path.Join(Path.GetTempPath(), "igloo-nav-absent-" + Guid.NewGuid().ToString("N"))),
            install, new UsbWriterViewModel(null!, NullLogger<UsbWriterViewModel>.Instance));

        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        picker.SelectedItem = Assert.Single(picker.DistroItems);
        await wizard.NextCommand.ExecuteAsync(null);
        setup.SetPasswords("temporary-passphrase", "temporary-passphrase");
        await wizard.NextCommand.ExecuteAsync(null);
        disk.SelectedItem = disk.DiskItems[0];
        Assert.True(disk.CanProceed);
        var originalSize = disk.LinuxSizeGb;
        var pending = new TaskCompletionSource<DirectInstallSpaceEstimate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        preparation.PendingMeasurement = pending.Task;
        var next = wizard.NextCommand.ExecuteAsync(null);
        Assert.True(disk.IsCheckingSpace);

        if (changeSize)
        {
            disk.LinuxSizeGb = originalSize + 1;
        }
        else
        {
            // The new target's check finishes before the old Next check settles.
            preparation.PendingMeasurement = null;
            disk.SelectedItem = disk.DiskItems[1];
        }
        Assert.Equal(changeSize ? originalSize + 1 : originalSize, disk.LinuxSizeGb);
        pending.SetResult(preparation.Space);
        await next;

        Assert.True(disk.CanProceed);
        Assert.Same(disk, wizard.CurrentPage);
        Assert.Null(staging.LastInput);
        Assert.False(install.IsComplete);
    }

    private sealed class StubMaintenance : IWindowsMaintenance
    {
        public Task<int> StartBitLockerDecryptionAsync(string systemDrive,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
        public void RestartToFirmware() { }
        public void RestartToInstaller() { }
    }

    private static DistroManifest Manifest(string id) => new()
    {
        Id = id,
        DisplayName = id,
        Description = "Fixture",
        SupportedInstallModes = ["dual-boot"],
        MinimumRequirements = new DistroRequirements { MinDiskBytes = 20 * GiB },
        Iso = new DistroIsoSpec
        {
            DownloadUrl = new Uri($"https://example.invalid/{id}.iso"),
            Sha256 = new string('a', 64),
        },
    };

    private static PreflightReport Report(IReadOnlyList<DiskInfo> disks) => new()
    {
        IsUefi = true,
        SecureBootEnabled = false,
        TpmPresent = true,
        BitLocker = BitLockerState.NotEncrypted,
        GpuVendor = "intel",
        TotalRamBytes = 8 * GiB,
        Findings = [],
        Disks = disks,
    };

    private sealed class Catalog(IReadOnlyList<DistroManifest> distros) : IProviderCatalog
    {
        public IReadOnlyList<DistroManifest> LoadedDistros => distros;
    }

    private sealed class Compatibility : IProviderCompatibility
    {
        public bool IsLoaded(string distroId) => true;
        public IReadOnlyList<PreflightFinding> Evaluate(string distroId, PreflightReport report) => [];
        public Task<ProviderInspection?> InspectAsync(string distroId,
            CancellationToken cancellationToken = default) => Task.FromResult<ProviderInspection?>(null);
    }

    private sealed class CachedAcquisition : IIsoWorkflow
    {
        public int Calls { get; private set; }

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new IsoAcquisitionResult(@"C:\verified\debian.iso",
                true, false, 1024, new string('a', 64)));
        }
    }

    private sealed class RecordingAcquisition : IIsoWorkflow
    {
        public List<string> DistroIds { get; } = [];

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            DistroIds.Add(distro.Id);
            return Task.FromResult(new IsoAcquisitionResult($@"C:\verified\{distro.Id}.iso",
                true, false, 1024, new string('a', 64)));
        }
    }

    private sealed class RecordingStaging : IMigrationStagingWorkflow
    {
        public LocalMigrationStagingInput? LastInput { get; private set; }

        public Task<FileStagingResult> StageAsync(LocalMigrationStagingInput input,
            IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default)
        {
            LastInput = input;
            return Task.FromResult(new FileStagingResult(@"C:\verified\staging", 1, 1));
        }
    }

    private sealed class Profile : IUserProfileInspection
    {
        public IReadOnlyList<AppSuggestion> GetSuggestedPackages() => [];
        public string? FindAccountPicture() => null;
        public string? FlatpakForBrowser(string browserName) => null;
    }
}

