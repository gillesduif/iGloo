using System.Reflection;
using System.Security.Cryptography;
using Igloo.Migration.Chromium;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class ChromiumSnapshotCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Locked_temporary_shm_does_not_prevent_wal_and_main_cleanup_or_hide_read_failure(bool failRead)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Join(Path.GetTempPath(), $"igloo-cleanup-source-{Guid.NewGuid():N}.db");
        string? snapshot = null;
        FileStream? held = null;
        var readError = new InvalidOperationException("fixture read failure");
        try
        {
            using (var setup = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = source, Pooling = false }.ToString()))
            {
                setup.Open();
                using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE selected_data(value TEXT); INSERT INTO selected_data VALUES('committed')";
                command.ExecuteNonQuery();
            }
            var originalSource = SHA256.HashData(File.ReadAllBytes(source));
            var snapshotType = typeof(LoginDataReader).Assembly.GetType("Igloo.Migration.Chromium.ChromiumSqliteSnapshot", true)!;
            var read = snapshotType.GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(int));
            Func<SqliteConnection, int> callback = connection =>
            {
                snapshot = connection.DataSource;
                Assert.NotEqual(source, snapshot);
                File.WriteAllText(snapshot + "-shm", "fixture-owned coordination file");
                File.WriteAllText(snapshot + "-wal", "fixture-owned write-ahead log");
                held = new FileStream(snapshot + "-shm", FileMode.Open, FileAccess.Read, FileShare.Read);
                if (failRead) throw readError;
                return 1;
            };

            var invocation = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, [source, callback]));

            var cleanup = Assert.IsType<IOException>(invocation.InnerException);
            var failures = Assert.IsType<AggregateException>(cleanup.InnerException).InnerExceptions;
            Assert.Contains(failures, error => error is IOException);
            if (failRead) Assert.Contains(readError, failures);
            Assert.NotNull(snapshot);
            Assert.True(File.Exists(snapshot + "-shm"));
            Assert.False(File.Exists(snapshot + "-wal"));
            Assert.False(File.Exists(snapshot));
            Assert.Equal(originalSource, SHA256.HashData(File.ReadAllBytes(source)));
        }
        finally
        {
            held?.Dispose();
            if (snapshot is not null)
            {
                File.Delete(snapshot + "-shm");
                File.Delete(snapshot + "-wal");
                File.Delete(snapshot);
            }
            File.Delete(source);
        }
    }
}
