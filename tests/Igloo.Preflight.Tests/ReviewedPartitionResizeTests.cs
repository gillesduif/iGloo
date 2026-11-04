using System.Buffers.Binary;
using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class ReviewedPartitionResizeTests : IDisposable
{
    private const ulong MiB = PreparationSpacePlanning.Alignment;
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "igloo-reviewed-resize-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExactReviewedSourceReturnsShrinkReceiptOnlyAfterFreshReadback()
    {
        var plan = Plan();
        var storage = new StorageFake(plan);
        var calls = 0;
        var service = Service(storage, (_, size, _) =>
        {
            calls++;
            Assert.Equal(plan.ReviewedIntent!.ResultingSourceBytes, size);
            storage.Resized = true;
            return 0;
        });

        var receipt = await service.ShrinkReviewedAsync(plan);

        Assert.Equal(1, calls);
        Assert.True(storage.InventoryReads >= 4);
        Assert.All(storage.ExplicitSizeReads, Assert.True);
        Assert.Equal(2, storage.ExplicitSizeReads.Count);
        Assert.Equal(plan.GenerationId, receipt.GenerationId);
        Assert.Equal(plan.ReviewedIntent!.ShrinkSource, receipt.Before);
        Assert.Equal(plan.ReviewedIntent.ShrinkSource with
            { SizeBytes = plan.ReviewedIntent.ResultingSourceBytes }, receipt.After);
        Assert.Equal(receipt.Before.SizeBytes, receipt.NtfsBefore!.SizeBytes);
        Assert.Equal(receipt.After.SizeBytes, receipt.NtfsAfter!.SizeBytes);
    }

    [Theory]
    [InlineData("source-guid")]
    [InlineData("source-size")]
    [InlineData("windows-esp")]
    [InlineData("dirty")]
    [InlineData("bitlocker")]
    public async Task ChangedOrUnsafeSourceStopsBeforeNativeMutation(string change)
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { Change = change };
        var calls = 0;
        var service = Service(storage, (_, _, _) => { calls++; return 0; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ShrinkReviewedAsync(plan));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ASuccessfulProviderStatusWithoutExactPostResizeGeometryReturnsNoReceipt()
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { IncorrectAfterSize = true };
        var calls = 0;
        var service = Service(storage, (_, _, _) =>
        {
            calls++;
            storage.Resized = true;
            return 0;
        });

        var failure = await Assert.ThrowsAsync<ReviewedShrinkMutationException>(() =>
            service.ShrinkReviewedAsync(plan));
        Assert.Equal(1, calls);
        Assert.Null(failure.VerifiedReceipt);
    }

    [Theory]
    [InlineData("ntfs-size")]
    [InlineData("ntfs-unavailable-after-shrink")]
    public async Task ShrinkCannotIssueReceiptWhenNtfsReadbackDiffersOrIsUnavailable(string change)
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { Change = change };
        var service = Service(storage, (_, _, _) =>
        {
            storage.Resized = true;
            return 0;
        });

        var failure = await Assert.ThrowsAsync<ReviewedShrinkMutationException>(() =>
            service.ShrinkReviewedAsync(plan));

        Assert.Null(failure.VerifiedReceipt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedProviderStatusOrCancellationAfterMutationStillObtainsExactReadback(
        bool cancelDuringNativeCall)
    {
        var plan = Plan();
        var storage = new StorageFake(plan);
        using var cancellation = new CancellationTokenSource();
        var service = Service(storage, (_, _, _) =>
        {
            storage.Resized = true;
            if (cancelDuringNativeCall)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return 5;
        });

        var failure = await Assert.ThrowsAsync<ReviewedShrinkMutationException>(() =>
            service.ShrinkReviewedAsync(plan, cancellation.Token));
        Assert.True(storage.InventoryReads >= 4);
        Assert.Equal(plan.ReviewedIntent!.ShrinkSource with
            { SizeBytes = plan.ReviewedIntent.ResultingSourceBytes },
            failure.VerifiedReceipt!.After);
    }

    [Fact]
    public async Task ExactReviewedRestoreReopensNativeGptAndOriginalVolume()
    {
        var plan = Plan();
        var shrink = Shrink(plan);
        var storage = new StorageFake(plan) { Resized = true };
        var calls = 0;
        var service = RestoreService(storage, (_, size, _) =>
        {
            calls++;
            Assert.Equal(shrink.Before.SizeBytes, size);
            storage.Resized = false;
            return 0;
        });

        var (store, reference) = Checkpoint(plan, shrink);
        var restored = await service.RestoreReviewedShrinkAsync(store, reference);

        Assert.Equal(shrink.Before, restored);
        Assert.Equal(1, calls);
        Assert.True(storage.InventoryReads >= 4);
        Assert.Equal(2, storage.NativeGptReadsBeforeRestore);
        Assert.Equal(1, storage.NativeGptReadsAfterRestore);
        Assert.Equal(2, storage.NtfsReadsBeforeRestore);
        Assert.Equal(1, storage.NtfsReadsAfterRestore);
        Assert.Equal([true, true], storage.ExplicitSizeReads);
    }

    [Fact]
    public async Task RestoreRefusesChangedDurableReferenceBeforeNativeMutation()
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { Resized = true };
        var calls = 0;
        var service = RestoreService(storage, (_, _, _) => { calls++; return 0; });

        var reference = Checkpoint(plan, Shrink(plan)).Reference;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreReviewedShrinkAsync(
                new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory)),
                reference with { Sha256 = new string('0', 64) }));

        Assert.Equal(0, calls);
        Assert.Equal(0, storage.InventoryReads);
    }

    [Fact]
    public async Task LegacyCheckpointWithoutNtfsSizesCannotRestore()
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { Resized = true };
        var calls = 0;
        var service = RestoreService(storage, (_, _, _) => { calls++; return 0; });
        var (store, reference) = Checkpoint(plan,
            Shrink(plan) with { NtfsBefore = null, NtfsAfter = null });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreReviewedShrinkAsync(store, reference));

        Assert.Equal(0, calls);
        Assert.Equal(0, storage.InventoryReads);
    }

    [Theory]
    [InlineData("source-guid")]
    [InlineData("source-size")]
    [InlineData("windows-esp")]
    [InlineData("dirty")]
    [InlineData("bitlocker")]
    [InlineData("extra-partition")]
    [InlineData("native-gpt")]
    [InlineData("restore-size-limit")]
    [InlineData("ntfs-size")]
    [InlineData("ntfs-unavailable")]
    public async Task RestoreRejectsChangedOrUnsupportedSourceBeforeMutation(string change)
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { Resized = true, Change = change };
        var calls = 0;
        var service = RestoreService(storage, (_, _, _) => { calls++; return 0; });

        var (store, reference) = Checkpoint(plan, Shrink(plan));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreReviewedShrinkAsync(store, reference));

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreProviderFailureOrCancellationRetainsVerifiedOriginalGeometry(
        bool cancelDuringNativeCall)
    {
        var plan = Plan();
        var shrink = Shrink(plan);
        var storage = new StorageFake(plan) { Resized = true };
        using var cancellation = new CancellationTokenSource();
        var service = RestoreService(storage, (_, _, _) =>
        {
            storage.Resized = false;
            if (cancelDuringNativeCall)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return 5;
        });

        var (store, reference) = Checkpoint(plan, shrink);
        var failure = await Assert.ThrowsAsync<ReviewedRestoreMutationException>(() =>
            service.RestoreReviewedShrinkAsync(store, reference, cancellation.Token));

        Assert.Equal(shrink.Before, failure.VerifiedRestoredVolume);
    }

    [Fact]
    public async Task RestoreStatusAloneCannotProveNativeGptReadback()
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { Resized = true, Change = "native-after-restore" };
        var service = RestoreService(storage, (_, _, _) =>
        {
            storage.Resized = false;
            return 0;
        });

        var (store, reference) = Checkpoint(plan, Shrink(plan));
        var failure = await Assert.ThrowsAsync<ReviewedRestoreMutationException>(() =>
            service.RestoreReviewedShrinkAsync(store, reference));

        Assert.Null(failure.VerifiedRestoredVolume);
        Assert.Equal(1, storage.NativeGptReadsAfterRestore);
    }

    [Theory]
    [InlineData("ntfs-after-restore")]
    [InlineData("ntfs-unavailable-after-restore")]
    public async Task MatchingGptButWrongOrUnavailableNtfsAfterMutationCannotProveRestore(
        string change)
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { Resized = true, Change = change };
        var service = RestoreService(storage, (_, _, _) =>
        {
            storage.Resized = false;
            return 0;
        });
        var (store, reference) = Checkpoint(plan, Shrink(plan));

        var failure = await Assert.ThrowsAsync<ReviewedRestoreMutationException>(() =>
            service.RestoreReviewedShrinkAsync(store, reference));

        Assert.Null(failure.VerifiedRestoredVolume);
        Assert.Equal(1, storage.NtfsReadsAfterRestore);
    }

    [Fact]
    public void NtfsSizeReaderRejectsIncompleteAndInconsistentSectorData()
    {
        var data = new byte[96];
        BinaryPrimitives.WriteInt64LittleEndian(data, 12345);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), 100000);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), 12500);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), 4096);

        Assert.Equal(100000UL * 512, ReviewedNtfsSizeReader.Parse(data).SizeBytes);
        Assert.Throws<InvalidDataException>(() => ReviewedNtfsSizeReader.Parse(data.AsSpan(..95)));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), 12501);
        Assert.Throws<InvalidDataException>(() => ReviewedNtfsSizeReader.Parse(data));
    }

    private static PartitionResizeService Service(StorageFake storage,
        Func<WindowsStorageRow, ulong, CancellationToken, uint> resize) =>
        new(NullLogger<PartitionResizeService>.Instance, storage, new BitLockerFake(storage),
            resize, storage.GptRange, storage.NtfsSize);

    private static PartitionResizeService RestoreService(StorageFake storage,
        Func<WindowsStorageRow, ulong, CancellationToken, uint> resize) =>
        new(NullLogger<PartitionResizeService>.Instance, storage, new BitLockerFake(storage),
            resize, storage.GptRange, storage.NtfsSize);

    private static ReviewedShrinkReceiptV1 Shrink(PreparationPlanV1 plan)
    {
        var after = plan.ReviewedIntent!.ShrinkSource with
            { SizeBytes = plan.ReviewedIntent.ResultingSourceBytes };
        return PreparedLayoutRules.VerifyShrink(plan, A(after), A(after)).Value with
        {
            NtfsBefore = new(plan.ReviewedIntent.ShrinkSource.SizeBytes, 12345, 512, 4096),
            NtfsAfter = new(after.SizeBytes, 12345, 512, 4096),
        };
    }

    private (PreparedLayoutStore Store, PreparationCheckpointReference Reference) Checkpoint(
        PreparationPlanV1 plan, ReviewedShrinkReceiptV1 shrink)
    {
        Directory.CreateDirectory(_directory);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = store.PersistAndReopen(new PreparationCheckpointV1(1,
            PreparationState.Planned, plan, [], null) { ShrinkReceipt = shrink });
        return (store, reference);
    }

    private static PreparationPlanV1 Plan()
    {
        var disk = new CanonicalDiskIdentityV1("eui.reviewed-shrink", 8, 17,
            new("ea043c21-4319-4603-8f40-b8159ef170d4"), 100000 * MiB, 512, 4096);
        var esp = new CanonicalVolumeIdentityV1(disk,
            new("7a61394b-26d7-451b-87f8-3609e72f290d"),
            new("42ab62f4-9092-42c7-80bd-816879d85d52"),
            PreparationSpacePlanning.EspType, MiB, 260 * MiB, "FAT32");
        var source = new CanonicalVolumeIdentityV1(disk,
            new("65fef750-146e-4abf-8f6f-a2e39a71d55e"),
            new("d0e49dc7-235f-4bd1-a8b5-792797dc84a4"),
            PreparationSpacePlanning.BasicDataType, 261 * MiB, 59739 * MiB, "NTFS");
        var space = PreparationSpacePlanning.Plan(new(40000 * MiB, 60000 * MiB,
            8000 * MiB, 1000 * MiB, 0, 4096, true)).Value;
        return new PreparationPlanV1(1, Guid.NewGuid(), disk, esp,
            [esp.PartitionGuid, source.PartitionGuid], space)
        {
            ReviewedIntent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new string('A', 64), new string('B', 64),
                new(5UL * 1024 * 1024 * 1024, 100 * MiB, 100 * MiB, 100 * MiB), source,
                [PreparedStorageOwnership.PartitionOf(esp), PreparedStorageOwnership.PartitionOf(source)],
                20000 * MiB, 39739 * MiB, 40000 * MiB, 60000 * MiB),
        };
    }

    private static Observation<T> A<T>(T value) => Observations.Available(value);
    private static Observation<T> Missing<T>() =>
        Observations.Failure<T>(ObservationAvailability.Absent, "Absent");

    private sealed class StorageFake(PreparationPlanV1 plan) : IWindowsStorageReader
    {
        public string? Change { get; init; }
        public bool Resized { get; set; }
        public bool IncorrectAfterSize { get; init; }
        public int InventoryReads { get; private set; }
        public int NativeGptReadsBeforeRestore { get; private set; }
        public int NativeGptReadsAfterRestore { get; private set; }
        public int NtfsReadsBeforeRestore { get; private set; }
        public int NtfsReadsAfterRestore { get; private set; }
        public List<bool> ExplicitSizeReads { get; } = [];

        private CanonicalVolumeIdentityV1 Source => plan.ReviewedIntent!.ShrinkSource;
        private ulong CurrentSize => Resized
            ? plan.ReviewedIntent!.ResultingSourceBytes +
                (IncorrectAfterSize || Change == "source-size" ? MiB : 0)
            : Source.SizeBytes + (Change == "source-size" ? MiB : 0);

        public Observation<WindowsStorageSnapshot> ReadIdentitySnapshot()
        {
            InventoryReads++;
            var disk = plan.TargetDisk;
            var esp = plan.WindowsEsp;
            var source = Source;
            var diskRow = new DiskObservation(A(2U), A(disk.UniqueId), A(disk.UniqueIdFormat),
                A("fixture-serial"), A(disk.BusType), A("Fixture disk"), A(disk.SizeBytes), A(2U),
                A(disk.GptDiskGuid), A(disk.LogicalSectorSize), A(disk.PhysicalSectorSize));
            var espGuid = Change == "windows-esp" ? Guid.NewGuid() : esp.PartitionGuid;
            var sourceGuid = Change == "source-guid" ? Guid.NewGuid() : source.PartitionGuid;
            var espPartition = new PartitionObservation(A(2U), A(1U), A(espGuid),
                A(esp.PartitionType), A(esp.OffsetBytes), A(esp.SizeBytes), A(true), A(false),
                A(false), Missing<char>(), A(ImmutableArray<string>.Empty));
            var sourcePartition = new PartitionObservation(A(2U), A(2U), A(sourceGuid),
                A(source.PartitionType), A(source.OffsetBytes), A(CurrentSize), A(false), A(true),
                A(false), A('C'), A(ImmutableArray<string>.Empty));
            var espVolume = new VolumeObservation(A(esp.VolumeGuid), A(espGuid), A("FAT32"),
                A("System"), Missing<char>(), A("OK"), A(false));
            var sourceVolume = new VolumeObservation(A(source.VolumeGuid), A(sourceGuid), A("NTFS"),
                A("Windows"), A('C'), A("OK"), A(Change == "dirty"));
            var partitions = new List<PartitionObservation> { espPartition, sourcePartition };
            if (Change == "extra-partition")
                partitions.Add(new(A(2U), A(3U), A(Guid.NewGuid()),
                    A(PreparationSpacePlanning.BasicDataType), A(70000 * MiB), A(100 * MiB),
                    A(false), A(false), A(false), Missing<char>(),
                    A(ImmutableArray<string>.Empty)));
            return A(new WindowsStorageSnapshot([diskRow], [.. partitions],
                [espVolume, sourceVolume]));
        }

        public ReviewedGptUsableRangeV1 GptRange(uint diskNumber)
        {
            Assert.Equal(2U, diskNumber);
            if (Resized) NativeGptReadsBeforeRestore++;
            else NativeGptReadsAfterRestore++;
            var source = Source;
            var nativeSourceSize = CurrentSize +
                (Change == "native-gpt" && Resized ||
                 Change == "native-after-restore" && !Resized ? MiB : 0);
            return new(plan.TargetDisk.GptDiskGuid, MiB, plan.TargetDisk.SizeBytes - 2 * MiB,
                128, [new(plan.WindowsEsp.PartitionGuid, plan.WindowsEsp.PartitionType,
                        plan.WindowsEsp.OffsetBytes, plan.WindowsEsp.SizeBytes),
                    new(source.PartitionGuid, source.PartitionType,
                        source.OffsetBytes, nativeSourceSize)]);
        }

        public ReviewedNtfsSizeV1 NtfsSize(Guid volumeGuid)
        {
            Assert.Equal(Source.VolumeGuid, volumeGuid);
            if (Resized) NtfsReadsBeforeRestore++;
            else NtfsReadsAfterRestore++;
            if (Change == "ntfs-unavailable" ||
                Change == "ntfs-unavailable-after-shrink" && Resized ||
                Change == "ntfs-unavailable-after-restore" && !Resized)
                throw new InvalidOperationException("The fixture NTFS volume is unavailable.");
            var bytes = CurrentSize +
                (Change == "ntfs-size" && Resized ||
                 Change == "ntfs-after-restore" && !Resized ? MiB : 0);
            return new(bytes, 12345, 512, 4096);
        }

        public WindowsStorageBatch ReadDisks() => throw new NotSupportedException();
        public WindowsStorageBatch ReadVolumes(char driveLetter) => throw new NotSupportedException();

        public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null,
            bool efiOnly = false) => new([new WindowsStorageRow(new (string, object?)[]
        {
            ("DiskNumber", 2U), ("PartitionNumber", 2U), ("Guid", Source.PartitionGuid.ToString("B")),
            ("GptType", Source.PartitionType.ToString("B")), ("Offset", Source.OffsetBytes),
            ("Size", CurrentSize), ("IsBoot", true), ("DriveLetter", "C:"),
        }.ToImmutableDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase),
            "fixture://exact-source")]);

        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition,
            bool explicitParameters = false)
        {
            ExplicitSizeReads.Add(explicitParameters);
            return new(new WindowsStorageRow(new (string, object?)[]
            {
                ("ReturnValue", 0U), ("SizeMin", 35000 * MiB),
                ("SizeMax", Change == "restore-size-limit" ? Source.SizeBytes - MiB : Source.SizeBytes),
            }.ToImmutableDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase)));
        }
    }

    private sealed class BitLockerFake(StorageFake storage) : IWindowsBitLockerReader
    {
        public Observation<BitLockerVolumeObservation> ReadExactVolume(VolumeObservation volume) =>
            A(new BitLockerVolumeObservation(volume.VolumeGuid.Value, A('C'),
                A(storage.Change == "bitlocker" ? 2U : 0U), A(0U), A(0U), A(0U)));
        public WindowsStorageBatch ReadByDriveLetter(char driveLetter) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
