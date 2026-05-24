using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.App.ViewModels;

public sealed partial class PreflightViewModel : ObservableObject
{
    private readonly IPreflightChecker _checker;
    private readonly ILogger<PreflightViewModel> _logger;

    // ── Observable state ────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private PreflightReport? _report;

    // ── Derived / display properties (all recomputed when Report changes) ──

    public bool HasReport   => Report is not null;
    public bool HasError    => ErrorMessage is not null;
    public bool HasFindings => Findings.Count > 0;
    public bool HasNoFindings => HasReport && !HasFindings;
    public bool HasBlockers => Findings.Any(f => f.Severity == FindingSeverity.Blocker);

    /// <summary>True when the check has completed without blockers — enables "Next" in the wizard.</summary>
    public bool CanProceed  => HasReport && !HasBlockers;

    public string FirmwareDisplay    => Report?.IsUefi == true ? "UEFI" : "Legacy BIOS";
    public bool   FirmwareOk         => Report?.IsUefi == true;

    public string SecureBootDisplay  => Report?.SecureBootEnabled == true ? "Enabled" : "Disabled";
    public bool   SecureBootWarn     => Report?.SecureBootEnabled == true;

    public string TpmDisplay         => Report?.TpmPresent == true ? "Present" : "Not detected";

    public string BitLockerDisplay   => Report?.BitLocker switch
    {
        BitLockerState.NotEncrypted          => "Not encrypted",
        BitLockerState.EncryptedAndUnlocked  => "Encrypted — unlocked",
        BitLockerState.SuspendedProtection   => "Encrypted — protection suspended",
        BitLockerState.EncryptedAndLocked    => "Encrypted — locked",
        BitLockerState.DecryptionInProgress  => "Decryption in progress…",
        _                                    => "Unknown",
    };
    public bool BitLockerBlocked =>
        Report?.BitLocker is BitLockerState.EncryptedAndUnlocked
                           or BitLockerState.SuspendedProtection
                           or BitLockerState.EncryptedAndLocked
                           or BitLockerState.DecryptionInProgress;

    public string RamDisplay =>
        Report is null ? string.Empty :
        Report.TotalRamBytes >= 1024L * 1024 * 1024
            ? $"{Report.TotalRamBytes / (1024.0 * 1024 * 1024):N1} GB"
            : $"{Report.TotalRamBytes / (1024L * 1024):N0} MB";

    public string GpuDisplay => Report?.GpuVendor ?? string.Empty;

    public IReadOnlyList<DiskInfo>        Disks    => Report?.Disks    ?? Array.Empty<DiskInfo>();
    public IReadOnlyList<PreflightFinding> Findings => Report?.Findings ?? Array.Empty<PreflightFinding>();

    // ── Constructor ─────────────────────────────────────────────────────────

    public PreflightViewModel(IPreflightChecker checker, ILogger<PreflightViewModel> logger)
    {
        _checker = checker;
        _logger  = logger;
    }

    // ── Action-status banners (shown after one-click fixes) ──────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBitLockerActionStatus))]
    private string? _bitLockerActionStatus;

    public bool HasBitLockerActionStatus => BitLockerActionStatus is not null;

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunCheckAsync(CancellationToken ct)
    {
        IsRunning    = true;
        ErrorMessage = null;
        Report       = null;
        try
        {
            Report = await _checker.RunAsync(ct);
            _logger.LogInformation("Pre-flight complete — {Count} finding(s)", Report.Findings.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Pre-flight check cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pre-flight check failed");
            ErrorMessage = $"System check failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Runs <c>manage-bde -off &lt;SystemDrive&gt;</c> to start BitLocker decryption,
    /// then automatically re-runs the pre-flight check so the UI reflects the new state.
    /// </summary>
    [RelayCommand]
    private async Task DisableBitLockerAsync()
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        BitLockerActionStatus = $"Starting BitLocker decryption on {systemDrive}…";

        try
        {
            // manage-bde.exe only exists in the real System32 (not SysWOW64).
            // When Igloo runs as a 32-bit process on 64-bit Windows, the WOW64 layer
            // silently redirects System32 → SysWOW64, so SpecialFolder.System returns
            // the wrong directory. The "Sysnative" virtual folder bypasses this for
            // 32-bit callers; it doesn't exist for 64-bit callers (hence the fallback).
            var manageBde = FindNativeExe("manage-bde.exe");

            using var proc = Process.Start(new ProcessStartInfo(manageBde, $"-off {systemDrive}")
            {
                UseShellExecute  = false,
                CreateNoWindow   = true,
            })!;

            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                BitLockerActionStatus = "Decryption started — re-running system check…";
                await Task.Delay(1200);
                if (!IsRunning)
                    await RunCheckCommand.ExecuteAsync(null);
                BitLockerActionStatus = null;  // hide banner; check results speak for themselves
            }
            else
            {
                BitLockerActionStatus =
                    $"manage-bde exited with code {proc.ExitCode}. " +
                    "Try running 'manage-bde -off C:' in an elevated command prompt.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DisableBitLocker failed");
            BitLockerActionStatus = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Schedules a restart directly into UEFI firmware settings (<c>shutdown /r /fw /t 10</c>).
    /// The user disables Secure Boot there manually, then boots back into Windows.
    /// </summary>
    [RelayCommand]
    private void RestartToFirmware()
    {
        var confirm = MessageBox.Show(
            "Your PC will restart into UEFI firmware settings in 10 seconds.\n\n" +
            "• Find the 'Secure Boot' option\n" +
            "• Set it to Disabled\n" +
            "• Save changes and exit\n\n" +
            "Save all open work before clicking OK.",
            "Restart to firmware settings?",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            Process.Start(new ProcessStartInfo(FindNativeExe("shutdown.exe"), "/r /fw /t 10")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RestartToFirmware failed");
            MessageBox.Show(
                $"Could not schedule restart: {ex.Message}\n\n" +
                "Run 'shutdown /r /fw /t 0' in an elevated command prompt manually.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full path to a System32 executable, bypassing WOW64 file-system
    /// redirection for 32-bit processes running on 64-bit Windows.
    ///
    /// A 32-bit process's <c>SpecialFolder.System</c> resolves to <c>SysWOW64</c>, which
    /// lacks many admin tools (manage-bde, shutdown, …). The virtual <c>Sysnative</c> folder
    /// bypasses the redirect for 32-bit callers; on a native 64-bit process it doesn't exist
    /// and the real <c>System32</c> path is used instead.
    /// </summary>
    private static string FindNativeExe(string exeName)
    {
        var winRoot  = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var sysnative = Path.Combine(winRoot, "Sysnative", exeName);
        return File.Exists(sysnative)
            ? sysnative
            : Path.Combine(winRoot, "System32", exeName);
    }

    // ── Property-change hooks ────────────────────────────────────────────────

    partial void OnReportChanged(PreflightReport? value)
    {
        // Fire all display properties that depend on Report in one pass.
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasNoFindings));
        OnPropertyChanged(nameof(HasBlockers));
        OnPropertyChanged(nameof(CanProceed));
        OnPropertyChanged(nameof(FirmwareDisplay));
        OnPropertyChanged(nameof(FirmwareOk));
        OnPropertyChanged(nameof(SecureBootDisplay));
        OnPropertyChanged(nameof(SecureBootWarn));
        OnPropertyChanged(nameof(TpmDisplay));
        OnPropertyChanged(nameof(BitLockerDisplay));
        OnPropertyChanged(nameof(BitLockerBlocked));
        OnPropertyChanged(nameof(RamDisplay));
        OnPropertyChanged(nameof(GpuDisplay));
        OnPropertyChanged(nameof(Disks));
        OnPropertyChanged(nameof(Findings));
    }
}
