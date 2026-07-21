using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Models;
using Igloo.Preflight;

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

    public bool IsUsernameValid => LinuxUsernameRules.IsValid(LinuxUsername);

    // ── Linux password ────────────────────────────────────────────────────────

    /// <summary>
    /// The password the user typed in the first PasswordBox.
    /// Set from code-behind (PasswordBox.Password is not bindable).
    /// </summary>
    public string LinuxPassword { get; private set; } = string.Empty;
    public string LinuxPasswordConfirm { get; private set; } = string.Empty;

    public bool IsPasswordValid => LinuxPassword.Length >= 8;
    public bool IsPasswordMatch => LinuxPassword == LinuxPasswordConfirm;

    /// <summary>Called by the view code-behind when either PasswordBox changes.</summary>
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

    // ── Folders to migrate ────────────────────────────────────────────────────

    [ObservableProperty] private bool _includeDocuments = true;
    [ObservableProperty] private bool _includeDownloads = true;
    [ObservableProperty] private bool _includePictures = true;
    [ObservableProperty] private bool _includeDesktop = true;
    [ObservableProperty] private bool _includeMusic = false;
    [ObservableProperty] private bool _includeVideos = false;

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

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The selected folders in wizard order, each paired with its absolute source
    /// location. The absolute path comes from the Known Folder API, so OneDrive
    /// Known Folder Move redirection is honoured. Single source of truth for the
    /// three getters below.
    /// </summary>
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

    /// <summary>Returns absolute paths of the selected user folders that actually exist.</summary>
    public IReadOnlyList<string> GetSelectedFolderPaths()
        => SelectedFolderSources()
            .Where(f => !string.IsNullOrEmpty(f.Absolute) && Directory.Exists(f.Absolute))
            .Select(f => f.Absolute!)
            .ToList();

    /// <summary>Returns the display names of the selected folders (for the manifest).</summary>
    public IReadOnlyList<string> GetSelectedFolderNames()
        => SelectedFolderSources().Select(f => f.Name).ToList();

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

    /// <summary>Returns the selected Linux app suggestions as manifest entries.</summary>
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

    // ── Browser detection ─────────────────────────────────────────────────────

    private static IReadOnlyList<BrowserEntry> DetectBrowsers()
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

/// <summary>A browser detected on the Windows system that the user can opt in/out of migrating.</summary>
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

/// <summary>
/// A Windows app detected by <see cref="WindowsAppScanner"/> paired with its
/// Linux equivalent. Wraps a <see cref="DetectedSuggestion"/> with an
/// observable <see cref="IsSelected"/> checkbox for the wizard UI.
/// </summary>
public sealed partial class SuggestedPackageEntry : ObservableObject
{
    public string WindowsDisplayName { get; }
    public string LinuxAppName { get; }
    public string? FlatpakId { get; }
    public string? NativePackage { get; }

    [ObservableProperty] private bool _isSelected = true;

    public SuggestedPackageEntry(DetectedSuggestion suggestion)
    {
        WindowsDisplayName = suggestion.WindowsDisplayName;
        LinuxAppName = suggestion.LinuxAppName;
        FlatpakId = suggestion.FlatpakId;
        NativePackage = suggestion.NativePackage;
    }
}
