using FluentAssertions;
using Microsoft.Win32;
using Xunit;

namespace Igloo.Preflight.Tests;

public class WindowsAppScannerTests
{
    private static HashSet<string> Installed(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Display_names_match_keywords_case_insensitively_as_substrings()
    {
        var results = WindowsAppScanner.Match(Installed(
            "VLC media player 3.0.20", "Spotify Music", "Random Tool"));

        results.Select(r => r.LinuxAppName)
            .Should().BeEquivalentTo("VLC media player", "Spotify");
        results.Single(r => r.LinuxAppName == "Spotify")
            .WindowsDisplayName.Should().Be("Spotify Music");
    }

    [Fact]
    public void Unknown_apps_produce_no_suggestions()
    {
        WindowsAppScanner.Match(Installed("Some Bespoke CRM 9")).Should().BeEmpty();
    }

    [Fact]
    public void Each_mapping_yields_at_most_one_suggestion()
    {
        var results = WindowsAppScanner.Match(Installed(
            "OBS Studio 30", "obs-studio (64bit)"));

        results.Should().ContainSingle().Which.LinuxAppName.Should().Be("OBS Studio");
    }

    [Fact]
    public void Scan_never_throws()
    {
        var act = WindowsAppScanner.Scan;
        act.Should().NotThrow();
    }

    // Mozilla-style installers append the architecture and locale to DisplayName.
    // Verbatim from HKLM on the 2026-08-20 test machine.
    [Fact]
    public void Mozilla_style_display_names_still_match()
    {
        var results = WindowsAppScanner.Match(Installed(
            "Zen Browser (x64 en-US)", "Mozilla Thunderbird (x64 nl)"));

        results.Select(r => r.LinuxAppName)
            .Should().BeEquivalentTo("Zen Browser", "Thunderbird");
    }

    [Fact]
    public void Flatpak_lookup_answers_by_linux_app_name()
    {
        WindowsAppScanner.FlatpakFor("Zen Browser").Should().Be("app.zen_browser.zen");
        WindowsAppScanner.FlatpakFor("Brave").Should().Be("com.brave.Browser");
    }

    // Firefox ships with every distribution iGloo installs, so it has no mapping -
    // GetSelectedSuggestions relies on null here to leave it out of the install list.
    [Fact]
    public void Flatpak_lookup_returns_null_for_browsers_iGloo_does_not_install()
    {
        WindowsAppScanner.FlatpakFor("Mozilla Firefox").Should().BeNull();
        WindowsAppScanner.FlatpakFor("Microsoft Edge").Should().BeNull();
    }

    // Regression: iGloo publishes win-x86, so reading HKLM through the process view
    // returns only WOW6432Node and every 64-bit-only install disappears - which is
    // how Thunderbird, KeePassXC and Zen Browser went missing from the app list.
    [Fact]
    public void Both_registry_views_reach_the_installed_list()
    {
        var all = WindowsAppScanner.ReadInstalledDisplayNames();

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            WindowsAppScanner.ReadHklmView(view).Should().OnlyContain(
                name => all.Contains(name), $"{view} must be merged into the scan");
    }

    [Fact]
    public void The_two_registry_views_are_not_the_same_key()
    {
        // A 64-bit Windows always has installs in both; if this ever fails the views
        // collapsed into one and the test above stops proving anything.
        Environment.Is64BitOperatingSystem.Should().BeTrue();
        WindowsAppScanner.ReadHklmView(RegistryView.Registry64)
            .Should().NotBeEquivalentTo(WindowsAppScanner.ReadHklmView(RegistryView.Registry32));
    }
}
