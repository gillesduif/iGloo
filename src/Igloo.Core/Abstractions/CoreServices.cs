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
    bool IsSystem, bool IsBoot, long ShrinkableBytes = 0);

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
    Uri? GpgSignatureUrl, Uri? GpgKeyUrl, Uri? GpgSignedDataUrl = null,
    byte[]? GpgKeyData = null, string? GpgKeyFingerprint = null);
// GpgKeyData:        the trusted signing key bundled with the distro (preferred over
//                    fetching GpgKeyUrl — no untrusted keyserver round-trip).
// GpgKeyFingerprint: the pinned 160-bit fingerprint; the signing key MUST match it.
// GpgSignedDataUrl: set for the detached-signature model (Debian/Ubuntu) — it is
// the plain checksum data file (SHA256SUMS) that GpgSignatureUrl detaches-signs.
// When null, GpgSignatureUrl is treated as a Fedora-style clear-signed CHECKSUM.

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

public enum UsbWritePhase { ShrinkingPartition, WritingIso, CreatingOemdrv, PatchingGrub, CopyingFiles, Complete }

// ── Direct Install (no USB) ───────────────────────────────────────────────────

/// <summary>
/// Installs the Fedora KDE Live ISO directly onto a temporary FAT32 partition
/// carved from the target disk - no USB drive required.
/// Only applicable for <see cref="DiskInstallMode.DualBoot"/>.
/// </summary>
public interface IDirectInstallService
{
    /// <summary>
    /// Creates the OEMDRV temp partition on the disk, copies the ISO and
    /// migration artefacts onto it, and configures a GRUB2 EFI that loop-boots
    /// the ISO.  The Windows partition shrink is also performed here.
    /// </summary>
    Task PrepareAsync(
        int                               diskNumber,
        long                              linuxSizeBytes,
        string                            isoPath,
        string                            stagingDirectory,
        InstallerBootSpec                 bootSpec,
        string?                           stage2Url = null,
        IProgress<DirectInstallProgress>? progress  = null,
        CancellationToken                 ct        = default);

    /// <summary>
    /// Writes the UEFI <c>BootNext</c> NVRAM variable so the firmware boots
    /// the GRUB installer exactly once on the next reboot, then returns.
    /// </summary>
    Task RegisterBootEntryAsync(
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken                 ct       = default);
}

public enum DirectInstallPhase
{
    ShrinkingPartition,
    CreatingPartition,
    CopyingIso,
    CopyingFiles,
    ConfiguringGrub,
    RegisteringBootEntry,
    Complete,
}

public sealed record DirectInstallProgress(
    DirectInstallPhase Phase,
    long               BytesWritten = 0,
    long               BytesTotal   = 0,
    string?            Message      = null);

// ── Installation mode ─────────────────────────────────────────────────────────

/// <summary>How Linux will coexist (or not) with existing data on the target disk.</summary>
public enum DiskInstallMode
{
    /// <summary>
    /// The entire target disk is erased and Linux is installed alone.
    /// Kickstart: <c>clearpart --drives=X --all --initlabel</c>.
    /// </summary>
    ReplaceDisk,

    /// <summary>
    /// The main Windows NTFS partition is shrunk to create free space, and Linux
    /// is installed in that space alongside Windows.
    /// Kickstart: <c>clearpart --none</c> (Anaconda uses the unpartitioned free space).
    /// iGloo shrinks the Windows partition during the USB-write step.
    /// </summary>
    DualBoot,
}

// ── Partition resize ──────────────────────────────────────────────────────────

/// <summary>
/// Queries how much a Windows NTFS partition can be shrunk and performs the resize.
/// Required for the <see cref="DiskInstallMode.DualBoot"/> path - Linux needs
/// unpartitioned free space that Anaconda can claim.
/// </summary>
public interface IPartitionResizeService
{
    /// <summary>
    /// Returns the number of bytes by which the largest NTFS partition on
    /// <paramref name="diskNumber"/> can be shrunk (i.e. how much space is available
    /// for a Linux partition without data loss).  Returns 0 if no shrinkable partition
    /// is found or the query fails.
    /// </summary>
    Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default);

    /// <summary>
    /// Shrinks the main NTFS partition on <paramref name="diskNumber"/> so that
    /// <paramref name="linuxSizeBytes"/> of unallocated space is freed for Linux.
    /// Requires the process to be running with administrator privileges.
    /// </summary>
    Task ShrinkAsync(int diskNumber, long linuxSizeBytes,
        IProgress<string>? progress = null, CancellationToken ct = default);
}
