using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Web.Services;

/// <summary>Records every mutating operator call in the console audit log with the console account that made it.</summary>
internal sealed class AuditingFleetOperatorClient(IFleetOperatorClient inner, ConsoleAuditLog log, ConsoleSession session) : IFleetOperatorClient
{
    public bool IsSampleData => inner.IsSampleData;

    public Task<OperatorResult<ServerHealth>> GetHealthAsync(CancellationToken cancellationToken = default) =>
        inner.GetHealthAsync(cancellationToken);

    public Task<OperatorResult<IReadOnlyList<TrustedDeviceView>>> GetEnrollmentsAsync(CancellationToken cancellationToken = default) =>
        inner.GetEnrollmentsAsync(cancellationToken);

    public async Task<OperatorResult> ChangeTrustAsync(Guid deviceId, AgentTrustStatus status, CancellationToken cancellationToken = default)
    {
        var result = await inner.ChangeTrustAsync(deviceId, status, cancellationToken).ConfigureAwait(false);
        await RecordAsync("ChangeTrust", deviceId.ToString("D"), result.Failure, status.ToString()).ConfigureAwait(false);
        return result;
    }

    public Task<OperatorResult<IReadOnlyList<EnrollmentTokenInfo>>> GetEnrollmentTokensAsync(CancellationToken cancellationToken = default) =>
        inner.GetEnrollmentTokensAsync(cancellationToken);

    public async Task<OperatorResult<EnrollmentTokenCreated>> CreateEnrollmentTokenAsync(int lifetimeMinutes, CancellationToken cancellationToken = default)
    {
        var result = await inner.CreateEnrollmentTokenAsync(lifetimeMinutes, cancellationToken).ConfigureAwait(false);
        // The token plaintext is never written anywhere; only its identifier is.
        await RecordAsync("CreateEnrollmentToken", result.Value?.TokenId.ToString("D"), result.Failure, $"lifetimeMinutes={lifetimeMinutes}").ConfigureAwait(false);
        return result;
    }

    public async Task<OperatorResult> RevokeEnrollmentTokenAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var result = await inner.RevokeEnrollmentTokenAsync(tokenId, cancellationToken).ConfigureAwait(false);
        await RecordAsync("RevokeEnrollmentToken", tokenId.ToString("D"), result.Failure, null).ConfigureAwait(false);
        return result;
    }

    public Task<OperatorResult<IReadOnlyList<MigrationProfileRevision>>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        inner.GetProfilesAsync(cancellationToken);

    public async Task<OperatorResult<MigrationProfileRevision>> CreateProfileAsync(MigrationProfileSpec spec, CancellationToken cancellationToken = default)
    {
        var result = await inner.CreateProfileAsync(spec, cancellationToken).ConfigureAwait(false);
        await RecordAsync("CreateProfile", result.Value?.RevisionId.ToString("D"), result.Failure, spec?.Name).ConfigureAwait(false);
        return result;
    }

    public async Task<OperatorResult<MigrationProfileRevision>> AddProfileRevisionAsync(Guid profileId, MigrationProfileSpec spec, CancellationToken cancellationToken = default)
    {
        var result = await inner.AddProfileRevisionAsync(profileId, spec, cancellationToken).ConfigureAwait(false);
        await RecordAsync("AddProfileRevision", result.Value?.RevisionId.ToString("D"), result.Failure, profileId.ToString("D")).ConfigureAwait(false);
        return result;
    }

    public Task<OperatorResult<IReadOnlyList<ReadOnlyWorkItem>>> GetWorkItemsAsync(CancellationToken cancellationToken = default) =>
        inner.GetWorkItemsAsync(cancellationToken);

    public async Task<OperatorResult<ReadOnlyWorkItem>> RequestWorkAsync(RequestReadOnlyWork request, CancellationToken cancellationToken = default)
    {
        var result = await inner.RequestWorkAsync(request, cancellationToken).ConfigureAwait(false);
        await RecordAsync("RequestWork", result.Value?.WorkItemId.ToString("D"), result.Failure, request?.Type.ToString()).ConfigureAwait(false);
        return result;
    }

    public Task<OperatorResult<IReadOnlyList<DryRunEvidence>>> GetEvidenceAsync(CancellationToken cancellationToken = default) =>
        inner.GetEvidenceAsync(cancellationToken);

    public Task<OperatorResult<IReadOnlyList<PlanApproval>>> GetApprovalsAsync(CancellationToken cancellationToken = default) =>
        inner.GetApprovalsAsync(cancellationToken);

    public async Task<OperatorResult<PlanApproval>> ApproveAsync(ApprovePlanRequest request, CancellationToken cancellationToken = default)
    {
        var result = await inner.ApproveAsync(request, cancellationToken).ConfigureAwait(false);
        await RecordAsync("Approve", result.Value?.ApprovalId.ToString("D"), result.Failure, request?.DryRunId.ToString("D")).ConfigureAwait(false);
        return result;
    }

    public async Task<OperatorResult> RevokeApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default)
    {
        var result = await inner.RevokeApprovalAsync(approvalId, cancellationToken).ConfigureAwait(false);
        await RecordAsync("RevokeApproval", approvalId.ToString("D"), result.Failure, null).ConfigureAwait(false);
        return result;
    }

    public Task<OperatorResult<IReadOnlyList<PreparedMigrationPlan>>> GetPlansAsync(CancellationToken cancellationToken = default) =>
        inner.GetPlansAsync(cancellationToken);

    public async Task<OperatorResult<PreparedMigrationPlan>> PrepareAsync(Guid approvalId, CancellationToken cancellationToken = default)
    {
        var result = await inner.PrepareAsync(approvalId, cancellationToken).ConfigureAwait(false);
        await RecordAsync("PreparePlan", result.Value?.PlanId.ToString("D"), result.Failure, approvalId.ToString("D")).ConfigureAwait(false);
        return result;
    }

    public Task<OperatorResult<IReadOnlyList<FleetAuditEvent>>> GetAuditAsync(CancellationToken cancellationToken = default) =>
        inner.GetAuditAsync(cancellationToken);

    private async Task RecordAsync(string action, string? target, OperatorFailure? failure, string? detail)
    {
        var account = await session.GetAccountAsync().ConfigureAwait(false);
        var outcome = failure is null
            ? "Succeeded"
            : failure.Error is null ? $"Failed:{failure.Kind}" : $"Rejected:{failure.Error.Code}";
        log.Append(new ConsoleAuditEntry(DateTimeOffset.UtcNow, account.Username, action, target, outcome, detail));
    }
}
