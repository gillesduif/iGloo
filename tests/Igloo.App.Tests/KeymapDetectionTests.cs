using FluentAssertions;
using Igloo.App.ViewModels;
using Xunit;

namespace Igloo.App.Tests;

public class KeymapDetectionTests
{
    [Theory]
    [InlineData("nl-BE", "be")]
    [InlineData("fr-BE", "be")]
    [InlineData("nl-NL", "nl")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-CH", "ch")]
    [InlineData("de-CH", "ch")]
    [InlineData("de-DE", "de")]
    [InlineData("pt-BR", "br")]
    [InlineData("pt-PT", "pt")]
    [InlineData("en-US", "us")]
    [InlineData("ja-JP", "us")]
    [InlineData("", "us")]
    public void Culture_fallback_maps_to_the_expected_xkb_layout(string culture, string expected)
    {
        KeymapDetection.FromCulture(culture).Should().Be(expected);
    }

    [Fact]
    public void DetectCurrent_always_returns_a_non_empty_layout()
    {
        KeymapDetection.DetectCurrent().Should().NotBeNullOrWhiteSpace();
    }
}
