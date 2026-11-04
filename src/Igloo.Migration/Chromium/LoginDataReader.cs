using System.Security.Cryptography;
using Igloo.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace Igloo.Migration.Chromium;

/// <summary>Reads the still-encrypted rows out of a Chromium "Login Data" SQLite database.</summary>
public static class LoginDataReader
{
    /// <summary>
    /// Returns the logins rows (URL, username, encrypted password blob) from
    /// the given Login Data database. SQLite reads a consistent snapshot of
    /// the main file and its write-ahead log, including logins saved while the
    /// browser is open. Rows without a username or password are skipped.
    /// </summary>
    public static IReadOnlyList<RawLogin> Read(string loginDataPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(loginDataPath);

        return ChromiumSqliteSnapshot.Read(loginDataPath, connection =>
        {
            var rows = new List<RawLogin>();
            using var command = connection.CreateCommand();
            // blacklisted_by_user = 1 rows are "never save" entries: they carry
            // no password and are excluded explicitly, not just incidentally.
            command.CommandText =
                "SELECT origin_url, username_value, password_value " +
                "FROM logins WHERE blacklisted_by_user = 0";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var url = reader.GetString(0);
                var username = reader.GetString(1);
                var encrypted = reader.GetFieldValue<byte[]>(2);

                if (url.Length == 0 || username.Length == 0 || encrypted.Length == 0)
                    continue;

                rows.Add(new RawLogin(url, username, encrypted));
            }
            return rows;
        });
    }
}

/// <summary>
/// Reads a browser SQLite database without creating WAL sidecars in its source
/// directory. An active WAL database already has both sidecars; SQLite can
/// then read a consistent snapshot, including committed WAL rows. A database
/// without sidecars is copied while Windows denies concurrent source writes.
/// An already-open Windows write handle can instead be read through SQLite's
/// locking protocol, without shared-memory creation and with a bounded wait.
/// A partial sidecar pair is refused before SQLite can repair it in place.
/// Inactive PERSIST journals are accepted; journals that might need recovery
/// are validated and replayed only beside an owned private main-file copy.
/// </summary>
internal static class ChromiumSqliteSnapshot
{
    public static TResult Read<TResult>(string path, Func<SqliteConnection, TResult> read)
    {
        var walPath = path + "-wal";
        var shmPath = path + "-shm";
        var hasWal = File.Exists(walPath);
        var hasShm = File.Exists(shmPath);
        if (hasWal != hasShm)
            throw new IOException("Browser database has an incomplete SQLite WAL sidecar pair.");

        if (hasWal)
        {
            // Holding both existing sidecars prevents a concurrent Windows
            // unlink/recreate race before SQLite opens its read-only snapshot.
            using var wal = new FileStream(walPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            using var shm = new FileStream(shmPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            return ReadConnection(path, read);
        }

        var snapshotPath = Path.Join(Path.GetTempPath(), $"igloo-browser-{Guid.NewGuid():N}.db");
        var snapshotCreated = false;
        var snapshotJournalCreated = false;
        var recoverSnapshot = false;
        Exception? readFailure = null;
        try
        {
            // With no sidecars, take a main-file snapshot. On Windows this
            // single handle's sharing mode rejects writers and file replacement
            // throughout the copy. Check sidecars again after acquiring it:
            // a browser may have opened between our first check and this lock.
            using (var source = OpenSourceForCopy(path))
            {
                if (source is null)
                    return ReadConnectionWithoutSharedMemory(path, read);
                if (File.Exists(walPath) || File.Exists(shmPath))
                    throw new IOException("Browser database entered WAL mode during snapshot admission.");
                // Hold both source files read-only with writer/replacement
                // exclusion. Any necessary rollback runs only on their copies.
                using var journal = OpenRollbackJournal(path + "-journal");
                byte[]? originalJournalHash = null;
                if (journal is not null)
                {
                    recoverSnapshot = ChromiumRollbackJournal.NeedsRecovery(journal, source.Length);
                    journal.Position = 0;
                    originalJournalHash = SHA256.HashData(journal);
                }
                var originalLength = source.Length;
                var originalHash = SHA256.HashData(source);
                source.Position = 0;

                using (var snapshot = new FileStream(snapshotPath, FileMode.CreateNew,
                           FileAccess.Write, FileShare.None))
                {
                    snapshotCreated = true;
                    source.CopyTo(snapshot);
                }
                if (recoverSnapshot)
                {
                    journal!.Position = 0;
                    using var snapshotJournal = new FileStream(snapshotPath + "-journal", FileMode.CreateNew,
                        FileAccess.Write, FileShare.None);
                    snapshotJournalCreated = true;
                    journal.CopyTo(snapshotJournal);
                }

                source.Position = 0;
                if (source.Length != originalLength ||
                    !SHA256.HashData(source).AsSpan().SequenceEqual(originalHash) ||
                    File.Exists(walPath) || File.Exists(shmPath))
                    throw new IOException("Browser database changed while its snapshot was copied.");
                if (journal is not null)
                {
                    journal.Position = 0;
                    if (!SHA256.HashData(journal).AsSpan().SequenceEqual(originalJournalHash))
                        throw new IOException("Browser rollback journal changed while its snapshot was copied.");
                }
                else if (File.Exists(path + "-journal"))
                    throw new IOException("Browser database entered a rollback transaction during snapshot capture.");
            }

            return ReadConnection(snapshotPath, read, recoverSnapshot);
        }
        catch (Exception error)
        {
            readFailure = error;
            throw;
        }
        finally
        {
            // SQLite may create WAL coordination files next to the private
            // copy; remove all of them, never the browser's own sidecars.
            if (snapshotCreated) CleanupPrivateSnapshot(snapshotPath, snapshotJournalCreated, readFailure);
        }
    }

    private static void CleanupPrivateSnapshot(string snapshotPath, bool snapshotJournalCreated, Exception? readFailure)
    {
        List<Exception>? failures = null;
        foreach (var ownedPath in new[] { snapshotPath + "-shm", snapshotPath + "-wal", snapshotPath + "-journal", snapshotPath })
        {
            // CreateNew failure must never grant ownership of a colliding journal.
            if (ownedPath == snapshotPath + "-journal" && !snapshotJournalCreated) continue;
            try { File.Delete(ownedPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                (failures ??= []).Add(error);
            }
        }
        if (failures is null) return;
        if (readFailure is not null) failures.Insert(0, readFailure);
        throw new IOException("Not all temporary browser snapshot files could be removed.",
            new AggregateException(failures));
    }

    private static FileStream? OpenSourceForCopy(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException error) when (OperatingSystem.IsWindows() &&
            error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
        {
            // Only source-handle sharing/lock violations admit this alternative.
            // Missing files, permissions, copy failures and corruption never do.
            return null;
        }
    }

    private static TResult ReadConnectionWithoutSharedMemory<TResult>(
        string path, Func<SqliteConnection, TResult> read)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        try { connection.Open(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 14) // SQLITE_CANTOPEN after a proven sharing violation
        {
            throw new BrowserProfileStagingException("Close the selected browsers and retry staging.",
                error, BrowserProfileStagingFailure.BrowserInUse);
        }
        using var command = connection.CreateCommand();
        // This must precede the first database read. Ordinary read-only SQLite
        // may create WAL sidecars if another process changed the journal mode.
        // EXCLUSIVE uses no shared-memory file, and Mode=ReadOnly prevents hot
        // journal recovery from changing the source. Busy readers fail promptly.
        command.CommandText = "PRAGMA locking_mode=EXCLUSIVE;";
        command.ExecuteNonQuery();
        // Establish a SQLite read lock before handing off to the backup/reader.
        // Exclusive locking mode retains that lock until this connection closes,
        // keeping committed pages stable even if a writer has a reserved lock.
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema;";
        try { command.ExecuteScalar(); }
        catch (SqliteException error) when (error.SqliteExtendedErrorCode == 776) // SQLITE_READONLY_ROLLBACK
        {
            throw new BrowserProfileStagingException(
                "Open and close the selected browser so it can recover its database, then retry.",
                error, BrowserProfileStagingFailure.RecoveryRequired);
        }
        return read(connection);
    }

    private static FileStream? OpenRollbackJournal(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException) { return null; }
    }

    private static TResult ReadConnection<TResult>(string path, Func<SqliteConnection, TResult> read,
        bool recoverPrivateSnapshot = false)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = recoverPrivateSnapshot ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        connection.Open();
        if (recoverPrivateSnapshot)
        {
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                throw new IOException("The recovered private browser snapshot failed its integrity check.");
        }
        return read(connection);
    }
}
