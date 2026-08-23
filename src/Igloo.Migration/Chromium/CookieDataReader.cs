using Microsoft.Data.Sqlite;

namespace Igloo.Migration.Chromium;

/// <summary>A row from the Chromium <c>cookies</c> table, still encrypted.</summary>
public sealed record RawCookie(
    string HostKey, string Name, string Path, ReadOnlyMemory<byte> EncryptedValue);

/// <summary>Reads the still-encrypted rows out of a Chromium "Cookies" SQLite database.</summary>
public static class CookieDataReader
{
    // Chromium moved the cookie store under Network/ in M77; both layouts are
    // still found in the wild, so try the current one first.
    private static readonly string[] RelativePaths =
        [System.IO.Path.Join("Network", "Cookies"), "Cookies"];

    /// <summary>The cookie database inside a profile directory, or null if absent.</summary>
    public static string? Locate(string profileDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileDir);
        foreach (var rel in RelativePaths)
        {
            var candidate = System.IO.Path.Join(profileDir, rel);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Returns the cookie rows from the given Cookies file. The file is copied
    /// to a temporary location first because a running browser holds it under a
    /// lock; the copy is deleted afterwards. Session cookies (no encrypted
    /// value) are skipped - they do not survive a browser restart anyway.
    /// </summary>
    public static IReadOnlyList<RawCookie> Read(string cookiesPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(cookiesPath);

        var tempPath = System.IO.Path.Join(
            System.IO.Path.GetTempPath(), $"igloo-cookies-{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(cookiesPath, tempPath, overwrite: true);

            var rows = new List<RawCookie>();
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = tempPath, Mode = SqliteOpenMode.ReadOnly }
                    .ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            // host_key, name and path are the cookie's identity, and the same
            // three columns the Linux side matches on to write the value back.
            command.CommandText =
                "SELECT host_key, name, path, encrypted_value FROM cookies";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var host = reader.GetString(0);
                var name = reader.GetString(1);
                var path = reader.GetString(2);
                var encrypted = reader.GetFieldValue<byte[]>(3);

                if (host.Length == 0 || encrypted.Length == 0)
                    continue;

                rows.Add(new RawCookie(host, name, path, encrypted));
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
