namespace Igloo.Core.Abstractions;


public interface IFileStagingService
{
    Task<FileStagingResult> StageAsync(FileStagingRequest request,
        IProgress<FileStagingProgress>? progress, CancellationToken ct = default);
}


public sealed record FileStagingRequest(
    string DistroId,
    IReadOnlyList<string> FolderPaths);


public sealed record FileStagingResult(
    string StagingDirectory,
    long TotalBytesCopied,
    int FileCount);


public sealed record FileStagingProgress(
    FileStagingPhase Phase,
    long BytesCopied,
    long BytesTotal,
    string CurrentItem);

public enum FileStagingPhase { Scanning, Copying, Generating, Complete }
