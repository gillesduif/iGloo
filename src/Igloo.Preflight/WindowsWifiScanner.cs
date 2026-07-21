using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Igloo.Core.Models;

namespace Igloo.Preflight;

/// <summary>
/// Exports the machine's saved Wi-Fi profiles (with cleartext keys) using
/// <c>netsh wlan export profile key=clear</c> and parses the resulting XML into
/// <see cref="WifiNetwork"/> records for the migration manifest.
///
/// We export to XML rather than scraping <c>netsh wlan show profile</c> text
/// because the text output is fully localized - label strings like "Key Content"
/// differ per Windows display language and cannot be matched reliably. The XML
/// profile schema (<c>WLANProfile/v1</c>) is fixed regardless of locale.
///
/// Defensive: never throws; returns an empty list on any failure (no WLAN
/// adapter, group-policy lockdown, no saved profiles, etc.).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsWifiScanner
{
    /// <summary>
    /// Scans saved Wi-Fi networks and returns one <see cref="WifiNetwork"/> per
    /// profile. The currently-connected network (if any) is flagged
    /// <see cref="WifiNetwork.IsPrimary"/> = true.
    /// </summary>
    public static IReadOnlyList<WifiNetwork> Scan()
    {
        string? tmpDir = null;
        try
        {
            tmpDir = Path.Combine(Path.GetTempPath(), "igloo-wlan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);

            // Export every WLAN user profile with cleartext keys into tmpDir.
            // Produces one "<Interface>-<SSID>.xml" file per profile.
            var export = RunNetsh($"wlan export profile key=clear folder=\"{tmpDir}\"");
            if (export is null)
                return [];

            var connectedValues = GetConnectedValues();

            var results = new List<WifiNetwork>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(tmpDir, "*.xml"))
            {
                var net = ParseProfile(file, connectedValues);
                if (net is null)
                    continue;
                if (!seen.Add(net.Ssid))
                    continue;   // de-dupe per-adapter duplicates
                results.Add(net);
            }

            return results;
        }
        catch
        {
            return [];
        }
        finally
        {
            if (tmpDir is not null)
            {
                try
                { Directory.Delete(tmpDir, recursive: true); }
                catch { /* best-effort */ }
            }
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    internal static WifiNetwork? ParseProfile(string path, HashSet<string> connectedValues)
    {
        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null)
                return null;

            // SSIDConfig → SSID → name
            var ssidConfig = FirstLocal(root, "SSIDConfig");
            var ssidElem = FirstLocal(ssidConfig, "SSID");
            var ssid = FirstLocal(ssidElem, "name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(ssid))
                return null;

            var hidden = string.Equals(
                FirstLocal(ssidConfig, "nonBroadcast")?.Value?.Trim(),
                "true", StringComparison.OrdinalIgnoreCase);

            // MSM → security → authEncryption → authentication
            var security = FirstLocal(FirstLocal(root, "MSM"), "security");
            var authEnc = FirstLocal(security, "authEncryption");
            var auth = FirstLocal(authEnc, "authentication")?.Value?.Trim() ?? "open";

            // MSM → security → sharedKey → keyMaterial
            var keyMaterial = FirstLocal(FirstLocal(security, "sharedKey"), "keyMaterial")?.Value;

            var (normalisedSecurity, psk) = NormaliseSecurity(auth, keyMaterial);

            return new WifiNetwork
            {
                Ssid = ssid,
                Security = normalisedSecurity,
                Psk = psk,
                Hidden = hidden,
                IsPrimary = connectedValues.Contains(ssid),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a WLANProfile <c>&lt;authentication&gt;</c> value to the agent's
    /// normalised security type and decides whether to carry the key.
    /// </summary>
    internal static (string security, string? psk) NormaliseSecurity(string auth, string? keyMaterial)
    {
        var a = auth.ToUpperInvariant();

        // open / no encryption
        if (a == "OPEN" || a == "NONE")
            return ("open", null);

        // Personal: WPAPSK, WPA2PSK, WPA3SAE - all use a pre-shared key / passphrase.
        if (a.Contains("PSK") || a.Contains("SAE"))
        {
            var psk = string.IsNullOrEmpty(keyMaterial) ? null : keyMaterial;
            // No key recovered (e.g. key not stored) → treat as open-ended; agent
            // will create the profile but the user must enter the password.
            return ("wpa-psk", psk);
        }

        // Enterprise (802.1X): WPA2, WPA3ENT, etc. - credentials are not a simple
        // PSK and cannot be auto-applied. Record for reference only.
        return ("unsupported", null);
    }

    /// <summary>
    /// Returns the set of values that appear after a colon in
    /// <c>netsh wlan show interfaces</c> output. We match on values rather than
    /// labels so this stays locale-independent: the currently-connected SSID and
    /// profile name both appear here, so a profile whose SSID is in this set is
    /// the active connection.
    /// </summary>
    private static HashSet<string> GetConnectedValues()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = RunNetsh("wlan show interfaces");
        if (text is null)
            return values;

        foreach (var line in text.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx < 0 || idx + 1 >= line.Length)
                continue;
            var value = line[(idx + 1)..].Trim();
            if (value.Length > 0)
                values.Add(value);
        }
        return values;
    }

    private static XElement? FirstLocal(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null)
                return null;

            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15_000))
            {
                try
                { p.Kill(); }
                catch { /* ignore */ }
                return null;
            }
            return output;
        }
        catch
        {
            return null;
        }
    }
}
