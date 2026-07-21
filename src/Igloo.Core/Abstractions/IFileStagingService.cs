namespace Igloo.Core.Abstractions;

/// <summary>Stages user files from Windows to a local directory that will be written to OEMDRV.</summary>
public interface IFileStagingService
{
    Task<FileStagingResult> StageAsync(FileStagingRequest request,
        IProgress<FileStagingProgress>? progress, CancellationToken ct = default);
}

/// <summary>Which folders to stage for one distro.</summary>
public sealed record FileStagingRequest(
    string DistroId,
    IReadOnlyList<string> FolderPaths);

/// <summary>Where the staged files landed and how much was copied.</summary>
public sealed record FileStagingResult(
    string StagingDirectory,
    long TotalBytesCopied,
    int FileCount);

/// <summary>Progress snapshot reported during file staging.</summary>
public sealed record FileStagingProgress(
    FileStagingPhase Phase,
    long BytesCopied,
    long BytesTotal,
    string CurrentItem);

public enum FileStagingPhase { Scanning, Copying, Generating, Complete }
