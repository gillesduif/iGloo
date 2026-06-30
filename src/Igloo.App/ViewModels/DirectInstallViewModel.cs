using System.Diagnostics;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Microsoft.Extensions.Logging;

namespace Igloo.App.ViewModels;

/// <summary>
/// View-model for the Direct Install wizard step (dual-boot, no USB).
///
/// Flow:
///   1. On navigation <see cref="Prepare"/> is called; the step auto-triggers
///      <see cref="InstallCommand"/> which runs in the background.
///   2. Progress through phases (shrink → partition → copy ISO → copy files → GRUB).
///   3. On completion the user clicks "Reboot to Install" which registers the
///      UEFI boot entry and initiates a Windows restart.
/// </summary>
public sealed partial class DirectInstallViewModel : ObservableObject
{
    private readonly IDirectInstallService              _installer;
    private readonly DistroRegistry                     _registry;
    private readonly ILogger<DirectInstallViewModel>    _logger;

    private int     _diskNumber;
    private long    _linuxSizeBytes;
    private string? _isoPath;
    private string? _stagingDirectory;
    private string? _stage2Url;
    private string? _distroId;

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCancelable))]
    private bool _isRunning;

    [ObservableProperty] private bool    _isComplete;
    [ObservableProperty] private bool    _hasError;
    [ObservableProperty] private string? _errorMessage;

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

    [ObservableProperty] private string?              _phaseDisplay;
    [ObservableProperty] private DirectInstallPhase   _currentPhase;
    [ObservableProperty] private bool                 _isRebooting;

    // ── Derived ───────────────────────────────────────────────────────────────

    public double ProgressPercent         => BytesTotal > 0 ? BytesWritten * 100.0 / BytesTotal : 0;
    public bool   IsProgressIndeterminate => BytesTotal == 0;

    /// <summary>Allow cancel at any point except during the atomic partition creation.</summary>
    public bool IsCancelable => !IsRunning || CurrentPhase != DirectInstallPhase.CreatingPartition;

    // ── Constructor ───────────────────────────────────────────────────────────

    public DirectInstallViewModel(
        IDirectInstallService           installer,
        DistroRegistry                  registry,
        ILogger<DirectInstallViewModel> logger)
    {
        _installer = installer;
        _registry  = registry;
        _logger    = logger;
    }

    // ── API called by MainWindowViewModel ─────────────────────────────────────

    /// <summary>
    /// Stores parameters from prior wizard steps and resets all state.
    /// Call before navigating to this step.
    /// </summary>
    public void Prepare(
        IsoAcquisitionResult isoResult,
        FileStagingResult    stagingResult,
        DiskInfo             targetDisk,
        int                  linuxSizeGb,
        string               distroId,
        string?              stage2Url = null)
    {
        _isoPath          = isoResult.LocalPath;
        _stagingDirectory = stagingResult.StagingDirectory;
        _linuxSizeBytes   = (long)linuxSizeGb * 1024 * 1024 * 1024;
        _stage2Url        = stage2Url;
        _distroId         = distroId;

        // Extract disk number from DeviceId e.g. "\\.\PHYSICALDRIVE2" → 2
        const string prefix = "\\\\.\\PHYSICALDRIVE";
        _diskNumber = int.Parse(targetDisk.DeviceId.AsSpan()[prefix.Length..]);

        IsComplete    = false;
        HasError      = false;
        ErrorMessage  = null;
        ErrorDetail   = null;
        PhaseDisplay  = null;
        BytesWritten  = 0;
        BytesTotal    = 0;
        IsRunning     = false;
        IsRebooting   = false;
        CurrentPhase  = DirectInstallPhase.ShrinkingPartition;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Runs the full preparation pipeline (shrink → partition → copy → GRUB).</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken ct)
    {
        if (_isoPath is null || _stagingDirectory is null) return;

        IsRunning    = true;
        HasError     = false;
        ErrorMessage = null;
        ErrorDetail  = null;
        BytesWritten = 0;
        BytesTotal   = 0;
        PhaseDisplay = "Preparing…";

        var progress = new Progress<DirectInstallProgress>(p =>
        {
            CurrentPhase = p.Phase;
            BytesWritten = p.BytesWritten;
            BytesTotal   = p.BytesTotal;
            PhaseDisplay = p.Phase switch
            {
                DirectInstallPhase.ShrinkingPartition  => p.Message ?? "Shrinking Windows partition…",
                DirectInstallPhase.CreatingPartition   => p.Message ?? "Creating installer partition…",
                DirectInstallPhase.CopyingIso          => "Copying installer image…",
                DirectInstallPhase.CopyingFiles        => "Copying migration files…",
                DirectInstallPhase.ConfiguringGrub     => "Configuring bootloader…",
                DirectInstallPhase.RegisteringBootEntry => "Registering boot entry…",
                DirectInstallPhase.Complete            => "Ready to reboot",
                _                                      => string.Empty,
            };
        });

        try
        {
            if (_distroId is null || !_registry.TryGet(_distroId, out var plugin))
                throw new InvalidOperationException(
                    $"No installer plugin is loaded for distro '{_distroId}'. " +
                    "Ensure the distro's Igloo.Distro.*.dll is present in its distros/ folder.");
            var bootSpec = plugin.GetInstallerBootSpec();

            await _installer.PrepareAsync(
                _diskNumber, _linuxSizeBytes,
                _isoPath, _stagingDirectory,
                bootSpec, _stage2Url,
                progress, ct);

            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Direct install cancelled at phase {Phase}", CurrentPhase);
            HasError     = true;
            ErrorMessage = "Installation preparation cancelled. " +
                           "You may need to remove the partial partition manually via Disk Management, " +
                           "then run Igloo again.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Direct install preparation failed");
            HasError     = true;
            ErrorMessage = ex.Message;
            ErrorDetail  = BuildErrorDetail(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Registers the UEFI BootNext entry then restarts Windows.
    /// Shown on the completion panel.
    /// </summary>
    [RelayCommand]
    private async Task RebootToInstallAsync()
    {
        IsRebooting  = true;
        PhaseDisplay = "Registering UEFI boot entry…";

        var progress = new Progress<DirectInstallProgress>(p =>
            PhaseDisplay = p.Message ?? PhaseDisplay);

        try
        {
            await _installer.RegisterBootEntryAsync(progress);
            _logger.LogInformation("UEFI BootNext registered - initiating reboot");

            // 10-second countdown reboot with a user-visible message.
            Process.Start(new ProcessStartInfo(
                "shutdown.exe",
                "/r /t 10 /c \"iGloo is restarting to install Linux. " +
                "Save any open work now.\"")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            });

            // Shut down the WPF app so the user isn't left with a dead window.
            await Task.Delay(2000);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RegisterBootEntry / reboot failed");
            IsRebooting  = false;
            HasError     = true;
            ErrorMessage = ex.Message;
            ErrorDetail  = BuildErrorDetail(ex);
        }
    }

    // ── Error formatting ──────────────────────────────────────────────────────

    private static string BuildErrorDetail(Exception ex)
    {
        var sb      = new StringBuilder();
        var current = ex;
        var depth   = 0;
        while (current is not null)
        {
            if (depth > 0) sb.AppendLine().AppendLine("── inner exception ───────────────────────────────");
            sb.Append('[').Append(current.GetType().FullName).AppendLine("]");
            sb.AppendLine(current.Message);
            if (current is System.ComponentModel.Win32Exception w32)
                sb.Append("Win32 error: ").Append(w32.NativeErrorCode)
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
