using FluentAssertions;
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
}
