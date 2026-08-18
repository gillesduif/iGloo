namespace Igloo.Core.Abstractions;

public interface IUsbWriterService
{
    
    Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(CancellationToken ct = default);

    Task WriteAsync(
        UsbDriveInfo drive,
        string isoPath,
        string stagingDirectory,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken ct = default);
}


/// <summary>A removable drive that can be used as the installer target.</summary>
/// <param name="DeviceId">Raw device path, e.g. <c>\\.\PHYSICALDRIVE1</c>.</param>
public sealed record UsbDriveInfo(
    int DriveIndex,
    string Model,
    long SizeBytes,
    string DeviceId);


public sealed record UsbWriteProgress(
    UsbWritePhase Phase,
    long BytesWritten,
    long BytesTotal,
    string? Message);

public enum UsbWritePhase { ShrinkingPartition, WritingIso, CreatingOemdrv, PatchingGrub, CopyingFiles, Complete }
