using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;

namespace Igloo.Fleet.Server;

internal static class RequestValidation
{
    internal static bool Identity(DeviceIdentity? identity) =>
        identity is not null && identity.AgentId != Guid.Empty && identity.DeviceId != Guid.Empty;

    internal static bool Version(string? version) =>
        version is { Length: > 0 and <= 32 } && System.Version.TryParse(version, out _);

    internal static bool Assessment(AssessmentResult result)
    {
        if (!Identity(result.Identity) || result.AssessmentId == Guid.Empty ||
            !Version(result.AgentVersion) || !Version(result.IglooVersion) || result.PreflightSchema != 1 ||
            result.Inventory is null || result.Inventory.TotalRamBytes < 0 || result.Inventory.DiskCount is < 0 or > 1024 ||
            result.Checks.IsDefault || result.Checks.Length > 7 ||
            result.Checks.Any(c => c is null || !Enum.IsDefined(c.Id) || !Enum.IsDefined(c.Status)) ||
            result.Checks.Select(c => c.Id).Distinct().Count() != result.Checks.Length ||
            !Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.Eligibility) ||
            result.StartedAt == default || result.CompletedAt < result.StartedAt ||
            result.CompletedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            return false;
        if (result.Outcome == AssessmentOutcome.Assessed &&
            Enumerable.Range(0, 6).Any(id => !result.Checks.Any(c => (int)c.Id == id)))
            return false;
        if (result.Outcome == AssessmentOutcome.LocalExecutionFailed &&
            (!result.Checks.IsEmpty || result.Inventory != new DeviceInventorySummary(0, 0)))
            return false;
        return result.Eligibility == EligibilityDecision.Evaluate(result.Checks, result.Outcome);
    }
}
