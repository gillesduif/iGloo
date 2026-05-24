using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Igloo.App.ViewModels;

/// <summary>
/// Orchestrates the linear wizard flow.
/// Each step is a ViewModel instance; the active one is bound to MainWindow's ContentControl
/// and resolved to a view via DataTemplates in App.xaml.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly List<object>             _steps;
    private readonly PreflightViewModel       _preflight;
    private readonly DistroSelectionViewModel _distroSelection;
    private readonly IsoAcquisitionViewModel  _isoAcquisition;
    private readonly MigrationSetupViewModel  _migrationSetup;
    private readonly DiskSelectionViewModel   _diskSelection;
    private readonly FileStagingViewModel     _fileStaging;
    private readonly UsbWriterViewModel       _usbWriter;
    private int _stepIndex;

    [ObservableProperty]
    private object _currentPage;

    // ── Computed navigation state ────────────────────────────────────────────

    public bool CanGoBack => _stepIndex > 0;

    public bool CanGoNext => CurrentPage switch
    {
        WelcomeViewModel               => true,
        PreflightViewModel pf          => pf.CanProceed,
        DistroSelectionViewModel ds    => ds.CanProceed,
        IsoAcquisitionViewModel acq    => acq.IsComplete && !acq.HasError,
        MigrationSetupViewModel setup  => setup.CanProceed,
        DiskSelectionViewModel disk    => disk.CanProceed,
        FileStagingViewModel fs        => fs.IsComplete && !fs.HasError,
        UsbWriterViewModel usb         => usb.IsComplete && !usb.HasError,
        _                              => false,
    };

    public string StepDescription => CurrentPage switch
    {
        WelcomeViewModel         => "Welcome",
        PreflightViewModel       => "System check",
        DistroSelectionViewModel => "Choose your Linux distribution",
        IsoAcquisitionViewModel  => "Downloading installer",
        MigrationSetupViewModel  => "Configure your Linux setup",
        DiskSelectionViewModel   => "Choose installation disk",
        FileStagingViewModel     => "Staging files",
        UsbWriterViewModel       => "Write to USB",
        _                        => string.Empty,
    };

    /// <summary>
    /// <c>true</c> when the user is on the last wizard step.
    /// Used in <c>MainWindow.xaml</c> to swap the "Next →" button label to "Finish".
    /// </summary>
    public bool IsLastStep => _stepIndex == _steps.Count - 1;

    // ── Constructor ──────────────────────────────────────────────────────────

    public MainWindowViewModel(
        WelcomeViewModel         welcome,
        PreflightViewModel       preflight,
        DistroSelectionViewModel distroSelection,
        IsoAcquisitionViewModel  isoAcquisition,
        MigrationSetupViewModel  migrationSetup,
        DiskSelectionViewModel   diskSelection,
        FileStagingViewModel     fileStaging,
        UsbWriterViewModel       usbWriter)
    {
        _preflight       = preflight;
        _distroSelection = distroSelection;
        _isoAcquisition  = isoAcquisition;
        _migrationSetup  = migrationSetup;
        _diskSelection   = diskSelection;
        _fileStaging     = fileStaging;
        _usbWriter       = usbWriter;

        _steps = [welcome, preflight, distroSelection, isoAcquisition, migrationSetup, diskSelection, fileStaging, usbWriter];
        _stepIndex   = 0;
        _currentPage = _steps[0];

        // Relay CanProceed / completion changes so CanGoNext stays in sync.
        preflight.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PreflightViewModel.CanProceed))
                OnPropertyChanged(nameof(CanGoNext));
        };

        distroSelection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DistroSelectionViewModel.CanProceed)
             || e.PropertyName is nameof(DistroSelectionViewModel.SelectedItem))
                OnPropertyChanged(nameof(CanGoNext));
        };

        isoAcquisition.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsoAcquisitionViewModel.IsComplete)
             || e.PropertyName is nameof(IsoAcquisitionViewModel.HasError))
                OnPropertyChanged(nameof(CanGoNext));
        };

        migrationSetup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MigrationSetupViewModel.CanProceed))
                OnPropertyChanged(nameof(CanGoNext));
        };

        diskSelection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DiskSelectionViewModel.CanProceed))
                OnPropertyChanged(nameof(CanGoNext));
        };

        fileStaging.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FileStagingViewModel.IsComplete)
             || e.PropertyName is nameof(FileStagingViewModel.HasError))
                OnPropertyChanged(nameof(CanGoNext));
        };

        usbWriter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UsbWriterViewModel.IsComplete)
             || e.PropertyName is nameof(UsbWriterViewModel.HasError))
                OnPropertyChanged(nameof(CanGoNext));
        };
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private static void Quit() => System.Windows.Application.Current.Shutdown();

    [RelayCommand]
    private void Back()
    {
        if (_stepIndex <= 0) return;
        _stepIndex--;
        CurrentPage = _steps[_stepIndex];
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        // On the last step (USB Writer) with a completed write, "Finish" shuts down.
        if (_stepIndex == _steps.Count - 1)
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        _stepIndex++;
        CurrentPage = _steps[_stepIndex];

        switch (CurrentPage)
        {
            // Auto-start system check when first navigating to the preflight step.
            case PreflightViewModel pf when pf.Report is null && !pf.IsRunning:
                await pf.RunCheckCommand.ExecuteAsync(null);
                break;

            // Refresh distro compatibility whenever the user arrives at the selection step.
            case DistroSelectionViewModel ds:
                ds.RefreshCompatibility(_preflight.Report);
                break;

            // Prepare + auto-start download when navigating to the acquisition step.
            case IsoAcquisitionViewModel acq when !acq.IsRunning && !acq.IsComplete:
                acq.Prepare(_distroSelection.SelectedDistro!);
                await acq.AcquireCommand.ExecuteAsync(null);
                break;

            // Populate disk list when navigating to the disk selection step.
            case DiskSelectionViewModel disk:
                disk.Prepare(_preflight.Report!);
                break;

            // Auto-start file staging when navigating to that step.
            case FileStagingViewModel fs when !fs.IsRunning && !fs.IsComplete:
                fs.Prepare(_migrationSetup, _preflight.Report!,
                    _distroSelection.SelectedDistro!, _diskSelection.SelectedDisk);
                await fs.StageCommand.ExecuteAsync(null);
                break;

            // Populate USB drive list when navigating to the write step.
            case UsbWriterViewModel usb when !usb.IsRunning && !usb.IsComplete:
                usb.Prepare(_isoAcquisition.Result!, _fileStaging.Result!);
                await usb.RefreshDrivesCommand.ExecuteAsync(null);
                break;
        }
    }

    // ── Property-change hooks ─────────────────────────────────────────────────

    partial void OnCurrentPageChanged(object value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(StepDescription));
        OnPropertyChanged(nameof(IsLastStep));
    }
}
