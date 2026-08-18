using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using Igloo.Core.Models;

namespace Igloo.Preflight;

[SupportedOSPlatform("windows")]
public static class WindowsWifiScanner
{
    public static IReadOnlyList<WifiNetwork> Scan()
    {
        string? tmpDir = null;
        try
        {
            tmpDir = Path.Join(Path.GetTempPath(), "igloo-wlan-" + Guid.NewGuid().ToString("N"));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
        finally
        {
            if (tmpDir is not null)
                TryDeleteDirectory(tmpDir);
        }
    }

    
    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    //   Private                                ─

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
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    internal static (string security, string? psk) NormaliseSecurity(string auth, string? keyMaterial)
    {
        var a = auth.ToUpperInvariant();

        // open / no encryption
        if (a == "OPEN" || a == "NONE")
            return ("open", null);

        // Personal: WPAPSK, WPA2PSK, WPA3SAE - all use a pre-shared key / passphrase.
        if (a.Contains("PSK", StringComparison.Ordinal) || a.Contains("SAE", StringComparison.Ordinal))
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

    private static HashSet<string> GetConnectedValues()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = RunNetsh("wlan show interfaces");
        if (text is null)
            return values;

        foreach (var line in text.Split('\n'))
        {
            var idx = line.IndexOf(':', StringComparison.Ordinal);
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
                TryKill(p);
                return null;
            }
            return output;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    
    private static bool TryKill(Process p)
    {
        try
        {
            p.Kill();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
}
