namespace Igloo.Core.Execution;

// Restoration is itself a mutation, with its own durable intent and read-back receipt.
// Wrap this adapter in the same journal coordinator used for a forward operation.
public sealed class BootRestorationAdapter(IRecoverableBootAdapter adapter, BootSnapshot snapshot)
    : IRecoverableMutationAdapter<BootState>
{
    public Task<MutationObservation<BootState>> InspectAsync(CancellationToken cancellationToken) =>
        adapter.InspectAsync(cancellationToken);

    public async Task<ApplyDisposition> ApplyAsync(AuthorizedMutation<BootState> mutation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (snapshot.Support != BootRecoverySupport.Exact || mutation.After != snapshot.State ||
            mutation.Before.Format != snapshot.State.Format || mutation.Before.Scope != snapshot.State.Scope)
            return ApplyDisposition.RejectedBeforeMutation;
        var actual = await adapter.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (actual.Uncertainty is not null || actual.State != mutation.Before)
            return ApplyDisposition.RejectedBeforeMutation;
        await adapter.RestoreAsync(snapshot, mutation.Before, cancellationToken).ConfigureAwait(false);
        return ApplyDisposition.Attempted;
    }

    public Task<MutationVerification<BootState>> VerifyAsync(BootState expected, CancellationToken cancellationToken) =>
        adapter.VerifyAsync(expected, cancellationToken);
}
