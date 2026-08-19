#if DEBUG
using Igloo.Preflight;

namespace Igloo.App.ViewModels;

/// <summary>Design-time stand-in for <see cref="MigrationSetupViewModel"/>.</summary>
/// <remarks>
/// Real BrowserEntry and SuggestedPackageEntry instances, so the item templates bind
/// against the properties they will at run time. The validation flags are set to the
/// happy path; flip them to preview the error states.
/// </remarks>
public sealed class MigrationSetupDesignData
{
    public string WindowsUsername { get; set; } = "Gilles";
    public string LinuxUsername { get; set; } = "gilles";
    public string Locale { get; set; } = "nl_BE.UTF-8";
    public string Timezone { get; set; } = "Europe/Brussels";
    public string Keymap { get; set; } = "be";

    public bool IsUsernameValid { get; set; } = true;
    public bool IsPasswordValid { get; set; } = true;
    public bool IsPasswordMatch { get; set; } = true;

    public bool IncludeDocuments { get; set; } = true;
    public bool IncludeDownloads { get; set; } = true;
    public bool IncludePictures { get; set; } = true;
    public bool IncludeMusic { get; set; }
    public bool IncludeVideos { get; set; }
    public bool IncludeDesktop { get; set; } = true;

    public bool HasDetectedBrowsers => DetectedBrowsers.Count > 0;
    public bool HasDetectedSuggestions => DetectedSuggestions.Count > 0;

    public IReadOnlyList<BrowserEntry> DetectedBrowsers { get; } =
    [
        new BrowserEntry("Google Chrome", @"C:\Users\Gilles\AppData\Local\Google\Chrome\User Data"),
        new BrowserEntry("Microsoft Edge", @"C:\Users\Gilles\AppData\Local\Microsoft\Edge\User Data"),
        new BrowserEntry("Mozilla Firefox", @"C:\Users\Gilles\AppData\Roaming\Mozilla\Firefox\Profiles"),
    ];

    public IReadOnlyList<SuggestedPackageEntry> DetectedSuggestions { get; } =
    [
        new SuggestedPackageEntry(new DetectedSuggestion(
            "Visual Studio Code", "Visual Studio Code", "com.visualstudio.code", "code")),
        new SuggestedPackageEntry(new DetectedSuggestion(
            "Spotify", "Spotify", "com.spotify.Client", null)),
        new SuggestedPackageEntry(new DetectedSuggestion(
            "VLC media player", "VLC", "org.videolan.VLC", "vlc")),
    ];
}
#endif
