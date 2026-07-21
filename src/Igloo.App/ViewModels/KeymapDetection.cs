using System.Globalization;
using Microsoft.Win32;

namespace Igloo.App.ViewModels;

/// <summary>
/// Maps the user's Windows keyboard layout to a Linux XKB keymap name.
///
/// Strategy (most-accurate first):
///   1. Registry <c>HKCU\Keyboard Layout\Preload\1</c> → KLID hex string (e.g. "0000080c").
///      This reflects the actual keyboard the user has installed, regardless of the
///      Windows display language.
///   2. <see cref="CultureInfo.CurrentUICulture"/> heuristic - fallback only, because
///      UI language ≠ keyboard layout (English Windows + Belgian AZERTY is common).
/// </summary>
internal static class KeymapDetection
{
    internal static string DetectCurrent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
            var klid = key?.GetValue("1")?.ToString()?.ToLowerInvariant().TrimStart('0');
            if (!string.IsNullOrEmpty(klid) && KlidMap.TryGetValue(klid, out var mapped))
                return mapped;
        }
        catch { /* registry unavailable - fall through */ }

        return FromCulture(CultureInfo.CurrentUICulture.Name);
    }

    /// <summary>
    /// Windows Keyboard Layout IDs (KLID) → Linux XKB layout names.
    /// KLIDs are 8-char hex; we strip leading zeroes before lookup.
    /// Reference: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/windows-language-pack-default-values
    /// </summary>
    private static readonly Dictionary<string, string> KlidMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        { "409",   "us" },  // English (US)
        { "809",   "gb" },  // English (UK)
        { "1009",  "ca" },  // English (Canada) - Canadian Multilingual Standard
        { "1409",  "us" },  // English (New Zealand)
        { "1809",  "gb" },  // English (Ireland)

        // Belgian / French
        { "80c",   "be" },  // Belgian French   (AZERTY)
        { "813",   "be" },  // Belgian Dutch    (AZERTY)
        { "40c",   "fr" },  // French (France)
        { "100c",  "ch" },  // French (Switzerland)

        // Germanic
        { "407",   "de" },  // German (Germany)
        { "807",   "ch" },  // German (Switzerland)
        { "c07",   "de" },  // German (Austria)
        { "1007",  "de" },  // German (Luxembourg)

        // Dutch
        { "413",   "nl" },  // Dutch (Netherlands)

        // Iberian
        { "40a",   "es" },  // Spanish (Spain)
        { "c0a",   "es" },  // Spanish (Spain, traditional sort)
        { "416",   "br" },  // Portuguese (Brazil)
        { "816",   "pt" },  // Portuguese (Portugal)

        // Italian
        { "410",   "it" },  // Italian (Italy)
        { "810",   "it" },  // Italian (Switzerland)

        // Nordic
        { "41d",   "se" },  // Swedish
        { "414",   "no" },  // Norwegian Bokmål
        { "814",   "no" },  // Norwegian Nynorsk
        { "406",   "dk" },  // Danish
        { "40b",   "fi" },  // Finnish

        // Eastern European
        { "415",   "pl" },  // Polish (programmers)
        { "10415", "pl" },  // Polish (214)
        { "405",   "cz" },  // Czech
        { "40e",   "hu" },  // Hungarian
        { "418",   "ro" },  // Romanian
        { "41b",   "sk" },  // Slovak

        // Other European
        { "41f",   "tr" },  // Turkish Q
        { "1041f", "tr" },  // Turkish F
        { "408",   "gr" },  // Greek
        { "419",   "ru" },  // Russian
        { "422",   "ua" },  // Ukrainian
        { "402",   "bg" },  // Bulgarian (phonetic)
        { "1402",  "bg" },  // Bulgarian (traditional)
        { "424",   "si" },  // Slovenian
        { "41a",   "hr" },  // Croatian
        { "c1a",   "rs" },  // Serbian (Latin)
        { "81a",   "rs" },  // Serbian (Cyrillic)
    };

    /// <summary>
    /// Last-resort fallback: infer a keymap from the Windows UI culture name.
    /// Less accurate than the KLID registry key because the display language
    /// often differs from the physical keyboard layout.
    /// </summary>
    internal static string FromCulture(string cultureName) => cultureName switch
    {
        _ when cultureName.EndsWith("-BE") => "be",
        _ when cultureName.StartsWith("nl") => "nl",
        _ when cultureName.StartsWith("fr-CH") => "ch",
        _ when cultureName.StartsWith("fr") => "fr",
        _ when cultureName.StartsWith("de-CH") => "ch",
        _ when cultureName.StartsWith("de") => "de",
        _ when cultureName.StartsWith("es") => "es",
        _ when cultureName.StartsWith("pt-BR") => "br",
        _ when cultureName.StartsWith("pt") => "pt",
        _ when cultureName.StartsWith("it") => "it",
        _ when cultureName.StartsWith("ru") => "ru",
        _ when cultureName.StartsWith("pl") => "pl",
        _ when cultureName.StartsWith("cs") => "cz",
        _ when cultureName.StartsWith("hu") => "hu",
        _ when cultureName.StartsWith("ro") => "ro",
        _ when cultureName.StartsWith("sk") => "sk",
        _ when cultureName.StartsWith("sv") => "se",
        _ when cultureName.StartsWith("nb") ||
               cultureName.StartsWith("nn") => "no",
        _ when cultureName.StartsWith("da") => "dk",
        _ when cultureName.StartsWith("fi") => "fi",
        _ when cultureName.StartsWith("tr") => "tr",
        _ when cultureName.StartsWith("el") => "gr",
        _ when cultureName.StartsWith("uk") => "ua",
        _ => "us",
    };
}
