using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Community.App.Views;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class PreflightViewModel : ObservableObject
{
    private readonly IPreflightWorkflow _preflight;
    private readonly ILogger<PreflightViewModel> _logger;
    private readonly IWindowsMaintenance _maintenance;

    //   Observable state                           

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string _errorTitle = "Run the system check again";

    [ObservableProperty]
    private bool _isRunning;

    // Removal repartitions the disk, so the report on screen is stale while it runs and
    // the wizard must not advance on it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private bool _isRemovingLinux;

    [ObservableProperty]
    private PreflightReport? _report;

    //   Derived / display properties (all recomputed when Report changes)  

    public bool HasReport => Report is not null;
    public bool HasError => ErrorMessage is not null;
    public bool HasFindings => Findings.Count > 0;
    public bool HasBlockers => Findings.Any(f => f.Severity == FindingSeverity.Blocker);

    
    public bool CanProceed => Report is { IsUefi: true, BitLocker: BitLockerState.NotEncrypted } &&
        !HasBlockers && !IsRemovingLinux;

    public string FirmwareDisplay => Report switch
    {
        null => "Not checked",
        { IsUefi: true } => "UEFI",
        _ => "Legacy BIOS",
    };
    public bool FirmwareOk => Report?.IsUefi == true;

    public string SecureBootDisplay => Report switch
    {
        null or { IsUefi: false } => "Not checked",
        { SecureBootEnabled: true } => "Enabled",
        _ => "Disabled",
    };
    public bool SecureBootWarn => Report is { IsUefi: true, SecureBootEnabled: true };

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
                           or BitLockerState.DecryptionInProgress
                           or BitLockerState.Unknown;

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

    public PreflightViewModel(IPreflightWorkflow preflight, IWindowsMaintenance maintenance,
        ILogger<PreflightViewModel> logger)
    {
        _preflight = preflight;
        _maintenance = maintenance;
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
        if (IsRemovingLinux || IsRunning)
            return;
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

        // Singular when the removal touches exactly one partition, so the warning agrees
        // with the list printed directly above it.
        var partitionCount = targets.Sum(t => t.Partitions.Count) + leftovers.Count;
        var dataLine = partitionCount == 1
            ? "All data on this partition will be permanently lost."
            : "All data on these partitions will be permanently lost.";

        // Danger severity: the safe button takes the default, so Enter cancels rather
        // than erasing partitions.
        if (!FluentMessageBox.Confirm(
                targets.Count > 0 ? "Uninstall Linux?" : "Remove iGloo system components?",
                 "This permanently deletes the following:\n\n" + string.Join("\n", lines) + "\n\n" +
                 dataLine + "\n" +
                 "Windows will automatically reclaim the freed space.",
                FluentMessageSeverity.Danger,
                primaryText: targets.Count > 0 ? "Uninstall" : "Remove"))
            return;

        try
        {
            IsRemovingLinux = true;
            ErrorMessage = null;
            // Removal can partially change partitions or boot entries before an
            // error. The old inspection must not allow the wizard to advance.
            Report = null;
            LinuxActionStatus = "Removing…";
            var progress = new Progress<string>(s => LinuxActionStatus = s);
            await _preflight.RemoveLinuxAsync(targets, leftovers, removingAllLinux, progress);

            LinuxActionStatus = null;
            IsRemovingLinux = false;
            if (!IsRunning)
                await RunCheckCommand.ExecuteAsync(null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Linux removal stopped before its result was verified");
            LinuxActionStatus = null;
            ErrorMessage = "Run the system check again before choosing a disk. Linux removal stopped before its result was verified.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Linux removal failed");
            LinuxActionStatus = null;
            ErrorTitle = "Check the disk before continuing";
            ErrorMessage = "Linux removal could not finish. Run the system check again and " +
                "review the affected disk. If its state is unclear, ask for help with the iGloo log.";
        }
        finally
        {
            IsRemovingLinux = false;
        }
    }

    //   Commands                               ─

    /// <summary>
    /// Hands back any free space sitting directly behind Windows, whoever freed it.
    /// </summary>
    /// <remarks>
    /// The common case: the first-boot agent deletes the staging partition from Linux,
    /// where the NTFS volume in front of it cannot be resized, so the space is correctly
    /// left unallocated - and nothing ever claimed it back. Without this step the shrink
    /// estimate only sees what Windows can still give up, the Target Disk page refuses,
    /// and the user is sent to Disk Management, which is the manual disk work iGloo
    /// exists to remove. Observed again on 2026-10-25 (55.86 GB unallocated behind C:,
    /// wizard reporting 24 GiB shrinkable). Present in v0.2-alpha, dropped by the
    /// backend refactor, restored here.
    /// Deliberately only the disk Windows boots from: unallocated space on a secondary
    /// drive may be there on purpose. Non-fatal - a disk we could not tidy is no worse
    /// than the one we found.
    /// </remarks>
    private async Task<PreflightReport> ReclaimAdjacentFreeSpaceAsync(PreflightReport report, CancellationToken ct)
    {
        try
        {
            var windowsDisk = report.Disks.FirstOrDefault(d => d.Partitions.Any(p => p.IsBoot));
            if (windowsDisk is null || !TryGetDiskNumber(windowsDisk.DeviceId, out var diskNumber))
                return report;
            var bytes = await _preflight.ReclaimAdjacentFreeSpaceAsync(diskNumber, ct);
            if (bytes <= 0)
                return report;
            _logger.LogInformation("Reclaimed {Gb:N1} GB of unallocated space on disk {Disk}",
                bytes / (1024.0 * 1024 * 1024), diskNumber);
            return await _preflight.InspectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not reclaim adjacent free space (non-fatal)");
            return report;
        }
    }

    private static bool TryGetDiskNumber(string deviceId, out uint number)
    {
        const string marker = "PHYSICALDRIVE";
        var at = deviceId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        number = 0;
        return at >= 0 && uint.TryParse(deviceId[(at + marker.Length)..], out number);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunCheckAsync(CancellationToken ct)
    {
        if (IsRemovingLinux)
            return;
        IsRunning = true;
        ErrorTitle = "Run the system check again";
        ErrorMessage = null;
        Report = null;
        try
        {
            // Held in a local until everything is settled: assigning the observable
            // Report mid-run would render the results UNDERNEATH the still-visible
            // "Reading your hardware…" panel (HasReport and IsRunning both true).
            var report = await _preflight.InspectAsync(ct);
            report = await ReclaimAdjacentFreeSpaceAsync(report, ct);
            Report = report;
            _logger.LogInformation("Pre-flight complete - {Count} finding(s)", Report.Findings.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Pre-flight check cancelled");
            ErrorMessage = "The system check was cancelled.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Pre-flight check failed");
            if (ex is InvalidOperationException &&
                ex.Message.StartsWith("Open Windows System Information and check Secure Boot State,",
                    StringComparison.Ordinal))
            {
                ErrorTitle = "Check Secure Boot State in Windows";
                ErrorMessage = ex.Message;
            }
            else if (ex is InvalidOperationException &&
                     ex.Message.StartsWith("Check BIOS Mode in Windows System Information,",
                         StringComparison.Ordinal))
            {
                ErrorTitle = "Check BIOS Mode in Windows";
                ErrorMessage = ex.Message;
            }
            else
            {
                ErrorMessage = "If the system check still fails, open the latest iGloo log in " +
                    "your Windows app-data folder (Igloo\\logs) and ask for help.";
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task DisableBitLockerAsync()
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (systemDrive is not { Length: 2 } || !char.IsAsciiLetter(systemDrive[0]) ||
            systemDrive[1] != ':')
        {
            BitLockerActionStatus = "The Windows system drive is unavailable. Run the system check again.";
            return;
        }
        BitLockerActionStatus = $"Starting BitLocker decryption on {systemDrive}…";

        try
        {
            var exitCode = await _maintenance.StartBitLockerDecryptionAsync(systemDrive);

            if (exitCode == 0)
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
                    $"Open an elevated Command Prompt and run 'manage-bde -off {systemDrive}'. " +
                    $"iGloo could not start decryption (exit code {exitCode}).";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "DisableBitLocker failed");
            BitLockerActionStatus = "Open Windows Settings > Privacy & security > Device encryption " +
                "and turn off BitLocker. Run the system check again after decryption finishes.";
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
            _maintenance.RestartToFirmware();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "RestartToFirmware failed");
            FluentMessageBox.Show(
                "Restart into firmware settings manually",
                "Run 'shutdown /r /fw /t 0' in an elevated Command Prompt, or restart " +
                "and press the firmware key for your board " +
                "(usually Del or F2).",
                FluentMessageSeverity.Danger);
        }
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
