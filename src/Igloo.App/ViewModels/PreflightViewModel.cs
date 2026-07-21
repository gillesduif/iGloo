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

    public bool HasReport => Report is not null;
    public bool HasError => ErrorMessage is not null;
    public bool HasFindings => Findings.Count > 0;
    public bool HasNoFindings => HasReport && !HasFindings;
    public bool HasBlockers => Findings.Any(f => f.Severity == FindingSeverity.Blocker);

    /// <summary>True when the check has completed without blockers - enables "Next" in the wizard.</summary>
    public bool CanProceed => HasReport && !HasBlockers;

    public string FirmwareDisplay => Report?.IsUefi == true ? "UEFI" : "Legacy BIOS";
    public bool FirmwareOk => Report?.IsUefi == true;

    public string SecureBootDisplay => Report?.SecureBootEnabled == true ? "Enabled" : "Disabled";
    public bool SecureBootWarn => Report?.SecureBootEnabled == true;

    public string TpmDisplay => Report?.TpmPresent == true ? "Present" : "Not detected";

    public string BitLockerDisplay => Report?.BitLocker switch
    {
        BitLockerState.NotEncrypted => "Not encrypted",
        BitLockerState.EncryptedAndUnlocked => "Encrypted - unlocked",
        BitLockerState.SuspendedProtection => "Encrypted - protection suspended",
        BitLockerState.EncryptedAndLocked => "Encrypted - locked",
        BitLockerState.DecryptionInProgress => "Decryption in progress…",
        _ => "Unknown",
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

    public IReadOnlyList<DiskInfo> Disks => Report?.Disks ?? Array.Empty<DiskInfo>();
    public IReadOnlyList<PreflightFinding> Findings => Report?.Findings ?? Array.Empty<PreflightFinding>();

    /// <summary>Presentation model for the Disk Management-style partition bars.</summary>
    public IReadOnlyList<DiskView> DiskViews => BuildDiskViews();

    // ── Constructor ─────────────────────────────────────────────────────────

    public PreflightViewModel(IPreflightChecker checker, ILinuxRemovalService linuxRemoval,
        ILogger<PreflightViewModel> logger)
    {
        _checker = checker;
        _linuxRemoval = linuxRemoval;
        _logger = logger;
    }

    private readonly ILinuxRemovalService _linuxRemoval;

    // ── Action-status banners (shown after one-click fixes) ──────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBitLockerActionStatus))]
    private string? _bitLockerActionStatus;

    public bool HasBitLockerActionStatus => BitLockerActionStatus is not null;

    // ── Existing Linux installations (detect + remove) ───────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinuxActionStatus))]
    private string? _linuxActionStatus;

    public bool HasLinuxActionStatus => LinuxActionStatus is not null;

    /// <summary>Selectable wrappers around the report's detected installations.</summary>
    public IReadOnlyList<LinuxInstallItem> LinuxInstalls { get; private set; } = [];

    public bool HasLinux => LinuxInstalls.Count > 0;
    public bool HasSingleLinux => LinuxInstalls.Count == 1;   // one install → single-OS layout
    public bool HasMultipleLinux => LinuxInstalls.Count > 1;    // several → multiple-choice layout
    public LinuxInstallItem? SingleLinux => LinuxInstalls.Count == 1 ? LinuxInstalls[0] : null;

    public bool HasSeedLeftovers => (Report?.SeedLeftovers.Count ?? 0) > 0;

    /// <summary>Leftover seed partitions without any Linux install → own small card.</summary>
    public bool ShowLeftoversOnlyCard => !HasLinux && HasSeedLeftovers;

    public string SeedLeftoverSummary => Report is null
        ? string.Empty
        : string.Join(" · ", Report.SeedLeftovers.Select(
            s => $"{s.Partition.Label} ({FormatBytes(s.Partition.SizeBytes)})"));

    public bool CanRemoveSelected => LinuxInstalls.Any(i => i.IsSelected);

    private void OnLinuxSelectionChanged()
    {
        OnPropertyChanged(nameof(CanRemoveSelected));
        RemoveSelectedLinuxCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private Task RemoveLinuxAsync()
        => RemoveLinuxCoreAsync([.. LinuxInstalls.Select(i => i.Installation)]);

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private Task RemoveSelectedLinuxAsync()
        => RemoveLinuxCoreAsync([.. LinuxInstalls.Where(i => i.IsSelected).Select(i => i.Installation)]);

    [RelayCommand]
    private Task RemoveSeedLeftoversAsync() => RemoveLinuxCoreAsync([]);

    private async Task RemoveLinuxCoreAsync(IReadOnlyList<LinuxInstallation> targets)
    {
        var leftovers = Report?.SeedLeftovers ?? [];
        if (targets.Count == 0 && leftovers.Count == 0)
            return;

        // Only when the last Linux goes may the service clear ALL Linux boot entries.
        var removingAllLinux = targets.Count > 0 && targets.Count == LinuxInstalls.Count;

        var lines = targets
            .Select(t => $"•  {t.DisplayName} — {FormatBytes(t.TotalBytes)} on {t.DiskModel} " +
                         $"({t.Partitions.Count} partition{(t.Partitions.Count == 1 ? "" : "s")})")
            .Concat(leftovers.Select(s =>
                $"•  iGloo installer partition {s.Partition.Label} — " +
                $"{FormatBytes(s.Partition.SizeBytes)} on {s.DiskModel}"))
            .ToList();

        var confirm = MessageBox.Show(
            "This will permanently delete:\n\n" + string.Join("\n", lines) + "\n\n" +
            "All data on these partitions is destroyed. This cannot be undone.\n" +
            "Freed space next to your Windows partition is added back to it automatically.",
            targets.Count > 0 ? "Remove Linux from this PC?" : "Remove iGloo leftovers?",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        try
        {
            LinuxActionStatus = "Removing…";
            var progress = new Progress<string>(s => LinuxActionStatus = s);
            await _linuxRemoval.RemoveAsync(targets, leftovers, removingAllLinux, progress);

            LinuxActionStatus = "Removal complete - re-running system check…";
            if (!IsRunning)
                await RunCheckCommand.ExecuteAsync(null);
            LinuxActionStatus = null;   // the refreshed report speaks for itself
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Linux removal failed");
            LinuxActionStatus = $"Removal failed: {ex.Message}";
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunCheckAsync(CancellationToken ct)
    {
        IsRunning = true;
        ErrorMessage = null;
        Report = null;
        try
        {
            Report = await _checker.RunAsync(ct);
            _logger.LogInformation("Pre-flight complete - {Count} finding(s)", Report.Findings.Count);
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
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                BitLockerActionStatus = "Decryption started - re-running system check…";
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

        if (confirm != MessageBoxResult.OK)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(FindNativeExe("shutdown.exe"), "/r /fw /t 10")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
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
        var winRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var sysnative = Path.Combine(winRoot, "Sysnative", exeName);
        return File.Exists(sysnative)
            ? sysnative
            : Path.Combine(winRoot, "System32", exeName);
    }

    // ── Partition-bar presentation model ─────────────────────────────────────

    /// <summary>
    /// Gaps below this size are alignment noise (the ~1 MiB GPT lead-in, sector
    /// padding) and are not rendered as unallocated segments.
    /// </summary>
    private const long UnallocatedThresholdBytes = 32L * 1024 * 1024;

    private IReadOnlyList<DiskView> BuildDiskViews()
    {
        if (Report is null)
            return [];

        var views = new List<DiskView>();
        foreach (var disk in Report.Disks)
        {
            var parts = disk.Partitions
                .Where(p => p.SizeBytes > 0)
                .OrderBy(p => p.OffsetBytes >= 0 ? p.OffsetBytes : long.MaxValue)
                .ToList();
            // Without offsets we can still size segments correctly; only the
            // unallocated gaps lose their true position (they collapse to a tail).
            var haveOffsets = parts.Count > 0 && parts.All(p => p.OffsetBytes >= 0);

            var segments = new List<PartitionSegment>();
            long cursor = 0;
            foreach (var p in parts)
            {
                if (haveOffsets && p.OffsetBytes - cursor >= UnallocatedThresholdBytes)
                    segments.Add(PartitionSegment.Unallocated(p.OffsetBytes - cursor));

                var (kind, name) = ClassifyPartition(p);
                var fsKnown = p.FileSystem is not (null or "" or "Unknown");
                var detail = fsKnown && !string.Equals(name, p.FileSystem, StringComparison.OrdinalIgnoreCase)
                    ? $"{p.FileSystem} · {FormatBytes(p.SizeBytes)}"
                    : FormatBytes(p.SizeBytes);

                segments.Add(new PartitionSegment(
                    name, detail, p.SizeBytes, kind, p.IsSystem, p.IsBoot, IsUnallocated: false));

                cursor = haveOffsets ? Math.Max(cursor, p.OffsetBytes + p.SizeBytes)
                                     : cursor + p.SizeBytes;
            }

            if (disk.TotalBytes - cursor >= UnallocatedThresholdBytes)
                segments.Add(PartitionSegment.Unallocated(disk.TotalBytes - cursor));

            views.Add(new DiskView(disk.Model, disk.TotalBytes, disk.PartitionStyle, segments));
        }
        return views;
    }

    /// <summary>
    /// Names label-less service partitions by their GPT type GUID (the reason
    /// Disk Management can say "EFI system partition" where a raw volume listing
    /// says "Unknown"), then falls back to label / filesystem / flags.
    /// </summary>
    private static (string Kind, string Name) ClassifyPartition(PartitionInfo p)
    {
        var gpt = p.GptType?.Trim('{', '}').ToLowerInvariant();
        switch (gpt)
        {
            case "c12a7328-f81f-11d2-ba4b-00a0c93ec93b":
                return ("Efi", "EFI system");
            case "e3c9e316-0b5c-4db8-817d-f92df00215ae":
                return ("Msr", "Microsoft Reserved");
            case "de94bba4-06d1-4d40-a16a-bfd50179d6ac":
                return ("Recovery", "Recovery");
            case "0fc63daf-8483-4772-8e79-3d69d8477de4":                       // Linux filesystem
            case "0657fd6d-a4ab-43c4-84e5-0933c84b4f4f":                       // Linux swap
            case "e6d6d379-f507-44c2-a23c-238f2a3df928":                       // Linux LVM
            case "933ac7e1-2eb4-4f13-b844-0e14e2aef915":
                return ("Linux", p.Label ?? "Linux");  // /home
        }

        // iGloo's own transient install partitions (kickstart seed, staged ISO).
        if (p.Label is "OEMDRV" or "CIDATA" or "IGLOOISO")
            return ("Seed", $"{p.Label} (iGloo)");

        if (p.IsBoot)
            return ("Windows", p.Label ?? "Windows");

        if (p.FileSystem is not (null or "" or "Unknown"))
            return ("Data", p.Label ?? p.FileSystem);

        if (p.IsSystem)
            return ("Efi", p.Label ?? "System");

        return ("Unknown", p.Label ?? "Partition");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N1} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):N0} MB",
        _ => $"{bytes / 1024.0:N0} KB",
    };

    // ── Property-change hooks ────────────────────────────────────────────────

    partial void OnReportChanged(PreflightReport? value)
    {
        LinuxInstalls = value?.LinuxInstallations
            .Select(li => new LinuxInstallItem(
                li, li.DisplayName, $"{FormatBytes(li.TotalBytes)} · {li.DiskModel}",
                OnLinuxSelectionChanged))
            .ToList() ?? [];
        OnPropertyChanged(nameof(LinuxInstalls));
        OnPropertyChanged(nameof(HasLinux));
        OnPropertyChanged(nameof(HasSingleLinux));
        OnPropertyChanged(nameof(HasMultipleLinux));
        OnPropertyChanged(nameof(SingleLinux));
        OnPropertyChanged(nameof(HasSeedLeftovers));
        OnPropertyChanged(nameof(ShowLeftoversOnlyCard));
        OnPropertyChanged(nameof(SeedLeftoverSummary));
        OnPropertyChanged(nameof(CanRemoveSelected));
        RemoveSelectedLinuxCommand.NotifyCanExecuteChanged();

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
        OnPropertyChanged(nameof(DiskViews));
        OnPropertyChanged(nameof(Findings));
    }
}

/// <summary>One disk in the STORAGE section: header facts plus its partition bar.</summary>
public sealed record DiskView(string Model, long TotalBytes, string PartitionStyle,
    IReadOnlyList<PartitionSegment> Segments);

/// <summary>
/// One segment of a disk's partition bar (a partition, or an unallocated gap).
/// <see cref="Kind"/> keys the fill color (see <c>PartitionKindToBrushConverter</c>);
/// <see cref="SizeBytes"/> doubles as the segment's proportional layout weight.
/// </summary>
public sealed record PartitionSegment(string Name, string Detail, long SizeBytes,
    string Kind, bool IsSystem, bool IsBoot, bool IsUnallocated)
{
    public double Weight => SizeBytes;

    public static PartitionSegment Unallocated(long sizeBytes) => new(
        "Unallocated", FormatBytesStatic(sizeBytes), sizeBytes,
        Kind: "Free", IsSystem: false, IsBoot: false, IsUnallocated: true);

    private static string FormatBytesStatic(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N1} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):N0} MB",
        _ => $"{bytes / 1024.0:N0} KB",
    };
}

/// <summary>
/// One detected Linux installation in the removal UI. <see cref="IsSelected"/>
/// backs the checkbox in the multiple-installations layout.
/// </summary>
public sealed partial class LinuxInstallItem : ObservableObject
{
    private readonly Action _selectionChanged;

    public LinuxInstallItem(LinuxInstallation installation, string title, string detail,
        Action selectionChanged)
    {
        Installation = installation;
        Title = title;
        Detail = detail;
        _selectionChanged = selectionChanged;
    }

    public LinuxInstallation Installation { get; }
    public string Title { get; }
    public string Detail { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}
