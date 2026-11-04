using Igloo.Community.App.ViewModels;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class DistroSelectionViewModelTests
{
    [Fact]
    public void FilteringToSoleInstallableDistributionEnablesNext()
    {
        var catalog = new Catalog([
            Manifest("kde-neon", "KDE Plasma", "coming-soon"),
            Manifest("linuxmint-cinnamon", "Cinnamon", "available"),
        ]);
        var picker = new DistroSelectionViewModel(catalog, new Compatibility(),
            NullLogger<DistroSelectionViewModel>.Instance);

        picker.SelectedCategory = "Cinnamon";

        Assert.Equal("linuxmint-cinnamon", picker.SelectedDistro?.Id);
        Assert.True(picker.CanProceed);
    }

    [Fact]
    public void SwitchingCategoryKeepsUnchangedChipList()
    {
        var picker = new DistroSelectionViewModel(new Catalog([
                Manifest("linuxmint-cinnamon", "Cinnamon", "available"),
                Manifest("fedora-kde", "KDE Plasma", "available"),
            ]), new Compatibility(), NullLogger<DistroSelectionViewModel>.Instance);
        var chips = picker.Categories;

        picker.SelectedCategory = "Cinnamon";

        Assert.Same(chips, picker.Categories);
    }

    [Fact]
    public void DesktopFilterCentersAnInstallableDistroAheadOfComingSoonCovers()
    {
        var picker = new DistroSelectionViewModel(new Catalog([
                Manifest("garuda", "KDE Plasma", "coming-soon"),
                Manifest("fedora-kde", "KDE Plasma", "available"),
                Manifest("kde-neon", "KDE Plasma", "coming-soon"),
            ]), new Compatibility(), NullLogger<DistroSelectionViewModel>.Instance);

        picker.SelectedCategory = "KDE Plasma";

        Assert.Equal("fedora-kde", picker.SelectedDistro?.Id);
        Assert.True(picker.CanProceed);
    }

    [Fact]
    public void RecommendedShelfCentersAnInstallableDistroWhenAnUnavailableCoverRanksFirst()
    {
        var picker = new DistroSelectionViewModel(new Catalog([
                Manifest("ubuntu", "GNOME", "coming-soon"),
                Manifest("debian", "GNOME", "available"),
            ]), new Compatibility(), NullLogger<DistroSelectionViewModel>.Instance);

        picker.SetRecommendation(["ubuntu", "debian"]);
        picker.RefreshCompatibility(null);

        Assert.Equal(DistroSelectionViewModel.RecommendedCategory, picker.SelectedCategory);
        Assert.Equal("debian", picker.SelectedDistro?.Id);
        Assert.True(picker.CanProceed);
    }

    private static DistroManifest Manifest(string id, string desktop, string status) => new()
    {
        Id = id,
        DisplayName = id,
        Description = id,
        DefaultDesktopEnvironment = desktop,
        Status = status,
        Iso = new DistroIsoSpec
        {
            DownloadUrl = new Uri("https://example.invalid/installer.iso"),
            Sha256 = new string('a', 64),
        },
    };

    private sealed class Catalog(IReadOnlyList<DistroManifest> distros) : IProviderCatalog
    {
        public IReadOnlyList<DistroManifest> LoadedDistros => distros;
    }

    private sealed class Compatibility : IProviderCompatibility
    {
        public bool IsLoaded(string distroId) => true;
        public IReadOnlyList<PreflightFinding> Evaluate(string distroId, PreflightReport report) => [];
        public Task<ProviderInspection?> InspectAsync(string distroId,
            CancellationToken cancellationToken = default) => Task.FromResult<ProviderInspection?>(null);
    }
}
