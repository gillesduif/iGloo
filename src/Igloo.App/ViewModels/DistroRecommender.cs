using Igloo.Core.Models;

namespace Igloo.App.ViewModels;

public static class DistroRecommender
{
    // Option ids the Welcome quiz feeds in (kept as strings so the quiz copy can
    // change freely without touching the scoring).
    public const string UseEveryday = "everyday";
    public const string UseGaming = "gaming";
    public const string UseWork = "work";
    public const string UseTinker = "tinker";
    public const string StyleFamiliar = "familiar";
    public const string StyleFresh = "fresh";
    public const string UpdatesStable = "stable";
    public const string UpdatesLatest = "latest";

    private sealed record Traits(int Everyday, int Gaming, int Work, int Tinker,
                                 int WindowsLike, int Modern, int Stable, int Cutting);

    private static readonly Dictionary<string, Traits> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        //                       every gaming work tinker | winlike modern | stable cutting
        ["bazzite"] = new(1, 2, 0, 1, 0, 2, 1, 2),
        ["cachyos"] = new(0, 2, 0, 2, 1, 1, 0, 2),
        ["endeavouros"] = new(0, 1, 0, 2, 1, 1, 0, 2),
        ["garuda"] = new(0, 2, 0, 2, 1, 2, 0, 2),
        ["linux-lite"] = new(2, 0, 1, 0, 2, 0, 2, 0),
        ["linuxmint-cinnamon"] = new(2, 1, 1, 0, 2, 0, 2, 0),
        ["mx-linux"] = new(2, 0, 1, 1, 1, 0, 2, 0),
        ["nobara"] = new(1, 2, 0, 1, 1, 1, 1, 2),
        ["zorin-os"] = new(2, 1, 1, 0, 2, 1, 1, 0),
        ["ubuntu"] = new(2, 1, 2, 1, 0, 1, 1, 1),
        ["debian"] = new(1, 0, 1, 2, 0, 0, 2, 0),
        ["fedora-kde"] = new(1, 1, 2, 1, 1, 1, 1, 1),
        ["fedora-workstation"] = new(1, 0, 2, 1, 0, 2, 1, 1),
        ["kde-neon"] = new(0, 0, 1, 2, 1, 1, 0, 2),
        ["manjaro"] = new(0, 2, 0, 2, 0, 1, 0, 2),
        ["opensuse"] = new(1, 0, 2, 1, 1, 0, 1, 1),
        ["pop-os"] = new(1, 2, 1, 1, 0, 2, 0, 1),
        ["elementary-os"] = new(1, 0, 1, 0, 0, 2, 1, 0),
        ["deepin"] = new(1, 0, 0, 0, 0, 2, 1, 0),
    };

    public static IReadOnlyList<DistroManifest> Recommend(IReadOnlyList<DistroManifest> catalog,
        string? use, string? style, string? updates)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (use is null || style is null || updates is null || catalog.Count == 0)
            return [];

        var ranked = catalog
            .Select((m, order) => (Manifest: m, Score: Score(m, use, style, updates), Order: order))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Order)          // catalog order breaks exact ties deterministically
            .ToList();

        var best = ranked[0].Score;
        return ranked
            .TakeWhile(x => x.Score >= best - 2.0)
            .Take(4)
            .Select(x => x.Manifest)
            .ToList();
    }

    private static double Score(DistroManifest m, string use, string style, string updates)
    {
        if (!Table.TryGetValue(m.Id, out var t))
            t = new Traits(1, 0, 1, 0, 0, 0, 1, 0);   // unknown catalog entry: mild neutral

        var score =
            2.0 * (use switch
            {
                UseEveryday => t.Everyday,
                UseGaming => t.Gaming,
                UseWork => t.Work,
                UseTinker => t.Tinker,
                _ => 0,
            })
            + 1.5 * (style == StyleFamiliar ? t.WindowsLike : t.Modern)
            + 1.5 * (updates == UpdatesStable ? t.Stable : t.Cutting);

        // Tie-break, not a thumb on the scale: an installable distro beats an
        // equally-scored coming-soon one.
        if (m.IsAvailable)
            score += 0.5;
        return score;
    }
}
