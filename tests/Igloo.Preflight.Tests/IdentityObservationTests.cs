using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class IdentityObservationTests
{
    [Fact]
    public void WmiStatusIsPreservedSeparatelyFromExceptionHResult()
    {
        Assert.Equal(ObservationAvailability.AccessDenied, WindowsStorageReader.ClassifyManagementStatus(System.Management.ManagementStatus.AccessDenied));
        Assert.Equal(ObservationAvailability.Unsupported, WindowsStorageReader.ClassifyManagementStatus(System.Management.ManagementStatus.InvalidClass));
        var raw = new WindowsStorageBatch([], new InvalidOperationException("generic wrapper"))
        { ProviderAvailability = ObservationAvailability.AccessDenied };
        Assert.Equal(ObservationAvailability.AccessDenied, raw.Availability);
        Assert.Throws<InvalidOperationException>(() => raw.RowsOrThrow().ToArray());
    }

    [Fact]
    public void BitLockerProviderCorrelationIgnoresDriveLetterAndRejectsPartialInventory()
    {
        var id = Guid.NewGuid();
        var correct = Row(("DeviceID", $@"\\?\Volume{{{id}}}\"), ("DriveLetter", "D:"));
        var reused = Row(("DeviceID", $@"\\?\Volume{{{Guid.NewGuid()}}}\"), ("DriveLetter", "C:"));
        Assert.Same(correct, WindowsBitLockerReader.CorrelateVolume(new([reused, correct]), id).Value);
        Assert.Equal(ObservationAvailability.Unavailable, WindowsBitLockerReader.CorrelateVolume(new([reused]), id).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, WindowsBitLockerReader.CorrelateVolume(new([correct, correct]), id).Availability);
        Assert.Equal(ObservationAvailability.AccessDenied,
            WindowsBitLockerReader.CorrelateVolume(new([correct], new UnauthorizedAccessException()), id).Availability);
    }

    [Fact]
    public void CanonicalProjectionCapturesProviderIdentityWithoutInventingMissingFacts()
    {
        var id = Guid.NewGuid();
        var disk = StorageObservationProjection.Disk(Row(("Number", 3U), ("UniqueId", "eui-123"),
            ("UniqueIdFormat", (ushort)2), ("SerialNumber", "serial"), ("BusType", (ushort)17),
            ("FriendlyName", "name"), ("Size", 1000UL), ("PartitionStyle", (ushort)2),
            ("Guid", id.ToString()), ("LogicalSectorSize", 512U), ("PhysicalSectorSize", 4096U)));
        Assert.Equal("eui-123", disk.UniqueId.Value);
        Assert.Equal(id, disk.GptGuid.Value);
        Assert.Equal(3U, disk.Number.Value);
        Assert.Equal(512U, disk.LogicalSectorSize.Value);
        Assert.Equal(4096U, disk.PhysicalSectorSize.Value);
        Assert.Equal(StorageIdentityStrength.ProviderUniqueIdentity, disk.IdentityStrength);
        Assert.Equal(StorageIdentityStrength.Unavailable, StorageObservationProjection.Disk(Row(("Number", 3))).IdentityStrength);
        var partition = StorageObservationProjection.Partition(Row(("Guid", id.ToString()), ("Offset", 42UL),
            ("Size", 100UL), ("IsBoot", true), ("IsSystem", false), ("IsActive", null)));
        Assert.Equal(id, partition.PartitionGuid.Value);
        Assert.Equal(42UL, partition.Offset.Value);
        Assert.True(partition.IsBoot.Value);
        Assert.False(partition.IsSystem.Value);
        Assert.Equal(ObservationAvailability.Unavailable, partition.IsActive.Availability);
        Assert.Equal(ObservationAvailability.Unsupported, partition.GptType.Availability);
    }

    [Fact]
    public void UnavailableDoesNotExposeZeroOrFalse()
    {
        Assert.Equal(0, Observations.Available(0).Value);
        Assert.False(Observations.Available(false).Value);
        Assert.Throws<InvalidOperationException>(() => Observations.Failure<int>(ObservationAvailability.Unavailable, "failed").Value);
        Assert.Throws<InvalidOperationException>(() => Observations.Failure<bool>(ObservationAvailability.AccessDenied, "denied").Value);
    }

    [Fact]
    public void MissingPropertyAndNullPropertyRemainDistinct()
    {
        var row = Row(("Size", null), ("Offset", 0UL));
        Assert.Equal(ObservationAvailability.Unavailable, row.Fact("Size", v => (ulong)v).Availability);
        Assert.Equal(ObservationAvailability.Unsupported, row.Fact("Guid", v => (string)v).Availability);
        Assert.Equal(0UL, row.Fact("Offset", v => (ulong)v).Value);
    }

    [Theory]
    [InlineData(0, ObservationAvailability.Available)]
    [InlineData(1, ObservationAvailability.Unsupported)]
    [InlineData(40001, ObservationAvailability.AccessDenied)]
    [InlineData(5, ObservationAvailability.Unavailable)]
    public void SupportedSizeKeepsRawFailureAndLegacyPayload(uint code, ObservationAvailability expected)
    {
        var row = Row(("ReturnValue", code), ("SizeMin", 0UL), ("SizeMax", 100UL));
        var raw = new WindowsObservation<WindowsStorageRow>(row);
        var result = StorageObservationProjection.SupportedSize(raw);
        Assert.Equal(expected, result.Availability);
        Assert.Same(row, raw.ValueOrThrow());
        if (code == 0) Assert.Equal(new SupportedSizeObservation(0, 100), result.Value);
    }

    [Fact]
    public void VolumeOwnershipUsesGuidAccessPathAndNeverDriveLetter()
    {
        var id = Guid.NewGuid();
        var partitionId = Guid.NewGuid();
        var partition = StorageObservationProjection.Partition(Row(("Guid", partitionId.ToString()),
            ("AccessPaths", new[] { $@"\\?\Volume{{{id}}}\" })));
        var row = Row(("DeviceID", $@"\\?\Volume{{{id}}}\"), ("DriveLetter", "D:"));
        Assert.Equal(partitionId, StorageObservationProjection.Volume(row, [partition]).PartitionGuid.Value);
        Assert.Equal(ObservationAvailability.Ambiguous, StorageObservationProjection.Volume(row, [partition, partition]).PartitionGuid.Availability);
        Assert.Equal(ObservationAvailability.Unavailable, StorageObservationProjection.Volume(row, []).PartitionGuid.Availability);
        Assert.False(WindowsVolumeIdentity.TryParse("C:", out _));
    }

    [Theory]
    [InlineData(203, ObservationAvailability.Absent)]
    [InlineData(5, ObservationAvailability.AccessDenied)]
    [InlineData(1314, ObservationAvailability.AccessDenied)]
    [InlineData(1, ObservationAvailability.Unsupported)]
    [InlineData(122, ObservationAvailability.Unavailable)]
    public void FirmwareFailureRetainsMeaning(int error, ObservationAvailability expected)
    {
        var raw = new FirmwareVariableObservation(null, error);
        Assert.Equal(expected, raw.Availability);
        Assert.Equal(expected, raw.DecodeBootNext().Availability);
    }

    [Fact]
    public void BootNextIsExactlyOneLittleEndianIndex()
    {
        Assert.Equal((ushort)0x1234, new FirmwareVariableObservation([0x34, 0x12], 0).DecodeBootNext().Value);
        Assert.Equal(ObservationAvailability.Ambiguous, new FirmwareVariableObservation([1], 0).DecodeBootNext().Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, new FirmwareVariableObservation([1, 0, 0], 0).DecodeBootNext().Availability);
    }

    [Fact]
    public void ProviderErrorsDoNotTurnIntoEmptySuccess()
    {
        Assert.Equal(ObservationAvailability.AccessDenied, new WindowsStorageBatch([], new UnauthorizedAccessException()).Availability);
        Assert.Equal(ObservationAvailability.Unsupported, new WindowsStorageBatch([], new NotSupportedException()).Availability);
        Assert.Equal(ObservationAvailability.Available, new WindowsStorageBatch([]).Availability);
    }

    private static WindowsStorageRow Row(params (string Name, object? Value)[] facts) =>
        new(facts.ToImmutableDictionary(f => f.Name, f => f.Value));
}
