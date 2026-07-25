using FluentAssertions;
using Igloo.Core.Models;
using Xunit;

namespace Igloo.Core.Tests;

/// <summary>
/// Characterization tests for <see cref="DistroManifest"/> computed properties. The
/// catalog UI uses <see cref="DistroManifest.IsAvailable"/> to grey out entries and
/// <see cref="DistroManifest.LogoAbsolutePath"/> to resolve bundled assets.
/// </summary>
public class DistroManifestTests
{
    private static DistroManifest Manifest(string? status = null, string? logo = null,
        string? sourceDirectory = null) => new()
        {
            Id = "test",
            DisplayName = "Test",
            Description = "Test distro",
            Iso = new DistroIsoSpec { DownloadUrl = new Uri("https://example.org/x.iso"), Sha256 = "abc" },
            Status = status,
            Logo = logo,
            SourceDirectory = sourceDirectory,
        };

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("available", true)]
    [InlineData("AVAILABLE", true)]
    [InlineData("coming-soon", false)]
    [InlineData("in-development", false)]
    public void IsAvailable_depends_only_on_the_status_string(string? status, bool expected)
    {
        Manifest(status).IsAvailable.Should().Be(expected);
    }

    [Fact]
    public void Logo_path_is_null_without_both_logo_and_source_directory()
    {
        Manifest(logo: "logo/x.png").LogoAbsolutePath.Should().BeNull();
        Manifest(sourceDirectory: @"C:\distros\test").LogoAbsolutePath.Should().BeNull();
        Manifest(logo: "", sourceDirectory: @"C:\distros\test").LogoAbsolutePath.Should().BeNull();
    }

    [Fact]
    public void Logo_path_combines_source_directory_and_relative_logo()
    {
        var manifest = Manifest(logo: "logo/x.png", sourceDirectory: @"C:\distros\test");

        manifest.LogoAbsolutePath.Should().Be(@"C:\distros\test\logo\x.png");
    }
}
