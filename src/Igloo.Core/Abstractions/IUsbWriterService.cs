namespace Igloo.Core.Abstractions;

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
        UsbDriveInfo drive,
        string isoPath,
        string stagingDirectory,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken ct = default);
}

/// <summary>A USB mass-storage drive available for writing.</summary>
/// <param name="DeviceId">Raw device path, e.g. <c>\\.\PHYSICALDRIVE1</c>.</param>
public sealed record UsbDriveInfo(
    int DriveIndex,
    string Model,
    long SizeBytes,
    string DeviceId);

/// <summary>Progress snapshot reported during a USB write operation.</summary>
public sealed record UsbWriteProgress(
    UsbWritePhase Phase,
    long BytesWritten,
    long BytesTotal,
    string? Message);

public enum UsbWritePhase { ShrinkingPartition, WritingIso, CreatingOemdrv, PatchingGrub, CopyingFiles, Complete }
