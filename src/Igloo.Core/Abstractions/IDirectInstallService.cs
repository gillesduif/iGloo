namespace Igloo.Core.Abstractions;

public interface IDirectInstallService
{
    Observation<bool> PreparationAvailability { get; }

    Task<DirectInstallSpaceEstimate> MeasureSpaceAsync(int diskNumber, string isoPath,
        InstallerBootSpec bootSpec, long stagedBytes, CancellationToken ct = default) =>
        throw new NotSupportedException("Direct installation space measurement is unavailable.");

    Task PrepareAsync(
        int diskNumber,
        long linuxSizeBytes,
        string isoPath,
        string stagingDirectory,
        InstallerBootSpec bootSpec,
        Uri? stage2Url = null,
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken ct = default);

    Task RegisterBootEntryAsync(
        IProgress<DirectInstallProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>Read-only capacity estimate; it does not authorize a partition change.</summary>
/// <param name="ShrinkableBytes">Current Windows boot-volume shrink limit.</param>
/// <param name="InstallerBytes">Installer partitions in addition to the requested Linux root.</param>
/// <param name="StagingBytes">Additional Windows space reserved for the pending migration package.</param>
public sealed record DirectInstallSpaceEstimate(long ShrinkableBytes, long InstallerBytes,
    long StagingBytes);

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
    long BytesWritten = 0,
    long BytesTotal = 0,
    string? Message = null);
