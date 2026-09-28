using System.Text.Json;
using Igloo.Fleet.Web.Authentication;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class WebAuthenticationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "igloo-web-auth-tests-" + Guid.NewGuid().ToString("N"));
    // Deliberately public test input, never an operator credential.
    private const string FixturePassword = "synthetic-test-password-only";

    [Fact]
    public void AdministratorSurvivesIndependentReopenWithoutPersistingPlaintext()
    {
        var store = Open();
        Assert.False(store.HasOperators());
        var created = store.CreateInitialAdministrator("test.operator", "Test operator", FixturePassword);
        var reopened = Open();
        Assert.True(reopened.HasOperators());
        Assert.Equal(created.OperatorId, reopened.ValidateCredentials("TEST.OPERATOR", FixturePassword)!.OperatorId);
        Assert.DoesNotContain(FixturePassword, File.ReadAllText(Path.Combine(_directory, "operators.json")), StringComparison.Ordinal);
        Assert.Null(reopened.ValidateCredentials("test.operator", "incorrect-fixture-password"));
        Assert.Null(reopened.ValidateCredentials("missing.operator", FixturePassword));
    }

    [Fact]
    public void ExistingAdministratorCannotBeReplacedThroughSetup()
    {
        Open().CreateInitialAdministrator("test.operator", "Test operator", FixturePassword);
        Assert.Throws<InvalidOperationException>(() => Open().CreateInitialAdministrator("other.operator", "Other operator", FixturePassword));
    }

    [Fact]
    public void CorruptStoreDoesNotBecomeAnEmptySetupOpportunity()
    {
        var store = Open();
        File.WriteAllText(Path.Combine(_directory, "operators.json"), "not-json");
        Assert.Throws<JsonException>(() => store.HasOperators());
        Assert.Throws<JsonException>(() => store.CreateInitialAdministrator("test.operator", "Test operator", FixturePassword));
    }

    [Theory]
    [InlineData("../operator", "Test operator", FixturePassword)]
    [InlineData("test.operator", "", FixturePassword)]
    [InlineData("test.operator", "Test operator", "short")]
    public void InvalidSetupDoesNotPublishAnAccount(string username, string displayName, string password)
    {
        var store = Open();
        Assert.Throws<ArgumentException>(() => store.CreateInitialAdministrator(username, displayName, password));
        Assert.False(store.HasOperators());
    }

    private FleetOperatorStore Open() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["FleetAuthentication:DataDirectory"] = _directory }).Build());

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
