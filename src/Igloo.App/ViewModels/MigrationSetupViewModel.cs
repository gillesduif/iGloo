using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Models;
using Igloo.Preflight;

namespace Igloo.App.ViewModels;

public sealed partial class MigrationSetupViewModel : ObservableObject
{
    //   Auto-detected / read-only context                   

    
    public string WindowsUsername { get; } = Environment.UserName;

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

    
    public void SetPasswords(string password, string confirm)
    {
        LinuxPassword = password;
        LinuxPasswordConfirm = confirm;
        OnPropertyChanged(nameof(LinuxPassword));
        OnPropertyChanged(nameof(LinuxPasswordConfirm));
        OnPropertyChanged(nameof(IsPasswordValid));
        OnPropertyChanged(nameof(IsPasswordMatch));
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

        // Detect installed browsers.
        DetectedBrowsers = DetectBrowsers();

        // Detect Windows apps that have Linux equivalents.
        DetectedSuggestions = WindowsAppScanner.Scan()
            .Select(s => new SuggestedPackageEntry(s))
            .ToList();
    }

    //   Public API                               

    private IEnumerable<(string Name, string? Absolute)> SelectedFolderSources()
    {
        if (IncludeDocuments)
            yield return ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (IncludeDownloads)
            yield return ("Downloads", Path.Combine(
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
                // Chromium family - recorded only (Phase 2).
                result.Add(new BrowserMigration
                {
                    Name = b.Name,
                    Engine = "chromium",
                    IncludesPasswords = false,
                });
            }
        }

        return result;
    }

    
    public IReadOnlyList<SuggestedPackage> GetSelectedSuggestions()
        => DetectedSuggestions
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

    //   Browser detection                           ─

    private static List<BrowserEntry> DetectBrowsers()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var results = new List<BrowserEntry>();
        Check("Google Chrome", Path.Combine(localApp, "Google", "Chrome", "User Data", "Default"));
        Check("Microsoft Edge", Path.Combine(localApp, "Microsoft", "Edge", "User Data", "Default"));
        Check("Mozilla Firefox", Path.Combine(appData, "Mozilla", "Firefox", "Profiles"));
        Check("Brave", Path.Combine(localApp, "BraveSoftware", "Brave-Browser", "User Data", "Default"));
        Check("Zen Browser", Path.Combine(appData, "zen", "Profiles"));
        Check("Vivaldi", Path.Combine(localApp, "Vivaldi", "User Data", "Default"));
        Check("Opera", Path.Combine(appData, "Opera Software", "Opera Stable", "Default"));
        Check("Waterfox", Path.Combine(appData, "Waterfox", "Profiles"));
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
