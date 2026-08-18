using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Migration;

public sealed partial class FileStagingService : IFileStagingService
{
    private readonly ILogger<FileStagingService> _logger;

    public FileStagingService(ILogger<FileStagingService> logger)
    {
        _logger = logger;
    }

    // Enumeration options used for both scan and (implicitly) copy phases:
    //   • IgnoreInaccessible  - silently skip directories we can't enter (no throw).
    //   • AttributesToSkip    - skip NTFS junction points and symlinks.
    //     Windows places junctions like "My Music", "My Pictures", "My Videos" inside
    //     Documents; following them either duplicates data or raises UnauthorizedAccessException.
    //     We do NOT add Hidden/System here so that user dotfiles and hidden config folders
    //     are included when the user explicitly selects a folder to stage.
    private static readonly EnumerationOptions ScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public async Task<FileStagingResult> StageAsync(
        FileStagingRequest request,
        IProgress<FileStagingProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stagingRoot = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "staging", request.DistroId);

        // Clean up any leftover staging from a previous run.
        if (Directory.Exists(stagingRoot))
        {
            LogRemovingPreviousStaging(stagingRoot);
            Directory.Delete(stagingRoot, recursive: true);
        }
        Directory.CreateDirectory(stagingRoot);

        progress?.Report(new FileStagingProgress(FileStagingPhase.Scanning, 0, 0, "Scanning files…"));
        var (jobs, totalBytes) = ScanFolders(request.FolderPaths, stagingRoot, ct);

        LogStagingStart(jobs.Count, totalBytes, request.FolderPaths.Count, stagingRoot);

        long bytesCopied = await CopyJobsAsync(jobs, totalBytes, progress, ct).ConfigureAwait(false);

        LogStagingComplete(bytesCopied, jobs.Count);

        return new FileStagingResult(stagingRoot, bytesCopied, jobs.Count);
    }

    
    private (List<(string Source, string Destination)> Jobs, long TotalBytes) ScanFolders(
        IReadOnlyList<string> folderPaths, string stagingRoot, CancellationToken ct)
    {
        var jobs = new List<(string Source, string Destination)>();
        long totalBytes = 0;

        foreach (var folder in folderPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(folder))
            {
                LogSkippingMissingFolder(folder);
                continue;
            }

            var folderName = Path.GetFileName(folder);
            var destRoot = Path.Join(stagingRoot, "files", folderName);

            foreach (var file in Directory.EnumerateFiles(folder, "*", ScanOptions))
            {
                ct.ThrowIfCancellationRequested();

                var relPath = Path.GetRelativePath(folder, file);
                jobs.Add((file, Path.Join(destRoot, relPath)));

                totalBytes += TryGetFileLength(file);
            }
        }

        return (jobs, totalBytes);
    }

    
    private static long TryGetFileLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    
    private async Task<long> CopyJobsAsync(
        List<(string Source, string Destination)> jobs, long totalBytes,
        IProgress<FileStagingProgress>? progress, CancellationToken ct)
    {
        long bytesCopied = 0;
        const int BufSize = 128 * 1024;
        var buffer = new byte[BufSize];

        foreach (var (src, dst) in jobs)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new FileStagingProgress(
                FileStagingPhase.Copying,
                bytesCopied,
                totalBytes,
                Path.GetFileName(src)));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                var inStream = new FileStream(
                    src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    BufSize, useAsync: true);
                await using var inCfg = inStream.ConfigureAwait(false);
                var outStream = new FileStream(
                    dst, FileMode.Create, FileAccess.Write, FileShare.None,
                    BufSize, useAsync: true);
                await using var outCfg = outStream.ConfigureAwait(false);

                int read;
                while ((read = await inStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await outStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    bytesCopied += read;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSkippingInaccessibleFile(ex, src);
            }
        }

        return bytesCopied;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Removing previous staging at {Dir}")]
    private partial void LogRemovingPreviousStaging(string dir);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Staging {Count} file(s) (~{Bytes} bytes) from {Folders} folder(s) to {Dir}")]
    private partial void LogStagingStart(int count, long bytes, int folders, string dir);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Staging complete: {Bytes} bytes copied in {Count} file(s)")]
    private partial void LogStagingComplete(long bytes, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping non-existent folder {Folder}")]
    private partial void LogSkippingMissingFolder(string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping inaccessible file {Path}")]
    private partial void LogSkippingInaccessibleFile(Exception ex, string path);
}
