namespace Igloo.Core.Abstractions;


public interface IFileStagingService
{
    Task<FileStagingResult> StageAsync(FileStagingRequest request,
        IProgress<FileStagingProgress>? progress, CancellationToken ct = default);
}


public sealed record FileStagingRequest(
    string DistroId,
    IReadOnlyList<string> FolderPaths)
{
    /// <summary>Logical destination names corresponding to FolderPaths, when Windows known folders are redirected.</summary>
    public IReadOnlyList<string> FolderNames { get; init; } = [];

    /// <summary>Selected browser roots relative to the current Windows user profile.</summary>
    public IReadOnlyList<string> BrowserRelativePaths { get; init; } = [];

    /// <summary>Selected browser metadata, used to stage the portable files for its engine.</summary>
    public IReadOnlyList<Models.BrowserMigration> BrowserProfiles { get; init; } = [];
}


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
