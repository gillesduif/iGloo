using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Execution;

namespace Igloo.Core.Recovery;

public enum RecoveryWorkflow { WindowsBootConfiguration, CommunityDirectInstallBootRegistration }
public enum RecoveryRelevance { Required, RelevantOpaque, ObservedUnrelated, UnsupportedRelevant }
public enum RecoveryEvidenceKind { BcdObject, FirmwareEntry }
public enum RecoveryExclusionReason { RequiredDependency, MutationSlot, OutsideActiveDependencyClosure, UnmodifiedSelectionListEntry }
public sealed record RecoveryEvidenceClassification(RecoveryEvidenceKind Kind, string Identity,
    RecoveryRelevance Relevance, RecoveryExclusionReason Reason);

// Mutation slots are explicit identifiers, including presently absent slots. No description matching,
// partition selection, dynamic free-index search, approval, or permission is encoded here.
// Direct-install orchestration has not proved its complete footprint; that workflow remains Partial
// even if a caller sets MutationFootprintResolved. WindowsBootConfiguration covers configuration
// observations only, never partition transitions or staged file contents.
public sealed record RecoveryScopeV1(int SchemaVersion, RecoveryWorkflow Workflow,
    bool MutationFootprintResolved, bool IncludeRtc, ushort WindowsBootEntry,
    ImmutableArray<Guid> BcdMutationObjects, ImmutableArray<ushort> FirmwareMutationEntries)
{
    public static RecoveryScopeV1 UnresolvedDirectInstall(ushort windowsBootEntry) =>
        new(1, RecoveryWorkflow.CommunityDirectInstallBootRegistration, false, true, windowsBootEntry, [], []);
}

public enum WinReImageAssurance { ContentIdentityOnly }
public sealed record WinReConfigurationV1(int SectionVersion, Observation<bool> Enabled,
    Observation<Guid> RecoveryLoaderId, Observation<CanonicalVolumeIdentityV1> RecoveryVolume,
    Observation<CanonicalFileIdentityV1> Image, WinReImageAssurance ImageAssurance = WinReImageAssurance.ContentIdentityOnly);

public sealed record EspAssociationV1(int SectionVersion, CanonicalVolumeIdentityV1 EspVolume,
    CanonicalVolumeIdentityV1 WindowsVolume, ushort BootEntryIndex, Guid BcdBootManagerId,
    CanonicalFileIdentityV1 BootManagerExecutable);

// Capture diagnostics and transient locators are deliberately outside the semantic state hash.
public sealed record RecoveryCaptureMetadataV1(ImmutableArray<RecoveryVolumeLocatorV1> VolumeLocators,
    ImmutableArray<string> DiagnosticCodes);

public sealed record RecoverySnapshotV1(int SchemaVersion, DateTimeOffset CapturedAtUtc,
    RecoveryScopeV1 Scope, Observation<RecoveryTargetBindingV1> Binding,
    BcdRecoveryGraphV1 BootConfiguration, FirmwareSnapshotV1 Firmware,
    Observation<EspAssociationV1> Esp, WinReConfigurationV1 WinRe, RtcRegistryStateV1? Rtc,
    Observation<bool> IndependentReadback, RecoveryCaptureMetadataV1 Metadata)
{
    public const int CurrentSchemaVersion = 1;
    public string CanonicalHash { get; init; } = "";
}

public enum RecoverySnapshotIssueCode
{
    SchemaUnsupported, InvalidStructure, HashMismatch, ScopeUnresolved, BindingUnavailable,
    InvalidBinding, BcdDependency, RequiredBcdElementMissing, BcdAssociationMismatch,
    FirmwareUnavailable, FirmwareMalformed, FirmwareIdentityMismatch, NativeAttributesUnavailable,
    EspUnavailable, EspAssociationMismatch, WinReUnavailable, WinReAssociationMismatch,
    RtcUnavailable, ReadbackUnavailable,
}
public sealed record RecoverySnapshotIssue(RecoverySnapshotIssueCode Code,
    ObservationAvailability Availability, string? Element = null);
public sealed record RecoverySnapshotAssessment(BootRecoverySupport Support,
    ImmutableArray<RecoverySnapshotIssue> Issues, ImmutableArray<RecoveryEvidenceClassification> Evidence);
public enum RecoverySnapshotMatch { ExactMatch, Changed, Missing, ObservationUnavailable, Unsupported, Ambiguous }
public sealed record RecoverySnapshotComparison(RecoverySnapshotMatch Result, ImmutableArray<RecoverySnapshotIssue> Issues);

// Capture only. There are intentionally no Apply, Restore, authorization or persistence methods.
public interface IRecoverySnapshotCapture
{
    RecoverySnapshotV1 Capture(RecoveryScopeV1 scope, Guid canonicalTargetVolume);
}
