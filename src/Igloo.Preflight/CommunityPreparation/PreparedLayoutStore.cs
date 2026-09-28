using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Preparation;
using Igloo.Preflight.CommunityRecovery;

namespace Igloo.Preflight.CommunityPreparation;

public sealed record PreparationCheckpointV1(int SchemaVersion, PreparationState State,
    PreparationPlanV1 Plan, ImmutableArray<PreparedPartitionV1> CreationReceipts, PreparedLayoutV1? Layout)
{
    public PreparedStorageOwnershipV1? StorageOwnership { get; init; }
}
public sealed record PreparationCheckpointReference(Guid ArtifactId, Guid GenerationId, string Sha256);

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
        var assessed = PreparedLayoutRules.AssessOwnership(checkpoint.Plan, checkpoint.Plan.GenerationId,
            checkpoint.CreationReceipts, checkpoint.CreationReceipts.Select(r => r.Identity).ToImmutableArray(), checkpoint.Plan.WindowsEsp);
        var completePartitions = checkpoint.StorageOwnership is not null && PreparedStorageOwnership.VerifyStructure(
            new PreparedLayoutV1(1, checkpoint.Plan, checkpoint.CreationReceipts, "", [])
            { StorageOwnership = checkpoint.StorageOwnership }).Availability == Core.Abstractions.ObservationAvailability.Available;
        var partialPartitions = checkpoint.StorageOwnership is not null && PreparedStorageOwnership.VerifyPartialStructure(
            checkpoint.Plan, checkpoint.StorageOwnership, checkpoint.CreationReceipts).Availability == Core.Abstractions.ObservationAvailability.Available;
        if (checkpoint.StorageOwnership is not null && !completePartitions &&
            (checkpoint.State != PreparationState.CreationInProgress || !partialPartitions))
            throw new InvalidDataException("Complete partition ownership is invalid.");
        if (checkpoint.State == PreparationState.CreationInProgress && (!partialPartitions || completePartitions))
            throw new InvalidDataException("Creation-in-progress checkpoint must contain incomplete verified receipts.");
        if (checkpoint.State is PreparationState.CreatedAndVerified or PreparationState.ContentStagedAndVerified &&
            !completePartitions && assessed.State != PreparationState.CreatedAndVerified)
            throw new InvalidDataException("Preparation creation receipts are incomplete.");
        if (checkpoint.State == PreparationState.Planned && (!checkpoint.CreationReceipts.IsEmpty || checkpoint.Layout is not null || checkpoint.StorageOwnership is not null))
            throw new InvalidDataException("Planned checkpoint contains unverified created state.");
        if (checkpoint.State == PreparationState.ContentStagedAndVerified && (checkpoint.Layout is null ||
            !JsonSerializer.SerializeToUtf8Bytes(checkpoint.Layout.Plan, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(checkpoint.Plan, Options)) ||
            !checkpoint.Layout.Partitions.SequenceEqual(checkpoint.CreationReceipts) ||
            !JsonSerializer.SerializeToUtf8Bytes(checkpoint.Layout.StorageOwnership, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(checkpoint.StorageOwnership, Options)) ||
            PreparedLayoutRules.VerifyBootFiles(checkpoint.Layout, checkpoint.Layout.BootFiles).Availability != Core.Abstractions.ObservationAvailability.Available))
            throw new InvalidDataException("Preparation content evidence is incomplete.");
        if (checkpoint.State != PreparationState.ContentStagedAndVerified && checkpoint.Layout is not null)
            throw new InvalidDataException("Unexpected staged layout.");
    }
}
