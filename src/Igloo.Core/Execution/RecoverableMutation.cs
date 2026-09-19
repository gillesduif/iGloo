namespace Igloo.Core.Execution;

public enum MutationOutcome { NotApplied, AppliedAndVerified, AmbiguousRecoveryRequired, FailedWithoutMutation }
public enum ApplyDisposition { Attempted, RejectedBeforeMutation }
public enum VerificationDisposition { Exact, Different, Unsupported }

// Stable hardware and partition identifiers are required; ordinal disk numbers are not authority.
public sealed record StorageTarget(string HardwareId, Guid DiskId, Guid PartitionId, int PartitionNumber);
public sealed record StorageState(StorageTarget Target, long OffsetBytes, long LengthBytes,
    string FileSystem, string Label);
public sealed record AuthorizedMutation<TState>(TState Before, TState After) where TState : notnull;
public sealed record MutationObservation<TState>(TState? State, string? Uncertainty) where TState : notnull;
public sealed record MutationVerification<TState>(VerificationDisposition Disposition,
    MutationObservation<TState> Observation) where TState : notnull;

public interface IRecoverableMutationAdapter<TState> where TState : notnull
{
    Task<MutationObservation<TState>> InspectAsync(CancellationToken cancellationToken);
    // Recheck the exact before-state at the mutation boundary. RejectedBeforeMutation
    // is permitted only when no effect was attempted; throws make no such promise.
    Task<ApplyDisposition> ApplyAsync(AuthorizedMutation<TState> mutation, CancellationToken cancellationToken);
    Task<MutationVerification<TState>> VerifyAsync(TState expected, CancellationToken cancellationToken);
}

// Opaque snapshot content is immutable and adapter-specific. Scope/version must cover every
// affected entry, order, next-boot setting and EFI file; incomplete captures cannot authorize apply.
public sealed record BootState(string Format, string Scope, string CanonicalContent);
public enum BootRecoverySupport { Exact, Partial, Unsupported }
public sealed record BootSnapshot(BootState State, BootRecoverySupport Support);
public sealed record BootRestoration(BootRecoverySupport Support, MutationVerification<BootState> Verification);
public interface IRecoverableBootAdapter : IRecoverableMutationAdapter<BootState>
{
    Task<BootSnapshot> CaptureAsync(CancellationToken cancellationToken);
    Task<BootRestoration> RestoreAsync(BootSnapshot snapshot, BootState expectedBefore, CancellationToken cancellationToken);
}
