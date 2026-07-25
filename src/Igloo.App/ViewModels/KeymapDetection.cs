using System.Globalization;
using System.IO;
using System.Security;
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
        => TryDetectFromRegistry() ?? FromCulture(CultureInfo.CurrentUICulture.Name);

    /// <summary>
    /// Reads the installed keyboard's KLID from the registry and maps it, or returns
    /// <c>null</c> when the key is missing/unreadable so the caller falls back to culture.
    /// </summary>
    private static string? TryDetectFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
            // KlidMap uses OrdinalIgnoreCase, so no manual lower-casing is needed.
            var klid = key?.GetValue("1")?.ToString()?.TrimStart('0');
            return !string.IsNullOrEmpty(klid) && KlidMap.TryGetValue(klid, out var mapped)
                ? mapped
                : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
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
        _ when cultureName.EndsWith("-BE", StringComparison.Ordinal) => "be",
        _ when cultureName.StartsWith("nl", StringComparison.Ordinal) => "nl",
        _ when cultureName.StartsWith("fr-CH", StringComparison.Ordinal) => "ch",
        _ when cultureName.StartsWith("fr", StringComparison.Ordinal) => "fr",
        _ when cultureName.StartsWith("de-CH", StringComparison.Ordinal) => "ch",
        _ when cultureName.StartsWith("de", StringComparison.Ordinal) => "de",
        _ when cultureName.StartsWith("es", StringComparison.Ordinal) => "es",
        _ when cultureName.StartsWith("pt-BR", StringComparison.Ordinal) => "br",
        _ when cultureName.StartsWith("pt", StringComparison.Ordinal) => "pt",
        _ when cultureName.StartsWith("it", StringComparison.Ordinal) => "it",
        _ when cultureName.StartsWith("ru", StringComparison.Ordinal) => "ru",
        _ when cultureName.StartsWith("pl", StringComparison.Ordinal) => "pl",
        _ when cultureName.StartsWith("cs", StringComparison.Ordinal) => "cz",
        _ when cultureName.StartsWith("hu", StringComparison.Ordinal) => "hu",
        _ when cultureName.StartsWith("ro", StringComparison.Ordinal) => "ro",
        _ when cultureName.StartsWith("sk", StringComparison.Ordinal) => "sk",
        _ when cultureName.StartsWith("sv", StringComparison.Ordinal) => "se",
        _ when cultureName.StartsWith("nb", StringComparison.Ordinal) ||
               cultureName.StartsWith("nn", StringComparison.Ordinal) => "no",
        _ when cultureName.StartsWith("da", StringComparison.Ordinal) => "dk",
        _ when cultureName.StartsWith("fi", StringComparison.Ordinal) => "fi",
        _ when cultureName.StartsWith("tr", StringComparison.Ordinal) => "tr",
        _ when cultureName.StartsWith("el", StringComparison.Ordinal) => "gr",
        _ when cultureName.StartsWith("uk", StringComparison.Ordinal) => "ua",
        _ => "us",
    };
}
