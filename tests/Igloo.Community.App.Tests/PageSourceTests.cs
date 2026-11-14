using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Igloo.Community.App.Tests;

// Reads the app's own source: a page that names something absent fails at run time, not at build time.
public sealed class PageSourceTests
{
    // Fonts that are part of every supported Windows. A font from an app package can lose its file.
    private static readonly string[] InBoxFonts = ["Segoe UI", "Segoe MDL2 Assets", "Segoe Fluent Icons", "Consolas"];

    private static string AppSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Igloo.sln")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the app source is read from the repository");
        return Path.Join(directory!.FullName, "src", "Igloo.Community.App");
    }

    [Fact]
    public void The_staging_page_shows_the_title_the_view_model_sets()
    {
        var page = File.ReadAllText(Path.Join(AppSource(), "Views", "FileStagingPage.xaml"));

        page.Should().Contain("Text=\"{Binding ErrorTitle}\"").And.NotContain("Text=\"Staging failed\"");
    }

    [Fact]
    public void Every_named_font_is_part_of_Windows()
    {
        var named = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppSource(), "*.*", SearchOption.AllDirectories)
                     .Where(file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                                    file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                     .Where(file => !file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) &&
                                    !file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in new[]
                     {
                         "FontFamily=\"([^\"{]+)\"",
                         "Property=\"FontFamily\"\\s+Value=\"([^\"{]+)\"",
                         "new FontFamily\\(\"([^\"]+)\"\\)",
                     })
                foreach (Match match in Regex.Matches(text, pattern))
                    named.AddRange(match.Groups[1].Value.Split(',').Select(name =>
                        $"{name.Trim()} ({Path.GetFileName(file)})"));
        }

        named.Should().NotBeEmpty();
        named.Where(entry => !InBoxFonts.Any(font => entry.StartsWith(font + " (", StringComparison.Ordinal)))
            .Should().BeEmpty();
    }
}
