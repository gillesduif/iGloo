using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class ReviewedPartitionCreationTests : IDisposable
{
    private const ulong MiB = PreparationSpacePlanning.Alignment;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "igloo-reviewed-create-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NativeDriveLayoutHeaderProvidesExactUsableBoundsOrFailsClosed()
    {
        const int headerBytes = 48;
        const int entryBytes = 144;
        var bytes = new byte[headerBytes + 5 * entryBytes];
        var guid = Guid.NewGuid();
        var partitions = Enumerable.Range(1, 4).Select(number =>
            new ReviewedGptPartitionV1(Guid.NewGuid(), PreparationSpacePlanning.BasicDataType,
                checked((ulong)number * MiB), MiB)).ToImmutableArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1); // PARTITION_STYLE_GPT
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 5); // includes one unused entry
        guid.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 17408);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), 32212220416);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 128);
        for (var index = 0; index < partitions.Length; index++)
            WriteGptEntry(bytes.AsSpan(headerBytes + index * entryBytes, entryBytes),
                partitions[index], checked((uint)(index + 1)));

        var parsed = ReviewedGptUsableRangeReader.Parse(bytes);
        Assert.Equal(guid, parsed.DiskGuid);
        Assert.Equal(17408UL, parsed.FirstUsableOffsetBytes);
        Assert.Equal(32212220416UL, parsed.UsableLengthBytes);
        Assert.Equal(5U, parsed.PartitionCount);
        Assert.True(partitions.SequenceEqual(parsed.Partitions));
        Assert.Throws<InvalidDataException>(() => ReviewedGptUsableRangeReader.Parse(bytes.AsSpan(0, headerBytes - 1)));
        Assert.Throws<InvalidDataException>(() => ReviewedGptUsableRangeReader.Parse(bytes.AsSpan(0, bytes.Length - 1)));
        var partial = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(partial.AsSpan(headerBytes + 4 * entryBytes + 24), 5);
        Assert.Throws<InvalidDataException>(() => ReviewedGptUsableRangeReader.Parse(partial));
        var duplicate = (byte[])bytes.Clone();
        partitions[0].PartitionGuid.TryWriteBytes(duplicate.AsSpan(headerBytes + entryBytes + 48, 16));
        Assert.Throws<InvalidDataException>(() => ReviewedGptUsableRangeReader.Parse(duplicate));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0); // MBR
        Assert.Throws<InvalidDataException>(() => ReviewedGptUsableRangeReader.Parse(bytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 4); // count exceeds maximum
        Assert.Throws<InvalidDataException>(() => ReviewedGptUsableRangeReader.Parse(bytes));
    }

    [Fact]
    public void SeedReceiptCapacityAdmitsMoreThanTenThousandFilesAndRejectsOversizeBeforeCopy()
    {
        var plan = Plan();
        var created = new PreparationCheckpointV1(1, PreparationState.CreatedAndVerified,
            plan, [], null);
        var payload = plan.WindowsEsp with { VolumeGuid = Guid.NewGuid() };
        var linuxEsp = plan.WindowsEsp with { VolumeGuid = Guid.NewGuid() };
        const string digest = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var ordinaryPaths = Enumerable.Range(0, 10_001)
            .Select(index => $@"\files\note{index:D5}.txt").ToArray();
        var upperBound = PreparedLayoutStore.RequireReviewedSeedReceiptCapacity(created,
            payload, linuxEsp, null, digest, ordinaryPaths, []);
        var actualReceipt = new ReviewedInstallerSeedReceiptV1(plan.GenerationId,
            payload, linuxEsp, null, digest,
            ordinaryPaths.Select(path => new CanonicalFileIdentityV1(
                payload.VolumeGuid, path, 1, digest)).ToImmutableArray(), []);
        var actualBytes = JsonSerializer.SerializeToUtf8Bytes(created with
        {
            State = PreparationState.ContentStagedAndVerified,
            ReviewedSeedContent = actualReceipt,
        });
        Assert.True(actualBytes.LongLength <= upperBound);
        Assert.True(upperBound < CommunityRecoveryArtifactStore.MaximumArtifactBytes);

        var longName = new string('雪', 240);
        var oversizedPaths = Enumerable.Range(0, 16_000)
            .Select(index => $@"\files\{longName}{index:D5}.txt").ToArray();
        Assert.Throws<InvalidDataException>(() =>
            PreparedLayoutStore.RequireReviewedSeedReceiptCapacity(created,
                payload, linuxEsp, null, digest, oversizedPaths, []));
    }

    [Fact]
    public void ReviewedStagingHashRejectsAnOpenWriterEvenWhenFileSizeIsUnchanged()
    {
        var agent = Path.Join(_directory, "igloo-agent");
        Directory.CreateDirectory(agent);
        try
        {
            File.WriteAllText(Path.Join(_directory, "migration-manifest.json"), "{}");
            File.WriteAllText(Path.Join(agent, "first-boot.sh"), "#!/bin/sh\n");
            var selected = Path.Join(_directory, "selected.txt");
            File.WriteAllText(selected, "pinned content");
            var before = ReviewedMediaInspector.InspectStagedContent(_directory);

            using (var writer = new FileStream(selected, FileMode.Open,
                       FileAccess.Write, FileShare.Read))
                Assert.Throws<IOException>(() =>
                    ReviewedMediaInspector.InspectStagedContent(_directory));

            Assert.Equal(before, ReviewedMediaInspector.InspectStagedContent(_directory));
        }
        finally
        {
            Directory.Delete(agent, recursive: true);
        }
    }

    private static void WriteGptEntry(Span<byte> destination, ReviewedGptPartitionV1 partition, uint number)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, 1); // PARTITION_STYLE_GPT
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], checked((long)partition.OffsetBytes));
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], checked((long)partition.SizeBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], number);
        partition.PartitionType.TryWriteBytes(destination[32..48]);
        partition.PartitionGuid.TryWriteBytes(destination[48..64]);
    }

    [Fact]
    public void ExactCreateAndFormatPersistsReceiptsBeforeTheNextWrite()
    {
        var plan = Plan();
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var shrink = Shrink(plan);
        var calls = new List<string>();
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) =>
            {
                calls.Add("create:" + allocation.Role);
                Assert.Equal(plan.Space.Allocations[storage.Created.Count], allocation);
                Assert.Contains(Directory.GetFiles(_directory), path =>
                {
                    var durable = JsonSerializer.Deserialize<PreparationCheckpointV1>(File.ReadAllBytes(path));
                    return durable?.ShrinkReceipt == shrink &&
                        (durable.StorageOwnership?.CreatedPartitions.Length ?? 0) == storage.Created.Count &&
                        durable.CreationReceipts.Length == Math.Min(storage.Created.Count, 2);
                });
                return storage.Create(allocation);
            },
            (_, allocation, _) =>
            {
                calls.Add("format:" + allocation.Role);
                storage.Format(allocation.Role);
                return 0;
            }, storage.GptRange, storage.NtfsSize);

        reference = service.CreateAndFormatNext(reference, shrink);
        var first = store.Reopen(reference);
        Assert.Equal(PreparationState.CreationInProgress, first.State);
        Assert.Equal(shrink, first.ShrinkReceipt);
        Assert.Single(first.StorageOwnership!.CreatedPartitions);
        Assert.Single(first.CreationReceipts);
        Assert.Equal(new[] { "create:LinuxEsp", "format:LinuxEsp" }, calls);

        reference = service.CreateAndFormatNext(reference, shrink);
        reference = service.CreateAndFormatNext(reference, shrink);
        var final = store.Reopen(reference);
        Assert.Equal(PreparationState.CreatedAndVerified, final.State);
        Assert.Equal(3, final.StorageOwnership!.CreatedPartitions.Length);
        Assert.Equal(2, final.CreationReceipts.Length);
        Assert.Equal(new[] { "create:LinuxEsp", "format:LinuxEsp", "create:Payload",
            "format:Payload", "create:LinuxRoot" }, calls);
        Assert.Equal(ObservationAvailability.Available, PreparedStorageOwnership.VerifyStructure(
            new PreparedLayoutV1(1, plan, final.CreationReceipts, "", [])
            { StorageOwnership = final.StorageOwnership, ShrinkReceipt = shrink }).Availability);
    }

    [Fact]
    public void GeometryOnlyShrinkReceiptCanReopenButCannotAuthorizeCreateOrPersistence()
    {
        var plan = Plan();
        var oldReceipt = Shrink(plan) with { NtfsBefore = null, NtfsAfter = null };
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var planned = store.PersistAndReopen(new PreparationCheckpointV1(1,
            PreparationState.Planned, plan, [], null));
        var writes = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, _, _) => { writes++; throw new Xunit.Sdk.XunitException("Unexpected native create."); },
            (_, _, _) => { writes++; throw new Xunit.Sdk.XunitException("Unexpected native format."); },
            storage.GptRange, storage.NtfsSize);

        Assert.Throws<InvalidOperationException>(() => service.PersistReviewedShrink(planned, oldReceipt));
        Assert.Throws<InvalidOperationException>(() => service.CreateAndFormatNext(planned, oldReceipt));
        var legacy = store.PersistAndReopen(store.Reopen(planned) with { ShrinkReceipt = oldReceipt });
        Assert.Equal(oldReceipt, store.Reopen(legacy).ShrinkReceipt);
        Assert.Throws<InvalidOperationException>(() => service.CreateAndFormatNext(legacy, oldReceipt));
        Assert.Equal(0, writes);
        Assert.Empty(storage.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistReviewedShrinkRejectsChangedOrUnavailableNtfs(bool unavailable)
    {
        var plan = Plan();
        var storage = new StorageFake(plan)
        {
            NtfsSizeChanged = !unavailable,
            NtfsUnavailable = unavailable,
        };
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var planned = store.PersistAndReopen(new PreparationCheckpointV1(1,
            PreparationState.Planned, plan, [], null));
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, _, _) => throw new Xunit.Sdk.XunitException("Unexpected native create."),
            (_, _, _) => throw new Xunit.Sdk.XunitException("Unexpected native format."),
            storage.GptRange, storage.NtfsSize);

        Assert.Throws<InvalidOperationException>(() => service.PersistReviewedShrink(planned, Shrink(plan)));
        Assert.Null(store.Reopen(planned).ShrinkReceipt);
        Assert.Empty(storage.Created);
    }

    [Fact]
    public void PersistReviewedShrinkRechecksNtfsAfterDurableSave()
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { NtfsChangeAtRead = 3 };
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var planned = store.PersistAndReopen(new PreparationCheckpointV1(1,
            PreparationState.Planned, plan, [], null));
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, _, _) => throw new Xunit.Sdk.XunitException("Unexpected native create."),
            (_, _, _) => throw new Xunit.Sdk.XunitException("Unexpected native format."),
            storage.GptRange, storage.NtfsSize);

        Assert.Throws<InvalidOperationException>(() => service.PersistReviewedShrink(planned, Shrink(plan)));
        Assert.Equal(3, storage.NtfsReads);
        Assert.Empty(storage.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateAndFormatRequiresCurrentReceiptedNtfsBeforeNativeWrite(bool unavailable)
    {
        var plan = Plan();
        var storage = new StorageFake(plan)
        {
            NtfsSizeChanged = !unavailable,
            NtfsUnavailable = unavailable,
        };
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var writes = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, _, _) => { writes++; throw new Xunit.Sdk.XunitException("Unexpected native create."); },
            (_, _, _) => { writes++; throw new Xunit.Sdk.XunitException("Unexpected native format."); },
            storage.GptRange, storage.NtfsSize);

        Assert.Throws<InvalidOperationException>(() => service.CreateAndFormatNext(reference, Shrink(plan)));
        Assert.Equal(0, writes);
        Assert.Empty(storage.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedStorageSequenceRejectsForgedRecoveryProofBeforeNativeMutation(
        bool preCreateRoot)
    {
        var isoPath = Path.Join(_directory, "reviewed-storage.iso");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(isoPath, "pinned disposable installer image");
        var iso = new FileInfo(isoPath);
        var pin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(isoPath)));
        var baseline = Plan(preCreateRoot: preCreateRoot);
        var plan = baseline with { ReviewedIntent = baseline.ReviewedIntent! with
        {
            ArtifactSha256 = pin,
            Media = baseline.ReviewedIntent.Media with { FullIsoBytes = (ulong)iso.Length },
        } };
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var planned = store.PersistAndReopen(new(1, PreparationState.Planned, plan, [], null));
        var storage = new StorageFake(plan) { Resized = false };
        var writes = new List<string>();
        var resizer = new PartitionResizeService(NullLogger<PartitionResizeService>.Instance,
            storage, new BitLockerFake(), (_, size, _) =>
            {
                writes.Add("resize");
                Assert.Equal(plan.ReviewedIntent!.ResultingSourceBytes, size);
                storage.Resized = true;
                return 0;
            }, storage.GptRange, storage.NtfsSize);
        var reviewed = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) =>
            {
                writes.Add("create:" + allocation.Role);
                Assert.Contains(Directory.GetFiles(_directory, "*.json"), path =>
                    JsonSerializer.Deserialize<PreparationCheckpointV1>(File.ReadAllBytes(path))
                        ?.ShrinkReceipt == Shrink(plan));
                return storage.Create(allocation);
            },
            (_, allocation, _) =>
            {
                writes.Add("format:" + allocation.Role);
                storage.Format(allocation.Role);
                return 0;
            }, storage.GptRange, storage.NtfsSize);
        var boot = new InstallerBootSpec
        {
            MenuTitle = "Reviewed fixture", KernelCmdline = "fixture", IsoSha256 = pin,
            CopyFullIsoToVolume = false, PreCreateRootPartition = preCreateRoot,
        };
        Task<PreparationPlanV1> Resolve(CancellationToken _) => Task.FromResult(plan);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            DirectInstallService.PrepareReviewedStorageAsync(store, reviewed, resizer,
                planned, Resolve, boot, 8000 * (long)MiB, isoPath));
        Assert.Empty(writes);
        Assert.Equal(PreparationState.Planned, store.Reopen(planned).State);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            DirectInstallService.PrepareReviewedStorageAsync(store, reviewed, resizer,
                planned, Resolve, boot, 8000 * (long)MiB, isoPath,
                (bound, _) => Observations.Available(
                    PreparedLayoutRecoveryFootprint.Derive(bound).Value with
                        { MissingProofCodes = [] })));
        Assert.Empty(writes);
        Assert.Equal(PreparationState.Planned, store.Reopen(planned).State);

        Assert.Empty(storage.Created);
        Assert.Equal(PreparationState.Planned, store.Reopen(planned).State);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ReviewedNativeEntryRechecksMediaAndRejectsMissingRestorationProofs(
        bool externalMedia, bool ownedRoot)
    {
        Directory.CreateDirectory(_directory);
        var stage = Path.Join(_directory, "reviewed-entry-stage");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(Path.Join(stage, "igloo-agent"));
        Directory.CreateDirectory(Path.Join(stage, "files"));
        try
        {
            var image = ReviewedPinnedIsoSourceTests.MakeIso();
            var isoPath = Path.Join(_directory, "reviewed-entry.iso");
            await File.WriteAllBytesAsync(isoPath, image);
            var pin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image));
            IDistroPlugin provider = ownedRoot
                ? new ReviewedOwnedProviderFake(pin, !externalMedia)
                : new ReviewedProviderFake { IsoPin = pin, UseIsoBoot = !externalMedia };
            var bootSpec = provider.GetInstallerBootSpec() with
                { PreCreateRootPartition = ownedRoot };
            var kernelPath = Path.Join(_directory, "reviewed-entry-kernel");
            var initrdPath = Path.Join(_directory, "reviewed-entry-initrd");
            if (externalMedia)
            {
                await File.WriteAllTextAsync(kernelPath, "kernel fixture");
                await File.WriteAllTextAsync(initrdPath, "raw initrd fixture");
            }
            var baseline = Plan(preCreateRoot: ownedRoot, linuxBytes: 8192 * MiB);
            var plan = baseline with { ReviewedIntent = baseline.ReviewedIntent! with
            {
                ArtifactSha256 = pin,
                Media = new((ulong)image.Length,
                    externalMedia ? (ulong)"kernel fixture".Length : (ulong)"signed shim".Length,
                    externalMedia ? (ulong)"raw initrd fixture".Length : (ulong)"signed grub".Length,
                    0),
            } };
            var manifest = new MigrationManifest
            {
                DistroId = provider.Id,
                User = new MigrationUser { WindowsUsername = "fixture",
                    PreferredLinuxUsername = "fixture" },
                Files = new FileMigrationPlan { StagingPath = "fixture" },
                Hardware = new HardwareProfile
                {
                    InstallMode = "dual-boot", TargetDiskGuid = plan.TargetDisk.GptDiskGuid,
                    TargetDiskBytes = (long)plan.TargetDisk.SizeBytes,
                    LinuxPartitionSizeGb = 8,
                },
            };
            await File.WriteAllTextAsync(Path.Join(stage, "migration-manifest.json"),
                JsonSerializer.Serialize(manifest));
            await File.WriteAllTextAsync(Path.Join(stage, "igloo-agent", "first-boot.sh"),
                "#!/bin/sh\nexit 0\n");
            await File.WriteAllTextAsync(Path.Join(stage, "files", "notes..draft.txt"),
                "selected user data");
            var staged = ReviewedMediaInspector.InspectStagedContent(stage);
            plan = plan with { ReviewedIntent = plan.ReviewedIntent! with
                { StagedInventorySha256 = staged.InventorySha256 } };
            var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
            var planned = store.PersistAndReopen(new(1, PreparationState.Planned, plan, [], null));
            var footprint = PreparedLayoutRecoveryFootprint.Derive(plan).Value;
            var request = new ReviewedDirectInstallRequestV1(provider.Id, planned, plan,
                plan.TargetDisk, bootSpec, 8L * 1024 * 1024 * 1024, isoPath,
                externalMedia ? kernelPath : null, externalMedia ? initrdPath : null,
                stage, staged.TotalBytes, staged.InventorySha256, footprint);
            var resolves = 0;
            Task<PreparationPlanV1> Resolve(CancellationToken _)
            {
                resolves++;
                return Task.FromResult(plan);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.ReviewNativeEntryAsync(request with
                    { ExpectedRecoveryFootprint = footprint with { MissingProofCodes = [] } },
                    provider, Resolve, store));
            Assert.Equal(0, resolves);
            var launcher = Path.Join(stage, "igloo-agent", "first-boot.sh");
            await File.WriteAllTextAsync(launcher, "#!/bin/sh\nexit 1\n");
            var changed = ReviewedMediaInspector.InspectStagedContent(stage);
            Assert.Equal(staged.TotalBytes, changed.TotalBytes);
            Assert.NotEqual(staged.InventorySha256, changed.InventorySha256);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.ReviewNativeEntryAsync(request, provider,
                    Resolve, store));
            Assert.Equal(2, resolves);
            await File.WriteAllTextAsync(launcher, "#!/bin/sh\nexit 0\n");
            resolves = 0;
            var blocked = await Assert.ThrowsAsync<NotSupportedException>(() =>
                DirectInstallService.ReviewNativeEntryAsync(request, provider, Resolve, store));
            Assert.Contains("NtfsResizeRestorationUnproven", blocked.Message,
                StringComparison.Ordinal);
            Assert.Equal(3, resolves);
            Assert.Equal(PreparationState.Planned, store.Reopen(planned).State);
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    [Fact]
    public void ReviewedSelectedFileNameAllowsInteriorDotsButRejectsTraversal()
    {
        Assert.True(ReviewedSeedPathRules.TrySegments(@"\files\notes..draft.txt", out _));
        Assert.False(ReviewedSeedPathRules.TrySegments(@"\files\..\outside.txt", out _));
        Assert.False(ReviewedSeedPathRules.TrySegments(@"\files\name.\outside.txt", out _));
    }

    [Theory]
    [InlineData(@"\files\COM¹.txt")]
    [InlineData(@"\files\COM².txt")]
    [InlineData(@"\files\COM³.txt")]
    [InlineData(@"\files\LPT¹.txt")]
    [InlineData(@"\files\LPT².txt")]
    [InlineData(@"\files\LPT³.txt")]
    [InlineData(@"\files\folder\report?.txt")]
    [InlineData(@"\files\folder\report*.txt")]
    [InlineData(@"\files\folder\report|.txt")]
    public void ReviewedSeedDestinationRejectsWindowsReservedNamesOnEveryHost(string path)
    {
        Assert.False(ReviewedSeedPathRules.TrySegments(path, out _));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task UncertainShrinkReceiptPersistsExactOrAmbiguousCheckpointBeforeCreate(
        bool exactPostGeometry, bool failReceiptWrite)
    {
        var isoPath = Path.Join(_directory, "reviewed-uncertain-shrink.iso");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(isoPath, "pinned disposable image");
        var bytes = await File.ReadAllBytesAsync(isoPath);
        var pin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var baseline = Plan(preCreateRoot: false);
        var plan = baseline with { ReviewedIntent = baseline.ReviewedIntent! with
        {
            ArtifactSha256 = pin,
            Media = baseline.ReviewedIntent.Media with { FullIsoBytes = (ulong)bytes.Length },
        } };
        var store = new PreparedLayoutStore(failReceiptWrite
            ? new FailingSecondWriteStore(_directory)
            : new CommunityRecoveryArtifactStore(_directory));
        var planned = store.PersistAndReopen(new(1, PreparationState.Planned, plan, [], null));
        var storage = new StorageFake(plan) { Resized = exactPostGeometry };
        var reviewed = new ReviewedPartitionCreationService(storage, store,
            (_, _, _) => throw new Xunit.Sdk.XunitException("Create ran after uncertain shrink."),
            (_, _, _) => throw new Xunit.Sdk.XunitException("Format ran after uncertain shrink."),
            storage.GptRange, storage.NtfsSize);
        var uncertain = new ReviewedShrinkMutationException(
            exactPostGeometry ? Shrink(plan) : null,
            new InvalidOperationException("Simulated uncertain resize return."));
        var error = DirectInstallService.PersistUncertainReviewedShrink(
            store, reviewed, planned, uncertain);

        var durable = store.Reopen(error.LatestCheckpoint);
        Assert.Equal(exactPostGeometry && !failReceiptWrite ? PreparationState.Planned :
            PreparationState.AmbiguousLeftover, durable.State);
        Assert.Equal(exactPostGeometry && !failReceiptWrite ? Shrink(plan) : null,
            durable.ShrinkReceipt);
        Assert.Empty(durable.CreationReceipts);
        Assert.Empty(storage.Created);
    }

    [Fact]
    public void InstallerCreatedLinuxRootIsNotInventedAsAWindowsPartitionReceipt()
    {
        var plan = Plan(preCreateRoot: false);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var shrink = Shrink(plan);
        var calls = new List<PreparationRole>();
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) =>
            {
                calls.Add(allocation.Role);
                return storage.Create(allocation);
            },
            (_, allocation, _) =>
            {
                storage.Format(allocation.Role);
                return 0;
            }, storage.GptRange, storage.NtfsSize);

        reference = service.CreateAndFormatNext(reference, shrink);
        reference = service.CreateAndFormatNext(reference, shrink);
        var checkpoint = store.Reopen(reference);

        Assert.Equal(PreparationState.CreatedAndVerified, checkpoint.State);
        Assert.Equal([PreparationRole.LinuxEsp, PreparationRole.Payload], calls);
        Assert.DoesNotContain(checkpoint.StorageOwnership!.CreatedPartitions,
            receipt => receipt.Role == PreparationRole.LinuxRoot);
        Assert.Equal(2, checkpoint.CreationReceipts.Length);
        Assert.Equal(ObservationAvailability.Available, PreparedStorageOwnership.VerifyStructure(
            new PreparedLayoutV1(1, plan, checkpoint.CreationReceipts, "", [])
            { StorageOwnership = checkpoint.StorageOwnership, ShrinkReceipt = shrink }).Availability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedRootHandoffRequiresDurableReceiptAndIndependentCurrentGpt(
        bool includeIso)
    {
        var plan = Plan(preCreateRoot: true, isoBytes: includeIso ? 6000 * MiB : 0);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var writes = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => { writes++; return storage.Create(allocation); },
            (_, allocation, _) => { writes++; storage.Format(allocation.Role); return 0; },
            storage.GptRange, storage.NtfsSize);
        Assert.Throws<InvalidOperationException>(() => service.ObserveOwnedRootHandoff(reference));
        Assert.Equal(0, writes);
        foreach (var _ in plan.Space.Allocations.Where(a => a.CreateInWindows))
            reference = service.CreateAndFormatNext(reference, Shrink(plan));

        var checkpoint = store.Reopen(reference);
        var before = writes;
        var handoff = service.ObserveOwnedRootHandoff(reference);
        Assert.Equal(reference, handoff.Checkpoint);
        Assert.Equal(plan.GenerationId, handoff.GenerationId);
        Assert.Equal(plan.TargetDisk, handoff.TargetDisk);
        Assert.Equal(checkpoint.StorageOwnership!.CreatedPartitions.Single(r =>
            r.Role == PreparationRole.LinuxRoot).Identity, handoff.LinuxRoot);
        Assert.Equal(checkpoint.CreationReceipts.Single(r =>
            r.Role == PreparationRole.LinuxEsp).Identity, handoff.LinuxEsp);
        Assert.Equal(checkpoint.CreationReceipts.Single(r =>
            r.Role == PreparationRole.Payload).Identity, handoff.Payload);
        Assert.Equal(includeIso, handoff.Iso is not null);
        Assert.DoesNotContain(checkpoint.CreationReceipts,
            r => r.Role == PreparationRole.LinuxRoot);
        Assert.Equal(before, writes);
        Assert.Throws<InvalidOperationException>(() => service.ObserveInstallerHandoff(reference));

        var mismatchedNative = new ReviewedPartitionCreationService(storage, store,
            (_, _, _) => throw new Xunit.Sdk.XunitException("Read-only handoff created a partition."),
            (_, _, _) => throw new Xunit.Sdk.XunitException("Read-only handoff formatted a partition."),
            number =>
            {
                var current = storage.GptRange(number);
                return current with { Partitions = current.Partitions.Select(partition =>
                    partition.PartitionGuid == handoff.LinuxRoot.PartitionGuid
                        ? partition with { PartitionGuid = Guid.NewGuid() }
                        : partition).ToImmutableArray() };
            }, storage.NtfsSize);
        Assert.Throws<InvalidOperationException>(() =>
            mismatchedNative.ObserveOwnedRootHandoff(reference));
        storage.ChangePayloadFileSystem = true;
        Assert.Throws<InvalidOperationException>(() => service.ObserveOwnedRootHandoff(reference));
        storage.ChangePayloadFileSystem = false;
        storage.ExtraPartition = true;
        Assert.Throws<InvalidOperationException>(() => service.ObserveOwnedRootHandoff(reference));
        Assert.Equal(before, writes);
    }

    [Fact]
    public void OwnedRootBindingRequiresStableIndependentFatUuidReadback()
    {
        var plan = Plan(preCreateRoot: true);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => storage.Create(allocation),
            (_, allocation, _) => { storage.Format(allocation.Role); return 0; },
            storage.GptRange, storage.NtfsSize);
        foreach (var _ in plan.Space.Allocations.Where(allocation => allocation.CreateInWindows))
            reference = service.CreateAndFormatNext(reference, Shrink(plan));
        var handoff = service.ObserveOwnedRootHandoff(reference);
        var observations = 0;
        Observation<string> ReadFat(CanonicalVolumeIdentityV1 volume)
        {
            observations++;
            if (volume == plan.WindowsEsp) return Observations.Available("1111-AAAA");
            if (volume == handoff.LinuxEsp) return Observations.Available("2222-BBBB");
            if (volume == handoff.Payload) return Observations.Available("3333-CCCC");
            throw new Xunit.Sdk.XunitException("A different volume was nominated for FAT readback.");
        }

        var binding = service.ObserveOwnedRootBinding(reference, ReadFat);
        Assert.Equal(6, observations);
        Assert.Equal(handoff, binding.Storage);
        Assert.Equal("1111-AAAA", binding.Ownership.Esp.WindowsEsp.FileSystemUuid);
        Assert.Equal("2222-BBBB", binding.Ownership.Esp.LinuxEsp.FileSystemUuid);
        Assert.Equal("3333-CCCC", binding.Ownership.Esp.Payload.FileSystemUuid);
        Assert.Equal(handoff.LinuxRoot, binding.Ownership.Layout.StorageOwnership!
            .CreatedPartitions.Single(receipt => receipt.Role == PreparationRole.LinuxRoot).Identity);
        Assert.Null(binding.Ownership.IsoFileSystemUuid);
        Assert.Throws<InvalidOperationException>(() => service.ObserveOwnedRootBinding(reference,
            volume => volume == handoff.LinuxEsp && ++observations > 9
                ? Observations.Available("EEEE-FFFF") : ReadFat(volume)));
        storage.ExtraPartition = true;
        Assert.Throws<InvalidOperationException>(() => service.ObserveOwnedRootBinding(reference,
            ReadFat));
    }

    [Fact]
    public void OwnedRootBindingRefusesIsoWithoutNtfsUuidReadback()
    {
        var plan = Plan(preCreateRoot: true, isoBytes: 6000 * MiB);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => storage.Create(allocation),
            (_, allocation, _) => { storage.Format(allocation.Role); return 0; },
            storage.GptRange, storage.NtfsSize);
        foreach (var _ in plan.Space.Allocations.Where(allocation => allocation.CreateInWindows))
            reference = service.CreateAndFormatNext(reference, Shrink(plan));
        Assert.Throws<NotSupportedException>(() => service.ObserveOwnedRootBinding(reference,
            _ => throw new Xunit.Sdk.XunitException("ISO case must fail before FAT reads.")));
    }

    [Fact]
    public void CompletedWindowsReceiptsYieldOnlyTheCurrentGptRootGap()
    {
        var plan = Plan(preCreateRoot: false);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var firstUsable = 34UL * plan.TargetDisk.LogicalSectorSize;
        var usableEnd = plan.TargetDisk.SizeBytes - 33UL * plan.TargetDisk.LogicalSectorSize;
        var native = storage.GptRange(2);
        var nativeCalls = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => storage.Create(allocation),
            (_, allocation, _) => { storage.Format(allocation.Role); return 0; },
            _ => { nativeCalls++; return native; }, storage.NtfsSize);
        reference = service.CreateAndFormatNext(reference, Shrink(plan));
        native = storage.GptRange(2);
        reference = service.CreateAndFormatNext(reference, Shrink(plan));
        var nativePartitions = new[]
        {
            new ReviewedGptPartitionV1(plan.WindowsEsp.PartitionGuid,
                plan.WindowsEsp.PartitionType, plan.WindowsEsp.OffsetBytes, plan.WindowsEsp.SizeBytes),
            new ReviewedGptPartitionV1(plan.ReviewedIntent!.ShrinkSource.PartitionGuid,
                plan.ReviewedIntent.ShrinkSource.PartitionType,
                plan.ReviewedIntent.ShrinkSource.OffsetBytes, plan.ReviewedIntent.ResultingSourceBytes),
        }.Concat(storage.Created.Select(created => new ReviewedGptPartitionV1(created.PartitionGuid,
            created.Allocation.PartitionType, created.Allocation.OffsetBytes,
            created.Allocation.SizeBytes))).ToImmutableArray();
        // Windows may return an unused layout entry. Only the nonempty GPT
        // entries must match the complete WMI partition set.
        native = native with { PartitionCount = (uint)nativePartitions.Length + 1,
            Partitions = nativePartitions };

        var result = service.ObserveInstallerFreeExtent(reference);
        var root = plan.Space.Allocations.Single(a => a.Role == PreparationRole.LinuxRoot);
        var esp = store.Reopen(reference).StorageOwnership!.CreatedPartitions.Single(
            r => r.Role == PreparationRole.LinuxEsp);
        Assert.Equal(plan.GenerationId, result.GenerationId);
        Assert.Equal(plan.TargetDisk.GptDiskGuid, result.TargetDiskGuid);
        Assert.Equal(esp.Identity.PartitionGuid, result.LinuxEspPartitionGuid);
        Assert.Equal(root.OffsetBytes, result.OffsetBytes);
        Assert.Equal(usableEnd - root.OffsetBytes, result.SizeBytes);
        Assert.Equal(root.SizeBytes, result.RootMinimumBytes);
        Assert.Equal(6, nativeCalls);

        native = native with { DiskGuid = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => service.ObserveInstallerFreeExtent(reference));
        native = native with { DiskGuid = plan.TargetDisk.GptDiskGuid,
            Partitions = nativePartitions.SetItem(0,
                nativePartitions[0] with { PartitionType = Guid.NewGuid() }) };
        Assert.Throws<InvalidOperationException>(() => service.ObserveInstallerFreeExtent(reference));
        native = native with { Partitions = nativePartitions,
            UsableLengthBytes = root.OffsetBytes - firstUsable };
        Assert.Throws<InvalidOperationException>(() => service.ObserveInstallerFreeExtent(reference));
        storage.ExtraPartition = true;
        native = native with { UsableLengthBytes = usableEnd - firstUsable };
        Assert.Throws<InvalidOperationException>(() => service.ObserveInstallerFreeExtent(reference));
    }

    [Fact]
    public void InstallerGapCannotBeInferredFromAnIncompleteCheckpointOrPrecreatedRoot()
    {
        var plan = Plan(preCreateRoot: false);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = store.PersistAndReopen(new(1, PreparationState.Planned, plan, [], null));
        var nativeCalls = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, _, _) => throw new InvalidOperationException("read-only observer invoked a create"),
            (_, _, _) => throw new InvalidOperationException("read-only observer invoked a format"),
            _ => { nativeCalls++; throw new InvalidOperationException("incomplete checkpoint read GPT"); });
        Assert.Throws<InvalidOperationException>(() => service.ObserveInstallerFreeExtent(reference));
        Assert.Equal(0, nativeCalls);

        var precreatedPlan = Plan(preCreateRoot: true);
        var precreatedStore = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var precreated = precreatedStore.PersistAndReopen(new(1, PreparationState.Planned,
            precreatedPlan, [], null));
        var precreatedService = new ReviewedPartitionCreationService(new StorageFake(precreatedPlan),
            precreatedStore,
            (_, _, _) => throw new InvalidOperationException("read-only observer invoked a create"),
            (_, _, _) => throw new InvalidOperationException("read-only observer invoked a format"),
            _ => { nativeCalls++; throw new InvalidOperationException("precreated root read GPT"); });
        Assert.Throws<InvalidOperationException>(() => precreatedService.ObserveInstallerFreeExtent(precreated));
        Assert.Equal(0, nativeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedHandoffRebindsExactFormattedVolumesAndRejectsFileSystemDrift(bool includeIso)
    {
        var plan = Plan(preCreateRoot: false, isoBytes: includeIso ? 6000 * MiB : 0);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var writes = 0;
        var nativeReads = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => { writes++; return storage.Create(allocation); },
            (_, allocation, _) => { writes++; storage.Format(allocation.Role); return 0; },
            _ =>
            {
                nativeReads++;
                var first = 34UL * plan.TargetDisk.LogicalSectorSize;
                var end = plan.TargetDisk.SizeBytes - 33UL * plan.TargetDisk.LogicalSectorSize;
                var partitions = new[]
                {
                    new ReviewedGptPartitionV1(plan.WindowsEsp.PartitionGuid,
                        plan.WindowsEsp.PartitionType, plan.WindowsEsp.OffsetBytes,
                        plan.WindowsEsp.SizeBytes),
                    new ReviewedGptPartitionV1(plan.ReviewedIntent!.ShrinkSource.PartitionGuid,
                        plan.ReviewedIntent.ShrinkSource.PartitionType,
                        plan.ReviewedIntent.ShrinkSource.OffsetBytes,
                        plan.ReviewedIntent.ResultingSourceBytes),
                }.Concat(storage.Created.Select(created => new ReviewedGptPartitionV1(
                    created.PartitionGuid, created.Allocation.PartitionType,
                    created.Allocation.OffsetBytes, created.Allocation.SizeBytes))).ToImmutableArray();
                return new ReviewedGptUsableRangeV1(plan.TargetDisk.GptDiskGuid,
                    first, end - first, (uint)partitions.Length, partitions);
            }, storage.NtfsSize);
        reference = service.CreateAndFormatNext(reference, Shrink(plan));
        reference = service.CreateAndFormatNext(reference, Shrink(plan));
        if (includeIso)
            reference = service.CreateAndFormatNext(reference, Shrink(plan));
        var writesBeforeObservation = writes;

        var handoff = service.ObserveInstallerHandoff(reference);
        var checkpoint = store.Reopen(reference);
        Assert.Equal(reference, handoff.Checkpoint);
        Assert.Equal(plan.GenerationId, handoff.GenerationId);
        Assert.Equal(plan.TargetDisk, handoff.TargetDisk);
        Assert.Equal(checkpoint.CreationReceipts.Single(r => r.Role == PreparationRole.LinuxEsp).Identity,
            handoff.LinuxEsp);
        Assert.Equal(checkpoint.CreationReceipts.Single(r => r.Role == PreparationRole.Payload).Identity,
            handoff.Payload);
        if (includeIso)
            Assert.Equal(checkpoint.CreationReceipts.Single(r => r.Role == PreparationRole.Iso).Identity,
                handoff.Iso);
        else
            Assert.Null(handoff.Iso);
        Assert.Equal(plan.Space.Allocations.Single(a => a.Role == PreparationRole.LinuxRoot).OffsetBytes,
            handoff.InstallerRoot.OffsetBytes);
        Assert.Equal(handoff.LinuxEsp.PartitionGuid, handoff.InstallerRoot.LinuxEspPartitionGuid);
        Assert.Equal(writesBeforeObservation, writes);
        Assert.Equal(4 + 2 * (includeIso ? 3 : 2), nativeReads);

        var staging = Path.Combine(_directory, "reviewed-staging");
        var agentDirectory = Path.Combine(staging, "igloo-agent");
        Directory.CreateDirectory(agentDirectory);
        var manifestPath = Path.Combine(staging, "migration-manifest.json");
        var launcherPath = Path.Combine(agentDirectory, "first-boot.sh");
        try
        {
            var manifest = new MigrationManifest
            {
                DistroId = "debian",
                User = new MigrationUser { WindowsUsername = "fixture",
                    PreferredLinuxUsername = "fixture" },
                Files = new FileMigrationPlan { StagingPath = "fixture" },
                Hardware = new HardwareProfile
                {
                    InstallMode = "dual-boot", TargetDiskGuid = plan.TargetDisk.GptDiskGuid,
                    TargetDiskBytes = (long)plan.TargetDisk.SizeBytes,
                },
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
            await File.WriteAllTextAsync(launcherPath, "#!/bin/sh\nexit 0\n");
            var bootSpec = new InstallerBootSpec
            {
                MenuTitle = "Reviewed fixture", KernelCmdline = "fixture",
                IsoSha256 = new string('A', 64), CopyFullIsoToVolume = includeIso,
                PreCreateRootPartition = false,
            };
            var content = service.ObserveContentHandoff(reference, handoff, bootSpec,
                8000 * (long)MiB, staging);
            Assert.Equal(handoff, content.Storage);
            Assert.Equal(2, content.StagedFiles.Length);
            Assert.Equal(content.StagedFiles.Sum(file => file.SizeBytes), content.StagedBytes);
            Assert.All(content.StagedFiles, file => Assert.Equal(64, file.Sha256.Length));
            Assert.Equal(64, content.BootSpecSha256.Length);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.PrepareReviewedAsync(service, content, reference,
                    bootSpec, 8000 * (long)MiB, "unavailable.iso", "unavailable-initrd"));
            await Assert.ThrowsAsync<InvalidDataException>(() => DirectInstallService.PrepareReviewedAsync(
                service, content, reference,
                bootSpec with { KernelCmdline = "fixture changed" },
                8000 * (long)MiB, "unavailable.iso", "unavailable-initrd"));
            await Assert.ThrowsAsync<InvalidDataException>(() => DirectInstallService.PrepareReviewedAsync(
                service, content with { StagedBytes = content.StagedBytes - 1 }, reference,
                bootSpec, 8000 * (long)MiB, "unavailable.iso", "unavailable-initrd"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => DirectInstallService.PrepareReviewedAsync(
                service, content with { Storage = handoff with { GenerationId = Guid.NewGuid() } },
                reference, bootSpec, 8000 * (long)MiB, "unavailable.iso", "unavailable-initrd"));
            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(manifest with { DistroId = "fedora" }));
            Assert.Equal(content.StagedBytes, new FileInfo(manifestPath).Length +
                new FileInfo(launcherPath).Length);
            await Assert.ThrowsAsync<InvalidDataException>(() => DirectInstallService.PrepareReviewedAsync(
                service, content, reference, bootSpec, 8000 * (long)MiB,
                "unavailable.iso", "unavailable-initrd"));
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
            Assert.Throws<InvalidOperationException>(() => service.ObserveContentHandoff(
                reference, handoff with { GenerationId = Guid.NewGuid() }, bootSpec,
                8000 * (long)MiB, staging));
            Assert.Throws<InvalidDataException>(() => service.ObserveContentHandoff(
                reference with { Sha256 = new string('0', 64) }, handoff, bootSpec,
                8000 * (long)MiB, staging));
            Assert.Equal(writesBeforeObservation, writes);
        }
        finally
        {
            File.Delete(launcherPath);
            File.Delete(manifestPath);
            Directory.Delete(agentDirectory);
            Directory.Delete(staging);
        }

        storage.ChangePayloadFileSystem = true;
        Assert.Throws<InvalidOperationException>(() => service.ObserveInstallerHandoff(reference));
        Assert.Equal(writesBeforeObservation, writes);
    }

    [Theory]
    [InlineData("debian")]
    [InlineData("linuxmint-cinnamon")]
    [InlineData("fedora-kde")]
    public async Task ReviewedSeedCopyRequiresEmptyExactVolumeAndPersistsCompleteReadback(
        string distroId)
    {
        var isoContainedBoot = distroId != "debian";
        var oemDrvConfig = distroId == "fedora-kde";
        var isoImage = ReviewedPinnedIsoSourceTests.MakeIso();
        var isoPin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(isoImage));
        var baseline = Plan(preCreateRoot: false, linuxBytes: 8192 * MiB);
        var plan = baseline with
        {
            ReviewedIntent = baseline.ReviewedIntent! with
            {
                ArtifactSha256 = isoPin,
                Media = baseline.ReviewedIntent.Media with
                {
                    FullIsoBytes = (ulong)isoImage.Length,
                    KernelBytes = isoContainedBoot ? 11UL : (ulong)"kernel fixture".Length,
                    InitrdBytes = isoContainedBoot ? 11UL : (ulong)"raw initrd fixture".Length,
                    Stage2Bytes = 13,
                },
            },
        };
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var reviewed = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => storage.Create(allocation),
            (_, allocation, _) => { storage.Format(allocation.Role); return 0; },
            storage.GptRange, storage.NtfsSize);
        reference = reviewed.CreateAndFormatNext(reference, Shrink(plan));
        reference = reviewed.CreateAndFormatNext(reference, Shrink(plan));
        var handoff = reviewed.ObserveInstallerHandoff(reference);
        Assert.Equal(3U, reviewed.ObserveLinuxEspPartitionNumber(handoff));
        var stage = Path.Join(_directory, "reviewed-copy-stage");
        var payloadRoot = Path.Join(_directory, "reviewed-copy-payload");
        var espRoot = Path.Join(_directory, "reviewed-copy-linux-esp");
        Directory.CreateDirectory(Path.Join(stage, "igloo-agent"));
        Directory.CreateDirectory(Path.Join(stage, "igloo-initrd"));
        Directory.CreateDirectory(Path.Join(stage, "files", "igloolab"));
        Directory.CreateDirectory(Path.Join(stage, "browser-profiles", "chrome"));
        Directory.CreateDirectory(payloadRoot);
        Directory.CreateDirectory(espRoot);
        var isoPath = Path.Join(_directory, "reviewed-copy-iso.iso");
        var kernelPath = Path.Join(_directory, "reviewed-copy-kernel");
        var initrdPath = Path.Join(_directory, "reviewed-copy-initrd");
        try
        {
            var manifest = new MigrationManifest
            {
                DistroId = distroId,
                User = new MigrationUser { WindowsUsername = "fixture",
                    PreferredLinuxUsername = "fixture" },
                Files = new FileMigrationPlan { StagingPath = "fixture",
                    Folders = [new MigrationFolder { Name = "igloolab",
                        SourceRelativePath = "Documents" }] },
                Browsers = [new BrowserMigration { Name = "Chrome",
                    SourceRelativePath = "chrome",
                    ProfileStagingPath = "browser-profiles/chrome" }],
                Hardware = new HardwareProfile
                {
                    InstallMode = "dual-boot", TargetDiskGuid = plan.TargetDisk.GptDiskGuid,
                    TargetDiskBytes = (long)plan.TargetDisk.SizeBytes,
                    LinuxPartitionSizeGb = 8,
                },
            };
            await File.WriteAllTextAsync(Path.Join(stage, "migration-manifest.json"),
                JsonSerializer.Serialize(manifest));
            await File.WriteAllTextAsync(Path.Join(stage, "preseed.cfg"), "ordinary preseed");
            if (oemDrvConfig)
                await File.WriteAllTextAsync(Path.Join(stage, "ks.cfg"), "ordinary kickstart");
            await File.WriteAllTextAsync(Path.Join(stage, "igloo-initrd", "guard.sh"), "ordinary guard");
            await File.WriteAllTextAsync(Path.Join(stage, "igloo-agent", "first-boot.sh"), "#!/bin/sh\n");
            await File.WriteAllBytesAsync(Path.Join(stage, "files", "igloolab", "empty.txt"), []);
            await File.WriteAllBytesAsync(Path.Join(stage, "browser-profiles", "chrome", "empty.db"), []);
            await File.WriteAllBytesAsync(isoPath, isoImage);
            await File.WriteAllTextAsync(kernelPath, "kernel fixture");
            await File.WriteAllTextAsync(initrdPath, "raw initrd fixture");
            var provider = new ReviewedProviderFake
                { Id = distroId, IsoPin = isoPin, IncludeStage2 = true,
                    CopyFullIso = !oemDrvConfig, UseIsoBoot = isoContainedBoot,
                    UseOemDrvConfig = oemDrvConfig };
            var bootSpec = provider.GetInstallerBootSpec();
            var content = oemDrvConfig
                ? reviewed.ObserveContentHandoff(reference, handoff, bootSpec,
                    8L * 1024 * 1024 * 1024, stage)
                : await DirectInstallService.StageReviewedProviderContentAsync(
                    reviewed, reference, provider, bootSpec, 8L * 1024 * 1024 * 1024, stage);
            var inspection = new ReviewedInstallerInspection(reviewed).Inspect(
                reference, bootSpec, 8L * 1024 * 1024 * 1024, stage, isoPath,
                isoImage.Length);
            Assert.Equal(handoff.TargetDisk, inspection.TargetDisk);
            Assert.Equal(handoff.InstallerRoot, inspection.InstallerRoot);
            Assert.Equal(content.StagedBytes, inspection.StagedBytes);
            Assert.Equal(content.StagedFiles.Select(file => file.Sha256),
                inspection.StagedFiles.Select(file => file.Sha256));
            CancellationTokenSource? cancelAfterIsoCopy = null;
            Observation<CanonicalFileIdentityV1> ReadCopied(Guid volume, string relative)
            {
                var root = volume == handoff.Payload.VolumeGuid ? payloadRoot :
                    volume == handoff.LinuxEsp.VolumeGuid ? espRoot : null;
                if (root is null) return Missing<CanonicalFileIdentityV1>();
                var path = Path.Join(root, relative.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return Missing<CanonicalFileIdentityV1>();
                if (relative == (oemDrvConfig
                        ? @"\migration-manifest.json" : @"\debian-fixture.iso"))
                    cancelAfterIsoCopy?.Cancel();
#pragma warning disable CA1849 // The injected readback delegate is synchronous by contract.
                var bytes = File.ReadAllBytes(path);
#pragma warning restore CA1849
                return A(new CanonicalFileIdentityV1(volume, relative, bytes.Length,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            }
            var writer = new ReviewedSeedFileWriter(reviewed,
                volume => volume == handoff.Payload.VolumeGuid ? payloadRoot :
                    volume == handoff.LinuxEsp.VolumeGuid ? espRoot :
                    throw new InvalidOperationException("Unexpected fixture volume"), ReadCopied);
            Task<PreparationCheckpointReference> CopySeed(CancellationToken token = default) =>
                isoContainedBoot
                    ? writer.CopyAndPersistPinnedSeedAsync(content, bootSpec,
                        8L * 1024 * 1024 * 1024, isoPath, null, null, token)
                    : writer.CopyAndPersistPinnedExternalSeedAsync(content, bootSpec,
                        8L * 1024 * 1024 * 1024, isoPath, kernelPath, initrdPath, token);
            var emptyFile = Path.Join(stage, "files", "igloolab", "empty.txt");
            var emptyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]));
            foreach (var executablePath in new[] { @"\igloo-agent\empty.sh", @"\preseed.cfg" })
                Assert.Throws<InvalidDataException>(() => writer.CopyNew(handoff,
                    PreparationRole.Payload, executablePath, emptyFile, 0, emptyHash));
            Assert.False(File.Exists(Path.Join(payloadRoot, "igloo-agent", "empty.sh")));
            var occupied = Path.Join(payloadRoot, "EFI", "debian", "grub.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
            await File.WriteAllTextAsync(occupied, "occupied");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CopySeed());
            Assert.False(File.Exists(Path.Join(payloadRoot, "migration-manifest.json")));
            File.Delete(occupied);

            var interrupted = Path.Join(payloadRoot, "EFI", "debian",
                "grub.cfg.igloo-copy-" + new string('a', 32) + ".tmp");
            await File.WriteAllTextAsync(interrupted, "interrupted copy");
            await Assert.ThrowsAsync<InvalidOperationException>(() => CopySeed());
            Assert.Equal("interrupted copy", await File.ReadAllTextAsync(interrupted));
            Assert.False(File.Exists(Path.Join(payloadRoot, "migration-manifest.json")));
            File.Delete(interrupted);

            using (var temporaryCancellation = new CancellationTokenSource())
            {
                var observedTemporary = false;
                Observation<CanonicalFileIdentityV1> CancelAtTemporaryReadback(
                    Guid volume, string relative)
                {
                    if (volume == handoff.Payload.VolumeGuid &&
                        relative.StartsWith(@"\migration-manifest.json.igloo-copy-",
                            StringComparison.Ordinal))
                    {
                        observedTemporary = true;
                        temporaryCancellation.Cancel();
                    }
                    return ReadCopied(volume, relative);
                }
                var temporaryWriter = new ReviewedSeedFileWriter(reviewed,
                    volume => volume == handoff.Payload.VolumeGuid ? payloadRoot :
                        throw new InvalidOperationException("Unexpected fixture volume"),
                    CancelAtTemporaryReadback);
                Assert.Throws<OperationCanceledException>(() =>
                    temporaryWriter.CopyStaged(content, "migration-manifest.json",
                        temporaryCancellation.Token));
                Assert.True(observedTemporary);
                Assert.False(File.Exists(Path.Join(payloadRoot, "migration-manifest.json")));
                Assert.Empty(Directory.EnumerateFiles(payloadRoot,
                    "*.igloo-copy-*.tmp", SearchOption.AllDirectories));
            }

            using (var cancellation = new CancellationTokenSource())
            {
                cancelAfterIsoCopy = cancellation;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    CopySeed(cancellation.Token));
                cancelAfterIsoCopy = null;
            }
            Assert.Equal(PreparationState.CreatedAndVerified, store.Reopen(reference).State);
            Assert.Empty(Directory.EnumerateFiles(payloadRoot, "*",
                SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(payloadRoot, "*.igloo-copy-*.tmp",
                SearchOption.AllDirectories));

            if (!oemDrvConfig)
            {
                var alteredFirstCopy = false;
                Observation<CanonicalFileIdentityV1> AlterReceiptedCopyBeforeRollback(
                    Guid volume, string relative)
                {
                    var observed = ReadCopied(volume, relative);
                    if (!alteredFirstCopy && volume == handoff.Payload.VolumeGuid &&
                        relative == @"\migration-manifest.json" &&
                        observed.Availability == ObservationAvailability.Available)
                    {
                        File.WriteAllText(Path.Join(payloadRoot, "debian-fixture.iso"),
                            "foreign replacement");
                        alteredFirstCopy = true;
                        throw new IOException("Injected later seed readback failure");
                    }
                    return observed;
                }
                var tamperedWriter = new ReviewedSeedFileWriter(reviewed,
                    volume => volume == handoff.Payload.VolumeGuid ? payloadRoot :
                        volume == handoff.LinuxEsp.VolumeGuid ? espRoot :
                        throw new InvalidOperationException("Unexpected fixture volume"),
                    AlterReceiptedCopyBeforeRollback);
                await Assert.ThrowsAsync<ReviewedSeedRestorationException>(() =>
                    tamperedWriter.CopyAndPersistPinnedSeedAsync(content, bootSpec,
                        8L * 1024 * 1024 * 1024, isoPath,
                        isoContainedBoot ? null : kernelPath,
                        isoContainedBoot ? null : initrdPath));
                Assert.True(alteredFirstCopy);
                Assert.Equal("foreign replacement", await File.ReadAllTextAsync(
                    Path.Join(payloadRoot, "debian-fixture.iso")));
                File.Delete(Path.Join(payloadRoot, "debian-fixture.iso"));
                Assert.Empty(Directory.EnumerateFiles(payloadRoot, "*",
                    SearchOption.AllDirectories));
            }

            var staged = await CopySeed();
            var checkpoint = store.Reopen(staged);
            Assert.Equal(PreparationState.ContentStagedAndVerified, checkpoint.State);
            Assert.NotNull(checkpoint.ReviewedSeedContent);
            Assert.Contains(checkpoint.ReviewedSeedContent.PayloadFiles,
                file => file.RelativePath == @"\EFI\debian\grub.cfg");
            Assert.Equal("signed shim", await File.ReadAllTextAsync(Path.Join(payloadRoot,
                "EFI", "BOOT", "BOOTX64.EFI")));
            Assert.Equal("stage2 squash", await File.ReadAllTextAsync(Path.Join(payloadRoot,
                "images", "install.img")));
            Assert.Equal(isoContainedBoot ? "signed shim" : "kernel fixture",
                await File.ReadAllTextAsync(Path.Join(payloadRoot, "igloo-boot", "linux")));
            Assert.StartsWith(isoContainedBoot ? "signed grub" : "raw initrd fixture",
                Encoding.ASCII.GetString(await File.ReadAllBytesAsync(Path.Join(payloadRoot,
                    "igloo-boot", "initrd"))), StringComparison.Ordinal);
            if (oemDrvConfig)
                Assert.Equal("signed grub", await File.ReadAllTextAsync(Path.Join(payloadRoot,
                    "igloo-boot", "initrd")));
            Assert.True(File.Exists(Path.Join(payloadRoot, "igloo-seed-identity.json")));
            Assert.Equal(0, new FileInfo(Path.Join(payloadRoot,
                "files", "igloolab", "empty.txt")).Length);
            Assert.Equal(0, new FileInfo(Path.Join(payloadRoot,
                "browser-profiles", "chrome", "empty.db")).Length);
            Assert.Contains(checkpoint.ReviewedSeedContent.PayloadFiles,
                file => file.RelativePath == @"\files\igloolab\empty.txt" &&
                    file.Length == 0 && file.Sha256 == emptyHash);
            Assert.Contains(checkpoint.ReviewedSeedContent.PayloadFiles,
                file => file.RelativePath == @"\browser-profiles\chrome\empty.db" &&
                    file.Length == 0 && file.Sha256 == emptyHash);
            Assert.Empty(Directory.EnumerateFiles(espRoot, "*", SearchOption.AllDirectories));
            var closed = await Assert.ThrowsAsync<NotSupportedException>(() =>
                DirectInstallService.PrepareReviewedAsync(reviewed, content, staged,
                    bootSpec, 8L * 1024 * 1024 * 1024, isoPath,
                    isoContainedBoot ? null : initrdPath, ReadCopied));
            Assert.Contains("Dedicated iGloo/Linux ESP preparation is blocked",
                closed.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
            Directory.Delete(payloadRoot, recursive: true);
            Directory.Delete(espRoot, recursive: true);
            File.Delete(isoPath);
            File.Delete(kernelPath);
            File.Delete(initrdPath);
        }
    }

    [Fact]
    public async Task ReviewedIsoVolumeCopyMovesOnlyACompleteReadBackTemporaryFile()
    {
        var plan = Plan(preCreateRoot: false, isoBytes: 6000 * MiB);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var reviewed = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => storage.Create(allocation),
            (_, allocation, _) => { storage.Format(allocation.Role); return 0; },
            storage.GptRange, storage.NtfsSize);
        for (var i = 0; i < 3; i++)
            reference = reviewed.CreateAndFormatNext(reference, Shrink(plan));
        var handoff = reviewed.ObserveInstallerHandoff(reference);
        Assert.NotNull(handoff.Iso);
        var source = Path.Join(_directory, "reviewed-iso-copy-source.bin");
        var payloadRoot = Path.Join(_directory, "reviewed-iso-copy-payload");
        var isoRoot = Path.Join(_directory, "reviewed-iso-copy-volume");
        Directory.CreateDirectory(payloadRoot);
        Directory.CreateDirectory(isoRoot);
        try
        {
            var bytes = Encoding.ASCII.GetBytes("complete source image");
            await File.WriteAllBytesAsync(source, bytes);
            var digest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes));
            using var cancellation = new CancellationTokenSource();
            var observedTemporary = false;
            Observation<CanonicalFileIdentityV1> ReadCopied(Guid volume, string relative)
            {
                var root = volume == handoff.Iso.VolumeGuid ? isoRoot :
                    volume == handoff.Payload.VolumeGuid ? payloadRoot : null;
                if (root is null) return Missing<CanonicalFileIdentityV1>();
                var path = Path.Join(root, relative.TrimStart('\\').Replace('\\',
                    Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return Missing<CanonicalFileIdentityV1>();
                var observed = File.ReadAllBytes(path);
                if (volume == handoff.Iso.VolumeGuid &&
                    relative.StartsWith(@"\source.iso.igloo-copy-", StringComparison.Ordinal) &&
                    !observedTemporary)
                {
                    observedTemporary = true;
                    cancellation.Cancel();
                }
                return A(new CanonicalFileIdentityV1(volume, relative, observed.Length,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(observed))));
            }
            var writer = new ReviewedSeedFileWriter(reviewed,
                volume => volume == handoff.Iso.VolumeGuid ? isoRoot :
                    volume == handoff.Payload.VolumeGuid ? payloadRoot :
                    throw new InvalidOperationException("Unexpected fixture volume"), ReadCopied);
            Assert.Throws<OperationCanceledException>(() => writer.CopyNew(handoff,
                PreparationRole.Iso, @"\source.iso", source, bytes.Length, digest,
                cancellation.Token));
            Assert.True(observedTemporary);
            Assert.Empty(Directory.EnumerateFiles(isoRoot, "*", SearchOption.AllDirectories));
            var interrupted = Path.Join(isoRoot,
                "source.iso.igloo-copy-" + new string('b', 32) + ".tmp");
            await File.WriteAllBytesAsync(interrupted, bytes);
            Assert.Throws<InvalidOperationException>(() => writer.CopyNew(handoff,
                PreparationRole.Iso, @"\source.iso", source, bytes.Length, digest));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(interrupted));
            Assert.False(File.Exists(Path.Join(isoRoot, "source.iso")));
            File.Delete(interrupted);
            var copied = writer.CopyNew(handoff, PreparationRole.Iso,
                @"\source.iso", source, bytes.Length, digest);
            Assert.Equal(handoff.Iso.VolumeGuid, copied.VolumeGuid);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Join(isoRoot, "source.iso")));
            Assert.Empty(Directory.EnumerateFiles(isoRoot, "*.igloo-copy-*.tmp",
                SearchOption.AllDirectories));
        }
        finally
        {
            File.Delete(source);
            Directory.Delete(payloadRoot, recursive: true);
            Directory.Delete(isoRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ReviewedProviderStageUsesFreshExactExtentAndRejectsChangedInputs()
    {
        var isoImage = ReviewedPinnedIsoSourceTests.MakeIso();
        var isoPin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(isoImage));
        var baseline = Plan(preCreateRoot: false, linuxBytes: 8192 * MiB);
        var plan = baseline with
        {
            ReviewedIntent = baseline.ReviewedIntent! with
            {
                ArtifactSha256 = isoPin,
                Media = baseline.ReviewedIntent.Media with
                {
                    FullIsoBytes = (ulong)isoImage.Length,
                    KernelBytes = (ulong)"kernel fixture".Length,
                    InitrdBytes = (ulong)"raw initrd fixture".Length,
                    Stage2Bytes = 13,
                },
            },
        };
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => storage.Create(allocation),
            (_, allocation, _) => { storage.Format(allocation.Role); return 0; },
            _ =>
            {
                var source = plan.ReviewedIntent!.ShrinkSource;
                var partitions = new[]
                {
                    new ReviewedGptPartitionV1(plan.WindowsEsp.PartitionGuid,
                        plan.WindowsEsp.PartitionType, plan.WindowsEsp.OffsetBytes,
                        plan.WindowsEsp.SizeBytes),
                    new ReviewedGptPartitionV1(source.PartitionGuid, source.PartitionType,
                        source.OffsetBytes, plan.ReviewedIntent.ResultingSourceBytes),
                }.Concat(storage.Created.Select(created => new ReviewedGptPartitionV1(
                    created.PartitionGuid, created.Allocation.PartitionType,
                    created.Allocation.OffsetBytes, created.Allocation.SizeBytes))).ToImmutableArray();
                var first = 34UL * plan.TargetDisk.LogicalSectorSize;
                var end = plan.TargetDisk.SizeBytes - 33UL * plan.TargetDisk.LogicalSectorSize;
                return new ReviewedGptUsableRangeV1(plan.TargetDisk.GptDiskGuid,
                    first, end - first, (uint)partitions.Length, partitions);
            }, storage.NtfsSize);
        reference = service.CreateAndFormatNext(reference, Shrink(plan));
        reference = service.CreateAndFormatNext(reference, Shrink(plan));
        var expected = service.ObserveInstallerHandoff(reference);
        var provider = new ReviewedProviderFake { IsoPin = isoPin, IncludeStage2 = true };
        var bootSpec = provider.GetInstallerBootSpec();
        var stage = Path.Join(_directory, "reviewed-provider-stage");
        Directory.CreateDirectory(Path.Join(stage, "igloo-agent"));
        Directory.CreateDirectory(Path.Join(stage, "igloo-initrd"));
        var manifestPath = Path.Join(stage, "migration-manifest.json");
        var preseedPath = Path.Join(stage, "preseed.cfg");
        try
        {
            var manifest = new MigrationManifest
            {
                DistroId = "debian",
                User = new MigrationUser { WindowsUsername = "fixture",
                    PreferredLinuxUsername = "fixture" },
                Files = new FileMigrationPlan { StagingPath = "fixture" },
                Hardware = new HardwareProfile
                {
                    InstallMode = "dual-boot", TargetDiskGuid = plan.TargetDisk.GptDiskGuid,
                    TargetDiskBytes = (long)plan.TargetDisk.SizeBytes,
                    LinuxPartitionSizeGb = 8,
                },
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
            await File.WriteAllTextAsync(preseedPath, "ordinary preseed");
            await File.WriteAllTextAsync(Path.Join(stage, "igloo-initrd", "guard.sh"), "ordinary guard");
            await File.WriteAllTextAsync(Path.Join(stage, "igloo-agent", "first-boot.sh"), "#!/bin/sh\n");

            provider.ConfigFileName = "preseed.cfg.";
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.StageReviewedProviderContentAsync(service, reference,
                    provider, provider.GetInstallerBootSpec(), 8L * 1024 * 1024 * 1024,
                    stage));
            Assert.Equal("ordinary preseed", await File.ReadAllTextAsync(preseedPath));
            provider.ConfigFileName = "preseed.cfg";
            provider.AfterRender = () => storage.OnNextIdentityRead = provider.MutateRenderedContents;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DirectInstallService.StageReviewedProviderContentAsync(service, reference,
                    provider, bootSpec with { IsoSha256 = new string('B', 64) },
                    8L * 1024 * 1024 * 1024, stage));
            Assert.Equal("ordinary preseed", await File.ReadAllTextAsync(preseedPath));
            Assert.Equal("ordinary guard", await File.ReadAllTextAsync(
                Path.Join(stage, "igloo-initrd", "guard.sh")));

            var content = await DirectInstallService.StageReviewedProviderContentAsync(
                service, reference, provider, bootSpec, 8L * 1024 * 1024 * 1024, stage);
            provider.AfterRender = null;
            Assert.Equal(expected, content.Storage);
            Assert.Equal(expected.InstallerRoot, provider.LastExtent);
            Assert.Equal((byte)'X', provider.LastConfigBytes![0]);
            Assert.Equal((byte)'X', provider.LastExtraBytes![0]);
            Assert.Contains(expected.InstallerRoot.OffsetBytes.ToString(CultureInfo.InvariantCulture),
                await File.ReadAllTextAsync(preseedPath), StringComparison.Ordinal);
            Assert.StartsWith("bound offset ", await File.ReadAllTextAsync(preseedPath),
                StringComparison.Ordinal);
            Assert.Contains(expected.InstallerRoot.LinuxEspPartitionGuid.ToString("D"),
                await File.ReadAllTextAsync(Path.Join(stage, "igloo-initrd", "guard.sh")),
                StringComparison.Ordinal);
            Assert.StartsWith("ESP ", await File.ReadAllTextAsync(
                Path.Join(stage, "igloo-initrd", "guard.sh")), StringComparison.Ordinal);
            Assert.Equal(4, content.StagedFiles.Length);
            var afterBufferMutation = service.ObserveContentHandoff(reference, expected, bootSpec,
                8L * 1024 * 1024 * 1024, stage);
            Assert.Equal(content.StagedFiles.ToArray(), afterBufferMutation.StagedFiles.ToArray());
            Assert.Equal(content.StagedBytes, afterBufferMutation.StagedBytes);
            Assert.Equal(content.BootSpecSha256, afterBufferMutation.BootSpecSha256);
            var bootPayload = DirectInstallService.BuildReviewedBootPayload(content, bootSpec);
            Assert.Equal(expected, bootPayload.Storage);
            Assert.Equal(content.BootSpecSha256, bootPayload.BootSpecSha256);
            Assert.Contains("igloo.seedpartuuid=" + expected.Payload.PartitionGuid.ToString("D"),
                bootPayload.GrubConfig, StringComparison.Ordinal);
            Assert.Contains("igloo.linuxesppartuuid=" + expected.LinuxEsp.PartitionGuid.ToString("D"),
                bootPayload.GrubConfig, StringComparison.Ordinal);
            Assert.Equal(ReviewedSeedFileWriter.LinuxEspCompiledPrefixConfigPath,
                bootPayload.LinuxEspCompiledPrefixConfigPath);
            Assert.Contains(expected.Payload.PartitionGuid.ToString("D"),
                bootPayload.LinuxEspGrubConfig, StringComparison.Ordinal);
            Assert.Contains(expected.LinuxEsp.PartitionGuid.ToString("D"),
                bootPayload.LinuxEspGrubConfig, StringComparison.Ordinal);
            Assert.Contains("configfile ($root)/EFI/BOOT/grub.cfg",
                bootPayload.LinuxEspGrubConfig, StringComparison.Ordinal);
            Assert.Collection(bootPayload.InitrdFiles,
                file => Assert.Equal("preseed.cfg", file.Name),
                file => Assert.Equal("guard.sh", file.Name));
            Assert.StartsWith("bound offset ",
                Encoding.UTF8.GetString(bootPayload.InitrdFiles[0].Contents.AsSpan()),
                StringComparison.Ordinal);
            Assert.StartsWith("ESP ",
                Encoding.UTF8.GetString(bootPayload.InitrdFiles[1].Contents.AsSpan()),
                StringComparison.Ordinal);
            var aliasBoot = bootSpec with { InitrdConfigPath = "preseed.cfg." };
            var aliasContent = content with
            {
                BootSpecSha256 = ReviewedPartitionCreationService.HashBootSpec(aliasBoot),
            };
            Assert.Throws<InvalidDataException>(() =>
                DirectInstallService.BuildReviewedBootPayload(aliasContent, aliasBoot));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.PrepareReviewedAsync(service, content, reference,
                    bootSpec, 8L * 1024 * 1024 * 1024,
                    Path.Join(_directory, "unavailable-reviewed-iso.iso"),
                    "unavailable-reviewed-initrd"));

            var payloadRoot = Path.Join(_directory, "reviewed-payload-readback");
            var linuxEspRoot = Path.Join(_directory, "reviewed-linux-esp-readback");
            Directory.CreateDirectory(payloadRoot);
            Directory.CreateDirectory(linuxEspRoot);
            try
            {
                var copied = new List<CanonicalFileIdentityV1>();
                Observation<CanonicalFileIdentityV1> ReadCopied(Guid volume, string relative)
                {
                    var volumeRoot = volume == expected.Payload.VolumeGuid ? payloadRoot :
                        volume == expected.LinuxEsp.VolumeGuid ? linuxEspRoot : null;
                    if (volumeRoot is null) return Missing<CanonicalFileIdentityV1>();
                    var path = Path.Join(volumeRoot,
                        relative.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar));
                    if (!File.Exists(path)) return Missing<CanonicalFileIdentityV1>();
#pragma warning disable CA1849 // The injected readback delegate is synchronous by contract.
                    var bytes = File.ReadAllBytes(path);
#pragma warning restore CA1849
                    return A(new CanonicalFileIdentityV1(volume, relative, bytes.Length,
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
                }
                CanonicalFileIdentityV1 WriteSeedFile(string relative, byte[] bytes)
                {
                    var path = Path.Join(payloadRoot, relative.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, bytes);
                    var identity = new CanonicalFileIdentityV1(expected.Payload.VolumeGuid,
                        relative, bytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
                    copied.Add(identity);
                    return identity;
                }
                var fileWriter = new ReviewedSeedFileWriter(service,
                    volume => volume == expected.Payload.VolumeGuid ? payloadRoot :
                        volume == expected.LinuxEsp.VolumeGuid ? linuxEspRoot :
                        throw new InvalidOperationException("Unexpected fixture volume"), ReadCopied);
                var pinnedManifest = content.StagedFiles.Single(file =>
                    file.RelativePath == "migration-manifest.json");
                var staleStorage = expected with
                {
                    Payload = expected.Payload with { VolumeGuid = Guid.NewGuid() },
                };
                Assert.Throws<InvalidOperationException>(() => fileWriter.CopyNew(staleStorage,
                    PreparationRole.Payload, @"\stale-storage.txt", manifestPath,
                    pinnedManifest.SizeBytes, pinnedManifest.Sha256));
                Assert.False(File.Exists(Path.Join(payloadRoot, "stale-storage.txt")));
                Assert.Throws<InvalidDataException>(() => fileWriter.CopyNew(expected,
                    PreparationRole.Payload, @"\wrong-source.txt", manifestPath,
                    pinnedManifest.SizeBytes, new string('0', 64)));
                Assert.False(File.Exists(Path.Join(payloadRoot, "wrong-source.txt")));
                foreach (var unsafePath in new[]
                         { @"\EFI\.\BOOTX64.EFI", @"\alias. ", @"\CON.txt" })
                    Assert.Throws<InvalidDataException>(() => fileWriter.CopyNew(expected,
                        PreparationRole.Payload, unsafePath, manifestPath,
                        pinnedManifest.SizeBytes, pinnedManifest.Sha256));
                copied.Add(fileWriter.CopyStaged(content, "migration-manifest.json"));
                Assert.Throws<InvalidOperationException>(() =>
                    fileWriter.CopyStaged(content, "migration-manifest.json"));
                Assert.Throws<InvalidDataException>(() =>
                    fileWriter.CopyStaged(content, "not-in-the-handoff.txt"));
                foreach (var file in content.StagedFiles.Where(file =>
                             file.RelativePath != "migration-manifest.json"))
                    WriteSeedFile("\\" + file.RelativePath.Replace('/', '\\'),
                        await File.ReadAllBytesAsync(Path.Join(stage,
                            file.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
                var isoPath = Path.Join(_directory, "reviewed-iso-source.iso");
                var changedIsoPath = Path.Join(_directory, "changed-reviewed-iso.iso");
                await File.WriteAllBytesAsync(isoPath, isoImage);
                var changedIso = (byte[])isoImage.Clone();
                changedIso[24 * 2048] ^= 1;
                await File.WriteAllBytesAsync(changedIsoPath, changedIso);
                Assert.Throws<InvalidDataException>(() =>
                    fileWriter.CopyPinnedIsoBootFiles(content, bootSpec, changedIsoPath));
                Assert.False(File.Exists(Path.Join(payloadRoot, "EFI", "BOOT", "BOOTX64.EFI")));
                copied.AddRange(fileWriter.CopyPinnedIsoBootFiles(content, bootSpec, isoPath));
                Assert.Equal("signed shim", await File.ReadAllTextAsync(Path.Join(payloadRoot,
                    "EFI", "BOOT", "BOOTX64.EFI")));
                Assert.Equal("signed grub", await File.ReadAllTextAsync(Path.Join(payloadRoot,
                    "EFI", "BOOT", "grubx64.efi")));
                Assert.Equal("stage2 squash", await File.ReadAllTextAsync(Path.Join(payloadRoot,
                    "images", "install.img")));
                var pinnedShim = Path.Join(payloadRoot, "EFI", "BOOT", "BOOTX64.EFI");
                var shimBytes = await File.ReadAllBytesAsync(pinnedShim);
                var shimHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(shimBytes));
                Assert.Throws<InvalidDataException>(() => fileWriter.CopyExactLinuxEspFile(expected,
                    PreparedLayoutRules.ShimPath, pinnedShim, shimBytes.Length, new string('0', 64)));
                Assert.Throws<InvalidDataException>(() => fileWriter.CopyExactLinuxEspFile(expected,
                    @"\EFI\iGloo\shimx64.efi.", pinnedShim, shimBytes.Length, shimHash));
                Assert.Throws<InvalidDataException>(() => fileWriter.CopyExactLinuxEspFile(expected,
                    @"\EFI\iGloo\grub.cfg.", pinnedShim, shimBytes.Length, shimHash));
                Assert.Throws<InvalidOperationException>(() => fileWriter.CopyExactLinuxEspFile(
                    expected with { LinuxEsp = expected.LinuxEsp with { VolumeGuid = Guid.NewGuid() } },
                    PreparedLayoutRules.ShimPath, pinnedShim, shimBytes.Length, shimHash));
                var espShim = Path.Join(linuxEspRoot, "EFI", "iGloo", "shimx64.efi");
                Assert.False(File.Exists(espShim));
                using (var cancelTemporary = new CancellationTokenSource())
                {
                    var cancelledAtDestinationRead = false;
                    Observation<CanonicalFileIdentityV1> CancelBeforeTemporaryWrite(
                        Guid volume, string relative)
                    {
                        if (volume == expected.LinuxEsp.VolumeGuid &&
                            relative == PreparedLayoutRules.ShimPath &&
                            !cancelledAtDestinationRead)
                        {
                            cancelledAtDestinationRead = true;
                            cancelTemporary.Cancel();
                        }
                        return ReadCopied(volume, relative);
                    }
                    var interruptedWriter = new ReviewedSeedFileWriter(service,
                        volume => volume == expected.Payload.VolumeGuid ? payloadRoot :
                            volume == expected.LinuxEsp.VolumeGuid ? linuxEspRoot :
                            throw new InvalidOperationException("Unexpected fixture volume"),
                        CancelBeforeTemporaryWrite);
                    Assert.Throws<OperationCanceledException>(() =>
                        interruptedWriter.CopyExactLinuxEspFile(expected,
                            PreparedLayoutRules.ShimPath, pinnedShim, shimBytes.Length,
                            shimHash, cancelTemporary.Token));
                    Assert.True(cancelledAtDestinationRead);
                    Assert.False(File.Exists(espShim));
                    Assert.Empty(Directory.EnumerateFiles(linuxEspRoot,
                        "*.igloo-copy-*.tmp", SearchOption.AllDirectories));
                }

                var occupiedBeforeRename = false;
                Observation<CanonicalFileIdentityV1> OccupyDestinationBeforeRename(
                    Guid volume, string relative)
                {
                    if (volume == expected.LinuxEsp.VolumeGuid &&
                        relative.StartsWith(PreparedLayoutRules.ShimPath + ".igloo-copy-",
                            StringComparison.Ordinal) && !occupiedBeforeRename)
                    {
                        File.WriteAllText(espShim, "foreign destination");
                        occupiedBeforeRename = true;
                    }
                    return ReadCopied(volume, relative);
                }
                var competingWriter = new ReviewedSeedFileWriter(service,
                    volume => volume == expected.Payload.VolumeGuid ? payloadRoot :
                        volume == expected.LinuxEsp.VolumeGuid ? linuxEspRoot :
                        throw new InvalidOperationException("Unexpected fixture volume"),
                    OccupyDestinationBeforeRename);
                Assert.Throws<InvalidOperationException>(() =>
                    competingWriter.CopyExactLinuxEspFile(expected,
                        PreparedLayoutRules.ShimPath, pinnedShim, shimBytes.Length, shimHash));
                Assert.True(occupiedBeforeRename);
                Assert.Equal("foreign destination", await File.ReadAllTextAsync(espShim));
                Assert.Empty(Directory.EnumerateFiles(linuxEspRoot,
                    "*.igloo-copy-*.tmp", SearchOption.AllDirectories));
                File.Delete(espShim);
                var espCopy = fileWriter.CopyExactLinuxEspFile(expected,
                    PreparedLayoutRules.ShimPath, pinnedShim, shimBytes.Length, shimHash);
                Assert.Equal(expected.GenerationId, espCopy.GenerationId);
                Assert.Equal(expected.TargetDisk, espCopy.TargetDisk);
                Assert.Equal(expected.LinuxEsp, espCopy.LinuxEsp);
                Assert.Equal(expected.LinuxEsp.VolumeGuid, espCopy.File.VolumeGuid);
                Assert.Equal(espCopy, fileWriter.ReobserveLinuxEspFileCopy(expected, espCopy));
                Assert.Throws<InvalidDataException>(() => fileWriter.ReobserveLinuxEspFileCopy(
                    expected, espCopy with { TargetDisk = expected.TargetDisk with
                    { GptDiskGuid = Guid.NewGuid() } }));
                Assert.Throws<InvalidDataException>(() => fileWriter.ReobserveLinuxEspFileCopy(
                    expected, espCopy with { LinuxEsp = expected.LinuxEsp with
                    { PartitionGuid = Guid.NewGuid() } }));
                Assert.Throws<InvalidOperationException>(() => fileWriter.CopyExactLinuxEspFile(expected,
                    PreparedLayoutRules.ShimPath, pinnedShim, shimBytes.Length, shimHash));
                await File.WriteAllTextAsync(espShim, "changed installed ESP copy");
                Assert.Throws<InvalidDataException>(() =>
                    fileWriter.ReobserveLinuxEspFileCopy(expected, espCopy));
                await File.WriteAllBytesAsync(espShim, shimBytes);
                Assert.Equal(espCopy, fileWriter.ReobserveLinuxEspFileCopy(expected, espCopy));
                WriteSeedFile(@"\EFI\BOOT\grub.cfg", Encoding.ASCII.GetBytes(bootPayload.GrubConfig));
                var kernelPath = Path.Join(_directory, "reviewed-kernel-source");
                var initrdPath = Path.Join(_directory, "reviewed-initrd-source");
                var changedPath = Path.Join(_directory, "changed-installer-source");
                try
                {
                    await File.WriteAllTextAsync(kernelPath, "kernel fixture");
                    await File.WriteAllTextAsync(initrdPath, "raw initrd fixture");
                    await File.WriteAllTextAsync(changedPath, "changed installer source");
                    Assert.Throws<InvalidDataException>(() =>
                        fileWriter.CopyPinnedKernel(content, bootSpec, changedPath));
                    Assert.False(File.Exists(Path.Join(payloadRoot, "igloo-boot", "linux")));
                    Assert.Throws<InvalidDataException>(() =>
                        fileWriter.CopyInjectedPinnedInitrd(content, bootSpec, changedPath));
                    Assert.False(File.Exists(Path.Join(payloadRoot, "igloo-boot", "initrd")));
                    copied.Add(fileWriter.CopyPinnedKernel(content, bootSpec, kernelPath));
                    copied.Add(fileWriter.CopyInjectedPinnedInitrd(content, bootSpec, initrdPath));
                    var injected = await File.ReadAllBytesAsync(Path.Join(payloadRoot,
                        "igloo-boot", "initrd"));
                    Assert.StartsWith("raw initrd fixture", Encoding.ASCII.GetString(injected),
                        StringComparison.Ordinal);
                    Assert.True(injected.Length > "raw initrd fixture".Length);
                }
                finally
                {
                    File.Delete(kernelPath);
                    File.Delete(changedPath);
                }
                WriteSeedFile(@"\igloo-seed-identity.json", Encoding.ASCII.GetBytes("{}"));
                var receipt = new ReviewedInstallerSeedReceiptV1(expected.GenerationId,
                    expected.Payload, expected.LinuxEsp, null, content.BootSpecSha256,
                    copied.ToImmutableArray(), []);
                var aliasReceipt = receipt with { PayloadFiles = receipt.PayloadFiles.Add(
                    new CanonicalFileIdentityV1(expected.Payload.VolumeGuid, @"\alias. ", 1,
                        new string('A', 64))) };
                Assert.Throws<InvalidDataException>(() => service.VerifyAndPersistReviewedSeedContent(
                    content, bootPayload, aliasReceipt, bootSpec, 8L * 1024 * 1024 * 1024,
                     isoPath, initrdPath, ReadCopied));
                var wrongKernel = receipt with { PayloadFiles = receipt.PayloadFiles.Select(file =>
                    file.RelativePath == @"\igloo-boot\linux"
                        ? file with { Sha256 = new string('A', 64) }
                        : file).ToImmutableArray() };
                Assert.Throws<InvalidDataException>(() => service.VerifyAndPersistReviewedSeedContent(
                    content, bootPayload, wrongKernel, bootSpec, 8L * 1024 * 1024 * 1024,
                     isoPath, initrdPath, ReadCopied));
                Assert.Throws<InvalidDataException>(() => service.VerifyAndPersistReviewedSeedContent(
                    content, bootPayload, receipt, bootSpec, 8L * 1024 * 1024 * 1024,
                     changedIsoPath, initrdPath, ReadCopied));
                await File.WriteAllTextAsync(initrdPath, "changed raw initrd fixture");
                Assert.Throws<InvalidDataException>(() => service.VerifyAndPersistReviewedSeedContent(
                    content, bootPayload, receipt, bootSpec, 8L * 1024 * 1024 * 1024,
                    isoPath, initrdPath, ReadCopied));
                await File.WriteAllTextAsync(initrdPath, "raw initrd fixture");
                var installedInitrdPath = Path.Join(payloadRoot, "igloo-boot", "initrd");
                var installedInitrd = await File.ReadAllBytesAsync(installedInitrdPath);
                var forgedInitrd = (byte[])installedInitrd.Clone();
                forgedInitrd[^1] ^= 1;
                await File.WriteAllBytesAsync(installedInitrdPath, forgedInitrd);
                var forgedInitrdReceipt = receipt with { PayloadFiles = receipt.PayloadFiles.Select(file =>
                    file.RelativePath == @"\igloo-boot\initrd"
                        ? file with { Sha256 = Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(forgedInitrd)) }
                        : file).ToImmutableArray() };
                Assert.Throws<InvalidDataException>(() => service.VerifyAndPersistReviewedSeedContent(
                    content, bootPayload, forgedInitrdReceipt, bootSpec, 8L * 1024 * 1024 * 1024,
                    isoPath, initrdPath, ReadCopied));
                await File.WriteAllBytesAsync(installedInitrdPath, installedInitrd);
                var shimPath = Path.Join(payloadRoot, "EFI", "BOOT", "BOOTX64.EFI");
                var forgedShim = Encoding.ASCII.GetBytes("forged shim");
                await File.WriteAllBytesAsync(shimPath, forgedShim);
                var forgedReceipt = receipt with { PayloadFiles = receipt.PayloadFiles.Select(file =>
                    file.RelativePath == @"\EFI\BOOT\BOOTX64.EFI"
                        ? file with
                        {
                            Length = forgedShim.Length,
                            Sha256 = Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(forgedShim)),
                        }
                        : file).ToImmutableArray() };
                Assert.Throws<InvalidDataException>(() => service.VerifyAndPersistReviewedSeedContent(
                    content, bootPayload, forgedReceipt, bootSpec, 8L * 1024 * 1024 * 1024,
                     isoPath, initrdPath, ReadCopied));
                await File.WriteAllBytesAsync(shimPath, Encoding.ASCII.GetBytes("signed shim"));
                var grubPath = Path.Join(payloadRoot, "EFI", "BOOT", "grub.cfg");
                await File.WriteAllTextAsync(grubPath, "wrong ESP binding");
                Assert.Throws<InvalidDataException>(() => service.VerifyAndPersistReviewedSeedContent(
                    content, bootPayload, receipt, bootSpec, 8L * 1024 * 1024 * 1024,
                     isoPath, initrdPath, ReadCopied));
                Assert.Equal(PreparationState.CreatedAndVerified, store.Reopen(reference).State);
                await File.WriteAllBytesAsync(grubPath, Encoding.ASCII.GetBytes(bootPayload.GrubConfig));
                var staged = service.VerifyAndPersistReviewedSeedContent(content, bootPayload,
                     receipt, bootSpec, 8L * 1024 * 1024 * 1024, isoPath, initrdPath, ReadCopied);
                var reopened = store.Reopen(staged);
                Assert.Equal(PreparationState.ContentStagedAndVerified, reopened.State);
                Assert.Equal(receipt.GenerationId, reopened.ReviewedSeedContent!.GenerationId);
                Assert.Equal(receipt.Payload, reopened.ReviewedSeedContent.Payload);
                Assert.Equal(receipt.LinuxEsp, reopened.ReviewedSeedContent.LinuxEsp);
                Assert.Equal(receipt.PayloadFiles.ToArray(),
                    reopened.ReviewedSeedContent.PayloadFiles.ToArray());
                var seedGrub = receipt.PayloadFiles.Single(file =>
                    file.RelativePath == @"\igloo-boot\grubx64.efi");
                var espGrub = fileWriter.CopyExactLinuxEspFile(expected,
                    PreparedLayoutRules.GrubPath,
                    Path.Join(payloadRoot, "igloo-boot", "grubx64.efi"),
                    seedGrub.Length, seedGrub.Sha256);
                var espConfigSource = Path.Join(_directory, "reviewed-linux-esp-grub.cfg");
                var espConfigBytes = Encoding.ASCII.GetBytes(bootPayload.LinuxEspGrubConfig);
                await File.WriteAllBytesAsync(espConfigSource, espConfigBytes);
                var espConfig = fileWriter.CopyExactLinuxEspFile(expected,
                    ReviewedSeedFileWriter.LinuxEspGrubConfigPath, espConfigSource,
                    espConfigBytes.Length, Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(espConfigBytes)));
                Assert.Throws<InvalidDataException>(() => fileWriter.BindReviewedLinuxEspBootFiles(
                    content, staged, bootSpec, isoPath, initrdPath,
                    espCopy, espGrub, espConfig));
                var espPrefixConfig = fileWriter.CopyExactLinuxEspFile(expected,
                    ReviewedSeedFileWriter.LinuxEspCompiledPrefixConfigPath, espConfigSource,
                    espConfigBytes.Length, Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(espConfigBytes)));
                var bootBinding = fileWriter.BindReviewedLinuxEspBootFiles(content, staged,
                    bootSpec, isoPath, initrdPath,
                    espCopy, espGrub, espConfig, espPrefixConfig);
                Assert.Equal(expected, bootBinding.Storage);
                Assert.Equal(staged, bootBinding.SeedCheckpoint);
                Assert.Equal(PreparedLayoutRules.ShimPath, bootBinding.Shim.RelativePath);
                Assert.Equal(PreparedLayoutRules.GrubPath, bootBinding.Grub.RelativePath);
                Assert.Equal(espPrefixConfig.File, bootBinding.CompiledPrefixConfig);
                var espConfigTarget = Path.Join(linuxEspRoot, "EFI", "iGloo", "grub.cfg");
                await File.WriteAllTextAsync(espConfigTarget, "changed boot config");
                Assert.Throws<InvalidDataException>(() => fileWriter.BindReviewedLinuxEspBootFiles(
                    content, staged, bootSpec,
                    isoPath, initrdPath, espCopy, espGrub, espConfig, espPrefixConfig));
                await File.WriteAllBytesAsync(espConfigTarget, espConfigBytes);
                var espPrefixTarget = Path.Join(linuxEspRoot, "boot", "grub", "grub.cfg");
                await File.WriteAllTextAsync(espPrefixTarget, "changed compiled prefix config");
                Assert.Throws<InvalidDataException>(() => fileWriter.BindReviewedLinuxEspBootFiles(
                    content, staged, bootSpec,
                    isoPath, initrdPath, espCopy, espGrub, espConfig, espPrefixConfig));
                await File.WriteAllBytesAsync(espPrefixTarget, espConfigBytes);
                Assert.Equal(bootBinding, fileWriter.BindReviewedLinuxEspBootFiles(content,
                    staged, bootSpec,
                    isoPath, initrdPath, espCopy, espGrub, espConfig, espPrefixConfig));
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    DirectInstallService.PrepareReviewedAsync(service, content, staged,
                        bootSpec, 8L * 1024 * 1024 * 1024, isoPath, initrdPath,
                        ReadCopied, expectedBootBinding: bootBinding with
                        { Grub = bootBinding.Grub with { Sha256 = new string('0', 64) } }));
                var boundClosed = await Assert.ThrowsAsync<NotSupportedException>(() =>
                    DirectInstallService.PrepareReviewedAsync(service, content, staged,
                        bootSpec, 8L * 1024 * 1024 * 1024, isoPath, initrdPath,
                        ReadCopied, expectedBootBinding: bootBinding));
                Assert.Contains("Dedicated iGloo/Linux ESP preparation is blocked",
                    boundClosed.Message, StringComparison.Ordinal);
                File.Delete(espConfigSource);
                var forgedReference = store.PersistAndReopen(reopened with
                {
                    ReviewedSeedContent = forgedReceipt,
                });
                Assert.Throws<InvalidDataException>(() => fileWriter.BindReviewedLinuxEspBootFiles(
                    content, forgedReference, bootSpec,
                    isoPath, initrdPath, espCopy, espGrub, espConfig, espPrefixConfig));
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    DirectInstallService.PrepareReviewedAsync(service, content,
                        forgedReference, bootSpec, 8L * 1024 * 1024 * 1024,
                         isoPath, initrdPath, ReadCopied));
                await File.WriteAllTextAsync(shimPath, "changed on OEMDRV");
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    DirectInstallService.PrepareReviewedAsync(service, content, staged,
                         bootSpec, 8L * 1024 * 1024 * 1024, isoPath, initrdPath, ReadCopied));
                await File.WriteAllTextAsync(shimPath, "signed shim");
                var closed = await Assert.ThrowsAsync<NotSupportedException>(() =>
                    DirectInstallService.PrepareReviewedAsync(service, content, staged,
                         bootSpec, 8L * 1024 * 1024 * 1024, isoPath, initrdPath, ReadCopied));
                Assert.Contains("Dedicated iGloo/Linux ESP preparation is blocked",
                    closed.Message, StringComparison.Ordinal);

                // Re-enter through the ordered internal handoff on a fresh ESP.
                // It copies only after the persisted seed and current target have
                // been reobserved, then returns a plan whose declaration is closed.
                File.Delete(espShim);
                File.Delete(Path.Join(linuxEspRoot, "EFI", "iGloo", "grubx64.efi"));
                File.Delete(espConfigTarget);
                File.Delete(espPrefixTarget);
                var bootSnapshot = CommunityRecoveryFixture.Exact();
                var bootEntries = Enumerable.Range(0, 256).Select(index =>
                    bootSnapshot.Firmware.BootEntries.SingleOrDefault(entry => entry.Index == index) ??
                    EfiRecoveryParser.ParseBootEntry((ushort)index,
                        bootSnapshot.Firmware.BootNextRaw)).ToImmutableArray();
                var evidence = new BootRegistrationEvidence(
                    Observations.Available(bootSnapshot.Binding.Value with
                    { TargetVolume = expected.LinuxEsp, WindowsVolume =
                        plan.ReviewedIntent!.ShrinkSource with
                        { SizeBytes = plan.ReviewedIntent.ResultingSourceBytes } }), 3,
                    bootSnapshot.BootConfiguration,
                    bootSnapshot.Firmware with { BootEntries = bootEntries }, bootSnapshot.Rtc!);
                var wrongTarget = evidence with { Binding = Observations.Available(
                    evidence.Binding.Value with { TargetVolume = expected.Payload }) };
                Assert.Throws<InvalidDataException>(() => fileWriter.CopyAndPlanReviewedLinuxEspBoot(
                    content, staged, bootSpec, 8L * 1024 * 1024 * 1024,
                    isoPath, initrdPath, () => wrongTarget, Guid.NewGuid()));
                Assert.False(File.Exists(espShim));
                var wrongWindowsBootVolume = evidence with { Binding = Observations.Available(
                    evidence.Binding.Value with { WindowsVolume = evidence.Binding.Value.WindowsVolume with
                        { VolumeGuid = Guid.NewGuid() } }) };
                Assert.NotEqual(evidence.Binding.Value.WindowsVolume,
                    wrongWindowsBootVolume.Binding.Value.WindowsVolume);
                Assert.True(CanonicalRecoveryIdentity.IsValid(
                    wrongWindowsBootVolume.Binding.Value.WindowsVolume));
                Assert.Throws<InvalidDataException>(() => fileWriter.CopyAndPlanReviewedLinuxEspBoot(
                    content, staged, bootSpec, 8L * 1024 * 1024 * 1024,
                    isoPath, initrdPath, () => wrongWindowsBootVolume, Guid.NewGuid()));
                Assert.False(File.Exists(espShim));
                var wrongPartitionNumber = evidence with { TargetPartitionNumber = 4 };
                Assert.Throws<InvalidDataException>(() => fileWriter.CopyAndPlanReviewedLinuxEspBoot(
                    content, staged, bootSpec, 8L * 1024 * 1024 * 1024,
                    isoPath, initrdPath, () => wrongPartitionNumber, Guid.NewGuid()));
                Assert.False(File.Exists(espShim));
                var unsupportedBoot = evidence with
                {
                    Rtc = evidence.Rtc with { KeyPresent = Observations.Available(false) },
                };
                var unavailablePlan = fileWriter.CopyAndPlanReviewedLinuxEspBoot(
                    content, staged, bootSpec, 8L * 1024 * 1024 * 1024,
                    isoPath, initrdPath, () => unsupportedBoot, Guid.NewGuid());
                Assert.Equal(ObservationAvailability.Unsupported, unavailablePlan.Availability);
                Assert.Equal("BootPlanRtcKeyCreationNotCovered", unavailablePlan.Code);
                Assert.False(File.Exists(espShim));
                Assert.Empty(Directory.EnumerateFiles(linuxEspRoot, "*",
                    SearchOption.AllDirectories));
                var evidenceReads = 0;
                Assert.Throws<InvalidOperationException>(() => fileWriter.CopyAndPlanReviewedLinuxEspBoot(
                    content, staged, bootSpec, 8L * 1024 * 1024 * 1024,
                    isoPath, initrdPath, () =>
                    {
                        if (++evidenceReads == 2)
                            storage.LinuxEspPartitionNumberOverride = 4;
                        return evidence;
                    }, Guid.NewGuid()));
                Assert.Equal(2, evidenceReads);
                Assert.False(File.Exists(espShim));
                Assert.Empty(Directory.EnumerateFiles(linuxEspRoot, "*",
                    SearchOption.AllDirectories));
                storage.LinuxEspPartitionNumberOverride = null;
                var tamperReads = 0;
                var unresolved = Assert.Throws<ReviewedLinuxEspRestorationException>(() =>
                    fileWriter.CopyAndPlanReviewedLinuxEspBoot(
                        content, staged, bootSpec, 8L * 1024 * 1024 * 1024,
                        isoPath, initrdPath, () =>
                        {
                            if (++tamperReads == 2)
                                File.WriteAllText(espShim, "another process changed this copy");
                            return evidence;
                        }, Guid.NewGuid()));
                Assert.Contains("Inspect the target before retrying", unresolved.Message,
                    StringComparison.Ordinal);
                Assert.True(File.Exists(espShim));
                Assert.False(File.Exists(Path.Join(linuxEspRoot, "EFI", "iGloo",
                    "grubx64.efi")));
                File.Delete(espShim);
                Directory.Delete(Path.Join(linuxEspRoot, "EFI", "iGloo"));
                Directory.Delete(Path.Join(linuxEspRoot, "EFI"));
                Directory.Delete(Path.Join(linuxEspRoot, "boot", "grub"));
                Directory.Delete(Path.Join(linuxEspRoot, "boot"));
                Assert.Empty(Directory.EnumerateFileSystemEntries(linuxEspRoot));

                var cancelledReads = 0;
                using var cancelCopy = new CancellationTokenSource();
                Assert.Throws<OperationCanceledException>(() =>
                    fileWriter.CopyAndPlanReviewedLinuxEspBoot(
                        content, staged, bootSpec, 8L * 1024 * 1024 * 1024,
                        isoPath, initrdPath, () =>
                        {
                            if (++cancelledReads == 2) cancelCopy.Cancel();
                            return evidence;
                        }, Guid.NewGuid(), cancelCopy.Token));
                Assert.Equal(2, cancelledReads);
                Assert.Empty(Directory.EnumerateFileSystemEntries(linuxEspRoot));
                var planned = fileWriter.CopyAndPlanReviewedLinuxEspBoot(
                    content, staged, bootSpec, 8L * 1024 * 1024 * 1024,
                    isoPath, initrdPath, () => evidence, Guid.NewGuid());
                Assert.Equal(ObservationAvailability.Available, planned.Availability);
                Assert.Equal(PreparedLayoutRules.ShimPath, planned.Value.PlannedLoaderPath);
                Assert.Equal(ObservationAvailability.Unsupported, planned.Value.Declaration.Availability);
                Assert.Equal("signed shim", await File.ReadAllTextAsync(espShim));
                Assert.Equal("signed grub", await File.ReadAllTextAsync(Path.Join(linuxEspRoot,
                    "EFI", "iGloo", "grubx64.efi")));
                Assert.Equal(espConfigBytes, await File.ReadAllBytesAsync(espConfigTarget));
                Assert.Equal(espConfigBytes, await File.ReadAllBytesAsync(espPrefixTarget));
                Assert.Throws<InvalidOperationException>(() => fileWriter.CopyAndPlanReviewedLinuxEspBoot(
                    content, staged, bootSpec, 8L * 1024 * 1024 * 1024,
                    isoPath, initrdPath, () => evidence, Guid.NewGuid()));
            }
            finally
            {
                File.Delete(Path.Join(_directory, "reviewed-initrd-source"));
                Directory.Delete(payloadRoot, recursive: true);
                Directory.Delete(linuxEspRoot, recursive: true);
            }
            await File.WriteAllTextAsync(Path.Join(stage, "igloo-initrd", "guard.sh"), "changed guard");
            Assert.Throws<InvalidDataException>(() =>
                DirectInstallService.BuildReviewedBootPayload(content, bootSpec));
            await File.WriteAllTextAsync(Path.Join(stage, "igloo-initrd", "guard.sh"),
                "ESP " + expected.LinuxEsp.PartitionGuid.ToString("D"));

            var reviewedPreseed = await File.ReadAllTextAsync(preseedPath);
            storage.ExtraPartition = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DirectInstallService.StageReviewedProviderContentAsync(service, reference,
                    provider, bootSpec, 8L * 1024 * 1024 * 1024, stage));
            Assert.Equal(reviewedPreseed, await File.ReadAllTextAsync(preseedPath));
            storage.ExtraPartition = false;
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest with
            {
                Hardware = manifest.Hardware with { TargetDiskGuid = Guid.NewGuid() },
            }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.StageReviewedProviderContentAsync(service, reference,
                    provider, bootSpec, 8L * 1024 * 1024 * 1024, stage));
            Assert.Equal(reviewedPreseed, await File.ReadAllTextAsync(preseedPath));
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
            var oemDrvHelpers = new[]
            {
                "igloo-partman.sh", "igloo-bound-free-choices.sh",
                "igloo-bound-free-do-option.sh", "igloo-bound-efi-fstab.sh",
            };
            foreach (var helper in oemDrvHelpers)
                await File.WriteAllTextAsync(Path.Join(stage, helper), "ordinary helper");
            provider.BootExtraPaths = [];
            provider.ReturnedExtraPaths = oemDrvHelpers;
            var oemDrvBootSpec = provider.GetInstallerBootSpec();
            var oemDrvContent = await DirectInstallService.StageReviewedProviderContentAsync(
                service, reference, provider, oemDrvBootSpec, 8L * 1024 * 1024 * 1024, stage);
            Assert.All(oemDrvHelpers, helper => Assert.Contains(oemDrvContent.StagedFiles,
                file => file.RelativePath == helper));
            var pinnedPreseed = await File.ReadAllTextAsync(preseedPath);
            provider.BootExtraPaths = ["igloo-initrd/guard.sh"];
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.StageReviewedProviderContentAsync(service, reference,
                    provider, provider.GetInstallerBootSpec(), 8L * 1024 * 1024 * 1024, stage));
            Assert.Equal(pinnedPreseed, await File.ReadAllTextAsync(preseedPath));
            provider.BootExtraPaths = [];
            provider.ReturnedExtraPaths = [.. oemDrvHelpers, "igloo-unbound.sh"];
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.StageReviewedProviderContentAsync(service, reference,
                    provider, provider.GetInstallerBootSpec(), 8L * 1024 * 1024 * 1024, stage));
            Assert.Equal(pinnedPreseed, await File.ReadAllTextAsync(preseedPath));
            provider.ReturnedExtraPaths = ["files/selected.txt"];
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.StageReviewedProviderContentAsync(service, reference,
                    provider, provider.GetInstallerBootSpec(), 8L * 1024 * 1024 * 1024, stage));
            Assert.Equal(pinnedPreseed, await File.ReadAllTextAsync(preseedPath));
            await File.WriteAllTextAsync(preseedPath, "tampered preseed");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DirectInstallService.PrepareReviewedAsync(service, content, reference,
                    bootSpec, 8L * 1024 * 1024 * 1024, "unavailable.iso", "unavailable-initrd"));
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    [Fact]
    public void MeasuredStagingBytesCannotOverflowAReviewedPayloadAllocation()
    {
        var plan = Plan(preCreateRoot: false);
        var bootSpec = new InstallerBootSpec
        {
            MenuTitle = "Reviewed fixture", KernelCmdline = "fixture",
            IsoSha256 = new string('A', 64), PreCreateRootPartition = false,
        };
        Assert.True(ReviewedPartitionCreationService.RequireMeasuredSizing(plan, bootSpec,
            8000 * (long)MiB, 0).OemDrvBytes < (long)plan.Space.Allocations.Single(
                a => a.Role == PreparationRole.Payload).SizeBytes);
        Assert.Throws<InvalidOperationException>(() =>
            ReviewedPartitionCreationService.RequireMeasuredSizing(plan, bootSpec,
                8000 * (long)MiB, 200 * (long)MiB));
    }

    [Fact]
    public void UnreceiptedProviderSuccessPersistsAmbiguityAndCannotBeAdopted()
    {
        var plan = Plan();
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var formatCalls = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) =>
            {
                _ = storage.Create(allocation);
                return new NativePartitionCreateResult(0, null);
            },
            (_, _, _) => { formatCalls++; return 0; }, storage.GptRange, storage.NtfsSize);

        var failure = Assert.Throws<AmbiguousPartitionCreationException>(() =>
            service.CreateAndFormatNext(reference, Shrink(plan)));
        Assert.Equal(PreparationState.AmbiguousLeftover, store.Reopen(failure.Checkpoint!).State);
        Assert.Equal(0, formatCalls);
        Assert.Throws<InvalidOperationException>(() => service.CreateAndFormatNext(reference, Shrink(plan)));
    }

    [Fact]
    public void UnreviewedExtraPartitionStopsBeforeNativeCreate()
    {
        var plan = Plan();
        var storage = new StorageFake(plan) { ExtraPartition = true };
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = store.PersistAndReopen(new(1, PreparationState.Planned, plan, [], null));
        var calls = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, _, _) => { calls++; throw new InvalidOperationException("unexpected native create"); },
            (_, _, _) => 0, storage.GptRange, storage.NtfsSize);

        Assert.Throws<InvalidOperationException>(() => service.CreateAndFormatNext(reference, Shrink(plan)));
        Assert.Equal(0, calls);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("wrong-disk")]
    [InlineData("stale-partition")]
    [InlineData("no-usable-root")]
    public void NativeGptMustConfirmThePostShrinkGapBeforeAnyCreate(string change)
    {
        var plan = Plan(preCreateRoot: false);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var nativeCalls = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, _, _) => { nativeCalls++; throw new InvalidOperationException("unexpected create"); },
            (_, _, _) => { nativeCalls++; throw new InvalidOperationException("unexpected format"); },
            diskNumber =>
            {
                var gpt = storage.GptRange(diskNumber);
                return change switch
                {
                    "wrong-disk" => gpt with { DiskGuid = Guid.NewGuid() },
                    "stale-partition" => gpt with { Partitions = gpt.Partitions.SetItem(1,
                        gpt.Partitions[1] with { SizeBytes = gpt.Partitions[1].SizeBytes + MiB }) },
                    "no-usable-root" => gpt with
                    {
                        UsableLengthBytes = plan.Space.Allocations[^1].OffsetBytes -
                            gpt.FirstUsableOffsetBytes,
                    },
                    _ => throw new InvalidOperationException("Unexpected test case"),
                };
            }, storage.NtfsSize);

        Assert.Throws<InvalidOperationException>(() =>
            service.CreateAndFormatNext(reference, Shrink(plan)));
        Assert.Equal(0, nativeCalls);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void FailedFormatRetainsCreatedReceiptAndPersistsAmbiguity()
    {
        var plan = Plan();
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => storage.Create(allocation), (_, _, _) => 5, storage.GptRange, storage.NtfsSize);

        var failure = Assert.Throws<AmbiguousPartitionCreationException>(() =>
            service.CreateAndFormatNext(reference, Shrink(plan)));
        var checkpoint = store.Reopen(failure.Checkpoint!);
        Assert.Equal(PreparationState.AmbiguousLeftover, checkpoint.State);
        Assert.Single(checkpoint.StorageOwnership!.CreatedPartitions);
        Assert.Empty(checkpoint.CreationReceipts);
    }

    [Fact]
    public void ChangedPriorFormattedVolumeStopsBeforeTheNextNativeCreate()
    {
        var plan = Plan();
        var shrink = Shrink(plan);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => storage.Create(allocation),
            (_, allocation, _) => { storage.Format(allocation.Role); return 0; }, storage.GptRange, storage.NtfsSize);

        reference = service.CreateAndFormatNext(reference, shrink);
        reference = service.CreateAndFormatNext(reference, shrink);
        Assert.Equal(2, storage.Created.Count);

        storage.ChangePayloadFileSystem = true;
        Assert.Throws<InvalidOperationException>(() => service.CreateAndFormatNext(reference, shrink));
        Assert.Equal(2, storage.Created.Count);
        Assert.Equal(PreparationState.CreationInProgress, store.Reopen(reference).State);
    }

    [Fact]
    public void PriorFormattedVolumeChangingDuringCreateMarksNewPartitionAmbiguous()
    {
        var plan = Plan();
        var shrink = Shrink(plan);
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) =>
            {
                var result = storage.Create(allocation);
                if (allocation.Role == PreparationRole.LinuxRoot)
                    storage.ChangePayloadFileSystem = true;
                return result;
            },
            (_, allocation, _) => { storage.Format(allocation.Role); return 0; }, storage.GptRange, storage.NtfsSize);

        reference = service.CreateAndFormatNext(reference, shrink);
        reference = service.CreateAndFormatNext(reference, shrink);
        var failure = Assert.Throws<AmbiguousPartitionCreationException>(() =>
            service.CreateAndFormatNext(reference, shrink));

        Assert.Equal(3, storage.Created.Count);
        var ambiguous = store.Reopen(failure.Checkpoint!);
        Assert.Equal(PreparationState.AmbiguousLeftover, ambiguous.State);
        Assert.Equal(2, ambiguous.StorageOwnership!.CreatedPartitions.Length);
        Assert.Equal(2, ambiguous.CreationReceipts.Length);
        Assert.Throws<InvalidOperationException>(() =>
            service.CreateAndFormatNext(reference, shrink));
    }

    [Fact]
    public void CancellationAfterDurableCreateStopsBeforeFormatWithAmbiguousCheckpoint()
    {
        var plan = Plan();
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        using var cancellation = new CancellationTokenSource();
        var formats = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) =>
            {
                var result = storage.Create(allocation);
                cancellation.Cancel();
                return result;
            },
            (_, _, _) => { formats++; return 0; }, storage.GptRange, storage.NtfsSize);

        var failure = Assert.Throws<AmbiguousPartitionCreationException>(() =>
            service.CreateAndFormatNext(reference, Shrink(plan), cancellation.Token));
        var checkpoint = store.Reopen(failure.Checkpoint!);
        Assert.IsType<OperationCanceledException>(failure.InnerException);
        Assert.Contains("manual reconciliation", failure.Message, StringComparison.Ordinal);
        Assert.Equal(PreparationState.AmbiguousLeftover, checkpoint.State);
        Assert.Single(checkpoint.StorageOwnership!.CreatedPartitions);
        Assert.Empty(checkpoint.CreationReceipts);
        Assert.Equal(0, formats);
        Assert.Throws<InvalidOperationException>(() =>
            service.CreateAndFormatNext(failure.Checkpoint!, Shrink(plan)));
    }

    [Fact]
    public void ReviewedPartialOwnershipRequiresTheDurableExactShrinkReceipt()
    {
        var plan = Plan();
        var shrink = Shrink(plan);
        var allocation = plan.Space.Allocations[0];
        var created = new PreparedGptPartitionV1(plan.TargetDisk, Guid.NewGuid(),
            allocation.PartitionType, allocation.OffsetBytes, allocation.SizeBytes);
        var ownership = new PreparedStorageOwnershipV1(plan.GenerationId,
            [PreparedStorageOwnership.PartitionOf(plan.WindowsEsp),
             PreparedStorageOwnership.PartitionOf(shrink.After)],
            [PreparedStorageOwnership.VerifyCreation(plan, allocation.Role, A(created), A(created)).Value]);
        var checkpoint = new PreparationCheckpointV1(1, PreparationState.CreationInProgress,
            plan, [], null) { StorageOwnership = ownership };
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));

        Assert.Throws<InvalidDataException>(() => store.PersistAndReopen(checkpoint));
        Assert.Throws<InvalidDataException>(() => store.PersistAndReopen(checkpoint with
        {
            ShrinkReceipt = shrink,
            StorageOwnership = ownership with { PreservedPartitions =
                [PreparedStorageOwnership.PartitionOf(plan.WindowsEsp),
                 PreparedStorageOwnership.PartitionOf(shrink.Before)] },
        }));
        var reopened = store.Reopen(store.PersistAndReopen(checkpoint with { ShrinkReceipt = shrink }));
        Assert.Equal(shrink, reopened.ShrinkReceipt);
    }

    [Fact]
    public void OutOfOrderPartialReceiptCannotAuthorizeTheNextCreate()
    {
        var plan = Plan();
        var shrink = Shrink(plan);
        var payload = plan.Space.Allocations.Single(a => a.Role == PreparationRole.Payload);
        var volume = new CanonicalVolumeIdentityV1(plan.TargetDisk, Guid.NewGuid(), Guid.NewGuid(),
            payload.PartitionType, payload.OffsetBytes, payload.SizeBytes, payload.FileSystem);
        var partition = PreparedStorageOwnership.PartitionOf(volume);
        var checkpoint = new PreparationCheckpointV1(1, PreparationState.CreationInProgress,
            plan, [PreparedLayoutRules.VerifyCreation(plan, payload.Role, A(volume), A(volume)).Value], null)
        {
            ShrinkReceipt = shrink,
            StorageOwnership = new PreparedStorageOwnershipV1(plan.GenerationId,
                [PreparedStorageOwnership.PartitionOf(plan.WindowsEsp),
                 PreparedStorageOwnership.PartitionOf(shrink.After)],
                [PreparedStorageOwnership.VerifyCreation(plan, payload.Role, A(partition), A(partition)).Value]),
        };
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var reference = store.PersistAndReopen(checkpoint);
        var nativeCalls = 0;
        var service = new ReviewedPartitionCreationService(new StorageFake(plan), store,
            (_, _, _) => { nativeCalls++; throw new InvalidOperationException("unexpected create"); },
            (_, _, _) => { nativeCalls++; throw new InvalidOperationException("unexpected format"); });

        Assert.Throws<InvalidOperationException>(() => service.CreateAndFormatNext(reference, shrink));
        Assert.Equal(0, nativeCalls);
    }

    [Fact]
    public void IncompleteOwnershipArraysCannotBePersistedAsAReviewedCheckpoint()
    {
        var plan = Plan();
        var store = new PreparedLayoutStore(new CommunityRecoveryArtifactStore(_directory));
        var checkpoint = new PreparationCheckpointV1(1, PreparationState.CreationInProgress,
            plan, [], null)
        {
            ShrinkReceipt = Shrink(plan),
            StorageOwnership = new PreparedStorageOwnershipV1(plan.GenerationId, default, default),
        };

        Assert.Throws<InvalidDataException>(() => store.PersistAndReopen(checkpoint));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void FailedDurableWriteAfterNativeCreationRemainsClassifiedAmbiguous()
    {
        var plan = Plan();
        var storage = new StorageFake(plan);
        var store = new PreparedLayoutStore(new FailingAfterShrinkStore(_directory));
        var reference = ShrunkCheckpoint(store, plan);
        var formats = 0;
        var service = new ReviewedPartitionCreationService(storage, store,
            (_, allocation, _) => storage.Create(allocation),
            (_, _, _) => { formats++; return 0; }, storage.GptRange, storage.NtfsSize);

        var failure = Assert.Throws<AmbiguousPartitionCreationException>(() =>
            service.CreateAndFormatNext(reference, Shrink(plan)));
        Assert.Null(failure.Checkpoint);
        Assert.Contains("could not be persisted", failure.Message, StringComparison.Ordinal);
        Assert.Single(storage.Created);
        Assert.Equal(0, formats);
        Assert.Single(Directory.GetFiles(_directory));
    }

    private static PreparationCheckpointReference ShrunkCheckpoint(
        PreparedLayoutStore store, PreparationPlanV1 plan) =>
        store.PersistAndReopen(new PreparationCheckpointV1(1,
            PreparationState.Planned, plan, [], null) { ShrinkReceipt = Shrink(plan) });

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

    private static PreparationPlanV1 Plan(bool preCreateRoot = true, ulong isoBytes = 0,
        ulong linuxBytes = 8000 * MiB)
    {
        var disk = new CanonicalDiskIdentityV1("eui.reviewed-create", 8, 17,
            new("cedb8d63-4d19-4ab1-a201-6866ad68e492"), 100000 * MiB, 512, 4096);
        var esp = new CanonicalVolumeIdentityV1(disk,
            new("62a874d0-d6f7-434b-bcd4-10634f7e3f12"),
            new("da65544d-7194-4d44-a021-4bd06309b1c1"),
            PreparationSpacePlanning.EspType, MiB, 260 * MiB, "FAT32");
        var source = new CanonicalVolumeIdentityV1(disk,
            new("b1b45b64-4444-4899-bb81-1cc6dad0768b"),
            new("c24a453c-e3b2-4ec5-af87-11e4aac82ec7"),
            PreparationSpacePlanning.BasicDataType, 261 * MiB, 59739 * MiB, "NTFS");
        var space = PreparationSpacePlanning.Plan(new(40000 * MiB, 60000 * MiB,
            linuxBytes, 1000 * MiB, isoBytes, 4096, preCreateRoot)).Value;
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
        private readonly List<(PreparationAllocationV1 Allocation, Guid PartitionGuid, Guid VolumeGuid)> _created = [];
        private readonly HashSet<PreparationRole> _formatted = [];
        public bool ExtraPartition { get; set; }
        public bool ChangePayloadFileSystem { get; set; }
        public uint? LinuxEspPartitionNumberOverride { get; set; }
        public bool Resized { get; set; } = true;
        public bool NtfsSizeChanged { get; set; }
        public bool NtfsUnavailable { get; set; }
        public int? NtfsChangeAtRead { get; set; }
        public int NtfsReads { get; private set; }
        public Action? OnNextIdentityRead { get; set; }
        public List<(PreparationAllocationV1 Allocation, Guid PartitionGuid, Guid VolumeGuid)> Created => _created;

        public ReviewedGptUsableRangeV1 GptRange(uint diskNumber)
        {
            Assert.Equal(2U, diskNumber);
            var source = plan.ReviewedIntent!.ShrinkSource;
            var partitions = new[]
            {
                new ReviewedGptPartitionV1(plan.WindowsEsp.PartitionGuid,
                    plan.WindowsEsp.PartitionType, plan.WindowsEsp.OffsetBytes,
                    plan.WindowsEsp.SizeBytes),
                new ReviewedGptPartitionV1(source.PartitionGuid, source.PartitionType,
                    source.OffsetBytes, Resized ? plan.ReviewedIntent.ResultingSourceBytes : source.SizeBytes),
            }.Concat(_created.Select(part => new ReviewedGptPartitionV1(part.PartitionGuid,
                part.Allocation.PartitionType, part.Allocation.OffsetBytes,
                part.Allocation.SizeBytes))).ToList();
            if (ExtraPartition)
                partitions.Add(new(Guid.NewGuid(), PreparationSpacePlanning.BasicDataType,
                    98000 * MiB, MiB));
            var first = 34UL * plan.TargetDisk.LogicalSectorSize;
            var end = plan.TargetDisk.SizeBytes - 33UL * plan.TargetDisk.LogicalSectorSize;
            return new(plan.TargetDisk.GptDiskGuid, first, end - first,
                (uint)partitions.Count + 1, partitions.ToImmutableArray());
        }

        public ReviewedNtfsSizeV1 NtfsSize(Guid volumeGuid)
        {
            Assert.Equal(plan.ReviewedIntent!.ShrinkSource.VolumeGuid, volumeGuid);
            NtfsReads++;
            if (NtfsUnavailable)
                throw new InvalidOperationException("Fixture NTFS size unavailable.");
            var bytes = (Resized ? plan.ReviewedIntent.ResultingSourceBytes :
                plan.ReviewedIntent.ShrinkSource.SizeBytes) +
                (NtfsSizeChanged || NtfsChangeAtRead is { } threshold && NtfsReads >= threshold
                    ? MiB : 0);
            return new(bytes, 12345, plan.TargetDisk.LogicalSectorSize, 4096);
        }

        public NativePartitionCreateResult Create(PreparationAllocationV1 allocation)
        {
            var guid = Guid.NewGuid();
            _created.Add((allocation, guid, Guid.NewGuid()));
            return new(0, PartitionRow(allocation, guid, checked((uint)(_created.Count + 2))));
        }

        public void Format(PreparationRole role) => _formatted.Add(role);

        public Observation<WindowsStorageSnapshot> ReadIdentitySnapshot()
        {
            var onRead = OnNextIdentityRead;
            OnNextIdentityRead = null;
            onRead?.Invoke();
            var disk = plan.TargetDisk;
            var source = plan.ReviewedIntent!.ShrinkSource;
            var esp = plan.WindowsEsp;
            var diskRow = new DiskObservation(A(2U), A(disk.UniqueId), A(disk.UniqueIdFormat),
                A("fixture-serial"), A(disk.BusType), A("Fixture disk"), A(disk.SizeBytes), A(2U),
                A(disk.GptDiskGuid), A(disk.LogicalSectorSize), A(disk.PhysicalSectorSize));
            var partitions = ImmutableArray.CreateBuilder<PartitionObservation>();
            partitions.Add(new(A(2U), A(1U), A(esp.PartitionGuid), A(esp.PartitionType),
                A(esp.OffsetBytes), A(esp.SizeBytes), A(true), A(false), A(false),
                Missing<char>(), A(ImmutableArray<string>.Empty)));
            partitions.Add(new(A(2U), A(2U), A(source.PartitionGuid), A(source.PartitionType),
                A(source.OffsetBytes), A(Resized ? plan.ReviewedIntent.ResultingSourceBytes : source.SizeBytes), A(false), A(true),
                A(false), A('C'), A(ImmutableArray<string>.Empty)));
            var volumes = ImmutableArray.CreateBuilder<VolumeObservation>();
            volumes.Add(new(A(esp.VolumeGuid), A(esp.PartitionGuid), A("FAT32"), A("System"),
                Missing<char>(), A("OK"), A(false)));
            volumes.Add(new(A(source.VolumeGuid), A(source.PartitionGuid), A("NTFS"), A("Windows"),
                A('C'), A("OK"), A(false)));
            for (var i = 0; i < _created.Count; i++)
            {
                var (allocation, guid, volumeGuid) = _created[i];
                var number = allocation.Role == PreparationRole.LinuxEsp &&
                    LinuxEspPartitionNumberOverride is { } changed ? changed : (uint)(i + 3);
                partitions.Add(new(A(2U), A(number), A(guid), A(allocation.PartitionType),
                    A(allocation.OffsetBytes), A(allocation.SizeBytes), A(false), A(false), A(false),
                    Missing<char>(), A(ImmutableArray<string>.Empty)));
                if (_formatted.Contains(allocation.Role))
                    volumes.Add(new(A(volumeGuid), A(guid), A(ChangePayloadFileSystem &&
                        allocation.Role == PreparationRole.Payload ? "exFAT" : allocation.FileSystem),
                        A("Igloo"), Missing<char>(), A("OK"), A(false)));
            }
            if (ExtraPartition)
                partitions.Add(new(A(2U), A(99U), A(Guid.NewGuid()), A(PreparationSpacePlanning.BasicDataType),
                    A(98000 * MiB), A(MiB), A(false), A(false), A(false),
                    Missing<char>(), A(ImmutableArray<string>.Empty)));
            return A(new WindowsStorageSnapshot([diskRow], partitions.ToImmutable(), volumes.ToImmutable()));
        }

        public WindowsStorageBatch ReadDisks() => new([Row("fixture://exact-disk",
            ("Number", 2U), ("UniqueId", plan.TargetDisk.UniqueId),
            ("UniqueIdFormat", plan.TargetDisk.UniqueIdFormat), ("BusType", plan.TargetDisk.BusType),
            ("Guid", plan.TargetDisk.GptDiskGuid.ToString("B")), ("Size", plan.TargetDisk.SizeBytes),
            ("LogicalSectorSize", plan.TargetDisk.LogicalSectorSize),
            ("PhysicalSectorSize", plan.TargetDisk.PhysicalSectorSize), ("PartitionStyle", 2U))]);

        public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null,
            bool efiOnly = false) => partitionNumber == 2
                ? new([Row("fixture://exact-windows-source",
                    ("DiskNumber", 2U), ("PartitionNumber", 2U),
                    ("Guid", plan.ReviewedIntent!.ShrinkSource.PartitionGuid.ToString("B")),
                    ("GptType", plan.ReviewedIntent.ShrinkSource.PartitionType.ToString("B")),
                    ("Offset", plan.ReviewedIntent.ShrinkSource.OffsetBytes),
                    ("Size", Resized ? plan.ReviewedIntent.ResultingSourceBytes : plan.ReviewedIntent.ShrinkSource.SizeBytes),
                    ("IsBoot", true), ("DriveLetter", "C:"))])
                : new(_created.Select((part, i) =>
                    PartitionRow(part.Allocation, part.PartitionGuid, (uint)(i + 3))).ToArray());

        private static WindowsStorageRow PartitionRow(PreparationAllocationV1 allocation,
            Guid guid, uint number) => Row("fixture://created-partition",
                ("DiskNumber", 2U), ("PartitionNumber", number), ("Guid", guid.ToString("B")),
                ("GptType", allocation.PartitionType.ToString("B")), ("Offset", allocation.OffsetBytes),
                ("Size", allocation.SizeBytes));

        private static WindowsStorageRow Row(string path, params (string Key, object? Value)[] values) =>
            new(values.ToImmutableDictionary(p => p.Key, p => p.Value,
                StringComparer.OrdinalIgnoreCase), path);

        public WindowsStorageBatch ReadVolumes(char driveLetter) => throw new NotSupportedException();
        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition,
            bool explicitParameters = false) => new(Row("fixture://supported-size",
                ("ReturnValue", 0U), ("SizeMin", 35000 * MiB),
                ("SizeMax", plan.ReviewedIntent!.ShrinkSource.SizeBytes)));
    }

    private sealed class BitLockerFake : IWindowsBitLockerReader
    {
        public Observation<BitLockerVolumeObservation> ReadExactVolume(VolumeObservation volume) =>
            A(new BitLockerVolumeObservation(volume.VolumeGuid.Value, A('C'),
                A(0U), A(0U), A(0U), A(0U)));
        public WindowsStorageBatch ReadByDriveLetter(char driveLetter) => throw new NotSupportedException();
    }

    private sealed class FailingAfterShrinkStore(string directory) : ICommunityRecoveryArtifactStore
    {
        private readonly CommunityRecoveryArtifactStore _inner = new(directory);
        private int _writes;

        public void PersistNew(Guid artifactId, ReadOnlySpan<byte> artifact)
        {
            if (++_writes > 1) throw new IOException("Simulated durable write failure.");
            _inner.PersistNew(artifactId, artifact);
        }

        public byte[] Reopen(Guid artifactId) => _inner.Reopen(artifactId);
    }

    private sealed class FailingSecondWriteStore(string directory) : ICommunityRecoveryArtifactStore
    {
        private readonly CommunityRecoveryArtifactStore _inner = new(directory);
        private int _writes;

        public void PersistNew(Guid artifactId, ReadOnlySpan<byte> artifact)
        {
            if (++_writes == 2) throw new IOException("Simulated exact-receipt write failure.");
            _inner.PersistNew(artifactId, artifact);
        }

        public byte[] Reopen(Guid artifactId) => _inner.Reopen(artifactId);
    }

    private sealed class ReviewedProviderFake : IDistroPlugin, IReviewedInstallerConfigProvider
    {
        public string Id { get; set; } = "debian";
        public DistroMetadata Metadata { get; } = new()
        {
            DisplayName = "Debian fixture", Description = "Reviewed fixture",
            DefaultDesktopEnvironment = "GNOME", InstallerType = InstallerType.DebianInstaller,
            IsoDownloadUrl = new Uri("https://example.invalid/debian.iso"),
            IsoSha256 = new string('A', 64),
        };
        public ReviewedInstallerFreeExtentV1? LastExtent { get; private set; }
        public Action? AfterRender { get; set; }
        public byte[]? LastConfigBytes { get; private set; }
        public byte[]? LastExtraBytes { get; private set; }
        public string ConfigFileName { get; set; } = "preseed.cfg";
        public string IsoPin { get; set; } = new string('A', 64);
        public bool IncludeStage2 { get; set; }
        public bool CopyFullIso { get; set; }
        public bool UseIsoBoot { get; set; }
        public bool UseOemDrvConfig { get; set; }
        public IReadOnlyList<string> BootExtraPaths { get; set; } = ["igloo-initrd/guard.sh"];
        public IReadOnlyList<string> ReturnedExtraPaths { get; set; } = ["igloo-initrd/guard.sh"];
        public void MutateRenderedContents()
        {
            LastConfigBytes![0] = (byte)'X';
            LastExtraBytes![0] = (byte)'X';
        }
        public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report) => [];
        public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest,
            CancellationToken ct = default) => Task.FromResult(new InstallerConfig(
                "preseed.cfg", Encoding.UTF8.GetBytes("ordinary preseed"), []));
        public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default) =>
            Task.FromResult(new AgentPayload([]));
        public InstallerBootSpec GetInstallerBootSpec() => new()
        {
            MenuTitle = "Reviewed fixture", KernelCmdline = "fixture",
            IsoSha256 = IsoPin, ConfigDelivery = UseOemDrvConfig
                ? ConfigDelivery.OemDrvLabel : ConfigDelivery.InjectIntoInitrd,
            InitrdConfigPath = UseOemDrvConfig ? null : ConfigFileName,
            InitrdExtraPaths = UseOemDrvConfig ? [] : BootExtraPaths,
            CopyFullIsoToVolume = CopyFullIso,
            IsoVolumeFileName = CopyFullIso ? "debian-fixture.iso" : null,
            ExtraIsoFiles = IncludeStage2
                ? [new IsoFileStage("images/install.img", "images/install.img", true)]
                : [],
            KernelIsoPaths = UseIsoBoot ? ["EFI/BOOT/BOOTX64.EFI"] : [],
            InitrdIsoPaths = UseIsoBoot ? ["EFI/BOOT/grubx64.efi"] : [],
            KernelUrl = UseIsoBoot ? null : new Uri("https://example.invalid/kernel"),
            InitrdUrl = UseIsoBoot ? null : new Uri("https://example.invalid/initrd"),
            KernelSha256 = UseIsoBoot ? null : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.ASCII.GetBytes("kernel fixture"))),
            InitrdSha256 = UseIsoBoot ? null : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.ASCII.GetBytes("raw initrd fixture"))),
            PreCreateRootPartition = false,
        };
        public Task<InstallerConfig> RenderReviewedInstallerConfigAsync(MigrationManifest manifest,
            ReviewedInstallerFreeExtentV1 installerRoot, CancellationToken ct = default)
        {
            LastExtent = installerRoot;
            LastConfigBytes = Encoding.UTF8.GetBytes("bound offset " + installerRoot.OffsetBytes);
            LastExtraBytes = Encoding.UTF8.GetBytes(
                "ESP " + installerRoot.LinuxEspPartitionGuid.ToString("D"));
            var config = new InstallerConfig(ConfigFileName, LastConfigBytes,
                ReturnedExtraPaths.Select(path => new InstallerConfigExtra(path, LastExtraBytes))
                    .ToArray());
            AfterRender?.Invoke();
            return Task.FromResult(config);
        }
    }

    private sealed class ReviewedOwnedProviderFake(string isoPin, bool useIsoBoot)
        : IDistroPlugin, IReviewedInstallerStorageProvider
    {
        private readonly ReviewedProviderFake _source = new()
        {
            Id = "fedora-kde", IsoPin = isoPin, UseIsoBoot = useIsoBoot,
            UseOemDrvConfig = true,
        };

        public string Id => _source.Id;
        public DistroMetadata Metadata => _source.Metadata;
        public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report) =>
            _source.CheckCompatibility(report);
        public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest,
            CancellationToken ct = default) => _source.RenderInstallerConfigAsync(manifest, ct);
        public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default) =>
            _source.GetAgentPayloadAsync(ct);
        public InstallerBootSpec GetInstallerBootSpec() => _source.GetInstallerBootSpec();

        public Observation<string> RenderReviewedStorage(InstallationOwnershipV1 ownership,
            Guid expectedGeneration, Observation<InstallerRuntimeInventoryV1> inventory) =>
            throw new NotSupportedException("Rendering is outside the read-only entry test.");
        public Task<Observation<InstallerConfig>> RenderReviewedInstallerConfigAsync(
            MigrationManifest manifest, InstallationOwnershipV1 ownership,
            Guid expectedGeneration, Observation<InstallerRuntimeInventoryV1> inventory,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Rendering is outside the read-only entry test.");
        public Task<Observation<InstallerConfig>> RenderDeferredReviewedInstallerConfigAsync(
            MigrationManifest manifest, InstallationOwnershipV1 ownership,
            Guid expectedGeneration, CancellationToken ct = default) =>
            throw new NotSupportedException("Rendering is outside the read-only entry test.");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.GetFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
