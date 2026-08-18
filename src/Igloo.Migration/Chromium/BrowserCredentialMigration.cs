using System.Runtime.Versioning;
using System.Security.Cryptography;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging;

namespace Igloo.Migration.Chromium;

/// <summary>
/// Attaches encrypted Chromium credential blobs to manifest browser entries
/// (browser migration Phase 2, docs/decisions/011-chromium-credential-migration.md).
/// </summary>
public static partial class BrowserCredentialMigration
{
    // Browser display name (as detected by MigrationSetupViewModel) to its
    // Chromium user-data root relative to the matching AppData special folder.
    // Opera keeps Local State in the profile root; every other browser uses
    // a "User Data" directory under LocalApplicationData.
    private static readonly Dictionary<string, (Environment.SpecialFolder Base, string Relative)>
        ChromiumRoots = new(StringComparer.Ordinal)
        {
            ["Google Chrome"] = (Environment.SpecialFolder.LocalApplicationData,
                Path.Join("Google", "Chrome", "User Data")),
            ["Microsoft Edge"] = (Environment.SpecialFolder.LocalApplicationData,
                Path.Join("Microsoft", "Edge", "User Data")),
            ["Brave"] = (Environment.SpecialFolder.LocalApplicationData,
                Path.Join("BraveSoftware", "Brave-Browser", "User Data")),
            ["Vivaldi"] = (Environment.SpecialFolder.LocalApplicationData,
                Path.Join("Vivaldi", "User Data")),
            ["Opera"] = (Environment.SpecialFolder.ApplicationData,
                Path.Join("Opera Software", "Opera Stable")),
        };

    /// <summary>
    /// Returns the browser list with <c>credentialsBlob</c> and
    /// <c>includesPasswords</c> set for every Chromium entry whose passwords
    /// could be decrypted and re-encrypted. Entries that cannot be migrated
    /// (App-Bound Encryption, locked database, unknown browser, no saved
    /// logins, or no Linux password supplied) are returned unchanged, so the
    /// manifest stays truthful about what will migrate.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<BrowserMigration> AttachCredentials(
        IReadOnlyList<BrowserMigration> browsers, string? linuxPassword, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(browsers);
        ArgumentNullException.ThrowIfNull(logger);

        var chromiumSelected = browsers.Any(b =>
            string.Equals(b.Engine, "chromium", StringComparison.OrdinalIgnoreCase));
        if (!chromiumSelected)
            return browsers;

        if (string.IsNullOrEmpty(linuxPassword))
        {
            LogNoPassword(logger);
            return browsers;
        }

        var result = new List<BrowserMigration>(browsers.Count);
        foreach (var entry in browsers)
        {
            if (!string.Equals(entry.Engine, "chromium", StringComparison.OrdinalIgnoreCase)
                || !ChromiumRoots.TryGetValue(entry.Name, out var rootSpec))
            {
                result.Add(entry);
                continue;
            }

            result.Add(Enrich(entry, rootSpec, linuxPassword, logger));
        }
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static BrowserMigration Enrich(
        BrowserMigration entry,
        (Environment.SpecialFolder Base, string Relative) rootSpec,
        string linuxPassword, ILogger logger)
    {
        var userDataRoot = Path.Join(
            Environment.GetFolderPath(rootSpec.Base), rootSpec.Relative);
        if (!Directory.Exists(userDataRoot))
        {
            LogRootMissing(logger, userDataRoot);
            return entry;
        }

        var extraction = ChromiumCredentialExtractor.Extract(userDataRoot, logger);
        if (extraction.Logins.Count == 0)
            return entry;

        var payload = CredentialProtector.BuildPayload(entry.Name, extraction.Logins);
        var envelope = CredentialProtector.Protect(payload, linuxPassword);
        CryptographicOperations.ZeroMemory(payload);

        LogAttached(logger, entry.Name, extraction.Logins.Count);
        return entry with
        {
            CredentialsBlob = Convert.ToBase64String(envelope),
            IncludesPasswords = true,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Chromium browsers are selected but no Linux password was supplied; " +
                  "credentials cannot be encrypted for transit and will not migrate")]
    private static partial void LogNoPassword(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Chromium user-data root {Root} not found; skipping password migration")]
    private static partial void LogRootMissing(ILogger logger, string root);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Encrypted {Count} login(s) for {Browser} into the migration manifest")]
    private static partial void LogAttached(ILogger logger, string browser, int count);
}
