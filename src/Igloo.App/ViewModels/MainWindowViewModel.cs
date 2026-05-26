using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Igloo.App.ViewModels;

/// <summary>
/// Orchestrates the linear wizard flow with a branch at the final install step:
///   • Dual boot  → DirectInstallViewModel  (no USB needed)
///   • Replace    → UsbWriterViewModel       (USB required)
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
    private readonly DirectInstallViewModel   _directInstall;
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
        DirectInstallViewModel         => false,  // user reboots — no "Next"
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
        DirectInstallViewModel   => "Install without USB",
        UsbWriterViewModel       => "Write to USB",
        _                        => string.Empty,
    };

    /// <summary>True on the last wizard step — swaps "Next" label to "Finish".</summary>
    public bool IsLastStep =>
        CurrentPage is UsbWriterViewModel && _diskSelection.InstallMode == Igloo.Core.Abstractions.DiskInstallMode.ReplaceDisk
        || CurrentPage is DirectInstallViewModel;

    // ── Constructor ──────────────────────────────────────────────────────────

    public MainWindowViewModel(
        WelcomeViewModel         welcome,
        PreflightViewModel       preflight,
        DistroSelectionViewModel distroSelection,
        IsoAcquisitionViewModel  isoAcquisition,
        MigrationSetupViewModel  migrationSetup,
        DiskSelectionViewModel   diskSelection,
        FileStagingViewModel     fileStaging,
        DirectInstallViewModel   directInstall,
        UsbWriterViewModel       usbWriter)
    {
        _preflight       = preflight;
        _distroSelection = distroSelection;
        _isoAcquisition  = isoAcquisition;
        _migrationSetup  = migrationSetup;
        _diskSelection   = diskSelection;
        _fileStaging     = fileStaging;
        _directInstall   = directInstall;
        _usbWriter       = usbWriter;

        // The step list ends with TWO install pages — only one is ever shown.
        _steps = [welcome, preflight, distroSelection, isoAcquisition, migrationSetup,
                  diskSelection, fileStaging, directInstall, usbWriter];
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

        // Skip over the hidden install page when going back.
        _stepIndex--;
        if (_steps[_stepIndex] is DirectInstallViewModel && _diskSelection.InstallMode != Igloo.Core.Abstractions.DiskInstallMode.DualBoot)
            _stepIndex--;
        if (_steps[_stepIndex] is UsbWriterViewModel && _diskSelection.InstallMode != Igloo.Core.Abstractions.DiskInstallMode.ReplaceDisk)
            _stepIndex--;

        CurrentPage = _steps[_stepIndex];
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        // Last step: USB writer "Finish" shuts down.
        if (CurrentPage is UsbWriterViewModel && _usbWriter.IsComplete)
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        _stepIndex++;

        // ── Branch: after FileStagingViewModel, jump to the right install step ──
        if (_steps[_stepIndex - 1] is FileStagingViewModel)
        {
            if (_diskSelection.InstallMode == Igloo.Core.Abstractions.DiskInstallMode.DualBoot)
            {
                // Skip UsbWriterViewModel — land on DirectInstallViewModel.
                _stepIndex = _steps.IndexOf(_directInstall);
            }
            else
            {
                // Skip DirectInstallViewModel — land on UsbWriterViewModel.
                _stepIndex = _steps.IndexOf(_usbWriter);
            }
        }

        CurrentPage = _steps[_stepIndex];

        switch (CurrentPage)
        {
            case PreflightViewModel pf when pf.Report is null && !pf.IsRunning:
                await pf.RunCheckCommand.ExecuteAsync(null);
                break;

            case DistroSelectionViewModel ds:
                ds.RefreshCompatibility(_preflight.Report);
                break;

            case IsoAcquisitionViewModel acq when !acq.IsRunning && !acq.IsComplete:
                acq.Prepare(_distroSelection.SelectedDistro!);
                await acq.AcquireCommand.ExecuteAsync(null);
                break;

            case DiskSelectionViewModel disk:
                disk.Prepare(_preflight.Report!);
                break;

            case FileStagingViewModel fs when !fs.IsRunning && !fs.IsComplete:
                fs.Prepare(_migrationSetup, _preflight.Report!,
                    _distroSelection.SelectedDistro!,
                    _diskSelection.SelectedDisk,
                    _diskSelection.InstallMode,
                    _diskSelection.LinuxSizeGb);
                await fs.StageCommand.ExecuteAsync(null);
                break;

            case DirectInstallViewModel di when !di.IsRunning && !di.IsComplete:
                di.Prepare(_isoAcquisition.Result!, _fileStaging.Result!,
                    _diskSelection.SelectedDisk!,
                    _diskSelection.LinuxSizeGb,
                    _distroSelection.SelectedDistro?.Iso.Stage2Url);
                await di.InstallCommand.ExecuteAsync(null);
                break;

            case UsbWriterViewModel usb when !usb.IsRunning && !usb.IsComplete:
                usb.Prepare(_isoAcquisition.Result!, _fileStaging.Result!,
                    _diskSelection.SelectedDisk,
                    _diskSelection.InstallMode,
                    _diskSelection.LinuxSizeGb);
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
