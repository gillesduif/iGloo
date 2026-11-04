using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Igloo.Community.App.ViewModels;

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

    public bool IsCheckingTargetDisk => CurrentPage is DiskSelectionViewModel { IsCheckingSpace: true };

    public bool CanGoBack => _stepIndex > 0 && (CurrentPage switch
    {
        PreflightViewModel pf => !pf.IsRunning && !pf.IsRemovingLinux,
        IsoAcquisitionViewModel acq => !acq.IsRunning,
        FileStagingViewModel fs => !fs.IsRunning,
        // A completed direct preparation may already own persistent storage and
        // boot state. Re-entering it would implicitly repeat that operation.
        DirectInstallViewModel di => !di.IsRunning && !di.IsComplete,
        UsbWriterViewModel usb => !usb.IsRunning && !usb.IsEnumerating,
        _ => true,
    });

    // Closing the window must wait for every active operation, including one
    // that has not yet settled after the displayed page changed.
    public bool IsCloseBlocked => _preflight.IsRemovingLinux || _isoAcquisition.IsRunning ||
        _fileStaging.IsRunning || _directInstall.IsRunning || _directInstall.IsRebooting ||
        _usbWriter.IsRunning;

    public bool CanGoNext => CurrentPage switch
    {
        WelcomeViewModel => true,
        PreflightViewModel pf => pf.CanProceed && !pf.IsRunning,
        DistroSelectionViewModel ds => ds.CanProceed,
        IsoAcquisitionViewModel acq => acq.IsComplete && acq.Result is not null &&
                                      !acq.HasError && !acq.IsRunning,
        MigrationSetupViewModel setup => setup.CanProceed,
        DiskSelectionViewModel disk => disk.CanProceed,
        FileStagingViewModel fs => fs.IsComplete && fs.Result is not null &&
                                   !fs.HasError && !fs.IsRunning,
        // A boot-registration failure leaves the prepared installer available for
        // another Restart attempt; it must not repeat partition preparation.
        DirectInstallViewModel di => di.IsComplete && !di.IsRunning && !di.IsRebooting,
        _ => false,
    };

    public string PrimaryActionLabel => CurrentPage switch
    {
        DirectInstallViewModel di => di.IsRebooting ? "Restarting…" : di.IsComplete ? "Restart" :
            di.IsRunning ? "Preparing…" : di.HasError ? "Preparation failed" : "Preparing installation…",
        _ when IsLastStep => "Finish",
        _ => "Next  →",
    };

    /// <summary>Segoe Fluent Icons glyph shown before the label, empty for none.</summary>
    /// <remarks>
    /// Only the restart step gets one. "Next" keeps its arrow inside the label,
    /// where it trails the word and points the way the wizard is going; a leading
    /// glyph there would point backwards at the text it precedes.
    /// </remarks>
    public string PrimaryActionGlyph => CurrentPage switch
    {
        DirectInstallViewModel => "\uE7E8",  // PowerButton
        _ => string.Empty,
    };

    public bool HasPrimaryActionGlyph => PrimaryActionGlyph.Length > 0;

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

    
    public bool IsLastStep => CurrentPage is DirectInstallViewModel;


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

        // Community always installs alongside the existing Windows installation.
        _steps = [welcome, preflight, distroSelection, isoAcquisition, migrationSetup,
                  diskSelection, fileStaging, directInstall];
        _stepIndex = 0;
        _currentPage = _steps[0];

        // Relay CanProceed / completion changes so CanGoNext stays in sync.
        RefreshCanGoNextWhenChanged(preflight,
            nameof(PreflightViewModel.CanProceed), nameof(PreflightViewModel.IsRunning));
        RefreshCanGoNextWhenChanged(distroSelection,
            nameof(DistroSelectionViewModel.CanProceed), nameof(DistroSelectionViewModel.SelectedItem));
        RefreshCanGoNextWhenChanged(isoAcquisition,
            nameof(IsoAcquisitionViewModel.IsComplete), nameof(IsoAcquisitionViewModel.HasError),
            nameof(IsoAcquisitionViewModel.IsRunning));
        RefreshCanGoNextWhenChanged(migrationSetup, nameof(MigrationSetupViewModel.CanProceed));
        RefreshCanGoNextWhenChanged(diskSelection, nameof(DiskSelectionViewModel.CanProceed));
        diskSelection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiskSelectionViewModel.IsCheckingSpace))
                OnPropertyChanged(nameof(IsCheckingTargetDisk));
        };
        RefreshCanGoNextWhenChanged(fileStaging,
            nameof(FileStagingViewModel.IsComplete), nameof(FileStagingViewModel.HasError),
            nameof(FileStagingViewModel.IsRunning));
        RefreshCanGoNextWhenChanged(directInstall,
            nameof(DirectInstallViewModel.IsComplete),
            nameof(DirectInstallViewModel.HasError),
            nameof(DirectInstallViewModel.IsRunning),
            nameof(DirectInstallViewModel.IsRebooting),
            nameof(DirectInstallViewModel.IsReadyToInstall));
        RefreshCanGoNextWhenChanged(usbWriter,
            nameof(UsbWriterViewModel.IsComplete), nameof(UsbWriterViewModel.HasError),
            nameof(UsbWriterViewModel.IsRunning));
        foreach (var activeStep in new ObservableObject[]
                 { preflight, isoAcquisition, fileStaging, directInstall, usbWriter })
            activeStep.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PreflightViewModel.IsRunning) or
                    nameof(PreflightViewModel.IsRemovingLinux) or
                    nameof(DirectInstallViewModel.IsComplete) or
                    nameof(UsbWriterViewModel.IsEnumerating))
                    OnPropertyChanged(nameof(CanGoBack));
            };
    }

    private void RefreshCanGoNextWhenChanged(ObservableObject stepViewModel, params string[] propertyNames)
        => stepViewModel.PropertyChanged += (_, e) =>
        {
            if (propertyNames.Contains(e.PropertyName))
            {
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(PrimaryActionLabel));
                OnPropertyChanged(nameof(PrimaryActionGlyph));
                OnPropertyChanged(nameof(HasPrimaryActionGlyph));
            }
        };

    //   Commands                               ─

    [RelayCommand]
    private static void Quit() => Application.Current.MainWindow?.Close();

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack)
            return;

        if (CurrentPage is DiskSelectionViewModel selection)
            selection.CancelSpaceCheck();

        _stepIndex--;

        CurrentPage = _steps[_stepIndex];
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!CanGoNext)
            return;

        if (CurrentPage is DiskSelectionViewModel selection)
        {
            var chosenDisk = selection.SelectedDisk;
            var chosenMode = selection.InstallMode;
            var chosenRootGb = selection.LinuxSizeGb;
            await selection.RefreshSpaceAsync(preserveSelection: true);
            // The cards and allocation controls remain usable during this read-only
            // check. A change belongs to a new review, even if the new disk has
            // the same space limit and its own check has already completed.
            // A changed limit may also make WPF clamp the slider.
            if (!ReferenceEquals(CurrentPage, selection) || !selection.CanProceed ||
                !ReferenceEquals(selection.SelectedDisk, chosenDisk) ||
                selection.InstallMode != chosenMode ||
                selection.LinuxSizeGb != chosenRootGb)
                return;
        }

        // Last step of the dual-boot path: Restart registers the prepared
        // installer boot entry before requesting the Windows restart.
        if (CurrentPage is DirectInstallViewModel directInstall)
        {
            await directInstall.RebootToInstallCommand.ExecuteAsync(null);
            return;
        }

        // Lock in the chosen distro before advancing. CanGoNext gates the distro
        // step on a valid selection, so this captures the user's pick the moment
        // they leave it  and keeps it even if the list later rebuilds.
        if (CurrentPage is DistroSelectionViewModel &&
            _distroSelection.SelectedDistro is { } picked)
            _selectedDistro = picked;

        _stepIndex++;

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

            case IsoAcquisitionViewModel acq when !acq.IsRunning:
                var needsAcquisition = !acq.IsComplete ||
                    !string.Equals(acq.Distro?.Id, _selectedDistro?.Id,
                        StringComparison.Ordinal);
                if (needsAcquisition)
                {
                    acq.Prepare(_selectedDistro!);
                    await acq.AcquireCommand.ExecuteAsync(null);
                }

                // A passing verification is information, not a decision: the receipt
                // screen only ever collects a Next click. This also applies when
                // returning from distribution selection to the same verified image.
                // Report a new verification in a banner; a failed check stays here.
                if (acq.IsComplete && !acq.HasError)
                {
                    if (needsAcquisition)
                        Notify("Download verified", BuildVerificationMessage(acq));
                    await NextAsync();
                }
                break;

            case DiskSelectionViewModel disk:
                disk.Prepare(_preflight.Report!, _selectedDistro, _isoAcquisition.Result,
                    new FileStagingRequest(_selectedDistro!.Id, _migrationSetup.GetSelectedFolderPaths())
                    {
                        FolderNames = _migrationSetup.GetSelectedFolderNames(),
                        BrowserRelativePaths = _migrationSetup.GetSelectedBrowsers()
                            .Where(browser => !string.IsNullOrWhiteSpace(browser.SourceRelativePath))
                            .Select(browser => browser.SourceRelativePath).ToArray(),
                        BrowserProfiles = _migrationSetup.GetSelectedBrowsers()
                            .Where(browser => !string.IsNullOrWhiteSpace(browser.SourceRelativePath)).ToArray(),
                    });
                break;


            case MigrationSetupViewModel setup:
                setup.ConfigureDistro(_selectedDistro!.Id);
                break;

            // Revisiting setup or target selection can change the account, files,
            // distribution or exact disk. Build a fresh package for those inputs.
            case FileStagingViewModel fs when !fs.IsRunning:
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
                    _selectedDistro!.Id, _selectedDistro.Iso.Stage2Url);
                await di.InstallCommand.ExecuteAsync(null);
                break;

        }
    }

    //   Property-change hooks                         ─

    partial void OnCurrentPageChanged(object value)
    {
        OnPropertyChanged(nameof(IsCheckingTargetDisk));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(PrimaryActionGlyph));
        OnPropertyChanged(nameof(HasPrimaryActionGlyph));
        OnPropertyChanged(nameof(StepDescription));
        OnPropertyChanged(nameof(ShowStepTitle));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(StepMarkers));
    }
}


public sealed record StepMarker(int Number, string Title, string Glyph, bool IsDone, bool IsCurrent);
