using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Igloo.Migration.Chromium;

/// <summary>
/// Thrown when a Chromium browser protects its master key with App-Bound
/// Encryption (Chrome 127+, current Edge/Brave). Defeating ABE requires
/// executing code inside the browser's own installation through its COM
/// elevation service, which is credential-theft tradecraft; iGloo does not
/// do it. See docs/decisions/011-chromium-credential-migration.md.
/// </summary>
public sealed class ChromiumAppBoundException : Exception
{
    public ChromiumAppBoundException() { }

    public ChromiumAppBoundException(string message) : base(message) { }

    public ChromiumAppBoundException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Extracts the AES-256 master key from a Chromium "Local State" file.</summary>
public static class ChromiumLocalState
{
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();

    /// <summary>
    /// Returns the 32-byte master key for the browser whose user-data root is
    /// <paramref name="userDataRoot"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">No Local State file exists.</exception>
    /// <exception cref="InvalidDataException">The file has no usable encrypted_key.</exception>
    /// <exception cref="ChromiumAppBoundException">The key uses App-Bound Encryption.</exception>
    [SupportedOSPlatform("windows")]
    public static byte[] GetMasterKey(string userDataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDataRoot);

        var localStatePath = Path.Combine(userDataRoot, "Local State");
        if (!File.Exists(localStatePath))
            throw new FileNotFoundException("Chromium Local State not found.", localStatePath);

        using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));

        if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt))
            throw new InvalidDataException("Local State contains no os_crypt section.");

        // ABE-first check: a browser that has migrated to App-Bound Encryption
        // keeps a legacy encrypted_key only during the transition window, but
        // its NEW passwords are already v20 and undecryptable for us. Reporting
        // ABE up front prevents silently migrating a partial, stale set.
        if (osCrypt.TryGetProperty("app_bound_encrypted_key", out _))
        {
            throw new ChromiumAppBoundException(
                $"{userDataRoot} uses App-Bound Encryption; passwords cannot be migrated.");
        }

        if (!osCrypt.TryGetProperty("encrypted_key", out var keyElement))
            throw new InvalidDataException("Local State contains no os_crypt.encrypted_key.");

        byte[] wrapped;
        try
        {
            wrapped = Convert.FromBase64String(keyElement.GetString() ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("os_crypt.encrypted_key is not valid base64.", ex);
        }

        if (wrapped.Length <= DpapiPrefix.Length
            || !wrapped.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
        {
            throw new InvalidDataException("os_crypt.encrypted_key lacks the DPAPI prefix.");
        }

        // Current-user scope: the wizard runs as the migrating user (elevated
        // but in the same logon session), so the user's DPAPI master keys are
        // available. Running as a different account would fail here by design.
        return ProtectedData.Unprotect(
            wrapped.AsSpan(DpapiPrefix.Length).ToArray(),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
    }
}
