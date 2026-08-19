
namespace Igloo.App.ViewModels;

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

        return name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-');
    }

    
    internal static string Sanitize(string windowsName)
    {
        // useradd only accepts lowercase names, so each character is lowered before it is vetted.
        var s = string.Concat(windowsName
            .Select(char.ToLowerInvariant)
            // Apostrophes sit inside a word, so dropping them keeps the word whole:
            // "D'huyvetter" becomes "dhuyvetter", not "d_huyvetter".
            .Where(c => c is not ('\'' or '’'))
            .Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-' ? c : '_'));

        // Everything else that is invalid becomes a separator, so a run of them
        // ("Jan  Peeters", "a.-b") would otherwise leave "__" in the name.
        while (s.Contains("__", StringComparison.Ordinal))
            s = s.Replace("__", "_", StringComparison.Ordinal);

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
