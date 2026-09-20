using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class SharedObservationReaderTests
{
    [Theory]
    [InlineData(null, BcdListingStatus.Unavailable)]
    [InlineData(1, BcdListingStatus.CommandFailed)]
    [InlineData(0, BcdListingStatus.Unparsed)]
    public void RawBcdListingNeverClaimsACompleteParsedSnapshot(int? exitCode, BcdListingStatus expected) =>
        Assert.Equal(expected, new BcdListingObservation("unrecognized listing", "", exitCode).Status);

    [Fact]
    public void PreflightUsesSharedRowsButKeepsCommunityProjection()
    {
        var reader = new StorageFake();
        var checker = new WindowsPreflightChecker(NullLogger<WindowsPreflightChecker>.Instance, reader, new BitLockerFake());
        var disk = Assert.Single(checker.QueryDisks());
        Assert.Equal(@"\\.\PHYSICALDRIVE2", disk.DeviceId);
        Assert.Equal(300, disk.FreeBytes);
        Assert.Equal(new[] { 2, 3, 4, 1 }, disk.Partitions.Select(p => p.Index));
        Assert.Equal(-1, disk.Partitions[3].OffsetBytes);
        Assert.Null(disk.Partitions[3].GptType);
        Assert.Equal(100, disk.Partitions[3].ShrinkableBytes);
        Assert.Equal(100, disk.Partitions[0].ShrinkableBytes);
        Assert.Equal("System", disk.Partitions[0].Label);
        Assert.True(disk.Partitions[0].IsBoot);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShrinkMissingFailureAndNativeRejectionStillProjectZero(int failure)
    {
        var reader = new StorageFake { Failure = failure };
        var checker = new WindowsPreflightChecker(NullLogger<WindowsPreflightChecker>.Instance, reader, new BitLockerFake());
        Assert.All(Assert.Single(checker.QueryDisks()).Partitions, p => Assert.Equal(0, p.ShrinkableBytes));
    }

    [Fact]
    public void PartialDiskEnumerationRetainsCompletedRows()
    {
        var reader = new StorageFake { DiskError = new InvalidOperationException("provider failed after first disk") };
        var checker = new WindowsPreflightChecker(NullLogger<WindowsPreflightChecker>.Instance, reader, new BitLockerFake());
        Assert.Single(checker.QueryDisks());
        Assert.NotNull(reader.ReadDisks().Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BitLockerStillQueriesCAndProjectsUnknownForMissingOrFailedObservation(bool failed)
    {
        var bitLocker = new BitLockerFake { Failed = failed };
        var checker = new WindowsPreflightChecker(NullLogger<WindowsPreflightChecker>.Instance, new StorageFake(), bitLocker);
        Assert.Equal(BitLockerState.Unknown, checker.QueryBitLockerState());
        Assert.Equal('C', bitLocker.QueriedLetter);
    }

    [Fact]
    public void ResizeUsesSameLetterAndDeltaPolicyWithoutFilesystemOrOsVolumeFiltering()
    {
        var reader = new StorageFake();
        var service = new PartitionResizeService(NullLogger<PartitionResizeService>.Instance, reader);
        var (selected, delta) = service.FindNtfsPartition(2);
        // First largest delta wins, despite this partition not being the OS partition.
        Assert.Equal(1, selected!["PartitionNumber"]);
        Assert.Equal(100, delta);
        Assert.Equal(0, reader.VolumeReads);
        Assert.Equal(new[] { 1, 2, 3 }, reader.SizeReads);
    }

    [Fact]
    public void ResizeSkipsUnletteredAndFailedSizeObservations()
    {
        var reader = new StorageFake { Failure = 1 };
        var service = new PartitionResizeService(NullLogger<PartitionResizeService>.Instance, reader);
        Assert.Null(service.FindNtfsPartition(2).row);
        Assert.DoesNotContain(4, reader.SizeReads);
    }

    [Fact]
    public void FirmwareFailureKeepsLegacyEmptyEnumerationButRetainsRawNativeError()
    {
        var reader = new FirmwareFake();
        Assert.Empty(EfiBootEntries.EnumerateObserved(reader));
        Assert.Equal(1314, reader.ReadBootOrder().NativeError);
        Assert.Equal(256, reader.EntryReads.Count);
        Assert.Empty(EfiBootEntries.DecodeBootOrder(null));
        Assert.Equal(new ushort[] { 2 }, EfiBootEntries.DecodeBootOrder([2, 0, 255]));
    }

    [Fact]
    public void FirmwareEnumerationStillIncludesHighIndicesFromBootOrder()
    {
        var reader = new FirmwareFake { Order = [0, 2] };
        Assert.Empty(EfiBootEntries.EnumerateObserved(reader));
        Assert.Contains((ushort)0x0200, reader.EntryReads);
    }

    private static WindowsStorageRow Row(params (string Key, object? Value)[] values) =>
        new(values.ToImmutableDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));

    private sealed class StorageFake : IWindowsStorageReader
    {
        public int Failure { get; init; }
        public Exception? DiskError { get; init; }
        public int VolumeReads { get; private set; }
        public List<int> SizeReads { get; } = [];
        public WindowsStorageBatch ReadDisks() => new([Row(("Number", 2U), ("FriendlyName", "Disk"), ("Size", 1000L),
            ("AllocatedSize", 700L), ("PartitionStyle", 2))], DiskError);
        public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null, bool efiOnly = false)
        {
            var rows = Enumerable.Range(1, 4).Select(n => Row(("PartitionNumber", n), ("Size", 200L),
                ("Offset", n == 1 ? null : 100L * n), ("GptType", n == 1 ? null : "{type}"),
                ("IsBoot", n == 2), ("IsSystem", false), ("DriveLetter", n == 4 ? '\0' : (char)('C' + n - 1))));
            // The fourth row exists only to characterize the resizer's unlettered exclusion.
            if (partitionNumber.HasValue) rows = rows.Where(r => (int)r["PartitionNumber"]! == partitionNumber.Value);
            return new(rows.ToArray());
        }
        public WindowsStorageBatch ReadVolumes(char driveLetter)
        {
            VolumeReads++;
            return new([Row(("FileSystem", "NTFS"), ("Label", "System"), ("DeviceID", "volume-guid"))]);
        }
        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition, bool explicitParameters = false)
        {
            SizeReads.Add((int)partition["PartitionNumber"]!);
            if (Failure == 1) return new(null, new InvalidOperationException("unavailable"));
            if (Failure == 2) return new(null);
            return new(Row(("ReturnValue", Failure == 3 ? 5U : 0U), ("SizeMin", 100L), ("SizeMax", 200L)));
        }
    }

    private sealed class BitLockerFake : IWindowsBitLockerReader
    {
        public bool Failed { get; init; }
        public char QueriedLetter { get; private set; }
        public WindowsStorageBatch ReadByDriveLetter(char driveLetter)
        {
            QueriedLetter = driveLetter;
            return new([], Failed ? new InvalidOperationException("denied") : null);
        }
    }

    private sealed class FirmwareFake : IWindowsFirmwareReader
    {
        public ImmutableArray<byte>? Order { get; init; }
        public List<ushort> EntryReads { get; } = [];
        public FirmwareVariableObservation ReadBootOrder(int bufferBytes = 4096) => new(Order, Order is null ? 1314 : 0);
        public FirmwareVariableObservation ReadBootEntry(ushort index, int bufferBytes = 4096)
        {
            EntryReads.Add(index);
            return new(null, 1314);
        }
    }
}
