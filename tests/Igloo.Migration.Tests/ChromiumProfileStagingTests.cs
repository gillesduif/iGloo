using System.Security.Cryptography;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class ChromiumProfileStagingTests : IDisposable
{
    private readonly string _relative = "igloo-chromium-staging-" + Guid.NewGuid().ToString("N");
    private readonly string _distro = "igloo-test-" + Guid.NewGuid().ToString("N");
    private string Source => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _relative);
    private string Staging => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Igloo", "staging", _distro);
    private static FileStagingService Service => new(NullLogger<FileStagingService>.Instance);
    private FileStagingRequest Request => new(_distro, [])
    {
        // The wizard and workflow send both legacy paths and typed selections.
        BrowserRelativePaths = [_relative, _relative],
        BrowserProfiles = [new BrowserMigration
        {
            Name = "Microsoft Edge", Engine = "chromium", SourceRelativePath = _relative,
        }],
    };

    [Fact]
    public async Task Exclusively_locked_Edge_machine_state_does_not_block_portable_profile()
    {
        Write("Default/Bookmarks", "bookmarks");
        Write("Default/Sessions/Session_1", "tabs");
        Write("Local State", "machine-specific-key");
        Write("Default/Cache/LOCK", "cache lock");
        Write("first_party_sets.db", "unneeded runtime state");
        using var held = new FileStream(Path.Join(Source, "first_party_sets.db"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cache = new FileStream(Path.Join(Source, "Default", "Cache", "LOCK"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var measured = await FileStagingService.MeasureSelectedBytesAsync(Request);
        var result = await Service.StageAsync(Request, null);
        var root = Destination(result);

        Assert.Equal(13, measured);
        Assert.Equal(measured, result.TotalBytesCopied);
        Assert.Equal(2, result.FileCount);
        Assert.Equal("bookmarks", await File.ReadAllTextAsync(Path.Join(root, "Default", "Bookmarks")));
        Assert.Equal("tabs", await File.ReadAllTextAsync(Path.Join(root, "Default", "Sessions", "Session_1")));
        Assert.False(File.Exists(Path.Join(root, "first_party_sets.db")));
        Assert.False(File.Exists(Path.Join(root, "Local State")));
        Assert.False(Directory.Exists(Path.Join(root, "Default", "Cache")));
    }

    [Theory]
    [InlineData("History")]
    [InlineData("Favicons")]
    [InlineData("Top Sites")]
    [InlineData("Network/Cookies")]
    public async Task Open_database_snapshot_includes_committed_WAL_rows_without_source_writes(string relative)
    {
        var database = Path.Join(Source, "Default", Path.Join(relative.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Pooling = false,
        }.ToString());
        await writer.OpenAsync();
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " +
            "CREATE TABLE selected_data(value TEXT); INSERT INTO selected_data VALUES('committed in WAL');";
        await command.ExecuteNonQueryAsync();
        Assert.True(new FileInfo(database + "-wal").Length > 0);
        command.CommandText = "BEGIN IMMEDIATE; INSERT INTO selected_data VALUES('not committed');";
        await command.ExecuteNonQueryAsync();
        var originalMain = Hash(database);
        var originalWal = Hash(database + "-wal");

        var measured = await FileStagingService.MeasureSelectedBytesAsync(Request);
        var result = await Service.StageAsync(Request, null);
        var stagedDatabase = Path.Join(Destination(result), "Default", Path.Join(relative.Split('/')));

        Assert.Equal(originalMain, Hash(database));
        Assert.Equal(originalWal, Hash(database + "-wal"));
        Assert.Equal(new FileInfo(stagedDatabase).Length, result.TotalBytesCopied);
        Assert.True(result.TotalBytesCopied <= measured);
        Assert.Equal(1, result.FileCount);
        Assert.False(File.Exists(stagedDatabase + "-wal"));
        Assert.False(File.Exists(stagedDatabase + "-shm"));
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = stagedDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        await snapshot.OpenAsync();
        using var read = snapshot.CreateCommand();
        read.CommandText = "SELECT value FROM selected_data";
        Assert.Equal("committed in WAL", await read.ExecuteScalarAsync());
        read.CommandText = "SELECT COUNT(*) FROM selected_data";
        Assert.Equal(1L, await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_new_profile_without_portable_files_still_has_the_root_required_by_first_boot()
    {
        Write("Default/Preferences", "machine preferences");

        var result = await Service.StageAsync(Request, null);

        Assert.Equal(0, result.TotalBytesCopied);
        Assert.Equal(0, result.FileCount);
        Assert.True(Directory.Exists(Path.Join(Destination(result), "Default")));
        Assert.False(File.Exists(Path.Join(Destination(result), "Default", "Preferences")));
    }

    [Theory]
    [InlineData("History", false)]
    [InlineData("Favicons", false)]
    [InlineData("Top Sites", false)]
    [InlineData("Network/Cookies", false)]
    [InlineData("History", true)]
    [InlineData("Favicons", true)]
    [InlineData("Top Sites", true)]
    [InlineData("Network/Cookies", true)]
    public async Task Persist_database_with_retained_journal_is_captured_without_source_writes(
        string relative, bool keepBrowserOpen)
    {
        if (keepBrowserOpen && !OperatingSystem.IsWindows()) return;
        var database = Path.Join(Source, "Default", Path.Join(relative.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Pooling = false }.ToString());
        {
            await writer.OpenAsync();
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=PERSIST; " +
                "CREATE TABLE selected_data(value TEXT); " +
                "INSERT INTO selected_data VALUES('committed with retained journal');";
            await command.ExecuteNonQueryAsync();
        }
        if (!keepBrowserOpen) await writer.CloseAsync();

        // Real SQLite PERSIST commits leave a nonempty journal with its header zeroed.
        Assert.True(new FileInfo(database + "-journal").Length > 512);
        var journalHeader = new byte[28];
        using (var journal = new FileStream(database + "-journal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await journal.ReadExactlyAsync(journalHeader);
        Assert.All(journalHeader, value => Assert.Equal(0, value));
        var originalMain = Hash(database);
        var originalJournal = Hash(database + "-journal");
        var originalEntries = Directory.GetFileSystemEntries(Path.GetDirectoryName(database)!);
        var measured = await FileStagingService.MeasureSelectedBytesAsync(Request);

        var result = await Service.StageAsync(Request, null);
        var stagedDatabase = Path.Join(Destination(result), "Default", Path.Join(relative.Split('/')));

        Assert.Equal(originalMain, Hash(database));
        Assert.Equal(originalJournal, Hash(database + "-journal"));
        Assert.Equal(originalEntries, Directory.GetFileSystemEntries(Path.GetDirectoryName(database)!));
        Assert.True(result.TotalBytesCopied <= measured);
        Assert.Equal(1, result.FileCount);
        Assert.False(File.Exists(stagedDatabase + "-journal"));
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = stagedDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await snapshot.OpenAsync();
        using var read = snapshot.CreateCommand();
        read.CommandText = "SELECT value FROM selected_data";
        Assert.Equal("committed with retained journal", await read.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Interrupted_rollback_transaction_is_staged_from_committed_data_without_source_writes(bool spill)
    {
        var database = Path.Join(Source, "Default", "History");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        ChromiumRollbackFixture.CreateHotDatabase(database, spill);
        var originalMain = Hash(database);
        var originalJournal = Hash(database + "-journal");

        var measured = await FileStagingService.MeasureSelectedBytesAsync(Request);
        var result = await Service.StageAsync(Request, null);

        Assert.Equal(originalMain, Hash(database));
        Assert.Equal(originalJournal, Hash(database + "-journal"));
        Assert.True(result.TotalBytesCopied <= measured);
        var staged = Path.Join(Destination(result), "Default", "History");
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = staged, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await snapshot.OpenAsync();
        using var read = snapshot.CreateCommand();
        read.CommandText = "SELECT username_value FROM logins";
        Assert.Equal("alice", await read.ExecuteScalarAsync());
        Assert.False(File.Exists(staged + "-journal"));
    }

    [Fact]
    public async Task Unspilled_rollback_transaction_stages_only_committed_rows_without_source_writes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var database = Path.Join(Source, "Default", "History");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = database, Pooling = false }.ToString()))
        {
            await writer.OpenAsync();
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=PERSIST; " +
                "CREATE TABLE selected_data(value TEXT); INSERT INTO selected_data VALUES('committed'); " +
                "BEGIN IMMEDIATE; UPDATE selected_data SET value='uncommitted';";
            await command.ExecuteNonQueryAsync();
            var originalMain = Hash(database);
            var originalJournal = Hash(database + "-journal");

            var activeResult = await Service.StageAsync(Request, null);

            Assert.Equal(originalMain, Hash(database));
            Assert.Equal(originalJournal, Hash(database + "-journal"));
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Join(Destination(activeResult), "Default", "History"),
                Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            await snapshot.OpenAsync();
            using var read = snapshot.CreateCommand();
            read.CommandText = "SELECT value FROM selected_data";
            Assert.Equal("committed", await read.ExecuteScalarAsync());
            command.CommandText = "ROLLBACK";
            await command.ExecuteNonQueryAsync();
        }

        var result = await Service.StageAsync(Request, null);
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public async Task Spilled_rollback_transaction_is_copied_as_its_committed_state_without_touching_the_source()
    {
        // A browser still holding a write transaction (background processes after
        // its windows closed) must not stop the migration: the database and its
        // rollback journal are byte-copied and SQLite rolls the private copy back to
        // the last committed state. The source bytes are never written.
        if (!OperatingSystem.IsWindows()) return;
        var database = Path.Join(Source, "Default", "History");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        WriteDatabase(database, 1024 * 1024);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Pooling = false }.ToString());
        await writer.OpenAsync();
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA cache_size=10; BEGIN IMMEDIATE; " +
            "UPDATE selected_data SET value=randomblob(1024*1024);";
        await command.ExecuteNonQueryAsync();
        var originalMain = Hash(database);
        var originalJournal = Hash(database + "-journal");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var result = await Service.StageAsync(Request, null);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Locked copy took {timer.Elapsed}.");
        Assert.Equal(originalMain, Hash(database));
        Assert.Equal(originalJournal, Hash(database + "-journal"));
        Assert.Equal(1, result.FileCount);
        var staged = Path.Join(Destination(result), "Default", "History");
        Assert.False(File.Exists(staged + "-journal"));
        using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = staged, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await snapshot.OpenAsync();
            using var read = snapshot.CreateCommand();
            read.CommandText = "SELECT length(value) FROM selected_data";
            Assert.Equal(1024L * 1024, await read.ExecuteScalarAsync());
            read.CommandText = "SELECT value = zeroblob(1024*1024) FROM selected_data";
            Assert.Equal(1L, await read.ExecuteScalarAsync());
        }
        command.CommandText = "ROLLBACK";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Exclusively_locked_bookmarks_fail_with_close_browser_action_and_can_retry()
    {
        if (!OperatingSystem.IsWindows()) return;
        Write("Default/Bookmarks", "bookmarks");
        using (var held = new FileStream(Path.Join(Source, "Default", "Bookmarks"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<BrowserProfileStagingException>(() => Service.StageAsync(Request, null));
            Assert.Contains("Close the selected browsers", error.Message, StringComparison.Ordinal);
            Assert.Equal(BrowserProfileStagingFailure.BrowserInUse, error.Reason);
            Assert.Empty(Directory.GetDirectories(Staging));
        }

        var result = await Service.StageAsync(Request, null);
        Assert.Equal("bookmarks", await File.ReadAllTextAsync(Path.Join(Destination(result), "Default", "Bookmarks")));
    }

    [Fact]
    public async Task Orphan_rollback_journal_is_not_silently_omitted_from_selected_profile()
    {
        Write("Default/History-journal", "orphan journal");
        var journal = Path.Join(Source, "Default", "History-journal");
        var original = Hash(journal);

        var measured = await Assert.ThrowsAsync<IOException>(() => FileStagingService.MeasureSelectedBytesAsync(Request));
        var staged = await Assert.ThrowsAsync<IOException>(() => Service.StageAsync(Request, null));

        Assert.Contains("sidecars but no main file", measured.Message, StringComparison.Ordinal);
        Assert.Contains("sidecars but no main file", staged.Message, StringComparison.Ordinal);
        Assert.Equal(original, Hash(journal));
        Assert.False(File.Exists(Path.Join(Source, "Default", "History")));
        Assert.Empty(Directory.GetDirectories(Staging));
    }

    [Fact]
    public async Task Corrupt_database_is_not_reported_as_a_successful_snapshot()
    {
        Write("Default/History", "not a database");
        await Assert.ThrowsAsync<BrowserProfileStagingException>(() => Service.StageAsync(Request, null));
        Assert.Empty(Directory.GetDirectories(Staging));
        Assert.Equal("not a database", await File.ReadAllTextAsync(Path.Join(Source, "Default", "History")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_database_is_captured_unless_it_grew_beyond_the_scanned_budget(bool growAfterScan)
    {
        var database = Path.Join(Source, "Default", "History");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        WriteDatabase(database, 12);
        var original = Hash(database);
        var measured = await FileStagingService.MeasureSelectedBytesAsync(Request);
        var progress = new InlineProgress(p =>
        {
            if (growAfterScan && p.Phase == FileStagingPhase.Copying)
                WriteDatabase(database, 128 * 1024);
        });

        if (growAfterScan)
        {
            var error = await Assert.ThrowsAsync<BrowserProfileStagingException>(() => Service.StageAsync(Request, progress));
            Assert.Contains("grew beyond", error.InnerException!.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetDirectories(Staging));
        }
        else
        {
            var result = await Service.StageAsync(Request, progress);
            Assert.Equal(measured, result.TotalBytesCopied);
            Assert.Equal(original, Hash(database));
            Assert.False(File.Exists(database + "-wal"));
            Assert.False(File.Exists(database + "-shm"));
        }
    }

    private static void WriteDatabase(string path, int size)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS selected_data(value BLOB); " +
            "DELETE FROM selected_data; INSERT INTO selected_data VALUES(zeroblob($size));";
        command.Parameters.AddWithValue("$size", size);
        command.ExecuteNonQuery();
    }

    private sealed class InlineProgress(Action<FileStagingProgress> report) : IProgress<FileStagingProgress>
    {
        public void Report(FileStagingProgress value) => report(value);
    }

    [Fact]
    public async Task An_explicitly_selected_folder_keeps_files_named_like_browser_machine_state()
    {
        Write("Documents/first_party_sets.db", "selected by user");
        var result = await Service.StageAsync(new(_distro, [Path.Join(Source, "Documents")]), null);
        Assert.Equal("selected by user", await File.ReadAllTextAsync(
            Path.Join(result.StagingDirectory, "files", "Documents", "first_party_sets.db")));
    }

    private string Destination(FileStagingResult result) => Path.Join(result.StagingDirectory,
        Path.Join(FileStagingService.BrowserProfileStagingPath(_relative).Split('/')));

    private void Write(string relative, string value)
    {
        var path = Path.Join(Source, Path.Join(relative.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
    }

    public void Dispose()
    {
        foreach (var path in new[] { Source, Staging })
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
