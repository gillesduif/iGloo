using System.Text.Json;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianLabUserDataPlanV1(int SchemaVersion, Guid OperationId,
    InstallationStorageProvenanceV1 Provenance, string InitramfsSha256, string InitramfsCloseSha256,
    string RetentionSha256, string DerivationSha256, InstallerLabUserDataDerivationV1 Derivation,
    ReadOnlyMemory<byte> Transfer, ReadOnlyMemory<byte> Delivery, string ExecutionSha256)
{
    public string Fingerprint(ValidatedInstallationStorage storage, DebianVerifiedInitramfs predecessor)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(predecessor);
        var transfer = JsonSerializer.Deserialize<DebianUserDataPlanV1>(Transfer.Span) ?? throw new InvalidDataException("UserData transfer missing.");
        if (SchemaVersion != 1 || OperationId == Guid.Empty || Provenance != storage.Provenance ||
            Provenance is not { Provider: "IsolatedFileBackedLab", Version: 5, Scope: "SelectedDocumentTrees" } ||
            Provenance.GenerationId != predecessor.Plan.Provenance.GenerationId || transfer.GenerationId != Provenance.GenerationId ||
            transfer.OperationId != OperationId || transfer.Scope != DebianUserData.Scope ||
            transfer.Username != predecessor.Configured.Plan.Username || transfer.Uid != predecessor.Configured.Plan.UserId ||
            transfer.Gid != transfer.Uid || transfer.Home != "/home/" + transfer.Username ||
            InitramfsSha256 != predecessor.ResultSha256 || InitramfsCloseSha256 != predecessor.CloseSha256 ||
            Derivation is null || DerivationSha256 != DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(Derivation)) ||
            Derivation.DeliverySha256 != DebianConfiguredRootArtifacts.Digest(Delivery.Span) ||
            Derivation.Authorization.TransferSha256 != DebianConfiguredRootArtifacts.Digest(Transfer.Span) ||
            Derivation.Authorization.InitramfsSha256 != InitramfsSha256 || Derivation.Authorization.CloseSha256 != InitramfsCloseSha256 ||
            Derivation.Authorization.RetentionSha256 != RetentionSha256 || Derivation.Authorization.OperationId != OperationId ||
            Derivation.Authorization.AttemptId != Provenance.RunId || Derivation.Authorization.GenerationId != Provenance.GenerationId ||
            storage.Continuation != new InstallationContinuationV1(OperationId, InitramfsSha256, RetentionSha256) { DerivationSha256 = DerivationSha256 } ||
            !DebianDeploymentPlanning.Hash(RetentionSha256) || !DebianDeploymentPlanning.Hash(DerivationSha256) ||
            !DebianDeploymentPlanning.Hash(ExecutionSha256) || Transfer.Length is <= 0 or > 1048576 || Delivery.Length is <= 0 or > 1048576)
            throw new InvalidDataException("UserData successor rejected.");
        return DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new { Plan = this,
            predecessor.ResultSha256, predecessor.CloseSha256, predecessor.PlanSha256 }));
    }
}

public sealed partial class DebianMountSessionAuthority
{
    public DebianLabUserDataPlanV1? UserDataPlan { get; }
    public DebianVerifiedInitramfs? InitramfsPredecessor { get; }
    public DebianJournalStoreWitnessV1? UserDataStore { get; }
    private byte[]? _userDataDeclaration;
    private string? _userDataResultReference;
    private bool _userDataVerified;
    private bool _userDataIntent;
    private int _userDataStep;
    private string? _userDataProducerSha256;
    private string? _userDataReceiptSha256;
    private string? _userDataBundleSha256;
    private static readonly string[] UserDataSteps = ["Baseline", "Transfer", "Admission", "Verify"];

    private DebianMountSessionAuthority(ValidatedInstallationStorage storage, DebianVerifiedInitramfs predecessor,
        DebianLabUserDataPlanV1 plan, IDebianDeploymentJournal session, IDebianDeploymentJournal effects,
        DebianJournalStoreWitnessV1 producerStore, DebianRootDevelopmentPinV1 pin)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(pin); ArgumentNullException.ThrowIfNull(producerStore);
        plan = plan with { Transfer = plan.Transfer.ToArray(), Delivery = plan.Delivery.ToArray() };
        _planHash = plan.Fingerprint(storage, predecessor);
        var imported = predecessor.Configured.Imported;
        if (pin.BuildId != imported.BuildId || pin.DescriptorSha256 != imported.DescriptorSha256 ||
            !DebianDeploymentPlanning.Hash(pin.PolicySha256) || pin.NotBeforeUtc > DateTimeOffset.UtcNow ||
            pin.NotAfterUtc <= DateTimeOffset.UtcNow || ReferenceEquals(session, effects) ||
            effects is not DebianLinuxDeploymentJournal nativeEffects || nativeEffects.PersistentWitness != producerStore ||
            producerStore.Path != "/lab-journal/userdata/" + plan.Provenance.RunId.ToString("D") + "/effects")
            throw new InvalidDataException("UserData lab composition requires protected persistent journals.");
        nativeEffects.RequirePersistentStoreWitness();
        if (session is not DebianLinuxDeploymentJournal nativeSession) throw new InvalidDataException("Native UserData journal required.");
        nativeSession.RequirePersistentStoreWitness();
        LabStorage = storage; InitramfsPredecessor = predecessor; Predecessor = imported;
        UserDataPlan = plan; UserDataStore = producerStore; _journal = session; _importJournal = effects;
        _authenticator = new DebianDevelopmentRootAuthenticator(pin);
    }

    public static DebianMountSessionAuthority ForLabUserData(ValidatedInstallationStorage storage, DebianVerifiedInitramfs predecessor,
        DebianLabUserDataPlanV1 plan, IDebianDeploymentJournal session, IDebianDeploymentJournal effects,
        DebianJournalStoreWitnessV1 producerStore, DebianRootDevelopmentPinV1 pin) => new(storage, predecessor, plan, session, effects, producerStore, pin);

    private object UserDataContext(Guid challenge)
    {
        if (UserDataPlan is null || _action != DebianMountSessionAction.TransferUserData || !_sourceAuthenticated ||
            DateTimeOffset.UtcNow >= _sourceExpires || Leases is null || _userDataDeclaration is not null || _importReserved)
            throw new InvalidDataException("UserData context outside active authority.");
        _userDataDeclaration = SerializeUserDataContext(UserDataPlan, Leases, _planHash);
        return new { Challenge = challenge, Accepted = true, Declaration = _userDataDeclaration };
    }

    internal static byte[] SerializeUserDataContext(DebianLabUserDataPlanV1 plan, InstallerBlockLeaseSet leases, string planHash)
    {
        var p = JsonSerializer.Deserialize<DebianUserDataPlanV1>(plan.Transfer.Span)!;
        if (leases.Provenance != plan.Provenance || leases.GenerationId != p.GenerationId)
            throw new InvalidDataException("UserData lease provenance differs.");
        var context = new DebianUserDataCanonicalDeclarationV1(1, DebianUserDataAdmission.Scope, plan.Provenance,
            leases.SessionId, planHash, DebianConfiguredRootArtifacts.Digest(plan.Transfer.Span), plan.InitramfsSha256,
            plan.InitramfsCloseSha256, DebianConfiguredRootArtifacts.Digest(plan.Delivery.Span),
            $"userdata/{p.OperationId:D}/source", new(p.Username, p.Uid, p.Gid, p.Home), leases.Bindings);
        if (!DebianUserDataAdmission.ValidDeclaration(context, p)) throw new InvalidDataException("UserData lease/account declaration rejected.");
        return DebianUserDataAdmission.Serialize(context);
    }

    private async Task<object> UserDataCheckpointAsync(JsonElement message, Guid challenge, CancellationToken ct)
    {
        if (UserDataPlan is null || _action != DebianMountSessionAction.TransferUserData || !_sourceAuthenticated ||
            DateTimeOffset.UtcNow >= _sourceExpires || _importJournal is null || Leases is null || _userDataDeclaration is null ||
            _userDataVerified || _userDataStep > 0 && !_intent)
            throw new InvalidDataException("UserData checkpoint outside active authority.");
        var r = message.GetProperty("Record"); var outcome = r.GetProperty("Outcome").GetString(); var step = r.GetProperty("Step").GetString();
        if (_userDataStep >= UserDataSteps.Length || step != UserDataSteps[_userDataStep] ||
            outcome is not ("IntentDurable" or "AppliedAndVerified" or "OutcomeUnknown") ||
            outcome == "IntentDurable" && _userDataIntent || outcome != "IntentDurable" && !_userDataIntent)
            throw new InvalidDataException("UserData effect order rejected.");
        if (!_importReserved) { _importReserved = true; await _importJournal.BeginNewAsync(GenerationId, _planHash, ct).ConfigureAwait(false); }
        if (outcome == "AppliedAndVerified")
        {
            var o = r.GetProperty("ObserverEvidence");
            if (DebianDeploymentPlanning.TextHash(o.GetRawText()) != r.GetProperty("ObserverEvidenceSha256").GetString() ||
                o.GetProperty("OperationId").GetGuid() != UserDataPlan.OperationId || o.GetProperty(nameof(SessionId)).GetGuid() != SessionId ||
                o.GetProperty(nameof(PlanSha256)).GetString() != _planHash || o.GetProperty("Step").GetString() != step ||
                o.GetProperty("AuthoritySha256").GetString() != DebianConfiguredRootArtifacts.Digest(_userDataDeclaration) ||
                o.GetProperty("PackageStateSha256").GetString() != InitramfsPredecessor!.Observation.GetProperty("PackageStateSha256").GetString() ||
                !DebianDeploymentPlanning.Hash(o.GetProperty("FilesystemSha256").GetString()) || step != "Baseline" &&
                (!o.GetProperty("ConfiguredAndInitramfsPreserved").GetBoolean() || o.GetProperty("MachineIdentity").GetString() != "FirstBootPending"))
                throw new InvalidDataException("UserData independent observation changed.");
            if (step == "Transfer")
            {
                var receipt = r.GetProperty("Receipt").GetBytesFromBase64(); var bundle = r.GetProperty("Bundle").GetBytesFromBase64();
                if (!DebianUserDataAdmission.ValidateLabAdmissionStructure(JsonSerializer.Deserialize<DebianUserDataPlanV1>(UserDataPlan.Transfer.Span)!,
                    JsonSerializer.Deserialize<DebianUserDataCanonicalDeclarationV1>(_userDataDeclaration)!, receipt, bundle))
                    throw new InvalidDataException("UserData terminal chain rejected.");
                _userDataReceiptSha256 = DebianConfiguredRootArtifacts.Digest(receipt); _userDataBundleSha256 = DebianConfiguredRootArtifacts.Digest(bundle);
                _userDataProducerSha256 = JsonSerializer.Deserialize<JsonElement>(receipt).GetProperty("EvidenceSha256").GetString();
            }
            if (step is "Admission" or "Verify")
                if (o.GetProperty("Admission").GetProperty("ResultSha256").GetString() != _userDataProducerSha256 ||
                    o.GetProperty("Admission").GetProperty("ReceiptSha256").GetString() != _userDataReceiptSha256 ||
                    o.GetProperty("Admission").GetProperty("EvidenceSha256").GetString() != _userDataBundleSha256)
                    throw new InvalidDataException("UserData admission differs from completed producer.");
        }
        if (step == "Admission" && outcome == "IntentDurable")
        {
            var admission = r.GetProperty("Admission");
            var files = admission.GetProperty("Files").EnumerateArray().ToArray();
            if (admission.GetProperty("Policy").GetString() != "CreateNewLabUserDataAdmissionV1" ||
                admission.GetProperty("Destination").GetString() != DebianUserDataAdmission.InputDirectory ||
                admission.GetProperty("AuthoritySha256").GetString() != DebianConfiguredRootArtifacts.Digest(_userDataDeclaration) ||
                admission.GetProperty("ResultSha256").GetString() != _userDataProducerSha256 || files.Length != 2 ||
                files[0].GetProperty("Name").GetString() != DebianUserDataAdmission.EvidenceName ||
                files[1].GetProperty("Name").GetString() != DebianUserDataAdmission.ReceiptName ||
                files[0].GetProperty("Sha256").GetString() != _userDataBundleSha256 ||
                files[1].GetProperty("Sha256").GetString() != _userDataReceiptSha256 ||
                files.Any(f => f.GetProperty("Length").GetInt64() is <= 0 or > 1048576 ||
                    f.GetProperty("Uid").GetInt32() != 0 || f.GetProperty("Gid").GetInt32() != 0 || f.GetProperty("Mode").GetInt32() != 420))
                throw new InvalidDataException("UserData admission intent changed.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 5, GenerationId, SessionId, PlanSha256 = _planHash,
            UserDataPlan.OperationId, LabStorage!.Provenance, UserDataPlan.InitramfsSha256, UserDataPlan.InitramfsCloseSha256,
            AuthoritySha256 = DebianConfiguredRootArtifacts.Digest(_userDataDeclaration), Sequence = _importSequence,
            PreviousSha256 = _importPreviousHash, Leases.Bindings, Leases.ProtectedStateSha256, Step = step, Outcome = outcome, Evidence = r });
        var reference = await _importJournal.AppendDurablyAsync(GenerationId, bytes, ct).ConfigureAwait(false);
        var reopened = await _importJournal.ReopenAsync(reference, ct).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(reopened)) throw new IOException("UserData result reopen mismatch.");
        _importPreviousHash = DebianConfiguredRootArtifacts.Digest(bytes); _importSequence++;
        _userDataIntent = outcome == "IntentDurable";
        if (outcome == "AppliedAndVerified") _userDataStep++;
        if (_userDataStep == UserDataSteps.Length) { _userDataVerified = true; _userDataResultReference = reference; }
        if (outcome == "OutcomeUnknown") State = DebianMountSessionState.OutcomeUnknown;
        return new { Challenge = challenge, Accepted = true, Reference = reference, Sha256 = _importPreviousHash };
    }
}
