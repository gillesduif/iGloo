using System.Text.Json;
using System.Security.Cryptography;
using Igloo.Core.Preparation;
using Igloo.Core.Abstractions;

namespace Igloo.Distro.Debian.Deployment;

// Closed lab successor declaration. It is not a product agent installation or
// a standalone capability: the verified UserData/Close, independent copies,
// current leases, authenticated runtime and persistent journals remain required.
public sealed record DebianLabAgentStagePlanV1(int SchemaVersion, Guid OperationId,
    InstallationStorageProvenanceV1 Provenance, string UserDataSha256, string UserDataCloseSha256,
    string RetentionSha256, string DerivationSha256, InstallerLabAgentStageDerivationV1 Derivation,
    DebianAgentOfflineStageV1 Stage, string ExecutionSha256)
{
    public string Fingerprint(ValidatedInstallationStorage storage, DebianVerifiedUserData predecessor)
    {
        ArgumentNullException.ThrowIfNull(storage); ArgumentNullException.ThrowIfNull(predecessor);
        var root = storage.Receipts.Single(r => r.FileSystem.Type == "EXT4").Creation.Partition.PartitionGuid;
        var expected = DebianAgentProfiles.DeclareLabOfflineStage(Provenance.GenerationId, OperationId, root);
        if (SchemaVersion != 1 || OperationId == Guid.Empty || Provenance != storage.Provenance ||
            Provenance is not { Provider: "IsolatedFileBackedLab", Version: 6, Scope: "OfflineAgentStage" } ||
            Provenance.GenerationId != predecessor.Plan.Provenance.GenerationId ||
            UserDataSha256 != predecessor.ResultSha256 || UserDataCloseSha256 != predecessor.CloseSha256 ||
            Derivation is null || DerivationSha256 != DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(Derivation)) ||
            Derivation.Authorization.OperationId != OperationId || Derivation.Authorization.AttemptId != Provenance.RunId ||
            Derivation.Authorization.GenerationId != Provenance.GenerationId ||
            Derivation.Authorization.UserDataSha256 != UserDataSha256 ||
            Derivation.Authorization.CloseSha256 != UserDataCloseSha256 ||
            Derivation.Authorization.RetentionSha256 != RetentionSha256 ||
            storage.Continuation != new InstallationContinuationV1(OperationId, UserDataSha256, RetentionSha256)
                { DerivationSha256 = DerivationSha256 } ||
            Stage is null || Stage.Files.IsDefault || !Stage.Files.SequenceEqual(expected.Files) ||
            Stage with { Files = expected.Files } != expected ||
            !DebianDeploymentPlanning.Hash(ExecutionSha256))
            throw new InvalidDataException("Lab offline-agent successor rejected.");
        return DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new {
            Plan = this, predecessor.ResultSha256, predecessor.CloseSha256, predecessor.PlanSha256 }));
    }
}

public sealed partial class DebianMountSessionAuthority
{
    public DebianLabAgentStagePlanV1? AgentStagePlan { get; }
    public DebianVerifiedUserData? UserDataPredecessor { get; }
    private static readonly string[] AgentStageSteps = ["Baseline", "Publication", "Verify"];
    private int _agentStageStep;
    private bool _agentStageIntent;
    private bool _agentStageVerified;
    private string? _agentStageReference;
    private string? _agentStageFilesystemHash;

    private DebianMountSessionAuthority(ValidatedInstallationStorage storage, DebianVerifiedUserData predecessor,
        DebianLabAgentStagePlanV1 plan, IDebianDeploymentJournal session, IDebianDeploymentJournal effects,
        DebianRootDevelopmentPinV1 pin)
    {
        ArgumentNullException.ThrowIfNull(storage); ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(pin);
        _planHash = plan.Fingerprint(storage, predecessor);
        var imported = predecessor.Initramfs.Configured.Imported;
        if (pin.BuildId != imported.BuildId || pin.DescriptorSha256 != imported.DescriptorSha256 ||
            !DebianDeploymentPlanning.Hash(pin.PolicySha256) || pin.NotBeforeUtc > DateTimeOffset.UtcNow ||
            pin.NotAfterUtc <= DateTimeOffset.UtcNow || ReferenceEquals(session, effects) ||
            session is not DebianLinuxDeploymentJournal nativeSession ||
            effects is not DebianLinuxDeploymentJournal nativeEffects ||
            nativeEffects.PersistentWitness?.Path != "/lab-journal/agent-stage/" + plan.Provenance.RunId.ToString("D") + "/effects" ||
            nativeSession.PersistentWitness?.Path != "/lab-journal/agent-stage/" + plan.Provenance.RunId.ToString("D") + "/session")
            throw new InvalidDataException("Agent stage requires distinct protected persistent journals.");
        nativeSession.RequirePersistentStoreWitness(); nativeEffects.RequirePersistentStoreWitness();
        LabStorage = storage; UserDataPredecessor = predecessor; Predecessor = imported; AgentStagePlan = plan;
        _journal = session; _importJournal = effects; _authenticator = new DebianDevelopmentRootAuthenticator(pin);
    }

    public static DebianMountSessionAuthority ForLabOfflineAgentStage(ValidatedInstallationStorage storage,
        DebianVerifiedUserData predecessor, DebianLabAgentStagePlanV1 plan, IDebianDeploymentJournal session,
        IDebianDeploymentJournal effects, DebianRootDevelopmentPinV1 pin) =>
        new(storage, predecessor, plan, session, effects, pin);

    internal static object AgentStagePacket(DebianLabAgentStagePlanV1 plan) => new {
        Declaration = JsonSerializer.SerializeToUtf8Bytes(plan.Stage),
        Worker = DebianFirstBootEvidence.WorkerBytes(),
        Unit = System.Text.Encoding.UTF8.GetBytes(DebianAgentProfiles.Unit)
    };

    internal static string AgentStageSourceSha256()
    {
        var worker = DebianFirstBootEvidence.WorkerBytes();
        var unit = System.Text.Encoding.UTF8.GetBytes(DebianAgentProfiles.Unit);
        return Convert.ToHexString(SHA256.HashData([.. worker, .. unit]));
    }

    private async Task<object> AgentStageCheckpointAsync(JsonElement message, Guid challenge, CancellationToken ct)
    {
        if (AgentStagePlan is null || UserDataPredecessor is null || _action != DebianMountSessionAction.StageOfflineAgent ||
            !_sourceAuthenticated || DateTimeOffset.UtcNow >= _sourceExpires || _importJournal is null || Leases is null ||
            _agentStageVerified || _agentStageStep > 0 && !_intent)
            throw new InvalidDataException("Agent stage checkpoint outside active authority.");
        var record = message.GetProperty("Record");
        var step = record.GetProperty("Step").GetString(); var outcome = record.GetProperty("Outcome").GetString();
        if (_agentStageStep >= AgentStageSteps.Length || step != AgentStageSteps[_agentStageStep] ||
            outcome is not ("IntentDurable" or "AppliedAndVerified" or "OutcomeUnknown") ||
            outcome == "IntentDurable" && _agentStageIntent || outcome != "IntentDurable" && !_agentStageIntent)
            throw new InvalidDataException("Agent stage effect order rejected.");
        if (!_importReserved) { _importReserved = true; await _importJournal.BeginNewAsync(GenerationId, _planHash, ct).ConfigureAwait(false); }
        if (step == "Publication" && outcome == "IntentDurable")
        {
            var intent = record.GetProperty("PublicationIntent");
            var files = intent.GetProperty("Files").Deserialize<DebianAgentFileV1[]>();
            if (intent.GetProperty("Policy").GetString() != "CreateNewOfflineAgentStageV1" ||
                intent.GetProperty(nameof(PlanSha256)).GetString() != DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(AgentStagePlan.Stage)) ||
                intent.GetProperty("SourceSha256").GetString() != AgentStageSourceSha256() ||
                files is null || !files.SequenceEqual(AgentStagePlan.Stage.Files) ||
                !intent.GetProperty("Before").GetProperty("WorkerAbsent").GetBoolean() ||
                !intent.GetProperty("Before").GetProperty("UnitAbsent").GetBoolean() ||
                !intent.GetProperty("Before").GetProperty("ConfigurationAbsent").GetBoolean() ||
                !intent.GetProperty("Before").GetProperty("EnableLinkAbsent").GetBoolean())
                throw new InvalidDataException("Agent stage publication intent changed.");
        }
        if (outcome == "AppliedAndVerified")
        {
            var observed = record.GetProperty("ObserverEvidence");
            var hash = observed.GetProperty("FilesystemSha256").GetString();
            if (DebianDeploymentPlanning.TextHash(observed.GetRawText()) != record.GetProperty("ObserverEvidenceSha256").GetString() ||
                observed.GetProperty("OperationId").GetGuid() != AgentStagePlan.OperationId ||
                observed.GetProperty(nameof(SessionId)).GetGuid() != SessionId ||
                observed.GetProperty(nameof(PlanSha256)).GetString() != _planHash ||
                observed.GetProperty("Step").GetString() != step ||
                observed.GetProperty("PackageStateSha256").GetString() != UserDataPredecessor.Observation.GetProperty("PackageStateSha256").GetString() ||
                !DebianDeploymentPlanning.Hash(hash))
                throw new InvalidDataException("Agent stage independent observation changed.");
            if (step == "Baseline")
            {
                if (hash != UserDataPredecessor.Observation.GetProperty("FilesystemSha256").GetString())
                    throw new InvalidDataException("Agent stage baseline differs from UserData.");
            }
            else
            {
                if (hash == UserDataPredecessor.Observation.GetProperty("FilesystemSha256").GetString() ||
                    _agentStageFilesystemHash is not null && hash != _agentStageFilesystemHash ||
                    !observed.GetProperty("ConfigurationAbsent").GetBoolean() ||
                    !observed.GetProperty("EnableLinkAbsent").GetBoolean() ||
                    !observed.GetProperty("UserDataAndConfiguredInitramfsPreserved").GetBoolean() ||
                    DebianAgentProfiles.VerifyLabOfflineStage(AgentStagePlan.Stage,
                        Observations.Available(observed.GetProperty("StageReadback").Deserialize<DebianAgentOfflineReadbackV1>()!))
                        .Availability != ObservationAvailability.Available)
                    throw new InvalidDataException("Agent stage installed-state observation changed.");
                _agentStageFilesystemHash = hash;
            }
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 6, GenerationId, SessionId,
            PlanSha256 = _planHash, AgentStagePlan.OperationId, LabStorage!.Provenance,
            AgentStagePlan.UserDataSha256, AgentStagePlan.UserDataCloseSha256,
            Sequence = _importSequence, PreviousSha256 = _importPreviousHash, Leases.Bindings,
            Leases.ProtectedStateSha256, Step = step, Outcome = outcome, Evidence = record });
        var reference = await _importJournal.AppendDurablyAsync(GenerationId, bytes, ct).ConfigureAwait(false);
        var reopened = await _importJournal.ReopenAsync(reference, ct).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(reopened)) throw new IOException("Agent stage result reopen mismatch.");
        _importPreviousHash = DebianConfiguredRootArtifacts.Digest(bytes); _importSequence++;
        _agentStageIntent = outcome == "IntentDurable";
        if (outcome == "AppliedAndVerified") _agentStageStep++;
        if (_agentStageStep == AgentStageSteps.Length) { _agentStageVerified = true; _agentStageReference = reference; }
        if (outcome == "OutcomeUnknown") State = DebianMountSessionState.OutcomeUnknown;
        return new { Challenge = challenge, Accepted = true, Reference = reference, Sha256 = _importPreviousHash };
    }
}
