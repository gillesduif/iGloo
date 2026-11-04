using System.IO;
using FluentAssertions;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Host;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class MigrationSetupBrowserSelectionTests
{
    private sealed class Profile(IReadOnlyDictionary<string, string> browserApps,
        IReadOnlyList<AppSuggestion>? suggestions = null) : IUserProfileInspection
    {
        public IReadOnlyList<AppSuggestion> GetSuggestedPackages() => suggestions ?? [];
        public string? FindAccountPicture() => null;
        public string? FlatpakForBrowser(string browserName)
            => browserApps.GetValueOrDefault(browserName);
    }

    [Fact]
    public void Selected_profiles_include_the_browser_install_needed_to_open_them()
    {
        var profile = new Profile(new Dictionary<string, string>
        {
            ["Google Chrome"] = "com.google.Chrome",
            ["Microsoft Edge"] = "com.microsoft.Edge",
            ["Waterfox"] = "net.waterfox.waterfox",
        }, [new AppSuggestion("Google Chrome", "Google Chrome", "com.google.Chrome", null)]);
        var vm = new MigrationSetupViewModel(profile,
        [Browser("Google Chrome", "AppData", "Local", "Google", "Chrome", "User Data", "Default"),
         Browser("Microsoft Edge", "AppData", "Local", "Microsoft", "Edge", "User Data", "Default"),
         Browser("Mozilla Firefox", "AppData", "Roaming", "Mozilla", "Firefox", "Profiles"),
         Browser("Waterfox", "AppData", "Roaming", "Waterfox", "Profiles")]);

        // A user can decline an app suggestion without losing the browser they
        // explicitly selected for profile migration.
        vm.DetectedSuggestions.Single().IsSelected = false;

        vm.DetectedBrowsers.Should().OnlyContain(browser => browser.CanMigrate && browser.IsSelected);
        vm.GetSelectedBrowserNames().Should().BeEquivalentTo(
            "Google Chrome", "Microsoft Edge", "Mozilla Firefox", "Waterfox");
        vm.GetSelectedBrowsers().Should().Contain(browser =>
            browser.Name == "Microsoft Edge" && browser.Engine == "chromium"
            && browser.SourceRelativePath.EndsWith("Microsoft/Edge/User Data", StringComparison.Ordinal));
        vm.GetSelectedBrowsers().Should().Contain(browser =>
            browser.Name == "Waterfox" && browser.Engine == "gecko"
            && browser.DestRelativePath == ".waterfox");
        vm.GetSelectedBrowsers().Should().Contain(browser =>
            browser.Name == "Mozilla Firefox" && browser.Engine == "gecko"
            && browser.DestRelativePath == ".mozilla/firefox");

        var packages = vm.GetSelectedSuggestions();
        packages.Select(package => package.FlatpakId).Should().BeEquivalentTo(
            "com.google.Chrome", "com.microsoft.Edge", "net.waterfox.waterfox");
        packages.Should().OnlyContain(package => package.AutoInstall);
    }

    [Fact]
    public void Browser_without_a_Linux_install_mapping_is_visible_but_not_exported()
    {
        var vm = new MigrationSetupViewModel(new Profile(new Dictionary<string, string>()),
        [Browser("Microsoft Edge", "AppData", "Local", "Microsoft", "Edge", "User Data", "Default"),
         Browser("Waterfox", "AppData", "Roaming", "Waterfox", "Profiles"),
         Browser("Mozilla Firefox", "AppData", "Roaming", "Mozilla", "Firefox", "Profiles")]);

        vm.DetectedBrowsers.Should().Contain(browser => browser.Name == "Microsoft Edge"
            && !browser.CanMigrate && !browser.IsSelected && browser.NeedsManualTransfer
            && browser.AvailabilityMessage.StartsWith("Continue without Microsoft Edge", StringComparison.Ordinal));
        vm.DetectedBrowsers.Should().Contain(browser => browser.Name == "Waterfox"
            && !browser.CanMigrate && !browser.IsSelected);
        vm.DetectedBrowsers.Single(browser => browser.Name == "Mozilla Firefox")
            .IsSelected.Should().BeTrue();

        // Even if a caller toggles a disabled row, all manifest projections keep
        // the unsupported profile out and retain the bundled Firefox profile.
        vm.DetectedBrowsers.Single(browser => browser.Name == "Microsoft Edge").IsSelected = true;
        vm.GetSelectedBrowserNames().Should().ContainSingle().Which.Should().Be("Mozilla Firefox");
        vm.GetSelectedBrowsers().Should().ContainSingle().Which.Name.Should().Be("Mozilla Firefox");
        vm.GetSelectedSuggestions().Should().BeEmpty();
    }

    private static BrowserEntry Browser(string name, params string[] relativePath)
        => new(name, Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Join(relativePath)));
}
