using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class SharedObservationReaderTests
{
    private static readonly Guid BoundDiskGuid = Guid.Parse("d49831ee-a32a-4f84-9589-47862f4a7ce7");

    [Fact]
    public void PreflightExposesGptTargetOnlyFromCompleteUniqueGptIdentity()
    {
        DiskInfo Disk(StorageFake reader) => Assert.Single(new WindowsPreflightChecker(
            NullLogger<WindowsPreflightChecker>.Instance, reader, new BitLockerFake()).QueryDisks());

        Assert.Equal(BoundDiskGuid, Disk(new StorageFake { ExactIdentity = true }).GptDiskGuid);
        Assert.Null(Disk(new StorageFake()).GptDiskGuid);
        Assert.Null(Disk(new StorageFake { ExactIdentity = true,
            DiskError = new InvalidOperationException("partial enumeration") }).GptDiskGuid);
    }

    [Fact]
    public void PreflightKeepsVerifiedGptGuidForVirtualMachineDisks()
    {
        var disk = Assert.Single(new WindowsPreflightChecker(
            NullLogger<WindowsPreflightChecker>.Instance,
            new StorageFake { ExactIdentity = true, BusType = 14 }, new BitLockerFake()).QueryDisks());

        Assert.Equal(BoundDiskGuid, disk.GptDiskGuid);
    }

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
    public void ResizeSelectsOnlyTheObservedBootNtfsPartition()
    {
        var reader = new StorageFake();
        var service = new PartitionResizeService(NullLogger<PartitionResizeService>.Instance, reader);
        var (selected, delta) = service.FindNtfsPartition(2);
        Assert.Equal(2, selected!["PartitionNumber"]);
        Assert.Equal(100, delta);
        Assert.Equal(1, reader.VolumeReads);
        Assert.Equal(new[] { 2 }, reader.SizeReads);
    }

    [Fact]
    public void ResizeRejectsNonNtfsOrAmbiguousBootVolumesBeforeSizeQuery()
    {
        foreach (var reader in new[]
                 {
                     new StorageFake { VolumeFileSystem = "ReFS" },
                     new StorageFake { ExtraBootPartition = true },
                 })
        {
            var service = new PartitionResizeService(NullLogger<PartitionResizeService>.Instance, reader);
            Assert.Null(service.FindNtfsPartition(2).row);
            Assert.Empty(reader.SizeReads);
        }
    }

    [Fact]
    public async Task ResizeStopsBeforeNativeWmiMutationWhenExactIdentityIsUnavailable()
    {
        var service = new PartitionResizeService(NullLogger<PartitionResizeService>.Instance,
            new StorageFake());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ShrinkAsync(2, 50));
        Assert.Contains("Exact Windows storage identity is unavailable", failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeReadbackRequiresTheSelectedBootPartitionAndExactRequestedSize()
    {
        const string partitionGuid = "{c7d1a040-7802-4de4-828f-47fa6c65c46f}";
        var selected = Row(("DiskNumber", 2), ("PartitionNumber", 3),
            ("Offset", 1048576L), ("Size", 200L), ("GptType", "basic"),
            ("Guid", partitionGuid), ("IsBoot", true), ("DriveLetter", "C"));
        var resized = Row(("DiskNumber", 2), ("PartitionNumber", 3),
            ("Offset", 1048576L), ("Size", 150L), ("GptType", "basic"),
            ("Guid", partitionGuid), ("IsBoot", true), ("DriveLetter", "C"));
        Assert.True(PartitionResizeService.SameResizedPartition(selected, resized, 150));
        Assert.False(PartitionResizeService.SameResizedPartition(selected, resized, 149));
        Assert.False(PartitionResizeService.SameResizedPartition(selected,
            Row(("DiskNumber", 2), ("PartitionNumber", 4), ("Offset", 1048576L),
                ("Size", 150L), ("GptType", "basic"), ("IsBoot", true),
                ("Guid", partitionGuid), ("DriveLetter", "C")), 150));
        Assert.False(PartitionResizeService.SameResizedPartition(selected,
            Row(("DiskNumber", 2), ("PartitionNumber", 3), ("Offset", 1048576L),
                ("Size", 150L), ("GptType", "basic"), ("IsBoot", true),
                ("Guid", "{e1379374-c1d3-43e5-b993-8799fe377245}"), ("DriveLetter", "C")), 150));
        Assert.False(PartitionResizeService.SameResizedPartition(selected,
            Row(("DiskNumber", 2), ("PartitionNumber", 3), ("Offset", 1048576L),
                ("Size", 150L), ("GptType", "basic"), ("IsBoot", true),
                ("DriveLetter", "C")), 150));
    }

    [Fact]
    public void ResizeSelectionRejectsRecycledPartitionIdentityBeforeMutation()
    {
        var selected = Row(("DiskNumber", 2), ("PartitionNumber", 3),
            ("Offset", 1048576L), ("Size", 200L), ("GptType", "basic"),
            ("Guid", "{c7d1a040-7802-4de4-828f-47fa6c65c46f}"),
            ("IsBoot", true), ("DriveLetter", "C"));
        var changed = Row(("DiskNumber", 2), ("PartitionNumber", 3),
            ("Offset", 1048576L), ("Size", 200L), ("GptType", "basic"),
            ("Guid", "{e1379374-c1d3-43e5-b993-8799fe377245}"),
            ("IsBoot", true), ("DriveLetter", "C"));
        Assert.False(PartitionResizeService.SameObservedPartition(selected, changed));
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
        public bool ExactIdentity { get; init; }
        public uint BusType { get; init; } = 17;
        public Exception? DiskError { get; init; }
        public string VolumeFileSystem { get; init; } = "NTFS";
        public bool ExtraBootPartition { get; init; }
        public int VolumeReads { get; private set; }
        public List<int> SizeReads { get; } = [];
        public WindowsStorageBatch ReadDisks() => new([ExactIdentity
            ? Row(("Number", 2U), ("FriendlyName", "Disk"), ("Size", 1000L),
                ("AllocatedSize", 700L), ("PartitionStyle", 2),
                ("UniqueId", "provider-disk-2"), ("UniqueIdFormat", 3U),
                ("BusType", BusType), ("Guid", BoundDiskGuid.ToString("D")))
            : Row(("Number", 2U), ("FriendlyName", "Disk"), ("Size", 1000L),
                ("AllocatedSize", 700L), ("PartitionStyle", 2))], DiskError);
        public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null, bool efiOnly = false)
        {
            var rows = Enumerable.Range(1, 4).Select(n => Row(("DiskNumber", 2),
                ("PartitionNumber", n), ("Guid", $"00000000-0000-0000-0000-{n:000000000000}"),
                ("Size", 200L),
                ("Offset", n == 1 ? null : 100L * n), ("GptType", n == 1 ? null : "{type}"),
                ("IsBoot", n == 2 || ExtraBootPartition && n == 3), ("IsSystem", false),
                ("DriveLetter", n == 4 ? '\0' : (char)('C' + n - 1))));
            // The fourth row exists only to characterize the resizer's unlettered exclusion.
            if (partitionNumber.HasValue) rows = rows.Where(r => (int)r["PartitionNumber"]! == partitionNumber.Value);
            return new(rows.ToArray());
        }
        public WindowsStorageBatch ReadVolumes(char driveLetter)
        {
            VolumeReads++;
            return new([Row(("FileSystem", VolumeFileSystem), ("Label", "System"), ("DeviceID", "volume-guid"))]);
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
