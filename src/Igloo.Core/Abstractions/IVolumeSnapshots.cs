namespace Igloo.Core.Abstractions;

/// <summary>A read-only view of a volume as it was at one moment; disposing it removes the snapshot.</summary>
public interface IVolumeSnapshot : IDisposable
{
    /// <summary>The path inside the snapshot of a file or folder on the live volume.</summary>
    string Map(string path);
}

/// <summary>Makes snapshots of a local volume, so files a running program keeps open can be read completely.</summary>
public interface IVolumeSnapshots
{
    /// <summary>A snapshot of the volume that holds the path, or null when Windows cannot make one.</summary>
    IVolumeSnapshot? TryCreate(string pathOnVolume);
}

/// <summary>Raised before anything is copied when the snapshot a staging request asked for cannot be made.</summary>
public sealed class VolumeSnapshotUnavailableException : InvalidOperationException
{
    public VolumeSnapshotUnavailableException() { }

    public VolumeSnapshotUnavailableException(string message) : base(message) { }

    public VolumeSnapshotUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
