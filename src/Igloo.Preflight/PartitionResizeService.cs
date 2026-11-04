using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Igloo.Preflight;

// A native resize was invoked, so a failed status or readback cannot be treated
// as an untouched plan. Carry an exact post-resize receipt when one exists.
#pragma warning disable CA1032
internal sealed class ReviewedShrinkMutationException(
    ReviewedShrinkReceiptV1? verifiedReceipt, Exception inner)
    : InvalidOperationException("The reviewed Windows resize has an uncertain native outcome.", inner)
{
    internal ReviewedShrinkReceiptV1? VerifiedReceipt { get; } = verifiedReceipt;
}

// A native expansion may have happened even when its status or readback fails.
// The independently confirmed original geometry is retained when available.
internal sealed class ReviewedRestoreMutationException(
    CanonicalVolumeIdentityV1? verifiedRestoredVolume, Exception inner)
    : InvalidOperationException("The reviewed Windows restore has an uncertain native outcome.", inner)
{
    internal CanonicalVolumeIdentityV1? VerifiedRestoredVolume { get; } = verifiedRestoredVolume;
}
#pragma warning restore CA1032

[SupportedOSPlatform("windows")]
public sealed class PartitionResizeService : IPartitionResizeService
{
    private readonly IWindowsStorageReader _storage;
    private readonly IWindowsBitLockerReader _bitLocker;
    private readonly Func<WindowsStorageRow, ulong, CancellationToken, uint> _resizeExact;
    private readonly Func<uint, ReviewedGptUsableRangeV1> _gptLayout;
    private readonly Func<Guid, ReviewedNtfsSizeV1> _ntfsSize;

    private readonly ILogger<PartitionResizeService> _logger;

    public PartitionResizeService(ILogger<PartitionResizeService> logger) :
        this(logger, new WindowsStorageReader(), new WindowsBitLockerReader()) { }
    public PartitionResizeService(ILogger<PartitionResizeService> logger, IWindowsStorageReader storage)
        : this(logger, storage, new WindowsBitLockerReader()) { }
    public PartitionResizeService(ILogger<PartitionResizeService> logger, IWindowsStorageReader storage,
        IWindowsBitLockerReader bitLocker)
        : this(logger, storage, bitLocker, ResizeExactViaWmi) { }

    internal PartitionResizeService(ILogger<PartitionResizeService> logger, IWindowsStorageReader storage,
        IWindowsBitLockerReader bitLocker,
        Func<WindowsStorageRow, ulong, CancellationToken, uint> resizeExact)
        : this(logger, storage, bitLocker, resizeExact, ReviewedGptUsableRangeReader.Read) { }

    internal PartitionResizeService(ILogger<PartitionResizeService> logger, IWindowsStorageReader storage,
        IWindowsBitLockerReader bitLocker,
        Func<WindowsStorageRow, ulong, CancellationToken, uint> resizeExact,
        Func<uint, ReviewedGptUsableRangeV1> gptLayout)
        : this(logger, storage, bitLocker, resizeExact, gptLayout,
            ReviewedNtfsSizeReader.Read) { }

    internal PartitionResizeService(ILogger<PartitionResizeService> logger, IWindowsStorageReader storage,
        IWindowsBitLockerReader bitLocker,
        Func<WindowsStorageRow, ulong, CancellationToken, uint> resizeExact,
        Func<uint, ReviewedGptUsableRangeV1> gptLayout,
        Func<Guid, ReviewedNtfsSizeV1> ntfsSize)
    { _logger = logger; _storage = storage; _bitLocker = bitLocker;
        _resizeExact = resizeExact; _gptLayout = gptLayout; _ntfsSize = ntfsSize; }

    //   IPartitionResizeService                        ─

    public Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default)
        => Task.Run(() => GetShrinkableSpace(diskNumber), ct);

    public Task ShrinkAsync(int diskNumber, long linuxSizeBytes,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Shrink(diskNumber, linuxSizeBytes, progress, ct), ct);

    // This is an internal building block for a future reviewed executor. It does not
    // accept an ordinal or select a partition by free space, and the current UI never
    // calls it. The dedicated-ESP preparation gate remains closed.
    internal Task<ReviewedShrinkReceiptV1> ShrinkReviewedAsync(PreparationPlanV1 plan,
        CancellationToken ct = default)
        => Task.Run(() => ShrinkReviewed(plan, ct), ct);

    // Limited to the untouched, shrunk GPT layout in the same reviewed plan.
    // No product caller invokes this, and a geometry readback is not a durable
    // rollback of files, boot state or later partitions.
    internal Task<CanonicalVolumeIdentityV1> RestoreReviewedShrinkAsync(
        PreparedLayoutStore checkpoints, PreparationCheckpointReference reference,
        CancellationToken ct = default)
        => Task.Run(() => RestoreReviewedShrink(checkpoints, reference, ct), ct);

    private CanonicalVolumeIdentityV1 RestoreReviewedShrink(
        PreparedLayoutStore checkpoints, PreparationCheckpointReference reference,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var checkpoint = RequireRestorableCheckpoint(checkpoints, reference);
        var plan = checkpoint.Plan;
        var shrink = checkpoint.ShrinkReceipt!;

        var initial = ObserveReviewedSource(plan, resized: true);
        RequireNativeReviewedLayout(plan, initial.DiskNumber, resized: true);
        if (_ntfsSize(initial.Volume.VolumeGuid) != shrink.NtfsAfter)
            throw new InvalidOperationException("The reviewed NTFS size changed after its durable shrink receipt.");
        RequireReviewedRestoreSizeLimit(initial.PartitionRow, shrink);

        ct.ThrowIfCancellationRequested();
        var current = ObserveReviewedSource(plan, resized: true);
        RequireNativeReviewedLayout(plan, current.DiskNumber, resized: true);
        if (_ntfsSize(current.Volume.VolumeGuid) != shrink.NtfsAfter)
            throw new InvalidOperationException("The reviewed NTFS filesystem changed before restore.");
        if (initial.Volume != current.Volume || initial.DiskNumber != current.DiskNumber ||
            !string.Equals(initial.PartitionRow.ObjectPath, current.PartitionRow.ObjectPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The reviewed Windows restore source changed before mutation.");
        RequireReviewedRestoreSizeLimit(current.PartitionRow, shrink);
        _ = RequireRestorableCheckpoint(checkpoints, reference);
        ct.ThrowIfCancellationRequested();

        Exception? failure = null;
        try
        {
            var returnValue = _resizeExact(current.PartitionRow, shrink.Before.SizeBytes, ct);
            if (returnValue != 0)
                failure = new InvalidOperationException(
                    $"The reviewed Windows restore failed with return code {returnValue}.");
        }
#pragma warning disable CA1031
        catch (Exception error)
#pragma warning restore CA1031
        {
            failure = error;
        }

        CanonicalVolumeIdentityV1? restored = null;
        try
        {
            var providerResult = ObserveReviewedSource(plan, resized: false);
            var reopened = ObserveReviewedSource(plan, resized: false);
            RequireNativeReviewedLayout(plan, reopened.DiskNumber, resized: false);
            var ntfs = _ntfsSize(reopened.Volume.VolumeGuid);
            if (providerResult.Volume != shrink.Before || reopened.Volume != shrink.Before ||
                providerResult.DiskNumber != reopened.DiskNumber || ntfs != shrink.NtfsBefore)
                throw new InvalidOperationException("The reviewed Windows restore has no matching readback.");
            restored = reopened.Volume;
        }
#pragma warning disable CA1031
        catch (Exception readbackFailure)
#pragma warning restore CA1031
        {
            failure = failure is null ? readbackFailure :
                new AggregateException(failure, readbackFailure);
        }

        if (failure is not null)
            throw new ReviewedRestoreMutationException(restored, failure);
        return restored!;
    }

    private static PreparationCheckpointV1 RequireRestorableCheckpoint(
        PreparedLayoutStore checkpoints, PreparationCheckpointReference reference)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(reference);
        var checkpoint = checkpoints.Reopen(reference);
        if (checkpoint.State != PreparationState.Planned || checkpoint.ShrinkReceipt is not { } shrink ||
            !checkpoint.CreationReceipts.IsEmpty || checkpoint.StorageOwnership is not null ||
            checkpoint.Layout is not null || checkpoint.ReviewedSeedContent is not null ||
            PreparedLayoutRules.VerifyShrinkNtfs(checkpoint.Plan, shrink).Availability !=
                ObservationAvailability.Available)
            throw new InvalidOperationException("The durable reviewed shrink checkpoint is not eligible for restore.");
        return checkpoint;
    }

    private void RequireNativeReviewedLayout(PreparationPlanV1 plan, uint diskNumber, bool resized)
    {
        var native = _gptLayout(diskNumber);
        var intent = plan.ReviewedIntent!;
        var expected = intent.BeforePartitions.Select(p =>
            resized && p.PartitionGuid == intent.ShrinkSource.PartitionGuid
                ? p with { SizeBytes = intent.ResultingSourceBytes } : p)
            .OrderBy(p => p.PartitionGuid).ToArray();
        if (native.DiskGuid != plan.TargetDisk.GptDiskGuid ||
            native.Partitions.IsDefaultOrEmpty || native.Partitions.Length != expected.Length ||
            native.PartitionCount < native.Partitions.Length ||
            native.FirstUsableOffsetBytes >= plan.TargetDisk.SizeBytes ||
            native.UsableLengthBytes > plan.TargetDisk.SizeBytes - native.FirstUsableOffsetBytes ||
            !native.Partitions.Select(p => new PreparedGptPartitionV1(plan.TargetDisk,
                    p.PartitionGuid, p.PartitionType, p.OffsetBytes, p.SizeBytes))
                .OrderBy(p => p.PartitionGuid).SequenceEqual(expected))
            throw new InvalidOperationException("The native GPT differs from the reviewed Windows restore layout.");

        var usableEnd = checked(native.FirstUsableOffsetBytes + native.UsableLengthBytes);
        var originalEnd = checked(intent.ShrinkSource.OffsetBytes + intent.ShrinkSource.SizeBytes);
        if (originalEnd > usableEnd || native.Partitions.Any(p =>
                p.OffsetBytes < native.FirstUsableOffsetBytes ||
                p.OffsetBytes > usableEnd || p.SizeBytes > usableEnd - p.OffsetBytes) ||
            native.Partitions.OrderBy(p => p.OffsetBytes).Zip(
                native.Partitions.OrderBy(p => p.OffsetBytes).Skip(1)).Any(pair =>
                pair.First.OffsetBytes + pair.First.SizeBytes > pair.Second.OffsetBytes))
            throw new InvalidOperationException("The original NTFS extent is not free in the native GPT.");
    }

    private void RequireReviewedRestoreSizeLimit(WindowsStorageRow row, ReviewedShrinkReceiptV1 shrink)
    {
        var limit = StorageObservationProjection.SupportedSize(
            _storage.ReadSupportedSize(row, explicitParameters: true));
        if (limit.Availability != ObservationAvailability.Available ||
            limit.Value.Minimum > shrink.Before.SizeBytes ||
            limit.Value.Maximum < shrink.Before.SizeBytes ||
            shrink.After.SizeBytes >= shrink.Before.SizeBytes ||
            shrink.Before.SizeBytes % PreparationSpacePlanning.Alignment != 0)
            throw new InvalidOperationException("The exact reviewed Windows restore size is unsupported.");
    }

    private ReviewedShrinkReceiptV1 ShrinkReviewed(PreparationPlanV1 plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var review = PreparedLayoutRules.VerifyReviewedIntent(plan);
        if (review.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException($"The reviewed shrink intent is invalid: {review.Code}.");
        var intent = plan.ReviewedIntent!;
        var initial = ObserveReviewedSource(plan, resized: false);
        var initialNtfs = _ntfsSize(initial.Volume.VolumeGuid);
        RequireNtfsSize(initialNtfs, initial.Volume);
        RequireReviewedSizeLimit(initial.PartitionRow, intent);

        // Reopen the whole identity, the exact WMI row, NTFS dirty state and
        // BitLocker state immediately before the native mutation.
        ct.ThrowIfCancellationRequested();
        var current = ObserveReviewedSource(plan, resized: false);
        if (_ntfsSize(current.Volume.VolumeGuid) != initialNtfs)
            throw new InvalidOperationException("The reviewed NTFS filesystem changed before shrink.");
        if (initial.Volume != current.Volume ||
            !string.Equals(initial.PartitionRow.ObjectPath, current.PartitionRow.ObjectPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The reviewed Windows shrink source changed before mutation.");
        RequireReviewedSizeLimit(current.PartitionRow, intent);
        ct.ThrowIfCancellationRequested();

        Exception? failure = null;
        try
        {
            var returnValue = _resizeExact(current.PartitionRow, intent.ResultingSourceBytes, ct);
            if (returnValue != 0)
                failure = new InvalidOperationException(
                    $"The reviewed Windows resize failed with return code {returnValue}.");
        }
        // Once the native call begins, even a provider exception or cancellation
        // may follow a changed partition. Read it back before reporting failure.
#pragma warning disable CA1031
        catch (Exception error)
#pragma warning restore CA1031
        {
            failure = error;
        }

        ReviewedShrinkReceiptV1? verifiedReceipt = null;
        try
        {
            // These are separate fresh acquisitions; WMI's Resize return only
            // reports status and cannot serve as a canonical geometry receipt.
            var providerResult = ObserveReviewedSource(plan, resized: true).Volume;
            var independentReadback = ObserveReviewedSource(plan, resized: true).Volume;
            var receipt = PreparedLayoutRules.VerifyShrink(plan,
                Observations.Available(providerResult), Observations.Available(independentReadback));
            if (receipt.Availability != ObservationAvailability.Available)
                throw new InvalidOperationException(
                    $"The reviewed Windows shrink has no matching receipt: {receipt.Code}.");
            var afterNtfs = _ntfsSize(independentReadback.VolumeGuid);
            RequireNtfsSize(afterNtfs, independentReadback);
            verifiedReceipt = receipt.Value with { NtfsBefore = initialNtfs, NtfsAfter = afterNtfs };
            if (PreparedLayoutRules.VerifyShrinkNtfs(plan, verifiedReceipt).Availability !=
                ObservationAvailability.Available)
                throw new InvalidOperationException("The reviewed Windows shrink has no NTFS size receipt.");
        }
#pragma warning disable CA1031
        catch (Exception readbackFailure)
#pragma warning restore CA1031
        {
            failure = failure is null ? readbackFailure :
                new AggregateException(failure, readbackFailure);
        }

        if (failure is not null)
            throw new ReviewedShrinkMutationException(verifiedReceipt, failure);
        return verifiedReceipt!;
    }

    private static void RequireNtfsSize(ReviewedNtfsSizeV1 size,
        CanonicalVolumeIdentityV1 volume)
    {
        if (size.SizeBytes != volume.SizeBytes ||
            size.BytesPerSector != volume.Disk.LogicalSectorSize ||
            size.BytesPerCluster == 0 ||
            size.BytesPerCluster % size.BytesPerSector != 0)
            throw new InvalidOperationException("The exact NTFS filesystem size does not match its partition.");
    }

    private sealed record ReviewedSourceObservation(CanonicalVolumeIdentityV1 Volume,
        WindowsStorageRow PartitionRow, uint DiskNumber);

    private ReviewedSourceObservation ObserveReviewedSource(PreparationPlanV1 plan, bool resized)
    {
        var intent = plan.ReviewedIntent!;
        var expected = resized
            ? intent.ShrinkSource with { SizeBytes = intent.ResultingSourceBytes }
            : intent.ShrinkSource;
        var observation = _storage.ReadIdentitySnapshot();
        if (observation.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("The reviewed Windows storage inventory is unavailable.");
        var snapshot = observation.Value;
        var source = CanonicalRecoveryIdentity.Bind(observation, expected.VolumeGuid);
        var esp = CanonicalRecoveryIdentity.Bind(observation, plan.WindowsEsp.VolumeGuid);
        if (source.Availability != ObservationAvailability.Available || source.Value != expected ||
            esp.Availability != ObservationAvailability.Available || esp.Value != plan.WindowsEsp)
            throw new InvalidOperationException("The reviewed Windows shrink source or ESP changed.");

        var sourcePartitions = snapshot.Partitions.Where(p =>
            p.PartitionGuid.Availability == ObservationAvailability.Available &&
            p.PartitionGuid.Value == expected.PartitionGuid).ToArray();
        if (sourcePartitions.Length != 1 ||
            sourcePartitions[0].DiskNumber.Availability != ObservationAvailability.Available ||
            sourcePartitions[0].Number.Availability != ObservationAvailability.Available ||
            sourcePartitions[0].IsBoot.Availability != ObservationAvailability.Available ||
            !sourcePartitions[0].IsBoot.Value ||
            sourcePartitions[0].DriveLetter.Availability != ObservationAvailability.Available ||
            !char.IsAsciiLetter(sourcePartitions[0].DriveLetter.Value))
            throw new InvalidOperationException("The reviewed Windows boot partition is not uniquely observed.");
        var sourcePartition = sourcePartitions[0];
        var diskNumber = sourcePartition.DiskNumber.Value;
        var partitionNumber = sourcePartition.Number.Value;
        if (diskNumber > int.MaxValue || partitionNumber > int.MaxValue ||
            snapshot.Partitions.Any(p => p.DiskNumber.Availability != ObservationAvailability.Available))
            throw new InvalidOperationException("The reviewed Windows partition inventory is incomplete.");

        var expectedPartitions = intent.BeforePartitions.Select(p =>
            resized && p.PartitionGuid == expected.PartitionGuid
                ? p with { SizeBytes = intent.ResultingSourceBytes } : p)
            .OrderBy(p => p.PartitionGuid).ToArray();
        var currentPartitions = snapshot.Partitions.Where(p => p.DiskNumber.Value == diskNumber)
            .Select(p =>
            {
                if (p.PartitionGuid.Availability != ObservationAvailability.Available ||
                    p.GptType.Availability != ObservationAvailability.Available ||
                    p.Offset.Availability != ObservationAvailability.Available ||
                    p.Size.Availability != ObservationAvailability.Available)
                    throw new InvalidOperationException("The reviewed Windows GPT inventory is incomplete.");
                return new PreparedGptPartitionV1(plan.TargetDisk, p.PartitionGuid.Value,
                    p.GptType.Value, p.Offset.Value, p.Size.Value);
            }).OrderBy(p => p.PartitionGuid).ToArray();
        if (!currentPartitions.SequenceEqual(expectedPartitions))
            throw new InvalidOperationException("The reviewed Windows GPT layout changed.");

        var volumes = snapshot.Volumes.Where(v => v.VolumeGuid.Availability == ObservationAvailability.Available &&
            v.VolumeGuid.Value == expected.VolumeGuid).ToArray();
        if (volumes.Length != 1 ||
            volumes[0].DirtyBitSet.Availability != ObservationAvailability.Available ||
            volumes[0].DirtyBitSet.Value ||
            volumes[0].DriveLetter.Availability != ObservationAvailability.Available ||
            volumes[0].DriveLetter.Value != sourcePartition.DriveLetter.Value)
            throw new InvalidOperationException("The reviewed Windows NTFS volume is dirty or unavailable.");
        var bitLocker = _bitLocker.ReadExactVolume(volumes[0]);
        if (bitLocker.Availability != ObservationAvailability.Available ||
            bitLocker.Value.VolumeGuid != expected.VolumeGuid ||
            bitLocker.Value.ConversionStatus.Availability != ObservationAvailability.Available ||
            bitLocker.Value.ConversionStatus.Value != 0 ||
            bitLocker.Value.ProtectionStatus.Availability != ObservationAvailability.Available ||
            bitLocker.Value.ProtectionStatus.Value != 0 ||
            bitLocker.Value.LockStatus.Availability != ObservationAvailability.Available ||
            bitLocker.Value.LockStatus.Value != 0)
            throw new InvalidOperationException("The reviewed Windows NTFS volume is not proven decrypted and unlocked.");

        var rows = _storage.ReadPartitions((int)diskNumber, (int)partitionNumber).RowsOrThrow().ToArray();
        if (rows.Length != 1 || !SameReviewedRow(rows[0], sourcePartition, expected, diskNumber, partitionNumber))
            throw new InvalidOperationException("The reviewed Windows WMI partition changed.");
        return new ReviewedSourceObservation(source.Value, rows[0], diskNumber);
    }

    private static bool SameReviewedRow(WindowsStorageRow row, PartitionObservation partition,
        CanonicalVolumeIdentityV1 expected, uint diskNumber, uint partitionNumber)
    {
        if (string.IsNullOrWhiteSpace(row.ObjectPath)) return false;
        var observed = StorageObservationProjection.Partition(row);
        return observed.DiskNumber.Availability == ObservationAvailability.Available &&
            observed.DiskNumber.Value == diskNumber &&
            observed.Number.Availability == ObservationAvailability.Available &&
            observed.Number.Value == partitionNumber &&
            observed.PartitionGuid.Availability == ObservationAvailability.Available &&
            observed.PartitionGuid.Value == expected.PartitionGuid &&
            observed.GptType.Availability == ObservationAvailability.Available &&
            observed.GptType.Value == expected.PartitionType &&
            observed.Offset.Availability == ObservationAvailability.Available &&
            observed.Offset.Value == expected.OffsetBytes &&
            observed.Size.Availability == ObservationAvailability.Available &&
            observed.Size.Value == expected.SizeBytes &&
            observed.IsBoot.Availability == ObservationAvailability.Available &&
            observed.IsBoot.Value &&
            observed.DriveLetter.Availability == ObservationAvailability.Available &&
            observed.DriveLetter.Value == partition.DriveLetter.Value;
    }

    private void RequireReviewedSizeLimit(WindowsStorageRow row, ReviewedPreparationIntentV1 intent)
    {
        var limit = StorageObservationProjection.SupportedSize(_storage.ReadSupportedSize(row, explicitParameters: true));
        if (limit.Availability != ObservationAvailability.Available ||
            limit.Value.Maximum < intent.ShrinkSource.SizeBytes ||
            limit.Value.Minimum > intent.ResultingSourceBytes ||
            intent.ResultingSourceBytes >= intent.ShrinkSource.SizeBytes ||
            intent.ResultingSourceBytes % PreparationSpacePlanning.Alignment != 0)
            throw new InvalidOperationException("The exact reviewed Windows shrink size is unsupported.");
    }

    private static uint ResizeExactViaWmi(WindowsStorageRow selected, ulong resultingBytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(selected.ObjectPath))
            throw new InvalidOperationException("The selected Windows partition has no native object path.");
        using var target = new ManagementObject(selected.ObjectPath);
        target.Get();
        foreach (var key in new[] { "DiskNumber", "PartitionNumber", "Guid", "GptType", "Offset", "Size", "IsBoot", "DriveLetter" })
            if (!Equals(selected[key], target[key]))
                throw new InvalidOperationException("The selected native Windows partition changed before resize.");
        var supported = StorageObservationProjection.SupportedSize(WindowsStorageReader.ReadSupportedSize(target,
            explicitParameters: true));
        if (supported.Availability != ObservationAvailability.Available ||
            resultingBytes < supported.Value.Minimum || resultingBytes > supported.Value.Maximum)
            throw new InvalidOperationException("The native Windows partition no longer supports the requested size.");
        ct.ThrowIfCancellationRequested();
        using var input = target.GetMethodParameters("Resize");
        input["Size"] = resultingBytes;
        using var result = target.InvokeMethod("Resize", input, null);
        if (result is null)
            throw new InvalidOperationException("The native Windows resize returned no status.");
        return Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
    }

    //   Private helpers                            ─

    private long GetShrinkableSpace(int diskNumber)
    {
        var (selected, shrinkable) = FindNtfsPartition(diskNumber);
        if (selected is null) return 0;
        RequireDecryptedExactBootVolume(diskNumber, selected);
        return shrinkable;
    }

    private void Shrink(int diskNumber, long linuxSizeBytes,
        IProgress<string>? progress, CancellationToken ct)
    {
        const long MiB = 1024L * 1024;

        progress?.Report("Querying Windows partition layout…");
        _logger.LogInformation(
            "Partition resize: disk {Disk}, need {LinuxMiB} MiB for Linux",
            diskNumber, linuxSizeBytes / MiB);

        var (selected, shrinkable) = FindNtfsPartition(diskNumber);
        if (selected is null)
            throw new InvalidOperationException(
                $"No shrinkable NTFS partition found on disk {diskNumber}.");

        ct.ThrowIfCancellationRequested();

        if (shrinkable < linuxSizeBytes)
            throw new InvalidOperationException(
                $"Not enough shrinkable space: need {linuxSizeBytes / MiB} MiB, " +
                $"available {shrinkable / MiB} MiB.");

        // A disk ordinal and partition number can be recycled. Refuse a changed
        // partition and require exact disk/volume/BitLocker facts before opening
        // the resize method on the selected WMI object.
        var current = _storage.ReadPartitions(diskNumber,
            Convert.ToInt32(selected["PartitionNumber"], CultureInfo.InvariantCulture))
            .RowsOrThrow().ToArray();
        if (current.Length != 1 || !SameObservedPartition(selected, current[0]))
            throw new InvalidOperationException("The Windows partition changed after selection.");
        var beforeTarget = RequireDecryptedExactBootVolume(diskNumber, selected);

        // Recheck limits before invoking the shared native adapter. The adapter
        // opens the selected object and independently verifies identity and limits.
        var outSizes = _storage.ReadSupportedSize(selected, explicitParameters: true).ValueOrThrow()
            ?? throw new InvalidOperationException("The Windows partition size limits are unavailable.");
        if (Convert.ToUInt32(outSizes["ReturnValue"], CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("The Windows partition size limits are unavailable.");
        var sizeMin = Convert.ToInt64(outSizes["SizeMin"], CultureInfo.InvariantCulture);
        var sizeMax = Convert.ToInt64(outSizes["SizeMax"], CultureInfo.InvariantCulture);
        var currentSize = Convert.ToInt64(selected["Size"], CultureInfo.InvariantCulture);
        if (linuxSizeBytes <= 0 || sizeMin < 0 || sizeMax < currentSize ||
            currentSize <= linuxSizeBytes || sizeMin > currentSize)
            throw new InvalidOperationException("The observed Windows partition size limits are invalid.");

        // New size = current size − linux allocation, aligned down to 1 MiB.
        long newSize = currentSize - linuxSizeBytes;
        newSize = (newSize / MiB) * MiB;          // 1 MiB align

        if (newSize < sizeMin)
            throw new InvalidOperationException("The observed Windows partition cannot provide the requested allocation.");

        _logger.LogInformation(
            "Resizing NTFS partition: {Old} MiB → {New} MiB (freeing {Free} MiB for Linux)",
            currentSize / MiB, newSize / MiB, (currentSize - newSize) / MiB);

        progress?.Report($"Shrinking Windows partition from {currentSize / MiB:N0} MiB to {newSize / MiB:N0} MiB…");

        ct.ThrowIfCancellationRequested();

        var returnVal = _resizeExact(selected, (ulong)newSize, ct);

        if (returnVal != 0)
            throw new InvalidOperationException(
                $"MSFT_Partition.Resize() failed with return code {returnVal}. " +
                "Try running CHKDSK on the Windows partition and retrying.");

        // A successful WMI return is not proof that the intended GPT partition
        // reached the requested geometry. Reobserve its exact disk, partition and
        // volume identities before the caller can boot an installer into the space.
        var resized = _storage.ReadPartitions(diskNumber,
            Convert.ToInt32(selected["PartitionNumber"], CultureInfo.InvariantCulture))
            .RowsOrThrow().ToArray();
        if (resized.Length != 1 || !SameResizedPartition(selected, resized[0], newSize))
            throw new InvalidOperationException(
                "The Windows partition resize has no matching geometry readback; recheck the disk before installation.");
        var afterTarget = RequireDecryptedExactBootVolume(diskNumber, resized[0]);
        if (afterTarget != beforeTarget)
            throw new InvalidOperationException(
                "The Windows disk or boot volume identity changed after resizing; recheck the disk before installation.");

        _logger.LogInformation("Partition resize succeeded");
        progress?.Report("Windows partition shrunk successfully.");
    }

    internal static bool HasCandidateLetter(char letter) => letter != '\0';
    internal static bool ImprovesCandidate(long delta, long best) => delta > best;
    private static bool SamePartitionGuid(WindowsStorageRow selected, WindowsStorageRow current)
    {
        var before = StorageObservationProjection.GuidFact(selected, "Guid");
        var after = StorageObservationProjection.GuidFact(current, "Guid");
        return before.Availability == ObservationAvailability.Available &&
            after.Availability == ObservationAvailability.Available && before.Value == after.Value;
    }

    internal static bool SameObservedPartition(WindowsStorageRow selected, WindowsStorageRow current) =>
        SamePartitionGuid(selected, current) &&
        Equals(selected["DiskNumber"], current["DiskNumber"]) &&
        Equals(selected["PartitionNumber"], current["PartitionNumber"]) &&
        Equals(selected["Offset"], current["Offset"]) &&
        Equals(selected["Size"], current["Size"]) &&
        Equals(selected["GptType"], current["GptType"]) &&
        selected["IsBoot"] is true && current["IsBoot"] is true &&
        Equals(selected["DriveLetter"], current["DriveLetter"]);

    internal static bool SameResizedPartition(WindowsStorageRow selected,
        WindowsStorageRow current, long requestedSize) =>
        requestedSize > 0 && SamePartitionGuid(selected, current) && current["Size"] is not null &&
        Convert.ToInt64(current["Size"], CultureInfo.InvariantCulture) == requestedSize &&
        Equals(selected["DiskNumber"], current["DiskNumber"]) &&
        Equals(selected["PartitionNumber"], current["PartitionNumber"]) &&
        Equals(selected["Offset"], current["Offset"]) &&
        Equals(selected["GptType"], current["GptType"]) &&
        selected["IsBoot"] is true && current["IsBoot"] is true &&
        Equals(selected["DriveLetter"], current["DriveLetter"]);

    private (Guid DiskGuid, Guid PartitionGuid, Guid VolumeGuid)
        RequireDecryptedExactBootVolume(int diskNumber, WindowsStorageRow selected)
    {
        var observed = _storage.ReadIdentitySnapshot();
        if (observed.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("Exact Windows storage identity is unavailable for resizing.");
        var snapshot = observed.Value;
        if (snapshot.Disks.IsDefault || snapshot.Partitions.IsDefault || snapshot.Volumes.IsDefault)
            throw new InvalidOperationException("Exact Windows storage identity is unavailable for resizing.");
        var disk = InstallerDiskIdentity.Observe(snapshot.Disks, checked((uint)diskNumber));
        if (disk.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException($"The selected Windows disk could not be verified ({disk.Code}).");

        var partNumber = Convert.ToUInt32(selected["PartitionNumber"], CultureInfo.InvariantCulture);
        var offset = Convert.ToUInt64(selected["Offset"], CultureInfo.InvariantCulture);
        var size = Convert.ToUInt64(selected["Size"], CultureInfo.InvariantCulture);
        var letter = WmiValues.ToDriveLetter(selected["DriveLetter"]);
        var selectedGuid = StorageObservationProjection.GuidFact(selected, "Guid");
        var selectedType = StorageObservationProjection.GuidFact(selected, "GptType");
        var partitions = snapshot.Partitions.Where(p =>
            p.DiskNumber.Availability == ObservationAvailability.Available &&
            p.DiskNumber.Value == (uint)diskNumber &&
            p.Number.Availability == ObservationAvailability.Available &&
            p.Number.Value == partNumber).ToArray();
        if (partitions.Length != 1 ||
            selectedGuid.Availability != ObservationAvailability.Available ||
            selectedType.Availability != ObservationAvailability.Available ||
            partitions[0].PartitionGuid.Availability != ObservationAvailability.Available ||
            partitions[0].PartitionGuid.Value != selectedGuid.Value ||
            partitions[0].GptType.Availability != ObservationAvailability.Available ||
            partitions[0].GptType.Value != selectedType.Value ||
            partitions[0].Offset.Availability != ObservationAvailability.Available ||
            partitions[0].Offset.Value != offset ||
            partitions[0].Size.Availability != ObservationAvailability.Available ||
            partitions[0].Size.Value != size ||
            partitions[0].IsBoot.Availability != ObservationAvailability.Available ||
            !partitions[0].IsBoot.Value ||
            partitions[0].DriveLetter.Availability != ObservationAvailability.Available ||
            partitions[0].DriveLetter.Value != letter)
            throw new InvalidOperationException("The Windows boot partition identity changed before resizing.");

        var volumes = snapshot.Volumes.Where(v =>
            v.PartitionGuid.Availability == ObservationAvailability.Available &&
            v.PartitionGuid.Value == partitions[0].PartitionGuid.Value).ToArray();
        if (volumes.Length != 1 ||
            volumes[0].VolumeGuid.Availability != ObservationAvailability.Available ||
            volumes[0].VolumeGuid.Value == Guid.Empty ||
            volumes[0].DriveLetter.Availability != ObservationAvailability.Available ||
            volumes[0].DriveLetter.Value != letter ||
            volumes[0].FileSystem.Availability != ObservationAvailability.Available ||
            !string.Equals(volumes[0].FileSystem.Value, "NTFS", StringComparison.OrdinalIgnoreCase) ||
            volumes[0].DirtyBitSet.Availability != ObservationAvailability.Available ||
            volumes[0].DirtyBitSet.Value)
            throw new InvalidOperationException("The Windows boot NTFS volume is unavailable or dirty.");

        var bound = CanonicalRecoveryIdentity.BindForInstallation(observed, volumes[0].VolumeGuid.Value);
        if (bound.Availability != ObservationAvailability.Available ||
            bound.Value.Disk.GptDiskGuid != disk.Value.GptGuid.Value ||
            bound.Value.PartitionGuid != selectedGuid.Value)
            throw new InvalidOperationException("The Windows boot volume has no unique GPT partition and volume binding.");

        var encryption = _bitLocker.ReadExactVolume(volumes[0]);
        if (encryption.Availability != ObservationAvailability.Available ||
            encryption.Value.VolumeGuid != volumes[0].VolumeGuid.Value ||
            encryption.Value.ConversionStatus.Availability != ObservationAvailability.Available ||
            encryption.Value.ConversionStatus.Value != 0 ||
            encryption.Value.ProtectionStatus.Availability != ObservationAvailability.Available ||
            encryption.Value.ProtectionStatus.Value != 0 ||
            encryption.Value.LockStatus.Availability != ObservationAvailability.Available ||
            encryption.Value.LockStatus.Value != 0)
            throw new InvalidOperationException("The exact Windows boot volume is not proven decrypted and unlocked.");
        return (disk.Value.GptGuid.Value, partitions[0].PartitionGuid.Value,
            volumes[0].VolumeGuid.Value);
    }

    internal (WindowsStorageRow? row, long shrinkable) FindNtfsPartition(int diskNumber)
    {
        try
        {
            // Only the single running Windows OS partition is eligible. A lettered
            // data volume with more space must never win a shrink heuristic.
            var boot = _storage.ReadPartitions(diskNumber).RowsOrThrow()
                .Where(p => p["IsBoot"] is true && HasCandidateLetter(WmiValues.ToDriveLetter(p["DriveLetter"])))
                .ToArray();
            if (boot.Length != 1) return (null, 0);
            var selected = boot[0];
            var letter = WmiValues.ToDriveLetter(selected["DriveLetter"]);
            var volumes = _storage.ReadVolumes(letter).RowsOrThrow().ToArray();
            if (volumes.Length != 1 ||
                !string.Equals(volumes[0]["FileSystem"] as string, "NTFS", StringComparison.OrdinalIgnoreCase))
                return (null, 0);
            var supported = _storage.ReadSupportedSize(selected).ValueOrThrow();
            if (supported is null ||
                Convert.ToUInt32(supported["ReturnValue"], CultureInfo.InvariantCulture) != 0)
                return (null, 0);
            var sizeMin = Convert.ToInt64(supported["SizeMin"], CultureInfo.InvariantCulture);
            var sizeMax = Convert.ToInt64(supported["SizeMax"], CultureInfo.InvariantCulture);
            var currentSize = Convert.ToInt64(selected["Size"], CultureInfo.InvariantCulture);
            return sizeMin >= 0 && sizeMax >= currentSize && currentSize > sizeMin
                ? (selected, currentSize - sizeMin) : (null, 0);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException or OverflowException or InvalidCastException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "FindNtfsPartition failed for disk {Disk}", diskNumber);
            return (null, 0);
        }
    }
}

// Query the exact volume GUID without a drive-letter alias. The filesystem
// sector count is independent of MSFT_Partition.Size and the raw GPT table.
[SupportedOSPlatform("windows")]
internal static class ReviewedNtfsSizeReader
{
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const int BasicBufferBytes = 96;

    internal static ReviewedNtfsSizeV1 Read(Guid volumeGuid)
    {
        if (volumeGuid == Guid.Empty)
            throw new InvalidOperationException("The reviewed NTFS volume GUID is absent.");
        using var handle = File.OpenHandle($@"\\?\Volume{{{volumeGuid:D}}}",
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var output = new byte[128];
        if (!DeviceIoControl(handle, FsctlGetNtfsVolumeData, IntPtr.Zero, 0,
                output, checked((uint)output.Length), out var returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The exact NTFS filesystem size is unavailable.");
        return Parse(output.AsSpan(0, checked((int)returned)));
    }

    internal static ReviewedNtfsSizeV1 Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < BasicBufferBytes)
            throw new InvalidDataException("The NTFS volume data is incomplete.");
        var serial = BinaryPrimitives.ReadInt64LittleEndian(data);
        var sectors = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        var clusters = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
        var bytesPerSector = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]);
        var bytesPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]);
        if (sectors <= 0 || clusters <= 0 || bytesPerSector == 0 ||
            bytesPerCluster == 0 || bytesPerCluster % bytesPerSector != 0)
            throw new InvalidDataException("The NTFS volume size fields are invalid.");
        ulong bytes;
        try
        {
            bytes = checked((ulong)sectors * bytesPerSector);
            if (checked((ulong)clusters * bytesPerCluster) > bytes)
                throw new InvalidDataException("The NTFS cluster count exceeds its volume size.");
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("The NTFS volume size overflows.", error);
        }
        return new(bytes, serial, bytesPerSector, bytesPerCluster);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr input, uint inputBytes, [Out] byte[] output, uint outputBytes,
        out uint bytesReturned, IntPtr overlapped);
}
