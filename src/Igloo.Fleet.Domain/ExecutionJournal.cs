namespace Igloo.Fleet.Domain;

public sealed record ExecutionCorrelation(Guid ExecutionId, Guid PlanId, Guid DeviceId, Guid AgentId,
    Guid ApprovalId, Guid EvidenceId, string EvidenceHash, Guid ProfileRevisionId, Guid OperationId);
public enum ExecutionRecordKind { Intent, ObservedNotApplied, VerifiedReceipt, RecoveryRequired, ApplyInterrupted, RejectedWithoutMutation }
public sealed record ExecutionRecord<T>(int Version, long Sequence, ExecutionCorrelation Correlation,
    T Intent, ExecutionRecordKind Kind, DateTimeOffset AtUtc, string? ObservationJson, string? Detail);

// A session holds an exclusive cross-process lock until disposal. Intent appends must be
// committed durably before returning; a lock is never reclaimed by a wall-clock lease.
public interface IExecutionJournal<T>
{
    IDisposable Acquire();
    IReadOnlyList<ExecutionRecord<T>> Read(Guid operationId);
    void Append(ExecutionRecord<T> record);
}
