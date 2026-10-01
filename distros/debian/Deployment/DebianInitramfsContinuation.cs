using System.Text.Json;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// One image addition to a verified configured root. No managed update-initramfs,
// bootloader, configuration, package installation or production authority.
public sealed record DebianLabInitramfsPlanV1(int SchemaVersion, Guid OperationId,
    InstallationStorageProvenanceV1 Provenance, string ConfigurationSha256, string ConfigurationCloseSha256,
    string RetentionSha256, string DerivationSha256, string ConfiguredEntriesSha256, JsonElement Candidate,
    string ExecutionSha256)
{
    public const string Policy = "debian-trixie-lab-initramfs-candidate-v1";
    public const string KernelRelease = "6.12.107+deb13-amd64";
    public const string Destination = "/boot/initrd.img-" + KernelRelease;
    public string Workspace => "/var/lib/igloo/initramfs-workspaces/" + Provenance.RunId.ToString("D");

    public string Fingerprint(ValidatedInstallationStorage storage, DebianVerifiedConfiguration predecessor)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(predecessor);
        if (SchemaVersion != 1 || OperationId == Guid.Empty || Provenance != storage.Provenance ||
            Provenance is not { Provider: "IsolatedFileBackedLab", Version: 4, Scope: "InitramfsImage" } ||
            Provenance.GenerationId != predecessor.Plan.Provenance.GenerationId ||
            ConfigurationSha256 != predecessor.ResultSha256 || ConfigurationCloseSha256 != predecessor.CloseSha256 ||
            storage.Continuation != new InstallationContinuationV1(OperationId, ConfigurationSha256, RetentionSha256)
                { DerivationSha256 = DerivationSha256 } ||
            !DebianDeploymentPlanning.Hash(RetentionSha256) || !DebianDeploymentPlanning.Hash(DerivationSha256) ||
            !DebianDeploymentPlanning.Hash(ExecutionSha256) ||
            ConfiguredEntriesSha256 != predecessor.Observation.GetProperty("FilesystemDeltaSha256").GetString() ||
            Candidate.ValueKind != JsonValueKind.Object || Candidate.GetProperty("Policy").GetString() != Policy ||
            Candidate.GetProperty("KernelRelease").GetString() != KernelRelease ||
            Candidate.GetProperty("ConfigurationResultSha256").GetString() != ConfigurationSha256 ||
            Candidate.GetProperty("RootUuid").GetString() != storage.Receipts[2].FileSystem.Uuid ||
            Candidate.GetProperty("Output").GetString() != Destination ||
            Candidate.GetProperty("Modules").GetString() != "most" || Candidate.GetProperty("Resume").GetString() != "none" ||
            Candidate.GetProperty("Compression").GetString() != "gzip" ||
            Candidate.GetProperty("CpuVendor").GetString() != "AuthenticAMD" ||
            Candidate.GetProperty("StorageTopology").GetString() != "VirtioBlockExt4" ||
            !DebianDeploymentPlanning.Hash(Candidate.GetProperty("InputClosureSha256").GetString()) ||
            !DebianDeploymentPlanning.Hash(Candidate.GetProperty("HookSetSha256").GetString()))
            throw new InvalidDataException("Initramfs successor binding rejected.");
        return DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new { Plan = this,
            predecessor.ResultSha256, predecessor.CloseSha256, predecessor.PlanSha256 }));
    }
}

public sealed partial class DebianMountSessionAuthority
{
    public DebianLabInitramfsPlanV1? InitramfsPlan { get; }
    public DebianVerifiedConfiguration? ConfiguredPredecessor { get; }
    private int _initramfsStep;
    private bool _initramfsIntent;
    private bool _initramfsVerified;
    private string? _initramfsReference;
    private string? _initramfsImageSha256;
    private long _initramfsImageLength;
    private static readonly string[] InitramfsSteps = ["Baseline", "Generation", "Candidate", "Publication", "Verify"];

    private DebianMountSessionAuthority(ValidatedInstallationStorage storage, DebianVerifiedConfiguration predecessor,
        DebianLabInitramfsPlanV1 plan, IDebianDeploymentJournal session, IDebianDeploymentJournal effects,
        DebianRootDevelopmentPinV1 pin)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(pin);
        _planHash = plan.Fingerprint(storage, predecessor);
        if (pin.BuildId != predecessor.Imported.BuildId || pin.DescriptorSha256 != predecessor.Imported.DescriptorSha256 ||
            !DebianDeploymentPlanning.Hash(pin.PolicySha256) || pin.NotBeforeUtc > DateTimeOffset.UtcNow ||
            pin.NotAfterUtc <= DateTimeOffset.UtcNow || ReferenceEquals(session, effects))
            throw new InvalidDataException("Initramfs development composition rejected.");
        LabStorage = storage; ConfiguredPredecessor = predecessor; Predecessor = predecessor.Imported; InitramfsPlan = plan;
        _journal = session ?? throw new ArgumentNullException(nameof(session));
        _importJournal = effects ?? throw new ArgumentNullException(nameof(effects));
        _authenticator = new DebianDevelopmentRootAuthenticator(pin);
        if (session is DebianLinuxDeploymentJournal nativeSession) nativeSession.RequirePersistentStoreWitness();
        if (effects is DebianLinuxDeploymentJournal nativeEffects) nativeEffects.RequirePersistentStoreWitness();
    }

    public static DebianMountSessionAuthority ForLabInitramfs(ValidatedInstallationStorage storage,
        DebianVerifiedConfiguration predecessor, DebianLabInitramfsPlanV1 plan, IDebianDeploymentJournal session,
        IDebianDeploymentJournal effects, DebianRootDevelopmentPinV1 pin) => new(storage, predecessor, plan, session, effects, pin);

    private async Task<object> InitramfsCheckpointAsync(JsonElement message, Guid challenge, CancellationToken ct)
    {
        if (InitramfsPlan is null || _action != DebianMountSessionAction.GenerateInitramfs || !_sourceAuthenticated ||
            DateTimeOffset.UtcNow >= _sourceExpires || _importJournal is null || Leases is null || _initramfsVerified ||
            _initramfsStep > 0 && !_intent)
            throw new InvalidDataException("Initramfs checkpoint outside active authority.");
        var record = message.GetProperty("Record");
        var outcome = record.GetProperty("Outcome").GetString();
        var step = record.GetProperty("Step").GetString();
        if (_initramfsStep >= InitramfsSteps.Length || step != InitramfsSteps[_initramfsStep] ||
            outcome is not ("IntentDurable" or "AppliedAndVerified" or "OutcomeUnknown") ||
            outcome == "IntentDurable" && _initramfsIntent || outcome != "IntentDurable" && !_initramfsIntent)
            throw new InvalidDataException("Initramfs effect order rejected.");
        if (!_importReserved)
        {
            _importReserved = true;
            await _importJournal.BeginNewAsync(GenerationId, _planHash, ct).ConfigureAwait(false);
        }
        if (outcome == "AppliedAndVerified")
        {
            var observation = record.GetProperty("ObserverEvidence");
            if (DebianDeploymentPlanning.TextHash(observation.GetRawText()) != record.GetProperty("ObserverEvidenceSha256").GetString() ||
                observation.GetProperty("OperationId").GetGuid() != InitramfsPlan.OperationId ||
                observation.GetProperty(nameof(PlanSha256)).GetString() != _planHash ||
                observation.GetProperty("Step").GetString() != step)
                throw new InvalidDataException("Initramfs independent observation binding rejected.");
            if (observation.GetProperty("ConfiguredEntriesSha256").GetString() != InitramfsPlan.ConfiguredEntriesSha256 ||
                observation.GetProperty("PackageStateSha256").GetString() != ConfiguredPredecessor!.Observation.GetProperty("PackageStateSha256").GetString())
                throw new InvalidDataException("Initramfs configured baseline observation changed.");
            if (step is "Candidate" or "Verify")
            {
                var image = observation.GetProperty("Image"); var semantics = observation.GetProperty("ImageSemantics");
                if (!observation.GetProperty("CompleteImageQualification").GetBoolean() ||
                    image.GetProperty("Length").GetInt64() is <= 0 or > 536870912 ||
                    image.GetProperty("Sha256").GetString() != semantics.GetProperty("ImageSha256").GetString() ||
                    image.GetProperty("Length").GetInt64() != semantics.GetProperty("ImageLength").GetInt64())
                    throw new InvalidDataException("Complete independent image qualification required.");
                if (step == "Candidate")
                {
                    _initramfsImageSha256 = image.GetProperty("Sha256").GetString();
                    _initramfsImageLength = image.GetProperty("Length").GetInt64();
                }
            }
            if (step is "Publication" or "Verify") RequireAcceptedImage(observation.GetProperty("Image"));
        }
        if (step == "Publication" && outcome == "IntentDurable")
        {
            var publication = record.GetProperty("Publication");
            if (publication.GetProperty("Destination").GetString() != DebianLabInitramfsPlanV1.Destination ||
                publication.GetProperty("Policy").GetString() != "CreateNewInitramfsImageV1" ||
                publication.GetProperty("Uid").GetInt32() != 0 || publication.GetProperty("Gid").GetInt32() != 0 ||
                publication.GetProperty("Mode").GetInt32() != 384)
                throw new InvalidDataException("Initramfs publication intent changed.");
            RequireAcceptedImage(publication.GetProperty("Candidate"));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 4, GenerationId, SessionId, PlanSha256 = _planHash,
            InitramfsPlan.OperationId, LabStorage!.Provenance, InitramfsPlan.ConfigurationSha256, InitramfsPlan.ConfigurationCloseSha256,
            Sequence = _importSequence, PreviousSha256 = _importPreviousHash, Leases.Bindings, Leases.ProtectedStateSha256,
            Step = step, Outcome = outcome, Evidence = record });
        var reference = await _importJournal.AppendDurablyAsync(GenerationId, bytes, ct).ConfigureAwait(false);
        var reopened = await _importJournal.ReopenAsync(reference, ct).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(reopened)) throw new IOException("Initramfs result reopen mismatch.");
        _importPreviousHash = DebianConfiguredRootArtifacts.Digest(bytes); _importSequence++;
        _initramfsIntent = outcome == "IntentDurable";
        if (outcome == "AppliedAndVerified") _initramfsStep++;
        if (_initramfsStep == InitramfsSteps.Length) { _initramfsVerified = true; _initramfsReference = reference; }
        if (outcome == "OutcomeUnknown") State = DebianMountSessionState.OutcomeUnknown;
        return new { Challenge = challenge, Accepted = true, Reference = reference, Sha256 = _importPreviousHash };
    }

    private void RequireAcceptedImage(JsonElement image)
    {
        if (_initramfsImageSha256 is null || image.GetProperty("Sha256").GetString() != _initramfsImageSha256 ||
            image.GetProperty("Length").GetInt64() != _initramfsImageLength)
            throw new InvalidDataException("Installed image differs from qualified candidate.");
    }
}
