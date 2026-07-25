using FluentAssertions;
using Igloo.App.ViewModels;
using Igloo.Core.Models;
using Xunit;

namespace Igloo.App.Tests;

public class DistroRecommenderTests
{
    private static DistroManifest Distro(string id, string? status = null) => new()
    {
        Id = id,
        DisplayName = id,
        Description = id,
        Status = status,
        Iso = new DistroIsoSpec { DownloadUrl = new Uri("https://example.org/x.iso"), Sha256 = "abc" },
    };

    private static readonly IReadOnlyList<DistroManifest> Catalog =
    [
        Distro("linuxmint-cinnamon"),
        Distro("linux-lite"),
        Distro("debian"),
        Distro("fedora-kde"),
        Distro("garuda"),
        Distro("cachyos"),
    ];

    [Fact]
    public void No_recommendation_until_all_three_answers_are_in()
    {
        DistroRecommender.Recommend(Catalog, null, DistroRecommender.StyleFamiliar,
            DistroRecommender.UpdatesStable).Should().BeEmpty();
        DistroRecommender.Recommend(Catalog, DistroRecommender.UseEveryday, null,
            DistroRecommender.UpdatesStable).Should().BeEmpty();
        DistroRecommender.Recommend(Catalog, DistroRecommender.UseEveryday,
            DistroRecommender.StyleFamiliar, null).Should().BeEmpty();
        DistroRecommender.Recommend([], DistroRecommender.UseEveryday,
            DistroRecommender.StyleFamiliar, DistroRecommender.UpdatesStable).Should().BeEmpty();
    }

    [Fact]
    public void Everyday_familiar_stable_ranks_mint_first()
    {
        var result = DistroRecommender.Recommend(Catalog,
            DistroRecommender.UseEveryday, DistroRecommender.StyleFamiliar,
            DistroRecommender.UpdatesStable);

        result.Should().NotBeEmpty();
        result[0].Id.Should().Be("linuxmint-cinnamon",
            "mint and linux-lite tie on traits; catalog order breaks the tie");
    }

    [Fact]
    public void Gaming_fresh_latest_favors_the_gaming_distros()
    {
        var result = DistroRecommender.Recommend(Catalog,
            DistroRecommender.UseGaming, DistroRecommender.StyleFresh,
            DistroRecommender.UpdatesLatest);

        result[0].Id.Should().Be("garuda");
    }

    [Fact]
    public void Never_more_than_four_recommendations()
    {
        var result = DistroRecommender.Recommend(Catalog,
            DistroRecommender.UseEveryday, DistroRecommender.StyleFamiliar,
            DistroRecommender.UpdatesStable);

        result.Count.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public void Unknown_catalog_ids_get_neutral_traits_not_a_crash()
    {
        var exotic = new[] { Distro("some-brand-new-distro") };

        var result = DistroRecommender.Recommend(exotic,
            DistroRecommender.UseTinker, DistroRecommender.StyleFresh,
            DistroRecommender.UpdatesLatest);

        result.Should().ContainSingle().Which.Id.Should().Be("some-brand-new-distro");
    }

    [Fact]
    public void Installable_distro_outranks_an_identically_scored_coming_soon_one()
    {
        // Same unknown id twice: identical neutral traits, only availability differs.
        var catalog = new[]
        {
            Distro("unknown-a", status: "coming-soon"),
            Distro("unknown-b"),
        };

        var result = DistroRecommender.Recommend(catalog,
            DistroRecommender.UseEveryday, DistroRecommender.StyleFamiliar,
            DistroRecommender.UpdatesStable);

        result[0].Id.Should().Be("unknown-b",
            "the +0.5 availability tie-break must beat catalog order");
    }
}
