using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class CanonicalRecoveryIdentityTests
{
    private static readonly Guid VolumeId = new("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid PartitionId = new("bbbbbbbb-1111-2222-3333-444444444444");

    [Fact]
    public void FreshOrdinalAndLetterChangesDoNotAlterCanonicalBinding()
    {
        var first = CanonicalRecoveryIdentity.Bind(A(Inventory(0, 3, 'C')), VolumeId);
        var second = CanonicalRecoveryIdentity.Bind(A(Inventory(7, 9, 'Z')), VolumeId);
        Assert.Equal(ObservationAvailability.Available, first.Availability);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void DuplicateVolumeIdentityIsAmbiguous()
    {
        var inventory = Inventory(0, 3, 'C');
        Assert.Equal(ObservationAvailability.Ambiguous, CanonicalRecoveryIdentity.Bind(A(inventory with
        { Volumes = inventory.Volumes.Add(inventory.Volumes[0]) }), VolumeId).Availability);
    }

    [Fact]
    public void MissingVolumeIsNotObservationFailure()
    {
        Assert.Equal(ObservationAvailability.Absent, CanonicalRecoveryIdentity.Bind(A(Inventory(0, 3, 'C')), Guid.NewGuid()).Availability);
        Assert.Equal(ObservationAvailability.AccessDenied, CanonicalRecoveryIdentity.Bind(
            Observations.Failure<WindowsStorageSnapshot>(ObservationAvailability.AccessDenied, "Denied"), VolumeId).Availability);
    }

    [Fact]
    public void WrongOrDuplicatePartitionOwnershipCannotBind()
    {
        var inventory = Inventory(0, 3, 'C');
        Assert.Equal(ObservationAvailability.Unavailable, CanonicalRecoveryIdentity.Bind(A(inventory with { Partitions = [] }), VolumeId).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, CanonicalRecoveryIdentity.Bind(A(inventory with
        { Partitions = inventory.Partitions.Add(inventory.Partitions[0]) }), VolumeId).Availability);
    }

    [Theory]
    [InlineData(0u, 17u, 2u)]
    [InlineData(8u, 15u, 2u)]
    [InlineData(8u, 17u, 1u)]
    public void ReducedVirtualAndNonGptDiskIdentitiesAreUnsupported(uint format, uint bus, uint style)
    {
        var inventory = Inventory(0, 3, 'C');
        var disk = inventory.Disks[0] with { UniqueIdFormat = A(format), BusType = A(bus), PartitionStyle = A(style) };
        Assert.Equal(ObservationAvailability.Unsupported, CanonicalRecoveryIdentity.Bind(A(inventory with { Disks = [disk] }), VolumeId).Availability);
    }

    [Fact]
    public void DuplicatePhysicalIdentityCannotBecomeCanonical()
    {
        var inventory = Inventory(0, 3, 'C');
        Assert.Equal(ObservationAvailability.Ambiguous, CanonicalRecoveryIdentity.Bind(A(inventory with
        { Disks = inventory.Disks.Add(inventory.Disks[0] with { Number = A(8u) }) }), VolumeId).Availability);
    }

    private static WindowsStorageSnapshot Inventory(uint disk, uint partition, char letter) => new(
        [new(A(disk), A("eui.example"), A(8u), A("serial-metadata"), A(17u), A("disk-name-metadata"), A(1073741824UL), A(2u),
            A(new Guid("cccccccc-1111-2222-3333-444444444444")), A(512u), A(4096u))],
        [new(A(disk), A(partition), A(PartitionId), A(new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")), A(1048576UL), A(104857600UL),
            A(false), A(true), A(false), A(letter), A<ImmutableArray<string>>([$"\\\\?\\Volume{{{VolumeId:D}}}\\"]))],
        [new(A(VolumeId), A(PartitionId), A("NTFS"), A("metadata"), A(letter), A("OK"), A(false))]);
    private static Observation<T> A<T>(T value) => Observations.Available(value);
}
