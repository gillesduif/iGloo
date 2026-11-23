using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Igloo.Migration.Chromium;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Migration.Tests.Chromium;

/// <summary>
/// Covers the cookie loop itself, not just the pieces it calls. The building
/// blocks each had a test; the loop that decides which rows survive did not.
/// </summary>
public sealed class ExtractCookiesTests : IDisposable
{
    private readonly byte[] _key = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    private readonly string _profile = Path.Join(
        Path.GetTempPath(), $"igloo-cookieprofile-{Guid.NewGuid():N}");

    public ExtractCookiesTests() => Directory.CreateDirectory(_profile);

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); }
        catch (IOException ex) { Debug.WriteLine($"Temp cleanup failed for {_profile}: {ex.Message}"); }
    }

    // "v10" || nonce (12) || ciphertext || tag (16), AES-256-GCM - the shape
    // TryDecryptV10Bytes expects.
    private byte[] V10Blob(byte[] plaintext, byte[]? key = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key ?? _key, 16))
            gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. "v10"u8.ToArray(), .. nonce, .. ciphertext, .. tag];
    }

    private void WriteCookieDb(params (string Host, string Name, string Path, byte[] Value)[] rows)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Join(_profile, "Cookies")}");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE cookies (host_key TEXT, name TEXT, path TEXT, encrypted_value BLOB);";
            create.ExecuteNonQuery();
        }

        foreach (var (host, name, path, value) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO cookies VALUES ($h, $n, $p, $v);";
            insert.Parameters.AddWithValue("$h", host);
            insert.Parameters.AddWithValue("$n", name);
            insert.Parameters.AddWithValue("$p", path);
            insert.Parameters.AddWithValue("$v", value);
            insert.ExecuteNonQuery();
        }
    }

    private List<ChromiumCookie> Extract()
    {
        var cookies = new List<ChromiumCookie>();
        ChromiumCredentialExtractor.ExtractCookies(_key, _profile, cookies, NullLogger.Instance);
        return cookies;
    }

    [Fact]
    public void V10Cookie_ArrivesWithItsIdentityAndPlaintext()
    {
        // Bytes, not text: Chromium prefixes the plaintext with a domain hash,
        // so the value has to survive the round trip untouched.
        var plaintext = Encoding.UTF8.GetBytes("session=abc123");
        WriteCookieDb(("example.com", "session", "/", V10Blob(plaintext)));

        var cookies = Extract();

        cookies.Should().ContainSingle();
        cookies[0].HostKey.Should().Be("example.com");
        cookies[0].Name.Should().Be("session");
        cookies[0].Path.Should().Be("/");
        cookies[0].Value.ToArray().Should().Equal(plaintext);
    }

    [Fact]
    public void V20Cookie_IsSkippedWithoutCostingTheRestOfTheJar()
    {
        var keep = Encoding.UTF8.GetBytes("keep=me");
        WriteCookieDb(
            ("a.example", "appbound", "/",
                [.. "v20"u8.ToArray(), .. RandomNumberGenerator.GetBytes(40)]),
            ("b.example", "keep", "/", V10Blob(keep)));

        var cookies = Extract();

        cookies.Should().ContainSingle();
        cookies[0].Name.Should().Be("keep");
        cookies[0].Value.ToArray().Should().Equal(keep);
    }

    [Fact]
    public void LegacyDpapiCookie_IsSkipped()
    {
        WriteCookieDb(("a.example", "old", "/", [1, 2, 3, 4, 5, 6, 7, 8]));

        Extract().Should().BeEmpty();
    }

    [Fact]
    public void V10Cookie_UnderAnotherKey_IsSkipped()
    {
        var stranger = RandomNumberGenerator.GetBytes(32);
        WriteCookieDb(("a.example", "c", "/",
            V10Blob(Encoding.UTF8.GetBytes("nope"), stranger)));

        Extract().Should().BeEmpty();
    }

    [Fact]
    public void MissingCookieDatabase_AddsNothingAndDoesNotThrow()
    {
        var act = Extract;

        act.Should().NotThrow();
        Extract().Should().BeEmpty();
    }
}
