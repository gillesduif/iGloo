using System.Windows.Input;
using Igloo.Core.Abstractions;

namespace Igloo.App.ViewModels;

/// <summary>Design-time stand-in for <see cref="PreflightViewModel"/>.</summary>
/// <remarks>
/// Previews the finished report: findings present, one Linux install detected, BitLocker
/// clear. Set IsRunning to preview the scanning state instead - that one also starts the
/// magnifier storyboard, so it is the way to check the animation without running the app.
/// </remarks>
public sealed class PreflightDesignData
{
    public bool IsRunning { get; set; }
    public bool IsRemovingLinux { get; set; }
    public bool HasReport { get; set; } = true;
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; } =
        "System check failed: The storage provider did not respond.";

    public string FirmwareDisplay { get; } = "UEFI";
    public bool FirmwareOk { get; } = true;
    public string SecureBootDisplay { get; } = "Enabled";
    public bool SecureBootWarn { get; } = true;
    public string TpmDisplay { get; } = "2.0";
    public string RamDisplay { get; } = "32 GB";
    public string GpuDisplay { get; } = "NVIDIA GeForce RTX 4070";

    public string BitLockerDisplay { get; } = "Not encrypted";
    public bool BitLockerBlocked { get; }
    public bool HasBitLockerActionStatus { get; }
    public string BitLockerActionStatus { get; } = "";

    public bool HasFindings => Findings.Count > 0;

    // Copied verbatim from WindowsPreflightChecker: a preview that invents its own
    // codes and wording teaches the wrong house style. Empty the list to preview the
    // page with the findings section hidden.
    public IReadOnlyList<PreflightFinding> Findings { get; } =
    [
        new PreflightFinding(FindingSeverity.Info, "SECURE_BOOT_ON",
            "Secure Boot is enabled.", null),
        new PreflightFinding(FindingSeverity.Warning, "LOW_RAM",
            "Only 1024 MB of RAM detected. Most Linux distributions require at least 2 GB.",
            "Consider a lightweight distribution such as Debian netinstall or Alpine Linux."),
    ];

    public IReadOnlyList<DiskView> DiskViews { get; } =
    [
        new DiskView("Samsung SSD 990 PRO 2TB", 2_000_398_934_016, "GPT",
        [
            new PartitionSegment("EFI System", "100 MB", 104_857_600, "EFI", true, false, false),
            new PartitionSegment("Windows", "1.27 TB", 1_400_000_000_000, "NTFS", false, true, false),
            new PartitionSegment("Recovery", "800 MB", 838_860_800, "NTFS", false, false, false),
            PartitionSegment.Unallocated(559_000_000_000),
        ]),
    ];

    public bool HasLinux => LinuxInstalls.Count > 0;
    public bool HasSingleLinux => LinuxInstalls.Count == 1;
    public bool HasMultipleLinux => LinuxInstalls.Count > 1;
    public LinuxInstallItem? SingleLinux => LinuxInstalls.Count == 1 ? LinuxInstalls[0] : null;
    public bool HasLinuxActionStatus { get; }
    public string LinuxActionStatus { get; } = "";

    public IReadOnlyList<LinuxInstallItem> LinuxInstalls { get; } =
    [
        new LinuxInstallItem(
            new LinuxInstallation("Fedora Linux 42", 0, "Samsung SSD 990 PRO 2TB",
                [new PartitionInfo(5, "ext4", 120_000_000_000, null, false, false)],
                120_000_000_000, 6),
            "Fedora Linux 42", "112 GB on Samsung SSD 990 PRO 2TB", () => { }),
    ];

    public ICommand RemoveLinuxCommand { get; } = new DesignCommand();
    public ICommand RemoveSelectedLinuxCommand { get; } = new DesignCommand();
    public ICommand DisableBitLockerCommand { get; } = new DesignCommand();
}
