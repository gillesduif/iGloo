using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.App.Views;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.App.ViewModels;

public sealed partial class PreflightViewModel : ObservableObject
{
    private readonly IPreflightChecker _checker;
    private readonly ILogger<PreflightViewModel> _logger;
    private readonly ILinuxRemovalService _linuxRemoval;

    //   Observable state                           

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private PreflightReport? _report;

    //   Derived / display properties (all recomputed when Report changes)  

    public bool HasReport => Report is not null;
    public bool HasError => ErrorMessage is not null;
    public bool HasFindings => Findings.Count > 0;
    public bool HasNoFindings => HasReport && !HasFindings;
    public bool HasBlockers => Findings.Any(f => f.Severity == FindingSeverity.Blocker);

    
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

    // The model, when WMI gave us one: "NVIDIA GeForce RTX 5070" tells the user (and
    // a bug report) far more than "NVIDIA", and which driver is correct depends on
    // the specific chip rather than the vendor.
    public string GpuDisplay => Report?.GpuModel ?? Report?.GpuVendor ?? string.Empty;

    public IReadOnlyList<DiskInfo> Disks => Report?.Disks ?? Array.Empty<DiskInfo>();
    public IReadOnlyList<PreflightFinding> Findings => Report?.Findings ?? Array.Empty<PreflightFinding>();

    
    public IReadOnlyList<DiskView> DiskViews => BuildDiskViews();

    //   Constructor                             ─

    public PreflightViewModel(IPreflightChecker checker, ILinuxRemovalService linuxRemoval,
        ILogger<PreflightViewModel> logger)
    {
        _checker = checker;
        _linuxRemoval = linuxRemoval;
        _logger = logger;
    }


    //   Action-status banners (shown after one-click fixes)          

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBitLockerActionStatus))]
    private string? _bitLockerActionStatus;

    public bool HasBitLockerActionStatus => BitLockerActionStatus is not null;

    //   Existing Linux installations (detect + remove)            ─

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinuxActionStatus))]
    private string? _linuxActionStatus;

    public bool HasLinuxActionStatus => LinuxActionStatus is not null;

    
    public IReadOnlyList<LinuxInstallItem> LinuxInstalls { get; private set; } = [];

    public bool HasLinux => LinuxInstalls.Count > 0;
    public bool HasSingleLinux => LinuxInstalls.Count == 1;   // one install → single-OS layout
    public bool HasMultipleLinux => LinuxInstalls.Count > 1;    // several → multiple-choice layout
    public LinuxInstallItem? SingleLinux => LinuxInstalls.Count == 1 ? LinuxInstalls[0] : null;

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

    private async Task RemoveLinuxCoreAsync(IReadOnlyList<LinuxInstallation> targets)
    {
        var leftovers = Report?.SeedLeftovers ?? [];
        if (targets.Count == 0 && leftovers.Count == 0)
            return;

        // Only when the last Linux goes may the service clear ALL Linux boot entries.
        var removingAllLinux = targets.Count > 0 && targets.Count == LinuxInstalls.Count;

        var lines = targets
            .Select(t => $"•  {t.DisplayName}  {ByteFormat.Format(t.TotalBytes)} on {t.DiskModel} " +
                         $"({t.Partitions.Count} partition{(t.Partitions.Count == 1 ? "" : "s")})")
            .Concat(leftovers.Select(s =>
                $"•  iGloo installer partition {s.Partition.Label}  " +
                $"{ByteFormat.Format(s.Partition.SizeBytes)} on {s.DiskModel}"))
            .ToList();

        // Danger severity: the safe button takes the default, so Enter cancels rather
        // than erasing partitions.
        if (!FluentMessageBox.Confirm(
                targets.Count > 0 ? "Uninstall Linux operating system?" : "Remove iGloo system components?",
                 "This action permanently deletes the following components:\n\n" + string.Join("\n", lines) + "\n\n" +
                 "All data on these partitions will be destroyed. This action cannot be undone.\n" +
                 "Unallocated space adjacent to your Windows partition will be automatically reclaimed.",
                FluentMessageSeverity.Danger,
                primaryText: targets.Count > 0 ? "Uninstall Linux " : "Remove components"))
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Linux removal failed");
            LinuxActionStatus = $"An error occurred during removal: {ex.Message}";
        }
    }

    /// <summary>Extracts the disk number from a WMI device id ("\\.\PHYSICALDRIVE0" → 0).</summary>
    private static bool TryGetDiskNumber(string deviceId, out uint number)
    {
        const string marker = "PHYSICALDRIVE";
        var at = deviceId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        number = 0;
        return at >= 0 && uint.TryParse(deviceId[(at + marker.Length)..], out number);
    }

    //   Commands                               ─

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunCheckAsync(CancellationToken ct)
    {
        IsRunning = true;
        ErrorMessage = null;
        Report = null;
        try
        {
            // Held in a local until everything is settled: assigning the observable
            // Report mid-run would render the results UNDERNEATH the still-visible
            // "Reading your hardware…" panel (HasReport and IsRunning both true).
            var report = await _checker.RunAsync(ct);
            _logger.LogInformation("Pre-flight complete - {Count} finding(s)", report.Findings.Count);

            // A leftover OEMDRV from an earlier run has no purpose in preflight: the
            // installer creates a fresh one at install time. And it is iGloo's own
            // label-matched scratch partition, so clean it silently rather than show
            // the user a technical prompt. Best-effort: a busy partition is left for
            // the install phase's delete-and-recreate to handle.
            if (report.SeedLeftovers.Count > 0)
            {
                _logger.LogInformation("Auto-removing {Count} leftover installer partition(s)",
                    report.SeedLeftovers.Count);
                try
                {
                    await _linuxRemoval.RemoveAsync([], report.SeedLeftovers, removingAllLinux: false, ct: ct);
                    report = await _checker.RunAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Auto-cleanup of leftover installer partitions failed (non-fatal)");
                }
            }

            // Hand back any free space sitting directly behind Windows, whoever freed it.
            //
            // The block above only covers staging partitions still present on THIS side.
            // The common case is the other one: the first-boot agent deletes the staging
            // partition from Linux, where the NTFS volume in front of it cannot be
            // resized, so the space is correctly left unallocated - and nothing ever
            // claimed it back. The user was expected to finish up in Disk Management,
            // which is the manual disk work Igloo exists to remove. Runs every check, so
            // it also repairs disks left that way by earlier versions.
            // Deliberately only the disk Windows boots from. That is where Igloo carves
            // its staging partition, and it keeps the operation away from data disks:
            // unallocated space on a secondary drive may be there on purpose, and
            // silently growing someone's backup volume into it would be its own bug.
            try
            {
                var windowsDisk = report.Disks.FirstOrDefault(d => d.Partitions.Any(p => p.IsBoot));
                if (windowsDisk is not null && TryGetDiskNumber(windowsDisk.DeviceId, out var diskNumber))
                {
                    var bytes = await _linuxRemoval.ReclaimFreeSpaceAsync(diskNumber, ct: ct);
                    if (bytes > 0)
                    {
                        _logger.LogInformation("Reclaimed {Gb:N1} GB of unallocated space on disk {Disk}",
                            bytes / (1024.0 * 1024 * 1024), diskNumber);
                        report = await _checker.RunAsync(ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not reclaim adjacent free space (non-fatal)");
            }

            Report = report;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Pre-flight check cancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Pre-flight check failed");
            ErrorMessage = $"System check failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "DisableBitLocker failed");
            BitLockerActionStatus = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RestartToFirmware()
    {
        if (!FluentMessageBox.Confirm(
                "Restart to firmware settings?",
                "Your PC will restart into UEFI firmware settings in 10 seconds.\n\n" +
                "• Find the 'Secure Boot' option\n" +
                "• Set it to Disabled\n" +
                "• Save changes and exit\n\n" +
                "Save all open work before continuing.",
                FluentMessageSeverity.Warning,
                primaryText: "Restart now"))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(FindNativeExe("shutdown.exe"), "/r /fw /t 10")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RestartToFirmware failed");
            FluentMessageBox.Show(
                "Could not restart to firmware",
                $"{ex.Message}\n\n" +
                "You can do it manually: run 'shutdown /r /fw /t 0' in an elevated " +
                "command prompt, or reboot and press the firmware key for your board " +
                "(usually Del or F2).",
                FluentMessageSeverity.Danger);
        }
    }

    //   Helpers                                

    private static string FindNativeExe(string exeName)
    {
        var winRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var sysnative = Path.Combine(winRoot, "Sysnative", exeName);
        return File.Exists(sysnative)
            ? sysnative
            : Path.Combine(winRoot, "System32", exeName);
    }

    //   Partition-bar presentation model                   ─

    private const long UnallocatedThresholdBytes = 32L * 1024 * 1024;

    private List<DiskView> BuildDiskViews()
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
                    ? $"{p.FileSystem} · {ByteFormat.Format(p.SizeBytes)}"
                    : ByteFormat.Format(p.SizeBytes);

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

    // GPT partition-type GUIDs, lowercase and unbraced to match the normalization
    // in ClassifyPartition.
    private const string GptTypeEfi = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B";
    private const string GptTypeMsr = "E3C9E316-0B5C-4DB8-817D-F92DF00215AE";
    private const string GptTypeWindowsRecovery = "DE94BBA4-06D1-4D40-A16A-BFD50179D6AC";
    private const string GptTypeLinuxFilesystem = "0FC63DAF-8483-4772-8E79-3D69D8477DE4";
    private const string GptTypeLinuxSwap = "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F";
    private const string GptTypeLinuxLvm = "E6D6D379-F507-44C2-A23C-238F2A3DF928";
    private const string GptTypeLinuxHome = "933AC7E1-2EB4-4F13-B844-0E14E2AEF915";

    private static (string Kind, string Name) ClassifyPartition(PartitionInfo p)
    {
        var gpt = p.GptType?.Trim('{', '}').ToUpperInvariant();
        switch (gpt)
        {
            case GptTypeEfi:
                return ("Efi", "EFI system");
            case GptTypeMsr:
                return ("Msr", "Microsoft Reserved");
            case GptTypeWindowsRecovery:
                return ("Recovery", "Recovery");
            case GptTypeLinuxFilesystem:
            case GptTypeLinuxSwap:
            case GptTypeLinuxLvm:
            case GptTypeLinuxHome:
                return ("Linux", p.Label ?? "Linux");
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

    //   Property-change hooks                         

    partial void OnReportChanged(PreflightReport? value)
    {
        LinuxInstalls = value?.LinuxInstallations
            .Select(li => new LinuxInstallItem(
                li, li.DisplayName, $"{ByteFormat.Format(li.TotalBytes)} · {li.DiskModel}",
                OnLinuxSelectionChanged))
            .ToList() ?? [];
        OnPropertyChanged(nameof(LinuxInstalls));
        OnPropertyChanged(nameof(HasLinux));
        OnPropertyChanged(nameof(HasSingleLinux));
        OnPropertyChanged(nameof(HasMultipleLinux));
        OnPropertyChanged(nameof(SingleLinux));
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


public sealed record DiskView(string Model, long TotalBytes, string PartitionStyle,
    IReadOnlyList<PartitionSegment> Segments);

public sealed record PartitionSegment(string Name, string Detail, long SizeBytes,
    string Kind, bool IsSystem, bool IsBoot, bool IsUnallocated)
{
    public double Weight => SizeBytes;

    public static PartitionSegment Unallocated(long sizeBytes) => new(
        "Unallocated", ByteFormat.Format(sizeBytes), sizeBytes,
        Kind: "Free", IsSystem: false, IsBoot: false, IsUnallocated: true);
}

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
