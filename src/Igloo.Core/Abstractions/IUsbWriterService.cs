using System.Collections.Immutable;

namespace Igloo.Core.Abstractions;

public interface IUsbWriterService
{
    
    Task<IReadOnlyList<UsbDriveInfo>> EnumerateDrivesAsync(CancellationToken ct = default);

    Task WriteAsync(
        UsbDriveInfo drive,
        string isoPath,
        string stagingDirectory,
        string expectedIsoSha256,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken ct = default);

    Task WriteBootableFatSeedAsync(
        UsbDriveInfo drive,
        string isoPath,
        string stagingDirectory,
        string expectedIsoSha256,
        UsbStagingIdentity stagingIdentity,
        InstallerBootSpec bootSpec,
        IProgress<UsbWriteProgress>? progress,
        CancellationToken ct = default);
}

public sealed record UsbStagingIdentity(string ManifestSha256, string KickstartSha256)
{
    // Exact loaded-provider payload bytes, relative to the staging root with
    // forward slashes. The USB writer rejects a missing or incomplete list.
    public ImmutableArray<UsbStagedArtifactIdentity> ProviderArtifacts { get; init; } = [];
}

public sealed record UsbStagedArtifactIdentity(string RelativePath, string Sha256);


/// <summary>A removable drive that can be used as the installer target.</summary>
/// <param name="DeviceId">Raw device path, e.g. <c>\\.\PHYSICALDRIVE1</c>.</param>
public sealed record UsbDriveInfo(
    int DriveIndex,
    string Model,
    long SizeBytes,
    string DeviceId,
    string? PnpDeviceId = null,
    int? LogicalSectorBytes = null);


public sealed record UsbWriteProgress(
    UsbWritePhase Phase,
    long BytesWritten,
    long BytesTotal,
    string? Message);

public enum UsbWritePhase { ShrinkingPartition, WritingIso, CreatingOemdrv, PatchingGrub, CopyingFiles, Complete }
