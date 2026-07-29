using Microsoft.Data.Sqlite;

namespace Igloo.Migration.Chromium;

/// <summary>Reads the still-encrypted rows out of a Chromium "Login Data" SQLite database.</summary>
public static class LoginDataReader
{
    /// <summary>
    /// Returns the logins rows (URL, username, encrypted password blob) from
    /// the given Login Data file. The file is copied to a temporary location
    /// first because a running browser holds it under a lock; the copy is
    /// deleted afterwards. Rows without a username or password are skipped.
    /// </summary>
    public static IReadOnlyList<RawLogin> Read(string loginDataPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(loginDataPath);

        var tempPath = Path.Combine(
            Path.GetTempPath(), $"igloo-login-{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(loginDataPath, tempPath, overwrite: true);

            var rows = new List<RawLogin>();
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = tempPath, Mode = SqliteOpenMode.ReadOnly }
                    .ToString());
            connection.Open();

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
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp file in %TEMP% is harmless; the OS cleans it.
        }
    }
}
