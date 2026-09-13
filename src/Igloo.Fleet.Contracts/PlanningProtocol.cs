using System.Collections.Immutable;

namespace Igloo.Fleet.Contracts;

// Trusted endpoints use v2. Phase 0 v1 endpoints and payloads remain unchanged.
public static class PlanningProtocol
{
    public static FleetProtocolVersion Version => new(2, 0);
}
public enum PlanningCapability { PreflightV1, MigrationDryRunV1, EvidenceV1, ProfileSchemaV1 }
public enum AgentTrustStatus { Active, Disabled, Revoked }
public enum ReadOnlyWorkType { RunAssessment, RunMigrationDryRun }
public enum ReadOnlyWorkStatus { Pending, Leased, Completed, Expired }
public enum PlanningEligibility { Eligible, NeedsReview, Blocked, Unknown }
public enum PlanningReason
{
    PrecheckFailed, BitLockerUnknown, InsufficientSpace, UnsupportedDistro,
    SecureBootRequirementNotMet, HardwareUnsupported, CompatibilityReview,
    MissingEvidence, LocalReadFailure, DiskSelectionUnknown,
}
public enum ApprovalState { Approved, Revoked, Expired, Superseded }
public enum PreparedPlanState { Prepared, Expired, Invalidated, Superseded }

public sealed record EnrollmentRequest(FleetProtocolVersion Protocol, string Token, string SigningRequestPem);
public sealed record EnrollmentResponse(DeviceIdentity Identity, Guid EnrollmentId, string CertificatePem);
public sealed record EnrollmentTokenCreated(Guid TokenId, string Token, DateTimeOffset ExpiresAtUtc);
public sealed record EnrollmentTokenInfo(Guid TokenId, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
    string CreatedBy, bool Consumed, bool Revoked);
public sealed record CreateEnrollmentTokenRequest(int LifetimeMinutes);
public sealed record ChangeAgentTrustRequest(AgentTrustStatus Status);
public sealed record TrustedHeartbeat(FleetProtocolVersion Protocol, string AgentVersion, string IglooVersion,
    ImmutableArray<PlanningCapability> Capabilities);
public sealed record MigrationProfileSpec(string Name, string DistroId, long MinimumAvailableBytes, bool RequireSecureBoot);
public sealed record MigrationProfileRevision(Guid ProfileId, Guid RevisionId, int Revision, int SchemaVersion,
    MigrationProfileSpec Spec, DateTimeOffset CreatedAtUtc);
public sealed record RequestReadOnlyWork(Guid DeviceId, ReadOnlyWorkType Type, Guid? ProfileRevisionId, int LifetimeMinutes);
public sealed record ReadOnlyWorkItem(Guid WorkItemId, DeviceIdentity Identity, ReadOnlyWorkType Type,
    DateTimeOffset CreatedAtUtc, DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresAtUtc,
    ReadOnlyWorkStatus Status, int AttemptCount, Guid CorrelationId, int PayloadVersion,
    MigrationProfileRevision? Profile, Guid? LeaseId, DateTimeOffset? LeaseExpiresAtUtc);
public sealed record DryRunFacts(bool TargetSupported, bool SecureBootEnabled, long? AvailableBytes,
    long RequiredBytes, string? PluginHash, ImmutableArray<PlanningReason> Reasons);
public sealed record ReadOnlyWorkResult(FleetProtocolVersion Protocol, Guid WorkItemId, Guid LeaseId,
    Guid CorrelationId, Guid? ProfileRevisionId, AssessmentResult Assessment, DryRunFacts? Planning);
public sealed record PlanningDecision(Guid DecisionId, PlanningEligibility Status,
    ImmutableArray<PlanningReason> Reasons, int RulesVersion);
public sealed record DryRunEvidence(Guid DryRunId, ReadOnlyWorkResult Result, string EvidenceHash,
    DateTimeOffset ReceivedAtUtc, PlanningDecision Decision, ImmutableArray<PlanningCapability> CapabilitySnapshot);
public sealed record ApprovePlanRequest(Guid DryRunId, string EvidenceHash, Guid ProfileRevisionId,
    Guid DecisionId, string? ReviewReason);
public sealed record PlanApproval(Guid ApprovalId, DeviceIdentity Identity, Guid DryRunId, Guid ProfileRevisionId,
    Guid DecisionId, string EvidenceHash, DateTimeOffset ApprovedAtUtc, DateTimeOffset ExpiresAtUtc,
    string ApprovedBy, string? ReviewReason, ApprovalState Status);
public sealed record PreparePlanRequest(Guid ApprovalId);
public sealed record PreparedMigrationPlan(Guid PlanId, DeviceIdentity Identity, Guid ProfileRevisionId,
    Guid DryRunId, Guid DecisionId, Guid ApprovalId, string EvidenceHash, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc, string IglooVersion, string DistroId, string PluginHash,
    PreparedPlanState Status, string SafetyBoundary);
public sealed record FleetAuditEvent(Guid EventId, DateTimeOffset AtUtc, string ActorType, string ActorId,
    string Action, Guid TargetId, Guid CorrelationId);
public sealed record TrustedDeviceView(DeviceIdentity Identity, Guid EnrollmentId, string CertificateHash,
    DateTimeOffset CertificateExpiresAtUtc, AgentTrustStatus Status, DateTimeOffset? LastHeartbeatUtc,
    TrustedHeartbeat? Heartbeat);
