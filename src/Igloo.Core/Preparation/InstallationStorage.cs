using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Igloo.Core.Abstractions;

namespace Igloo.Core.Preparation;

// Installation geometry only. These are not Windows provider/volume identities.
public sealed record InstallationDiskV1(Guid GptDiskGuid, ulong SizeBytes, uint LogicalSectorSize);
public sealed record InstallationPartitionV1(InstallationDiskV1 Disk, Guid PartitionGuid,
    Guid PartitionType, ulong OffsetBytes, ulong SizeBytes);
public sealed record InstallationStorageProvenanceV1(string Provider, int Version, string Scope,
    Guid RunId, Guid GenerationId, string EvidenceSha256);
public sealed record InstallerLabPartitionIntentV1(string DevicePath, string DiskDevicePath, Guid PartitionGuid,
    Guid PartitionType, ulong OffsetBytes, ulong SizeBytes);
public sealed record InstallerLabTransitionV1(PreparationRole Role, InstallerLabPartitionIntentV1 Intended,
    string Before, string Created, string Formatted, InstallerFileSystemV1 FileSystem,
    string CreationIntent, string FormatIntent);
public sealed record InstallerLabCreationIntentV1(int SchemaVersion, Guid RunId, Guid GenerationId,
    PreparationRole Role, InstallerLabPartitionIntentV1 Partition, string BeforeSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }
}
public sealed record InstallerLabFormatIntentV1(int SchemaVersion, Guid RunId, Guid GenerationId,
    Guid PartitionGuid, InstallerFileSystemV1 FileSystem, string BeforeSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }
}

// Retains the complete native observations. Reopening/deserializing this is not validation.
public sealed record InstallerLabStorageEvidenceV1(int SchemaVersion, string Provider, string Scope,
    Guid RunId, Guid GenerationId, string HostObservationSha256,
    ImmutableArray<InstallerLabBackingV1> CreatedBackings, ImmutableArray<InstallerLabBackingV1> ReopenedBackings,
    ImmutableArray<InstallerLabGuestDiskV1> GuestDisks, ImmutableArray<InstallerLabTransitionV1> Transitions);

// No public/deserialization constructor. Only the lab transition verifier produces this
// storage-only context. Production plans still require their original Windows v1 types.
public sealed class ValidatedInstallationStorage
{
    internal ValidatedInstallationStorage(InstallationStorageProvenanceV1 provenance,
        InstallerLabAcquisitionV1 acquisition, ImmutableArray<InstallerLabFormatReceiptV1> receipts,
        ImmutableArray<InstallerRuntimePartitionV1> preserved, ImmutableArray<InstallationPartitionV1> closure,
        InstallationContinuationV1? continuation = null)
    {
        Provenance = provenance; Acquisition = acquisition; Receipts = receipts;
        Preserved = preserved; Closure = closure; Continuation = continuation;
    }

    public InstallationStorageProvenanceV1 Provenance { get; }
    public InstallerLabAcquisitionV1 Acquisition { get; }
    public ImmutableArray<InstallerLabFormatReceiptV1> Receipts { get; }
    public ImmutableArray<InstallerRuntimePartitionV1> Preserved { get; }
    public ImmutableArray<InstallationPartitionV1> Closure { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstallationContinuationV1? Continuation { get; }
}

public sealed record InstallationContinuationV1(Guid OperationId, string PredecessorResultSha256, string CheckpointSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DerivationSha256 { get; init; }
}

// Trusted-host development authorization and readbacks, not Windows identities or
// a recovery capability. The protected harness separately verifies the exact envelope.
public sealed record InstallerLabDerivedAuthorizationV1(int Version, string Scope, Guid AttemptId, Guid OperationId,
    Guid GenerationId, Guid FailedOperationId, string CheckpointSha256, string ImportSha256, string CloseSha256,
    string TargetSha256, string JournalSha256);
public sealed record InstallerLabCopyV1(string Serial, ulong SourceDevice, ulong SourceInode, ulong DestinationDevice,
    ulong DestinationInode, ulong Length, string SourceSha256, string ReopenedSha256);
public sealed record InstallerLabDerivationV1(InstallerLabDerivedAuthorizationV1 Authorization,
    string AuthorizationSha256, string CheckpointSha256, ImmutableArray<InstallerLabCopyV1> Copies);

public sealed record InstallerLabInitramfsAuthorizationV1(int Version, string Scope, Guid AttemptId,
    Guid OperationId, Guid GenerationId, string ConfigurationSha256, string CloseSha256,
    string RetentionSha256, string TargetSha256, string JournalSha256);
public sealed record InstallerLabInitramfsDerivationV1(InstallerLabInitramfsAuthorizationV1 Authorization,
    string AuthorizationSha256, ImmutableArray<InstallerLabCopyV1> Copies);

public sealed record InstallerLabUserDataAuthorizationV1(int Version, string Scope, Guid AttemptId,
    Guid OperationId, Guid GenerationId, string InitramfsSha256, string CloseSha256,
    string RetentionSha256, string TargetSha256, string JournalSha256, string TransferSha256);
public sealed record InstallerLabUserDataDerivationV1(InstallerLabUserDataAuthorizationV1 Authorization,
    string AuthorizationSha256, ImmutableArray<InstallerLabCopyV1> Copies, string DeliverySha256,
    string StagedTargetSha256);

public static class InstallationStorage
{
    private static readonly string[] DerivedSerials = ["IGLOO-LAB-TARGET", "IGLOO-LAB-JOURNAL"];
    // Initial copy equality and the later provisioned payload delta are distinct.
    // The trusted lab composition must reopen delivery and verify its exact staged
    // contents before admitting source authority. This is storage ownership only.
    public static Observation<ValidatedInstallationStorage> ContinueLabInitramfsCheckpoint(
        ValidatedInstallationStorage predecessor, InstallerLabUserDataDerivationV1 derivation,
        string authorizationSha256, string hostObservationSha256, ImmutableArray<InstallerLabBackingV1> declared,
        ImmutableArray<InstallerLabBackingV1> reopened, Observation<InstallerRuntimeInventoryV1> fresh,
        Observation<ImmutableArray<InstallerLabGuestDiskV1>> guest)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(derivation);
        var a = derivation.Authorization;
        if (predecessor.Provenance is not { Provider: "IsolatedFileBackedLab", Version: 4, Scope: "InitramfsImage" } ||
            a is not { Version: 1, Scope: "OneInitramfsCheckpointSelectedDocuments" } ||
            a.AttemptId == Guid.Empty || a.OperationId == Guid.Empty || a.AttemptId == predecessor.Provenance.RunId ||
            a.OperationId == predecessor.Continuation?.OperationId || a.GenerationId != predecessor.Provenance.GenerationId ||
            derivation.AuthorizationSha256 != authorizationSha256 || !Hash(authorizationSha256) ||
            new[] { a.InitramfsSha256, a.CloseSha256, a.RetentionSha256, a.TargetSha256, a.JournalSha256,
                a.TransferSha256, derivation.DeliverySha256, derivation.StagedTargetSha256 }.Any(h => !Hash(h)) ||
            derivation.StagedTargetSha256 == a.TargetSha256 || derivation.Copies.IsDefault || derivation.Copies.Length != 2 ||
            !derivation.Copies.Select(c => c.Serial).SequenceEqual(DerivedSerials))
            return Fail<ValidatedInstallationStorage>("UserDataDerivationAuthorizationInvalid");
        var acquired = InstallerLabAcquisition.Correlate(a.AttemptId, hostObservationSha256, declared, reopened, fresh, guest);
        if (acquired.Availability != ObservationAvailability.Available)
            return Observations.Failure<ValidatedInstallationStorage>(acquired.Availability, acquired.Code!);
        var copies = VerifyIndependentCopies(predecessor, acquired.Value, derivation.Copies, a.TargetSha256, a.JournalSha256, true);
        if (copies is not null) return Fail<ValidatedInstallationStorage>(copies);
        var context = new ValidatedInstallationStorage(new("IsolatedFileBackedLab", 5, "SelectedDocumentTrees", a.AttemptId,
            a.GenerationId, Digest(new { predecessor.Provenance, derivation, acquired.Value })), acquired.Value,
            predecessor.Receipts, predecessor.Preserved, predecessor.Closure,
            new(a.OperationId, a.InitramfsSha256, a.RetentionSha256) { DerivationSha256 = Digest(derivation) });
        var valid = Revalidate(context, fresh, guest);
        return valid.Availability == ObservationAvailability.Available ? Observations.Available(context) :
            Observations.Failure<ValidatedInstallationStorage>(valid.Availability, valid.Code!);
    }
    // Separate successor contract. Neither CoreConfiguration nor its historical
    // copy records acquire new meaning. The Debian composition also requires the
    // independently reopened configuration and Close capability before effects.
    public static Observation<ValidatedInstallationStorage> ContinueLabConfiguredCheckpoint(
        ValidatedInstallationStorage predecessor, InstallerLabInitramfsDerivationV1 derivation,
        string authorizationSha256, string hostObservationSha256, ImmutableArray<InstallerLabBackingV1> declared,
        ImmutableArray<InstallerLabBackingV1> reopened, Observation<InstallerRuntimeInventoryV1> fresh,
        Observation<ImmutableArray<InstallerLabGuestDiskV1>> guest)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(derivation);
        var authorization = derivation.Authorization;
        if (predecessor.Provenance is not { Provider: "IsolatedFileBackedLab", Version: 3, Scope: "CoreConfiguration" } ||
            authorization is not { Version: 1, Scope: "OneConfiguredCheckpointInitramfs" } ||
            authorization.AttemptId == Guid.Empty || authorization.OperationId == Guid.Empty ||
            authorization.AttemptId == predecessor.Provenance.RunId ||
            authorization.OperationId == predecessor.Continuation?.OperationId ||
            authorization.GenerationId != predecessor.Provenance.GenerationId ||
            derivation.AuthorizationSha256 != authorizationSha256 || !Hash(authorizationSha256) ||
            new[] { authorization.ConfigurationSha256, authorization.CloseSha256, authorization.RetentionSha256,
                authorization.TargetSha256, authorization.JournalSha256 }.Any(h => !Hash(h)) ||
            derivation.Copies.IsDefault || derivation.Copies.Length != 2 ||
            !derivation.Copies.Select(c => c.Serial).SequenceEqual(DerivedSerials))
            return Fail<ValidatedInstallationStorage>("InitramfsDerivationAuthorizationInvalid");
        var acquired = InstallerLabAcquisition.Correlate(authorization.AttemptId, hostObservationSha256, declared, reopened, fresh, guest);
        if (acquired.Availability != ObservationAvailability.Available)
            return Observations.Failure<ValidatedInstallationStorage>(acquired.Availability, acquired.Code!);
        var copies = VerifyIndependentCopies(predecessor, acquired.Value, derivation.Copies,
            authorization.TargetSha256, authorization.JournalSha256, true);
        if (copies is not null) return Fail<ValidatedInstallationStorage>(copies);
        var context = new ValidatedInstallationStorage(new("IsolatedFileBackedLab", 4, "InitramfsImage",
            authorization.AttemptId, authorization.GenerationId, Digest(new { predecessor.Provenance, derivation, acquired.Value })),
            acquired.Value, predecessor.Receipts, predecessor.Preserved, predecessor.Closure,
            new(authorization.OperationId, authorization.ConfigurationSha256, authorization.RetentionSha256)
            { DerivationSha256 = Digest(derivation) });
        var valid = Revalidate(context, fresh, guest);
        return valid.Availability == ObservationAvailability.Available ? Observations.Available(context) :
            Observations.Failure<ValidatedInstallationStorage>(valid.Availability, valid.Code!);
    }

    private static string? VerifyIndependentCopies(ValidatedInstallationStorage predecessor,
        InstallerLabAcquisitionV1 acquired, ImmutableArray<InstallerLabCopyV1> copies,
        string targetSha256, string journalSha256, bool bindSource)
    {
        var forbidden = predecessor.Acquisition.Disks.Select(d => (d.Backing.HostDevice, d.Backing.HostInode))
            .Concat(copies.Select(c => (c.SourceDevice, c.SourceInode))).ToArray();
        if (copies.Select(c => (c.DestinationDevice, c.DestinationInode)).Distinct().Count() != 2 ||
            copies.Select(c => (c.SourceDevice, c.SourceInode)).Distinct().Count() != 2) return "DerivedCopyAlias";
        foreach (var copy in copies)
        {
            var before = predecessor.Acquisition.Disks.SingleOrDefault(d => d.Backing.Serial == copy.Serial);
            var after = acquired.Disks.SingleOrDefault(d => d.Backing.Serial == copy.Serial);
            var expected = copy.Serial == "IGLOO-LAB-TARGET" ? targetSha256 : journalSha256;
            if (before is null || after is null || copy.SourceInode == 0 || copy.DestinationInode == 0 ||
                forbidden.Contains((copy.DestinationDevice, copy.DestinationInode)) ||
                bindSource && (copy.SourceDevice != before.Backing.HostDevice || copy.SourceInode != before.Backing.HostInode) ||
                copy.SourceSha256 != expected || copy.ReopenedSha256 != expected || copy.Length != before.Backing.Length ||
                after.Backing.HostDevice != copy.DestinationDevice || after.Backing.HostInode != copy.DestinationInode ||
                after.Backing.Length != copy.Length || after.Disk != before.Disk) return "DerivedCopyBindingInvalid";
        }
        return null;
    }
    public static Observation<ValidatedInstallationStorage> ContinueLabCheckpoint(ValidatedInstallationStorage predecessor,
        InstallerLabDerivedAuthorizationV1 authorization, InstallerLabDerivationV1 derivation,
        string authorizationSha256, string hostObservationSha256, ImmutableArray<InstallerLabBackingV1> declared,
        ImmutableArray<InstallerLabBackingV1> reopened, Observation<InstallerRuntimeInventoryV1> fresh,
        Observation<ImmutableArray<InstallerLabGuestDiskV1>> guest)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(derivation);
        if (predecessor.Provenance is not { Provider: "IsolatedFileBackedLab", Version: 2, Scope: "ConfiguredRootImport" } ||
            authorization is not { Version: 1, Scope: "OneCheckpointDerivedCoreConfiguration" } ||
            authorization.AttemptId == Guid.Empty || authorization.OperationId == Guid.Empty ||
            authorization.FailedOperationId == Guid.Empty || authorization.OperationId == authorization.FailedOperationId ||
            authorization.AttemptId == predecessor.Provenance.RunId || authorization.GenerationId != predecessor.Provenance.GenerationId ||
            derivation.Authorization != authorization || derivation.AuthorizationSha256 != authorizationSha256 ||
            !Hash(authorizationSha256) || derivation.CheckpointSha256 != authorization.CheckpointSha256 ||
            new[] { authorization.CheckpointSha256, authorization.ImportSha256, authorization.CloseSha256,
                authorization.TargetSha256, authorization.JournalSha256 }.Any(h => !Hash(h)) ||
            derivation.Copies.IsDefault || derivation.Copies.Length != 2 ||
            !derivation.Copies.Select(c => c.Serial).SequenceEqual(DerivedSerials))
            return Fail<ValidatedInstallationStorage>("DerivedAuthorizationInvalid");
        var acquired = InstallerLabAcquisition.Correlate(authorization.AttemptId, hostObservationSha256, declared, reopened, fresh, guest);
        if (acquired.Availability != ObservationAvailability.Available)
            return Observations.Failure<ValidatedInstallationStorage>(acquired.Availability, acquired.Code!);
        var copies = VerifyIndependentCopies(predecessor, acquired.Value, derivation.Copies,
            authorization.TargetSha256, authorization.JournalSha256, false);
        if (copies is not null) return Fail<ValidatedInstallationStorage>(copies);
        var context = new ValidatedInstallationStorage(new("IsolatedFileBackedLab", 3, "CoreConfiguration", authorization.AttemptId,
            authorization.GenerationId, Digest(new { predecessor.Provenance, derivation, acquired.Value })), acquired.Value,
            predecessor.Receipts, predecessor.Preserved, predecessor.Closure,
            new(authorization.OperationId, authorization.ImportSha256, authorization.CheckpointSha256) { DerivationSha256 = Digest(derivation) });
        var valid = Revalidate(context, fresh, guest);
        return valid.Availability == ObservationAvailability.Available ? Observations.Available(context) :
            Observations.Failure<ValidatedInstallationStorage>(valid.Availability, valid.Code!);
    }

    // A successor keeps creation/format lineage. Only the nominated target backing may
    // continue; a copy needs a different, explicitly reviewed derivation contract.
    public static Observation<ValidatedInstallationStorage> ContinueLabSameTarget(ValidatedInstallationStorage predecessor,
        Guid runId, Guid operationId, string predecessorResultSha256, string checkpointSha256,
        string hostObservationSha256, ImmutableArray<InstallerLabBackingV1> declared,
        ImmutableArray<InstallerLabBackingV1> reopened, Observation<InstallerRuntimeInventoryV1> fresh,
        Observation<ImmutableArray<InstallerLabGuestDiskV1>> guest)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        if (predecessor.Provenance is not { Provider: "IsolatedFileBackedLab", Version: 2, Scope: "ConfiguredRootImport" } ||
            operationId == Guid.Empty || !Hash(predecessorResultSha256) || !Hash(checkpointSha256))
            return Fail<ValidatedInstallationStorage>("ConfigurationLineageInvalid");
        var next = InstallerLabAcquisition.Correlate(runId, hostObservationSha256, declared, reopened, fresh, guest);
        if (next.Availability != ObservationAvailability.Available)
            return Observations.Failure<ValidatedInstallationStorage>(next.Availability, next.Code!);
        var target = predecessor.Receipts[0].Creation.Parent;
        var before = predecessor.Acquisition.Disks.Single(d => d.Disk.GptDiskGuid == target.Disk.GptDiskGuid).Backing;
        var after = next.Value.Disks.SingleOrDefault(d => d.Disk.GptDiskGuid == target.Disk.GptDiskGuid)?.Backing;
        if (after is null || before.HostDevice != after.HostDevice || before.HostInode != after.HostInode || before.Length != after.Length)
            return Fail<ValidatedInstallationStorage>("ContinuationTargetBackingChanged");
        // The original journal backing carries the single-use successor reservation.
        // Moving that reservation to an empty clone is not a continuation authority.
        foreach (var inherited in predecessor.Acquisition.Disks.Where(d => d.Backing.Serial != "IGLOO-GPT-RUNTIME"))
        {
            var observed = next.Value.Disks.SingleOrDefault(d => d.Disk.GptDiskGuid == inherited.Disk.GptDiskGuid)?.Backing;
            if (observed is null || observed.HostDevice != inherited.Backing.HostDevice ||
                observed.HostInode != inherited.Backing.HostInode || observed.Length != inherited.Backing.Length)
                return Fail<ValidatedInstallationStorage>("ContinuationPersistentBackingChanged");
        }
        var context = new ValidatedInstallationStorage(new("IsolatedFileBackedLab", 3, "CoreConfiguration", runId,
            predecessor.Provenance.GenerationId, Digest(new { predecessor.Provenance, operationId, predecessorResultSha256,
                checkpointSha256, next.Value })), next.Value, predecessor.Receipts, predecessor.Preserved, predecessor.Closure,
            new(operationId, predecessorResultSha256, checkpointSha256));
        var valid = Revalidate(context, fresh, guest);
        return valid.Availability == ObservationAvailability.Available ? Observations.Available(context) :
            Observations.Failure<ValidatedInstallationStorage>(valid.Availability, valid.Code!);
    }

    private static bool Hash(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static InstallationPartitionV1 Project(PreparedGptPartitionV1 p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(new(p.Disk.GptDiskGuid, p.Disk.SizeBytes, p.Disk.LogicalSectorSize),
            p.PartitionGuid, p.PartitionType, p.OffsetBytes, p.SizeBytes);
    }

    public static InstallationPartitionV1 Project(InstallerRuntimePartitionV1 p, InstallerRuntimeInventoryV1 inventory)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(inventory);
        var disk = inventory.Disks.Single(d => d.DevicePath == p.DiskDevicePath);
        return new(new(disk.GptDiskGuid, disk.SizeBytes, disk.LogicalSectorSize),
            p.PartitionGuid, p.PartitionType, p.OffsetBytes, p.SizeBytes);
    }

    // Both producer paths use this exact whole-inventory and protected-disk closure check.
    public static Observation<bool> VerifyClosure(ImmutableArray<InstallationPartitionV1> expected,
        Observation<InstallerRuntimeInventoryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Availability != ObservationAvailability.Available)
            return Observations.Failure<bool>(inventory.Availability, "InstallationInventoryUnavailable");
        var valid = InstallerEspBinding.ValidateInventory(inventory.Value);
        if (valid.Availability != ObservationAvailability.Available) return valid;
        if (expected.IsDefaultOrEmpty || expected.Select(p => p.PartitionGuid).Distinct().Count() != expected.Length)
            return Fail<bool>("InstallationClosureInvalid");
        foreach (var identity in expected)
        {
            var match = inventory.Value.Partitions.SingleOrDefault(p => p.PartitionGuid == identity.PartitionGuid);
            if (match is null) return Observations.Failure<bool>(ObservationAvailability.Absent, "InstallationPartitionMissing");
            if (Project(match, inventory.Value) != identity) return Fail<bool>("InstallationPartitionChanged");
        }
        if (inventory.Value.Partitions.Any(p => expected.Any(e => e.Disk.GptDiskGuid ==
                inventory.Value.Disks.Single(d => d.DevicePath == p.DiskDevicePath).GptDiskGuid) &&
            !expected.Any(e => e.PartitionGuid == p.PartitionGuid))) return Fail<bool>("InstallationPartitionSetChanged");
        return Observations.Available(true);
    }

    public static Observation<ValidatedInstallationStorage> VerifyLab(InstallerLabStorageEvidenceV1 evidence,
        Observation<InstallerRuntimeInventoryV1> fresh, Observation<ImmutableArray<InstallerLabGuestDiskV1>> guest)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(guest);
        if (!((evidence.SchemaVersion == 1 && evidence.Scope == "StorageSmoke") ||
              (evidence.SchemaVersion == 2 && evidence.Scope == "ConfiguredRootImport")) || evidence.Provider != "IsolatedFileBackedLab" ||
            evidence.GenerationId == Guid.Empty || evidence.GuestDisks.IsDefault ||
            guest.Availability != ObservationAvailability.Available || !evidence.GuestDisks.SequenceEqual(guest.Value) || evidence.Transitions.IsDefault || evidence.Transitions.Length != 3 ||
            !evidence.Transitions.Select(t => t.Role).SequenceEqual(new[] { PreparationRole.LinuxEsp, PreparationRole.Payload, PreparationRole.LinuxRoot }))
            return Fail<ValidatedInstallationStorage>("LabStorageProviderOrScopeInvalid");
        var acquired = InstallerLabAcquisition.Correlate(evidence.RunId, evidence.HostObservationSha256,
            evidence.CreatedBackings, evidence.ReopenedBackings, fresh, guest);
        if (acquired.Availability != ObservationAvailability.Available)
            return Observations.Failure<ValidatedInstallationStorage>(acquired.Availability, acquired.Code!);
        var receipts = ImmutableArray.CreateBuilder<InstallerLabFormatReceiptV1>();
        InstallerRuntimeInventoryV1? previous = null;
        ImmutableArray<InstallerRuntimePartitionV1> preserved = [];
        foreach (var t in evidence.Transitions)
        {
            var creationIntent = ParseIntent<InstallerLabCreationIntentV1>(t.CreationIntent);
            var formatIntent = ParseIntent<InstallerLabFormatIntentV1>(t.FormatIntent);
            if (creationIntent != new InstallerLabCreationIntentV1(evidence.SchemaVersion, evidence.RunId, evidence.GenerationId, t.Role, t.Intended, TextDigest(t.Before)) { Scope = evidence.SchemaVersion == 2 ? evidence.Scope : null } ||
                formatIntent != new InstallerLabFormatIntentV1(evidence.SchemaVersion, evidence.RunId, evidence.GenerationId, t.Intended.PartitionGuid, t.FileSystem, TextDigest(t.Created)) { Scope = evidence.SchemaVersion == 2 ? evidence.Scope : null })
                return Fail<ValidatedInstallationStorage>("LabIntentBindingChanged");
            var before = LinuxInstallerInventoryProtocol.Parse(t.Before);
            var created = LinuxInstallerInventoryProtocol.Parse(t.Created);
            var formatted = LinuxInstallerInventoryProtocol.Parse(t.Formatted);
            if (before.Availability != ObservationAvailability.Available || formatted.Availability != ObservationAvailability.Available ||
                previous is not null && !Equal(previous, before.Value))
                return Fail<ValidatedInstallationStorage>("LabTransitionChainIncomplete");
            if (previous is null) preserved = before.Value.Partitions.Where(p => p.DiskDevicePath == t.Intended.DiskDevicePath).ToImmutableArray();
            if (created.Availability != ObservationAvailability.Available) return Fail<ValidatedInstallationStorage>("LabCreationUnavailable");
            var intended = created.Value.Partitions.SingleOrDefault(p => p.DevicePath == t.Intended.DevicePath);
            if (intended is null || new InstallerLabPartitionIntentV1(intended.DevicePath, intended.DiskDevicePath,
                intended.PartitionGuid, intended.PartitionType, intended.OffsetBytes, intended.SizeBytes) != t.Intended)
                return Fail<ValidatedInstallationStorage>("LabIntentChanged");
            var creation = InstallerLabAcquisition.VerifyCreation(acquired.Value, evidence.GenerationId, intended,
                before, created, TextDigest(t.Before), TextDigest(t.CreationIntent), TextDigest(t.Created));
            if (creation.Availability != ObservationAvailability.Available)
                return Fail<ValidatedInstallationStorage>(creation.Code!);
            var format = InstallerLabAcquisition.VerifyFormat(creation.Value, evidence.GenerationId, t.FileSystem,
                created, formatted, TextDigest(t.Created), TextDigest(t.FormatIntent), TextDigest(t.Formatted));
            if (format.Availability != ObservationAvailability.Available) return Fail<ValidatedInstallationStorage>(format.Code!);
            var expectedType = t.Role == PreparationRole.LinuxEsp ? PreparationSpacePlanning.EspType :
                t.Role == PreparationRole.Payload ? PreparationSpacePlanning.BasicDataType : PreparationSpacePlanning.LinuxDataType;
            if (t.Intended.PartitionType != expectedType || t.FileSystem.Type != (t.Role == PreparationRole.LinuxRoot ? "EXT4" : "FAT32") ||
                t.Intended.OffsetBytes % (1024 * 1024) != 0 || t.Intended.SizeBytes % (1024 * 1024) != 0 ||
                receipts.Any(r => r.Creation.Parent != creation.Value.Parent))
                return Fail<ValidatedInstallationStorage>("LabStorageRoleInvalid");
            receipts.Add(format.Value); previous = formatted.Value;
        }
        if (preserved.IsEmpty || !preserved.Any(p => p.PartitionType == PreparationSpacePlanning.EspType) || previous is null ||
            !Equal(previous, fresh.Value)) return Fail<ValidatedInstallationStorage>("LabFinalObservationChanged");
        var closure = previous.Partitions.Where(p => p.DiskDevicePath == receipts[0].Creation.Parent.Disk.DevicePath)
            .Select(p => Project(p, previous)).ToImmutableArray();
        var check = VerifyClosure(closure, fresh);
        if (check.Availability != ObservationAvailability.Available) return Fail<ValidatedInstallationStorage>(check.Code!);
        return Observations.Available(new ValidatedInstallationStorage(new("IsolatedFileBackedLab", evidence.SchemaVersion, evidence.Scope,
            evidence.RunId, evidence.GenerationId, Digest(evidence)), acquired.Value, receipts.ToImmutable(), preserved, closure));
    }

    public static Observation<bool> Revalidate(ValidatedInstallationStorage storage,
        Observation<InstallerRuntimeInventoryV1> inventory, Observation<ImmutableArray<InstallerLabGuestDiskV1>> guest)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var a = storage.Acquisition;
        var correlation = InstallerLabAcquisition.Correlate(a.LabRunId, a.HostObservationSha256,
            a.Disks.Select(d => d.Backing).ToImmutableArray(), a.Disks.Select(d => d.Backing).ToImmutableArray(), inventory, guest);
        if (correlation.Availability != ObservationAvailability.Available)
            return Observations.Failure<bool>(correlation.Availability, correlation.Code!);
        if (!correlation.Value.Disks.SequenceEqual(a.Disks)) return Fail<bool>("LabRuntimeDiskChanged");
        var closure = VerifyClosure(storage.Closure, inventory);
        if (closure.Availability != ObservationAvailability.Available) return closure;
        var expected = storage.Preserved.Concat(storage.Receipts.Select(r => r.Creation.Partition with { FileSystem = Observations.Available(r.FileSystem) }));
        if (expected.Any(p => !inventory.Value.Partitions.Contains(p))) return Fail<bool>("LabFilesystemOrLocatorChanged");
        return Observations.Available(true);
    }

    private static T? ParseIntent<T>(string raw) where T : class
    {
        if (raw is null || raw.Length > 16384) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (Duplicate(document.RootElement)) return null;
            return JsonSerializer.Deserialize<T>(raw, StrictJson);
        }
        catch (JsonException) { return null; }
    }

    private static bool Duplicate(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count() ||
            value.EnumerateObject().Any(p => Duplicate(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(Duplicate),
        _ => false,
    };

    private static bool Equal(InstallerRuntimeInventoryV1 a, InstallerRuntimeInventoryV1 b) =>
        a.Disks.SequenceEqual(b.Disks) && a.Partitions.SequenceEqual(b.Partitions) && a.ExternalFileSystems.SequenceEqual(b.ExternalFileSystems);

    private static string TextDigest(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static Observation<T> Fail<T>(string code) => Observations.Failure<T>(ObservationAvailability.Ambiguous, code);
}
