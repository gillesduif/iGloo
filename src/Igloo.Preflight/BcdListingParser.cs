using System.Text.RegularExpressions;

namespace Igloo.Preflight;

internal static class BcdListingParser
{
    internal static IReadOnlyList<string> ParseStaleBcdIds(string? listing, string description)
    {
        if (string.IsNullOrEmpty(listing))
            return [];

        var ids = new List<string>();
        foreach (var block in Regex.Split(listing, @"\r?\n[ \t]*\r?\n"))
        {
            if (!block.Contains(description, StringComparison.Ordinal))
                continue;

            var id = Regex.Match(block, @"^identifier\s+(\{[0-9a-fA-F-]{36}\})",
                                 RegexOptions.Multiline).Groups[1].Value;
            if (id.Length > 0)
                ids.Add(id);
        }
        return ids;
    }

}
