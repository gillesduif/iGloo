using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Igloo.Migration.Chromium;

/// <summary>Result of extracting one Chromium browser's saved passwords.</summary>
public sealed record ChromiumExtractionResult(
    IReadOnlyList<ChromiumLogin> Logins,
    int SkippedAppBoundEntries,
    bool UsesAppBoundEncryption)
{
    /// <summary>Decrypted cookies from the same profiles, empty when none migrate.</summary>
    public IReadOnlyList<ChromiumCookie> Cookies { get; init; } = [];

    public bool HasAnything => Logins.Count > 0 || Cookies.Count > 0;

    public static ChromiumExtractionResult Empty { get; } = new([], 0, false);

    public static ChromiumExtractionResult AppBound { get; } = new([], 0, true);
}

/// <summary>
/// Decrypts the saved passwords of one Chromium browser on the Windows side.
/// DPAPI only unlocks in the owning user's logon session, so this must run on
/// Windows as the migrating user; there is no Linux-side equivalent.
/// </summary>
public static partial class ChromiumCredentialExtractor
{
    private static readonly byte[] V10Prefix = "v10"u8.ToArray();
    private static readonly byte[] V20Prefix = "v20"u8.ToArray();

    /// <summary>
    /// Extracts and decrypts every saved login under the given Chromium
    /// user-data root (the directory containing "Local State"). All failures
    /// degrade to a partial or empty result with a log line; this method only
    /// throws on programmer error, never on machine state.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ChromiumExtractionResult Extract(string userDataRoot, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDataRoot);
        ArgumentNullException.ThrowIfNull(logger);

        byte[] masterKey;
        try
        {
            masterKey = ChromiumLocalState.GetMasterKey(userDataRoot);
        }
        catch (ChromiumAppBoundException ex)
        {
            LogAppBound(logger, ex, userDataRoot);
            return ChromiumExtractionResult.AppBound;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or CryptographicException or System.Text.Json.JsonException)
        {
            LogNoMasterKey(logger, ex, userDataRoot);
            return ChromiumExtractionResult.Empty;
        }

        var logins = new List<ChromiumLogin>();
        var cookies = new List<ChromiumCookie>();
        var skippedAppBound = 0;

        foreach (var profileDir in EnumerateProfileDirs(userDataRoot))
        {
            ExtractCookies(masterKey, profileDir, cookies, logger);

            var loginDataPath = Path.Join(profileDir, "Login Data");
            if (!File.Exists(loginDataPath))
                continue;

            IReadOnlyList<RawLogin> rows;
            try
            {
                rows = LoginDataReader.Read(loginDataPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or Microsoft.Data.Sqlite.SqliteException)
            {
                // The classic cause is a running browser holding the file lock.
                LogProfileUnreadable(logger, ex, profileDir);
                continue;
            }

            foreach (var row in rows)
            {
                switch (Classify(row.EncryptedPassword.Span))
                {
                    case EntryKind.V10:
                        if (TryDecryptV10(masterKey, row.EncryptedPassword.Span, out var password))
                            logins.Add(new ChromiumLogin(row.Origin, row.Username, password));
                        break;
                    case EntryKind.V20:
                        skippedAppBound++;
                        break;
                    case EntryKind.LegacyDpapi:
                        if (TryDecryptLegacy(row.EncryptedPassword.ToArray(), out var legacyPassword))
                            logins.Add(new ChromiumLogin(row.Origin, row.Username, legacyPassword));
                        break;
                }
            }
        }

        CryptographicOperations.ZeroMemory(masterKey);
        LogExtracted(logger, userDataRoot, logins.Count, skippedAppBound);
        LogCookies(logger, userDataRoot, cookies.Count);
        return new ChromiumExtractionResult(logins, skippedAppBound, UsesAppBoundEncryption: false)
        {
            Cookies = cookies,
        };
    }

    /// <summary>
    /// Adds one profile's decrypted cookies to <paramref name="cookies"/>.
    /// Cookies are a convenience, not the point of the migration, so every
    /// failure here is logged and skipped rather than allowed to cost the
    /// passwords that come from the same profile.
    /// </summary>
    internal static void ExtractCookies(
        byte[] masterKey, string profileDir, List<ChromiumCookie> cookies, ILogger logger)
    {
        var cookiesPath = CookieDataReader.Locate(profileDir);
        if (cookiesPath is null)
            return;

        IReadOnlyList<RawCookie> rows;
        try
        {
            rows = CookieDataReader.Read(cookiesPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            LogCookiesUnreadable(logger, ex, profileDir);
            return;
        }

        // v20 (App-Bound) cookies are skipped for the same reason v20 passwords
        // are; the rest of the jar still migrates.
        foreach (var row in rows.Where(r => Classify(r.EncryptedValue.Span) == EntryKind.V10))
        {
            if (TryDecryptV10Bytes(masterKey, row.EncryptedValue.Span, out var value))
                cookies.Add(new ChromiumCookie(row.HostKey, row.Name, row.Path, value));
        }
    }

    private static IEnumerable<string> EnumerateProfileDirs(string userDataRoot)
    {
        // "Default" first, then "Profile N" directories. Anything else (Guest,
        // System Profile) holds no user credentials worth migrating.
        var defaultDir = Path.Join(userDataRoot, "Default");
        if (Directory.Exists(defaultDir))
            yield return defaultDir;

        IEnumerable<string> profiles;
        try
        {
            profiles = Directory.EnumerateDirectories(userDataRoot, "Profile *");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var dir in profiles)
            yield return dir;
    }

    private static EntryKind Classify(ReadOnlySpan<byte> blob)
    {
        if (blob.StartsWith(V10Prefix)) return EntryKind.V10;
        if (blob.StartsWith(V20Prefix)) return EntryKind.V20;
        return EntryKind.LegacyDpapi;
    }

    /// <summary>
    /// Decrypts a "v10" password value: prefix (3) || nonce (12) || ciphertext || tag (16),
    /// AES-256-GCM under the browser master key. Public for known-answer testing.
    /// </summary>
    public static bool TryDecryptV10(byte[] masterKey, ReadOnlySpan<byte> blob, out string password)
    {
        password = string.Empty;
        if (!TryDecryptV10Bytes(masterKey, blob, out var plaintext))
            return false;
        try
        {
            password = Encoding.UTF8.GetString(plaintext);
            return password.Length > 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Decrypts a "v10" value to its raw plaintext bytes. Cookies need this
    /// rather than the string form: newer Chromium prefixes a cookie's
    /// plaintext with a hash of its domain, which is not text and must survive
    /// the round trip untouched.
    /// </summary>
    public static bool TryDecryptV10Bytes(
        byte[] masterKey, ReadOnlySpan<byte> blob, out byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(masterKey);

        plaintext = [];
        const int headerLength = 3 + 12;
        if (blob.Length <= headerLength + CredentialProtector.TagLength)
            return false;

        var nonce = blob.Slice(3, 12);
        var body = blob[headerLength..];
        var buffer = new byte[body.Length - CredentialProtector.TagLength];
        try
        {
            using var gcm = new AesGcm(masterKey, CredentialProtector.TagLength);
            gcm.Decrypt(nonce, body[..^CredentialProtector.TagLength],
                body[^CredentialProtector.TagLength..], buffer);
            plaintext = buffer;
            return buffer.Length > 0;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(buffer);
            return false;
        }
    }

    // Pre-v80 Chromium encrypted each value directly with DPAPI. Rare today,
    // but free to support and harmless to keep.
    [SupportedOSPlatform("windows")]
    private static bool TryDecryptLegacy(byte[] blob, out string password)
    {
        password = string.Empty;
        try
        {
            var plaintext = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            password = Encoding.UTF8.GetString(plaintext);
            return password.Length > 0;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private enum EntryKind { V10, V20, LegacyDpapi }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Chromium browser at {Root} uses App-Bound Encryption; its passwords are not migratable")]
    private static partial void LogAppBound(ILogger logger, Exception ex, string root);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not obtain the Chromium master key at {Root}; skipping password migration for this browser")]
    private static partial void LogNoMasterKey(ILogger logger, Exception ex, string root);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not read the Login Data of profile {ProfileDir} (browser running?); skipping this profile")]
    private static partial void LogProfileUnreadable(ILogger logger, Exception ex, string profileDir);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Extracted {LoginCount} Chromium login(s) from {Root} ({Skipped} App-Bound entries skipped)")]
    private static partial void LogExtracted(ILogger logger, string root, int loginCount, int skipped);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Extracted {CookieCount} Chromium cookie(s) from {Root}")]
    private static partial void LogCookies(ILogger logger, string root, int cookieCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not read the cookies of profile {ProfileDir} (browser running?); "
                  + "its passwords still migrate")]
    private static partial void LogCookiesUnreadable(ILogger logger, Exception ex, string profileDir);
}
