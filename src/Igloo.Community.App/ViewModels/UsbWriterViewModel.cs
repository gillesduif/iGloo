using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class UsbWriterViewModel : ObservableObject
{
    private readonly IUsbPreparation _preparation;
    private readonly ILogger<UsbWriterViewModel> _logger;

    private string? _isoPath;
    private string? _isoSha256;
    private IsoAcquisitionResult? _verifiedIso;
    private string? _distroId;
    private string? _stagingDirectory;
    private DiskInfo? _targetDisk;
    private DiskInstallMode _installMode = DiskInstallMode.DualBoot;
    private int _linuxSizeGb;

    //   Observable state                            

    [ObservableProperty]
    private ObservableCollection<UsbDriveInfo> _drives = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    [NotifyPropertyChangedFor(nameof(HasUnsupportedDrive))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private UsbDriveInfo? _selectedDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _isEnumerating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    [NotifyPropertyChangedFor(nameof(IsCancelable))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _isComplete;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _hasError;
    [ObservableProperty] private string _errorTitle = "Review USB drive";
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGrubPatchNote))]
    private string? _grubPatchNote;

    public bool HasGrubPatchNote => !string.IsNullOrEmpty(GrubPatchNote);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    private string? _errorDetail;

    public bool HasErrorDetail => !string.IsNullOrEmpty(ErrorDetail);

    [ObservableProperty] private string? _phaseDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCancelable))]
    private UsbWritePhase _currentPhase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesWritten;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesTotal;

    //   Derived                                ─

    public bool CanWrite => SelectedDrive is { LogicalSectorBytes: 512 } drive &&
        _isoSha256 is not null && _verifiedIso is not null &&
        !string.IsNullOrWhiteSpace(_distroId) &&
        _targetDisk is { GptDiskGuid: { } targetGuid } target && targetGuid != Guid.Empty &&
        string.Equals(target.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(target.DeviceId, drive.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        _installMode == DiskInstallMode.DualBoot && _linuxSizeGb > 0 &&
        !IsRunning && !IsEnumerating && !IsComplete && !HasError;

    public bool HasUnsupportedDrive => SelectedDrive is { LogicalSectorBytes: not 512 };

    public bool IsCancelable => !IsRunning || CurrentPhase != UsbWritePhase.CreatingOemdrv;

    public double ProgressPercent => BytesTotal > 0 ? BytesWritten * 100.0 / BytesTotal : 0;
    public bool IsProgressIndeterminate => BytesTotal == 0;

    //   Constructor                              ─

    public UsbWriterViewModel(
        IUsbPreparation preparation,
        ILogger<UsbWriterViewModel> logger)
    {
        _preparation = preparation;
        _logger = logger;
    }

    //   API called by MainWindowViewModel                   ─

    public void Prepare(
        IsoAcquisitionResult isoResult,
        FileStagingResult stagingResult,
        DiskInfo? targetDisk = null,
        DiskInstallMode installMode = DiskInstallMode.DualBoot,
        int linuxSizeGb = 0,
        string? distroId = null)
    {
        ArgumentNullException.ThrowIfNull(isoResult);
        ArgumentNullException.ThrowIfNull(stagingResult);
        if (installMode != DiskInstallMode.DualBoot)
            throw new ArgumentOutOfRangeException(nameof(installMode),
                "iGloo Community always installs alongside Windows.");

        _isoPath = isoResult.LocalPath;
        _isoSha256 = isoResult.Sha256Verified ? isoResult.VerifiedSha256 : null;
        _verifiedIso = isoResult;
        _distroId = distroId;
        _stagingDirectory = stagingResult.StagingDirectory;
        _targetDisk = targetDisk;
        _installMode = installMode;
        _linuxSizeGb = linuxSizeGb;
        // A drive selected for an earlier wizard run is not current evidence for
        // this image and target. Require a fresh enumeration before enabling Write.
        SelectedDrive = null;
        Drives.Clear();
        OnPropertyChanged(nameof(CanWrite));
        WriteCommand.NotifyCanExecuteChanged();

        IsComplete = false;
        HasError = false;
        ErrorTitle = "Review USB drive";
        ErrorMessage = null;
        ErrorDetail = null;
        PhaseDisplay = null;
        BytesWritten = 0;
        BytesTotal = 0;
        IsRunning = false;
        CurrentPhase = UsbWritePhase.WritingIso;
        GrubPatchNote = null;
    }

    //   Commands                                

    
    [RelayCommand]
    private async Task RefreshDrivesAsync(CancellationToken ct)
    {
        // A completed receipt names the exact drive that was written. Refreshing
        // after success must not clear or replace that drive under the receipt.
        if (IsRunning || IsEnumerating || IsComplete)
            return;
        SelectedDrive = null;
        Drives.Clear();
        HasError = false;
        ErrorMessage = null;
        ErrorDetail = null;
        IsEnumerating = true;
        try
        {
            var found = await _preparation.EnumerateDrivesAsync(ct);
            foreach (var d in found)
                Drives.Add(d);

            _logger.LogInformation("Drive enumeration complete: {Count} USB drive(s) found", Drives.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("USB drive enumeration cancelled (navigated away)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to enumerate USB drives");
            HasError = true;
            ErrorTitle = "Refresh USB drives";
            ErrorMessage = "Select Refresh to scan for USB drives again. " +
                           "Keep the drive connected and check that Windows can see it.";
            ErrorDetail = BuildErrorDetail(ex);
        }
        finally
        {
            IsEnumerating = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanWrite))]
    private async Task WriteAsync(CancellationToken ct)
    {
        if (SelectedDrive is null || _isoPath is null || _stagingDirectory is null ||
            _isoSha256 is null || _verifiedIso is null || string.IsNullOrWhiteSpace(_distroId))
            return;

        IsRunning = true;
        HasError = false;
        ErrorTitle = "Review USB drive";
        ErrorMessage = null;
        ErrorDetail = null;
        BytesWritten = 0;
        BytesTotal = 0;
        PhaseDisplay = "Preparing…";

        var progress = new Progress<UsbWriteProgress>(p =>
        {
            BytesWritten = p.BytesWritten;
            BytesTotal = p.BytesTotal;
            CurrentPhase = p.Phase;    // drives IsCancelable and cancel-message logic
            PhaseDisplay = p.Phase switch
            {
                UsbWritePhase.ShrinkingPartition => "Shrinking Windows partition…",
                UsbWritePhase.WritingIso => "Writing installer image…",
                UsbWritePhase.CreatingOemdrv => "Creating OEMDRV partition…",
                UsbWritePhase.PatchingGrub => "Patching GRUB configuration…",
                UsbWritePhase.CopyingFiles => "Copying migration files…",
                UsbWritePhase.Complete => "Complete",
                _ => string.Empty,
            };

            // Capture the last GRUB patch message so it is displayed on completion.
            if (p.Phase == UsbWritePhase.PatchingGrub && !string.IsNullOrEmpty(p.Message))
                GrubPatchNote = p.Message;
        });

        try
        {
            var input = new UsbPreparationInput(SelectedDrive, _isoPath,
                _stagingDirectory, _targetDisk, _installMode, _linuxSizeGb, _isoSha256!)
            {
                DistroId = _distroId,
                VerifiedIso = _verifiedIso,
            };
            // USB verification and writing precede any Windows partition resize.
            if (_installMode == DiskInstallMode.DualBoot && _targetDisk is not null && _linuxSizeGb > 0)
            {
                var shrinkProgress = new Progress<string>(msg =>
                {
                    PhaseDisplay = msg;
                    _logger.LogInformation("[resize] {Message}", msg);
                });

                await _preparation.WriteAsync(input, progress, shrinkProgress, ct);
            }
            else
                await _preparation.WriteAsync(input, progress, null, ct);

            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("USB write cancelled at phase {Phase}", CurrentPhase);

            HasError = true;
            ErrorTitle = "Refresh USB drive";
            ErrorMessage = CurrentPhase == UsbWritePhase.WritingIso
                // The Fedora route can still be verifying inputs here; no USB
                // mutation has been reported, so do not claim a truncated image.
                ? "Select Refresh, reselect the USB drive, then start a new write. " +
                  "The cancelled preparation has no verified installer result."
                : CurrentPhase == UsbWritePhase.ShrinkingPartition
                ? "Select Refresh and recheck the target disk before trying again. " +
                  "USB preparation completed, but Windows allocation was interrupted."
                : "Select Refresh and rewrite the USB drive before booting it. " +
                  "The cancelled write may have left incomplete installer files.";
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "USB write failed - insufficient rights");
            HasError = true;
            ErrorTitle = "Refresh USB drive";
            ErrorMessage = "Select Refresh and recheck that Windows can access the same USB drive, " +
                           "then select Write to USB for a new write. Do not boot from this drive until it completes.";
            ErrorDetail = BuildErrorDetail(ex);
        }
        catch (NotSupportedException ex) when (_distroId?.StartsWith("fedora-", StringComparison.OrdinalIgnoreCase) == true
            && ex.Message.Contains("Turn off saved Wi-Fi migration", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Fedora saved Wi-Fi migration was rejected before USB writing");
            HasError = true;
            ErrorTitle = "Turn off saved Wi-Fi migration";
            ErrorMessage = "Select Back until Configuration, turn off saved Wi-Fi migration, " +
                           "then continue to restage before writing USB. " +
                           "Use wired Ethernet or an open Wi-Fi network for this Fedora netinstall.";
            ErrorDetail = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "USB write failed");
            HasError = true;
            ErrorTitle = "Review installer inputs";
            ErrorMessage = "Select Refresh and recheck the USB drive before trying again. " +
                           "Use Back to review the installer inputs if the failure repeats. " +
                           "Do not boot from this drive until a new write completes.";
            ErrorDetail = BuildErrorDetail(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    
    [RelayCommand]
    private static void Finish() => Application.Current.Shutdown();

    //   Error formatting                            

    private static string BuildErrorDetail(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        var depth = 0;

        while (current is not null)
        {
            if (depth > 0)
                sb.AppendLine().AppendLine("  inner exception                ─");

            sb.Append('[').Append(current.GetType().FullName).AppendLine("]");
            sb.AppendLine(current.Message);

            if (current is System.ComponentModel.Win32Exception w32)
                sb.Append("Win32 error code: ").Append(w32.NativeErrorCode)
                  .Append(" (0x").Append(w32.NativeErrorCode.ToString("X8", CultureInfo.InvariantCulture)).AppendLine(")");

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                sb.AppendLine();
                sb.AppendLine(current.StackTrace);
            }

            current = current.InnerException;
            depth++;
        }

        return sb.ToString().TrimEnd();
    }
}
