using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.TestData;
using Xunit;
using static Igloo.TestData.InstallationOwnershipFixture;

namespace Igloo.Core.Tests;

public sealed class InstallerBlockLeaseTests
{
    private static Observation<InstallerBlockLeaseSet> Acquire(TargetRootFixture.State state) => InstallerBlockLeases.Acquire(
        state.Ownership, state.Root, state.Root.GenerationId, Id(700), new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero),
        A(state.Inventory), A(state.Mounts.DeviceNumbers));

    [Fact]
    public void LeasesUseWholeLayoutAndOnlyOwnedRoles()
    {
        var state = TargetRootFixture.Create(); var leases = Acquire(state).Value;
        Assert.Equal(3, leases.Bindings.Length);
        Assert.Equal(InstallerBlockAccess.ReadWrite, leases.Bindings[0].Access);
        Assert.All(leases.Bindings.Skip(1), b => Assert.Equal(InstallerBlockAccess.ReadOnly, b.Access));
        Assert.DoesNotContain(leases.Bindings, b => b.Partition.PartitionGuid == state.Ownership.Esp.WindowsEsp.Volume.PartitionGuid);
        Assert.All(leases.Bindings, b => Assert.Equal(state.Ownership.Layout.Plan.TargetDisk, b.Partition.Disk));
        Assert.True(InstallerBlockLeases.Revalidate(leases, state.Ownership, state.Root, A(state.Inventory), A(state.Mounts.DeviceNumbers)).Value);
    }

    [Fact]
    public void InventoryAndDeviceEnumerationAreNotAuthority()
    {
        var state = TargetRootFixture.Create();
        var before = Acquire(state).Value;
        var changed = state with { Inventory = state.Inventory with { Partitions = state.Inventory.Partitions.Reverse().ToImmutableArray() },
            Mounts = state.Mounts with { DeviceNumbers = state.Mounts.DeviceNumbers.Reverse().ToImmutableArray() } };
        Assert.Equal(before.ProtectedStateSha256, Acquire(changed).Value.ProtectedStateSha256);
        Assert.Equal(before.Bindings.AsEnumerable(), Acquire(changed).Value.Bindings.AsEnumerable());
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("partition")]
    [InlineData("type")]
    [InlineData("geometry")]
    [InlineData("filesystem")]
    [InlineData("duplicate-partition")]
    [InlineData("duplicate-filesystem")]
    [InlineData("missing")]
    [InlineData("extra")]
    public void ChangedWholeLayoutRejectsLease(string change)
    {
        var state = TargetRootFixture.Create(); var parts = state.Inventory.Partitions;
        var root = parts.Single(p => p.PartitionGuid == state.Root.Partition.PartitionGuid);
        var replacement = change switch
        {
            "parent" => root with { DiskDevicePath = "/dev/wrong" },
            "partition" => root with { PartitionGuid = Id(999) },
            "type" => root with { PartitionType = state.Ownership.Esp.WindowsEsp.Volume.PartitionType },
            "geometry" => root with { SizeBytes = root.SizeBytes - 512 },
            "filesystem" => root with { FileSystem = Fs("EXT4", Id(991).ToString("D")) },
            "duplicate-filesystem" => root with { FileSystem = state.Inventory.Partitions.Single(p => p.PartitionGuid == state.Ownership.Esp.LinuxEsp.Volume.PartitionGuid).FileSystem },
            _ => root,
        };
        parts = change switch
        {
            "missing" => parts.Remove(root), "duplicate-partition" => parts.Add(root),
            "extra" => parts.Add(root with { DevicePath = "/dev/extra", PartitionGuid = Id(998) }),
            _ => parts.Replace(root, replacement),
        };
        Assert.NotEqual(ObservationAvailability.Available, Acquire(state with { Inventory = state.Inventory with { Partitions = parts } }).Availability);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("alias")]
    [InlineData("extra")]
    [InlineData("zero")]
    public void CompleteUnaliasedBlockStatRequired(string change)
    {
        var state = TargetRootFixture.Create(); var numbers = state.Mounts.DeviceNumbers;
        numbers = change switch
        {
            "missing" => numbers.RemoveAt(0), "alias" => numbers.SetItem(1, numbers[1] with { Minor = numbers[0].Minor }),
            "extra" => numbers.Add(new("/dev/unplanned", 8, 255)), _ => numbers.SetItem(0, numbers[0] with { Major = 0 }),
        };
        Assert.Equal(ObservationAvailability.Ambiguous, Acquire(state with { Mounts = state.Mounts with { DeviceNumbers = numbers } }).Availability);
    }

    [Fact]
    public void RenumberingDoesNotChangeCanonicalIdentityButRequiresSessionRestart()
    {
        var state = TargetRootFixture.Create(); var old = Acquire(state).Value;
        var numbers = state.Mounts.DeviceNumbers.Select(n => n with { Major = 8 }).ToImmutableArray();
        var next = Acquire(state with { Mounts = state.Mounts with { DeviceNumbers = numbers } }).Value;
        Assert.Equal(old.Bindings.Select(b => b.CanonicalSha256), next.Bindings.Select(b => b.CanonicalSha256));
        Assert.Equal("BlockLocatorChangedSessionRestartRequired", InstallerBlockLeases.Revalidate(old, state.Ownership,
            state.Root, A(state.Inventory), A(numbers)).Code);
    }

    [Fact]
    public void PreservedFilesystemChangeInvalidatesActiveLease()
    {
        var state = TargetRootFixture.Create(); var leases = Acquire(state).Value;
        var preserved = state.Inventory.Partitions.First(p => state.Ownership.Layout.StorageOwnership!.PreservedPartitions.Any(i => i.PartitionGuid == p.PartitionGuid));
        var inventory = state.Inventory with { Partitions = state.Inventory.Partitions.Replace(preserved, preserved with { FileSystem = Fs("FAT32", "FFFF-FFFF") }) };
        Assert.NotEqual(ObservationAvailability.Available, InstallerBlockLeases.Revalidate(leases, state.Ownership, state.Root, A(inventory), A(state.Mounts.DeviceNumbers)).Availability);
    }

    [Theory]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Absent)]
    [InlineData(ObservationAvailability.Ambiguous)]
    public void ObservationFailureStateIsPreserved(ObservationAvailability availability)
    {
        var state = TargetRootFixture.Create();
        var result = InstallerBlockLeases.Acquire(state.Ownership, state.Root, state.Root.GenerationId, Id(700), DateTimeOffset.UtcNow,
            Observations.Failure<InstallerRuntimeInventoryV1>(availability, "fixture"), A(state.Mounts.DeviceNumbers));
        Assert.Equal(availability, result.Availability);
    }

    [Fact]
    public void WrongGenerationCannotAcquire()
    {
        var state = TargetRootFixture.Create();
        Assert.NotEqual(ObservationAvailability.Available, InstallerBlockLeases.Acquire(state.Ownership, state.Root, Id(999),
            Id(700), DateTimeOffset.UtcNow, A(state.Inventory), A(state.Mounts.DeviceNumbers)).Availability);
    }
}
