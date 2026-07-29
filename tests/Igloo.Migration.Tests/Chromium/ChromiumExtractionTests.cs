using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Igloo.Migration.Chromium;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Igloo.Migration.Tests;

[SupportedOSPlatform("windows")]
public sealed class ChromiumLocalStateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"igloo-localstate-{Guid.NewGuid():N}");

    public ChromiumLocalStateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
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
        File.WriteAllText(Path.Combine(_root, "Local State"), json);

        ChromiumLocalState.GetMasterKey(_root).Should().Equal(expected);
    }

    [Fact]
    public void GetMasterKey_AppBoundKey_ThrowsAppBound()
    {
        var json = "{\"os_crypt\":{\"app_bound_encrypted_key\":\"AAAA\"}}";
        File.WriteAllText(Path.Combine(_root, "Local State"), json);

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
        File.WriteAllText(Path.Combine(_root, "Local State"), json);

        var act = () => ChromiumLocalState.GetMasterKey(_root);
        act.Should().Throw<InvalidDataException>();
    }
}

public sealed class LoginDataReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"igloo-logindata-{Guid.NewGuid():N}");

    public LoginDataReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Read_ReturnsNonBlacklistedRows()
    {
        var dbPath = Path.Combine(_dir, "Login Data");
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE logins (origin_url TEXT, username_value TEXT, " +
                "password_value BLOB, blacklisted_by_user INTEGER);" +
                "INSERT INTO logins VALUES ('https://a.example', 'alice', X'010203', 0);" +
                "INSERT INTO logins VALUES ('https://b.example', '', X'0102', 0);" +
                "INSERT INTO logins VALUES ('https://c.example', 'never', X'0102', 1);";
            cmd.ExecuteNonQuery();
        }

        var rows = LoginDataReader.Read(dbPath);

        rows.Should().HaveCount(1);
        rows[0].Origin.Should().Be("https://a.example");
        rows[0].Username.Should().Be("alice");
        rows[0].EncryptedPassword.ToArray().Should().Equal([1, 2, 3]);
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
