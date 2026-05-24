using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.App.ViewModels;

/// <summary>
/// View-model for the USB Writer wizard step (M5).
///
/// Flow:
///   1. On navigation, <see cref="Prepare"/> stores paths from prior steps and
///      kicks off <see cref="RefreshDrivesCommand"/> to populate the drive list.
///   2. The user selects a drive and clicks "Write to USB" — <see cref="WriteCommand"/>.
///   3. Progress (ISO write → OEMDRV creation → file copy) is reported live.
///   4. On completion, <see cref="IsComplete"/> is set; the main wizard's
///      "Finish" button becomes active.
/// </summary>
public sealed partial class UsbWriterViewModel : ObservableObject
{
    private readonly IUsbWriterService          _writer;
    private readonly ILogger<UsbWriterViewModel> _logger;

    private string? _isoPath;
    private string? _stagingDirectory;

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<UsbDriveInfo> _drives = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    private UsbDriveInfo? _selectedDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    private bool _isEnumerating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    [NotifyPropertyChangedFor(nameof(IsCancelable))]
    private bool _isRunning;

    [ObservableProperty] private bool          _isComplete;
    [ObservableProperty] private bool          _hasError;
    [ObservableProperty] private string?       _errorMessage;

    /// <summary>
    /// Set after the GRUB patch step completes (success or best-effort skip).
    /// Shown in the "USB drive is ready" panel so the user can confirm
    /// whether <c>nomodeset rd.live.check=0</c> were baked in.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGrubPatchNote))]
    private string? _grubPatchNote;

    public bool HasGrubPatchNote => !string.IsNullOrEmpty(GrubPatchNote);

    /// <summary>
    /// Full technical detail (exception type, message, Win32 error code, stack trace)
    /// displayed in a copyable panel so the developer can diagnose failures without
    /// digging through log files.  <c>null</c> for user-initiated cancellations.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    private string?       _errorDetail;

    public bool HasErrorDetail => !string.IsNullOrEmpty(ErrorDetail);

    [ObservableProperty] private string?       _phaseDisplay;

    /// <summary>
    /// The phase currently executing.  Used to gate the Cancel button:
    /// Phase 2 (<see cref="UsbWritePhase.CreatingOemdrv"/>) is atomic and
    /// cannot be cancelled once started.
    /// </summary>
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

    // ── Derived ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>true</c> when the user has selected a drive and no write is in progress.
    /// Bound to the "Write to USB" button's <c>IsEnabled</c>.
    /// </summary>
    public bool CanWrite => SelectedDrive is not null && !IsRunning && !IsEnumerating && !IsComplete;

    /// <summary>
    /// <c>false</c> during Phase 2 (diskpart), which runs atomically and cannot be
    /// interrupted.  The Cancel button is disabled in that window.
    /// </summary>
    public bool IsCancelable => !IsRunning || CurrentPhase != UsbWritePhase.CreatingOemdrv;

    public double ProgressPercent         => BytesTotal > 0 ? BytesWritten * 100.0 / BytesTotal : 0;
    public bool   IsProgressIndeterminate => BytesTotal == 0;

    // ── Constructor ───────────────────────────────────────────────────────────

    public UsbWriterViewModel(
        IUsbWriterService           writer,
        ILogger<UsbWriterViewModel> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    // ── API called by MainWindowViewModel ─────────────────────────────────────

    /// <summary>
    /// Stores paths produced by prior wizard steps and resets all observable state.
    /// Call this before navigating to this step.
    /// </summary>
    public void Prepare(IsoAcquisitionResult isoResult, FileStagingResult stagingResult)
    {
        _isoPath          = isoResult.LocalPath;
        _stagingDirectory = stagingResult.StagingDirectory;

        IsComplete   = false;
        HasError     = false;
        ErrorMessage = null;
        ErrorDetail  = null;
        PhaseDisplay = null;
        BytesWritten = 0;
        BytesTotal   = 0;
        IsRunning    = false;
        CurrentPhase = UsbWritePhase.WritingIso;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Populates <see cref="Drives"/> by querying WMI for USB mass-storage devices.</summary>
    [RelayCommand]
    private async Task RefreshDrivesAsync(CancellationToken ct)
    {
        IsEnumerating = true;
        try
        {
            var found = await _writer.EnumerateDrivesAsync(ct);
            Drives.Clear();
            foreach (var d in found)
                Drives.Add(d);

            if (Drives.Count > 0)
                SelectedDrive = Drives[0];

            _logger.LogInformation("Drive enumeration complete: {Count} USB drive(s) found", Drives.Count);
        }
        catch (OperationCanceledException) { /* navigation away */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate USB drives");
        }
        finally
        {
            IsEnumerating = false;
        }
    }

    /// <summary>
    /// Begins the three-phase write: raw ISO → OEMDRV partition → staging copy.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanWrite))]
    private async Task WriteAsync(CancellationToken ct)
    {
        if (SelectedDrive is null || _isoPath is null || _stagingDirectory is null) return;

        IsRunning    = true;
        HasError     = false;
        ErrorMessage = null;
        ErrorDetail  = null;
        BytesWritten = 0;
        BytesTotal   = 0;
        PhaseDisplay = "Preparing…";

        var progress = new Progress<UsbWriteProgress>(p =>
        {
            BytesWritten = p.BytesWritten;
            BytesTotal   = p.BytesTotal;
            CurrentPhase = p.Phase;    // drives IsCancelable and cancel-message logic
            PhaseDisplay = p.Phase switch
            {
                UsbWritePhase.WritingIso     => "Writing installer image…",
                UsbWritePhase.CreatingOemdrv => "Creating OEMDRV partition…",
                UsbWritePhase.PatchingGrub   => "Patching GRUB configuration…",
                UsbWritePhase.CopyingFiles   => "Copying migration files…",
                UsbWritePhase.Complete       => "Complete",
                _                            => string.Empty,
            };

            // Capture the last GRUB patch message so it is displayed on completion.
            if (p.Phase == UsbWritePhase.PatchingGrub && !string.IsNullOrEmpty(p.Message))
                GrubPatchNote = p.Message;
        });

        try
        {
            await _writer.WriteAsync(
                SelectedDrive, _isoPath, _stagingDirectory, progress, ct);

            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("USB write cancelled at phase {Phase}", CurrentPhase);

            HasError     = true;
            ErrorMessage = CurrentPhase == UsbWritePhase.WritingIso
                // Cancelled mid-ISO: the image on the stick is truncated.
                ? "Write cancelled — the USB drive contains an incomplete installer image " +
                  "and must be re-written before it can be used. " +
                  "Return to this step and run the writer again."
                // Cancelled after Phase 1 (during file copy): ISO is intact, files incomplete.
                : "Write cancelled — the installer image is on the drive but the migration " +
                  "files were not fully copied. " +
                  "Return to this step and run the writer again to complete it.";
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "USB write failed — insufficient rights");
            HasError     = true;
            ErrorMessage = ex.Message;
            ErrorDetail  = BuildErrorDetail(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USB write failed");
            HasError     = true;
            ErrorMessage = ex.Message;
            ErrorDetail  = BuildErrorDetail(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Shuts down the application — bound to the "Finish" button shown on completion.</summary>
    [RelayCommand]
    private static void Finish() => Application.Current.Shutdown();

    // ── Error formatting ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds a multi-line technical dump suitable for display and copy-paste.
    /// Walks the inner-exception chain; includes Win32 native error codes when present.
    /// </summary>
    private static string BuildErrorDetail(Exception ex)
    {
        var sb      = new StringBuilder();
        var current = ex;
        var depth   = 0;

        while (current is not null)
        {
            if (depth > 0)
                sb.AppendLine().AppendLine("── inner exception ───────────────────────────────");

            sb.Append('[').Append(current.GetType().FullName).AppendLine("]");
            sb.AppendLine(current.Message);

            if (current is System.ComponentModel.Win32Exception w32)
                sb.Append("Win32 error code: ").Append(w32.NativeErrorCode)
                  .Append(" (0x").Append(w32.NativeErrorCode.ToString("X8")).AppendLine(")");

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
