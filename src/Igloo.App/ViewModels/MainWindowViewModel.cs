using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.App.ViewModels;

/// <summary>
/// Orchestrates the linear wizard flow with a branch at the final install step:
///   • Dual boot  → DirectInstallViewModel  (no USB needed)
///   • Replace    → UsbWriterViewModel       (USB required)
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly List<object> _steps;
    private readonly WelcomeViewModel _welcome;
    private readonly PreflightViewModel _preflight;
    private readonly DistroSelectionViewModel _distroSelection;
    private readonly IsoAcquisitionViewModel _isoAcquisition;
    private readonly MigrationSetupViewModel _migrationSetup;
    private readonly DiskSelectionViewModel _diskSelection;
    private readonly FileStagingViewModel _fileStaging;
    private readonly DirectInstallViewModel _directInstall;
    private readonly UsbWriterViewModel _usbWriter;

    // Captured the moment the user advances past distro selection. Downstream steps
    // (ISO download, staging, install) must NOT read DistroSelection.SelectedDistro
    // live: it is SelectedItem?.Manifest, and WPF resets the bound SelectedItem to
    // null whenever the list is rebuilt — which NRE'd fs.Prepare on distro.Id.
    private DistroManifest? _selectedDistro;

    private int _stepIndex;

    [ObservableProperty]
    private object _currentPage;

    //   Computed navigation state                       

    public bool CanGoBack => _stepIndex > 0;

    public bool CanGoNext => CurrentPage switch
    {
        WelcomeViewModel => true,
        PreflightViewModel pf => pf.CanProceed,
        DistroSelectionViewModel ds => ds.CanProceed,
        IsoAcquisitionViewModel acq => acq.IsComplete && !acq.HasError,
        MigrationSetupViewModel setup => setup.CanProceed,
        DiskSelectionViewModel disk => disk.CanProceed,
        FileStagingViewModel fs => fs.IsComplete && !fs.HasError,
        // The dual-boot path ends by rebooting into the installer: the forward
        // button becomes "Reboot" once preparation succeeds (and locks while it fires).
        DirectInstallViewModel di => di.IsComplete && !di.HasError && !di.IsRebooting,
        UsbWriterViewModel usb => usb.IsComplete && !usb.HasError,
        _ => false,
    };

    /// <summary>
    /// Label on the wizard's forward button. Rebooting into the installer is the last
    /// navigation step of the dual-boot path, not a separate action, so the button is
    /// simply renamed rather than duplicated by a second one on the page.
    /// </summary>
    public string PrimaryActionLabel => CurrentPage switch
    {
        DirectInstallViewModel di => di.IsRebooting ? "Rebooting…" : "Reboot  ↻",
        _ when IsLastStep => "Finish",
        _ => "Next  →",
    };

    public string StepDescription => CurrentPage switch
    {
        WelcomeViewModel => "Welcome",
        PreflightViewModel => "System check",
        DistroSelectionViewModel => "Choose your Linux distribution",
        IsoAcquisitionViewModel => "Downloading installer",
        MigrationSetupViewModel => "Configure your Linux setup",
        DiskSelectionViewModel => "Choose installation disk",
        FileStagingViewModel => "Staging files",
        DirectInstallViewModel => "Install without USB",
        UsbWriterViewModel => "Write to USB",
        _ => string.Empty,
    };

    /// <summary>True on the last wizard step - swaps "Next" label to "Finish".</summary>
    public bool IsLastStep =>
        CurrentPage is UsbWriterViewModel && _diskSelection.InstallMode == DiskInstallMode.ReplaceDisk
        || CurrentPage is DirectInstallViewModel;

    //   Step indicator (display only)                     
    // The two install pages share the final slot, so the user-visible journey is
    // always 8 steps regardless of which install path is taken.

    public static int StepCount => 8;

    public int StepNumber => CurrentPage switch
    {
        WelcomeViewModel => 1,
        PreflightViewModel => 2,
        DistroSelectionViewModel => 3,
        IsoAcquisitionViewModel => 4,
        MigrationSetupViewModel => 5,
        DiskSelectionViewModel => 6,
        FileStagingViewModel => 7,
        DirectInstallViewModel => 8,
        UsbWriterViewModel => 8,
        _ => 1,
    };

    /// <summary>Rail label + Fluent (Segoe MDL2) glyph per step, so the left
    /// navigation names every step and gives it a recognisable icon.</summary>
    private static readonly (string Title, string Glyph)[] StepTitles =
    [
        ("Welcome", ""),  // Home
        ("System check", ""),  // Diagnostic
        ("Distribution", ""),  // All apps
        ("Download", ""),  // Download
        ("Your setup", ""),  // Settings
        ("Disk", ""),  // Hard drive
        ("Staging", ""),  // Copy
        ("Install", ""),  // Play / go
    ];

    /// <summary>One marker per wizard step; rebuilt on navigation for the left rail.</summary>
    public IReadOnlyList<StepMarker> StepMarkers =>
        Enumerable.Range(1, StepCount)
            .Select(n => new StepMarker(n, StepTitles[n - 1].Title, StepTitles[n - 1].Glyph,
                                        n < StepNumber, n == StepNumber))
            .ToList();

    //   Constructor                              

    public MainWindowViewModel(
        WelcomeViewModel welcome,
        PreflightViewModel preflight,
        DistroSelectionViewModel distroSelection,
        IsoAcquisitionViewModel isoAcquisition,
        MigrationSetupViewModel migrationSetup,
        DiskSelectionViewModel diskSelection,
        FileStagingViewModel fileStaging,
        DirectInstallViewModel directInstall,
        UsbWriterViewModel usbWriter)
    {
        ArgumentNullException.ThrowIfNull(welcome);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(distroSelection);
        ArgumentNullException.ThrowIfNull(isoAcquisition);
        ArgumentNullException.ThrowIfNull(migrationSetup);
        ArgumentNullException.ThrowIfNull(diskSelection);
        ArgumentNullException.ThrowIfNull(fileStaging);
        ArgumentNullException.ThrowIfNull(directInstall);
        ArgumentNullException.ThrowIfNull(usbWriter);

        _welcome = welcome;
        _preflight = preflight;
        _distroSelection = distroSelection;
        _isoAcquisition = isoAcquisition;
        _migrationSetup = migrationSetup;
        _diskSelection = diskSelection;
        _fileStaging = fileStaging;
        _directInstall = directInstall;
        _usbWriter = usbWriter;

        // The step list ends with TWO install pages - only one is ever shown.
        _steps = [welcome, preflight, distroSelection, isoAcquisition, migrationSetup,
                  diskSelection, fileStaging, directInstall, usbWriter];
        _stepIndex = 0;
        _currentPage = _steps[0];

        // Relay CanProceed / completion changes so CanGoNext stays in sync.
        RefreshCanGoNextWhenChanged(preflight, nameof(PreflightViewModel.CanProceed));
        RefreshCanGoNextWhenChanged(distroSelection,
            nameof(DistroSelectionViewModel.CanProceed), nameof(DistroSelectionViewModel.SelectedItem));
        RefreshCanGoNextWhenChanged(isoAcquisition,
            nameof(IsoAcquisitionViewModel.IsComplete), nameof(IsoAcquisitionViewModel.HasError));
        RefreshCanGoNextWhenChanged(migrationSetup, nameof(MigrationSetupViewModel.CanProceed));
        RefreshCanGoNextWhenChanged(diskSelection, nameof(DiskSelectionViewModel.CanProceed));
        RefreshCanGoNextWhenChanged(fileStaging,
            nameof(FileStagingViewModel.IsComplete), nameof(FileStagingViewModel.HasError));
        RefreshCanGoNextWhenChanged(directInstall,
            nameof(DirectInstallViewModel.IsComplete),
            nameof(DirectInstallViewModel.HasError),
            nameof(DirectInstallViewModel.IsRebooting));
        RefreshCanGoNextWhenChanged(usbWriter,
            nameof(UsbWriterViewModel.IsComplete), nameof(UsbWriterViewModel.HasError));
    }

    private void RefreshCanGoNextWhenChanged(ObservableObject stepViewModel, params string[] propertyNames)
        => stepViewModel.PropertyChanged += (_, e) =>
        {
            if (propertyNames.Contains(e.PropertyName))
            {
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(PrimaryActionLabel));
            }
        };

    //   Commands                               ─

    [RelayCommand]
    private static void Quit() => Application.Current.Shutdown();

    [RelayCommand]
    private void Back()
    {
        if (_stepIndex <= 0)
            return;

        // Skip over the hidden install page when going back.
        _stepIndex--;
        if (_steps[_stepIndex] is DirectInstallViewModel && _diskSelection.InstallMode != DiskInstallMode.DualBoot)
            _stepIndex--;
        if (_steps[_stepIndex] is UsbWriterViewModel && _diskSelection.InstallMode != DiskInstallMode.ReplaceDisk)
            _stepIndex--;

        CurrentPage = _steps[_stepIndex];
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        // Last step: USB writer "Finish" shuts down.
        if (CurrentPage is UsbWriterViewModel && _usbWriter.IsComplete)
        {
            Application.Current.Shutdown();
            return;
        }

        // Last step of the dual-boot path: "Reboot" hands the machine to the installer.
        if (CurrentPage is DirectInstallViewModel directInstall && directInstall.IsComplete)
        {
            await directInstall.RebootToInstallCommand.ExecuteAsync(null);
            return;
        }

        // Lock in the chosen distro before advancing. CanGoNext gates the distro
        // step on a valid selection, so this captures the user's pick the moment
        // they leave it — and keeps it even if the list later rebuilds.
        if (_distroSelection.SelectedDistro is { } picked)
            _selectedDistro = picked;

        _stepIndex++;

        //   Branch: after FileStagingViewModel, jump to the right install step  
        if (_steps[_stepIndex - 1] is FileStagingViewModel)
        {
            if (_diskSelection.InstallMode == DiskInstallMode.DualBoot)
            {
                // Skip UsbWriterViewModel - land on DirectInstallViewModel.
                _stepIndex = _steps.IndexOf(_directInstall);
            }
            else
            {
                // Skip DirectInstallViewModel - land on UsbWriterViewModel.
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
                ds.SetRecommendation(_welcome.RecommendedDistroIds);
                ds.RefreshCompatibility(_preflight.Report);
                break;

            case IsoAcquisitionViewModel acq when !acq.IsRunning && !acq.IsComplete:
                acq.Prepare(_selectedDistro!);
                await acq.AcquireCommand.ExecuteAsync(null);
                break;

            case DiskSelectionViewModel disk:
                disk.Prepare(_preflight.Report!);
                break;

            case FileStagingViewModel fs when !fs.IsRunning && !fs.IsComplete:
                fs.Prepare(_migrationSetup, _preflight.Report!,
                    _selectedDistro!,
                    _diskSelection.SelectedDisk,
                    _diskSelection.InstallMode,
                    _diskSelection.LinuxSizeGb);
                await fs.StageCommand.ExecuteAsync(null);
                break;

            case DirectInstallViewModel di when !di.IsRunning && !di.IsComplete:
                di.Prepare(_isoAcquisition.Result!, _fileStaging.Result!,
                    _diskSelection.SelectedDisk!,
                    _diskSelection.LinuxSizeGb,
                    _selectedDistro!.Id,
                    _selectedDistro?.Iso.Stage2Url);
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

    //   Property-change hooks                         ─

    partial void OnCurrentPageChanged(object value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(StepDescription));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(StepMarkers));
    }
}

/// <summary>A single entry in the wizard's step rail.</summary>
public sealed record StepMarker(int Number, string Title, string Glyph, bool IsDone, bool IsCurrent);
