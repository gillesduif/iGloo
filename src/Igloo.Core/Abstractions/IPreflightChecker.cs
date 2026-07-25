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
    public required long TotalRamBytes { get; init; }
    public required IReadOnlyList<PreflightFinding> Findings { get; init; }

    
    public IReadOnlyList<LinuxInstallation> LinuxInstallations { get; init; } = [];

    public IReadOnlyList<SeedLeftover> SeedLeftovers { get; init; } = [];
}

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
