using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Igloo.Fleet.Web.Presentation;

/// <summary>One navigation entry. <see cref="Implemented"/> gates rendering while pages land step by step.</summary>
internal sealed record NavEntry(string Key, string Href, Func<Icon>? Icon = null, NavLinkMatch Match = NavLinkMatch.Prefix, bool Implemented = true);

internal sealed record NavSection(string Key, Func<Icon> Icon, IReadOnlyList<NavEntry> Entries);

/// <summary>The information architecture from console-design.md, section 3. Labels are ShellStrings keys.</summary>
internal static class ConsoleNavigation
{
    public static readonly NavEntry Overview = new("Overview", "/", static () => new Icons.Regular.Size20.Home(), NavLinkMatch.All);

    public static readonly NavEntry Audit = new("AuditEvents", "/audit", static () => new Icons.Regular.Size20.DocumentBulletList());

    public static readonly IReadOnlyList<NavSection> Sections =
    [
        new("Enrollments", static () => new Icons.Regular.Size20.Certificate(),
        [
            new("AllEnrollments", "/enrollments", Match: NavLinkMatch.All),
            new("EnrollmentTokens", "/enrollments/tokens"),
        ]),
        new("Planning", static () => new Icons.Regular.Size20.ClipboardTask(),
        [
            new("MigrationProfiles", "/planning/profiles"),
            new("WorkItems", "/planning/work"),
            new("Evidence", "/planning/evidence"),
            new("Approvals", "/planning/approvals"),
            new("PreparedPlans", "/planning/plans"),
        ]),
    ];

    public static readonly IReadOnlyList<NavEntry> Settings =
    [
        new("ServerConnection", "/settings/server"),
        new("ConsoleAccounts", "/settings/accounts"),
        new("ScopeAndLimits", "/settings/scope"),
        new("About", "/settings/about"),
    ];

    /// <summary>Every entry a signed-in operator can reach from the navigation, for tests and for the breadcrumb trail.</summary>
    public static IEnumerable<NavEntry> AllEntries()
    {
        yield return Overview;
        foreach (var section in Sections)
        {
            foreach (var entry in section.Entries)
            {
                yield return entry;
            }
        }

        yield return Audit;
        foreach (var page in ModuleCatalog.AllPages())
        {
            yield return new NavEntry(page.Key, page.Href, page.Icon);
        }

        foreach (var entry in Settings)
        {
            yield return entry;
        }
    }

    public static NavSection? SectionFor(string relativePath) =>
        Sections.FirstOrDefault(section => section.Entries.Any(entry => IsUnder(relativePath, entry)));

    private static bool IsUnder(string relativePath, NavEntry entry)
    {
        var path = "/" + relativePath.TrimStart('/');
        return entry.Match == NavLinkMatch.All
            ? string.Equals(path.TrimEnd('/'), entry.Href.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            : path.StartsWith(entry.Href, StringComparison.OrdinalIgnoreCase);
    }
}
