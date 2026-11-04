using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Igloo.Core.Abstractions;
using Igloo.Migration.Chromium;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Igloo.Migration.Tests;

[SupportedOSPlatform("windows")]
public sealed class ChromiumLocalStateTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(), $"igloo-localstate-{Guid.NewGuid():N}");

    public ChromiumLocalStateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException ex) { Debug.WriteLine($"Temp cleanup failed for {_root}: {ex.Message}"); }
    }

    [Fact]
    public void GetMasterKey_DpapiWrappedKey_Unprotects()
    {
        if (!OperatingSystem.IsWindows())
            return; // DPAPI exists only on Windows; CI runs Windows.

        var expected = RandomNumberGenerator.GetBytes(32);
        var wrapped = ProtectedData.Protect(expected, null, DataProtectionScope.CurrentUser);
        var withPrefix = Encoding.ASCII.GetBytes("DPAPI").Concat(wrapped).ToArray();
        var json = "{\"os_crypt\":{\"encrypted_key\":\"" +
                   Convert.ToBase64String(withPrefix) + "\"}}";
        File.WriteAllText(Path.Join(_root, "Local State"), json);

        ChromiumLocalState.GetMasterKey(_root).Should().Equal(expected);
    }

    [Fact]
    public void GetMasterKey_AppBoundKey_ThrowsAppBound()
    {
        var json = "{\"os_crypt\":{\"app_bound_encrypted_key\":\"AAAA\"}}";
        File.WriteAllText(Path.Join(_root, "Local State"), json);

        var act = () => ChromiumLocalState.GetMasterKey(_root);
        act.Should().Throw<ChromiumAppBoundException>();
    }

    [Fact]
    public void GetMasterKey_MissingFile_ThrowsFileNotFound()
    {
        var act = () => ChromiumLocalState.GetMasterKey(_root);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void GetMasterKey_KeyWithoutDpapiPrefix_ThrowsInvalidData()
    {
        var json = "{\"os_crypt\":{\"encrypted_key\":\"" +
                   Convert.ToBase64String([1, 2, 3, 4]) + "\"}}";
        File.WriteAllText(Path.Join(_root, "Local State"), json);

        var act = () => ChromiumLocalState.GetMasterKey(_root);
        act.Should().Throw<InvalidDataException>();
    }
}

public sealed class LoginDataReaderTests : IDisposable
{
    private readonly string _dir = Path.Join(
        Path.GetTempPath(), $"igloo-logindata-{Guid.NewGuid():N}");

    public LoginDataReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException ex) { Debug.WriteLine($"Temp cleanup failed for {_dir}: {ex.Message}"); }
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("TRUNCATE")]
    [InlineData("PERSIST")]
    public void Read_ReturnsNonBlacklistedRowsWithoutSourceWrites(string journalMode)
    {
        var dbPath = Path.Join(_dir, "Login Data");
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            switch (journalMode)
            {
                case "DELETE": cmd.CommandText = "PRAGMA journal_mode=DELETE"; break;
                case "TRUNCATE": cmd.CommandText = "PRAGMA journal_mode=TRUNCATE"; break;
                case "PERSIST": cmd.CommandText = "PRAGMA journal_mode=PERSIST"; break;
                default: throw new ArgumentOutOfRangeException(nameof(journalMode));
            }
            cmd.ExecuteNonQuery();
            cmd.CommandText =
                "CREATE TABLE logins (origin_url TEXT, username_value TEXT, " +
                "password_value BLOB, blacklisted_by_user INTEGER);" +
                "INSERT INTO logins VALUES ('https://a.example', 'alice', X'010203', 0);" +
                "INSERT INTO logins VALUES ('https://b.example', '', X'0102', 0);" +
                "INSERT INTO logins VALUES ('https://c.example', 'never', X'0102', 1);";
            cmd.ExecuteNonQuery();
        }

        var sourceFiles = Directory.GetFiles(_dir).ToDictionary(path => path, ChromiumTestFile.HashSharedFile);
        var rows = LoginDataReader.Read(dbPath);

        rows.Should().HaveCount(1);
        rows[0].Origin.Should().Be("https://a.example");
        rows[0].Username.Should().Be("alice");
        rows[0].EncryptedPassword.ToArray().Should().Equal([1, 2, 3]);
        Directory.GetFiles(_dir).Should().BeEquivalentTo(sourceFiles.Keys);
        foreach (var (path, hash) in sourceFiles)
            ChromiumTestFile.HashSharedFile(path).Should().Equal(hash);
    }

    [Fact]
    public void Read_IncludesCommittedLoginStillInActiveWal()
    {
        var dbPath = Path.Join(_dir, "Login Data");
        using (var setup = new SqliteConnection($"Data Source={dbPath}"))
        {
            setup.Open();
            using var command = setup.CreateCommand();
            command.CommandText = "CREATE TABLE logins (origin_url TEXT, username_value TEXT, " +
                                  "password_value BLOB, blacklisted_by_user INTEGER)";
            command.ExecuteNonQuery();
        }

        using var activeBrowser = new SqliteConnection($"Data Source={dbPath}");
        activeBrowser.Open();
        using (var command = activeBrowser.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " +
                                  "INSERT INTO logins VALUES ('https://new.example', 'alice', X'010203', 0)";
            command.ExecuteNonQuery();
        }

        File.Exists(dbPath + "-wal").Should().BeTrue();
        File.Exists(dbPath + "-shm").Should().BeTrue();
        var entriesBefore = Directory.GetFileSystemEntries(_dir);
        var mainBefore = ChromiumTestFile.HashSharedFile(dbPath);
        var walBefore = ChromiumTestFile.HashSharedFile(dbPath + "-wal");
        var rows = LoginDataReader.Read(dbPath);

        rows.Should().ContainSingle();
        rows[0].Origin.Should().Be("https://new.example");
        ChromiumTestFile.HashSharedFile(dbPath).Should().Equal(mainBefore);
        ChromiumTestFile.HashSharedFile(dbPath + "-wal").Should().Equal(walBefore);
        // SQLite updates its existing shared-memory coordination file while reading.
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo(entriesBefore);
    }

    [Fact]
    public void Read_RejectsMissingSharedMemorySidecarBeforeSqliteCanCreateIt()
    {
        var dbPath = Path.Join(_dir, "Login Data");
        using (var setup = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            setup.Open();
            using var command = setup.CreateCommand();
            command.CommandText = "CREATE TABLE logins (origin_url TEXT, username_value TEXT, " +
                                  "password_value BLOB, blacklisted_by_user INTEGER)";
            command.ExecuteNonQuery();
        }

        using var activeBrowser = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        activeBrowser.Open();
        using (var command = activeBrowser.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " +
                                  "INSERT INTO logins VALUES ('https://new.example', 'alice', X'010203', 0)";
            command.ExecuteNonQuery();
        }

        var isolated = Path.Join(_dir, "isolated-login");
        ChromiumTestFile.CopySharedFile(dbPath, isolated);
        ChromiumTestFile.CopySharedFile(dbPath + "-wal", isolated + "-wal");
        File.Exists(isolated + "-shm").Should().BeFalse();

        var act = () => LoginDataReader.Read(isolated);
        act.Should().Throw<IOException>();
        File.Exists(isolated + "-shm").Should().BeFalse();
    }

    [Fact]
    public void Read_ClosedWalDatabaseWithoutSidecarsKeepsDirectoryQuiet()
    {
        var dbPath = Path.Join(_dir, "Login Data");
        using (var browser = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            browser.Open();
            using var command = browser.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; " +
                                  "CREATE TABLE logins (origin_url TEXT, username_value TEXT, " +
                                  "password_value BLOB, blacklisted_by_user INTEGER); " +
                                  "INSERT INTO logins VALUES ('https://closed.example', 'alice', X'010203', 0)";
            command.ExecuteNonQuery();
        }

        File.Exists(dbPath + "-wal").Should().BeFalse();
        File.Exists(dbPath + "-shm").Should().BeFalse();
        var entriesBefore = Directory.GetFileSystemEntries(_dir);

        LoginDataReader.Read(dbPath).Should().ContainSingle()
            .Which.Origin.Should().Be("https://closed.example");
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo(entriesBefore);
    }

    [Fact]
    public void Read_RefusesExclusivelyLockedMainFileWithoutCreatingSidecars()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dbPath = Path.Join(_dir, "Login Data");
        using (var setup = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            setup.Open();
            using var command = setup.CreateCommand();
            command.CommandText = "CREATE TABLE logins (origin_url TEXT, username_value TEXT, " +
                                  "password_value BLOB, blacklisted_by_user INTEGER)";
            command.ExecuteNonQuery();
        }

        using var writer = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None);
        var timer = Stopwatch.StartNew();
        var act = () => LoginDataReader.Read(dbPath);
        var failure = act.Should().Throw<BrowserProfileStagingException>().Which;
        failure.Reason.Should().Be(BrowserProfileStagingFailure.BrowserInUse);
        failure.InnerException.Should().BeOfType<SqliteException>();
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        File.Exists(dbPath + "-wal").Should().BeFalse();
        File.Exists(dbPath + "-shm").Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_InterruptedRollbackJournalUsesCommittedRowsWithoutMutatingSource(bool spill)
    {
        var dbPath = Path.Join(_dir, "Login Data");
        ChromiumRollbackFixture.CreateHotDatabase(dbPath, spill);
        var originalMain = ChromiumTestFile.HashSharedFile(dbPath);
        var originalJournal = ChromiumTestFile.HashSharedFile(dbPath + "-journal");

        LoginDataReader.Read(dbPath).Should().ContainSingle().Which.Username.Should().Be("alice");
        ChromiumTestFile.HashSharedFile(dbPath).Should().Equal(originalMain);
        ChromiumTestFile.HashSharedFile(dbPath + "-journal").Should().Equal(originalJournal);
        if (spill) ChromiumRollbackFixture.VerifyRecovery(dbPath);
    }

    [Fact]
    public void Read_SharingFallbackRefusesHotRecoveryWithoutMutatingSource()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dbPath = Path.Join(_dir, "Login Data");
        ChromiumRollbackFixture.CreateHotDatabase(dbPath);
        var originalMain = ChromiumTestFile.HashSharedFile(dbPath);
        var originalJournal = ChromiumTestFile.HashSharedFile(dbPath + "-journal");
        using (var holder = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var act = () => LoginDataReader.Read(dbPath);
            act.Should().Throw<BrowserProfileStagingException>()
                .Which.Reason.Should().Be(BrowserProfileStagingFailure.RecoveryRequired);
        }

        ChromiumTestFile.HashSharedFile(dbPath).Should().Equal(originalMain);
        ChromiumTestFile.HashSharedFile(dbPath + "-journal").Should().Equal(originalJournal);
        ChromiumRollbackFixture.VerifyRecovery(dbPath);
    }

    [Fact]
    public void Read_SharingFallbackAfterTransitionToColdWalCannotCreateOrChangeSidecars()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dbPath = Path.Join(_dir, "Login Data");
        using (var browser = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            browser.Open();
            using var command = browser.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=PERSIST; " +
                "CREATE TABLE logins(origin_url TEXT, username_value TEXT, password_value BLOB, blacklisted_by_user INTEGER); " +
                "INSERT INTO logins VALUES('https://committed.example', 'alice', X'010203', 0); " +
                "PRAGMA journal_mode=WAL;";
            command.ExecuteNonQuery();
        }
        File.Exists(dbPath + "-wal").Should().BeFalse();
        File.Exists(dbPath + "-shm").Should().BeFalse();
        var originalMain = ChromiumTestFile.HashSharedFile(dbPath);
        var entries = Directory.GetFiles(_dir);
        using var holder = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var act = () => LoginDataReader.Read(dbPath);
        act.Should().Throw<SqliteException>();

        ChromiumTestFile.HashSharedFile(dbPath).Should().Equal(originalMain);
        Directory.GetFiles(_dir).Should().BeEquivalentTo(entries);
    }
}

public sealed class CookieDataReaderTests : IDisposable
{
    private readonly string _dir = Path.Join(
        Path.GetTempPath(), $"igloo-cookies-{Guid.NewGuid():N}");

    public CookieDataReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException ex) { Debug.WriteLine($"Temp cleanup failed for {_dir}: {ex.Message}"); }
    }

    [Fact]
    public void Read_IncludesCommittedCookieStillInActiveWal()
    {
        var dbPath = Path.Join(_dir, "Cookies");
        using (var setup = new SqliteConnection($"Data Source={dbPath}"))
        {
            setup.Open();
            using var command = setup.CreateCommand();
            command.CommandText = "CREATE TABLE cookies (host_key TEXT, name TEXT, path TEXT, encrypted_value BLOB)";
            command.ExecuteNonQuery();
        }

        using var activeBrowser = new SqliteConnection($"Data Source={dbPath}");
        activeBrowser.Open();
        using (var command = activeBrowser.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " +
                                  "INSERT INTO cookies VALUES ('.new.example', 'session', '/', X'010203')";
            command.ExecuteNonQuery();
        }

        File.Exists(dbPath + "-wal").Should().BeTrue();
        File.Exists(dbPath + "-shm").Should().BeTrue();
        var entriesBefore = Directory.GetFileSystemEntries(_dir);
        var mainBefore = ChromiumTestFile.HashSharedFile(dbPath);
        var walBefore = ChromiumTestFile.HashSharedFile(dbPath + "-wal");
        var rows = CookieDataReader.Read(dbPath);

        rows.Should().ContainSingle();
        rows[0].HostKey.Should().Be(".new.example");
        ChromiumTestFile.HashSharedFile(dbPath).Should().Equal(mainBefore);
        ChromiumTestFile.HashSharedFile(dbPath + "-wal").Should().Equal(walBefore);
        // SQLite updates its existing shared-memory coordination file while reading.
        Directory.GetFileSystemEntries(_dir).Should().BeEquivalentTo(entriesBefore);
    }
}

file static class ChromiumTestFile
{
    public static byte[] HashSharedFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    public static void CopySharedFile(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None);
        input.CopyTo(output);
    }
}

public sealed class ChromiumDecryptorTests
{
    // Known-answer: a v10 blob constructed with a fixed key and nonce must
    // decrypt to the exact plaintext through the extractor's path.
    [Fact]
    public void TryDecryptV10_KnownAnswer()
    {
        var key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f" +
                                        "101112131415161718191a1b1c1d1e1f");
        var nonce = Convert.FromHexString("101112131415161718191a1b");
        var plaintext = Encoding.UTF8.GetBytes("s3cret!");

        byte[] body;
        using (var gcm = new AesGcm(key, 16))
        {
            var ct = new byte[plaintext.Length];
            var tag = new byte[16];
            gcm.Encrypt(nonce, plaintext, ct, tag);
            body = ct.Concat(tag).ToArray();
        }
        var blob = Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(body).ToArray();

        ChromiumCredentialExtractor.TryDecryptV10(key, blob, out var password)
            .Should().BeTrue();
        password.Should().Be("s3cret!");
    }

    [Fact]
    public void TryDecryptV10_WrongKey_ReturnsFalse()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var otherKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes("password");

        byte[] body;
        using (var gcm = new AesGcm(key, 16))
        {
            var ct = new byte[plaintext.Length];
            var tag = new byte[16];
            gcm.Encrypt(nonce, plaintext, ct, tag);
            body = ct.Concat(tag).ToArray();
        }
        var blob = Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(body).ToArray();

        ChromiumCredentialExtractor.TryDecryptV10(otherKey, blob, out _)
            .Should().BeFalse();
    }
}
