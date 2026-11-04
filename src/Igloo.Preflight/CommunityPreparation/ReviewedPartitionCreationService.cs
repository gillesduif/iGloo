using System.Collections.Immutable;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;

namespace Igloo.Preflight.CommunityPreparation;

internal sealed record NativePartitionCreateResult(uint ReturnValue, WindowsStorageRow? CreatedPartition);

internal sealed class AmbiguousPartitionCreationException : InvalidOperationException
{
    public AmbiguousPartitionCreationException() { }
    public AmbiguousPartitionCreationException(string message) : base(message) { }
    public AmbiguousPartitionCreationException(string message, Exception inner) : base(message, inner) { }
    internal PreparationCheckpointReference? Checkpoint { get; init; }
}

// Read-only storage handoff for a reviewed installer. Content, boot recovery,
// and installer execution must be bound separately before any write is allowed.
internal sealed record ReviewedInstallerHandoffV1(PreparationCheckpointReference Checkpoint,
    Guid GenerationId, CanonicalDiskIdentityV1 TargetDisk,
    CanonicalVolumeIdentityV1 LinuxEsp, CanonicalVolumeIdentityV1 Payload,
    CanonicalVolumeIdentityV1? Iso, ReviewedInstallerFreeExtentV1 InstallerRoot);

// The owned-root mode retains the Windows CreatePartition receipt instead of
// presenting that partition as an installer free extent. This is read-only
// handoff evidence; Linux must independently resolve and verify it at runtime.
internal sealed record ReviewedOwnedRootHandoffV1(PreparationCheckpointReference Checkpoint,
    Guid GenerationId, CanonicalDiskIdentityV1 TargetDisk,
    CanonicalVolumeIdentityV1 LinuxEsp, CanonicalVolumeIdentityV1 Payload,
    CanonicalVolumeIdentityV1? Iso, PreparedGptPartitionV1 LinuxRoot);
internal sealed record ReviewedOwnedRootBindingV1(ReviewedOwnedRootHandoffV1 Storage,
    InstallationOwnershipV1 Ownership);

internal sealed record ReviewedStagedFileV1(string RelativePath, long SizeBytes, string Sha256);
internal sealed record ReviewedContentHandoffV1(ReviewedInstallerHandoffV1 Storage,
    string StagingDirectory, ImmutableArray<ReviewedStagedFileV1> StagedFiles,
    long StagedBytes, DirectInstallService.DirectInstallSizePlan Sizing,
    string BootSpecSha256);
internal sealed record ReviewedOwnedContentHandoffV1(ReviewedOwnedRootBindingV1 Storage,
    string StagingDirectory, ImmutableArray<ReviewedStagedFileV1> StagedFiles,
    long StagedBytes, DirectInstallService.DirectInstallSizePlan Sizing,
    string BootSpecSha256);

// One exact allocation at a time. This is not registered in the product path;
// DedicatedEspPreparationSupport remains closed. The caller supplies a pinned
// checkpoint reference, never a label, drive letter or discovered leftover.
[SupportedOSPlatform("windows")]
internal sealed class ReviewedPartitionCreationService
{
    private static readonly string[] FormatPartitionKeys =
        ["DiskNumber", "PartitionNumber", "Guid", "GptType", "Offset", "Size"];
    private readonly IWindowsStorageReader _storage;
    private readonly PreparedLayoutStore _checkpoints;
    private readonly Func<WindowsStorageRow, PreparationAllocationV1, CancellationToken, NativePartitionCreateResult> _create;
    private readonly Func<WindowsStorageRow, PreparationAllocationV1, CancellationToken, uint> _format;
    private readonly Func<uint, ReviewedGptUsableRangeV1> _gptRange;
    private readonly Func<Guid, ReviewedNtfsSizeV1> _ntfsSize;

    internal ReviewedPartitionCreationService(IWindowsStorageReader storage, PreparedLayoutStore checkpoints)
        : this(storage, checkpoints, CreateExactViaWmi, FormatExactViaWmi,
            ReviewedGptUsableRangeReader.Read) { }

    internal ReviewedPartitionCreationService(IWindowsStorageReader storage, PreparedLayoutStore checkpoints,
        Func<WindowsStorageRow, PreparationAllocationV1, CancellationToken, NativePartitionCreateResult> create,
        Func<WindowsStorageRow, PreparationAllocationV1, CancellationToken, uint> format)
        : this(storage, checkpoints, create, format, ReviewedGptUsableRangeReader.Read) { }

    internal ReviewedPartitionCreationService(IWindowsStorageReader storage, PreparedLayoutStore checkpoints,
        Func<WindowsStorageRow, PreparationAllocationV1, CancellationToken, NativePartitionCreateResult> create,
        Func<WindowsStorageRow, PreparationAllocationV1, CancellationToken, uint> format,
        Func<uint, ReviewedGptUsableRangeV1> gptRange)
        : this(storage, checkpoints, create, format, gptRange, ReviewedNtfsSizeReader.Read) { }

    internal ReviewedPartitionCreationService(IWindowsStorageReader storage, PreparedLayoutStore checkpoints,
        Func<WindowsStorageRow, PreparationAllocationV1, CancellationToken, NativePartitionCreateResult> create,
        Func<WindowsStorageRow, PreparationAllocationV1, CancellationToken, uint> format,
        Func<uint, ReviewedGptUsableRangeV1> gptRange,
        Func<Guid, ReviewedNtfsSizeV1> ntfsSize)
    {
        _storage = storage;
        _checkpoints = checkpoints;
        _create = create;
        _format = format;
        _gptRange = gptRange;
        _ntfsSize = ntfsSize;
    }

    // This observation has no mutation or execution authority. The installer
    // root has no Windows creation receipt, so derive its whole *current* GPT
    // free region only after all Windows-created partitions were receipted.
    internal ReviewedInstallerFreeExtentV1 ObserveInstallerFreeExtent(
        PreparationCheckpointReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var checkpoint = _checkpoints.Reopen(reference);
        var plan = checkpoint.Plan;
        var root = plan.Space.Allocations.Single(a => a.Role == PreparationRole.LinuxRoot);
        var linuxEsp = plan.Space.Allocations.Single(a => a.Role == PreparationRole.LinuxEsp);
        var owned = checkpoint.StorageOwnership;
        if (checkpoint.State != PreparationState.CreatedAndVerified || root.CreateInWindows ||
            checkpoint.ShrinkReceipt is not { } shrink || owned is null ||
            PreparedLayoutRules.VerifyShrinkNtfs(plan, shrink).Availability != ObservationAvailability.Available ||
            PreparedLayoutRules.VerifyReviewedIntent(plan).Availability != ObservationAvailability.Available ||
            PreparedStorageOwnership.VerifyStructure(new PreparedLayoutV1(1, plan,
                checkpoint.CreationReceipts, "", [])
                { StorageOwnership = owned, ShrinkReceipt = checkpoint.ShrinkReceipt }).Availability !=
                ObservationAvailability.Available)
            throw new InvalidOperationException("The reviewed installer-created root checkpoint is incomplete.");
        var espReceipt = owned.CreatedPartitions.SingleOrDefault(r => r.Role == linuxEsp.Role);
        if (espReceipt is null || espReceipt.Identity.OffsetBytes != linuxEsp.OffsetBytes ||
            espReceipt.Identity.SizeBytes != linuxEsp.SizeBytes)
            throw new InvalidOperationException("The exact prepared Linux ESP receipt is unavailable.");

        var expected = owned.PreservedPartitions.Concat(owned.CreatedPartitions.Select(r => r.Identity)).ToArray();
        var before = ObserveExactInventory(plan, expected);
        RequireReceiptedNtfs(before, shrink);
        ct.ThrowIfCancellationRequested();
        var gpt = _gptRange(before.DiskNumber);
        var after = ObserveExactInventory(plan, expected);
        RequireReceiptedNtfs(after, shrink);
        var gptAfter = _gptRange(after.DiskNumber);
        if (after.DiskNumber != before.DiskNumber ||
            !string.Equals(after.DiskRow.ObjectPath, before.DiskRow.ObjectPath,
                StringComparison.OrdinalIgnoreCase) ||
            gpt.Partitions.IsDefaultOrEmpty || gptAfter.Partitions.IsDefaultOrEmpty ||
            gpt.DiskGuid != gptAfter.DiskGuid ||
            gpt.FirstUsableOffsetBytes != gptAfter.FirstUsableOffsetBytes ||
            gpt.UsableLengthBytes != gptAfter.UsableLengthBytes ||
            gpt.PartitionCount != gptAfter.PartitionCount ||
            !gpt.Partitions.OrderBy(p => p.PartitionGuid).SequenceEqual(
                gptAfter.Partitions.OrderBy(p => p.PartitionGuid)) ||
            gpt.DiskGuid != plan.TargetDisk.GptDiskGuid ||
            gpt.Partitions.Length != expected.Length ||
            !gpt.Partitions.Select(p => new PreparedGptPartitionV1(plan.TargetDisk,
                p.PartitionGuid, p.PartitionType, p.OffsetBytes, p.SizeBytes))
                .OrderBy(p => p.PartitionGuid).SequenceEqual(after.Partitions) ||
            gpt.FirstUsableOffsetBytes % plan.TargetDisk.LogicalSectorSize != 0 ||
            gpt.UsableLengthBytes % plan.TargetDisk.LogicalSectorSize != 0 ||
            gpt.FirstUsableOffsetBytes >= plan.TargetDisk.SizeBytes ||
            gpt.UsableLengthBytes > plan.TargetDisk.SizeBytes - gpt.FirstUsableOffsetBytes)
            throw new InvalidOperationException("The independently observed GPT usable range changed or is incomplete.");

        var usableEnd = checked(gpt.FirstUsableOffsetBytes + gpt.UsableLengthBytes);
        var gaps = new List<(ulong Offset, ulong Size)>();
        var cursor = gpt.FirstUsableOffsetBytes;
        foreach (var partition in after.Partitions.OrderBy(p => p.OffsetBytes))
        {
            if (partition.OffsetBytes < cursor || partition.OffsetBytes > usableEnd ||
                partition.SizeBytes == 0 ||
                partition.SizeBytes > usableEnd - partition.OffsetBytes)
                throw new InvalidOperationException("The target GPT partitions exceed or overlap the usable range.");
            if (partition.OffsetBytes > cursor)
                gaps.Add((cursor, partition.OffsetBytes - cursor));
            cursor = checked(partition.OffsetBytes + partition.SizeBytes);
        }
        if (cursor < usableEnd)
            gaps.Add((cursor, usableEnd - cursor));
        var rootEnd = checked(root.OffsetBytes + root.SizeBytes);
        var matches = gaps.Where(gap => gap.Offset == root.OffsetBytes &&
            gap.Size >= root.SizeBytes && rootEnd <= gap.Offset + gap.Size).ToArray();
        if (matches.Length != 1 || gaps.Count(gap => gap.Size >= root.SizeBytes) != 1)
            throw new InvalidOperationException("The installer root free extent is absent or ambiguous.");
        return new(plan.GenerationId, plan.TargetDisk.GptDiskGuid, espReceipt.Identity.PartitionGuid,
            matches[0].Offset, matches[0].Size, root.SizeBytes);
    }

    internal ReviewedInstallerHandoffV1 ObserveInstallerHandoff(
        PreparationCheckpointReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var checkpoint = _checkpoints.Reopen(reference);
        var plan = checkpoint.Plan;
        if (checkpoint.State != PreparationState.CreatedAndVerified ||
            checkpoint.StorageOwnership is null || checkpoint.ShrinkReceipt is not { } shrink ||
            PreparedLayoutRules.VerifyShrinkNtfs(plan, shrink).Availability != ObservationAvailability.Available ||
            PreparedStorageOwnership.VerifyStructure(new PreparedLayoutV1(1, plan,
                checkpoint.CreationReceipts, "", [])
                { StorageOwnership = checkpoint.StorageOwnership,
                    ShrinkReceipt = checkpoint.ShrinkReceipt }).Availability !=
                ObservationAvailability.Available)
            throw new InvalidOperationException("The reviewed storage handoff checkpoint is incomplete.");

        // The native GPT reader and WMI partition inventory must agree before
        // and after binding formatted volumes. No volume label or letter is
        // accepted as identity, and a root partition must still be absent.
        var root = ObserveInstallerFreeExtent(reference, ct);
        var before = ObserveHandoffVolumes(checkpoint);
        ct.ThrowIfCancellationRequested();
        var rootAfter = ObserveInstallerFreeExtent(reference, ct);
        var after = ObserveHandoffVolumes(checkpoint);
        if (root != rootAfter || before != after ||
            root.LinuxEspPartitionGuid != after.LinuxEsp.PartitionGuid)
            throw new InvalidOperationException("The reviewed installer storage changed during handoff observation.");
        return new(reference, plan.GenerationId, plan.TargetDisk,
            after.LinuxEsp, after.Payload, after.Iso, rootAfter);
    }

    internal ReviewedOwnedRootHandoffV1 ObserveOwnedRootHandoff(
        PreparationCheckpointReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var checkpoint = _checkpoints.Reopen(reference);
        var plan = checkpoint.Plan;
        if (checkpoint.State != PreparationState.CreatedAndVerified ||
            checkpoint.StorageOwnership is not { } owned ||
            checkpoint.ShrinkReceipt is not { } shrink ||
            PreparedLayoutRules.VerifyShrinkNtfs(plan, shrink).Availability != ObservationAvailability.Available ||
            PreparedLayoutRules.VerifyReviewedIntent(plan).Availability !=
                ObservationAvailability.Available ||
            PreparedStorageOwnership.VerifyStructure(new PreparedLayoutV1(1, plan,
                checkpoint.CreationReceipts, "", [])
                { StorageOwnership = owned, ShrinkReceipt = checkpoint.ShrinkReceipt })
                .Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("The reviewed owned-root checkpoint is incomplete.");
        var allocation = plan.Space.Allocations.Single(a => a.Role == PreparationRole.LinuxRoot);
        var receipts = owned.CreatedPartitions.Where(r => r.Role == PreparationRole.LinuxRoot).ToArray();
        if (!allocation.CreateInWindows || allocation.FileSystem.Length != 0 ||
            receipts.Length != 1 ||
            checkpoint.CreationReceipts.Any(r => r.Role == PreparationRole.LinuxRoot))
            throw new InvalidOperationException("The reviewed Windows-owned root receipt is unavailable.");
        var root = receipts[0].Identity;
        if (root.Disk != plan.TargetDisk || root.PartitionType != allocation.PartitionType ||
            root.OffsetBytes != allocation.OffsetBytes || root.SizeBytes != allocation.SizeBytes)
            throw new InvalidOperationException("The reviewed Windows-owned root geometry changed.");

        var expected = owned.PreservedPartitions.Concat(owned.CreatedPartitions.Select(r => r.Identity))
            .ToArray();
        var before = ObserveExactInventory(plan, expected);
        RequireReceiptedNtfs(before, shrink);
        var nativeBefore = _gptRange(before.DiskNumber);
        var volumesBefore = ObserveHandoffVolumes(checkpoint);
        ct.ThrowIfCancellationRequested();
        var after = ObserveExactInventory(plan, expected);
        RequireReceiptedNtfs(after, shrink);
        var nativeAfter = _gptRange(after.DiskNumber);
        var volumesAfter = ObserveHandoffVolumes(checkpoint);
        if (before.DiskNumber != after.DiskNumber ||
            !string.Equals(before.DiskRow.ObjectPath, after.DiskRow.ObjectPath,
                StringComparison.OrdinalIgnoreCase) ||
            !MatchesNativeGpt(nativeBefore, before) ||
            !MatchesNativeGpt(nativeAfter, after) ||
            nativeBefore.DiskGuid != nativeAfter.DiskGuid ||
            nativeBefore.FirstUsableOffsetBytes != nativeAfter.FirstUsableOffsetBytes ||
            nativeBefore.UsableLengthBytes != nativeAfter.UsableLengthBytes ||
            nativeBefore.PartitionCount != nativeAfter.PartitionCount ||
            !nativeBefore.Partitions.OrderBy(p => p.PartitionGuid).SequenceEqual(
                nativeAfter.Partitions.OrderBy(p => p.PartitionGuid)) ||
            volumesBefore != volumesAfter)
            throw new InvalidOperationException("The reviewed owned-root GPT or volumes changed during handoff observation.");
        var usableEnd = checked(nativeAfter.FirstUsableOffsetBytes +
            nativeAfter.UsableLengthBytes);
        if (root.OffsetBytes < nativeAfter.FirstUsableOffsetBytes ||
            root.OffsetBytes > usableEnd || root.SizeBytes > usableEnd - root.OffsetBytes)
            throw new InvalidOperationException("The reviewed Windows-owned root exceeds the native GPT usable range.");
        return new(reference, plan.GenerationId, plan.TargetDisk,
            volumesAfter.LinuxEsp, volumesAfter.Payload, volumesAfter.Iso, root);

        bool MatchesNativeGpt(ReviewedGptUsableRangeV1 native, ExactInventory inventory) =>
            native.DiskGuid == plan.TargetDisk.GptDiskGuid &&
            !native.Partitions.IsDefaultOrEmpty &&
            native.Partitions.Length == expected.Length &&
            native.PartitionCount >= native.Partitions.Length &&
            native.FirstUsableOffsetBytes % plan.TargetDisk.LogicalSectorSize == 0 &&
            native.UsableLengthBytes % plan.TargetDisk.LogicalSectorSize == 0 &&
            native.FirstUsableOffsetBytes < plan.TargetDisk.SizeBytes &&
            native.UsableLengthBytes <= plan.TargetDisk.SizeBytes -
                native.FirstUsableOffsetBytes &&
            native.Partitions.Select(p => new PreparedGptPartitionV1(plan.TargetDisk,
                    p.PartitionGuid, p.PartitionType, p.OffsetBytes, p.SizeBytes))
                .OrderBy(p => p.PartitionGuid).SequenceEqual(inventory.Partitions);
    }

    // Reopen the complete receipted GPT handoff around independent FAT32
    // filesystem-byte observations. A Windows volume GUID is only a locator;
    // it is never substituted for Linux's FAT UUID. The optional NTFS ISO
    // partition has no 64-bit UUID reader here and remains unavailable.
    internal ReviewedOwnedRootBindingV1 ObserveOwnedRootBinding(
        PreparationCheckpointReference reference,
        Func<CanonicalVolumeIdentityV1, Observation<string>>? readFat = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var before = ObserveOwnedRootHandoff(reference, ct);
        if (before.Iso is not null)
            throw new NotSupportedException(
                "The reviewed owned-root ISO partition has no independent NTFS filesystem UUID readback.");
        var checkpoint = _checkpoints.Reopen(reference);
        var reader = readFat ?? (volume => ReviewedFatUuidReader.Read(volume));
        var windowsUuid = RequireUuid(checkpoint.Plan.WindowsEsp);
        var linuxUuid = RequireUuid(before.LinuxEsp);
        var payloadUuid = RequireUuid(before.Payload);
        ct.ThrowIfCancellationRequested();
        var after = ObserveOwnedRootHandoff(reference, ct);
        if (before != after ||
            !string.Equals(windowsUuid, RequireUuid(checkpoint.Plan.WindowsEsp), StringComparison.Ordinal) ||
            !string.Equals(linuxUuid, RequireUuid(after.LinuxEsp), StringComparison.Ordinal) ||
            !string.Equals(payloadUuid, RequireUuid(after.Payload), StringComparison.Ordinal) ||
            new[] { windowsUuid, linuxUuid, payloadUuid }
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new InvalidOperationException(
                "The reviewed owned-root FAT filesystem identity changed during readback.");
        var layout = new PreparedLayoutV1(1, checkpoint.Plan,
            checkpoint.CreationReceipts, payloadUuid, [])
        {
            StorageOwnership = checkpoint.StorageOwnership,
            ShrinkReceipt = checkpoint.ShrinkReceipt,
        };
        if (PreparedStorageOwnership.VerifyStructure(layout).Availability !=
            ObservationAvailability.Available)
            throw new InvalidOperationException(
                "The reviewed owned-root layout no longer matches its partition receipts.");
        var esp = new InstallerEspBindingV1(after.GenerationId, after.TargetDisk,
            new(checkpoint.Plan.WindowsEsp, windowsUuid),
            new(after.LinuxEsp, linuxUuid), new(after.Payload, payloadUuid));
        return new(after, new InstallationOwnershipV1(layout, esp, null));

        string RequireUuid(CanonicalVolumeIdentityV1 volume)
        {
            ct.ThrowIfCancellationRequested();
            var observed = reader(volume);
            if (observed.Availability != ObservationAvailability.Available ||
                observed.Value is not { Length: 9 } value || value[4] != '-' ||
                value.Where((character, index) => index != 4).Any(character =>
                    !Uri.IsHexDigit(character)))
                throw new InvalidOperationException(
                    "The exact reviewed FAT filesystem UUID is unavailable or invalid.");
            return value.ToUpperInvariant();
        }
    }

    // The partition number is an output of the exact GUID/GPT handoff, never
    // a caller-supplied target selector. Firmware needs the current number in
    // its HD() load option, so bind and reobserve it around ESP file copying.
    internal uint ObserveLinuxEspPartitionNumber(ReviewedInstallerHandoffV1 expected,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expected);
        if (ObserveInstallerHandoff(expected.Checkpoint, ct) != expected)
            throw new InvalidOperationException("The reviewed Linux ESP handoff changed before ordinal observation.");
        var checkpoint = _checkpoints.Reopen(expected.Checkpoint);
        var owned = checkpoint.StorageOwnership!;
        var partitions = owned.PreservedPartitions.Concat(owned.CreatedPartitions.Select(r => r.Identity))
            .ToArray();
        var inventory = ObserveExactInventory(checkpoint.Plan, partitions);
        var matches = inventory.Snapshot.Partitions.Where(partition =>
            partition.PartitionGuid.Availability == ObservationAvailability.Available &&
            partition.PartitionGuid.Value == expected.LinuxEsp.PartitionGuid).ToArray();
        if (matches.Length != 1 ||
            matches[0].DiskNumber.Availability != ObservationAvailability.Available ||
            matches[0].DiskNumber.Value != inventory.DiskNumber ||
            matches[0].Number.Availability != ObservationAvailability.Available ||
            matches[0].Number.Value == 0 ||
            PartitionFromObservation(checkpoint.Plan.TargetDisk, matches[0]) !=
            PreparedStorageOwnership.PartitionOf(expected.LinuxEsp))
            throw new InvalidOperationException("The exact reviewed Linux ESP has no unique current partition number.");
        ct.ThrowIfCancellationRequested();
        if (ObserveInstallerHandoff(expected.Checkpoint, ct) != expected)
            throw new InvalidOperationException("The reviewed Linux ESP handoff changed during ordinal observation.");
        return matches[0].Number.Value;
    }

    internal PreparationCheckpointV1 ReopenCreatedCheckpoint(
        PreparationCheckpointReference reference)
    {
        var checkpoint = _checkpoints.Reopen(reference);
        if (checkpoint.State != PreparationState.CreatedAndVerified ||
            checkpoint.Plan.ReviewedIntent is null)
            throw new InvalidOperationException("The reviewed storage checkpoint is not ready for installer content.");
        return checkpoint;
    }

    // Read-only content preflight for the exact storage handoff. The copied
    // bytes and destination still need a fresh comparison at the actual write
    // boundary; this record alone never authorizes preparation or boot setup.
    internal ReviewedContentHandoffV1 ObserveContentHandoff(
        PreparationCheckpointReference reference, ReviewedInstallerHandoffV1 expectedStorage,
        InstallerBootSpec bootSpec, long requestedLinuxBytes, string stagingDirectory,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var before = ObserveInstallerHandoff(reference, ct);
        if (before != expectedStorage)
            throw new InvalidOperationException("The reviewed installer storage handoff changed before content inspection.");
        DirectInstallService.ValidateExternalInstallerMedia(bootSpec);
        var bootSpecSha256 = HashBootSpec(bootSpec);
        var (hashed, total) = HashStagingFiles(stagingDirectory, ct);
        var checkpoint = _checkpoints.Reopen(reference);
        var sizing = RequireMeasuredSizing(checkpoint.Plan, bootSpec, requestedLinuxBytes, total);
        var after = ObserveInstallerHandoff(reference, ct);
        if (after != expectedStorage)
            throw new InvalidOperationException("The reviewed installer storage handoff changed during content inspection.");
        if (!string.Equals(bootSpecSha256, HashBootSpec(bootSpec), StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed installer boot recipe changed during content inspection.");
        return new(expectedStorage, Path.GetFullPath(stagingDirectory), hashed,
            total, sizing, bootSpecSha256);
    }

    internal ReviewedOwnedContentHandoffV1 ObserveOwnedContentHandoff(
        PreparationCheckpointReference reference, ReviewedOwnedRootBindingV1 expectedStorage,
        InstallerBootSpec bootSpec, long requestedLinuxBytes, string stagingDirectory,
        Func<CanonicalVolumeIdentityV1, Observation<string>>? readFat = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!SameOwnedBinding(ObserveOwnedRootBinding(reference, readFat, ct), expectedStorage))
            throw new InvalidOperationException("The reviewed owned-root handoff changed before content inspection.");
        DirectInstallService.ValidateExternalInstallerMedia(bootSpec);
        if (!bootSpec.PreCreateRootPartition)
            throw new InvalidDataException("The reviewed owned-root boot recipe does not create the root in Windows.");
        var bootSpecSha256 = HashBootSpec(bootSpec);
        var (hashed, total) = HashStagingFiles(stagingDirectory, ct);
        var checkpoint = _checkpoints.Reopen(reference);
        var sizing = RequireMeasuredSizing(checkpoint.Plan, bootSpec, requestedLinuxBytes, total);
        if (!SameOwnedBinding(ObserveOwnedRootBinding(reference, readFat, ct), expectedStorage))
            throw new InvalidOperationException("The reviewed owned-root handoff changed during content inspection.");
        if (!string.Equals(bootSpecSha256, HashBootSpec(bootSpec), StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed owned-root boot recipe changed during content inspection.");
        return new(expectedStorage, Path.GetFullPath(stagingDirectory), hashed,
            total, sizing, bootSpecSha256);
    }

    private static bool SameOwnedBinding(ReviewedOwnedRootBindingV1 current,
        ReviewedOwnedRootBindingV1 expected) =>
        current.Storage == expected.Storage &&
        JsonSerializer.SerializeToUtf8Bytes(current.Ownership).AsSpan().SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(expected.Ownership));

    private static (ImmutableArray<ReviewedStagedFileV1> Files, long Bytes) HashStagingFiles(
        string stagingDirectory, CancellationToken ct)
    {
        var staged = DirectInstallService.InspectStagingFiles(stagingDirectory)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        var hashed = ImmutableArray.CreateBuilder<ReviewedStagedFileV1>(staged.Length);
        long total = 0;
        foreach (var file in staged)
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file.SourcePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A reviewed staged file became redirected.");
            string sha256;
            FileStream? source = null;
            try
            {
#pragma warning disable CA2000 // The finally block disposes this stream on every exit path.
                source = File.OpenRead(file.SourcePath);
#pragma warning restore CA2000
                if (source.Length != file.SizeBytes)
                    throw new InvalidDataException("A reviewed staged file changed size.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = source.Read(buffer)) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, count);
                }
                if (source.Position != file.SizeBytes ||
                    (File.GetAttributes(file.SourcePath) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(file.SourcePath).Length != file.SizeBytes)
                    throw new InvalidDataException("A reviewed staged file changed while being hashed.");
                sha256 = Convert.ToHexString(hash.GetHashAndReset());
            }
            finally
            {
                source?.Dispose();
            }
            hashed.Add(new(file.RelativePath, file.SizeBytes, sha256));
            total = checked(total + file.SizeBytes);
        }
        var currentFiles = DirectInstallService.InspectStagingFiles(stagingDirectory)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => (file.RelativePath, file.SizeBytes)).ToArray();
        if (!staged.Select(file => (file.RelativePath, file.SizeBytes)).SequenceEqual(currentFiles))
            throw new InvalidDataException("The reviewed staged file inventory changed during content inspection.");
        return (hashed.MoveToImmutable(), total);
    }

    // The exact Windows writer will call this only after copying the staged
    // installer to the owned OEMDRV volume. It does no write itself. Every
    // claimed file is reopened from its volume GUID, and the resulting receipt
    // is persisted in the existing preparation checkpoint before boot setup.
    internal PreparationCheckpointReference VerifyAndPersistReviewedSeedContent(
        ReviewedContentHandoffV1 content, ReviewedBootPayloadV1 bootPayload,
        ReviewedInstallerSeedReceiptV1 claimed, InstallerBootSpec bootSpec,
        long requestedLinuxBytes, string pinnedIsoPath, string? pinnedRawInitrdPath,
        Func<Guid, string, Observation<CanonicalFileIdentityV1>>? readFile = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(bootPayload);
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentNullException.ThrowIfNull(bootSpec);
        var reference = content.Storage.Checkpoint;
        var checkpoint = _checkpoints.Reopen(reference);
        if (checkpoint.State != PreparationState.CreatedAndVerified ||
            claimed.PayloadFiles.IsDefaultOrEmpty || claimed.IsoFiles.IsDefault)
            throw new InvalidDataException("The reviewed installer seed has no complete created checkpoint or file set.");
        PreparedLayoutStore.RequireReviewedSeedReceiptCapacity(checkpoint,
            claimed.Payload, claimed.LinuxEsp, claimed.Iso, claimed.BootSpecSha256,
            claimed.PayloadFiles.Select(file => file.RelativePath).ToArray(),
            claimed.IsoFiles.Select(file => file.RelativePath).ToArray());
        var storage = ObserveInstallerHandoff(reference, ct);
        var current = ObserveContentHandoff(reference, storage, bootSpec,
            requestedLinuxBytes, content.StagingDirectory, ct);
        var generatedBoot = DirectInstallService.BuildReviewedBootPayload(current, bootSpec, ct);
        if (storage != content.Storage || current.StagedBytes != content.StagedBytes ||
            !string.Equals(current.StagingDirectory, content.StagingDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            current.Sizing != content.Sizing ||
            !current.StagedFiles.SequenceEqual(content.StagedFiles) ||
            !string.Equals(current.BootSpecSha256, content.BootSpecSha256, StringComparison.Ordinal) ||
            bootPayload.Storage != storage || bootPayload.BootSpecSha256 != content.BootSpecSha256 ||
            bootPayload.GrubConfig != generatedBoot.GrubConfig ||
            bootPayload.InitrdFiles.Length != generatedBoot.InitrdFiles.Length ||
            bootPayload.InitrdFiles.Zip(generatedBoot.InitrdFiles).Any(pair =>
                pair.First.Name != pair.Second.Name ||
                !pair.First.Contents.AsSpan().SequenceEqual(pair.Second.Contents.AsSpan())) ||
            claimed.GenerationId != storage.GenerationId || claimed.Payload != storage.Payload ||
            claimed.LinuxEsp != storage.LinuxEsp || claimed.Iso != storage.Iso ||
            claimed.BootSpecSha256 != content.BootSpecSha256)
            throw new InvalidDataException("The reviewed installer seed changed before readback.");
        if (!PreparedLayoutStore.ValidReviewedSeedReceipt(checkpoint with
            { State = PreparationState.ContentStagedAndVerified, ReviewedSeedContent = claimed }, claimed))
            throw new InvalidDataException("The reviewed OEMDRV receipt has unsafe or incomplete files.");

        var payloadFiles = claimed.PayloadFiles.ToDictionary(file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var staged in content.StagedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var path = "\\" + staged.RelativePath.Replace('/', '\\');
            if (!payloadFiles.TryGetValue(path, out var copied) ||
                copied.VolumeGuid != storage.Payload.VolumeGuid ||
                copied.Length != staged.SizeBytes ||
                !string.Equals(copied.Sha256, staged.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("A reviewed staged file has no exact OEMDRV copy receipt.");
        }
        var grubHash = Convert.ToHexString(SHA256.HashData(
            Encoding.ASCII.GetBytes(bootPayload.GrubConfig)));
        if (!payloadFiles.TryGetValue(@"\EFI\BOOT\grub.cfg", out var grub) ||
            !string.Equals(grub.Sha256, grubHash, StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed GRUB content differs from the exact ESP boot argument.");
        if (bootSpec.KernelUrl is not null &&
            (!payloadFiles.TryGetValue(@"\igloo-boot\linux", out var kernel) ||
             checkpoint.Plan.ReviewedIntent?.Media.KernelBytes is null or 0 or > long.MaxValue ||
             kernel.Length != (long)checkpoint.Plan.ReviewedIntent.Media.KernelBytes ||
             !string.Equals(kernel.Sha256, bootSpec.KernelSha256,
                 StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The reviewed installer kernel differs from its media pin.");
        VerifyPinnedInstallerInitrd(checkpoint, claimed, generatedBoot, bootSpec,
            pinnedIsoPath, pinnedRawInitrdPath, ct);
        if (bootSpec.CopyFullIsoToVolume)
        {
            if (string.IsNullOrWhiteSpace(bootSpec.IsoVolumeFileName) ||
                !ReviewedSeedPathRules.TrySegments("\\" + bootSpec.IsoVolumeFileName, out var isoName) ||
                isoName.Length != 1 ||
                !string.Equals(bootSpec.IsoSha256, checkpoint.Plan.ReviewedIntent!.ArtifactSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                checkpoint.Plan.ReviewedIntent.Media.FullIsoBytes is 0 or > long.MaxValue)
                throw new InvalidDataException("The reviewed installer ISO has no exact media pin.");
            var volumeFiles = current.Sizing.IsoOnOwnPartition
                ? claimed.IsoFiles : claimed.PayloadFiles;
            var iso = volumeFiles.Where(file => string.Equals(file.RelativePath,
                "\\" + isoName[0], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (iso.Length != 1 ||
                iso[0].Length != (long)checkpoint.Plan.ReviewedIntent.Media.FullIsoBytes ||
                !string.Equals(iso[0].Sha256, bootSpec.IsoSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The reviewed installer ISO copy differs from its media pin.");
        }
        else if (!claimed.IsoFiles.IsEmpty)
            throw new InvalidDataException("The reviewed installer has an unexpected ISO copy.");

        VerifyPinnedIsoBootMembers(checkpoint, claimed, bootSpec, pinnedIsoPath, ct);

        readFile ??= WindowsRecoveryPathReader.ReadFileIdentity;
        foreach (var file in claimed.PayloadFiles.Concat(claimed.IsoFiles))
        {
            ct.ThrowIfCancellationRequested();
            var observed = readFile(file.VolumeGuid, file.RelativePath);
            if (observed.Availability != ObservationAvailability.Available ||
                observed.Value != file)
                throw new InvalidDataException("A reviewed installer seed file changed on volume readback.");
        }
        if (ObserveInstallerHandoff(reference, ct) != storage)
            throw new InvalidOperationException("The reviewed storage changed during seed file readback.");
        var verified = checkpoint with
        {
            State = PreparationState.ContentStagedAndVerified,
            ReviewedSeedContent = claimed,
        };
        return _checkpoints.PersistAndReopen(verified);
    }

    // Reopen the derived seed checkpoint and all current storage/file identities
    // before a reviewed boot handoff. This is still read-only and never permits
    // NVRAM or partition mutation by itself.
    internal ReviewedInstallerSeedReceiptV1 VerifyPersistedReviewedSeed(
        PreparationCheckpointReference stagedReference,
        ReviewedInstallerHandoffV1 expectedStorage,
        ReviewedContentHandoffV1 content, InstallerBootSpec bootSpec,
        string pinnedIsoPath, string? pinnedRawInitrdPath,
        Func<Guid, string, Observation<CanonicalFileIdentityV1>>? readFile = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stagedReference);
        ArgumentNullException.ThrowIfNull(expectedStorage);
        ArgumentNullException.ThrowIfNull(bootSpec);
        var created = ReopenCreatedCheckpoint(expectedStorage.Checkpoint);
        var staged = _checkpoints.Reopen(stagedReference);
        var receipt = staged.ReviewedSeedContent;
        if (staged.State != PreparationState.ContentStagedAndVerified ||
            receipt is null ||
            !JsonSerializer.SerializeToUtf8Bytes(staged).AsSpan().SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(created with
                {
                    State = PreparationState.ContentStagedAndVerified,
                    ReviewedSeedContent = receipt,
                })) ||
            receipt.GenerationId != expectedStorage.GenerationId ||
            receipt.Payload != expectedStorage.Payload ||
            receipt.LinuxEsp != expectedStorage.LinuxEsp ||
            receipt.Iso != expectedStorage.Iso ||
            !string.Equals(receipt.BootSpecSha256, HashBootSpec(bootSpec),
                StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed seed receipt is not derived from the pinned created checkpoint.");
        if (ObserveInstallerHandoff(expectedStorage.Checkpoint, ct) != expectedStorage)
            throw new InvalidOperationException("The reviewed installer storage changed before seed readback.");
        VerifyPinnedIsoBootMembers(staged, receipt, bootSpec, pinnedIsoPath, ct);
        if (content.Storage != expectedStorage ||
            !string.Equals(content.BootSpecSha256, receipt.BootSpecSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed seed content lost its boot source binding.");
        var boot = DirectInstallService.BuildReviewedBootPayload(content, bootSpec, ct);
        VerifyPinnedInstallerInitrd(staged, receipt, boot, bootSpec,
            pinnedIsoPath, pinnedRawInitrdPath, ct);
        readFile ??= WindowsRecoveryPathReader.ReadFileIdentity;
        foreach (var file in receipt.PayloadFiles.Concat(receipt.IsoFiles))
        {
            ct.ThrowIfCancellationRequested();
            var observed = readFile(file.VolumeGuid, file.RelativePath);
            if (observed.Availability != ObservationAvailability.Available ||
                observed.Value != file)
                throw new InvalidDataException("A reviewed installer seed file changed before boot preparation.");
        }
        if (ObserveInstallerHandoff(expectedStorage.Checkpoint, ct) != expectedStorage)
            throw new InvalidOperationException("The reviewed installer storage changed during seed readback.");
        return receipt;
    }

    private static void VerifyPinnedInstallerInitrd(PreparationCheckpointV1 checkpoint,
        ReviewedInstallerSeedReceiptV1 receipt, ReviewedBootPayloadV1 boot,
        InstallerBootSpec bootSpec, string pinnedIsoPath,
        string? pinnedRawInitrdPath, CancellationToken ct)
    {
        var rawInitrdBytes = checkpoint.Plan.ReviewedIntent?.Media.InitrdBytes;
        var isoBytes = checkpoint.Plan.ReviewedIntent?.Media.FullIsoBytes;
        var initrd = receipt.PayloadFiles.Where(file => file.RelativePath ==
            @"\igloo-boot\initrd").ToArray();
        if (rawInitrdBytes is null or 0 or > long.MaxValue ||
            isoBytes is null or 0 or > long.MaxValue || initrd.Length != 1 ||
            (bootSpec.KernelUrl is null) != (pinnedRawInitrdPath is null))
            throw new InvalidDataException("The reviewed initrd has no pinned raw source.");
        var derived = ReviewedSeedFileWriter.MeasurePinnedInstallerInitrd(boot,
            bootSpec, pinnedIsoPath, pinnedRawInitrdPath,
            (long)isoBytes, (long)rawInitrdBytes, ct);
        if (initrd[0].Length != derived.SizeBytes ||
            !string.Equals(initrd[0].Sha256, derived.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed initrd differs from its pinned source and config.");
    }

    private static void VerifyPinnedIsoBootMembers(PreparationCheckpointV1 checkpoint,
        ReviewedInstallerSeedReceiptV1 receipt, InstallerBootSpec bootSpec,
        string pinnedIsoPath, CancellationToken ct)
    {
        // Receipt readback alone proves what is on OEMDRV, not which signed
        // media produced its loader files. Compare only declared members.
        var intent = checkpoint.Plan.ReviewedIntent;
        if (intent is null || intent.Media.FullIsoBytes is 0 or > long.MaxValue ||
            !string.Equals(bootSpec.IsoSha256, intent.ArtifactSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed boot receipt has no pinned ISO source.");
        var payloadFiles = receipt.PayloadFiles.ToDictionary(file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        using var source = new ReviewedPinnedIsoSource(pinnedIsoPath,
            checked((long)intent.Media.FullIsoBytes), intent.ArtifactSha256, ct);
        var efi = ReviewedSeedFileWriter.FindInstallerEfiPair(source);
        RequireIsoMember(@"\EFI\BOOT\BOOTX64.EFI", efi.Shim);
        RequireIsoMember(@"\EFI\BOOT\grubx64.efi", efi.Grub);
        RequireIsoMember(@"\igloo-boot\shimx64.efi", efi.Shim);
        RequireIsoMember(@"\igloo-boot\grubx64.efi", efi.Grub);
        if (bootSpec.KernelUrl is null)
        {
            var kernelMember = source.FirstAvailable(bootSpec.KernelIsoPaths) ??
                throw new InvalidDataException("The pinned ISO has no declared installer kernel.");
            if (intent.Media.KernelBytes is 0 or > long.MaxValue)
                throw new InvalidDataException("The ISO-contained kernel has no measured size.");
            RequireIsoMember(@"\igloo-boot\linux", kernelMember,
                (long)intent.Media.KernelBytes);
        }
        if (bootSpec.ExtraIsoFiles.Count > 1 ||
            (bootSpec.ExtraIsoFiles.Count == 1 && intent.Media.Stage2Bytes is 0 or > long.MaxValue))
            throw new InvalidDataException("The reviewed stage2 receipt has unsupported media inventory.");
        foreach (var extra in bootSpec.ExtraIsoFiles)
        {
            if (string.IsNullOrWhiteSpace(extra.OemDrvRelativePath) ||
                !ReviewedSeedPathRules.TrySegments(
                    "\\" + extra.OemDrvRelativePath.Replace('/', '\\'), out var segments))
                throw new InvalidDataException("The reviewed stage2 receipt has an unsafe path.");
            RequireIsoMember("\\" + string.Join('\\', segments), extra.IsoRelativePath,
                checked((long)intent.Media.Stage2Bytes));
        }
        source.VerifyPin(ct);

        void RequireIsoMember(string destination, string member, long? expectedBytes = null)
        {
            if (!payloadFiles.TryGetValue(destination, out var copied))
                throw new InvalidDataException("The reviewed OEMDRV boot member has no file receipt.");
            var measured = source.HashMember(member, expectedBytes, ct);
            if (copied.Length != measured.SizeBytes ||
                !string.Equals(copied.Sha256, measured.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The reviewed OEMDRV boot member differs from its pinned ISO source.");
        }
    }

    internal static string HashBootSpec(InstallerBootSpec bootSpec) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(bootSpec)));

    internal static DirectInstallService.DirectInstallSizePlan RequireMeasuredSizing(
        PreparationPlanV1 plan, InstallerBootSpec bootSpec, long requestedLinuxBytes,
        long stagedBytes)
    {
        if (stagedBytes < 0)
            throw new InvalidDataException("The reviewed staged byte count is invalid.");
        var review = DirectInstallService.ReviewPreparedIntent(plan, bootSpec, requestedLinuxBytes);
        if (review.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException($"The reviewed installer media is unavailable: {review.Code}.");
        var media = plan.ReviewedIntent!.Media;
        var sizing = DirectInstallService.PlanStorageSizes(bootSpec, requestedLinuxBytes,
            checked((long)media.FullIsoBytes), checked((long)media.KernelBytes),
            checked((long)media.InitrdBytes), checked((long)media.Stage2Bytes), stagedBytes);
        var allocations = plan.Space.Allocations;
        var payload = allocations.Single(a => a.Role == PreparationRole.Payload);
        var root = allocations.Single(a => a.Role == PreparationRole.LinuxRoot);
        var iso = allocations.SingleOrDefault(a => a.Role == PreparationRole.Iso);
        if (payload.SizeBytes < (ulong)sizing.OemDrvBytes ||
            root.SizeBytes < (ulong)requestedLinuxBytes ||
            (sizing.IsoOnOwnPartition
                ? iso is null || iso.SizeBytes < (ulong)sizing.IsoPartitionBytes
                : iso is not null))
            throw new InvalidOperationException("Measured staging exceeds the reviewed installer allocations.");
        return sizing;
    }

    private sealed record HandoffVolumes(CanonicalVolumeIdentityV1 LinuxEsp,
        CanonicalVolumeIdentityV1 Payload, CanonicalVolumeIdentityV1? Iso);

    private HandoffVolumes ObserveHandoffVolumes(PreparationCheckpointV1 checkpoint)
    {
        var plan = checkpoint.Plan;
        var owned = checkpoint.StorageOwnership!;
        var expected = owned.PreservedPartitions.Concat(owned.CreatedPartitions.Select(r => r.Identity)).ToArray();
        var inventory = ObserveExactInventory(plan, expected);
        var formatted = plan.Space.Allocations.Where(a => a.CreateInWindows && a.FileSystem.Length > 0).ToArray();
        if (formatted.Length is not (2 or 3) ||
            formatted.Any(a => a.Role is not (PreparationRole.LinuxEsp or PreparationRole.Payload or PreparationRole.Iso)) ||
            checkpoint.CreationReceipts.Length != formatted.Length)
            throw new InvalidOperationException("The reviewed formatted volume set is incomplete.");
        var bound = new Dictionary<PreparationRole, CanonicalVolumeIdentityV1>();
        foreach (var allocation in formatted)
        {
            var receipt = checkpoint.CreationReceipts.Single(r => r.Role == allocation.Role);
            var partition = owned.CreatedPartitions.Single(r => r.Role == allocation.Role).Identity;
            var observation = CanonicalRecoveryIdentity.Bind(Observations.Available(inventory.Snapshot),
                receipt.Identity.VolumeGuid);
            if (observation.Availability != ObservationAvailability.Available ||
                observation.Value != receipt.Identity ||
                observation.Value.Disk != plan.TargetDisk ||
                PreparedStorageOwnership.PartitionOf(observation.Value) != partition ||
                observation.Value.OffsetBytes != allocation.OffsetBytes ||
                observation.Value.SizeBytes != allocation.SizeBytes ||
                observation.Value.PartitionType != allocation.PartitionType ||
                !string.Equals(observation.Value.FileSystem, allocation.FileSystem,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A reviewed formatted volume changed or became ambiguous.");
            bound.Add(allocation.Role, observation.Value);
        }
        return new(bound[PreparationRole.LinuxEsp], bound[PreparationRole.Payload],
            bound.GetValueOrDefault(PreparationRole.Iso));
    }

    internal PreparationCheckpointReference CreateAndFormatNext(PreparationCheckpointReference reference,
        ReviewedShrinkReceiptV1 shrink, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var checkpoint = _checkpoints.Reopen(reference);
        var plan = checkpoint.Plan;
        var reviewed = PreparedLayoutRules.VerifyReviewedIntent(plan);
        var verifiedShrink = PreparedLayoutRules.VerifyShrink(plan, Observations.Available(shrink.After),
            Observations.Available(shrink.After));
        if (reviewed.Availability != ObservationAvailability.Available ||
            verifiedShrink.Availability != ObservationAvailability.Available ||
            PreparedLayoutRules.VerifyShrinkNtfs(plan, shrink).Availability != ObservationAvailability.Available ||
            shrink.GenerationId != plan.GenerationId || shrink.Before != plan.ReviewedIntent!.ShrinkSource ||
            checkpoint.State is not (PreparationState.Planned or PreparationState.CreationInProgress) ||
            checkpoint.ShrinkReceipt != shrink)
            throw new InvalidOperationException("The exact reviewed creation checkpoint or shrink receipt is unavailable.");

        var preserved = plan.ReviewedIntent!.BeforePartitions.Select(p =>
            p.PartitionGuid == shrink.Before.PartitionGuid
                ? p with { SizeBytes = shrink.After.SizeBytes } : p).ToImmutableArray();
        var allocations = plan.Space.Allocations.Where(a => a.CreateInWindows).ToArray();
        var prior = checkpoint.StorageOwnership?.CreatedPartitions ?? ImmutableArray<PartitionCreationReceiptV1>.Empty;
        if (checkpoint.StorageOwnership is not null &&
            (!checkpoint.StorageOwnership.PreservedPartitions.SequenceEqual(preserved) ||
             checkpoint.StorageOwnership.GenerationId != plan.GenerationId ||
             !prior.Select(r => r.Role).SequenceEqual(allocations.Take(prior.Length).Select(a => a.Role)) ||
             prior.Any(r => plan.Space.Allocations.Any(a => a.Role == r.Role && a.FileSystem.Length > 0) &&
                 !checkpoint.CreationReceipts.Any(f => f.Role == r.Role))))
            throw new InvalidOperationException("The previous reviewed partition is unformatted or changed; reconciliation is required.");
        var next = allocations.FirstOrDefault(a => prior.All(r => r.Role != a.Role));
        if (next is null)
            throw new InvalidOperationException("No reviewed Windows allocation remains to create.");

        var expectedBefore = preserved.Concat(prior.Select(r => r.Identity)).ToArray();
        var before = ObserveExactInventory(plan, expectedBefore);
        RequireReceiptedNtfs(before, shrink);
        RequirePriorFormattedVolumes(checkpoint, before);
        var beforeGap = ObserveRemainingInstallerExtent(plan, before, next);
        ct.ThrowIfCancellationRequested();
        // The shrink and NTFS-size observations were persisted before entry.
        // A caller-supplied receipt can never become authority at this point.
        var current = ObserveExactInventory(plan, expectedBefore);
        RequireReceiptedNtfs(current, shrink);
        RequirePriorFormattedVolumes(checkpoint, current);
        var currentGap = ObserveRemainingInstallerExtent(plan, current, next);
        if (before.DiskNumber != current.DiskNumber ||
            !string.Equals(before.DiskRow.ObjectPath, current.DiskRow.ObjectPath,
                StringComparison.OrdinalIgnoreCase) || beforeGap != currentGap)
            throw new InvalidOperationException("The exact Windows disk changed before partition creation.");

        ct.ThrowIfCancellationRequested();
        NativePartitionCreateResult native;
        try
        {
            native = _create(current.DiskRow, next, ct);
            if (native.ReturnValue != 0 || native.CreatedPartition is null)
                throw new InvalidOperationException($"MSFT_Disk.CreatePartition returned {native.ReturnValue} without an exact created partition.");
            var provider = PartitionFromRow(plan.TargetDisk, current.DiskNumber, native.CreatedPartition);
            var expectedAfter = expectedBefore.Append(provider).ToArray();
            var reopened = ObserveExactInventory(plan, expectedAfter);
            RequirePriorFormattedVolumes(checkpoint, reopened);
            var observed = reopened.Partitions.Single(p => p.PartitionGuid == provider.PartitionGuid);
            var receipt = PreparedStorageOwnership.VerifyCreation(plan, next.Role,
                Observations.Available(provider), Observations.Available(observed));
            if (receipt.Availability != ObservationAvailability.Available)
                throw new InvalidOperationException($"Created partition ownership was not verified: {receipt.Code}.");
            var ownership = new PreparedStorageOwnershipV1(plan.GenerationId, preserved,
                prior.Add(receipt.Value));
            checkpoint = checkpoint with { State = PreparationState.CreationInProgress,
                StorageOwnership = ownership };
            reference = next.FileSystem.Length == 0
                ? CompleteIfPossible(checkpoint, allocations.Length)
                : _checkpoints.PersistAndReopen(checkpoint);
        }
        catch (Exception error)
        {
            // A provider error may still leave a partition. Never adopt one by
            // matching its label or geometry on retry; persist ambiguity instead.
            throw MarkAmbiguous(checkpoint,
                "Partition creation may have changed the disk without an exact receipt.", error);
        }

        if (next.FileSystem.Length == 0)
            return reference;

        // A verified creation receipt is durable before formatting can begin.
        if (ct.IsCancellationRequested)
            throw MarkAmbiguous(checkpoint,
                "Partition creation was verified, but formatting was cancelled before it started; manual reconciliation is required.",
                new OperationCanceledException(ct));
        try
        {
            var created = checkpoint.StorageOwnership!.CreatedPartitions.Single(r => r.Role == next.Role).Identity;
            var expected = preserved.Concat(checkpoint.StorageOwnership.CreatedPartitions.Select(r => r.Identity)).ToArray();
            var observed = ObserveExactInventory(plan, expected);
            RequireReceiptedNtfs(observed, shrink);
            RequirePriorFormattedVolumes(checkpoint, observed);
            var partitionRow = FindExactPartitionRow(observed.DiskNumber, created);
            var status = _format(partitionRow, next, ct);
            if (status != 0)
                throw new InvalidOperationException($"MSFT_Volume.Format returned {status}.");
            var providerResult = ObserveFormattedVolume(plan, expected, created, next.FileSystem);
            var independent = ObserveFormattedVolume(plan, expected, created, next.FileSystem);
            var formatted = PreparedLayoutRules.VerifyCreation(plan, next.Role,
                Observations.Available(providerResult), Observations.Available(independent));
            if (formatted.Availability != ObservationAvailability.Available)
                throw new InvalidOperationException($"Formatted partition identity was not verified: {formatted.Code}.");
            checkpoint = checkpoint with { CreationReceipts = checkpoint.CreationReceipts.Add(formatted.Value) };
            RequirePriorFormattedVolumes(checkpoint, ObserveExactInventory(plan, expected));
            return CompleteIfPossible(checkpoint, allocations.Length);
        }
        catch (Exception error)
        {
            throw MarkAmbiguous(checkpoint,
                "Formatting may have changed the new partition without a verified receipt.", error);
        }
    }

    // Persist the independently read-back shrink before attempting any new
    // partition. A cancellation after Resize must still leave a durable
    // checkpoint for the exact changed Windows geometry.
    internal PreparationCheckpointReference PersistReviewedShrink(
        PreparationCheckpointReference reference, ReviewedShrinkReceiptV1 shrink)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(shrink);
        var checkpoint = _checkpoints.Reopen(reference);
        var plan = checkpoint.Plan;
        var verified = PreparedLayoutRules.VerifyShrink(plan,
            Observations.Available(shrink.After), Observations.Available(shrink.After));
        if (checkpoint.State != PreparationState.Planned ||
            checkpoint.ShrinkReceipt is not null ||
            !checkpoint.CreationReceipts.IsEmpty || checkpoint.StorageOwnership is not null ||
            verified.Availability != ObservationAvailability.Available ||
            new ReviewedShrinkReceiptV1(shrink.GenerationId, shrink.Before, shrink.After) != verified.Value ||
            PreparedLayoutRules.VerifyShrinkNtfs(plan, shrink).Availability != ObservationAvailability.Available ||
            plan.Space.Allocations.All(allocation => !allocation.CreateInWindows))
            throw new InvalidOperationException("The reviewed shrink has no exact planned checkpoint.");

        var preserved = plan.ReviewedIntent!.BeforePartitions.Select(partition =>
            partition.PartitionGuid == shrink.Before.PartitionGuid
                ? partition with { SizeBytes = shrink.After.SizeBytes } : partition).ToArray();
        var firstAllocation = plan.Space.Allocations.First(allocation => allocation.CreateInWindows);
        var before = ObserveExactInventory(plan, preserved);
        RequireReceiptedNtfs(before, shrink);
        var beforeGap = ObserveRemainingInstallerExtent(plan, before, firstAllocation);
        var after = ObserveExactInventory(plan, preserved);
        RequireReceiptedNtfs(after, shrink);
        var afterGap = ObserveRemainingInstallerExtent(plan, after, firstAllocation);
        if (before.DiskNumber != after.DiskNumber ||
            !string.Equals(before.DiskRow.ObjectPath, after.DiskRow.ObjectPath,
                StringComparison.OrdinalIgnoreCase) || beforeGap != afterGap)
            throw new InvalidOperationException("The reviewed Windows shrink geometry changed before checkpoint persistence.");

        var persisted = _checkpoints.PersistAndReopen(checkpoint with { ShrinkReceipt = shrink });
        var reopened = _checkpoints.Reopen(persisted);
        if (reopened.ShrinkReceipt != shrink)
            throw new InvalidOperationException("The durable reviewed NTFS shrink receipt changed.");
        var confirmed = ObserveExactInventory(plan, preserved);
        RequireReceiptedNtfs(confirmed, shrink);
        return persisted;
    }

    private void RequireReceiptedNtfs(ExactInventory inventory, ReviewedShrinkReceiptV1 shrink)
    {
        var bound = CanonicalRecoveryIdentity.Bind(Observations.Available(inventory.Snapshot),
            shrink.After.VolumeGuid);
        if (bound.Availability != ObservationAvailability.Available || bound.Value != shrink.After ||
            _ntfsSize(shrink.After.VolumeGuid) != shrink.NtfsAfter)
            throw new InvalidOperationException("The reviewed NTFS filesystem differs from its durable shrink receipt.");
    }

    private AmbiguousPartitionCreationException MarkAmbiguous(PreparationCheckpointV1 checkpoint,
        string message, Exception failure)
    {
        try
        {
            var reference = _checkpoints.PersistAndReopen(checkpoint with
                { State = PreparationState.AmbiguousLeftover });
            return new AmbiguousPartitionCreationException(message, failure)
                { Checkpoint = reference };
        }
        // Any artifact-store failure here must retain the ambiguity classification.
#pragma warning disable CA1031
        catch (Exception persistenceFailure)
#pragma warning restore CA1031
        {
            // The previous pinned checkpoint and a changed disk still prevent
            // adoption, but the caller must know ambiguity could not be recorded.
            return new AmbiguousPartitionCreationException(
                message + " The ambiguous checkpoint could not be persisted.",
                new AggregateException(failure, persistenceFailure));
        }
    }

    private PreparationCheckpointReference CompleteIfPossible(PreparationCheckpointV1 checkpoint,
        int plannedCount)
    {
        var allCreated = checkpoint.StorageOwnership!.CreatedPartitions.Length == plannedCount;
        var allFormatted = checkpoint.StorageOwnership.CreatedPartitions.All(r =>
            checkpoint.Plan.Space.Allocations.Single(a => a.Role == r.Role).FileSystem.Length == 0 ||
            checkpoint.CreationReceipts.Any(f => f.Role == r.Role));
        var complete = allCreated && allFormatted;
        return _checkpoints.PersistAndReopen(checkpoint with
            { State = complete ? PreparationState.CreatedAndVerified : PreparationState.CreationInProgress });
    }

    private sealed record ExactInventory(uint DiskNumber, WindowsStorageRow DiskRow,
        PreparedGptPartitionV1[] Partitions, WindowsStorageSnapshot Snapshot);

    private readonly record struct RemainingInstallerExtent(ulong FirstUsable, ulong UsableLength,
        uint PartitionCount, ulong GapOffset, ulong GapSize);

    // The plan's free extent is advisory until shrink has completed. Read the
    // native GPT and independently compare every nonempty entry to WMI before
    // the first and each later CreatePartition call. The remaining planned
    // allocations must fit in one *currently observed* contiguous gap.
    private RemainingInstallerExtent ObserveRemainingInstallerExtent(PreparationPlanV1 plan,
        ExactInventory inventory, PreparationAllocationV1 next)
    {
        var intent = plan.ReviewedIntent!;
        var gpt = _gptRange(inventory.DiskNumber);
        if (gpt.DiskGuid != plan.TargetDisk.GptDiskGuid ||
            gpt.Partitions.IsDefaultOrEmpty ||
            gpt.Partitions.Length != inventory.Partitions.Length ||
            gpt.FirstUsableOffsetBytes % plan.TargetDisk.LogicalSectorSize != 0 ||
            gpt.UsableLengthBytes % plan.TargetDisk.LogicalSectorSize != 0 ||
            gpt.FirstUsableOffsetBytes >= plan.TargetDisk.SizeBytes ||
            gpt.UsableLengthBytes > plan.TargetDisk.SizeBytes - gpt.FirstUsableOffsetBytes ||
            !gpt.Partitions.Select(p => new PreparedGptPartitionV1(plan.TargetDisk,
                    p.PartitionGuid, p.PartitionType, p.OffsetBytes, p.SizeBytes))
                .OrderBy(p => p.PartitionGuid).SequenceEqual(inventory.Partitions))
            throw new InvalidOperationException("The native GPT differs from the complete reviewed Windows inventory.");

        var usableEnd = checked(gpt.FirstUsableOffsetBytes + gpt.UsableLengthBytes);
        var plannedEnd = checked(plan.Space.Allocations[^1].OffsetBytes +
            plan.Space.Allocations[^1].SizeBytes);
        if (plannedEnd > usableEnd || next.OffsetBytes < intent.FreeOffsetBytes)
            throw new InvalidOperationException("The reviewed installer allocations exceed the native GPT usable range.");
        var cursor = gpt.FirstUsableOffsetBytes;
        var matching = 0;
        var gapOffset = 0UL;
        var gapSize = 0UL;
        foreach (var partition in gpt.Partitions.OrderBy(p => p.OffsetBytes))
        {
            if (partition.OffsetBytes < cursor || partition.OffsetBytes > usableEnd ||
                partition.SizeBytes == 0 || partition.SizeBytes > usableEnd - partition.OffsetBytes)
                throw new InvalidOperationException("The native GPT has overlapping or out-of-range partitions.");
            CheckGap(cursor, partition.OffsetBytes);
            cursor = checked(partition.OffsetBytes + partition.SizeBytes);
        }
        CheckGap(cursor, usableEnd);
        if (matching != 1)
            throw new InvalidOperationException("The planned installer extent is not one observed GPT free gap.");
        return new(gpt.FirstUsableOffsetBytes, gpt.UsableLengthBytes,
            gpt.PartitionCount, gapOffset, gapSize);

        void CheckGap(ulong start, ulong end)
        {
            if (start > next.OffsetBytes || end < plannedEnd) return;
            matching++;
            gapOffset = start;
            gapSize = end - start;
            var expectedStart = next == plan.Space.Allocations[0]
                ? intent.FreeOffsetBytes : next.OffsetBytes;
            if (start != expectedStart)
                throw new InvalidOperationException("The observed GPT gap does not begin at the reviewed remaining extent.");
        }
    }

    private ExactInventory ObserveExactInventory(PreparationPlanV1 plan,
        IReadOnlyCollection<PreparedGptPartitionV1> expected)
    {
        var observation = _storage.ReadIdentitySnapshot();
        if (observation.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("The Windows GPT inventory is unavailable.");
        var snapshot = observation.Value;
        var shrinkSource = plan.ReviewedIntent!.ShrinkSource with
            { SizeBytes = plan.ReviewedIntent.ResultingSourceBytes };
        var source = CanonicalRecoveryIdentity.Bind(observation, shrinkSource.VolumeGuid);
        var esp = CanonicalRecoveryIdentity.Bind(observation, plan.WindowsEsp.VolumeGuid);
        if (source.Availability != ObservationAvailability.Available || source.Value != shrinkSource ||
            esp.Availability != ObservationAvailability.Available || esp.Value != plan.WindowsEsp ||
            snapshot.Partitions.Any(p => p.DiskNumber.Availability != ObservationAvailability.Available))
            throw new InvalidOperationException("The Windows source or ESP identity changed before partition creation.");
        var owner = snapshot.Partitions.Single(p => p.PartitionGuid.Availability == ObservationAvailability.Available &&
            p.PartitionGuid.Value == shrinkSource.PartitionGuid);
        var diskNumber = owner.DiskNumber.Value;
        var partitions = snapshot.Partitions.Where(p => p.DiskNumber.Value == diskNumber)
            .Select(p => PartitionFromObservation(plan.TargetDisk, p)).OrderBy(p => p.PartitionGuid).ToArray();
        if (!partitions.SequenceEqual(expected.OrderBy(p => p.PartitionGuid)))
            throw new InvalidOperationException("The Windows partition set differs from the pinned reviewed checkpoint.");
        var disks = _storage.ReadDisks().RowsOrThrow().Where(r =>
            StorageObservationProjection.Disk(r).Number is { Availability: ObservationAvailability.Available } number &&
            number.Value == diskNumber).ToArray();
        if (disks.Length != 1 || !SameDisk(disks[0], plan.TargetDisk))
            throw new InvalidOperationException("The native Windows disk object differs from the reviewed target.");
        return new ExactInventory(diskNumber, disks[0], partitions, snapshot);
    }

    private static void RequirePriorFormattedVolumes(PreparationCheckpointV1 checkpoint,
        ExactInventory inventory)
    {
        foreach (var receipt in checkpoint.CreationReceipts)
        {
            var partition = checkpoint.StorageOwnership?.CreatedPartitions.SingleOrDefault(
                created => created.Role == receipt.Role);
            var observed = CanonicalRecoveryIdentity.Bind(
                Observations.Available(inventory.Snapshot), receipt.Identity.VolumeGuid);
            if (partition is null || observed.Availability != ObservationAvailability.Available ||
                observed.Value != receipt.Identity ||
                PreparedStorageOwnership.PartitionOf(observed.Value) != partition.Identity)
                throw new InvalidOperationException(
                    "A previously formatted reviewed volume changed before the next partition operation.");
        }
    }

    private static bool SameDisk(WindowsStorageRow row, CanonicalDiskIdentityV1 expected)
    {
        if (string.IsNullOrWhiteSpace(row.ObjectPath)) return false;
        var disk = StorageObservationProjection.Disk(row);
        return disk.UniqueId.Availability == ObservationAvailability.Available && disk.UniqueId.Value == expected.UniqueId &&
            disk.UniqueIdFormat.Availability == ObservationAvailability.Available && disk.UniqueIdFormat.Value == expected.UniqueIdFormat &&
            disk.BusType.Availability == ObservationAvailability.Available && disk.BusType.Value == expected.BusType &&
            disk.GptGuid.Availability == ObservationAvailability.Available && disk.GptGuid.Value == expected.GptDiskGuid &&
            disk.Size.Availability == ObservationAvailability.Available && disk.Size.Value == expected.SizeBytes &&
            disk.LogicalSectorSize.Availability == ObservationAvailability.Available && disk.LogicalSectorSize.Value == expected.LogicalSectorSize &&
            disk.PhysicalSectorSize.Availability == ObservationAvailability.Available && disk.PhysicalSectorSize.Value == expected.PhysicalSectorSize &&
            disk.PartitionStyle.Availability == ObservationAvailability.Available && disk.PartitionStyle.Value == 2;
    }

    private static PreparedGptPartitionV1 PartitionFromObservation(CanonicalDiskIdentityV1 disk,
        PartitionObservation observed)
    {
        if (observed.PartitionGuid.Availability != ObservationAvailability.Available ||
            observed.GptType.Availability != ObservationAvailability.Available ||
            observed.Offset.Availability != ObservationAvailability.Available ||
            observed.Size.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("A Windows GPT partition lacks exact identity.");
        return new(disk, observed.PartitionGuid.Value, observed.GptType.Value,
            observed.Offset.Value, observed.Size.Value);
    }

    private static PreparedGptPartitionV1 PartitionFromRow(CanonicalDiskIdentityV1 disk,
        uint diskNumber, WindowsStorageRow row)
    {
        var observed = StorageObservationProjection.Partition(row);
        if (observed.DiskNumber.Availability != ObservationAvailability.Available ||
            observed.DiskNumber.Value != diskNumber)
            throw new InvalidOperationException("The provider created partition on another disk.");
        return PartitionFromObservation(disk, observed);
    }

    private WindowsStorageRow FindExactPartitionRow(uint diskNumber, PreparedGptPartitionV1 created)
    {
        if (diskNumber > int.MaxValue)
            throw new InvalidOperationException("The exact Windows disk ordinal is unsupported.");
        var rows = _storage.ReadPartitions((int)diskNumber).RowsOrThrow().Where(r =>
            StorageObservationProjection.Partition(r).PartitionGuid is
                { Availability: ObservationAvailability.Available } guid &&
            guid.Value == created.PartitionGuid).ToArray();
        if (rows.Length != 1 || string.IsNullOrWhiteSpace(rows[0].ObjectPath) ||
            PartitionFromRow(created.Disk, diskNumber, rows[0]) != created)
            throw new InvalidOperationException("The exact created partition WMI object is unavailable.");
        return rows[0];
    }

    private CanonicalVolumeIdentityV1 ObserveFormattedVolume(PreparationPlanV1 plan,
        IReadOnlyCollection<PreparedGptPartitionV1> expected, PreparedGptPartitionV1 created,
        string fileSystem)
    {
        var inventory = ObserveExactInventory(plan, expected);
        var volumes = inventory.Snapshot.Volumes.Where(v =>
            v.PartitionGuid.Availability == ObservationAvailability.Available &&
            v.PartitionGuid.Value == created.PartitionGuid).ToArray();
        if (volumes.Length != 1 || volumes[0].VolumeGuid.Availability != ObservationAvailability.Available)
            throw new InvalidOperationException("The formatted Windows volume has no unique GUID.");
        var bound = CanonicalRecoveryIdentity.Bind(Observations.Available(inventory.Snapshot),
            volumes[0].VolumeGuid.Value);
        if (bound.Availability != ObservationAvailability.Available ||
            bound.Value.Disk != plan.TargetDisk ||
            PreparedStorageOwnership.PartitionOf(bound.Value) != created ||
            !string.Equals(bound.Value.FileSystem, fileSystem, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The formatted Windows volume changed or has the wrong filesystem.");
        return bound.Value;
    }

    private static NativePartitionCreateResult CreateExactViaWmi(WindowsStorageRow disk,
        PreparationAllocationV1 allocation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(disk.ObjectPath))
            throw new InvalidOperationException("The native Windows disk has no object path.");
        using var target = new ManagementObject(disk.ObjectPath);
        target.Get();
        foreach (var key in new[] { "Number", "UniqueId", "UniqueIdFormat", "BusType", "Guid",
            "Size", "LogicalSectorSize", "PhysicalSectorSize", "PartitionStyle" })
            if (!Equals(disk[key], target[key]))
                throw new InvalidOperationException("The native Windows disk changed before CreatePartition.");
        ct.ThrowIfCancellationRequested();
        using var input = target.GetMethodParameters("CreatePartition");
        input["Size"] = allocation.SizeBytes;
        input["UseMaximumSize"] = false;
        input["Offset"] = allocation.OffsetBytes;
        input["Alignment"] = checked((uint)PreparationSpacePlanning.Alignment);
        input["AssignDriveLetter"] = false;
        input["GptType"] = allocation.PartitionType.ToString("B");
        using var result = target.InvokeMethod("CreatePartition", input, null);
        if (result is null)
            throw new InvalidOperationException("MSFT_Disk.CreatePartition returned no status.");
        var code = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
        var created = result["CreatedPartition"] as ManagementBaseObject;
        return new(code, created is null ? null : new WindowsStorageRow(created.Properties.Cast<PropertyData>()
            .ToImmutableDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase)));
    }

    private static uint FormatExactViaWmi(WindowsStorageRow selected,
        PreparationAllocationV1 allocation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(selected.ObjectPath))
            throw new InvalidOperationException("The native Windows partition has no object path.");
        using var partition = new ManagementObject(selected.ObjectPath);
        partition.Get();
        foreach (var key in FormatPartitionKeys)
            if (!Equals(selected[key], partition[key]))
                throw new InvalidOperationException("The native Windows partition changed before format.");
        using var associated = partition.GetRelated("MSFT_Volume");
        var volumes = associated.Cast<ManagementObject>().ToArray();
        try
        {
            if (volumes.Length != 1)
                throw new InvalidOperationException("The created partition has no unique format target.");
            var volume = volumes[0];
            using var related = volume.GetRelated("MSFT_Partition");
            var owners = related.Cast<ManagementObject>().ToArray();
            try
            {
                if (owners.Length != 1 || FormatPartitionKeys.Any(key =>
                        !Equals(selected[key], owners[0][key])))
                    throw new InvalidOperationException("The format target is not reciprocally bound to the created partition.");
            }
            finally
            {
                foreach (var owner in owners) owner.Dispose();
            }
            ct.ThrowIfCancellationRequested();
            using var input = volume.GetMethodParameters("Format");
            input["FileSystem"] = allocation.FileSystem;
            input["FileSystemLabel"] = allocation.Role switch
            {
                PreparationRole.LinuxEsp => "IGLOOESP",
                PreparationRole.Payload => "OEMDRV",
                PreparationRole.Iso => "IGLOOISO",
                _ => throw new InvalidOperationException("The planned partition has no Windows filesystem."),
            };
            input["Full"] = false;
            input["Force"] = false;
            using var result = volume.InvokeMethod("Format", input, null);
            if (result is null)
                throw new InvalidOperationException("MSFT_Volume.Format returned no status.");
            return Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
        }
        finally
        {
            foreach (var volume in volumes) volume.Dispose();
        }
    }
}
