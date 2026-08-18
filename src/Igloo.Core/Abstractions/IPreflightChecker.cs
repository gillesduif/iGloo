using Igloo.Core.Models;

namespace Igloo.Core.Abstractions;


public interface IPreflightChecker
{
    Task<PreflightReport> RunAsync(CancellationToken ct = default);
}


public sealed record PreflightReport
{
    public required bool IsUefi { get; init; }
    public required bool SecureBootEnabled { get; init; }
    public required bool TpmPresent { get; init; }
    public required BitLockerState BitLocker { get; init; }
    public required IReadOnlyList<DiskInfo> Disks { get; init; }
    public required string GpuVendor { get; init; }

    /// <summary>Marketing name of the selected adapter, e.g. "NVIDIA GeForce RTX 5070".</summary>
    public string? GpuModel { get; init; }

    /// <summary>
    /// PCI id of the selected adapter in Linux "vendor:device" form, e.g. "10de:2c05".
    /// Which driver is correct depends on the specific chip, not the vendor: an RTX 50-series
    /// needs driver 570+ and NVIDIA's open kernel module, while a GTX 10-series must keep the
    /// proprietary one. Null when it could not be determined.
    /// </summary>
    public string? GpuDeviceId { get; init; }

    public required long TotalRamBytes { get; init; }

    /// <summary>The Windows desktop layout, so Linux can be brought up the same way.</summary>
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public required IReadOnlyList<PreflightFinding> Findings { get; init; }

    
    public IReadOnlyList<LinuxInstallation> LinuxInstallations { get; init; } = [];

    public IReadOnlyList<SeedLeftover> SeedLeftovers { get; init; } = [];
}

/// <summary>A Linux install found on disk, with the partitions and boot entry that belong to it.</summary>
/// <param name="FirmwareEntryIndex">The UEFI Boot#### index of this install's boot
/// entry when it could be paired unambiguously; null otherwise.</param>
public sealed record LinuxInstallation(
    string DisplayName,
    uint DiskNumber,
    string DiskModel,
    IReadOnlyList<PartitionInfo> Partitions,
    long TotalBytes,
    ushort? FirmwareEntryIndex);


public sealed record SeedLeftover(uint DiskNumber, string DiskModel, PartitionInfo Partition);

/// <summary>One monitor as Windows currently drives it.</summary>
/// <remarks>
/// <see cref="PnpId"/> is the identity that survives the crossing to Linux: display
/// names and enumeration order differ between the two systems and are not stable across
/// boots, but the monitor's own EDID is the same on both sides. Matching on it is what
/// stops a two-monitor setup from rotating the wrong screen.
/// </remarks>
public sealed record DisplayInfo
{
    /// <summary>EDID manufacturer + product code, e.g. "GSM5B09". Null if unreadable.</summary>
    public string? PnpId { get; init; }

    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public int RefreshHz { get; init; }

    /// <summary>Clockwise rotation in degrees: 0, 90, 180 or 270.</summary>
    public int RotationDegrees { get; init; }

    /// <summary>Top-left position in the virtual desktop, so multi-monitor layout is preserved.</summary>
    public int PositionX { get; init; }
    public int PositionY { get; init; }

    /// <summary>
    /// Windows display scaling in percent (100 = no scaling, 150 = 150%).
    /// KWin positions outputs in LOGICAL pixels while Windows positions are PHYSICAL
    /// pixels; without this the Linux side cannot convert coordinates or reproduce the
    /// scaling, and a scaled 4K panel ends up with gaps between screens. 0 when unknown
    /// (API unavailable) - treat as 100.
    /// </summary>
    public int ScalePercent { get; init; }

    public bool IsPrimary { get; init; }
}


public sealed record PreflightFinding(FindingSeverity Severity, string Code, string Message, string? Remediation = null);

public enum FindingSeverity { Info, Warning, Blocker }

public enum BitLockerState
{
    NotEncrypted,
    EncryptedAndUnlocked,
    EncryptedAndLocked,
    SuspendedProtection,

    
    DecryptionInProgress,

    Unknown,
}
