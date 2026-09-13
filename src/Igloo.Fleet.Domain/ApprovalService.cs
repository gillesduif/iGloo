using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Domain;

public sealed partial class PlanningService
{
    public PlanApproval Approve(ApprovePlanRequest request, string actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        return store.Transact(state =>
        {
            var evidence = Get(state.Evidence, request.DryRunId);
            var work = Get(state.Work, evidence.DryRunId);
            if (work.Profile is null || evidence.Result.Planning is not { TargetSupported: true } ||
                evidence.EvidenceHash != request.EvidenceHash || evidence.Decision.DecisionId != request.DecisionId ||
                work.Profile.RevisionId != request.ProfileRevisionId)
                throw new PlanningException(FleetErrorCode.ApprovalNotAllowed);
            CurrentEvidence(state, evidence);
            if (evidence.Decision.Status == PlanningEligibility.Blocked)
                throw new PlanningException(FleetErrorCode.EligibilityBlocked);
            if (evidence.Decision.Status == PlanningEligibility.Unknown ||
                (evidence.Decision.Status == PlanningEligibility.NeedsReview && string.IsNullOrWhiteSpace(request.ReviewReason)) ||
                request.ReviewReason?.Length > 500)
                throw new PlanningException(FleetErrorCode.ApprovalNotAllowed);
            if (state.Approvals.Values.Any(a => a.DryRunId == request.DryRunId && a.Status == ApprovalState.Approved))
                throw new PlanningException(FleetErrorCode.Conflict);
            var approval = new PlanApproval(Guid.NewGuid(), work.Identity, evidence.DryRunId, work.Profile.RevisionId,
                evidence.Decision.DecisionId, evidence.EvidenceHash, Now, Now.AddHours(4), actor, request.ReviewReason, ApprovalState.Approved);
            state.Approvals.Add(approval.ApprovalId, approval);
            Audit(state, "Operator", actor, "PlanApproved", approval.ApprovalId, work.CorrelationId);
            return approval;
        });
    }

    public void RevokeApproval(Guid id, string actor) => store.Transact(state =>
    {
        var approval = Get(state.Approvals, id);
        state.Approvals[id] = approval with { Status = ApprovalState.Revoked };
        foreach (var plan in state.Plans.Values.Where(p => p.ApprovalId == id).ToArray())
        {
            state.Plans[plan.PlanId] = plan with { Status = PreparedPlanState.Invalidated };
            Audit(state, "Operator", actor, "PreparedPlanInvalidated", plan.PlanId, state.Work[plan.DryRunId].CorrelationId);
        }
        Audit(state, "Operator", actor, "ApprovalRevoked", id, state.Work[approval.DryRunId].CorrelationId);
        return true;
    });

    public PreparedMigrationPlan Prepare(Guid approvalId, string actor) => store.Transact(state =>
    {
        var approval = Get(state.Approvals, approvalId);
        if (approval.Status != ApprovalState.Approved || approval.ExpiresAtUtc <= Now)
            throw new PlanningException(FleetErrorCode.ApprovalStale);
        var evidence = Get(state.Evidence, approval.DryRunId);
        CurrentEvidence(state, evidence);
        var existing = state.Plans.Values.SingleOrDefault(p => p.ApprovalId == approvalId);
        if (existing is not null) return existing;
        var profile = Get(state.Profiles, approval.ProfileRevisionId);
        var plan = new PreparedMigrationPlan(Guid.NewGuid(), approval.Identity, profile.RevisionId,
            approval.DryRunId, approval.DecisionId, approval.ApprovalId, approval.EvidenceHash, Now, approval.ExpiresAtUtc,
            evidence.Result.Assessment.IglooVersion, profile.Spec.DistroId, evidence.Result.Planning!.PluginHash!,
            PreparedPlanState.Prepared, "Non-executable planning artifact. Recovery readiness and fresh execution authorization required in a future phase.");
        state.Plans.Add(plan.PlanId, plan);
        Audit(state, "Operator", actor, "PreparedPlanCreated", plan.PlanId, state.Work[approval.DryRunId].CorrelationId);
        return plan;
    });

    public IReadOnlyList<PlanApproval> Approvals() => store.Transact(state =>
    {
        RefreshValidity(state);
        return state.Approvals.Values.ToArray();
    });
    public IReadOnlyList<PreparedMigrationPlan> Plans() => store.Transact(state =>
    {
        RefreshValidity(state);
        return state.Plans.Values.ToArray();
    });

    private void CurrentEvidence(PlanningState state, DryRunEvidence evidence)
    {
        Authorized(state, evidence.Result.Assessment.Identity);
        var work = state.Work[evidence.DryRunId];
        if (work.ExpiresAtUtc <= Now || evidence.ReceivedAtUtc.AddHours(4) <= Now ||
            work.Profile is null || state.Profiles.Values.Any(p => p.ProfileId == work.Profile.ProfileId && p.Revision > work.Profile.Revision))
            throw new PlanningException(FleetErrorCode.ApprovalStale);
        // Every later accepted assessment or dry-run invalidates approval, even if superficially compatible.
        var latest = state.Audit.LastOrDefault(a => a.Action == "AssessmentReceived" &&
            state.Evidence[a.TargetId].Result.Assessment.Identity.DeviceId == work.Identity.DeviceId);
        if (latest?.TargetId != evidence.DryRunId)
            throw new PlanningException(FleetErrorCode.ApprovalStale);
    }

    private void RefreshValidity(PlanningState state)
    {
        foreach (var approval in state.Approvals.Values.Where(a => a.Status == ApprovalState.Approved).ToArray())
        {
            var status = approval.ExpiresAtUtc <= Now ? ApprovalState.Expired : ApprovalState.Approved;
            try { CurrentEvidence(state, state.Evidence[approval.DryRunId]); }
            catch (PlanningException) { if (status == ApprovalState.Approved) status = ApprovalState.Superseded; }
            if (status != approval.Status)
            {
                state.Approvals[approval.ApprovalId] = approval with { Status = status };
                Audit(state, "Server", "validity", "ApprovalInvalidated", approval.ApprovalId, state.Work[approval.DryRunId].CorrelationId);
            }
        }
        foreach (var plan in state.Plans.Values.Where(p => p.Status == PreparedPlanState.Prepared).ToArray())
        {
            var approval = state.Approvals[plan.ApprovalId];
            var status = plan.ExpiresAtUtc <= Now ? PreparedPlanState.Expired :
                approval.Status == ApprovalState.Revoked ? PreparedPlanState.Invalidated :
                approval.Status != ApprovalState.Approved ? PreparedPlanState.Superseded : PreparedPlanState.Prepared;
            if (status != plan.Status)
            {
                state.Plans[plan.PlanId] = plan with { Status = status };
                Audit(state, "Server", "validity", "PreparedPlanInvalidated", plan.PlanId, state.Work[plan.DryRunId].CorrelationId);
            }
        }
    }
}
