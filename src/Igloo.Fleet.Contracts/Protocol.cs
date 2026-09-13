using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Igloo.Fleet.Contracts;

public readonly record struct FleetProtocolVersion(int Major, int Minor)
{
    public static FleetProtocolVersion Current => new(1, 0);
    [JsonIgnore]
    public bool IsSupported => this == Current;
}

public sealed record DeviceIdentity(Guid DeviceId, Guid AgentId);
public sealed record AgentCapabilities(bool ReadOnlyAssessment);
public sealed record AgentRegistrationRequest(
    FleetProtocolVersion Protocol, DeviceIdentity Identity, string AgentVersion,
    string IglooVersion, AgentCapabilities Capabilities);
public sealed record AgentRegistrationResponse(FleetProtocolVersion Protocol, DeviceIdentity Identity);
public sealed record AgentHeartbeat(FleetProtocolVersion Protocol, DeviceIdentity Identity, AgentCapabilities Capabilities);
public sealed record FleetVersion(FleetProtocolVersion Protocol, bool DevelopmentOnly);
public sealed record RegisteredDevice(AgentRegistrationRequest Registration, DateTimeOffset LastHeartbeat);

public enum CheckId { Firmware, SecureBoot, Tpm, BitLocker, Memory, Disks, UnknownFinding }
public enum CheckStatus { Passed, Information, Warning, Blocked, Unknown }
public enum EligibilityStatus { ReadyForReview, NeedsReview, Blocked }
public enum AssessmentOutcome { Assessed, LocalExecutionFailed }
public enum FleetErrorCode
{
    CommunicationFailure, UnsupportedAgentVersion, UnsupportedProtocol, InvalidRequest,
    PreflightBlocker, LocalExecutionFailure, ServerFailure, EnrollmentFailure, NotFound, Conflict,

}

public sealed record FleetError(FleetErrorCode Code, string Diagnostic);
public sealed record PreflightCheckResult(CheckId Id, CheckStatus Status);
public sealed record DeviceInventorySummary(long TotalRamBytes, int DiskCount);

// Deliberately no manifest, paths, hostname, arbitrary findings, exception text or user state.
public sealed record AssessmentResult(
    FleetProtocolVersion Protocol, Guid AssessmentId, DeviceIdentity Identity,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, string AgentVersion, string IglooVersion,
    int PreflightSchema, DeviceInventorySummary Inventory, ImmutableArray<PreflightCheckResult> Checks,
    EligibilityStatus Eligibility, AssessmentOutcome Outcome);
