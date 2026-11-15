using Igloo.Migration.Chromium;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Igloo.Migration;

public sealed partial class FileStagingService
{
    // Match the first-boot agents' _CHROMIUM_PLAIN_FILES / _CHROMIUM_PLAIN_DIRS.
    // Password extraction is separate. Machine state (including first_party_sets.db),
    // caches and runtime locks are neither portable nor consumed by those agents.
    private static readonly (string Path, bool IsDatabase)[] ChromiumPortableFiles =
    [
        ("Bookmarks", false),
        ("History", true),
        ("Favicons", true),
        ("Top Sites", true),
        ("Network/Cookies", true),
    ];

    private static IEnumerable<CopyJob> EnumerateChromiumJobs(
        string sourceRoot, string destinationRoot, CancellationToken ct)
    {
        var profile = Path.Join(sourceRoot, "Default");
        if (!BrowserDirectoryExists(profile))
            throw new DirectoryNotFoundException("The selected Chromium Default profile is unavailable.");

        foreach (var (relative, isDatabase) in ChromiumPortableFiles)
        {
            ct.ThrowIfCancellationRequested();
            var source = Path.Join(profile, Path.Join(relative.Split('/')));
            var parent = Path.GetDirectoryName(source)!;
            if (parent != profile && !BrowserDirectoryExists(parent)) continue;
            var length = BrowserFileLength(source);
            if (isDatabase)
            {
                // Reserve main + WAL + rollback journal. A consistent backup
                // folds committed pages into one main file; recovery may regrow
                // pages from a journal, but never beyond this measured budget.
                var walLength = BrowserFileLength(source + "-wal");
                var shmLength = BrowserFileLength(source + "-shm");
                var journalLength = BrowserFileLength(source + "-journal");
                if (length is null && (walLength is not null || shmLength is not null || journalLength is not null))
                    throw new IOException("A selected browser database has sidecars but no main file.");
                if (length is not null)
                    length = checked(length.Value + (walLength ?? 0) + (journalLength ?? 0));
            }
            if (length is null) continue;
            yield return new(source,
                Path.Join(destinationRoot, "Default", Path.Join(relative.Split('/'))),
                length.Value, IsBrowser: true, IsSqlite: isDatabase);
        }

        var sessions = Path.Join(profile, "Sessions");
        if (!BrowserDirectoryExists(sessions)) yield break;
        foreach (var source in EnumerateBrowserFiles(sessions, ct))
        {
            yield return new(source,
                Path.Join(destinationRoot, "Default", "Sessions", Path.GetRelativePath(sessions, source)),
                new FileInfo(source).Length, IsBrowser: true);
        }
    }

    private static bool BrowserDirectoryExists(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & FileAttributes.Directory) == 0)
            throw new IOException("A selected browser directory is redirected or is not a directory.");
        return true;
    }

    private static long? BrowserFileLength(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("A selected browser file is redirected or is not a regular file.");
        return new FileInfo(path).Length;
    }

    private long CopyChromiumDatabase(CopyJob job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            SnapshotChromiumDatabase(job, ct);
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) // SQLITE_BUSY / SQLITE_LOCKED
        {
            // The browser still holds the SQLite lock (typically background processes
            // after its windows closed). Rather than stopping the migration, take a
            // byte copy of the database and its journal and let SQLite recover that
            // private copy; the integrity check below still decides whether it is usable.
            _logger.LogWarning("Selected browser database {Path} is locked by a running browser; copying it with its journal instead of a live snapshot",
                Path.GetFileName(job.Source));
            CopyLockedChromiumDatabase(job, ct);
        }
        var length = new FileInfo(job.Destination).Length;
        if (length > job.ExpectedLength)
            throw new IOException("The selected browser database grew beyond the measured staging space.");
        return length;
    }

    private static void CopyLockedChromiumDatabase(CopyJob job, CancellationToken ct)
    {
        if (File.Exists(job.Destination)) File.Delete(job.Destination);
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            ct.ThrowIfCancellationRequested();
            var source = job.Source + suffix;
            if (!File.Exists(source)) continue;
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(job.Destination + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = job.Destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
               }.ToString()))
        {
            copy.Open(); // rolls back a hot journal / replays a WAL on the private copy
            using var command = copy.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                throw new IOException("The selected browser database could not be copied consistently while the browser is running. Close the browser and retry staging.");
        }
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            if (File.Exists(job.Destination + suffix)) File.Delete(job.Destination + suffix);
    }

    private static void SnapshotChromiumDatabase(CopyJob job, CancellationToken ct)
    {
        ChromiumSqliteSnapshot.Read(job.Source, source =>
        {
            ct.ThrowIfCancellationRequested();
            // Reserve a new file; the SQLite connection may only reopen that file.
            using (new FileStream(job.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = job.Destination,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            destination.Open();
            source.BackupDatabase(destination);
            ct.ThrowIfCancellationRequested();
            using var command = destination.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                throw new IOException("The selected browser database snapshot failed its integrity check.");
            return true;
        });
    }
}
