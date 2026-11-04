using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Web.Services;

/// <summary>
/// The seventeen operator routes of the trusted Fleet Server, and nothing else.
/// There is no execution, no device-scoped read and no server-side filtering or paging.
/// </summary>
internal interface IFleetOperatorClient
{
    bool IsSampleData { get; }

    Task<OperatorResult<ServerHealth>> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<OperatorResult<IReadOnlyList<TrustedDeviceView>>> GetEnrollmentsAsync(CancellationToken cancellationToken = default);

    Task<OperatorResult> ChangeTrustAsync(Guid deviceId, AgentTrustStatus status, CancellationToken cancellationToken = default);

    Task<OperatorResult<IReadOnlyList<EnrollmentTokenInfo>>> GetEnrollmentTokensAsync(CancellationToken cancellationToken = default);

    Task<OperatorResult<EnrollmentTokenCreated>> CreateEnrollmentTokenAsync(int lifetimeMinutes, CancellationToken cancellationToken = default);

    Task<OperatorResult> RevokeEnrollmentTokenAsync(Guid tokenId, CancellationToken cancellationToken = default);

    Task<OperatorResult<IReadOnlyList<MigrationProfileRevision>>> GetProfilesAsync(CancellationToken cancellationToken = default);

    Task<OperatorResult<MigrationProfileRevision>> CreateProfileAsync(MigrationProfileSpec spec, CancellationToken cancellationToken = default);

    Task<OperatorResult<MigrationProfileRevision>> AddProfileRevisionAsync(Guid profileId, MigrationProfileSpec spec, CancellationToken cancellationToken = default);

    Task<OperatorResult<IReadOnlyList<ReadOnlyWorkItem>>> GetWorkItemsAsync(CancellationToken cancellationToken = default);

    Task<OperatorResult<ReadOnlyWorkItem>> RequestWorkAsync(RequestReadOnlyWork request, CancellationToken cancellationToken = default);

    Task<OperatorResult<IReadOnlyList<DryRunEvidence>>> GetEvidenceAsync(CancellationToken cancellationToken = default);

    Task<OperatorResult<IReadOnlyList<PlanApproval>>> GetApprovalsAsync(CancellationToken cancellationToken = default);

    Task<OperatorResult<PlanApproval>> ApproveAsync(ApprovePlanRequest request, CancellationToken cancellationToken = default);

    Task<OperatorResult> RevokeApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default);

    Task<OperatorResult<IReadOnlyList<PreparedMigrationPlan>>> GetPlansAsync(CancellationToken cancellationToken = default);

    Task<OperatorResult<PreparedMigrationPlan>> PrepareAsync(Guid approvalId, CancellationToken cancellationToken = default);

    Task<OperatorResult<IReadOnlyList<FleetAuditEvent>>> GetAuditAsync(CancellationToken cancellationToken = default);
}
