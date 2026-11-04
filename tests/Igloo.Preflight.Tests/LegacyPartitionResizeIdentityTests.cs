using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityRecovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class LegacyPartitionResizeIdentityTests
{
    [Theory]
    [InlineData(null, 0U, 14U)]
    [InlineData("", 0U, 15U)]
    [InlineData("virtual-disk", 0U, 11U)]
    [InlineData("virtual-disk", 3U, 14U)]
    public async Task UniqueGptBootVolumeRemainsEligibleWithReducedProviderIdentity(
        string? uniqueId, uint format, uint bus)
    {
        var storage = new StorageFake(uniqueId, format, bus);
        var bitLocker = new BitLockerFake();
        var service = Service(storage, bitLocker);

        Assert.Equal(10L << 30, await service.GetShrinkableSpaceAsync(2));
        Assert.Equal(StorageFake.VolumeGuid, bitLocker.LastVolume);
        Assert.Equal(0, storage.NativeCalls);
    }

    [Fact]
    public async Task LegacyShrinkRevalidatesVirtualDiskAndReadsBackRequestedGeometry()
    {
        var storage = new StorageFake(null, 0, 14);
        var bitLocker = new BitLockerFake();
        await Service(storage, bitLocker).ShrinkAsync(2, 5L << 30);

        Assert.Equal(25UL << 30, storage.CurrentSize);
        Assert.Equal(1, storage.NativeCalls);
        Assert.Equal(2, bitLocker.ReadCount);
    }

    [Theory]
    [InlineData("duplicate-disk-guid")]
    [InlineData("missing-disk-guid")]
    [InlineData("non-gpt")]
    [InlineData("changed-partition")]
    [InlineData("duplicate-partition-guid")]
    [InlineData("duplicate-volume-guid")]
    [InlineData("dirty-volume")]
    [InlineData("unknown-dirty-state")]
    [InlineData("encrypted")]
    [InlineData("locked")]
    public async Task UnprovenOrUnsafeTargetStopsBeforeResize(string change)
    {
        var storage = new StorageFake(null, 0, 14);
        var bitLocker = new BitLockerFake
        {
            Conversion = change == "encrypted" ? 1U : 0U,
            Lock = change == "locked" ? 1U : 0U,
        };
        storage.ChangeSnapshot = snapshot => change switch
        {
            "duplicate-disk-guid" => snapshot with
            {
                Disks = snapshot.Disks.Add(snapshot.Disks[0] with { Number = A(3U) }),
            },
            "missing-disk-guid" => snapshot with
            {
                Disks = [snapshot.Disks[0] with { GptGuid = Missing<Guid>() }],
            },
            "non-gpt" => snapshot with
            {
                Disks = [snapshot.Disks[0] with { PartitionStyle = A(1U) }],
            },
            "changed-partition" => snapshot with
            {
                Partitions = [snapshot.Partitions[0] with { PartitionGuid = A(Guid.NewGuid()) }],
            },
            "duplicate-partition-guid" => snapshot with
            {
                Partitions = snapshot.Partitions.Add(snapshot.Partitions[0] with { Number = A(3U) }),
            },
            "duplicate-volume-guid" => snapshot with
            {
                Volumes = snapshot.Volumes.Add(snapshot.Volumes[0] with { PartitionGuid = A(Guid.NewGuid()) }),
            },
            "dirty-volume" => snapshot with
            {
                Volumes = [snapshot.Volumes[0] with { DirtyBitSet = A(true) }],
            },
            "unknown-dirty-state" => snapshot with
            {
                Volumes = [snapshot.Volumes[0] with { DirtyBitSet = Missing<bool>() }],
            },
            _ => snapshot,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(storage, bitLocker).ShrinkAsync(2, 5L << 30));
        Assert.Equal(0, storage.NativeCalls);
    }

    [Fact]
    public async Task ChangedDiskAfterResizeCannotReportSuccess()
    {
        var storage = new StorageFake(null, 0, 14);
        storage.ChangeSnapshot = snapshot => storage.NativeCalls == 0 ? snapshot :
            snapshot with { Disks = [snapshot.Disks[0] with { GptGuid = A(Guid.NewGuid()) }] };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(storage, new BitLockerFake()).ShrinkAsync(2, 5L << 30));
        Assert.Contains("identity changed after resizing", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, storage.NativeCalls);
    }

    [Fact]
    public async Task CancellationBeforeResizeMakesNoNativeCall()
    {
        var storage = new StorageFake(null, 0, 14);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(storage, new BitLockerFake()).ShrinkAsync(2, 5L << 30, ct: cancellation.Token));
        Assert.Equal(0, storage.NativeCalls);
    }

    [Fact]
    public void PreparedSeedUsesGptBindingWhileRecoveryStillRejectsReducedIdentity()
    {
        var storage = new StorageFake(null, 0, 14);
        var observed = WindowsBootRegistrationPlanning.ObservePreparedTarget(storage, 2, 2);
        Assert.Equal(ObservationAvailability.Available, observed.Availability);
        Assert.Equal(StorageFake.VolumeGuid, observed.Value.VolumeGuid);
        Assert.False(CanonicalRecoveryIdentity.IsValid(observed.Value));
        Assert.NotEqual(ObservationAvailability.Available,
            CanonicalRecoveryIdentity.Bind(storage.ReadIdentitySnapshot(), StorageFake.VolumeGuid).Availability);
    }

    private static PartitionResizeService Service(StorageFake storage, BitLockerFake bitLocker) =>
        new(NullLogger<PartitionResizeService>.Instance, storage, bitLocker, storage.Resize);

    private static Observation<T> A<T>(T value) => Observations.Available(value);
    private static Observation<T> Missing<T>() =>
        Observations.Failure<T>(ObservationAvailability.Unavailable, "FixtureUnavailable");

    private sealed class StorageFake(string? uniqueId, uint format, uint bus) : IWindowsStorageReader
    {
        internal static readonly Guid DiskGuid = Guid.Parse("11223344-5566-7788-9900-aabbccddeeff");
        internal static readonly Guid PartGuid = Guid.Parse("11223344-5566-7788-9900-aabbccddee01");
        internal static readonly Guid VolumeGuid = Guid.Parse("11223344-5566-7788-9900-aabbccddee02");
        private const string BasicData = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7";
        public ulong CurrentSize { get; private set; } = 30UL << 30;
        public int NativeCalls { get; private set; }
        public Func<WindowsStorageSnapshot, WindowsStorageSnapshot> ChangeSnapshot { get; set; } = value => value;

        public uint Resize(WindowsStorageRow selected, ulong size, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(PartGuid.ToString("D"), selected["Guid"]);
            Assert.Equal(CurrentSize, selected["Size"]);
            NativeCalls++;
            CurrentSize = size;
            return 0;
        }

        public WindowsStorageBatch ReadDisks() => new([Row(("Number", 2U), ("UniqueId", uniqueId),
            ("UniqueIdFormat", format), ("BusType", bus), ("FriendlyName", "Virtual disk"),
            ("Guid", DiskGuid.ToString("D")), ("PartitionStyle", 2U), ("Size", 40UL << 30),
            ("LogicalSectorSize", 512U), ("PhysicalSectorSize", 4096U))]);

        public WindowsStorageBatch ReadPartitions(int? diskNumber = null,
            int? partitionNumber = null, bool efiOnly = false) =>
            new([Row(("DiskNumber", 2U), ("PartitionNumber", 2U), ("Guid", PartGuid.ToString("D")),
                ("GptType", BasicData), ("Offset", 1UL << 20), ("Size", CurrentSize),
                ("IsBoot", true), ("IsSystem", false), ("IsActive", false), ("DriveLetter", 'C'),
                ("AccessPaths", new[] { $@"\\?\Volume{{{VolumeGuid:D}}}\" }))]);

        public WindowsStorageBatch ReadVolumes(char driveLetter) =>
            new([Row(("FileSystem", "NTFS"), ("DriveLetter", 'C'), ("Label", "Windows"))]);

        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition,
            bool explicitParameters = false) =>
            new(Row(("ReturnValue", 0U), ("SizeMin", 20UL << 30), ("SizeMax", CurrentSize)));

        public Observation<WindowsStorageSnapshot> ReadIdentitySnapshot() =>
            A(ChangeSnapshot(new(
                ReadDisks().Rows.Select(StorageObservationProjection.Disk).ToImmutableArray(),
                ReadPartitions().Rows.Select(StorageObservationProjection.Partition).ToImmutableArray(),
                [new(A(VolumeGuid), A(PartGuid), A("NTFS"), A("Windows"), A('C'), A("OK"), A(false))])));

        private static WindowsStorageRow Row(params (string Key, object? Value)[] values) =>
            new(values.ToImmutableDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
                "fixture://boot-partition");
    }

    private sealed class BitLockerFake : IWindowsBitLockerReader
    {
        public uint Conversion { get; init; }
        public uint Lock { get; init; }
        public Guid? LastVolume { get; private set; }
        public int ReadCount { get; private set; }

        public Observation<BitLockerVolumeObservation> ReadExactVolume(VolumeObservation volume)
        {
            LastVolume = volume.VolumeGuid.Value;
            ReadCount++;
            return A(new BitLockerVolumeObservation(volume.VolumeGuid.Value, A('C'),
                A(Conversion), A(0U), A(Lock), A(0U)));
        }

        public WindowsStorageBatch ReadByDriveLetter(char driveLetter) =>
            throw new InvalidOperationException("Resize must bind BitLocker to the volume GUID.");
    }
}
