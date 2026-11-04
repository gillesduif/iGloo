using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityRecovery;

namespace Igloo.Preflight.CommunityPreparation;

public sealed record PreparationCheckpointV1(int SchemaVersion, PreparationState State,
    PreparationPlanV1 Plan, ImmutableArray<PreparedPartitionV1> CreationReceipts, PreparedLayoutV1? Layout)
{
    public PreparedStorageOwnershipV1? StorageOwnership { get; init; }
    // Added without changing schema v1 so older non-reviewed checkpoints reopen.
    // A reviewed execution persists this exact shrink result before partition creation.
    public ReviewedShrinkReceiptV1? ShrinkReceipt { get; init; }
    // Installer boot originates on the separately owned OEMDRV payload volume.
    // The prepared Linux ESP remains reserved for the installed system.
    public ReviewedInstallerSeedReceiptV1? ReviewedSeedContent { get; init; }
}
public sealed record PreparationCheckpointReference(Guid ArtifactId, Guid GenerationId, string Sha256);

public sealed record ReviewedInstallerSeedReceiptV1(Guid GenerationId,
    CanonicalVolumeIdentityV1 Payload, CanonicalVolumeIdentityV1 LinuxEsp,
    CanonicalVolumeIdentityV1? Iso, string BootSpecSha256,
    ImmutableArray<CanonicalFileIdentityV1> PayloadFiles,
    ImmutableArray<CanonicalFileIdentityV1> IsoFiles);

// Reuses Community's flushed/CreateNew/reparse-checked artifact storage. A pinned reference is
// required for reopen; scanning a directory for plausible JSON is never ownership acquisition.
// This stores evidence, not an execution journal or a recovery authorization.
public sealed class PreparedLayoutStore(ICommunityRecoveryArtifactStore artifacts)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public PreparationCheckpointReference PersistAndReopen(PreparationCheckpointV1 checkpoint)
    {
        RequireStructure(checkpoint);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, Options);
        var reference = new PreparationCheckpointReference(Guid.NewGuid(), checkpoint.Plan.GenerationId,
            Convert.ToHexString(SHA256.HashData(bytes)));
        artifacts.PersistNew(reference.ArtifactId, bytes);
        _ = Reopen(reference);
        return reference;
    }

    public PreparationCheckpointV1 Reopen(PreparationCheckpointReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var bytes = artifacts.Reopen(reference.ArtifactId);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reference.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Preparation checkpoint integrity mismatch.");
        var checkpoint = JsonSerializer.Deserialize<PreparationCheckpointV1>(bytes, Options)
            ?? throw new InvalidDataException("Preparation checkpoint is missing.");
        RequireStructure(checkpoint);
        if (checkpoint.Plan.GenerationId != reference.GenerationId)
            throw new InvalidDataException("Preparation generation mismatch.");
        return checkpoint;
    }

    private static void RequireStructure(PreparationCheckpointV1 checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.SchemaVersion != 1 || checkpoint.Plan is null || !PreparedLayoutRules.IsValidPlan(checkpoint.Plan) ||
            !Enum.IsDefined(checkpoint.State) || checkpoint.CreationReceipts.IsDefault)
            throw new InvalidDataException("Invalid preparation checkpoint structure.");
        if (checkpoint.Plan.ReviewedIntent is null && checkpoint.ShrinkReceipt is not null)
            throw new InvalidDataException("An unreviewed checkpoint contains a shrink receipt.");
        if (checkpoint.StorageOwnership is not null &&
            (checkpoint.StorageOwnership.PreservedPartitions.IsDefaultOrEmpty ||
             checkpoint.StorageOwnership.CreatedPartitions.IsDefault))
            throw new InvalidDataException("Partition ownership has incomplete receipt arrays.");
        if (checkpoint.Plan.ReviewedIntent is not null)
        {
            var intent = checkpoint.Plan.ReviewedIntent;
            var receipt = checkpoint.ShrinkReceipt;
            if (receipt is not null && (receipt.GenerationId != checkpoint.Plan.GenerationId ||
                receipt.Before != intent.ShrinkSource ||
                PreparedLayoutRules.VerifyShrink(checkpoint.Plan,
                    Core.Abstractions.Observations.Available(receipt.After),
                    Core.Abstractions.Observations.Available(receipt.After)).Availability !=
                    Core.Abstractions.ObservationAvailability.Available ||
                (receipt.NtfsBefore is null) != (receipt.NtfsAfter is null) ||
                receipt.NtfsBefore is not null &&
                    PreparedLayoutRules.VerifyShrinkNtfs(checkpoint.Plan, receipt).Availability !=
                    Core.Abstractions.ObservationAvailability.Available))
                throw new InvalidDataException("The reviewed shrink receipt does not match the plan.");
            if (checkpoint.State is PreparationState.CreationInProgress or PreparationState.CreatedAndVerified or
                PreparationState.ContentStagedAndVerified && receipt is null)
                throw new InvalidDataException("A reviewed created checkpoint has no shrink receipt.");
            if (checkpoint.StorageOwnership is not null && receipt is null)
                throw new InvalidDataException("Reviewed partition ownership has no shrink receipt.");
            if (checkpoint.StorageOwnership is not null && receipt is not null &&
                !checkpoint.StorageOwnership.PreservedPartitions.Contains(
                    PreparedStorageOwnership.PartitionOf(receipt.After)))
                throw new InvalidDataException("Reviewed preserved partitions differ from the shrink receipt.");
        }
        var assessed = PreparedLayoutRules.AssessOwnership(checkpoint.Plan, checkpoint.Plan.GenerationId,
            checkpoint.CreationReceipts, checkpoint.CreationReceipts.Select(r => r.Identity).ToImmutableArray(), checkpoint.Plan.WindowsEsp);
        var completePartitions = checkpoint.StorageOwnership is not null && PreparedStorageOwnership.VerifyStructure(
            new PreparedLayoutV1(1, checkpoint.Plan, checkpoint.CreationReceipts, "", [])
            { StorageOwnership = checkpoint.StorageOwnership, ShrinkReceipt = checkpoint.ShrinkReceipt }).Availability == Core.Abstractions.ObservationAvailability.Available;
        var partialPartitions = checkpoint.StorageOwnership is not null && PreparedStorageOwnership.VerifyPartialStructure(
            checkpoint.Plan, checkpoint.StorageOwnership, checkpoint.CreationReceipts).Availability == Core.Abstractions.ObservationAvailability.Available;
        if (checkpoint.StorageOwnership is not null && !completePartitions &&
            (checkpoint.State is not (PreparationState.CreationInProgress or PreparationState.AmbiguousLeftover) || !partialPartitions))
            throw new InvalidDataException("Complete partition ownership is invalid.");
        if (checkpoint.Plan.ReviewedIntent is not null &&
            checkpoint.State is (PreparationState.CreatedAndVerified or PreparationState.ContentStagedAndVerified) &&
            !completePartitions)
            throw new InvalidDataException("Reviewed completed state has no complete partition ownership.");
        if (checkpoint.State == PreparationState.CreationInProgress && (!partialPartitions || completePartitions))
            throw new InvalidDataException("Creation-in-progress checkpoint must contain incomplete verified receipts.");
        if (checkpoint.State is PreparationState.CreatedAndVerified or PreparationState.ContentStagedAndVerified &&
            !completePartitions && assessed.State != PreparationState.CreatedAndVerified)
            throw new InvalidDataException("Preparation creation receipts are incomplete.");
        if (checkpoint.State == PreparationState.Planned && (!checkpoint.CreationReceipts.IsEmpty || checkpoint.Layout is not null || checkpoint.StorageOwnership is not null))
            throw new InvalidDataException("Planned checkpoint contains unverified created state.");
        if (checkpoint.State == PreparationState.AmbiguousLeftover &&
            (checkpoint.Layout is not null ||
             (checkpoint.StorageOwnership is null && !checkpoint.CreationReceipts.IsEmpty)))
            throw new InvalidDataException("Ambiguous checkpoint contains unverified ownership.");
        if (checkpoint.State == PreparationState.ContentStagedAndVerified)
        {
            if ((checkpoint.Layout is null) == (checkpoint.ReviewedSeedContent is null))
                throw new InvalidDataException("Preparation content has no unique receipt kind.");
            if (checkpoint.Layout is { } layout &&
                (!JsonSerializer.SerializeToUtf8Bytes(layout.Plan, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(checkpoint.Plan, Options)) ||
                 !layout.Partitions.SequenceEqual(checkpoint.CreationReceipts) ||
                 layout.ShrinkReceipt != checkpoint.ShrinkReceipt ||
                 !JsonSerializer.SerializeToUtf8Bytes(layout.StorageOwnership, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(checkpoint.StorageOwnership, Options)) ||
                 PreparedLayoutRules.VerifyBootFiles(layout, layout.BootFiles).Availability != Core.Abstractions.ObservationAvailability.Available))
                throw new InvalidDataException("Preparation content evidence is incomplete.");
            if (checkpoint.ReviewedSeedContent is { } seed && !ValidReviewedSeedReceipt(checkpoint, seed))
                throw new InvalidDataException("Reviewed installer seed content evidence is incomplete.");
        }
        if (checkpoint.State != PreparationState.ContentStagedAndVerified &&
            (checkpoint.Layout is not null || checkpoint.ReviewedSeedContent is not null))
            throw new InvalidDataException("Unexpected staged layout.");
    }

    internal static bool ValidReviewedSeedReceipt(PreparationCheckpointV1 checkpoint,
        ReviewedInstallerSeedReceiptV1 seed)
    {
        var owned = checkpoint.StorageOwnership;
        if (checkpoint.Plan.ReviewedIntent is null || owned is null ||
            seed.GenerationId != checkpoint.Plan.GenerationId ||
            seed.Payload == seed.LinuxEsp || seed.Payload.Disk != checkpoint.Plan.TargetDisk ||
            seed.LinuxEsp.Disk != checkpoint.Plan.TargetDisk ||
            seed.BootSpecSha256 is not { Length: 64 } ||
            !seed.BootSpecSha256.All(Uri.IsHexDigit) ||
            seed.PayloadFiles.IsDefaultOrEmpty || seed.IsoFiles.IsDefault)
            return false;
        if (owned.CreatedPartitions.SingleOrDefault(part => part.Role == PreparationRole.Payload)?.Identity !=
                PreparedStorageOwnership.PartitionOf(seed.Payload) ||
            owned.CreatedPartitions.SingleOrDefault(part => part.Role == PreparationRole.LinuxEsp)?.Identity !=
                PreparedStorageOwnership.PartitionOf(seed.LinuxEsp) ||
            owned.CreatedPartitions.SingleOrDefault(part => part.Role == PreparationRole.Iso)?.Identity !=
                (seed.Iso is null ? null : PreparedStorageOwnership.PartitionOf(seed.Iso)))
            return false;
        var required = new[]
        {
            @"\EFI\BOOT\BOOTX64.EFI", @"\EFI\BOOT\grubx64.efi", @"\EFI\BOOT\grub.cfg",
            @"\igloo-boot\linux", @"\igloo-boot\initrd", @"\migration-manifest.json",
            @"\igloo-seed-identity.json",
        };
        if (required.Any(path => !seed.PayloadFiles.Any(file =>
                string.Equals(file.RelativePath, path, StringComparison.OrdinalIgnoreCase))) ||
            !ValidFiles(seed.PayloadFiles, seed.Payload.VolumeGuid, allowSelectedDataEmpty: true) ||
            !ValidFiles(seed.IsoFiles, seed.Iso?.VolumeGuid, allowSelectedDataEmpty: false) ||
            (seed.Iso is null) != seed.IsoFiles.IsEmpty)
            return false;
        return true;
    }

    // The seed writer calls this before its first volume write. A receipt with
    // the exact destination paths, maximal positive lengths, and fixed-size
    // digests cannot serialize smaller than the eventual readback receipt.
    // Include the complete planned checkpoint so large plans consume their
    // share of the existing artifact limit too.
    internal static long RequireReviewedSeedReceiptCapacity(PreparationCheckpointV1 created,
        CanonicalVolumeIdentityV1 payload, CanonicalVolumeIdentityV1 linuxEsp,
        CanonicalVolumeIdentityV1? iso, string bootSpecSha256,
        IReadOnlyList<string> payloadPaths, IReadOnlyList<string> isoPaths)
    {
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(linuxEsp);
        ArgumentNullException.ThrowIfNull(payloadPaths);
        ArgumentNullException.ThrowIfNull(isoPaths);
        if (created.State != PreparationState.CreatedAndVerified ||
            (iso is null && isoPaths.Count != 0) ||
            bootSpecSha256 is not { Length: 64 } ||
            !bootSpecSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The reviewed seed receipt has no complete created checkpoint or volume binding.");

        var projected = new ReviewedInstallerSeedReceiptV1(created.Plan.GenerationId,
            payload, linuxEsp, iso, bootSpecSha256,
            ImmutableArray<CanonicalFileIdentityV1>.Empty,
            ImmutableArray<CanonicalFileIdentityV1>.Empty);
        long upperBound = JsonSerializer.SerializeToUtf8Bytes(created with
        {
            State = PreparationState.ContentStagedAndVerified,
            ReviewedSeedContent = projected,
        }, Options).LongLength;
        const string digest = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        AddFiles(payloadPaths, payload.VolumeGuid);
        if (iso is not null) AddFiles(isoPaths, iso.VolumeGuid);
        return upperBound;

        void AddFiles(IReadOnlyList<string> paths, Guid volumeGuid)
        {
            foreach (var path in paths)
            {
                if (!ReviewedSeedPathRules.TrySegments(path, out var segments))
                    throw new InvalidDataException("The reviewed seed receipt contains an unsafe destination path.");
                var canonicalPath = "\\" + string.Join('\\', segments);
                var longest = new CanonicalFileIdentityV1(volumeGuid, canonicalPath,
                    long.MaxValue, digest);
                // One comma per item slightly overestimates each nonempty array.
                upperBound = checked(upperBound +
                    JsonSerializer.SerializeToUtf8Bytes(longest, Options).LongLength + 1);
                if (upperBound > CommunityRecoveryArtifactStore.MaximumArtifactBytes)
                    throw new InvalidDataException("The reviewed seed file receipt exceeds the durable checkpoint capacity.");
            }
        }
    }

    private static bool ValidFiles(ImmutableArray<CanonicalFileIdentityV1> files, Guid? volumeGuid,
        bool allowSelectedDataEmpty) =>
        (volumeGuid is not null || files.IsEmpty) &&
        files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == files.Length &&
        files.All(file => file.VolumeGuid == volumeGuid &&
            (file.Length > 0 || allowSelectedDataEmpty && file.Length == 0 &&
                ReviewedSeedPathRules.AllowsEmptySelectedData(file.RelativePath)) &&
            file.Sha256 is { Length: 64 } && file.Sha256.All(Uri.IsHexDigit) &&
            ReviewedSeedPathRules.TrySegments(file.RelativePath, out _));
}

internal static class ReviewedSeedPathRules
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };

    internal static bool TrySegments(string? relative, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrWhiteSpace(relative) || !relative.StartsWith('\\') ||
            relative.Contains('/', StringComparison.Ordinal) ||
            relative.Contains(':', StringComparison.Ordinal))
            return false;
        var split = relative.Split('\\').Skip(1).ToArray();
        if (split.Length == 0 || split.Any(segment => segment.Length is 0 or > 255 ||
            segment is "." or ".." ||
            char.IsWhiteSpace(segment[0]) || char.IsWhiteSpace(segment[^1]) ||
            segment[^1] == '.' ||
            segment.Any(ch => ch < 32 || ch is '<' or '>' or '"' or '|' or '?' or '*' or '/' or '\\' or ':') ||
            DeviceNames.Contains(segment.Split('.')[0])))
            return false;
        segments = split;
        return true;
    }

    internal static bool AllowsEmptySelectedData(string? relative) =>
        TrySegments(relative, out var segments) && segments.Length >= 2 &&
        (segments[0] == "files" || segments[0] == "browser-profiles");
}
