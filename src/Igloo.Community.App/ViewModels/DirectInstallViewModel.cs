using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Host;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class DirectInstallViewModel : ObservableObject
{
    private readonly IInstallationPreparation _installer;
    private readonly IWindowsMaintenance _maintenance;
    private readonly ILogger<DirectInstallViewModel> _logger;

    private DiskInfo? _targetDisk;
    private long _linuxSizeBytes;
    private IsoAcquisitionResult? _verifiedIso;
    private string? _stagingDirectory;
    private string? _distroId;
    private Uri? _stage2Url;

    //   Observable state                            

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCancelable))]
    [NotifyPropertyChangedFor(nameof(IsReadyToInstall))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToInstall))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    private bool _isComplete;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToInstall))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    private bool _hasError;
    [ObservableProperty] private string _errorTitle = DefaultTitle;
    [ObservableProperty] private string? _errorMessage;

    // The name of the distribution, for the result line.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadyTitle))]
    private string? _distroName;

    public string ReadyTitle => $"Ready to install {DistroName ?? "Linux"}";

    private const string DefaultTitle = "Preparation failed";
    private const string CheckDetails = "Check the details and try again.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    private string? _errorDetail;

    public bool HasErrorDetail => !string.IsNullOrEmpty(ErrorDetail);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesWritten;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesTotal;

    [ObservableProperty] private string? _phaseDisplay;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCancelable))]
    private DirectInstallPhase _currentPhase;
    [ObservableProperty] private bool _isRebooting;

    // Open by default: partitioning is the step users most want to watch.
    [ObservableProperty] private bool _logsExpanded = true;
    [ObservableProperty] private string? _logTail;

    // Serilog keeps today's file open, so the tail is read with FileShare.ReadWrite.
    private const int LogTailBytes = 64 * 1024;
    private const int LogTailLines = 200;

    //   Derived                                ─

    public double ProgressPercent => BytesTotal > 0 ? BytesWritten * 100.0 / BytesTotal : 0;
    public bool IsProgressIndeterminate => BytesTotal == 0;

    
    public bool IsCancelable => !IsRunning || CurrentPhase != DirectInstallPhase.CreatingPartition;

    // After a failed registration the prepared installer stays; Restart tries that again, not the preparation.
    public bool CanRetry => HasError && !IsComplete && !IsRunning;

    public bool IsReadyToInstall => !IsRunning && !IsComplete && !HasError &&
        _verifiedIso is not null && _stagingDirectory is not null &&
        _targetDisk is not null && !string.IsNullOrWhiteSpace(_distroId);

    //   Log tail                                ─

    private static string LogDirectory => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Igloo", "logs");

    partial void OnLogsExpandedChanged(bool value)
    {
        if (value)
            RefreshLogTail();
    }

    // Collapse the log on success only: the finished page is a result, not a
    // console. An error keeps it open, because there the log is the answer.
    partial void OnIsCompleteChanged(bool value)
    {
        if (value)
            LogsExpanded = false;
    }

    [RelayCommand]
    private void RefreshLogTail()
    {
        try
        {
            var newest = new DirectoryInfo(LogDirectory)
                .EnumerateFiles("igloo-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                LogTail = "No log available.";
                return;
            }

            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > LogTailBytes)
                fs.Seek(-LogTailBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            var lines = reader.ReadToEnd().Split('\n');
            LogTail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - LogTailLines)))
                            .TrimStart('\r', '\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or SecurityException or DirectoryNotFoundException)
        {
            LogTail = "The log could not be read.";
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _logger.LogWarning(ex, "Could not open the log folder");
        }
    }

    //   Constructor                              ─

    public DirectInstallViewModel(IInstallationPreparation installer,
        IWindowsMaintenance maintenance, ILogger<DirectInstallViewModel> logger)
    {
        _installer = installer;
        _maintenance = maintenance;
        _logger = logger;
    }

    //   API called by MainWindowViewModel                   ─

    public void Prepare(
        IsoAcquisitionResult isoResult,
        FileStagingResult stagingResult,
        DiskInfo targetDisk,
        int linuxSizeGb,
        string distroId, Uri? stage2Url = null, string? distroName = null)
    {
        DistroName = distroName;
        ArgumentNullException.ThrowIfNull(isoResult);
        ArgumentNullException.ThrowIfNull(stagingResult);
        ArgumentNullException.ThrowIfNull(targetDisk);

        _verifiedIso = isoResult;
        _stagingDirectory = stagingResult.StagingDirectory;
        _linuxSizeBytes = (long)linuxSizeGb * 1024 * 1024 * 1024;
        _distroId = distroId;
        _stage2Url = stage2Url;

        _targetDisk = targetDisk;

        IsComplete = false;
        HasError = false;
        ErrorTitle = DefaultTitle;
        ErrorMessage = null;
        ErrorDetail = null;
        PhaseDisplay = null;
        BytesWritten = 0;
        BytesTotal = 0;
        IsRunning = false;
        IsRebooting = false;
        CurrentPhase = DirectInstallPhase.ShrinkingPartition;
        OnPropertyChanged(nameof(IsReadyToInstall));
    }

    //   Commands                                

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken ct)
    {
        if (_verifiedIso is null ||
            _stagingDirectory is null || _targetDisk is null)
            return;

        IsRunning = true;
        HasError = false;
        ErrorMessage = null;
        ErrorDetail = null;
        BytesWritten = 0;
        BytesTotal = 0;
        PhaseDisplay = "Preparing…";

        var uiProgress = new Progress<DirectInstallProgress>(p =>
        {
            CurrentPhase = p.Phase;
            BytesWritten = p.BytesWritten;
            BytesTotal = p.BytesTotal;
            PhaseDisplay = p.Phase switch
            {
                DirectInstallPhase.ShrinkingPartition => p.Message ?? "Resizing Windows partition…",
                DirectInstallPhase.CreatingPartition => p.Message ?? "Creating partitions…",
                DirectInstallPhase.CopyingIso => p.Message ?? "Copying installer…",
                DirectInstallPhase.CopyingFiles => p.Message ?? "Copying files…",
                DirectInstallPhase.ConfiguringGrub => p.Message ?? "Copying installer…",
                DirectInstallPhase.RegisteringBootEntry => "Restarting…",
                _ => string.Empty,
            };
            if (LogsExpanded)
                RefreshLogTail();
        });
        // The full-ISO staging copy reports per buffer; throttle before the UI
        // thread sees it (phase changes and completion always pass through).
        var progress = new Igloo.Core.Services.ThrottledProgress<DirectInstallProgress>(
            uiProgress,
            forceWhen: (cur, prev) => prev is null
                || cur.Phase != prev.Phase
                || cur.Message != prev.Message
                || (cur.BytesTotal > 0 && cur.BytesWritten >= cur.BytesTotal));

        // The installer partition gets a drive letter; Explorer is told not to open a window on it.
        var declinedBefore = AutoPlayCancellation.Declined;
        using var quiet = AutoPlayCancellation.Begin();
        try
        {
            if (_distroId is null) throw new InvalidOperationException("No distro was selected.");
            await _installer.PrepareAsync(_distroId, _targetDisk, _linuxSizeBytes,
                _verifiedIso, _stagingDirectory, _stage2Url, progress, ct);
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Direct install cancelled at phase {Phase}", CurrentPhase);
            HasError = true;
            ErrorTitle = "Preparation was canceled";
            ErrorMessage = "Try again.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Direct install preparation failed");
            HasError = true;
            // A refusal carries its own heading and instruction.
            (ErrorTitle, ErrorMessage) = ex is InstallationRefusedException refusal
                ? (refusal.Heading, refusal.Message)
                : (DefaultTitle, CheckDetails);
            ErrorDetail = BuildErrorDetail(ex);
        }
        finally
        {
            // Zero means Explorer never asked this window, and a window on the new drive was not prevented this way.
            _logger.LogInformation("Explorer asked {Count} time(s) whether to open a new drive during the preparation and was told no",
                AutoPlayCancellation.Declined - declinedBefore);
            IsRunning = false;
        }
    }

    /// <summary>Registers the prepared installer for one boot, then requests a Windows restart.</summary>
    [RelayCommand]
    private async Task RebootToInstallAsync()
    {
        if (!IsComplete || IsRebooting)
            return;
        IsRebooting = true;
        HasError = false;
        ErrorMessage = null;
        ErrorDetail = null;
        PhaseDisplay = "Restarting…";
        try
        {
            await _installer.RegisterBootEntryAsync(
                new Progress<DirectInstallProgress>(p => PhaseDisplay = p.Message ?? PhaseDisplay));
            _logger.LogInformation("UEFI BootNext registered; requesting restart into installer");
            _maintenance.RestartToInstaller();
            await Task.Delay(2000);
            Application.Current?.Shutdown();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or
            IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            _logger.LogError(ex, "Installer boot registration or restart failed");
            IsRebooting = false;
            HasError = true;
            // A refusal carries its own heading and instruction; nothing was registered when it is raised.
            (ErrorTitle, ErrorMessage) = ex is InstallationRefusedException refusal
                ? (refusal.Heading, refusal.Message)
                : ("Installer boot entry could not be registered", CheckDetails);
            ErrorDetail = BuildErrorDetail(ex);
        }
    }

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
                sb.Append("Win32 error: ").Append(w32.NativeErrorCode)
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
