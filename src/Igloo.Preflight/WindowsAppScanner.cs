using System.Runtime.Versioning;
using Igloo.Core.Models;
using Microsoft.Win32;

namespace Igloo.Preflight;

/// <summary>
/// Reads installed Windows applications from the registry uninstall keys and
/// matches them against a curated table of Linux equivalents.
///
/// Called once during the Migration Setup wizard step to populate the
/// "Suggested apps" checklist.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAppScanner
{
    // ── Registry paths that list installed applications ───────────────────────

    private static readonly string[] HklmUninstallPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",  // 32-bit on 64-bit
    ];

    private const string HkcuUninstallPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // ── Mapping: Windows keyword(s) → Linux equivalent ────────────────────────
    //
    // Each entry is:   (string[] keywords, linuxAppName, flatpakId, nativePackage)
    //
    // Keywords are matched case-insensitively against the registry DisplayName.
    // Order within this array does not affect output ordering.

    private static readonly AppMapping[] Mappings =
    [
        // ── Media ─────────────────────────────────────────────────────────────
        new(["vlc"],
            "VLC media player",     "org.videolan.VLC",                  null),
        new(["spotify"],
            "Spotify",              "com.spotify.Client",                null),
        new(["audacity"],
            "Audacity",             "org.audacityteam.Audacity",         null),
        new(["handbrake"],
            "HandBrake",            "fr.handbrake.ghb",                  null),
        new(["mpc-hc", "media player classic"],
            "MPV",                  "io.mpv.Mpv",                        null),

        // ── Gaming ────────────────────────────────────────────────────────────
        new(["steam"],
            "Steam",                "com.valvesoftware.Steam",           null),

        // ── Communication ─────────────────────────────────────────────────────
        new(["discord"],
            "Discord",              "com.discordapp.Discord",            null),
        new(["slack"],
            "Slack",                "com.slack.Slack",                   null),
        new(["signal"],
            "Signal",               "org.signal.Signal",                 null),
        new(["telegram"],
            "Telegram Desktop",     "org.telegram.desktop",              null),
        new(["zoom"],
            "Zoom",                 "us.zoom.Zoom",                      null),

        // ── Productivity ──────────────────────────────────────────────────────
        new(["libreoffice"],
            "LibreOffice",          "org.libreoffice.LibreOffice",       null),
        new(["thunderbird"],
            "Thunderbird",          "org.mozilla.Thunderbird",           null),
        new(["keepass"],
            "KeePassXC",            "org.keepassxc.KeePassXC",           null),
        new(["bitwarden"],
            "Bitwarden",            "com.bitwarden.desktop",             null),

        // ── Graphics / Creative ───────────────────────────────────────────────
        new(["gimp"],
            "GIMP",                 "org.gimp.GIMP",                     null),
        new(["inkscape"],
            "Inkscape",             "org.inkscape.Inkscape",             null),
        new(["blender"],
            "Blender",              "org.blender.Blender",               null),
        new(["obs studio", "obs-studio"],
            "OBS Studio",           "com.obsproject.Studio",             null),
        new(["krita"],
            "Krita",                "org.kde.krita",                     null),

        // ── Developer tools ───────────────────────────────────────────────────
        new(["visual studio code", "vscode"],
            "Visual Studio Code",   "com.visualstudio.code",             null),

        // ── File / Network ────────────────────────────────────────────────────
        new(["filezilla"],
            "FileZilla",            "org.filezillaproject.Filezilla",    null),

        // ── Web browsers ────────────────────────────────────────────────────────
        // Firefox and Falkon ship with Fedora KDE, so they are not listed here.
        // These are third-party browsers the user installed on Windows.
        new(["zen browser", "zen-browser"],
            "Zen Browser",          "app.zen_browser.zen",               null),
        new(["brave"],
            "Brave",                "com.brave.Browser",                 null),
        new(["vivaldi"],
            "Vivaldi",              "com.vivaldi.Vivaldi",               null),
        new(["opera"],
            "Opera",                "com.opera.Opera",                   null),
        new(["google chrome"],
            "Google Chrome",        "com.google.Chrome",                 null),
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the Windows registry for installed applications and returns a list
    /// of <see cref="SuggestedPackage"/> entries for any that have a known Linux
    /// equivalent. Defensive: never throws; returns an empty list on any error.
    /// </summary>
    public static IReadOnlyList<DetectedSuggestion> Scan()
    {
        try
        {
            var installed = ReadInstalledDisplayNames();
            return Match(installed);
        }
        catch
        {
            // Registry access can fail in restricted environments — never crash the wizard.
            return [];
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static HashSet<string> ReadInstalledDisplayNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // HKLM — system-wide installs (64-bit and 32-bit on 64-bit Windows)
        foreach (var path in HklmUninstallPaths)
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(path);
                if (root is null) continue;
                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        if (sub?.GetValue("DisplayName") is string dn
                            && !string.IsNullOrWhiteSpace(dn))
                            names.Add(dn);
                    }
                    catch { /* skip inaccessible subkey */ }
                }
            }
            catch { /* skip inaccessible hive */ }
        }

        // HKCU — per-user installs (Spotify installs here by default)
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(HkcuUninstallPath);
            if (root is not null)
            {
                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        if (sub?.GetValue("DisplayName") is string dn
                            && !string.IsNullOrWhiteSpace(dn))
                            names.Add(dn);
                    }
                    catch { /* skip inaccessible subkey */ }
                }
            }
        }
        catch { /* skip inaccessible hive */ }

        return names;
    }

    private static IReadOnlyList<DetectedSuggestion> Match(HashSet<string> installed)
    {
        var results = new List<DetectedSuggestion>();

        foreach (var mapping in Mappings)
        {
            // Find the first installed DisplayName that contains any keyword.
            var hit = installed.FirstOrDefault(name =>
                mapping.Keywords.Any(kw =>
                    name.Contains(kw, StringComparison.OrdinalIgnoreCase)));

            if (hit is null) continue;

            results.Add(new DetectedSuggestion(
                WindowsDisplayName: hit,
                LinuxAppName:       mapping.LinuxAppName,
                FlatpakId:          mapping.FlatpakId,
                NativePackage:      mapping.NativePackage));
        }

        return results;
    }

    // ── Private types ─────────────────────────────────────────────────────────

    private sealed record AppMapping(
        string[] Keywords,
        string   LinuxAppName,
        string?  FlatpakId,
        string?  NativePackage);
}

/// <summary>
/// A single match returned by <see cref="WindowsAppScanner.Scan"/>:
/// one detected Windows application paired with its Linux equivalent.
/// </summary>
public sealed record DetectedSuggestion(
    string  WindowsDisplayName,
    string  LinuxAppName,
    string? FlatpakId,
    string? NativePackage);
