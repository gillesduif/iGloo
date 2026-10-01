using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class DebianStorageSmokeTests
{
    [Fact]
    public async Task SmokeScopeHasNoImportAuthorityAndPersistsProvider()
    {
        var evidence = LabStorageFixture.Create(); var storage = LabStorageFixture.Validate(evidence);
        var journal = new DebianDeploymentFixture.Journal();
        var authority = DebianMountSessionAuthority.ForLabStorageSmoke(storage, journal);
        Assert.Null(authority.ImportPlan);
        await authority.BeginAsync(CancellationToken.None);
        await authority.ObserveSupervisorAsync(JsonSerializer.SerializeToElement(new { authority.SessionId, Kind = "SessionReady",
            Observation = new { Namespace = "mnt:[fixture-private]", Mounts = new[] { new { Propagation = Array.Empty<string>() } } } }),
            "mnt:[fixture-host]", CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => authority.BeginAction(DebianMountSessionAction.ImportConfiguredRoot));
        Assert.Throws<InvalidOperationException>(() => authority.BeginAction(DebianMountSessionAction.MountLinuxEsp));
        authority.BeginAction(DebianMountSessionAction.AcquireLeases);
        Assert.Throws<InvalidOperationException>(() => authority.BeginAction(DebianMountSessionAction.AcquireLeases));
        var inventory = LinuxInstallerInventoryProtocol.Parse(evidence.Transitions[^1].Formatted).Value;
        var message = JsonSerializer.SerializeToElement(new { authority.SessionId, authority.GenerationId, authority.PlanSha256,
            Action = "AcquireLeases", Kind = "Inventory", Challenge = Guid.NewGuid(), ObservationSequence = 1,
            Inventory = JsonSerializer.Deserialize<JsonElement>(LabStorageFixture.Wire(inventory)),
            DeviceNumbers = LabStorageFixture.Numbers(inventory), GuestDisks = evidence.GuestDisks });
        var response = await authority.AcceptEventAsync(message, CancellationToken.None);
        Assert.Equal("StorageSmoke", response.GetProperty("Leases").GetProperty("Provenance").GetProperty("Scope").GetString());
        await using var native = new DebianNativeMountSession(authority);
        Assert.Equal(ObservationAvailability.Unsupported, DebianConfiguredRootStages.ImportSupport(native, authority.Leases!).Availability);
        await Assert.ThrowsAsync<InvalidDataException>(() => authority.AcceptEventAsync(message, CancellationToken.None));
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, authority.State);
        Assert.All(journal.Checkpoints, bytes =>
        {
            using var doc = JsonDocument.Parse(bytes);
            Assert.Equal("IsolatedFileBackedLab", doc.RootElement.GetProperty("Provenance").GetProperty("Provider").GetString());
            Assert.Equal(evidence.GenerationId, doc.RootElement.GetProperty("GenerationId").GetGuid());
        });
    }

    [Fact]
    public async Task FailedIntentReopenPreventsSupervisorAuthorization()
    {
        var journal = new DebianDeploymentFixture.Journal { FailReopen = true };
        var authority = DebianMountSessionAuthority.ForLabStorageSmoke(LabStorageFixture.Validate(LabStorageFixture.Create()), journal);
        await Assert.ThrowsAsync<IOException>(() => authority.BeginAsync(CancellationToken.None));
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, authority.State);
        Assert.Throws<InvalidOperationException>(() => authority.BeginAction(DebianMountSessionAction.AcquireLeases));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.BeginAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("target")]
    [InlineData("tmpfs")]
    [InlineData("alias")]
    [InlineData("uuid")]
    public void JournalPlacementUsesSharedLeasesAndOutsideTargetDisk(string mutation)
    {
        var e = LabStorageFixture.Create(); var storage = LabStorageFixture.Validate(e);
        var inventory = LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted).Value;
        var numbers = LabStorageFixture.Numbers(inventory);
        var journal = inventory.Partitions.Single(p => p.DevicePath == "/dev/vdc1");
        var number = numbers.Single(n => n.DevicePath == (mutation == "target" ? "/dev/vdb4" : journal.DevicePath));
        var uuid = Guid.Parse(journal.FileSystem.Value.Uuid!);
        var a = new DebianJournalStoreWitnessV1("/journal/session", 200, 10, 30, number.Major, number.Minor, mutation == "tmpfs" ? "tmpfs" : "EXT4");
        var b = a with { Path = "/journal/import", Inode = mutation == "alias" ? a.Inode : 11 };
        var observed = JsonSerializer.SerializeToElement(new { Inventory = JsonSerializer.Deserialize<JsonElement>(LabStorageFixture.Wire(inventory)),
            DeviceNumbers = numbers, GuestDisks = e.GuestDisks, Stores = new[] { a, b } });
        if (mutation == "valid") Assert.Equal(2, DebianImportJournalStorage.Verify(storage, a.Path, b.Path, uuid, observed).Length);
        else Assert.Throws<InvalidDataException>(() => DebianImportJournalStorage.Verify(storage, a.Path, b.Path,
            mutation == "uuid" ? Guid.NewGuid() : uuid, observed));
    }
}
