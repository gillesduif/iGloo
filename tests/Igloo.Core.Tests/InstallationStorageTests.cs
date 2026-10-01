using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.TestData;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class InstallationStorageTests
{
    [Fact]
    public void ValidatedLabTransitionsProduceOnlyTaggedStorageLeases()
    {
        var e = LabStorageFixture.Create();
        var context = LabStorageFixture.Validate(e);
        var inventory = LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted);
        var lease = InstallerBlockLeases.Acquire(context, Guid.NewGuid(), DateTimeOffset.UtcNow, inventory,
            Observations.Available(LabStorageFixture.Numbers(inventory.Value)), Observations.Available(e.GuestDisks)).Value;
        Assert.Equal(context.Provenance, lease.Provenance);
        Assert.All(lease.Bindings, b => { Assert.Null(b.Partition); Assert.NotNull(b.StoragePartition); });
        Assert.Equal(new[] { InstallerBlockAccess.ReadWrite, InstallerBlockAccess.ReadOnly, InstallerBlockAccess.ReadOnly }, lease.Bindings.Select(b => b.Access));
        Assert.Empty(typeof(ValidatedInstallationStorage).GetConstructors());
        Assert.DoesNotContain("VolumeGuid", System.Text.Json.JsonSerializer.Serialize(lease), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("provider")]
    [InlineData("scope")]
    [InlineData("missing-before")]
    [InlineData("missing-format")]
    [InlineData("parent")]
    [InlineData("geometry")]
    [InlineData("fs")]
    [InlineData("launch")]
    [InlineData("alias")]
    public void ContradictoryOrIncompleteEvidenceCannotBecomeContext(string change)
    {
        var e = LabStorageFixture.Create();
        var t = e.Transitions[1];
        var altered = change switch
        {
            "generation" => e with { GenerationId = Guid.NewGuid() },
            "provider" => e with { Provider = "Windows" },
            "scope" => e with { Scope = "ImportConfiguredRoot" },
            "missing-before" => e with { Transitions = e.Transitions.SetItem(1, t with { Before = "{}" }) },
            "missing-format" => e with { Transitions = e.Transitions.SetItem(1, t with { Formatted = t.Created }) },
            "parent" => e with { Transitions = e.Transitions.SetItem(1, t with { Intended = t.Intended with { DiskDevicePath = "/dev/vda" } }) },
            "geometry" => e with { Transitions = e.Transitions.SetItem(1, t with { Intended = t.Intended with { OffsetBytes = t.Intended.OffsetBytes + 512 } }) },
            "fs" => e with { Transitions = e.Transitions.SetItem(1, t with { FileSystem = t.FileSystem with { Uuid = "9999-9999" } }) },
            "launch" => e with { ReopenedBackings = e.ReopenedBackings.SetItem(0, e.ReopenedBackings[0] with { LaunchSha256 = new('F', 64) }) },
            _ => e with { GuestDisks = e.GuestDisks.Add(e.GuestDisks[0]) },
        };
        var result = InstallationStorage.VerifyLab(altered, LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted), Observations.Available(altered.GuestDisks));
        Assert.NotEqual(ObservationAvailability.Available, result.Availability);
    }

    [Fact]
    public void FreshSubstitutedLocatorCannotReacquireAnActiveLease()
    {
        var e = LabStorageFixture.Create(); var c = LabStorageFixture.Validate(e);
        var i = LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted);
        var numbers = LabStorageFixture.Numbers(i.Value);
        var lease = InstallerBlockLeases.Acquire(c, Guid.NewGuid(), DateTimeOffset.UtcNow, i, Observations.Available(numbers), Observations.Available(e.GuestDisks)).Value;
        Assert.NotEqual(ObservationAvailability.Available, InstallerBlockLeases.Revalidate(lease, c, i,
            Observations.Available(numbers.SetItem(numbers.Length - 1, numbers[^1] with { Minor = 90 })), Observations.Available(e.GuestDisks)).Availability);
    }
}
