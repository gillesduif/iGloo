using Igloo.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Igloo.Migration;

public sealed partial class FileStagingService : IFileStagingService
{
    private sealed record CopyJob(string Source, string Destination, long ExpectedLength,
        bool IsBrowser = false, bool IsSqlite = false);

    private readonly ILogger<FileStagingService> _logger;

    public FileStagingService(ILogger<FileStagingService> logger)
    {
        _logger = logger;
    }

    // Enumeration options used for both scan and (implicitly) copy phases:
    //   • IgnoreInaccessible  - false: a selected file must not vanish silently.
    //   • AttributesToSkip    - skip NTFS junction points and symlinks.
    //     Windows places junctions like "My Music", "My Pictures", "My Videos" inside
    //     Documents; following them either duplicates data or raises UnauthorizedAccessException.
    //     We do NOT add Hidden/System here so that user dotfiles and hidden config folders
    //     are included when the user explicitly selects a folder to stage.
    private static readonly EnumerationOptions ScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public async Task<FileStagingResult> StageAsync(
        FileStagingRequest request,
        IProgress<FileStagingProgress>? progress,
        CancellationToken ct = default)
    {
        var folderNames = ValidateRequest(request);
        var stagingRoot = NewStagingRoot(request.DistroId);

        foreach (var folder in request.FolderPaths)
            RejectSourceContainingStaging(folder, stagingRoot);

        // A new generation cannot blend in stale files, and staging never deletes
        // a previous user's data merely because a new wizard run has started.
        Directory.CreateDirectory(stagingRoot);
        try
        {
            progress?.Report(new FileStagingProgress(FileStagingPhase.Scanning, 0, 0, "Scanning files…"));
            var (jobs, totalBytes) = ScanFolders(request.FolderPaths, folderNames, stagingRoot, ct: ct);
            ScanBrowserProfiles(request, stagingRoot, jobs, ref totalBytes, ct: ct);

            LogStagingStart(jobs.Count, totalBytes, request.FolderPaths.Count, stagingRoot);

            long bytesCopied = await CopyJobsAsync(jobs, totalBytes, progress, ct).ConfigureAwait(false);
            // SQLite snapshots fold WAL pages into the database and may be smaller
            // than the reserved source + WAL footprint. Each copy checks its size.

            LogStagingComplete(bytesCopied, jobs.Count);

            return new FileStagingResult(stagingRoot, bytesCopied, jobs.Count);
        }
        catch (Exception original)
        {
            // CopyJobsAsync has unwound and closed both streams by this point.
            // Remove only this new generation; older completed stages are intact.
            try
            {
                RemoveIncompleteGeneration(stagingRoot, request.DistroId);
            }
            catch (Exception cleanup)
            {
                throw new IncompleteFileStagingCleanupException(stagingRoot, original, cleanup);
            }
            throw;
        }
    }

    /// <summary>Measure the same selected files as StageAsync without creating a staging generation.</summary>
    public static Task<long> MeasureSelectedBytesAsync(FileStagingRequest request,
        CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var folderNames = ValidateRequest(request);
        var stagingRoot = NewStagingRoot(request.DistroId);
        foreach (var folder in request.FolderPaths)
            RejectSourceContainingStaging(folder, stagingRoot);
        var (jobs, totalBytes) = ScanFolders(request.FolderPaths, folderNames,
            stagingRoot, createDirectories: false, ct: ct);
        ScanBrowserProfiles(request, stagingRoot, jobs,
            ref totalBytes, createDirectories: false, ct: ct);
        const long fat32Limit = 4L * 1024 * 1024 * 1024;
        if (jobs.Any(job => job.ExpectedLength >= fat32Limit))
            throw new InvalidDataException("A selected migration file exceeds the FAT32 file-size limit.");
        return totalBytes;
    }, ct);

    private static string NewStagingRoot(string distroId) => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Igloo", "staging", distroId, Guid.NewGuid().ToString("N"));

    private static string[] ValidateRequest(FileStagingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.FolderPaths);
        ArgumentNullException.ThrowIfNull(request.FolderNames);
        ArgumentNullException.ThrowIfNull(request.BrowserRelativePaths);
        ArgumentNullException.ThrowIfNull(request.BrowserProfiles);
        if (!IsSafeDistroId(request.DistroId))
            throw new ArgumentException("Distribution ID must be one safe path segment.", nameof(request));

        if (request.FolderNames.Count != 0 && request.FolderNames.Count != request.FolderPaths.Count)
            throw new ArgumentException("Selected folder names must match their source paths.", nameof(request));
        var folderNames = request.FolderNames.Count == 0
            ? request.FolderPaths.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).ToArray()
            : request.FolderNames.ToArray();
        if (folderNames.Any(name => string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                                    name.Contains('/', StringComparison.Ordinal) ||
                                    name.Contains('\\', StringComparison.Ordinal) ||
                                    name.Contains(':', StringComparison.Ordinal) ||
                                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("Each selected folder needs one safe destination name.", nameof(request));
        if (folderNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != folderNames.Length)
            throw new ArgumentException("Selected folders must have distinct names.", nameof(request));
        return folderNames;
    }

    /// <summary>Discard only one generation produced by this staging service.</summary>
    public static void RemoveIncompleteGeneration(string directory, string distroId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!IsSafeDistroId(distroId))
            throw new IOException("The incomplete staging generation has an invalid distribution ID.");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.GetFullPath(Path.Join(local, "Igloo"));
        var staging = Path.Join(root, "staging");
        var parent = Path.Join(staging, distroId);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), parent, comparison) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new IOException("The incomplete staging path is not one generated package.");
        foreach (var path in new[] { root, staging, parent, full })
        {
            if (Directory.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The incomplete staging path is redirected.");
        }
        RemoveDirectoryTreeWithoutLinks(full);
    }

    private static bool IsSafeDistroId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        char.IsAsciiLetterOrDigit(id[0]) && char.IsAsciiLetterOrDigit(id[^1]) &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static void RemoveDirectoryTreeWithoutLinks(string directory)
    {
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The incomplete staging directory is redirected.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The incomplete staging directory contains a redirected entry.");
            if ((attributes & FileAttributes.Directory) != 0)
                RemoveDirectoryTreeWithoutLinks(entry);
            else
                File.Delete(entry);
        }
        Directory.Delete(directory);
    }

    
    private static (List<CopyJob> Jobs, long TotalBytes) ScanFolders(
        IReadOnlyList<string> folderPaths, string[] folderNames,
        string stagingRoot, bool createDirectories = true, CancellationToken ct = default)
    {
        var jobs = new List<CopyJob>();
        long totalBytes = 0;

        for (var index = 0; index < folderPaths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var folder = folderPaths[index];

            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException("A selected source folder is unavailable.");
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A selected source folder resolves through a redirected path.");

            var folderName = folderNames[index];
            var destRoot = Path.Join(stagingRoot, "files", folderName);
            if (createDirectories) Directory.CreateDirectory(destRoot);

            foreach (var file in Directory.EnumerateFiles(folder, "*", ScanOptions))
            {
                ct.ThrowIfCancellationRequested();

                var relPath = Path.GetRelativePath(folder, file);
                var size = new FileInfo(file).Length;
                jobs.Add(new(file, Path.Join(destRoot, relPath), size));
                totalBytes = checked(totalBytes + size);
            }
        }

        return (jobs, totalBytes);
    }

    private static void ScanBrowserProfiles(FileStagingRequest request, string stagingRoot,
        List<CopyJob> jobs, ref long totalBytes,
        bool createDirectories = true, CancellationToken ct = default)
    {
        if (request.BrowserRelativePaths.Count == 0 && request.BrowserProfiles.Count == 0) return;

        var engines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var browser in request.BrowserProfiles)
        {
            ArgumentNullException.ThrowIfNull(browser);
            var key = BrowserProfileStagingPath(browser.SourceRelativePath);
            if (engines.TryGetValue(key, out var engine) && engine != browser.Engine)
                throw new ArgumentException("Selected browser profiles disagree about their engine.", nameof(request));
            engines[key] = browser.Engine;
        }
        var browserPaths = request.BrowserRelativePaths.Concat(
            request.BrowserProfiles.Select(browser => browser.SourceRelativePath));

        var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profileRoot) || !Directory.Exists(profileRoot))
            throw new DirectoryNotFoundException("The current Windows user profile is unavailable.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in browserPaths)
        {
            ct.ThrowIfCancellationRequested();
            var portable = BrowserProfileStagingPath(relative);
            if (!seen.Add(portable)) continue;

            var segments = relative.Replace('\\', '/').Split('/');
            var source = profileRoot;
            foreach (var segment in segments)
            {
                source = Path.Join(source, segment);
                if (!Directory.Exists(source))
                    throw new DirectoryNotFoundException("A selected browser profile is unavailable.");
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A selected browser profile resolves through a redirected path.");
            }

            RejectSourceContainingStaging(source, stagingRoot);

            var destination = Path.Join(stagingRoot, Path.Join(portable.Split('/')));
            if (createDirectories) Directory.CreateDirectory(destination);
            if (engines.TryGetValue(portable, out var selectedEngine) && selectedEngine == "chromium")
            {
                // A fresh profile can have only machine-specific files. Keep its
                // portable root even then, so first boot can distinguish it from
                // a missing selected profile.
                if (createDirectories) Directory.CreateDirectory(Path.Join(destination, "Default"));
                foreach (var job in EnumerateChromiumJobs(source, destination, ct))
                {
                    jobs.Add(job);
                    totalBytes = checked(totalBytes + job.ExpectedLength);
                }
                continue;
            }
            foreach (var file in EnumerateBrowserFiles(source, ct))
            {
                ct.ThrowIfCancellationRequested();
                var relPath = Path.GetRelativePath(source, file);
                var size = new FileInfo(file).Length;
                jobs.Add(new(file, Path.Join(destination, relPath), size, IsBrowser: true));
                totalBytes = checked(totalBytes + size);
            }
        }
    }

    private static void RejectSourceContainingStaging(string source, string stagingRoot)
    {
        var fullSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var fullStaging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(fullSource)
            ? fullSource : fullSource + Path.DirectorySeparatorChar;
        if (string.Equals(fullSource, fullStaging, comparison) ||
            fullStaging.StartsWith(prefix, comparison))
            throw new InvalidOperationException(
                "Choose a source folder that does not contain the iGloo staging directory.");
    }

    private static IEnumerable<string> EnumerateBrowserFiles(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A selected browser profile contains a redirected entry.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    yield return entry;
            }
        }
    }

    /// <summary>Return the only seed-root-relative browser path accepted by staging.</summary>
    public static string BrowserProfileStagingPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new ArgumentException("A selected browser has no source profile path.", nameof(relative));
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." ||
                              s.Contains(':', StringComparison.Ordinal) ||
                              s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("A selected browser profile has an unsafe path.", nameof(relative));
        return "browser-profiles/" + string.Join('/', segments);
    }

    
    private async Task<long> CopyJobsAsync(
        List<CopyJob> jobs, long totalBytes,
        IProgress<FileStagingProgress>? progress, CancellationToken ct)
    {
        long bytesCopied = 0;
        const int BufSize = 128 * 1024;
        var buffer = new byte[BufSize];

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new FileStagingProgress(
                FileStagingPhase.Copying,
                bytesCopied,
                totalBytes,
                Path.GetFileName(job.Source)));

            Directory.CreateDirectory(Path.GetDirectoryName(job.Destination)!);
            try
            {
                var copied = job.IsSqlite
                    ? await Task.Run(() => CopyChromiumDatabase(job, ct), ct).ConfigureAwait(false)
                    : await CopyFileAsync(job, buffer, ct).ConfigureAwait(false);
                bytesCopied = checked(bytesCopied + copied);
            }
            catch (BrowserProfileStagingException)
            {
                // Preserve structured recovery advice from the SQLite reader.
                throw;
            }
            catch (IOException ex) when (job.IsBrowser)
            {
                var reason = ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
                    ? BrowserProfileStagingFailure.BrowserInUse
                    : BrowserProfileStagingFailure.SnapshotUnavailable;
                throw new BrowserProfileStagingException(
                    $"Could not capture selected browser file '{job.Source}'. Close the selected browsers and retry staging.", ex, reason);
            }
            catch (SqliteException ex) when (job.IsBrowser)
            {
                var reason = ex.SqliteErrorCode is 5 or 6
                    ? BrowserProfileStagingFailure.BrowserInUse
                    : BrowserProfileStagingFailure.SnapshotUnavailable;
                throw new BrowserProfileStagingException(
                    $"Could not capture selected browser file '{job.Source}'. Close the selected browsers and retry staging.", ex, reason);
            }
        }

        return bytesCopied;
    }

    private static async Task<long> CopyFileAsync(CopyJob job, byte[] buffer, CancellationToken ct)
    {
        using var inStream = new FileStream(
            job.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
            buffer.Length, useAsync: true);
        using var outStream = new FileStream(
            job.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            buffer.Length, useAsync: true);
        if (inStream.Length != job.ExpectedLength)
            throw new IOException("A selected source file changed after the staging scan.");

        var fileBytesCopied = 0L;
        int read;
        while ((read = await inStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await outStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            fileBytesCopied += read;
        }

        if (fileBytesCopied != job.ExpectedLength)
            throw new IOException("A selected source file changed while it was being staged.");

        // Flush here so the synchronous Dispose below has nothing left to write.
        await outStream.FlushAsync(ct).ConfigureAwait(false);
        return fileBytesCopied;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Staging {Count} file(s) (~{Bytes} bytes) from {Folders} folder(s) to {Dir}")]
    private partial void LogStagingStart(int count, long bytes, int folders, string dir);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Staging complete: {Bytes} bytes copied in {Count} file(s)")]
    private partial void LogStagingComplete(long bytes, int count);

}
