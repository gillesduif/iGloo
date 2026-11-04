using Microsoft.Data.Sqlite;
using Xunit;

namespace Igloo.Migration.Tests;

internal static class ChromiumRollbackFixture
{
    // Capture real SQLite pages and journal during a spilled, uncommitted
    // transaction. The detached pair represents the files left by a crash.
    public static void CreateHotDatabase(string path, bool spill = true)
    {
        var active = path + ".active";
        try
        {
            using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = active, Pooling = false }.ToString());
            writer.Open();
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA cache_size=10; " +
                "CREATE TABLE logins(origin_url TEXT, username_value TEXT, password_value BLOB, blacklisted_by_user INTEGER); " +
                "INSERT INTO logins VALUES('https://committed.example', 'alice', X'010203', 0); " +
                "CREATE TABLE spill(value BLOB); " +
                "WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM rows WHERE n<256) " +
                "INSERT INTO spill SELECT zeroblob(4096) FROM rows; " +
                "BEGIN IMMEDIATE; UPDATE logins SET username_value='uncommitted';";
            command.ExecuteNonQuery();
            if (spill)
            {
                command.CommandText = "UPDATE spill SET value=randomblob(4096);";
                command.ExecuteNonQuery();
            }

            CopyShared(active, path);
            CopyShared(active + "-journal", path + "-journal");
            Assert.True(new FileInfo(path + "-journal").Length > 512);
            var header = File.ReadAllBytes(path + "-journal").Take(28).ToArray();
            if (spill)
                Assert.Equal(Convert.FromHexString("D9D505F920A163D7"), header.Take(8).ToArray());
            else
            {
                Assert.All(header.Take(8), value => Assert.Equal(0, value));
                Assert.Contains(header.Skip(12), value => value != 0);
            }
            command.CommandText = "ROLLBACK";
            command.ExecuteNonQuery();
        }
        finally
        {
            File.Delete(active + "-journal");
            File.Delete(active);
        }
    }

    public static void VerifyRecovery(string path)
    {
        var recovery = path + ".recovery";
        try
        {
            CopyShared(path, recovery);
            CopyShared(path + "-journal", recovery + "-journal");
            using var recovered = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = recovery, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            recovered.Open();
            using var command = recovered.CreateCommand();
            command.CommandText = "SELECT username_value FROM logins";
            Assert.Equal("alice", command.ExecuteScalar());
            command.CommandText = "PRAGMA quick_check";
            Assert.Equal("ok", command.ExecuteScalar());
        }
        finally
        {
            File.Delete(recovery + "-journal");
            File.Delete(recovery);
        }
    }

    private static void CopyShared(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }
}
