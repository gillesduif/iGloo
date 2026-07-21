using System.Text;

namespace Igloo.App.ViewModels;

/// <summary>
/// Validation and sanitization for the Linux username the wizard writes into the
/// manifest. Mirrors useradd(8) constraints: ASCII letter first, then lowercase
/// letters/digits/underscore/hyphen, max 32 chars, no well-known system accounts.
/// </summary>
internal static class LinuxUsernameRules
{
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "root", "daemon", "bin", "sys", "nobody", "mail", "news",
            "uucp", "proxy", "www-data", "backup", "man", "list", "irc",
            "gnats", "games", "messagebus",
        };

    internal static bool IsValid(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (name.Length > 32)
            return false;
        if (ReservedNames.Contains(name))
            return false;
        if (!char.IsAsciiLetter(name[0]))
            return false;

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-'))
                return false;
        }
        return true;
    }

    /// <summary>Derives a plausible Linux username from a Windows account name; "user" as last resort.</summary>
    internal static string Sanitize(string windowsName)
    {
        var sb = new StringBuilder();
        foreach (var c in windowsName.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-' ? c : '_');

        var s = sb.ToString();

        // Strip leading non-letter characters.
        var start = 0;
        while (start < s.Length && !char.IsAsciiLetter(s[start]))
            start++;
        s = s[start..];

        if (string.IsNullOrEmpty(s))
            return "user";
        return s.Length > 32 ? s[..32] : s;
    }
}
