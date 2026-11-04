using Igloo.Community.App.ViewModels;
using Igloo.Core.Host;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class MigrationSetupWifiSelectionTests
{
    private sealed class EmptyProfile : IUserProfileInspection
    {
        public IReadOnlyList<AppSuggestion> GetSuggestedPackages() => [];
        public string? FindAccountPicture() => null;
        public string? FlatpakForBrowser(string browserName) => null;
    }

    [Theory]
    [InlineData("debian")]
    [InlineData("linuxmint-cinnamon")]
    [InlineData("fedora-kde")]
    [InlineData("fedora-workstation")]
    public void Every_distribution_migrates_saved_wifi_networks_by_default(string distroId)
    {
        var setup = new MigrationSetupViewModel(new EmptyProfile());

        setup.ConfigureDistro(distroId);
        Assert.True(setup.IsWifiMigrationAvailable);
        Assert.True(setup.IncludeWifiNetworks);
    }

    [Fact]
    public void Changing_distribution_does_not_reset_the_wifi_selection()
    {
        var setup = new MigrationSetupViewModel(new EmptyProfile());

        setup.ConfigureDistro("debian");
        setup.IncludeWifiNetworks = false;
        setup.ConfigureDistro("fedora-kde");
        Assert.False(setup.IncludeWifiNetworks);
        setup.ConfigureDistro("linuxmint-cinnamon");
        Assert.False(setup.IncludeWifiNetworks);
    }
}
