using Igloo.Core.Models;

namespace Igloo.Core.Abstractions;

/// <summary>Inspects the machine (firmware, BitLocker, disks, GPU, RAM) before any install step.</summary>
public interface IPreflightChecker
{
    Task<PreflightReport> RunAsync(CancellationToken ct = default);
}

/// <summary>Everything the wizard needs to know about the machine before offering an install.</summary>
public sealed record PreflightReport
{
    public required bool IsUefi { get; init; }
    public required bool SecureBootEnabled { get; init; }
    public required bool TpmPresent { get; init; }
    public required BitLockerState BitLocker { get; init; }
    public required IReadOnlyList<DiskInfo> Disks { get; init; }
    public required string GpuVendor { get; init; }
    public required long TotalRamBytes { get; init; }
    public required IReadOnlyList<PreflightFinding> Findings { get; init; }

    /// <summary>Existing Linux installations found on this machine (empty when none).</summary>
    public IReadOnlyList<LinuxInstallation> LinuxInstallations { get; init; } = [];

    /// <summary>
    /// Leftover iGloo installer partitions (OEMDRV/CIDATA/IGLOOISO) from installs
    /// that predate the agent-side cleanup step. Safe to delete from Windows.
    /// </summary>
    public IReadOnlyList<SeedLeftover> SeedLeftovers { get; init; } = [];
}

/// <summary>
/// One detected Linux installation: a contiguous run of Linux-typed GPT partitions
/// on a single disk. Known limitation: two distros installed in adjacent
/// partitions merge into one group, indistinguishable without mounting them.
/// </summary>
/// <param name="FirmwareEntryIndex">The UEFI Boot#### index of this install's boot
/// entry when it could be paired unambiguously; null otherwise.</param>
public sealed record LinuxInstallation(
    string DisplayName,
    uint DiskNumber,
    string DiskModel,
    IReadOnlyList<PartitionInfo> Partitions,
    long TotalBytes,
    ushort? FirmwareEntryIndex);

/// <summary>A leftover iGloo seed partition (matched by exact label) awaiting cleanup.</summary>
public sealed record SeedLeftover(uint DiskNumber, string DiskModel, PartitionInfo Partition);

/// <summary>One preflight observation, from informational to install-blocking.</summary>
public sealed record PreflightFinding(FindingSeverity Severity, string Code, string Message, string? Remediation = null);

public enum FindingSeverity { Info, Warning, Blocker }

public enum BitLockerState
{
    NotEncrypted,
    EncryptedAndUnlocked,
    EncryptedAndLocked,
    SuspendedProtection,

    /// <summary><c>manage-bde -off</c> is running; the drive is partially decrypted.</summary>
    DecryptionInProgress,

    Unknown,
}
