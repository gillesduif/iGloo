namespace Igloo.Core.Services;

/// <summary>Builds a hostname the installers will accept from a Linux user name.</summary>
/// <remarks>
/// Hostname rules are stricter than user name rules: RFC 1123 allows only a-z, 0-9 and
/// hyphens, with no leading or trailing hyphen and at most 63 characters. The user name
/// sanitizer maps invalid characters to an underscore, which is legal there and rejected
/// here - debian-installer stops on the "Invalid hostname" screen and the unattended
/// install turns into a manual one.
/// </remarks>
public static class LinuxHostname
{
    private const int MaxLength = 63;
    private const string Suffix = "-pc";
    private const string Fallback = "igloo-pc";

    /// <summary>
    /// The Windows computer name where it survives sanitising, otherwise
    /// <c>&lt;username&gt;-pc</c>.
    /// </summary>
    /// <remarks>
    /// "DESKTOP-Living" becomes "desktop-living": the machine keeps the name its owner
    /// gave it, which is the whole point of a migration. The username is only a fallback
    /// for domain names that sanitise away to nothing.
    /// </remarks>
    public static string FromMachine(string? computerName, string? username)
    {
        var fromComputer = Clean(computerName);
        return fromComputer is not null ? fromComputer : FromUsername(username);
    }

    /// <summary>Returns <paramref name="username"/> as <c>&lt;name&gt;-pc</c>, RFC 1123 clean.</summary>
    public static string FromUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return Fallback;

        var chars = new List<char>(username.Length);
        foreach (var raw in username)
        {
            var c = char.ToLowerInvariant(raw);
            // Underscores and spaces are word separators here, not characters to drop:
            // "gilles_dhuyvetter" reads better as "gilles-dhuyvetter" than "gillesdhuyvetter".
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
                chars.Add(c);
            else if (chars.Count > 0 && chars[^1] != '-')
                chars.Add('-');
        }

        // Trim first, then append: capping afterwards could leave a trailing hyphen.
        while (chars.Count > 0 && chars[^1] == '-')
            chars.RemoveAt(chars.Count - 1);

        if (chars.Count == 0)
            return Fallback;

        if (chars.Count > MaxLength - Suffix.Length)
            chars.RemoveRange(MaxLength - Suffix.Length, chars.Count - (MaxLength - Suffix.Length));
        while (chars.Count > 0 && chars[^1] == '-')
            chars.RemoveAt(chars.Count - 1);

        return chars.Count == 0 ? Fallback : new string([.. chars]) + Suffix;
    }

    /// <summary>RFC 1123 form of <paramref name="value"/>, or null when nothing is left.</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var chars = new List<char>(value.Length);
        foreach (var raw in value)
        {
            var c = char.ToLowerInvariant(raw);
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
                chars.Add(c);
            else if (chars.Count > 0 && chars[^1] != '-')
                chars.Add('-');
        }

        if (chars.Count > MaxLength)
            chars.RemoveRange(MaxLength, chars.Count - MaxLength);
        while (chars.Count > 0 && chars[^1] == '-')
            chars.RemoveAt(chars.Count - 1);

        return chars.Count == 0 ? null : new string([.. chars]);
    }
}
