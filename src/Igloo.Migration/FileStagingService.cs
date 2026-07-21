using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Migration;

/// <summary>
/// Copies the user's selected Windows folders to a local staging directory under
/// <c>%LOCALAPPDATA%\Igloo\staging\{distroId}\</c>.
///
/// The staging directory is consumed by <c>FileStagingViewModel</c> which also writes the
/// migration manifest and installer config there before everything is burned to OEMDRV.
/// </summary>
public sealed class FileStagingService : IFileStagingService
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
        var stagingRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "staging", request.DistroId);

        // Clean up any leftover staging from a previous run.
        if (Directory.Exists(stagingRoot))
        {
            _logger.LogDebug("Removing previous staging at {Dir}", stagingRoot);
            Directory.Delete(stagingRoot, recursive: true);
        }
        Directory.CreateDirectory(stagingRoot);

        progress?.Report(new FileStagingProgress(FileStagingPhase.Scanning, 0, 0, "Scanning files…"));
        var (jobs, totalBytes) = ScanFolders(request.FolderPaths, stagingRoot, ct);

        _logger.LogInformation(
            "Staging {Count} file(s) (~{Bytes} bytes) from {Folders} folder(s) to {Dir}",
            jobs.Count, totalBytes, request.FolderPaths.Count, stagingRoot);

        long bytesCopied = await CopyJobsAsync(jobs, totalBytes, progress, ct);

        _logger.LogInformation(
            "Staging complete: {Bytes} bytes copied in {Count} file(s)", bytesCopied, jobs.Count);

        return new FileStagingResult(stagingRoot, bytesCopied, jobs.Count);
    }

    /// <summary>Builds the copy plan: one (source, destination) pair per file, plus a size estimate.</summary>
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
                _logger.LogDebug("Skipping non-existent folder {Folder}", folder);
                continue;
            }

            var folderName = Path.GetFileName(folder);
            var destRoot = Path.Combine(stagingRoot, "files", folderName);

            foreach (var file in Directory.EnumerateFiles(folder, "*", ScanOptions))
            {
                ct.ThrowIfCancellationRequested();

                var relPath = Path.GetRelativePath(folder, file);
                jobs.Add((file, Path.Combine(destRoot, relPath)));

                try
                { totalBytes += new FileInfo(file).Length; }
                catch { /* file may be locked or gone - size estimate only */ }
            }
        }

        return (jobs, totalBytes);
    }

    /// <summary>Copies every job, skipping (with a warning) files that became inaccessible since the scan.</summary>
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

                await using var inStream = new FileStream(
                    src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    BufSize, useAsync: true);
                await using var outStream = new FileStream(
                    dst, FileMode.Create, FileAccess.Write, FileShare.None,
                    BufSize, useAsync: true);

                int read;
                while ((read = await inStream.ReadAsync(buffer, ct)) > 0)
                {
                    await outStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesCopied += read;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Skipping inaccessible file {Path}", src);
            }
        }

        return bytesCopied;
    }
}
