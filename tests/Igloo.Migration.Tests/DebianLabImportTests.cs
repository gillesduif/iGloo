using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class DebianLabImportTests
{
    [Theory]
    [InlineData("retag")]
    [InlineData("missing-scope")]
    [InlineData("wrong-format-scope")]
    [InlineData("downgrade")]
    public void OldOrContradictoryTransitionsCannotAuthorizeImport(string mutation)
    {
        var evidence = LabStorageFixture.Create(mutation != "retag");
        evidence = mutation switch
        {
            "retag" => evidence with { SchemaVersion = 2, Scope = "ConfiguredRootImport" },
            "downgrade" => evidence with { SchemaVersion = 1, Scope = "StorageSmoke" },
            "missing-scope" => evidence with { Transitions = evidence.Transitions.SetItem(0, evidence.Transitions[0] with
                { CreationIntent = evidence.Transitions[0].CreationIntent.Replace("ConfiguredRootImport", "", StringComparison.Ordinal) }) },
            _ => evidence with { Transitions = evidence.Transitions.SetItem(2, evidence.Transitions[2] with
                { FormatIntent = evidence.Transitions[2].FormatIntent.Replace("ConfiguredRootImport", "StorageSmoke", StringComparison.Ordinal) }) },
        };
        var result = InstallationStorage.VerifyLab(evidence, LinuxInstallerInventoryProtocol.Parse(evidence.Transitions[^1].Formatted),
            Observations.Available(evidence.GuestDisks));
        Assert.NotEqual(ObservationAvailability.Available, result.Availability);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("generation")]
    [InlineData("pin")]
    [InlineData("expired")]
    [InlineData("transport")]
    public void ImportCompositionRequiresBothAuthorities(string mutation)
    {
        var storage = LabStorageFixture.Validate(LabStorageFixture.Create(mutation != "scope"));
        var pin = new DebianRootDevelopmentPinV1(Guid.NewGuid(), new('A', 64), new('B', 64), DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        var plan = new DebianLabImportPlanV1(1, storage.Provenance, pin.BuildId, Guid.NewGuid(), pin.DescriptorSha256,
            pin.PolicySha256, new('C', 64), new('D', 64), 200, "Chunked", new('E', 64), 0);
        if (mutation == "generation") plan = plan with { Provenance = plan.Provenance with { GenerationId = Guid.NewGuid() } };
        if (mutation == "pin") pin = pin with { DescriptorSha256 = new('F', 64) };
        if (mutation == "expired") pin = pin with { NotAfterUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
        if (mutation == "transport") plan = plan with { TransportManifestSha256 = null };
        Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.ForLabDevelopmentImport(storage, plan,
            new DebianDeploymentFixture.Journal(), new DebianDeploymentFixture.Journal(), pin));
    }

    [Fact]
    public async Task ImportProvenancePersistsWithoutOpeningProductionOrSmoke()
    {
        var storage = LabStorageFixture.Validate(LabStorageFixture.Create(true));
        Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.ForLabStorageSmoke(storage, new DebianDeploymentFixture.Journal()));
        var pin = new DebianRootDevelopmentPinV1(Guid.NewGuid(), new('A', 64), new('B', 64), DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        var plan = new DebianLabImportPlanV1(1, storage.Provenance, pin.BuildId, Guid.NewGuid(), pin.DescriptorSha256,
            pin.PolicySha256, new('C', 64), new('D', 64), 200, "Chunked", new('E', 64), 0);
        var journal = new DebianDeploymentFixture.Journal();
        var authority = DebianMountSessionAuthority.ForLabDevelopmentImport(storage, plan, journal, new DebianDeploymentFixture.Journal(), pin);
        Assert.Null(authority.StorageSmoke);
        Assert.Same(storage, authority.LabStorage);
        Assert.Same(plan, authority.SourcePlan);
        await authority.BeginAsync(CancellationToken.None);
        using var record = JsonDocument.Parse(Assert.Single(journal.Checkpoints));
        Assert.Equal("ConfiguredRootImport", record.RootElement.GetProperty("Provenance").GetProperty("Scope").GetString());
        Assert.Equal(storage.Provenance.GenerationId, authority.GenerationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.BeginAsync(CancellationToken.None));
    }
}
