using System.Text.Json;
using Igloo.Core.Execution;
using Igloo.Fleet.Domain;

namespace Igloo.Fleet.Agent;

// Deliberately has no host registration, endpoint or production adapter.
public sealed class RecoveryCoordinator<TState>(IExecutionJournal<AuthorizedMutation<TState>> journal,
    IRecoverableMutationAdapter<TState> adapter) where TState : notnull
{
    public Task<MutationOutcome> RecoverAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        ExecutionRecord<AuthorizedMutation<TState>> intent;
        using (journal.Acquire())
        {
            var records = journal.Read(operationId);
            intent = records.Count > 0 ? records[0]
                : throw new InvalidOperationException("No durable intent exists to recover.");
        }
        return RunAsync(intent.Correlation, intent.Intent, inspectOnly: true, cancellationToken);
    }

    public async Task<MutationOutcome> RunAsync(ExecutionCorrelation correlation,
        AuthorizedMutation<TState> mutation, bool inspectOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(mutation);
        if (correlation.ExecutionId == Guid.Empty || correlation.PlanId == Guid.Empty ||
            correlation.DeviceId == Guid.Empty || correlation.AgentId == Guid.Empty ||
            correlation.ApprovalId == Guid.Empty || correlation.EvidenceId == Guid.Empty ||
            correlation.ProfileRevisionId == Guid.Empty || correlation.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(correlation.EvidenceHash))
            throw new ArgumentException("Complete approved execution correlation is required.", nameof(correlation));
        Validate(mutation);
        using var guard = journal.Acquire();
        var history = journal.Read(correlation.OperationId);
        foreach (var entry in history)
            if (entry.Correlation != correlation || entry.Intent != mutation)
                throw new InvalidDataException("Operation authority cannot change.");
        long sequence = history.Count;
        var observation = await InspectAsync().ConfigureAwait(false);
        if (history.Count == 0)
        {
            if (inspectOnly) throw new InvalidOperationException("No durable intent exists to recover.");
            Append(ExecutionRecordKind.Intent, null, null);
            if (!Exact(observation, mutation.Before))
                return RequireRecovery(observation, "Initial state does not match authority.");
            ApplyDisposition? disposition = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                disposition = await adapter.ApplyAsync(mutation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Exception text may contain private host data. Only independently observed state
                // can establish whether an effect occurred; persist no raw exception message.
                Append(ExecutionRecordKind.ApplyInterrupted, null, "Apply interrupted; inspection required.");
            }
            observation = await InspectAsync().ConfigureAwait(false);
            if (disposition == ApplyDisposition.RejectedBeforeMutation && Exact(observation, mutation.Before))
            {
                Append(ExecutionRecordKind.RejectedWithoutMutation, observation, "Adapter rejected before mutation; unchanged state observed.");
                return MutationOutcome.FailedWithoutMutation;
            }
        }
        if (Exact(observation, mutation.Before))
        {
            Append(ExecutionRecordKind.ObservedNotApplied, observation, null);
            return MutationOutcome.NotApplied;
        }
        MutationVerification<TState> verification;
        try
        {
            verification = await adapter.VerifyAsync(mutation.After, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return RequireRecovery(new(default, "Verification unavailable."), "Independent verification failed.");
        }
        if (verification.Disposition == VerificationDisposition.Exact && Exact(verification.Observation, mutation.After))
        {
            Append(ExecutionRecordKind.VerifiedReceipt, verification.Observation, null);
            return MutationOutcome.AppliedAndVerified;
        }
        return RequireRecovery(verification.Observation, "Neither authorized state can be proven.");

        void Append(ExecutionRecordKind kind, MutationObservation<TState>? observed, string? detail) =>
            journal.Append(new(1, sequence++, correlation, mutation, kind, DateTimeOffset.UtcNow,
                observed is null ? null : JsonSerializer.Serialize(observed), detail));
        MutationOutcome RequireRecovery(MutationObservation<TState> observed, string detail)
        {
            Append(ExecutionRecordKind.RecoveryRequired, observed, detail);
            return MutationOutcome.AmbiguousRecoveryRequired;
        }
        async Task<MutationObservation<TState>> InspectAsync()
        {
            try { return await adapter.InspectAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new(default, "Inspection unavailable."); }
        }
    }

    private static bool Exact(MutationObservation<TState> observation, TState expected) =>
        observation.Uncertainty is null && observation.State is not null &&
        EqualityComparer<TState>.Default.Equals(observation.State, expected);

    private static void Validate(AuthorizedMutation<TState> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation.Before);
        ArgumentNullException.ThrowIfNull(mutation.After);
        if (EqualityComparer<TState>.Default.Equals(mutation.Before, mutation.After))
            throw new ArgumentException("Mutation states must differ.", nameof(mutation));
        if (mutation.Before is StorageState before && mutation.After is StorageState after &&
            (before.Target != after.Target || string.IsNullOrWhiteSpace(before.Target.HardwareId) ||
             before.Target.DiskId == Guid.Empty || before.Target.PartitionId == Guid.Empty ||
             before.Target.PartitionNumber <= 0 || before.OffsetBytes < 0 || after.OffsetBytes < 0 ||
             before.LengthBytes <= 0 || after.LengthBytes <= 0 ||
             before.LengthBytes > long.MaxValue - before.OffsetBytes || after.LengthBytes > long.MaxValue - after.OffsetBytes ||
             string.IsNullOrWhiteSpace(before.FileSystem) || string.IsNullOrWhiteSpace(after.FileSystem)))
            throw new ArgumentException("Exact stable target and valid geometry are required.", nameof(mutation));
        if (mutation.Before is BootState bootBefore && mutation.After is BootState bootAfter &&
            (string.IsNullOrWhiteSpace(bootBefore.Format) || string.IsNullOrWhiteSpace(bootBefore.Scope) ||
             bootBefore.Format != bootAfter.Format || bootBefore.Scope != bootAfter.Scope ||
             string.IsNullOrWhiteSpace(bootBefore.CanonicalContent) || string.IsNullOrWhiteSpace(bootAfter.CanonicalContent)))
            throw new ArgumentException("Exact versioned boot scope and snapshot content are required.", nameof(mutation));
    }
}
