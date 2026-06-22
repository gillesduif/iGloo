using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Models;
using Igloo.Preflight;
using Microsoft.Win32;

namespace Igloo.App.ViewModels;

/// <summary>
/// View-model for the Migration Setup wizard step. The user configures:
///   • Linux username (auto-seeded from the Windows username, fully editable)
///   • Which personal folders to migrate (Documents, Downloads, Pictures, …)
///   • Which browser profiles to include
///   • Timezone and keyboard layout (auto-detected from Windows, editable)
/// </summary>
public sealed partial class MigrationSetupViewModel : ObservableObject
{
    // ── Auto-detected / read-only context ────────────────────────────────────

    /// <summary>Current Windows username shown as context.</summary>
    public string WindowsUsername { get; } = Environment.UserName;

    // ── Linux username ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsernameValid))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _linuxUsername;

    public bool IsUsernameValid => ValidateLinuxUsername(LinuxUsername);

    // ── Linux password ────────────────────────────────────────────────────────

    /// <summary>
    /// The password the user typed in the first PasswordBox.
    /// Set from code-behind (PasswordBox.Password is not bindable).
    /// </summary>
    public string LinuxPassword        { get; private set; } = string.Empty;
    public string LinuxPasswordConfirm { get; private set; } = string.Empty;

    public bool IsPasswordValid => LinuxPassword.Length >= 8;
    public bool IsPasswordMatch => LinuxPassword == LinuxPasswordConfirm;

    /// <summary>Called by the view code-behind when either PasswordBox changes.</summary>
    public void SetPasswords(string password, string confirm)
    {
        LinuxPassword        = password;
        LinuxPasswordConfirm = confirm;
        OnPropertyChanged(nameof(LinuxPassword));
        OnPropertyChanged(nameof(LinuxPasswordConfirm));
        OnPropertyChanged(nameof(IsPasswordValid));
        OnPropertyChanged(nameof(IsPasswordMatch));
        OnPropertyChanged(nameof(CanProceed));
    }

    // ── Folders to migrate ────────────────────────────────────────────────────

    [ObservableProperty] private bool _includeDocuments = true;
    [ObservableProperty] private bool _includeDownloads = true;
    [ObservableProperty] private bool _includePictures  = true;
    [ObservableProperty] private bool _includeDesktop   = true;
    [ObservableProperty] private bool _includeMusic     = false;
    [ObservableProperty] private bool _includeVideos    = false;

    // ── Browser profiles ─────────────────────────────────────────────────────

    public IReadOnlyList<BrowserEntry> DetectedBrowsers { get; }
    public bool HasDetectedBrowsers => DetectedBrowsers.Count > 0;

    // ── Suggested Linux apps ──────────────────────────────────────────────────

    public IReadOnlyList<SuggestedPackageEntry> DetectedSuggestions { get; }
    public bool HasDetectedSuggestions => DetectedSuggestions.Count > 0;

    // ── System settings ───────────────────────────────────────────────────────

    [ObservableProperty] private string _timezone;
    [ObservableProperty] private string _keymap;

    // ── CanProceed ────────────────────────────────────────────────────────────

    /// <summary>Enables "Next" once the username and password both pass validation.</summary>
    public bool CanProceed => IsUsernameValid && IsPasswordValid && IsPasswordMatch;

    // ── Constructor ──────────────────────────────────────────────────────────

    public MigrationSetupViewModel()
    {
        // Seed Linux username from Windows account name.
        _linuxUsername = SanitizeUsername(WindowsUsername);

        // Timezone: Windows ID → IANA.
        if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var ianaTimezone))
            ianaTimezone = "UTC";
        _timezone = ianaTimezone;

        // Keyboard: read the actual active layout from the registry, fall back to UI-culture heuristic.
        _keymap = DetectKeymap();

        // Detect installed browsers.
        DetectedBrowsers = DetectBrowsers();

        // Detect Windows apps that have Linux equivalents.
        DetectedSuggestions = WindowsAppScanner.Scan()
            .Select(s => new SuggestedPackageEntry(s))
            .ToList();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns absolute paths of the selected user folders that actually exist.</summary>
    public IReadOnlyList<string> GetSelectedFolderPaths()
    {
        var paths = new List<string>();

        void TryAdd(Environment.SpecialFolder sf)
        {
            var p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) paths.Add(p);
        }

        if (IncludeDocuments) TryAdd(Environment.SpecialFolder.MyDocuments);
        if (IncludeDownloads)
        {
            var dl = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(dl)) paths.Add(dl);
        }
        if (IncludePictures) TryAdd(Environment.SpecialFolder.MyPictures);
        if (IncludeDesktop)  TryAdd(Environment.SpecialFolder.DesktopDirectory);
        if (IncludeMusic)    TryAdd(Environment.SpecialFolder.MyMusic);
        if (IncludeVideos)   TryAdd(Environment.SpecialFolder.MyVideos);

        return paths;
    }

    /// <summary>Returns the display names of the selected folders (for the manifest).</summary>
    public IReadOnlyList<string> GetSelectedFolderNames()
    {
        var names = new List<string>();
        if (IncludeDocuments) names.Add("Documents");
        if (IncludeDownloads) names.Add("Downloads");
        if (IncludePictures)  names.Add("Pictures");
        if (IncludeDesktop)   names.Add("Desktop");
        if (IncludeMusic)     names.Add("Music");
        if (IncludeVideos)    names.Add("Videos");
        return names;
    }

    /// <summary>
    /// Returns the selected folders paired with their source path <em>relative to
    /// the Windows user profile</em> (forward slashes). The absolute path is
    /// resolved via the Known Folder API, so OneDrive-redirected folders come back
    /// as e.g. <c>("Documents", "OneDrive/Documents")</c> while non-redirected ones
    /// are <c>("Downloads", "Downloads")</c>. Only folders that actually exist on
    /// disk are included. The kickstart <c>%post</c> uses the relative path to copy
    /// from the real location instead of guessing <c>$WIN_HOME/&lt;name&gt;</c>.
    /// </summary>
    public IReadOnlyList<MigrationFolder> GetSelectedFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result  = new List<MigrationFolder>();

        void TryAdd(string name, string? absolute)
        {
            if (string.IsNullOrEmpty(absolute) || !Directory.Exists(absolute)) return;

            // Path relative to the profile, normalised to forward slashes for the
            // Linux-side %post. Falls back to the leaf name if the folder lives
            // outside the profile (e.g. redirected to another drive) - the %post
            // then logs "not found" rather than copying from a bogus location.
            var rel = Path.GetRelativePath(profile, absolute).Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                rel = name;

            result.Add(new MigrationFolder { Name = name, SourceRelativePath = rel });
        }

        if (IncludeDocuments) TryAdd("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (IncludeDownloads) TryAdd("Downloads", Path.Combine(profile, "Downloads"));
        if (IncludePictures)  TryAdd("Pictures",  Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        if (IncludeDesktop)   TryAdd("Desktop",   Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (IncludeMusic)     TryAdd("Music",     Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        if (IncludeVideos)    TryAdd("Videos",    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

        return result;
    }

    /// <summary>Returns the names of the browsers the user chose to include.</summary>
    public IReadOnlyList<string> GetSelectedBrowserNames()
        => DetectedBrowsers.Where(b => b.IsSelected).Select(b => b.Name).ToList();

    /// <summary>
    /// Phase 1 browser migration. Maps each selected browser to a
    /// <see cref="BrowserMigration"/>:
    ///   • Gecko browsers (Firefox / Zen / Waterfox) - the on-disk profile root is
    ///     OS-portable and includes saved passwords (NSS, not bound to the Windows
    ///     account). Source = the profile root relative to the Windows profile
    ///     (forward slashes); dest = the canonical Linux home location.
    ///   • Chromium browsers (Chrome / Edge / Brave / Vivaldi / Opera) - passwords
    ///     are DPAPI-bound to the Windows account and not portable, so these are
    ///     recorded (engine + name) but left with empty paths; the kickstart skips
    ///     them. Real migration is a future phase.
    /// </summary>
    public IReadOnlyList<BrowserMigration> GetSelectedBrowsers()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result  = new List<BrowserMigration>();

        // Gecko: detected ProfilePath ends in "Profiles"; the folder we copy is its
        // parent (the whole browser root), e.g. AppData/Roaming/Mozilla/Firefox.
        var geckoDest = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Mozilla Firefox"] = ".mozilla/firefox",
            ["Zen Browser"]     = ".zen",
            ["Waterfox"]        = ".waterfox",
        };

        foreach (var b in DetectedBrowsers.Where(b => b.IsSelected))
        {
            if (geckoDest.TryGetValue(b.Name, out var dest))
            {
                var root = Directory.GetParent(b.ProfilePath)?.FullName;
                var srcRel = root is not null
                    ? Path.GetRelativePath(profile, root).Replace('\\', '/')
                    : string.Empty;

                // Only migrate when the root sits under the Windows profile (it
                // always should for AppData); otherwise record without paths.
                var portable = !string.IsNullOrEmpty(srcRel)
                    && !srcRel.StartsWith("..", StringComparison.Ordinal)
                    && !Path.IsPathRooted(srcRel);

                result.Add(new BrowserMigration
                {
                    Name               = b.Name,
                    Engine             = "gecko",
                    SourceRelativePath = portable ? srcRel : string.Empty,
                    DestRelativePath   = portable ? dest   : string.Empty,
                    IncludesPasswords  = portable,
                });
            }
            else
            {
                // Chromium family - recorded only (Phase 2).
                result.Add(new BrowserMigration
                {
                    Name              = b.Name,
                    Engine            = "chromium",
                    IncludesPasswords = false,
                });
            }
        }

        return result;
    }

    /// <summary>Returns the selected Linux app suggestions as manifest entries.</summary>
    public IReadOnlyList<SuggestedPackage> GetSelectedSuggestions()
        => DetectedSuggestions
            .Where(s => s.IsSelected)
            .Select(s => new SuggestedPackage
            {
                WindowsAppName = s.WindowsDisplayName,
                LinuxAppName   = s.LinuxAppName,
                FlatpakId      = s.FlatpakId,
                NativePackage  = s.NativePackage,
                AutoInstall    = true,
            })
            .ToList();

    // ── Validation & sanitization ─────────────────────────────────────────────

    private static readonly HashSet<string> ReservedLinuxNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "root", "daemon", "bin", "sys", "nobody", "mail", "news",
            "uucp", "proxy", "www-data", "backup", "man", "list", "irc",
            "gnats", "games", "messagebus",
        };

    private static bool ValidateLinuxUsername(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 32)               return false;
        if (ReservedLinuxNames.Contains(name)) return false;
        if (!char.IsAsciiLetter(name[0]))   return false;

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-'))
                return false;
        }
        return true;
    }

    private static string SanitizeUsername(string windowsName)
    {
        var sb = new StringBuilder();
        foreach (var c in windowsName.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-' ? c : '_');

        var s = sb.ToString();

        // Strip leading non-letter characters.
        var start = 0;
        while (start < s.Length && !char.IsAsciiLetter(s[start]))
            start++;
        s = s[start..];

        if (string.IsNullOrEmpty(s)) return "user";
        return s.Length > 32 ? s[..32] : s;
    }

    // ── Keyboard layout detection ─────────────────────────────────────────────

    /// <summary>
    /// Returns the Linux XKB keymap name for the user's primary Windows keyboard layout.
    ///
    /// Strategy (most-accurate first):
    ///   1. Registry <c>HKCU\Keyboard Layout\Preload\1</c> → KLID hex string (e.g. "0000080c")
    ///      This reflects the actual keyboard the user has installed, regardless of the
    ///      Windows display language.
    ///   2. <see cref="CultureInfo.CurrentUICulture"/> heuristic - fallback only, because
    ///      UI language ≠ keyboard layout (English Windows + Belgian AZERTY is common).
    /// </summary>
    private static string DetectKeymap()
    {
        try
        {
            using var key  = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
            var klid = key?.GetValue("1")?.ToString()?.ToLowerInvariant().TrimStart('0');
            if (!string.IsNullOrEmpty(klid) && KlidMap.TryGetValue(klid, out var mapped))
                return mapped;
        }
        catch { /* registry unavailable - fall through */ }

        return CultureToKeymap(CultureInfo.CurrentUICulture.Name);
    }

    /// <summary>
    /// Windows Keyboard Layout IDs (KLID) → Linux XKB layout names.
    /// KLIDs are 8-char hex; we strip leading zeroes before lookup.
    /// Reference: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/windows-language-pack-default-values
    /// </summary>
    private static readonly Dictionary<string, string> KlidMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        { "409",   "us" },  // English (US)
        { "809",   "gb" },  // English (UK)
        { "1009",  "ca" },  // English (Canada) - Canadian Multilingual Standard
        { "1409",  "us" },  // English (New Zealand)
        { "1809",  "gb" },  // English (Ireland)

        // Belgian / French
        { "80c",   "be" },  // Belgian French   (AZERTY)
        { "813",   "be" },  // Belgian Dutch    (AZERTY)
        { "40c",   "fr" },  // French (France)
        { "100c",  "ch" },  // French (Switzerland)

        // Germanic
        { "407",   "de" },  // German (Germany)
        { "807",   "ch" },  // German (Switzerland)
        { "c07",   "de" },  // German (Austria)
        { "1007",  "de" },  // German (Luxembourg)

        // Dutch
        { "413",   "nl" },  // Dutch (Netherlands)

        // Iberian
        { "40a",   "es" },  // Spanish (Spain)
        { "c0a",   "es" },  // Spanish (Spain, traditional sort)
        { "416",   "br" },  // Portuguese (Brazil)
        { "816",   "pt" },  // Portuguese (Portugal)

        // Italian
        { "410",   "it" },  // Italian (Italy)
        { "810",   "it" },  // Italian (Switzerland)

        // Nordic
        { "41d",   "se" },  // Swedish
        { "414",   "no" },  // Norwegian Bokmål
        { "814",   "no" },  // Norwegian Nynorsk
        { "406",   "dk" },  // Danish
        { "40b",   "fi" },  // Finnish

        // Eastern European
        { "415",   "pl" },  // Polish (programmers)
        { "10415", "pl" },  // Polish (214)
        { "405",   "cz" },  // Czech
        { "40e",   "hu" },  // Hungarian
        { "418",   "ro" },  // Romanian
        { "41b",   "sk" },  // Slovak

        // Other European
        { "41f",   "tr" },  // Turkish Q
        { "1041f", "tr" },  // Turkish F
        { "408",   "gr" },  // Greek
        { "419",   "ru" },  // Russian
        { "422",   "ua" },  // Ukrainian
        { "402",   "bg" },  // Bulgarian (phonetic)
        { "1402",  "bg" },  // Bulgarian (traditional)
        { "424",   "si" },  // Slovenian
        { "41a",   "hr" },  // Croatian
        { "c1a",   "rs" },  // Serbian (Latin)
        { "81a",   "rs" },  // Serbian (Cyrillic)
    };

    /// <summary>
    /// Last-resort fallback: infer a keymap from the Windows UI culture name.
    /// Less accurate than the KLID registry key because the display language
    /// often differs from the physical keyboard layout.
    /// </summary>
    private static string CultureToKeymap(string cultureName) => cultureName switch
    {
        _ when cultureName.EndsWith("-BE")     => "be",
        _ when cultureName.StartsWith("nl")    => "nl",
        _ when cultureName.StartsWith("fr-CH") => "ch",
        _ when cultureName.StartsWith("fr")    => "fr",
        _ when cultureName.StartsWith("de-CH") => "ch",
        _ when cultureName.StartsWith("de")    => "de",
        _ when cultureName.StartsWith("es")    => "es",
        _ when cultureName.StartsWith("pt-BR") => "br",
        _ when cultureName.StartsWith("pt")    => "pt",
        _ when cultureName.StartsWith("it")    => "it",
        _ when cultureName.StartsWith("ru")    => "ru",
        _ when cultureName.StartsWith("pl")    => "pl",
        _ when cultureName.StartsWith("cs")    => "cz",
        _ when cultureName.StartsWith("hu")    => "hu",
        _ when cultureName.StartsWith("ro")    => "ro",
        _ when cultureName.StartsWith("sk")    => "sk",
        _ when cultureName.StartsWith("sv")    => "se",
        _ when cultureName.StartsWith("nb") ||
               cultureName.StartsWith("nn")    => "no",
        _ when cultureName.StartsWith("da")    => "dk",
        _ when cultureName.StartsWith("fi")    => "fi",
        _ when cultureName.StartsWith("tr")    => "tr",
        _ when cultureName.StartsWith("el")    => "gr",
        _ when cultureName.StartsWith("uk")    => "ua",
        _                                      => "us",
    };

    // ── Browser detection ─────────────────────────────────────────────────────

    private static IReadOnlyList<BrowserEntry> DetectBrowsers()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var results = new List<BrowserEntry>();
        Check("Google Chrome",  Path.Combine(localApp, "Google",       "Chrome",         "User Data", "Default"));
        Check("Microsoft Edge", Path.Combine(localApp, "Microsoft",    "Edge",           "User Data", "Default"));
        Check("Mozilla Firefox",Path.Combine(appData,  "Mozilla",      "Firefox",        "Profiles"));
        Check("Brave",          Path.Combine(localApp, "BraveSoftware","Brave-Browser",  "User Data", "Default"));
        Check("Zen Browser",    Path.Combine(appData,  "zen",          "Profiles"));
        Check("Vivaldi",        Path.Combine(localApp, "Vivaldi",      "User Data",      "Default"));
        Check("Opera",          Path.Combine(appData,  "Opera Software","Opera Stable",  "Default"));
        Check("Waterfox",       Path.Combine(appData,  "Waterfox",     "Profiles"));
        return results;

        void Check(string name, string path)
        {
            if (Directory.Exists(path))
                results.Add(new BrowserEntry(name, path));
        }
    }
}

/// <summary>A browser detected on the Windows system that the user can opt in/out of migrating.</summary>
public sealed partial class BrowserEntry : ObservableObject
{
    public string Name        { get; }
    public string ProfilePath { get; }

    [ObservableProperty] private bool _isSelected = true;

    public BrowserEntry(string name, string profilePath)
    {
        Name        = name;
        ProfilePath = profilePath;
    }
}

/// <summary>
/// A Windows app detected by <see cref="WindowsAppScanner"/> paired with its
/// Linux equivalent. Wraps a <see cref="DetectedSuggestion"/> with an
/// observable <see cref="IsSelected"/> checkbox for the wizard UI.
/// </summary>
public sealed partial class SuggestedPackageEntry : ObservableObject
{
    public string  WindowsDisplayName { get; }
    public string  LinuxAppName       { get; }
    public string? FlatpakId          { get; }
    public string? NativePackage      { get; }

    [ObservableProperty] private bool _isSelected = true;

    public SuggestedPackageEntry(DetectedSuggestion suggestion)
    {
        WindowsDisplayName = suggestion.WindowsDisplayName;
        LinuxAppName       = suggestion.LinuxAppName;
        FlatpakId          = suggestion.FlatpakId;
        NativePackage      = suggestion.NativePackage;
    }
}
