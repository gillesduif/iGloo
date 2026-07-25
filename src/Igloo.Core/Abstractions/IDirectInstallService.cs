namespace Igloo.Core.Abstractions;

public interface IDirectInstallService
{
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
