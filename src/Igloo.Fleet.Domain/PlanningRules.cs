using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Domain;

public static class PlanningRules
{
    public static PlanningDecision Evaluate(ReadOnlyWorkResult result, MigrationProfileSpec? profile = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var reasons = new HashSet<PlanningReason>();
        var unknown = result.Assessment.Outcome != AssessmentOutcome.Assessed;
        var blocked = result.Assessment.Checks.Any(c => c.Status == CheckStatus.Blocked);
        if (unknown) reasons.Add(PlanningReason.LocalReadFailure);
        if (blocked) reasons.Add(PlanningReason.PrecheckFailed);
        if (result.Assessment.Checks.Any(c => c.Id == CheckId.BitLocker && c.Status == CheckStatus.Unknown))
            reasons.Add(PlanningReason.BitLockerUnknown);
        if (result.Assessment.Checks.Any(c => c.Status is CheckStatus.Warning or CheckStatus.Unknown))
            reasons.Add(PlanningReason.CompatibilityReview);
        if (result.Planning is { } facts)
        {
            foreach (var reason in facts.Reasons) reasons.Add(reason);
            if (profile?.RequireSecureBoot == true && !facts.SecureBootEnabled)
                reasons.Add(PlanningReason.SecureBootRequirementNotMet);
            if (reasons.Contains(PlanningReason.LocalReadFailure)) unknown = true;
            if (!facts.TargetSupported) { reasons.Add(PlanningReason.UnsupportedDistro); blocked = true; }
            if (facts.AvailableBytes is null) reasons.Add(PlanningReason.DiskSelectionUnknown);
            else if (facts.AvailableBytes < facts.RequiredBytes) { reasons.Add(PlanningReason.InsufficientSpace); blocked = true; }
            if (reasons.Contains(PlanningReason.HardwareUnsupported) || reasons.Contains(PlanningReason.SecureBootRequirementNotMet))
                blocked = true;
        }
        else { unknown = true; reasons.Add(PlanningReason.MissingEvidence); }
        return new(Guid.NewGuid(), blocked ? PlanningEligibility.Blocked : unknown ? PlanningEligibility.Unknown :
            reasons.Count > 0 ? PlanningEligibility.NeedsReview : PlanningEligibility.Eligible,
            [.. reasons.Order()], 1);
    }
}
