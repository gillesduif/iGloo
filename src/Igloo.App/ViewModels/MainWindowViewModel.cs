using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Igloo.App.ViewModels;

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
    // null whenever the list is rebuilt  which NRE'd fs.Prepare on distro.Id.
    private DistroManifest? _selectedDistro;

    private int _stepIndex;

    [ObservableProperty]
    private object _currentPage;

    //   Notifications
    // For results the user should be TOLD but never has to act on - a passing
    // signature check, for instance. Parking on a "verified" screen just to collect a
    // Next click adds a step without adding a decision, so the wizard reports the
    // outcome as a Windows toast and moves on.

    /// <summary>
    /// Describes what was actually proven about the downloaded image.
    /// </summary>
    /// <remarks>
    /// Deliberately specific about WHICH checks passed. "Verified" alone is the kind of
    /// reassurance that means nothing; a signature check is the one thing standing
    /// between the user and a tampered mirror, so it is worth naming.
    /// </remarks>
    private static string BuildVerificationMessage(IsoAcquisitionViewModel acq)
    {
        var name = acq.Result is null ? "Image" : Path.GetFileName(acq.Result.LocalPath);
        return acq.Result switch
        {
            { Sha256Verified: true, GpgVerified: true } =>
                $"{name} verified - SHA-256 matched and the GPG signature is valid.",
            { Sha256Verified: true, GpgVerified: false } =>
                $"{name} verified - SHA-256 matched (no GPG signature was published).",
            _ => $"{name} downloaded.",
        };
    }

    /// <summary>Raises a Windows toast; never lets a cosmetic failure disturb the wizard.</summary>
    private static void Notify(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException
                                      or PlatformNotSupportedException or TypeLoadException)
        {
            // Toasts depend on machine-level state we do not control - notification
            // policy, the shell, an unregistered COM server on a stripped-down Windows.
            // The wizard has already moved on either way, so a missed toast must never
            // become an exception on the navigation path.
            Debug.WriteLine($"Toast suppressed: {ex.Message}");
        }
    }

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

    public string PrimaryActionLabel => CurrentPage switch
    {
        DirectInstallViewModel di => di.IsRebooting ? "Rebooting…" : "Reboot  ↻",
        _ when IsLastStep => "Finish",
        _ => "Next  →",
    };

    /// <summary>
    /// Whether the shell shows its page heading above the current step.
    /// </summary>
    /// <remarks>
    /// Every step gets one except Welcome, which carries its own branded hero - logo,
    /// wordmark and tagline. A "Welcome" heading directly above a header that already
    /// says iGloo says the same thing twice and costs vertical space the feature grid
    /// needs. The rail already marks which step you are on.
    /// </remarks>
    public bool ShowStepTitle => CurrentPage is not WelcomeViewModel;

    public string StepDescription => CurrentPage switch
    {
        WelcomeViewModel => "Welcome",
        PreflightViewModel => "System Check",
        DistroSelectionViewModel => "Linux distribution",
        IsoAcquisitionViewModel => "Download",
        MigrationSetupViewModel => "Configuration ",
        DiskSelectionViewModel => "Target Disk",
        FileStagingViewModel => "Data Migration ",
        DirectInstallViewModel => "Installation",
        UsbWriterViewModel => "Write to USB",
        _ => string.Empty,
    };

    
    public bool IsLastStep =>
        CurrentPage is UsbWriterViewModel && _diskSelection.InstallMode == DiskInstallMode.ReplaceDisk
        || CurrentPage is DirectInstallViewModel;


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

    private static readonly (string Title, string Glyph)[] StepTitles =
    [
        ("Welcome", ""),  // Home
        ("System Check", ""),  // Diagnostic
        ("Linux Distribution", ""),  // All apps
        ("Download", ""),  // Download
        ("Configuration", ""),  // Settings
        ("Target Disk", ""),  // Hard drive
        ("Data Migration ", ""),  // Copy
        ("Installation", ""),  // Play / go
    ];

    
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
        // they leave it  and keeps it even if the list later rebuilds.
        if (_distroSelection.SelectedDistro is { } picked)
            _selectedDistro = picked;

        _stepIndex++;

        //   Branch: after FileStagingViewModel, jump to the right install step  
        if (_steps[_stepIndex - 1] is FileStagingViewModel)
        {
            // Dual-boot skips UsbWriterViewModel; the USB path skips DirectInstallViewModel.
            _stepIndex = _steps.IndexOf(
                _diskSelection.InstallMode == DiskInstallMode.DualBoot ? _directInstall : _usbWriter);
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

                // A passing verification is information, not a decision: the receipt
                // screen only ever collects a Next click. Report the outcome in a
                // banner and carry on. A FAILED check is the opposite - it stays on
                // screen, because that one the user genuinely must see and act on.
                if (acq.IsComplete && !acq.HasError)
                {
                    Notify("Download verified", BuildVerificationMessage(acq));
                    await NextAsync();
                }
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

                // Nothing on the staging page asks the user anything: it reports
                // progress, and when the package is built the only available action is
                // Next. Advance automatically so a finished package moves straight on
                // instead of parking on a dead-end screen waiting for an inevitable
                // click. On error we stay put, so the failure stays on screen.
                if (fs.IsComplete && !fs.HasError)
                    await NextAsync();
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
        OnPropertyChanged(nameof(ShowStepTitle));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(StepMarkers));
    }
}


public sealed record StepMarker(int Number, string Title, string Glyph, bool IsDone, bool IsCurrent);
