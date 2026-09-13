using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Domain;

public sealed record EnrollmentTokenRecord(Guid TokenId, string Hash, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc, string CreatedBy, bool Consumed, bool Revoked);
public sealed record IssuedAgentCertificate(string CertificatePem, string CertificateHash, DateTimeOffset ExpiresAtUtc);

// Transaction-scoped state. Never exposed directly through an API. Immutable payloads preserve history.
public sealed class PlanningState
{
    public Dictionary<Guid, EnrollmentTokenRecord> Tokens { get; init; } = [];
    public Dictionary<Guid, TrustedDeviceView> Devices { get; init; } = [];
    public Dictionary<Guid, MigrationProfileRevision> Profiles { get; init; } = [];
    public Dictionary<Guid, ReadOnlyWorkItem> Work { get; init; } = [];
    public Dictionary<Guid, DryRunEvidence> Evidence { get; init; } = [];
    public Dictionary<Guid, PlanApproval> Approvals { get; init; } = [];
    public Dictionary<Guid, PreparedMigrationPlan> Plans { get; init; } = [];
    public System.Collections.ObjectModel.Collection<FleetAuditEvent> Audit { get; init; } = [];
}

public interface IPlanningStore
{
    T Read<T>(Func<PlanningState, T> read);
    T Transact<T>(Func<PlanningState, T> update);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "Every planning rejection must retain its stable protocol error code.")]
public sealed class PlanningException(FleetErrorCode code) : Exception(code.ToString())
{
    public FleetErrorCode Code { get; } = code;
}
