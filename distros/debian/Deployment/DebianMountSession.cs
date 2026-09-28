using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public enum DebianMountSessionAction { AcquireLeases, MountRoot, MountLinuxEsp, MountPayload, Inspect, UnmountLinuxEsp, UnmountPayload, UnmountRoot, Close, ImportConfiguredRoot, PrepareImportMountpoints }
public enum DebianMountSessionState { New, Active, Closed, OutcomeUnknown }

// Authority side of the private supervisor pipe. Native code sends fresh observations here;
// it cannot certify its own GPT ownership. This is NOT the 43-stage host or a success receipt.
// Use a separate journal instance/store from the stage runner, with the same plan/generation.
public sealed class DebianMountSessionAuthority
{
    private static readonly string[] ImportReadbackHashes = ["FilesystemSha256", "PackageStateSha256", "NeutralStateSha256"];
    private static readonly string[] ImportResultHashes = ["FilesystemSha256", "PackageStateSha256", "NeutralStateSha256", "ObserverEvidenceSha256"];
    private readonly InstallationOwnershipV1 _ownership;
    private readonly RootFileSystemReceiptV1 _root;
    private readonly string _planHash;
    private readonly IDebianDeploymentJournal _journal;
    private readonly IDebianDeploymentJournal? _importJournal;
    private readonly IDebianConfiguredRootAuthenticator? _authenticator;
    private bool _sourceAuthenticated;
    private bool _importReserved;
    private bool _importIntent;
    private bool _importVerified;
    private string? _importResultReference;
    private long _importSequence;
    private string? _importPreviousHash;
    private int _importFilesWritten;
    private DateTimeOffset _sourceExpires;
    private bool _scaffold;

    public DebianMountSessionAuthority(DebianDeploymentPlanV1 plan, IDebianDeploymentJournal journal)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ownership = plan.Ownership; _root = plan.Root;
        _planHash = DebianDeploymentPlanning.Fingerprint(plan);
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    private DebianMountSessionAuthority(DebianConfiguredRootImportPlanV1 plan, IDebianDeploymentJournal sessionJournal,
        IDebianDeploymentJournal importJournal, IDebianConfiguredRootAuthenticator authenticator)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ImportPlan = plan; _ownership = plan.Ownership; _root = plan.Root; _planHash = plan.Fingerprint();
        _journal = sessionJournal ?? throw new ArgumentNullException(nameof(sessionJournal));
        _importJournal = importJournal ?? throw new ArgumentNullException(nameof(importJournal));
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        if (ReferenceEquals(sessionJournal, importJournal)) throw new ArgumentException("Separate session and import journal stores required.");
        if (sessionJournal is DebianLinuxDeploymentJournal nativeSessionJournal) nativeSessionJournal.RequirePersistentStoreWitness();
        if (importJournal is DebianLinuxDeploymentJournal nativeImportJournal) nativeImportJournal.RequirePersistentStoreWitness();
    }

    public static DebianMountSessionAuthority ForDevelopmentImport(DebianConfiguredRootImportPlanV1 plan,
        IDebianDeploymentJournal sessionJournal, IDebianDeploymentJournal importJournal, DebianRootDevelopmentPinV1 externalPin)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(externalPin);
        if (externalPin.BuildId != plan.BuildId || externalPin.DescriptorSha256 != plan.DescriptorSha256 || externalPin.PolicySha256 != plan.PolicySha256)
            throw new InvalidDataException("External development pin differs from import plan.");
        return new(plan, sessionJournal, importJournal, new DebianDevelopmentRootAuthenticator(externalPin));
    }

    public DebianConfiguredRootImportPlanV1? ImportPlan { get; }
    private readonly HashSet<Guid> _challenges = [];
    private readonly List<DebianMountSessionAction> _mounted = [];
    private long _sequence;
    private string? _previousHash;
    private DebianMountSessionAction? _action;
    private bool _intent;
    private bool _result;
    private long _observationSequence;
    private bool _teardown;
    private long _actionObservationSequence;
    private long _intentObservationSequence;
    private bool _ready;

    public Guid SessionId { get; } = Guid.NewGuid();
    public Guid GenerationId => _root.GenerationId;
    public string PlanSha256 => _planHash;
    public InstallerBlockLeaseSet? Leases { get; private set; }
    public DebianMountSessionState State { get; private set; }

    public async Task BeginAsync(CancellationToken ct)
    {
        if (State != DebianMountSessionState.New) throw new InvalidOperationException("Session is single-use.");
        if (ImportPlan is not null && ImportPlan.TransportSupport.Availability != ObservationAvailability.Available)
            throw new NotSupportedException(ImportPlan.TransportSupport.Code);
        State = DebianMountSessionState.OutcomeUnknown;
        await _journal.BeginNewAsync(GenerationId, _planHash, ct).ConfigureAwait(false);
        await PersistAsync("CreatePrivateSupervisor", "IntentDurable", null, ct).ConfigureAwait(false);
        State = DebianMountSessionState.Active;
    }

    public async Task ObserveSupervisorAsync(JsonElement message, string parentMountNamespace, CancellationToken ct)
    {
        try
        {
            if (State != DebianMountSessionState.Active || _ready || _action is not null ||
                message.GetProperty(nameof(SessionId)).GetGuid() != SessionId ||
                message.GetProperty("Kind").GetString() != "SessionReady") throw new InvalidDataException("Unexpected supervisor startup.");
            var observation = message.GetProperty("Observation");
            var namespaceValue = observation.GetProperty("Namespace");
            var ns = namespaceValue.ValueKind == JsonValueKind.Number
                ? $"mnt:[{namespaceValue.GetUInt64()}]" : namespaceValue.GetString();
            if (string.IsNullOrEmpty(parentMountNamespace) || string.IsNullOrEmpty(ns) || !ns.StartsWith("mnt:[", StringComparison.Ordinal) || ns == parentMountNamespace ||
                observation.GetProperty("Mounts").GetArrayLength() == 0 ||
                observation.GetProperty("Mounts").EnumerateArray().Any(m => m.GetProperty("Propagation").GetArrayLength() != 0))
                throw new InvalidDataException("Independent private namespace observation rejected.");
            await PersistAsync("CreatePrivateSupervisor", "AppliedAndVerified", observation, ct).ConfigureAwait(false);
            _ready = true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException or OperationCanceledException)
        {
            State = DebianMountSessionState.OutcomeUnknown;
            throw;
        }
    }

    public void BeginAction(DebianMountSessionAction action)
    {
        if (State != DebianMountSessionState.Active || !_ready || _action is not null || !Enum.IsDefined(action))
            throw new InvalidOperationException("Session unavailable or concurrent action.");
        var expected = _mounted.Count switch
        {
            0 => Leases is null ? DebianMountSessionAction.AcquireLeases : ImportPlan is not null && !_scaffold
                ? DebianMountSessionAction.PrepareImportMountpoints : DebianMountSessionAction.MountRoot,
            1 => ImportPlan is null ? DebianMountSessionAction.MountLinuxEsp : DebianMountSessionAction.MountPayload,
            2 => ImportPlan is null ? DebianMountSessionAction.MountPayload : DebianMountSessionAction.Inspect,
            _ => DebianMountSessionAction.Inspect,
        };
        var allowed = action == expected && !_teardown || Leases is not null && action == DebianMountSessionAction.Inspect ||
            action == DebianMountSessionAction.ImportConfiguredRoot && ImportPlan is not null && !_importReserved && !_teardown &&
                _mounted.SequenceEqual([DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload]) ||
            action == DebianMountSessionAction.Close && _mounted.Count == 0 ||
            action == DebianMountSessionAction.UnmountLinuxEsp && _mounted.Contains(DebianMountSessionAction.MountLinuxEsp) ||
            action == DebianMountSessionAction.UnmountPayload && !_mounted.Contains(DebianMountSessionAction.MountLinuxEsp) && _mounted.Contains(DebianMountSessionAction.MountPayload) ||
            action == DebianMountSessionAction.UnmountRoot && _mounted.SequenceEqual([DebianMountSessionAction.MountRoot]);
        if (!allowed) throw new InvalidOperationException("Undeclared mount sequence.");
        if (ImportPlan is not null && action == DebianMountSessionAction.MountLinuxEsp)
            throw new InvalidOperationException("Import phase cannot compose an ESP beneath the root.");
        if (action is DebianMountSessionAction.UnmountRoot or DebianMountSessionAction.UnmountLinuxEsp or DebianMountSessionAction.UnmountPayload or DebianMountSessionAction.Close)
            _teardown = true;
        _action = action; _intent = false; _result = false;
        _actionObservationSequence = _observationSequence;
    }

    // Event JSON comes only from the hash-pinned supervisor on its private pipe. It is never a
    // disk-file capability, HTTP API or caller-supplied successful observation.
    public async Task<JsonElement> AcceptEventAsync(JsonElement message, CancellationToken ct)
    {
        try
        {
            if (State != DebianMountSessionState.Active || _action is null ||
                message.GetProperty(nameof(SessionId)).GetGuid() != SessionId || message.GetProperty(nameof(GenerationId)).GetGuid() != GenerationId ||
                message.GetProperty(nameof(PlanSha256)).GetString() != _planHash || message.GetProperty("Action").GetString() != _action.ToString())
                throw new InvalidDataException("Supervisor session binding changed.");
            var challenge = message.GetProperty("Challenge").GetGuid();
            if (challenge == Guid.Empty || _challenges.Count >= 20000 || !_challenges.Add(challenge)) throw new InvalidDataException("Replayed or excessive supervisor request.");
            object response;
            switch (message.GetProperty("Kind").GetString())
            {
                case "Inventory":
                    var sequence = message.GetProperty("ObservationSequence").GetInt64();
                    if (sequence != _observationSequence + 1) throw new InvalidDataException("Stale inventory sequence.");
                    var inventory = LinuxInstallerInventoryProtocol.Parse(message.GetProperty("Inventory").GetRawText());
                    var numbers = JsonSerializer.Deserialize<ImmutableArray<LinuxDeviceNumberV1>>(message.GetProperty("DeviceNumbers"));
                    if (Leases is null)
                    {
                        if (_action != DebianMountSessionAction.AcquireLeases) throw new InvalidDataException("Lease acquisition out of order.");
                        var acquired = InstallerBlockLeases.Acquire(_ownership, _root, GenerationId, SessionId,
                            DateTimeOffset.UtcNow, inventory, Observations.Available(numbers));
                        if (acquired.Availability != ObservationAvailability.Available)
                        {
                            await RejectObservationAsync(acquired.Availability, acquired.Code!, ct).ConfigureAwait(false);
                            throw new InvalidDataException(acquired.Code);
                        }
                        Leases = acquired.Value;
                    }
                    else
                    {
                        var fresh = InstallerBlockLeases.Revalidate(Leases, _ownership, _root, inventory, Observations.Available(numbers));
                        if (fresh.Availability != ObservationAvailability.Available)
                        {
                            await RejectObservationAsync(fresh.Availability, fresh.Code!, ct).ConfigureAwait(false);
                            throw new InvalidDataException(fresh.Code);
                        }
                    }
                    _observationSequence = sequence;
                    response = new { Challenge = challenge, Accepted = true, Leases };
                    break;
                case "AuthenticateImportSource":
                    if (_action != DebianMountSessionAction.ImportConfiguredRoot || ImportPlan is null || _authenticator is null ||
                        _sourceAuthenticated || _importReserved || _observationSequence <= _actionObservationSequence)
                        throw new InvalidDataException("Import source authentication out of order.");
                    var descriptor = message.GetProperty("Descriptor").GetBytesFromBase64();
                    var now = DateTimeOffset.UtcNow;
                    var auth = await _authenticator.AuthenticateAsync(descriptor, now, ct).ConfigureAwait(false);
                    if (auth.Availability != ObservationAvailability.Available || auth.Value.Authority != "DevelopmentImportOnly" ||
                        auth.Value.DescriptorSha256 != ImportPlan.DescriptorSha256 || auth.Value.SupportedUntilUtc <= now)
                        throw new InvalidDataException("Import authentication unavailable.");
                    // Authentication performs the strict codec/duplicate-property/attestation validation.
                    var artifact = JsonSerializer.Deserialize<DebianConfiguredRootArtifactV1>(descriptor) ?? throw new InvalidDataException("Missing artifact.");
                    ImportPlan.VerifyArtifact(artifact, descriptor);
                    if (ImportPlan.Transport == "Chunked")
                        _ = DebianRootTransports.Reopen(Convert.FromBase64String(message.GetProperty("TransportManifest").GetString()!), ImportPlan);
                    _sourceAuthenticated = true; _sourceExpires = auth.Value.SupportedUntilUtc;
                    response = new { Challenge = challenge, Accepted = true, ExpiresAt = _sourceExpires, Artifact = artifact };
                    break;
                case "ImportCheckpoint":
                    response = await ImportCheckpointAsync(message, challenge, ct).ConfigureAwait(false);
                    break;
                case "Checkpoint":
                    var state = message.GetProperty("Record").GetProperty(nameof(State)).GetString();
                    if (_action == DebianMountSessionAction.Inspect || state is not ("IntentDurable" or "AppliedAndVerified") ||
                        state == "IntentDurable" && _intent || state == "AppliedAndVerified" && (!_intent || _result))
                        throw new InvalidDataException("Invalid supervisor checkpoint transition.");
                    if (_action != DebianMountSessionAction.Close && (Leases is null ||
                        state == "IntentDurable" && _observationSequence <= _actionObservationSequence ||
                        state == "AppliedAndVerified" && _observationSequence <= _intentObservationSequence))
                        throw new InvalidDataException("Fresh canonical evidence required around effect.");
                    if (_action == DebianMountSessionAction.ImportConfiguredRoot &&
                        (state == "IntentDurable" && !_importIntent || state == "AppliedAndVerified" && !_importVerified))
                        throw new InvalidDataException("Independent import result required before session result.");
                    // The bytes are kept losslessly for independent reopen. Readback success here
                    // is a supervisor effect receipt, not a Debian semantic stage promotion.
                    await PersistAsync(_action.ToString()!, state, message.GetProperty("Record"), ct).ConfigureAwait(false);
                    if (state == "IntentDurable") { _intent = true; _intentObservationSequence = _observationSequence; }
                    else _result = true;
                    response = new { Challenge = challenge, Accepted = true };
                    break;
                default: throw new InvalidDataException("Unknown supervisor event.");
            }
            return JsonSerializer.SerializeToElement(response);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException or OperationCanceledException or NotSupportedException)
        {
            State = DebianMountSessionState.OutcomeUnknown;
            throw;
        }
    }

    public void CompleteAction(JsonElement result)
    {
        try { CompleteActionCore(result); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException)
        {
            State = DebianMountSessionState.OutcomeUnknown;
            throw;
        }
    }

    private void CompleteActionCore(JsonElement result)
    {
        if (State != DebianMountSessionState.Active || _action is null || result.GetProperty(nameof(SessionId)).GetGuid() != SessionId ||
            result.GetProperty("Action").GetString() != _action.ToString() || result.GetProperty(nameof(State)).GetString() != "AppliedAndVerified" ||
            _action == DebianMountSessionAction.Inspect && _observationSequence <= _actionObservationSequence ||
            _action != DebianMountSessionAction.Inspect && (!_intent || !_result))
        {
            State = DebianMountSessionState.OutcomeUnknown;
            throw new InvalidDataException("Incomplete native session action.");
        }
        if (_action is DebianMountSessionAction.MountRoot or DebianMountSessionAction.MountLinuxEsp or DebianMountSessionAction.MountPayload)
            _mounted.Add(_action.Value);
        if (_action == DebianMountSessionAction.ImportConfiguredRoot)
        {
            var import = result.GetProperty("ImportResult");
            if (!_importVerified || import.GetProperty("Reference").GetString() != _importResultReference ||
                import.GetProperty("Sha256").GetString() != _importPreviousHash ||
                import.GetProperty(nameof(GenerationId)).GetGuid() != GenerationId || import.GetProperty(nameof(PlanSha256)).GetString() != _planHash ||
                import.GetProperty("BuildId").GetGuid() != ImportPlan!.BuildId || import.GetProperty("DerivationId").GetGuid() != ImportPlan.DerivationId ||
                import.GetProperty("DescriptorSha256").GetString() != ImportPlan.DescriptorSha256 ||
                (ImportPlan.Transport == "Chunked" && (import.GetProperty("Transport").GetString() != "Chunked" ||
                    import.GetProperty("TransportManifestSha256").GetString() != ImportPlan.TransportManifestSha256)) ||
                import.GetProperty("Qualification").GetString() != "DevelopmentImportOnly")
                throw new InvalidDataException("Import handoff differs from reopened result.");
        }
        if (_action == DebianMountSessionAction.PrepareImportMountpoints) _scaffold = true;
        if (_action is DebianMountSessionAction.UnmountRoot or DebianMountSessionAction.UnmountLinuxEsp or DebianMountSessionAction.UnmountPayload)
            _mounted.Remove(_action.Value switch
            {
                DebianMountSessionAction.UnmountRoot => DebianMountSessionAction.MountRoot,
                DebianMountSessionAction.UnmountLinuxEsp => DebianMountSessionAction.MountLinuxEsp,
                _ => DebianMountSessionAction.MountPayload,
            });
        if (_action == DebianMountSessionAction.Close) State = DebianMountSessionState.Closed;
        _action = null;
    }

    public void Invalidate() => State = DebianMountSessionState.OutcomeUnknown;

    private async Task<object> ImportCheckpointAsync(JsonElement message, Guid challenge, CancellationToken ct)
    {
        if (_action != DebianMountSessionAction.ImportConfiguredRoot || ImportPlan is null || _importJournal is null ||
            !_sourceAuthenticated || Leases is null || _importVerified)
            throw new InvalidDataException("Import checkpoint outside active source/session.");
        var evidence = message.GetProperty("Record");
        var outcome = evidence.GetProperty("Outcome").GetString();
        if (!_importReserved)
        {
            if (outcome != "IntentDurable" || DateTimeOffset.UtcNow >= _sourceExpires)
                throw new InvalidDataException("Import intent or authentication window rejected.");
            _importReserved = true; // Failed reserve is never retried in this session.
            await _importJournal.BeginNewAsync(GenerationId, _planHash, ct).ConfigureAwait(false);
        }
        else if (!_importIntent || outcome is not ("Progress" or "AppliedAndVerified" or "Failed" or "OutcomeUnknown"))
            throw new InvalidDataException("Invalid import transition.");
        if (outcome == "Progress")
        {
            var count = evidence.GetProperty("FilesWritten").GetInt32();
            if (!_intent || count <= _importFilesWritten || count > 500000)
                throw new InvalidDataException("Import progress is stale, premature or unbounded.");
            _importFilesWritten = count;
        }
        if (outcome == "AppliedAndVerified" && (!_intent || _observationSequence <= _intentObservationSequence ||
            !ImportResultHashes
                .All(key => DebianDeploymentPlanning.Hash(evidence.GetProperty(key).GetString()))))
            throw new InvalidDataException("Independent readback and fresh canonical witness required.");
        if (outcome == "AppliedAndVerified")
        {
            if (DateTimeOffset.UtcNow >= _sourceExpires) throw new InvalidDataException("Development authentication expired during import.");
            var observed = evidence.GetProperty("ObserverEvidence");
            if (DebianDeploymentPlanning.TextHash(observed.GetRawText()) != evidence.GetProperty("ObserverEvidenceSha256").GetString() ||
                observed.GetProperty(nameof(GenerationId)).GetGuid() != GenerationId || observed.GetProperty(nameof(SessionId)).GetGuid() != SessionId ||
                observed.GetProperty(nameof(PlanSha256)).GetString() != _planHash || observed.GetProperty("ManifestSha256").GetString() != ImportPlan.ManifestSha256 ||
                ImportReadbackHashes.Any(key =>
                    observed.GetProperty(key).GetString() != evidence.GetProperty(key).GetString()))
                throw new InvalidDataException("Import observer evidence substituted.");
        }
        if (outcome == "Failed")
        {
            var observed = evidence.GetProperty("ObserverEvidence");
            if (!observed.GetProperty("HasContent").GetBoolean() ||
                !DebianDeploymentPlanning.Hash(observed.GetProperty("PartialStateSha256").GetString()) ||
                DebianDeploymentPlanning.TextHash(observed.GetRawText()) != evidence.GetProperty("ObserverEvidenceSha256").GetString() ||
                observed.GetProperty(nameof(GenerationId)).GetGuid() != GenerationId || observed.GetProperty(nameof(SessionId)).GetGuid() != SessionId ||
                observed.GetProperty(nameof(PlanSha256)).GetString() != _planHash || observed.GetProperty("ManifestSha256").GetString() != ImportPlan.ManifestSha256)
                throw new InvalidDataException("Failed import requires independent partial mutation evidence.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, GenerationId, SessionId, PlanSha256 = _planHash,
            ImportPlan.BuildId, ImportPlan.DerivationId, ImportPlan.DescriptorSha256, ImportPlan.ManifestSha256, ImportPlan.ContentSha256,
            ImportPlan.Transport, ImportPlan.TransportManifestSha256,
            Leases.ProtectedStateSha256, Leases.Bindings, Sequence = _importSequence, PreviousSha256 = _importPreviousHash,
            Operation = "ImportConfiguredRoot", Outcome = outcome, Evidence = evidence });
        var reference = await _importJournal.AppendDurablyAsync(GenerationId, bytes, ct).ConfigureAwait(false);
        var reopened = await _importJournal.ReopenAsync(reference, ct).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(reopened)) throw new IOException("Import checkpoint reopen mismatch.");
        _importPreviousHash = DebianConfiguredRootArtifacts.Digest(bytes); _importSequence++;
        _importIntent = true;
        if (outcome == "AppliedAndVerified") { _importVerified = true; _importResultReference = reference; }
        if (outcome is "Failed" or "OutcomeUnknown") State = DebianMountSessionState.OutcomeUnknown;
        return new { Challenge = challenge, Accepted = true, Reference = reference, Sha256 = _importPreviousHash };
    }

    private Task RejectObservationAsync(ObservationAvailability availability, string code, CancellationToken ct) =>
        PersistAsync("CanonicalLeaseObservation", "Rejected", JsonSerializer.SerializeToElement(new { Availability = availability.ToString(), Code = code }), ct);

    private async Task PersistAsync(string action, string state, JsonElement? evidence, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, GenerationId, SessionId, PlanSha256 = _planHash,
            Sequence = _sequence, PreviousSha256 = _previousHash, Action = action, State = state,
            ImportResultReference = _importResultReference, Evidence = evidence });
        var reference = await _journal.AppendDurablyAsync(GenerationId, bytes, ct).ConfigureAwait(false);
        var reopened = await _journal.ReopenAsync(reference, ct).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(reopened)) throw new IOException("Session checkpoint reopen mismatch.");
        _previousHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        _sequence++;
    }
}
