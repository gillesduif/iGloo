using FluentAssertions;
using Igloo.Community.App.ViewModels;
using Xunit;

namespace Igloo.Community.App.Tests;

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

    [Theory]
    // Dutch (Netherlands) as input language with the Belgian (Period) layout, first or second in the list.
    [InlineData("00000413,00000813", "00000413=00000813", "be")]
    [InlineData("00000813,00000413", "00000413=00000813", "be")]
    // A second layout of a language is preloaded under a d00x identifier.
    [InlineData("d0010813,00000813,00001009", "00000813=00000409;d0010813=00000813;00001009=00000813", "be")]
    [InlineData("D0010813", "d0010813=00000813", "be")]
    // Dutch (Belgium) as input language with the US layout.
    [InlineData("00000813", "00000813=00000409", "us")]
    // The Windows default for Dutch (Netherlands) is United States-International.
    [InlineData("00000413", "00000413=00020409", "us")]
    [InlineData("00000413", "", "nl")]
    [InlineData("0001080c", "", "be")]
    // A layout without a known Linux name is passed over for the next one.
    [InlineData("00000411,0000040c", "", "fr")]
    public void A_preloaded_language_is_resolved_to_the_layout_it_is_typed_on(
        string preload, string substitutes, string expected)
    {
        KeymapDetection.FromPreload(preload.Split(','), Substitutes(substitutes)).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000411")]
    [InlineData("d0010813")]
    public void Without_a_known_layout_the_registry_gives_no_answer(string preload)
    {
        KeymapDetection.FromPreload(preload.Split(','), Substitutes("")).Should().BeNull();
    }

    private static Dictionary<string, string> Substitutes(string pairs) => pairs
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('='))
        .ToDictionary(pair => pair[0], pair => pair[1]);

    [Fact]
    public void DetectCurrent_always_returns_a_non_empty_layout()
    {
        KeymapDetection.DetectCurrent().Should().NotBeNullOrWhiteSpace();
    }
}
