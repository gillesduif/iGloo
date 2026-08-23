using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Models;
using Igloo.Preflight;
using Microsoft.Extensions.Logging.Abstractions;

namespace Igloo.App.ViewModels;

public sealed partial class MigrationSetupViewModel : ObservableObject
{
    //   Auto-detected / read-only context                   

    
    public string WindowsUsername { get; } = Environment.UserName;

    // Carried over as the Linux hostname: the machine keeps the name its owner
    // gave it instead of one derived from the account.
    public string WindowsComputerName { get; } = Environment.MachineName;

    //   Linux username                             

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsernameValid))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private string _linuxUsername;

    public bool IsUsernameValid => LinuxUsernameRules.IsValid(LinuxUsername);

    //   Linux password                             

    public string LinuxPassword { get; private set; } = string.Empty;
    public string LinuxPasswordConfirm { get; private set; } = string.Empty;

    public bool IsPasswordValid => LinuxPassword.Length >= 8;
    public bool IsPasswordMatch => LinuxPassword == LinuxPasswordConfirm;

    // An untouched field is incomplete, not wrong. Fluent shows validation after the
    // user has typed something, so the form does not greet them with two errors.
    public bool ShowPasswordLengthError => LinuxPassword.Length > 0 && !IsPasswordValid;
    public bool ShowPasswordMismatch => LinuxPasswordConfirm.Length > 0 && !IsPasswordMatch;

    
    public void SetPasswords(string password, string confirm)
    {
        LinuxPassword = password;
        LinuxPasswordConfirm = confirm;
        OnPropertyChanged(nameof(LinuxPassword));
        OnPropertyChanged(nameof(LinuxPasswordConfirm));
        OnPropertyChanged(nameof(IsPasswordValid));
        OnPropertyChanged(nameof(IsPasswordMatch));
        OnPropertyChanged(nameof(ShowPasswordLengthError));
        OnPropertyChanged(nameof(ShowPasswordMismatch));
        OnPropertyChanged(nameof(CanProceed));
    }

    //   Folders to migrate                           

    [ObservableProperty] private bool _includeDocuments = true;
    [ObservableProperty] private bool _includeDownloads = true;
    [ObservableProperty] private bool _includePictures = true;
    [ObservableProperty] private bool _includeDesktop = true;
    [ObservableProperty] private bool _includeMusic = false;
    [ObservableProperty] private bool _includeVideos = false;

    //   Browser profiles                           ─

    public IReadOnlyList<BrowserEntry> DetectedBrowsers { get; }
    public bool HasDetectedBrowsers => DetectedBrowsers.Count > 0;

    //   Suggested Linux apps                          

    public IReadOnlyList<SuggestedPackageEntry> DetectedSuggestions { get; }
    public bool HasDetectedSuggestions => DetectedSuggestions.Count > 0;

    //   System settings                            ─

    [ObservableProperty] private string _timezone;
    [ObservableProperty] private string _keymap;
    [ObservableProperty] private string _locale;

    //   CanProceed                               

    
    public bool CanProceed => IsUsernameValid && IsPasswordValid && IsPasswordMatch;

    //   Constructor                              

    public MigrationSetupViewModel()
    {
        // Seed Linux username from Windows account name.
        _linuxUsername = LinuxUsernameRules.Sanitize(WindowsUsername);

        // Timezone: Windows ID → IANA.
        if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var ianaTimezone))
            ianaTimezone = "UTC";
        _timezone = ianaTimezone;

        // Keyboard: read the actual active layout from the registry, fall back to UI-culture heuristic.
        _keymap = KeymapDetection.DetectCurrent();

        // Display language: Windows culture → Linux locale (e.g. "nl-NL" → "nl_NL.UTF-8").
        _locale = ToLinuxLocale(CultureInfo.CurrentCulture);

        // Detect installed browsers.
        DetectedBrowsers = DetectBrowsers();

        // Detect Windows apps that have Linux equivalents.
        DetectedSuggestions = WindowsAppScanner.Scan()
            .Select(s => new SuggestedPackageEntry(s))
            .ToList();

        AccountPicturePath = AccountPictureReader.TryFindAccountPicture(
            NullLogger<MigrationSetupViewModel>.Instance);
    }

    /// <summary>
    /// The Windows account picture that will travel to Linux, or null when the user
    /// never set one. Bound as the card's avatar so the migration is visible rather
    /// than implied.
    /// </summary>
    public string? AccountPicturePath { get; }

    public bool HasAccountPicture => !string.IsNullOrEmpty(AccountPicturePath);

    // Maps a Windows culture (e.g. "nl-NL") to a glibc locale ("nl_NL.UTF-8"). A region-less or
    // invariant culture ("nl", "") has no reliable country to derive, so it falls back to en_US.UTF-8.
    private static string ToLinuxLocale(CultureInfo culture)
    {
        var name = culture.Name;
        return name.Contains('-', StringComparison.Ordinal)
            ? name.Replace('-', '_') + ".UTF-8"
            : "en_US.UTF-8";
    }

    //   Public API

    private IEnumerable<(string Name, string? Absolute)> SelectedFolderSources()
    {
        if (IncludeDocuments)
            yield return ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (IncludeDownloads)
            yield return ("Downloads", Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        if (IncludePictures)
            yield return ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        if (IncludeDesktop)
            yield return ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (IncludeMusic)
            yield return ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        if (IncludeVideos)
            yield return ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
    }

    
    public IReadOnlyList<string> GetSelectedFolderPaths()
        => SelectedFolderSources()
            .Where(f => !string.IsNullOrEmpty(f.Absolute) && Directory.Exists(f.Absolute))
            .Select(f => f.Absolute!)
            .ToList();

    
    public IReadOnlyList<string> GetSelectedFolderNames()
        => SelectedFolderSources().Select(f => f.Name).ToList();

    public IReadOnlyList<MigrationFolder> GetSelectedFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new List<MigrationFolder>();

        foreach (var (name, absolute) in SelectedFolderSources())
        {
            if (string.IsNullOrEmpty(absolute) || !Directory.Exists(absolute))
                continue;

            // Path relative to the profile, normalised to forward slashes for the
            // Linux-side %post. Falls back to the leaf name if the folder lives
            // outside the profile (e.g. redirected to another drive) - the %post
            // then logs "not found" rather than copying from a bogus location.
            var rel = Path.GetRelativePath(profile, absolute).Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                rel = name;

            result.Add(new MigrationFolder { Name = name, SourceRelativePath = rel });
        }

        return result;
    }

    
    public IReadOnlyList<string> GetSelectedBrowserNames()
        => DetectedBrowsers.Where(b => b.IsSelected).Select(b => b.Name).ToList();

    public IReadOnlyList<BrowserMigration> GetSelectedBrowsers()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new List<BrowserMigration>();

        // Gecko: detected ProfilePath ends in "Profiles"; the folder we copy is its
        // parent (the whole browser root), e.g. AppData/Roaming/Mozilla/Firefox.
        var geckoDest = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Mozilla Firefox"] = ".mozilla/firefox",
            ["Zen Browser"] = ".zen",
            ["Waterfox"] = ".waterfox",
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
                    Name = b.Name,
                    Engine = "gecko",
                    SourceRelativePath = portable ? srcRel : string.Empty,
                    DestRelativePath = portable ? dest : string.Empty,
                    IncludesPasswords = portable,
                });
            }
            else
            {
                // Chromium: the detected path is the profile, the folder the agent
                // copies from is its parent (the "User Data" root). Passwords and
                // cookies ride the encrypted blob instead; bookmarks, history and
                // favicons are plain files the agent lifts off the NTFS partition.
                var root = Directory.GetParent(b.ProfilePath)?.FullName;
                var srcRel = root is not null
                    ? Path.GetRelativePath(profile, root).Replace('\\', '/')
                    : string.Empty;
                var portable = !string.IsNullOrEmpty(srcRel)
                    && !srcRel.StartsWith("..", StringComparison.Ordinal)
                    && !Path.IsPathRooted(srcRel);

                result.Add(new BrowserMigration
                {
                    Name = b.Name,
                    Engine = "chromium",
                    SourceRelativePath = portable ? srcRel : string.Empty,
                    IncludesPasswords = false,
                });
            }
        }

        return result;
    }

    
    public IReadOnlyList<SuggestedPackage> GetSelectedSuggestions()
    {
        var packages = DetectedSuggestions
            .Where(s => s.IsSelected)
            .Select(s => new SuggestedPackage
            {
                WindowsAppName = s.WindowsDisplayName,
                LinuxAppName = s.LinuxAppName,
                FlatpakId = s.FlatpakId,
                NativePackage = s.NativePackage,
                AutoInstall = true,
            })
            .ToList();

        // Browsers and apps are two separate lists on the page, so a browser can be
        // picked for migration while its app is not - which lands the profile on a
        // machine that cannot open it. Migrating a browser implies installing it.
        foreach (var browser in DetectedBrowsers.Where(b => b.IsSelected))
        {
            var flatpakId = WindowsAppScanner.FlatpakFor(browser.Name);
            if (flatpakId is null
                || packages.Any(p => string.Equals(p.FlatpakId, flatpakId, StringComparison.Ordinal)))
                continue;

            packages.Add(new SuggestedPackage
            {
                WindowsAppName = browser.Name,
                LinuxAppName = browser.Name,
                FlatpakId = flatpakId,
                AutoInstall = true,
            });
        }

        return packages;
    }

    //   Browser detection                           ─

    private static List<BrowserEntry> DetectBrowsers()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var results = new List<BrowserEntry>();
        Check("Google Chrome", Path.Join(localApp, "Google", "Chrome", "User Data", "Default"));
        Check("Microsoft Edge", Path.Join(localApp, "Microsoft", "Edge", "User Data", "Default"));
        Check("Mozilla Firefox", Path.Join(appData, "Mozilla", "Firefox", "Profiles"));
        Check("Brave", Path.Join(localApp, "BraveSoftware", "Brave-Browser", "User Data", "Default"));
        Check("Zen Browser", Path.Join(appData, "zen", "Profiles"));
        Check("Vivaldi", Path.Join(localApp, "Vivaldi", "User Data", "Default"));
        Check("Opera", Path.Join(appData, "Opera Software", "Opera Stable", "Default"));
        Check("Waterfox", Path.Join(appData, "Waterfox", "Profiles"));
        return results;

        void Check(string name, string path)
        {
            if (Directory.Exists(path))
                results.Add(new BrowserEntry(name, path));
        }
    }
}


public sealed partial class BrowserEntry : ObservableObject
{
    public string Name { get; }
    public string ProfilePath { get; }

    [ObservableProperty] private bool _isSelected = true;

    public BrowserEntry(string name, string profilePath)
    {
        Name = name;
        ProfilePath = profilePath;
    }
}

public sealed partial class SuggestedPackageEntry : ObservableObject
{
    public string WindowsDisplayName { get; }
    public string LinuxAppName { get; }
    public string? FlatpakId { get; }
    public string? NativePackage { get; }

    [ObservableProperty] private bool _isSelected = true;

    public SuggestedPackageEntry(DetectedSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        WindowsDisplayName = suggestion.WindowsDisplayName;
        LinuxAppName = suggestion.LinuxAppName;
        FlatpakId = suggestion.FlatpakId;
        NativePackage = suggestion.NativePackage;
    }
}
