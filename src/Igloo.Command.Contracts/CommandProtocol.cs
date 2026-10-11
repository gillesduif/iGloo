using System.Text.Json;
using System.Text.Json.Serialization;

namespace Igloo.Command.Contracts;

public static class CommandProtocol
{
    public const int Version = 1;
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public enum CommandKind { Query, Plan, Mutation }
public enum RiskClass { None, Low, Elevated, Destructive }
public enum AuthorityRequirement { User, Administrator, ExplicitApproval }
public enum CommandErrorCode
{
    InvalidRequest, UnsupportedCapability, UnsupportedVersion, PreconditionFailed,
    StateChanged, ApprovalRequired, PolicyDenied, ResourceBusy, NotSupported,
    VerificationFailed, RecoveryRequired, Cancelled, InternalFailure,
}

public sealed record CapabilityDescriptor(
    string Id, int Version, int InputSchemaVersion, int ResultSchemaVersion,
    CommandKind Kind, RiskClass Risk, bool SupportsDryRun,
    AuthorityRequirement Authority, IReadOnlyList<string> SupportedProviders,
    bool RequiresObservedState = false)
{
    public string InputSchemaId => $"igloo.command.{Id}.input.v{InputSchemaVersion}";
    public string ResultSchemaId => $"igloo.command.{Id}.result.v{ResultSchemaVersion}";
}

public sealed record CommandRequest(
    int ProtocolVersion, string CapabilityId, int CapabilityVersion,
    Guid RequestId, Guid CorrelationId, JsonElement Input,
    bool DryRun = false, string? IdempotencyKey = null,
    string? ObservedStateFingerprint = null, Guid? OperationId = null);

public sealed record CommandError(CommandErrorCode Code, string Message, string? DetailCode = null);
public sealed record CommandWarning(string Code, string Message);
public sealed record ExecutionEvidence(string Code, string Value);

public sealed record CommandResult(
    int ProtocolVersion, string CapabilityId, int CapabilityVersion,
    Guid RequestId, Guid CorrelationId, Guid? OperationId, bool Succeeded,
    JsonElement? Data, CommandError? Error,
    IReadOnlyList<CommandWarning> Warnings,
    IReadOnlyList<ExecutionEvidence> Evidence,
    string? ObservedStateFingerprint = null);

public sealed record CommandProgress(Guid RequestId, Guid? OperationId,
    string Phase, long? Completed = null, long? Total = null);

/// <summary>One line of the CLI machine stream. Exactly one Result terminates a request.</summary>
public sealed record CommandStreamEvent(
    string Type, int ProtocolVersion, Guid RequestId, Guid CorrelationId,
    string? Product = null, string? ProductVersion = null,
    CommandProgress? Progress = null, CommandResult? Result = null);

public sealed record SystemInspection(bool IsUefi, bool SecureBootEnabled,
    bool TpmPresent, string BitLockerState, long TotalRamBytes,
    int DiskCount, IReadOnlyList<InspectionFinding> Findings);
public sealed record InspectionFinding(string Code, string Severity);
public sealed record DetailedSystemInspection(SystemInspection Summary,
    IReadOnlyList<InspectionDisk> Disks, int LinuxInstallationCount,
    int InstallerLeftoverCount);
/// <summary>Display and advisory facts only. Ordinals are not physical target identities.</summary>
public sealed record InspectionDisk(int Ordinal, long TotalBytes, long FreeBytes,
    string PartitionStyle, bool HasBootPartition, bool HasSystemPartition,
    IReadOnlyList<InspectionPartition> Partitions);
public sealed record InspectionPartition(int Index, long SizeBytes, long OffsetBytes,
    bool IsBoot, bool IsSystem, long ShrinkableBytes, string FileSystemCategory);
public sealed record DistroSummary(string Id, string DisplayName, string? Status);
public sealed record DistroList(IReadOnlyList<DistroSummary> Distros);
public sealed record DistroSelection(string DistroId);
public sealed record CapabilitySelection(string CapabilityId);
public sealed record DistroCompatibility(string DistroId,
    IReadOnlyList<InspectionFinding> Findings, bool HasBlockers);
public sealed record IsoAcquired(string DistroId, bool Sha256Verified,
    bool GpgVerified, long SizeBytes);
public sealed record ArtifactAcquired(string ArtifactId, string DistroId, string Sha256,
    bool GpgVerified, long SizeBytes);
public sealed record ArtifactSelection(string ArtifactId);
public sealed record ArtifactStatus(string ArtifactId, string DistroId, string Sha256,
    bool GpgVerified, long SizeBytes, bool Present, bool VerifiedNow);

public sealed record MigrationInspection(int WindowsBootDiskCount, long? ObservedAvailableBytes,
    IReadOnlyList<string> BlockerCodes, bool CanRequestAdvisoryPlan);
public sealed record MigrationPlanInput(string DistroId, long RequestedLinuxBytes);
public sealed record MigrationPlanResult(string DistroId, long RequiredBytes,
    long? ObservedAvailableBytes, IReadOnlyList<string> BlockerCodes,
    bool Executable, string Disposition);
public enum MigrationCapacityDisposition
{
    KnownAvailable, Insufficient, PhysicalDiskTooSmall, Unknown, AmbiguousTarget,
}
public sealed record MigrationPlanResultV2(Guid PlanId, string DistroId, long RequiredBytes,
    long? ObservedAvailableBytes, MigrationCapacityDisposition CapacityState,
    IReadOnlyList<string> BlockerCodes, string ObservedStateFingerprint,
    string ProviderRevisionFingerprint, bool Executable, string Disposition,
    string ObservedMachineFingerprint);
public sealed record PlanSelection(Guid PlanId);
public sealed record MigrationTargetBinding(Guid PlanId, Guid TargetId,
    string BoundStateFingerprint, bool Executable);
public sealed record MigrationAllocationInput(Guid PlanId, Guid TargetId,
    long RequestedShrinkBytes);
/// <summary>An exact advisory shrink intent, not an installer layout or execution grant.</summary>
public sealed record MigrationAllocationResult(Guid PlanId, Guid TargetId, Guid AllocationId,
    string Scope, long RequestedShrinkBytes, long PlannedShrinkBytes,
    long CurrentPartitionBytes, long ResultingPartitionBytes,
    long NewFreeOffsetBytes, string ObservedLayoutRevision,
    MigrationCapacityDisposition CapacityState, IReadOnlyList<string> WarningCodes,
    bool Executable);
public sealed record RecoveryInspection(string Support, string ReasonCode,
    bool HasExactSnapshot);
public sealed record OperationSelection(Guid OperationId);
public sealed record OperationStatus(Guid OperationId, string CapabilityId, string State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string CurrentStage,
    IReadOnlyList<string> EvidenceCodes, string? FailureCode, string? ContinuationCode);
