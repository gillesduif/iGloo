using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;

namespace Igloo.Fleet.Agent.Targets;

public sealed record StableTargetIdentity(string DiskUniqueId, uint UniqueIdFormat, Guid DiskGuid,
    Guid PartitionGuid, Guid VolumeGuid);
public sealed record TargetStructure(ulong DiskSize, uint PartitionStyle, uint LogicalSectorSize,
    uint PhysicalSectorSize, ulong PartitionOffset, ulong PartitionSize, Guid GptType, string FileSystem, string Label);
public sealed record TargetLocators(uint DiskNumber, uint PartitionNumber, char DriveLetter);

// Explicit input from a future approval workflow. There is deliberately no factory that
// turns a Prepared plan and fresh observations into an approved binding.
public sealed record ExactTargetBinding(int SchemaVersion, Guid PlanId, Guid ApprovalId,
    DeviceIdentity Endpoint, Guid ProfileRevisionId, string EvidenceHash,
    StableTargetIdentity Stable, TargetStructure Structure, TargetLocators Informational)
{
    public const int CurrentSchemaVersion = 1;
    public string TargetFingerprint => EvidenceIntegrity.Hash(new { SchemaVersion, Stable, Structure });
}

public enum TargetMatch { ExactMatch, Changed, Missing, Ambiguous, Unsupported, ObservationUnavailable }
public enum TargetMismatchReason
{
    BindingRequired, BindingSchemaUnsupported, InvalidBinding, PlanBindingChanged, InventoryUnavailable,
    IdentityUnavailable, ReducedDiskIdentity, PartitionStyleUnsupported, DuplicateDiskIdentity,
    DiskIdentityChanged, DiskMissing, DiskStructureChanged, PartitionIdentityChanged, PartitionMissing,
    DuplicatePartitionIdentity, PartitionGeometryChanged, VolumeIdentityChanged, VolumeMissing,
    DuplicateVolumeIdentity, VolumeOwnerUnproven, FileSystemChanged, BitLockerUnavailable, BitLockerWrongVolume,
}
public sealed record TargetRevalidation(TargetMatch Outcome, TargetMismatchReason? Reason);
