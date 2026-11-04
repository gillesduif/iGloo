using System.Security.Cryptography;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Web.Services;

/// <summary>
/// Development-only client over <see cref="SampleFleetData"/>. Mutations follow the Server's
/// operator rules closely enough that every page state can be exercised without a Fleet Server.
/// </summary>
internal sealed class SampleFleetOperatorClient(SampleFleetData data) : IFleetOperatorClient
{
    private const string Operator = "engineering-operator";

    public bool IsSampleData => true;

    public Task<OperatorResult<ServerHealth>> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OperatorResult<ServerHealth>.Success(new ServerHealth(PlanningProtocol.Version, "PreparedOnly")));

    public Task<OperatorResult<IReadOnlyList<TrustedDeviceView>>> GetEnrollmentsAsync(CancellationToken cancellationToken = default) =>
        List(d => d.Enrollments.Values.ToArray());

    public Task<OperatorResult> ChangeTrustAsync(Guid deviceId, AgentTrustStatus status, CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Transact(d =>
        {
            if (!d.Enrollments.TryGetValue(deviceId, out var device))
            {
                return Rejected(FleetErrorCode.NotFound);
            }

            if (device.Status == AgentTrustStatus.Revoked)
            {
                return Rejected(FleetErrorCode.CertificateRevoked);
            }

            d.Enrollments[deviceId] = device with { Status = status };
            if (status != AgentTrustStatus.Active)
            {
                Invalidate(d, deviceId, null);
            }

            return OperatorResult.Success;
        }));

    public Task<OperatorResult<IReadOnlyList<EnrollmentTokenInfo>>> GetEnrollmentTokensAsync(CancellationToken cancellationToken = default) =>
        List(d => d.Tokens.Values.ToArray());

    public Task<OperatorResult<EnrollmentTokenCreated>> CreateEnrollmentTokenAsync(int lifetimeMinutes, CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Transact(d =>
        {
            if (lifetimeMinutes is < 1 or > 60)
            {
                return OperatorResult<EnrollmentTokenCreated>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.InvalidRequest, "InvalidRequest"), 400);
            }

            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            d.Tokens[id] = new EnrollmentTokenInfo(id, now, now.AddMinutes(lifetimeMinutes), Operator, false, false);
            d.Record("Operator", Operator, "EnrollmentTokenCreated", id, id, now);
            // Sample plaintext: the same shape as a real token, never usable against a Server.
            return OperatorResult<EnrollmentTokenCreated>.Success(new EnrollmentTokenCreated(id, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now.AddMinutes(lifetimeMinutes)));
        }));

    public Task<OperatorResult> RevokeEnrollmentTokenAsync(Guid tokenId, CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Transact(d =>
        {
            if (!d.Tokens.TryGetValue(tokenId, out var token))
            {
                return Rejected(FleetErrorCode.NotFound);
            }

            if (token.Consumed)
            {
                return Rejected(FleetErrorCode.EnrollmentTokenConsumed);
            }

            d.Tokens[tokenId] = token with { Revoked = true };
            d.Record("Operator", Operator, "EnrollmentTokenRevoked", tokenId, tokenId);
            return OperatorResult.Success;
        }));

    public Task<OperatorResult<IReadOnlyList<MigrationProfileRevision>>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        List(d => d.Profiles.Values.ToArray());

    public Task<OperatorResult<MigrationProfileRevision>> CreateProfileAsync(MigrationProfileSpec spec, CancellationToken cancellationToken = default) =>
        SaveProfileAsync(null, spec);

    public Task<OperatorResult<MigrationProfileRevision>> AddProfileRevisionAsync(Guid profileId, MigrationProfileSpec spec, CancellationToken cancellationToken = default) =>
        SaveProfileAsync(profileId, spec);

    public Task<OperatorResult<IReadOnlyList<ReadOnlyWorkItem>>> GetWorkItemsAsync(CancellationToken cancellationToken = default) =>
        List(d => d.Work.Values.ToArray());

    public Task<OperatorResult<ReadOnlyWorkItem>> RequestWorkAsync(RequestReadOnlyWork request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(data.Transact(d =>
        {
            if (!d.Enrollments.TryGetValue(request.DeviceId, out var device))
            {
                return OperatorResult<ReadOnlyWorkItem>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.NotFound, "NotFound"), 400);
            }

            if (device.Status != AgentTrustStatus.Active)
            {
                return OperatorResult<ReadOnlyWorkItem>.Failed(OperatorFailureKind.Rejected,
                    new FleetError(device.Status == AgentTrustStatus.Revoked ? FleetErrorCode.CertificateRevoked : FleetErrorCode.AgentDisabled, device.Status.ToString()), 401);
            }

            if (request.LifetimeMinutes is < 1 or > 1440)
            {
                return OperatorResult<ReadOnlyWorkItem>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.InvalidRequest, "InvalidRequest"), 400);
            }

            var required = request.Type == ReadOnlyWorkType.RunAssessment
                ? new[] { PlanningCapability.PreflightV1, PlanningCapability.EvidenceV1 }
                : [PlanningCapability.PreflightV1, PlanningCapability.MigrationDryRunV1, PlanningCapability.EvidenceV1, PlanningCapability.ProfileSchemaV1];
            if (device.Heartbeat is null || required.Any(c => !device.Heartbeat.Capabilities.Contains(c)))
            {
                return OperatorResult<ReadOnlyWorkItem>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.CapabilityUnsupported, "CapabilityUnsupported"), 400);
            }

            MigrationProfileRevision? profile = null;
            if (request.Type == ReadOnlyWorkType.RunMigrationDryRun)
            {
                if (request.ProfileRevisionId is null || !d.Profiles.TryGetValue(request.ProfileRevisionId.Value, out profile))
                {
                    return OperatorResult<ReadOnlyWorkItem>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.NotFound, "NotFound"), 400);
                }

                if (d.Profiles.Values.Any(p => p.ProfileId == profile.ProfileId && p.Revision > profile.Revision))
                {
                    return OperatorResult<ReadOnlyWorkItem>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.ProfileInvalid, "ProfileInvalid"), 400);
                }
            }
            else if (request.ProfileRevisionId.HasValue)
            {
                return OperatorResult<ReadOnlyWorkItem>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.InvalidRequest, "InvalidRequest"), 400);
            }

            var now = DateTimeOffset.UtcNow;
            var work = new ReadOnlyWorkItem(Guid.NewGuid(), device.Identity, request.Type, now, now, now.AddMinutes(request.LifetimeMinutes),
                ReadOnlyWorkStatus.Pending, 0, Guid.NewGuid(), 1, profile, null, null);
            d.Work[work.WorkItemId] = work;
            d.Record("Operator", Operator, request.Type == ReadOnlyWorkType.RunAssessment ? "AssessmentRequested" : "DryRunRequested", work.WorkItemId, work.CorrelationId, now);
            return OperatorResult<ReadOnlyWorkItem>.Success(work);
        }));
    }

    public Task<OperatorResult<IReadOnlyList<DryRunEvidence>>> GetEvidenceAsync(CancellationToken cancellationToken = default) =>
        List(d => d.Evidence.Values.ToArray());

    public Task<OperatorResult<IReadOnlyList<PlanApproval>>> GetApprovalsAsync(CancellationToken cancellationToken = default) =>
        List(d => d.Approvals.Values.ToArray());

    public Task<OperatorResult<PlanApproval>> ApproveAsync(ApprovePlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(data.Transact(d =>
        {
            if (!d.Evidence.TryGetValue(request.DryRunId, out var evidence) || !d.Work.TryGetValue(evidence.DryRunId, out var work))
            {
                return OperatorResult<PlanApproval>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.NotFound, "NotFound"), 400);
            }

            if (work.Profile is null || evidence.Result.Planning is not { TargetSupported: true } ||
                evidence.EvidenceHash != request.EvidenceHash || evidence.Decision.DecisionId != request.DecisionId ||
                work.Profile.RevisionId != request.ProfileRevisionId)
            {
                return OperatorResult<PlanApproval>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.ApprovalNotAllowed, "ApprovalNotAllowed"), 400);
            }

            if (evidence.Decision.Status == PlanningEligibility.Blocked)
            {
                return OperatorResult<PlanApproval>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.EligibilityBlocked, "EligibilityBlocked"), 400);
            }

            if (evidence.Decision.Status == PlanningEligibility.Unknown ||
                (evidence.Decision.Status == PlanningEligibility.NeedsReview && string.IsNullOrWhiteSpace(request.ReviewReason)) ||
                request.ReviewReason?.Length > 500)
            {
                return OperatorResult<PlanApproval>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.ApprovalNotAllowed, "ApprovalNotAllowed"), 400);
            }

            if (d.Approvals.Values.Any(a => a.DryRunId == request.DryRunId && a.Status == ApprovalState.Approved))
            {
                return OperatorResult<PlanApproval>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.Conflict, "Conflict"), 409);
            }

            var latest = d.Evidence.Values.Where(e => e.Result.Assessment.Identity.DeviceId == work.Identity.DeviceId).MaxBy(e => e.ReceivedAtUtc);
            if (latest?.DryRunId != evidence.DryRunId || d.Profiles.Values.Any(p => p.ProfileId == work.Profile.ProfileId && p.Revision > work.Profile.Revision))
            {
                return OperatorResult<PlanApproval>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.ApprovalStale, "ApprovalStale"), 409);
            }

            var now = DateTimeOffset.UtcNow;
            var approval = new PlanApproval(Guid.NewGuid(), work.Identity, evidence.DryRunId, work.Profile.RevisionId, evidence.Decision.DecisionId,
                evidence.EvidenceHash, now, now.AddHours(4), Operator, request.ReviewReason, ApprovalState.Approved);
            d.Approvals[approval.ApprovalId] = approval;
            d.Record("Operator", Operator, "PlanApproved", approval.ApprovalId, work.CorrelationId, now);
            return OperatorResult<PlanApproval>.Success(approval);
        }));
    }

    public Task<OperatorResult> RevokeApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Transact(d =>
        {
            if (!d.Approvals.TryGetValue(approvalId, out var approval))
            {
                return Rejected(FleetErrorCode.NotFound);
            }

            var now = DateTimeOffset.UtcNow;
            d.Approvals[approvalId] = approval with { Status = ApprovalState.Revoked };
            foreach (var plan in d.Plans.Values.Where(p => p.ApprovalId == approvalId).ToArray())
            {
                d.Plans[plan.PlanId] = plan with { Status = PreparedPlanState.Invalidated };
                d.Record("Operator", Operator, "PreparedPlanInvalidated", plan.PlanId, d.Work[plan.DryRunId].CorrelationId, now);
            }

            d.Record("Operator", Operator, "ApprovalRevoked", approvalId, d.Work[approval.DryRunId].CorrelationId, now);
            return OperatorResult.Success;
        }));

    public Task<OperatorResult<IReadOnlyList<PreparedMigrationPlan>>> GetPlansAsync(CancellationToken cancellationToken = default) =>
        List(d => d.Plans.Values.ToArray());

    public Task<OperatorResult<PreparedMigrationPlan>> PrepareAsync(Guid approvalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Transact(d =>
        {
            if (!d.Approvals.TryGetValue(approvalId, out var approval))
            {
                return OperatorResult<PreparedMigrationPlan>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.NotFound, "NotFound"), 400);
            }

            var now = DateTimeOffset.UtcNow;
            if (approval.Status != ApprovalState.Approved || approval.ExpiresAtUtc <= now)
            {
                return OperatorResult<PreparedMigrationPlan>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.ApprovalStale, "ApprovalStale"), 409);
            }

            var existing = d.Plans.Values.SingleOrDefault(p => p.ApprovalId == approvalId);
            if (existing is not null)
            {
                return OperatorResult<PreparedMigrationPlan>.Success(existing);
            }

            var evidence = d.Evidence[approval.DryRunId];
            var profile = d.Profiles[approval.ProfileRevisionId];
            var plan = new PreparedMigrationPlan(Guid.NewGuid(), approval.Identity, profile.RevisionId, approval.DryRunId, approval.DecisionId,
                approval.ApprovalId, approval.EvidenceHash, now, approval.ExpiresAtUtc, evidence.Result.Assessment.IglooVersion, profile.Spec.DistroId,
                evidence.Result.Planning!.PluginHash!, PreparedPlanState.Prepared,
                "Non-executable planning artifact. Recovery readiness and fresh execution authorization required in a future phase.");
            d.Plans[plan.PlanId] = plan;
            d.Record("Operator", Operator, "PreparedPlanCreated", plan.PlanId, d.Work[approval.DryRunId].CorrelationId, now);
            return OperatorResult<PreparedMigrationPlan>.Success(plan);
        }));

    public Task<OperatorResult<IReadOnlyList<FleetAuditEvent>>> GetAuditAsync(CancellationToken cancellationToken = default) =>
        List(d => d.Audit.ToArray());

    private Task<OperatorResult<IReadOnlyList<T>>> List<T>(Func<SampleFleetData, T[]> read) =>
        Task.FromResult(OperatorResult<IReadOnlyList<T>>.Success(data.Read(read)));

    private Task<OperatorResult<MigrationProfileRevision>> SaveProfileAsync(Guid? profileId, MigrationProfileSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Task.FromResult(data.Transact(d =>
        {
            if (spec.Name is not { Length: > 0 and <= 80 } || spec.DistroId is not ("debian" or "fedora-kde" or "linuxmint-cinnamon") ||
                spec.MinimumAvailableBytes is < 21474836480L or > 1099511627776L)
            {
                return OperatorResult<MigrationProfileRevision>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.ProfileInvalid, "ProfileInvalid"), 400);
            }

            var prior = d.Profiles.Values.Where(p => p.ProfileId == profileId).ToArray();
            if (profileId.HasValue && prior.Length == 0)
            {
                return OperatorResult<MigrationProfileRevision>.Failed(OperatorFailureKind.Rejected, new FleetError(FleetErrorCode.NotFound, "NotFound"), 400);
            }

            var now = DateTimeOffset.UtcNow;
            var profile = new MigrationProfileRevision(profileId ?? Guid.NewGuid(), Guid.NewGuid(), prior.Length + 1, 1, spec, now);
            d.Profiles[profile.RevisionId] = profile;
            Invalidate(d, null, profile.ProfileId);
            d.Record("Operator", Operator, prior.Length == 0 ? "MigrationProfileCreated" : "MigrationProfileRevised", profile.RevisionId, profile.ProfileId, now);
            return OperatorResult<MigrationProfileRevision>.Success(profile);
        }));
    }

    private static void Invalidate(SampleFleetData d, Guid? deviceId, Guid? profileId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var approval in d.Approvals.Values.Where(a => a.Status == ApprovalState.Approved).ToArray())
        {
            var work = d.Work[approval.DryRunId];
            var matches = (deviceId.HasValue && approval.Identity.DeviceId == deviceId.Value) ||
                          (profileId.HasValue && work.Profile?.ProfileId == profileId.Value);
            if (!matches)
            {
                continue;
            }

            d.Approvals[approval.ApprovalId] = approval with { Status = ApprovalState.Superseded };
            d.Record("Server", "input-change", "ApprovalInvalidated", approval.ApprovalId, work.CorrelationId, now);
            foreach (var plan in d.Plans.Values.Where(p => p.ApprovalId == approval.ApprovalId && p.Status == PreparedPlanState.Prepared).ToArray())
            {
                d.Plans[plan.PlanId] = plan with { Status = PreparedPlanState.Superseded };
                d.Record("Server", "input-change", "PreparedPlanInvalidated", plan.PlanId, work.CorrelationId, now);
            }
        }
    }

    private static OperatorResult Rejected(FleetErrorCode code) =>
        OperatorResult.Failed(OperatorFailureKind.Rejected, new FleetError(code, code.ToString()), code is FleetErrorCode.Conflict ? 409 : 400);
}
