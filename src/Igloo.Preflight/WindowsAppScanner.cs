using System.Runtime.Versioning;
using System.Security;
using Igloo.Core.Models;
using Microsoft.Win32;

namespace Igloo.Preflight;

[SupportedOSPlatform("windows")]
public static class WindowsAppScanner
{
    //   Registry paths that list installed applications            ─

    private const string UninstallPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // iGloo publishes win-x86, and WOW64 redirects a 32-bit process's HKLM reads to
    // WOW6432Node - which hides every 64-bit-only install. Naming the views defeats
    // that the way FindNativeExe defeats the System32 redirect, and stays correct if
    // the app is ever published x64. Registry32 resolves to WOW6432Node by itself.
    private static readonly RegistryView[] HklmViews =
        [RegistryView.Registry64, RegistryView.Registry32];

    //   Mapping: Windows keyword(s) → Linux equivalent             
    //
    // Each entry is:   (string[] keywords, linuxAppName, flatpakId, nativePackage)
    //
    // Keywords are matched case-insensitively against the registry DisplayName.
    // Order within this array does not affect output ordering.

    private static readonly AppMapping[] Mappings =
    [
        //   Media                               ─
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

        //   Gaming                               
        new(["steam"],
            "Steam",                "com.valvesoftware.Steam",           null),

        //   Communication                           ─
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

        //   Productivity                            
        new(["libreoffice"],
            "LibreOffice",          "org.libreoffice.LibreOffice",       null),
        new(["thunderbird"],
            "Thunderbird",          "org.mozilla.Thunderbird",           null),
        new(["keepass"],
            "KeePassXC",            "org.keepassxc.KeePassXC",           null),
        new(["bitwarden"],
            "Bitwarden",            "com.bitwarden.desktop",             null),

        //   Graphics / Creative                        ─
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

        //   Developer tools                          ─
        new(["visual studio code", "vscode"],
            "Visual Studio Code",   "com.visualstudio.code",             null),

        //   File / Network                           
        new(["filezilla"],
            "FileZilla",            "org.filezillaproject.Filezilla",    null),

        //   Web browsers
        // Firefox is absent on purpose: every distribution iGloo installs already
        // ships it, so offering it would put a second Firefox on the machine.
        // GetSelectedSuggestions relies on FlatpakFor returning null for it.
        // These are the third-party browsers the user installed on Windows.
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

    //   Public API                               

    public static IReadOnlyList<DetectedSuggestion> Scan() => Match(ReadInstalledDisplayNames());

    /// <summary>The Flatpak that provides <paramref name="linuxAppName"/>, or null when
    /// iGloo has no mapping for it (Firefox, for one, ships with the distribution).</summary>
    public static string? FlatpakFor(string linuxAppName)
        => Mappings.FirstOrDefault(m =>
            string.Equals(m.LinuxAppName, linuxAppName, StringComparison.Ordinal))?.FlatpakId;

    //   Private                                ─

    internal static HashSet<string> ReadInstalledDisplayNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // HKLM - system-wide installs, both registry views.
        foreach (var view in HklmViews)
            names.UnionWith(ReadHklmView(view));

        // HKCU - per-user installs (Spotify installs here by default). This path is
        // not redirected, so the process view is the right one.
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        AddDisplayNames(hkcu, UninstallPath, names);

        return names;
    }

    internal static HashSet<string> ReadHklmView(RegistryView view)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        AddDisplayNames(hklm, UninstallPath, names);
        return names;
    }

    private static void AddDisplayNames(RegistryKey hive, string uninstallPath, HashSet<string> names)
    {
        using var root = TryOpenSubKey(hive, uninstallPath);
        if (root is null)
            return;

        foreach (var dn in TryGetSubKeyNames(root)
                     .Select(subName => TryReadDisplayName(root, subName))
                     .Where(name => !string.IsNullOrWhiteSpace(name)))
            names.Add(dn!);
    }

    // Registry reads throw SecurityException/UnauthorizedAccessException/IOException for keys
    // the current user can't reach; each helper reports that as "nothing here" (null / empty)
    // so one locked key trims the results instead of aborting the whole scan.

    private static RegistryKey? TryOpenSubKey(RegistryKey hive, string path)
    {
        try
        {
            return hive.OpenSubKey(path);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string[] TryGetSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static string? TryReadDisplayName(RegistryKey root, string subName)
    {
        try
        {
            using var sub = root.OpenSubKey(subName);
            return sub?.GetValue("DisplayName") as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<DetectedSuggestion> Match(HashSet<string> installed)
    {
        var results = new List<DetectedSuggestion>();

        foreach (var mapping in Mappings)
        {
            // Find the first installed DisplayName that contains any keyword.
            var hit = installed.FirstOrDefault(name =>
                mapping.Keywords.Any(kw =>
                    name.Contains(kw, StringComparison.OrdinalIgnoreCase)));

            if (hit is null)
                continue;

            results.Add(new DetectedSuggestion(
                WindowsDisplayName: hit,
                LinuxAppName: mapping.LinuxAppName,
                FlatpakId: mapping.FlatpakId,
                NativePackage: mapping.NativePackage));
        }

        return results;
    }

    //   Private types                             ─

    private sealed record AppMapping(
        string[] Keywords,
        string LinuxAppName,
        string? FlatpakId,
        string? NativePackage);
}

public sealed record DetectedSuggestion(
    string WindowsDisplayName,
    string LinuxAppName,
    string? FlatpakId,
    string? NativePackage);
