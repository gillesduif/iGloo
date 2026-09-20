using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Domain;

public sealed record PlanValidity(PreparedMigrationPlan? Plan, bool IsValid, FleetErrorCode? Error, DateTimeOffset EvaluatedAtUtc);

// Evaluates a transaction snapshot without refreshing it or writing audit events.
public static class PlanningValidity
{
    public static FleetErrorCode? DeviceError(TrustedDeviceView device, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Status == AgentTrustStatus.Revoked) return FleetErrorCode.CertificateRevoked;
        if (device.Status == AgentTrustStatus.Disabled) return FleetErrorCode.AgentDisabled;
        return device.CertificateExpiresAtUtc <= now ? FleetErrorCode.CertificateInvalid : null;
    }

    public static FleetErrorCode? EvidenceError(PlanningState state, DryRunEvidence evidence, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evidence);
        var identity = evidence.Result.Assessment.Identity;
        if (!state.Devices.TryGetValue(identity.DeviceId, out var device)) return FleetErrorCode.NotFound;
        if (device.Identity != identity) return FleetErrorCode.CertificateInvalid;
        if (DeviceError(device, now) is { } error) return error;
        if (!state.Work.TryGetValue(evidence.DryRunId, out var work)) return FleetErrorCode.NotFound;
        if (work.ExpiresAtUtc <= now || evidence.ReceivedAtUtc.AddHours(4) <= now || work.Profile is null ||
            state.Profiles.Values.Any(p => p.ProfileId == work.Profile.ProfileId && p.Revision > work.Profile.Revision))
            return FleetErrorCode.ApprovalStale;
        if (state.Audit.Any(a => a.Action == "AssessmentReceived" && !state.Evidence.ContainsKey(a.TargetId))) return FleetErrorCode.ApprovalStale;
        var latest = state.Audit.LastOrDefault(a => a.Action == "AssessmentReceived" &&
            state.Evidence[a.TargetId].Result.Assessment.Identity.DeviceId == work.Identity.DeviceId);
        return latest?.TargetId != evidence.DryRunId ? FleetErrorCode.ApprovalStale : null;
    }

    public static ApprovalState ApprovalStatus(PlanningState state, PlanApproval approval, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.Status != ApprovalState.Approved) return approval.Status;
        if (approval.ExpiresAtUtc <= now) return ApprovalState.Expired;
        return !state.Evidence.TryGetValue(approval.DryRunId, out var evidence) || EvidenceError(state, evidence, now) is not null
            ? ApprovalState.Superseded : ApprovalState.Approved;
    }

    public static PreparedPlanState PlanStatus(PreparedMigrationPlan plan, ApprovalState approval, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Status != PreparedPlanState.Prepared) return plan.Status;
        return plan.ExpiresAtUtc <= now ? PreparedPlanState.Expired : approval == ApprovalState.Revoked
            ? PreparedPlanState.Invalidated : approval != ApprovalState.Approved ? PreparedPlanState.Superseded : PreparedPlanState.Prepared;
    }

    public static PlanValidity Evaluate(PlanningState state, Guid planId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Plans.TryGetValue(planId, out var plan)) return new(null, false, FleetErrorCode.NotFound, now);
        if (!state.Approvals.TryGetValue(plan.ApprovalId, out var approval)) return new(plan, false, FleetErrorCode.ApprovalStale, now);
        if (approval.Identity != plan.Identity || approval.ProfileRevisionId != plan.ProfileRevisionId || approval.DryRunId != plan.DryRunId ||
            approval.DecisionId != plan.DecisionId || approval.EvidenceHash != plan.EvidenceHash)
            return new(plan, false, FleetErrorCode.ApprovalStale, now);
        var valid = PlanStatus(plan, ApprovalStatus(state, approval, now), now) == PreparedPlanState.Prepared;
        return new(plan, valid, valid ? null : FleetErrorCode.ApprovalStale, now);
    }
}
