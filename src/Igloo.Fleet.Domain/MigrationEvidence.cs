using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Domain;

public sealed record MigrationEvidence(AssessmentResult Assessment, DateTimeOffset ReceivedAt);

public static class EligibilityDecision
{
    public static EligibilityStatus Evaluate(IEnumerable<PreflightCheckResult> checks, AssessmentOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var results = checks.ToArray();
        if (results.Any(c => c.Status == CheckStatus.Blocked))
            return EligibilityStatus.Blocked;
        if (outcome != AssessmentOutcome.Assessed || results.Length == 0 ||
            results.Any(c => c.Status is CheckStatus.Warning or CheckStatus.Unknown))
            return EligibilityStatus.NeedsReview;
        return EligibilityStatus.ReadyForReview;
    }
}

// Storage contracts live above the implementation and can support durable storage later.
public interface IDeviceRepository
{
    bool Register(AgentRegistrationRequest registration, DateTimeOffset now);
    RegisteredDevice? Find(Guid deviceId);
    bool Heartbeat(AgentHeartbeat heartbeat, DateTimeOffset now);
}

public interface IAssessmentRepository
{
    bool Add(MigrationEvidence evidence);
    MigrationEvidence? Find(Guid assessmentId);
}
