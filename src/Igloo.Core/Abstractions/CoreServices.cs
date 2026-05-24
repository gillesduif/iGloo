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
}

public sealed record DiskInfo(string DeviceId, string Model, long TotalBytes, long FreeBytes,
    string PartitionStyle, IReadOnlyList<PartitionInfo> Partitions);

public sealed record PartitionInfo(int Index, string FileSystem, long SizeBytes, string? Label,
    bool IsSystem, bool IsBoot);

public enum BitLockerState
{
    NotEncrypted,
    EncryptedAndUnlocked,
    EncryptedAndLocked,
    SuspendedProtection,
    DecryptionInProgress,   // manage-bde -off is running; drive is partially decrypted
    Unknown,
}

public sealed record PreflightFinding(FindingSeverity Severity, string Code, string Message, string? Remediation = null);

public enum FindingSeverity { Info, Warning, Blocker }

public interface IIsoAcquisitionService
{
    Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification spec,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct = default);
}

public sealed record IsoSpecification(string DistroId, Uri DownloadUrl, string ExpectedSha256,
    Uri? GpgSignatureUrl, Uri? GpgKeyUrl);

public sealed record IsoAcquisitionResult(string LocalPath, bool Sha256Verified, bool GpgVerified, long SizeBytes);

public sealed record IsoAcquisitionProgress(IsoAcquisitionPhase Phase, long BytesCompleted,
    long? BytesTotal, string? Message);

public enum IsoAcquisitionPhase { ResolvingMirror, Downloading, VerifyingSha256, VerifyingGpg, Complete }

// ── File staging ─────────────────────────────────────────────────────────────

/// <summary>Stages user files from Windows to a local directory that will be written to OEMDRV.</summary>
public interface IFileStagingService
{
    Task<FileStagingResult> StageAsync(FileStagingRequest request,
        IProgress<FileStagingProgress>? progress, CancellationToken ct = default);
}

public sealed record FileStagingRequest(
    string                   DistroId,
    IReadOnlyList<string>    FolderPaths);

public sealed record FileStagingResult(
    string StagingDirectory,
    long   TotalBytesCopied,
    int    FileCount);

public sealed record FileStagingProgress(
    FileStagingPhase Phase,
    long             BytesCopied,
    long             BytesTotal,
    string           CurrentItem);

public enum FileStagingPhase { Scanning, Copying, Generating, Complete }

// ── USB Writer ────────────────────────────────────────────────────────────────

/// <summary>
/// Writes a bootable ISO to a removable USB drive and places the migration
/// staging directory on an OEMDRV FAT32 partition so Anaconda finds it automatically.
/// </summary>
public interface IUsbWriterService
{
    /// <summary>Returns all USB mass-storage drives currently attached.</summary>
    Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(CancellationToken ct = default);

    /// <summary>
    /// Raw-writes <paramref name="isoPath"/> to the physical drive, then creates a
    /// FAT32 <c>OEMDRV</c> partition in the remaining unallocated space and copies
    /// the contents of <paramref name="stagingDirectory"/> onto it.
    /// </summary>
    Task WriteAsync(
        UsbDriveInfo                 drive,
        string                       isoPath,
        string                       stagingDirectory,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken            ct = default);
}

/// <summary>A USB mass-storage drive available for writing.</summary>
public sealed record UsbDriveInfo(
    int    DriveIndex,
    string Model,
    long   SizeBytes,
    string DeviceId);   // e.g. \\.\PHYSICALDRIVE1

/// <summary>Progress snapshot reported during a USB write operation.</summary>
public sealed record UsbWriteProgress(
    UsbWritePhase Phase,
    long          BytesWritten,
    long          BytesTotal,
    string?       Message);

public enum UsbWritePhase { WritingIso, CreatingOemdrv, PatchingGrub, CopyingFiles, Complete }
