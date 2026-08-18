
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
            .Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-' ? c : '_'));

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
