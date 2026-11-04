using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class MainWindowRestartNavigationTests
{
    [Fact]
    public async Task Restart_registers_before_restarting_and_disables_navigation_while_pending()
    {
        var fixture = new Fixture();
        await fixture.PrepareAsync();
        var registration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Preparation.Register = () => registration.Task;
        var notifications = new List<string?>();
        fixture.Wizard.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);

        Assert.Equal("Restart", fixture.Wizard.PrimaryActionLabel);
        Assert.True(fixture.Wizard.HasPrimaryActionGlyph);
        Assert.True(fixture.Wizard.CanGoNext);
        var restart = fixture.Wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(["prepare", "register"], fixture.Calls);
        Assert.False(fixture.Wizard.CanGoNext);
        Assert.True(fixture.Wizard.IsCloseBlocked);
        Assert.Equal("Restarting…", fixture.Wizard.PrimaryActionLabel);
        Assert.Contains(nameof(MainWindowViewModel.CanGoNext), notifications);
        Assert.Contains(nameof(MainWindowViewModel.PrimaryActionLabel), notifications);

        registration.SetResult();
        await restart;

        Assert.Equal(["prepare", "register", "restart"], fixture.Calls);
        Assert.Same(fixture.Install, fixture.Wizard.CurrentPage);
        Assert.False(fixture.Install.HasError);
    }

    [Fact]
    public async Task Failed_registration_keeps_Restart_available_without_repeating_preparation()
    {
        var fixture = new Fixture();
        await fixture.PrepareAsync();
        fixture.Preparation.Register = () =>
            Task.FromException(new InvalidOperationException("registration readback failed"));

        await fixture.Wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(["prepare", "register"], fixture.Calls);
        Assert.True(fixture.Install.IsComplete);
        Assert.True(fixture.Install.HasError);
        Assert.True(fixture.Wizard.CanGoNext);
        Assert.False(fixture.Wizard.IsCloseBlocked);
        Assert.Equal("Restart", fixture.Wizard.PrimaryActionLabel);
        Assert.Same(fixture.Install, fixture.Wizard.CurrentPage);

        fixture.Preparation.Register = () => Task.CompletedTask;
        await fixture.Wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(["prepare", "register", "register", "restart"], fixture.Calls);
        Assert.False(fixture.Install.HasError);
        Assert.Same(fixture.Install, fixture.Wizard.CurrentPage);
    }

    [Fact]
    public async Task Restart_does_not_register_an_unprepared_or_running_installation()
    {
        var fixture = new Fixture();
        Assert.False(fixture.Wizard.CanGoNext);
        await fixture.Wizard.NextCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Calls);

        fixture.Install.IsComplete = true;
        fixture.Install.IsRunning = true;
        Assert.False(fixture.Wizard.CanGoNext);
        await fixture.Wizard.NextCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Calls);
        Assert.Same(fixture.Install, fixture.Wizard.CurrentPage);
    }

    private sealed class Fixture
    {
        public List<string> Calls { get; } = [];
        public RecordingPreparation Preparation { get; }
        public DirectInstallViewModel Install { get; }
        public MainWindowViewModel Wizard { get; }

        public Fixture()
        {
            Preparation = new RecordingPreparation(Calls);
            Install = new DirectInstallViewModel(Preparation, new RecordingMaintenance(Calls),
                NullLogger<DirectInstallViewModel>.Instance);
            var catalog = new Catalog();
            Wizard = new MainWindowViewModel(new WelcomeViewModel(catalog),
                new PreflightViewModel(null!, null!, NullLogger<PreflightViewModel>.Instance),
                new DistroSelectionViewModel(catalog, new Compatibility(),
                    NullLogger<DistroSelectionViewModel>.Instance),
                new IsoAcquisitionViewModel(null!, NullLogger<IsoAcquisitionViewModel>.Instance),
                new MigrationSetupViewModel(new Profile(), []),
                new DiskSelectionViewModel(),
                new FileStagingViewModel(null!, NullLogger<FileStagingViewModel>.Instance),
                Install, new UsbWriterViewModel(null!, NullLogger<UsbWriterViewModel>.Instance))
            { CurrentPage = Install };
        }

        public async Task PrepareAsync()
        {
            Install.Prepare(
                new IsoAcquisitionResult(@"C:\verified\debian.iso", true, true, 1024, new string('A', 64)),
                new FileStagingResult(@"C:\staged", 0, 0),
                new DiskInfo(@"\\.\PHYSICALDRIVE2", "Disposable target", 40L << 30, 0, "GPT", [])
                    { GptDiskGuid = Guid.Parse("0fcc16b4-f462-4251-975c-d67a142d4511") },
                20, "debian");
            await Install.InstallCommand.ExecuteAsync(null);
            Assert.True(Install.IsComplete);
        }
    }

    private sealed class RecordingPreparation(List<string> calls) : IInstallationPreparation
    {
        public Func<Task> Register { get; set; } = () => Task.CompletedTask;
        public Observation<bool> PreparationAvailability => Observations.Available(true);

        public Task PrepareAsync(string distroId, DiskInfo targetDisk, long linuxSizeBytes,
            IsoAcquisitionResult verifiedIso, string stagingDirectory, Uri? stage2Url,
            IProgress<DirectInstallProgress>? progress, CancellationToken cancellationToken = default)
        {
            calls.Add("prepare");
            return Task.CompletedTask;
        }

        public Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            calls.Add("register");
            return Register();
        }
    }

    private sealed class RecordingMaintenance(List<string> calls) : IWindowsMaintenance
    {
        public Task<int> StartBitLockerDecryptionAsync(string systemDrive,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void RestartToFirmware() => throw new NotSupportedException();
        public void RestartToInstaller() => calls.Add("restart");
    }

    private sealed class Catalog : IProviderCatalog
    {
        public IReadOnlyList<DistroManifest> LoadedDistros => [];
    }

    private sealed class Compatibility : IProviderCompatibility
    {
        public bool IsLoaded(string distroId) => true;
        public IReadOnlyList<PreflightFinding> Evaluate(string distroId, PreflightReport report) => [];
        public Task<ProviderInspection?> InspectAsync(string distroId,
            CancellationToken cancellationToken = default) => Task.FromResult<ProviderInspection?>(null);
    }

    private sealed class Profile : IUserProfileInspection
    {
        public IReadOnlyList<AppSuggestion> GetSuggestedPackages() => [];
        public string? FindAccountPicture() => null;
        public string? FlatpakForBrowser(string browserName) => null;
    }
}
