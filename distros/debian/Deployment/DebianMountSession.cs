using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

public enum DebianMountSessionAction { AcquireLeases, MountRoot, MountLinuxEsp, MountPayload, Inspect, UnmountLinuxEsp, UnmountPayload, UnmountRoot, Close, ImportConfiguredRoot, PrepareImportMountpoints, ConfigureCore, GenerateInitramfs, TransferUserData }
public enum DebianMountSessionState { New, Active, Closed, OutcomeUnknown }

// Authority side of the private supervisor pipe. Native code sends fresh observations here;
// it cannot certify its own GPT ownership. This is NOT the 43-stage host or a success receipt.
// Use a separate journal instance/store from the stage runner, with the same plan/generation.
public sealed partial class DebianMountSessionAuthority
{
    private static readonly string[] ImportReadbackHashes = ["FilesystemSha256", "PackageStateSha256", "NeutralStateSha256"];
    private static readonly string[] ImportResultHashes = ["FilesystemSha256", "PackageStateSha256", "NeutralStateSha256", "ObserverEvidenceSha256"];
    private readonly InstallationOwnershipV1? _ownership;
    private readonly RootFileSystemReceiptV1? _root;
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
        SourcePlan = plan; _ownership = plan.Ownership; _root = plan.Root; _planHash = plan.Fingerprint();
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

    private DebianMountSessionAuthority(ValidatedInstallationStorage storage, IDebianDeploymentJournal journal)
    {
        StorageSmoke = storage ?? throw new ArgumentNullException(nameof(storage));
        if (storage.Provenance.Scope != "StorageSmoke" || storage.Provenance.Version != 1) throw new InvalidDataException("Storage smoke scope required.");
        LabStorage = storage;
        _planHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(storage.Provenance)));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        if (journal is DebianLinuxDeploymentJournal native) native.RequirePersistentStoreWitness();
    }

    public static DebianMountSessionAuthority ForLabStorageSmoke(ValidatedInstallationStorage storage, IDebianDeploymentJournal journal) => new(storage, journal);
    private DebianMountSessionAuthority(ValidatedInstallationStorage storage, DebianLabImportPlanV1 plan,
        IDebianDeploymentJournal sessionJournal, IDebianDeploymentJournal importJournal, DebianRootDevelopmentPinV1 pin)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pin);
        _planHash = plan.Fingerprint(storage, pin);
        LabStorage = storage; SourcePlan = plan;
        _journal = sessionJournal ?? throw new ArgumentNullException(nameof(sessionJournal));
        _importJournal = importJournal ?? throw new ArgumentNullException(nameof(importJournal));
        _authenticator = new DebianDevelopmentRootAuthenticator(pin);
        if (ReferenceEquals(sessionJournal, importJournal)) throw new ArgumentException("Separate session and import journal stores required.");
        if (sessionJournal is DebianLinuxDeploymentJournal nativeSession) nativeSession.RequirePersistentStoreWitness();
        if (importJournal is DebianLinuxDeploymentJournal nativeImport) nativeImport.RequirePersistentStoreWitness();
    }

    public static DebianMountSessionAuthority ForLabDevelopmentImport(ValidatedInstallationStorage storage, DebianLabImportPlanV1 plan,
        IDebianDeploymentJournal sessionJournal, IDebianDeploymentJournal importJournal, DebianRootDevelopmentPinV1 externalPin) =>
        new(storage, plan, sessionJournal, importJournal, externalPin);

    public ValidatedInstallationStorage? LabStorage { get; }
    public DebianCoreConfigurationPlanV1? ConfigurationPlan { get; }
    public DebianVerifiedImport? Predecessor { get; }
    private bool _configurationVerified;
    private int _configurationStep;
    private bool _configurationStepIntent;
    private string? _configurationReference;
    private static readonly string[] ConfigurationSteps = ["Baseline", "Files", "Debconf", "Exim", "Tls", "Locale", "User", "Credential", "Sudo", "Verify"];

    private DebianMountSessionAuthority(ValidatedInstallationStorage storage, DebianVerifiedImport predecessor,
        DebianCoreConfigurationPlanV1 plan, IDebianDeploymentJournal journal, IDebianDeploymentJournal configurationJournal,
        DebianRootDevelopmentPinV1 pin)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(configurationJournal);
        _planHash = plan.Fingerprint(storage, predecessor);
        if (pin.BuildId != predecessor.BuildId || pin.DescriptorSha256 != predecessor.DescriptorSha256 ||
            !DebianDeploymentPlanning.Hash(pin.PolicySha256) || pin.NotBeforeUtc > DateTimeOffset.UtcNow || pin.NotAfterUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("Configuration artifact authentication mismatch.");
        LabStorage = storage; Predecessor = predecessor; ConfigurationPlan = plan;
        _journal = journal; _importJournal = configurationJournal; _authenticator = new DebianDevelopmentRootAuthenticator(pin);
        if (ReferenceEquals(journal, configurationJournal)) throw new InvalidDataException("Separate configuration journals required.");
        if (journal is DebianLinuxDeploymentJournal session) session.RequirePersistentStoreWitness();
        if (configurationJournal is DebianLinuxDeploymentJournal configuration) configuration.RequirePersistentStoreWitness();
    }

    public static DebianMountSessionAuthority ForLabConfiguration(ValidatedInstallationStorage storage, DebianVerifiedImport predecessor,
        DebianCoreConfigurationPlanV1 plan, IDebianDeploymentJournal sessionJournal, IDebianDeploymentJournal configurationJournal,
        DebianRootDevelopmentPinV1 externalPin) => new(storage, predecessor, plan, sessionJournal, configurationJournal, externalPin);
    public ValidatedInstallationStorage? StorageSmoke { get; }
    private bool RootOnly => SourcePlan is not null || StorageSmoke is not null || ConfigurationPlan is not null || InitramfsPlan is not null || UserDataPlan is not null;
    public string? LastResultReference { get; private set; }
    public string? LastResultSha256 { get; private set; }

    public IDebianImportSourceBinding? SourcePlan { get; }
    public DebianConfiguredRootImportPlanV1? ImportPlan => SourcePlan as DebianConfiguredRootImportPlanV1;
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
    public Guid GenerationId => LabStorage?.Provenance.GenerationId ?? _root!.GenerationId;
    public string PlanSha256 => _planHash;
    public InstallerBlockLeaseSet? Leases { get; private set; }
    public DebianMountSessionState State { get; private set; }

    public async Task BeginAsync(CancellationToken ct)
    {
        if (State != DebianMountSessionState.New) throw new InvalidOperationException("Session is single-use.");
        if (SourcePlan is not null && SourcePlan.TransportSupport.Availability != ObservationAvailability.Available)
            throw new NotSupportedException(SourcePlan.TransportSupport.Code);
        State = DebianMountSessionState.OutcomeUnknown;
        await _journal.BeginNewAsync(GenerationId, _planHash, ct).ConfigureAwait(false);
        await PersistAsync("CreatePrivateSupervisor", "IntentDurable", null, ct).ConfigureAwait(false);
        State = DebianMountSessionState.Active;
    }

    internal static string InitramfsFailureLocation(JsonElement message)
    {
        var location = message.GetProperty("Location").GetString();
        if (location is not ("Source" or "Baseline" or "GenerationInputs" or "Generation" or "Candidate" or "Publication" or "Verify" or "Handoff"))
            throw new InvalidDataException("Unknown initramfs failure location.");
        return location;
    }

    internal static object LeaseResponse(Guid challenge, InstallerBlockLeaseSet leases) =>
        new { Challenge = challenge, Accepted = true, Leases = leases };

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
            0 => Leases is null ? DebianMountSessionAction.AcquireLeases : RootOnly && !_scaffold
                ? DebianMountSessionAction.PrepareImportMountpoints : DebianMountSessionAction.MountRoot,
            1 => !RootOnly ? DebianMountSessionAction.MountLinuxEsp : DebianMountSessionAction.MountPayload,
            2 => !RootOnly ? DebianMountSessionAction.MountPayload : DebianMountSessionAction.Inspect,
            _ => DebianMountSessionAction.Inspect,
        };
        var allowed = action == expected && !_teardown || Leases is not null && action == DebianMountSessionAction.Inspect ||
            action == DebianMountSessionAction.TransferUserData && UserDataPlan is not null && !_importReserved && !_teardown &&
                _mounted.SequenceEqual([DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload]) ||
            action == DebianMountSessionAction.GenerateInitramfs && InitramfsPlan is not null && !_importReserved && !_teardown &&
                _mounted.SequenceEqual([DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload]) ||
            action == DebianMountSessionAction.ConfigureCore && ConfigurationPlan is not null && !_importReserved && !_teardown &&
                _mounted.SequenceEqual([DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload]) ||
            action == DebianMountSessionAction.ImportConfiguredRoot && SourcePlan is not null && !_importReserved && !_teardown &&
                _mounted.SequenceEqual([DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload]) ||
            action == DebianMountSessionAction.Close && _mounted.Count == 0 ||
            action == DebianMountSessionAction.UnmountLinuxEsp && _mounted.Contains(DebianMountSessionAction.MountLinuxEsp) ||
            action == DebianMountSessionAction.UnmountPayload && !_mounted.Contains(DebianMountSessionAction.MountLinuxEsp) && _mounted.Contains(DebianMountSessionAction.MountPayload) ||
            action == DebianMountSessionAction.UnmountRoot && _mounted.SequenceEqual([DebianMountSessionAction.MountRoot]);
        if (!allowed) throw new InvalidOperationException("Undeclared mount sequence.");
        if (RootOnly && action == DebianMountSessionAction.MountLinuxEsp)
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
                case "AuthenticateConfigurationSource":
                    if (!(ConfigurationPlan is not null && _action == DebianMountSessionAction.ConfigureCore ||
                        InitramfsPlan is not null && _action == DebianMountSessionAction.GenerateInitramfs ||
                        UserDataPlan is not null && _action == DebianMountSessionAction.TransferUserData) ||
                        Predecessor is null || _authenticator is null || _sourceAuthenticated || _observationSequence <= _actionObservationSequence)
                        throw new InvalidDataException("Configuration authentication out of order.");
                    var configurationDescriptor = message.GetProperty("Descriptor").GetBytesFromBase64();
                    var authentication = await _authenticator.AuthenticateAsync(configurationDescriptor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                    if (authentication.Availability != ObservationAvailability.Available || authentication.Value.Authority != "DevelopmentImportOnly" ||
                        authentication.Value.DescriptorSha256 != Predecessor.DescriptorSha256)
                        throw new InvalidDataException("Configuration source authentication rejected.");
                    var configuredArtifact = JsonSerializer.Deserialize<DebianConfiguredRootArtifactV1>(configurationDescriptor)!;
                    if (configuredArtifact.Manifest.Sha256 != Predecessor.ManifestSha256 || configuredArtifact.Content.Sha256 != Predecessor.ContentSha256)
                        throw new InvalidDataException("Configuration baseline changed.");
                    _sourceAuthenticated = true; _sourceExpires = authentication.Value.SupportedUntilUtc;
                    response = new { Challenge = challenge, Accepted = true, Artifact = configuredArtifact,
                        Fstab = TargetRootMounts.GenerateFstab(Leases ?? throw new InvalidDataException("Missing configuration leases.")),
                        AptSources = DebianDeploymentPlanning.InstalledAptSources };
                    break;
                case "ConfigurationCheckpoint":
                    response = await ConfigurationCheckpointAsync(message, challenge, ct).ConfigureAwait(false);
                    break;
                case "UserDataContext":
                    response = UserDataContext(challenge);
                    break;
                case "UserDataCheckpoint":
                    response = await UserDataCheckpointAsync(message, challenge, ct).ConfigureAwait(false);
                    break;
                case "UserDataStopped":
                    if (UserDataPlan is null || _action != DebianMountSessionAction.TransferUserData || _userDataVerified || !_sourceAuthenticated)
                        throw new InvalidDataException("UserData failure outside active authority.");
                    await PersistAsync("TransferUserData", "OutcomeUnknown", JsonSerializer.SerializeToElement(new {
                        Code = "UserDataOperationStopped", CompletedSteps = _userDataStep, UserDataResultReference = _userDataResultReference }), ct).ConfigureAwait(false);
                    State = DebianMountSessionState.OutcomeUnknown;
                    response = new { Challenge = challenge, Accepted = true };
                    break;
                case "InitramfsCheckpoint":
                    response = await InitramfsCheckpointAsync(message, challenge, ct).ConfigureAwait(false);
                    break;
                case "InitramfsStopped":
                    if (InitramfsPlan is null || _action != DebianMountSessionAction.GenerateInitramfs ||
                        _initramfsVerified || !_sourceAuthenticated)
                        throw new InvalidDataException("Initramfs failure outside active authority.");
                    await PersistAsync("GenerateInitramfs", "OutcomeUnknown", JsonSerializer.SerializeToElement(new {
                        Code = "InitramfsOperationStopped", Location = InitramfsFailureLocation(message), CompletedSteps = _initramfsStep,
                        InitramfsResultReference = _initramfsReference }), ct).ConfigureAwait(false);
                    State = DebianMountSessionState.OutcomeUnknown;
                    response = new { Challenge = challenge, Accepted = true };
                    break;
                case "ConfigurationStopped":
                    if (ConfigurationPlan is null || _action != DebianMountSessionAction.ConfigureCore || _configurationVerified ||
                        !_importReserved || message.GetProperty("Phase").GetString() is not
                            ("Baseline" or "ViewSetup" or "Files" or "Debconf" or "Exim" or "Tls" or "Locale" or "User" or "Credential" or "Sudo" or "Verify"))
                        throw new InvalidDataException("Configuration failure outside active phase.");
                    await PersistAsync("ConfigureCore", "OutcomeUnknown", JsonSerializer.SerializeToElement(new {
                        Phase = message.GetProperty("Phase").GetString(),
                        Code = "ConfigurationOperationStopped", CompletedSteps = _configurationStep,
                        ConfigurationResultReference = _configurationReference }), ct).ConfigureAwait(false);
                    State = DebianMountSessionState.OutcomeUnknown;
                    response = new { Challenge = challenge, Accepted = true };
                    break;
                case "Inventory":
                    var sequence = message.GetProperty("ObservationSequence").GetInt64();
                    if (sequence != _observationSequence + 1) throw new InvalidDataException("Stale inventory sequence.");
                    var inventory = LinuxInstallerInventoryProtocol.Parse(message.GetProperty("Inventory").GetRawText());
                    var numbers = JsonSerializer.Deserialize<ImmutableArray<LinuxDeviceNumberV1>>(message.GetProperty("DeviceNumbers"));
                    if (Leases is null)
                    {
                        if (_action != DebianMountSessionAction.AcquireLeases) throw new InvalidDataException("Lease acquisition out of order.");
                        var acquired = LabStorage is null
                            ? InstallerBlockLeases.Acquire(_ownership!, _root!, GenerationId, SessionId,
                                DateTimeOffset.UtcNow, inventory, Observations.Available(numbers))
                            : InstallerBlockLeases.Acquire(LabStorage!, SessionId, DateTimeOffset.UtcNow, inventory,
                                Observations.Available(numbers), ReadGuest(message));
                        if (acquired.Availability != ObservationAvailability.Available)
                        {
                            await RejectObservationAsync(acquired.Availability, acquired.Code!, ct).ConfigureAwait(false);
                            throw new InvalidDataException(acquired.Code);
                        }
                        Leases = acquired.Value;
                    }
                    else
                    {
                        var fresh = LabStorage is null
                            ? InstallerBlockLeases.Revalidate(Leases, _ownership!, _root!, inventory, Observations.Available(numbers))
                            : InstallerBlockLeases.Revalidate(Leases, LabStorage!, inventory, Observations.Available(numbers), ReadGuest(message));
                        if (fresh.Availability != ObservationAvailability.Available)
                        {
                            await RejectObservationAsync(fresh.Availability, fresh.Code!, ct).ConfigureAwait(false);
                            throw new InvalidDataException(fresh.Code);
                        }
                    }
                    _observationSequence = sequence;
                    response = LeaseResponse(challenge, Leases);
                    break;
                case "StorageInspection":
                    if (StorageSmoke is null || _action != DebianMountSessionAction.Inspect ||
                        !_mounted.SequenceEqual([DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload]) ||
                        _observationSequence <= _actionObservationSequence || _result)
                        throw new InvalidDataException("Storage inspection out of order.");
                    await PersistAsync("Inspect", "AppliedAndVerified", message.GetProperty("Observation"), ct).ConfigureAwait(false);
                    _result = true;
                    response = new { Challenge = challenge, Accepted = true };
                    break;
                case "AuthenticateImportSource":
                    if (_action != DebianMountSessionAction.ImportConfiguredRoot || SourcePlan is null || _authenticator is null ||
                        _sourceAuthenticated || _importReserved || _observationSequence <= _actionObservationSequence)
                        throw new InvalidDataException("Import source authentication out of order.");
                    var descriptor = message.GetProperty("Descriptor").GetBytesFromBase64();
                    var now = DateTimeOffset.UtcNow;
                    var auth = await _authenticator.AuthenticateAsync(descriptor, now, ct).ConfigureAwait(false);
                    if (auth.Availability != ObservationAvailability.Available || auth.Value.Authority != "DevelopmentImportOnly" ||
                        auth.Value.DescriptorSha256 != SourcePlan.DescriptorSha256 || auth.Value.SupportedUntilUtc <= now)
                        throw new InvalidDataException("Import authentication unavailable.");
                    // Authentication performs the strict codec/duplicate-property/attestation validation.
                    var artifact = JsonSerializer.Deserialize<DebianConfiguredRootArtifactV1>(descriptor) ?? throw new InvalidDataException("Missing artifact.");
                    SourcePlan.VerifyArtifact(artifact, descriptor);
                    if (SourcePlan.Transport == "Chunked")
                        _ = DebianRootTransports.Reopen(Convert.FromBase64String(message.GetProperty("TransportManifest").GetString()!), SourcePlan);
                    _sourceAuthenticated = true; _sourceExpires = auth.Value.SupportedUntilUtc;
                    response = new { Challenge = challenge, Accepted = true, ExpiresAt = _sourceExpires, Artifact = artifact };
                    break;
                case "ImportSourceRejected":
                    if (_action != DebianMountSessionAction.ImportConfiguredRoot || SourcePlan is null ||
                        !_sourceAuthenticated || _importReserved || _importIntent || _intent ||
                        message.GetProperty("Code").GetString() is not ("TransportChunkHashMismatch" or "TransportAggregateHashMismatch"))
                        throw new InvalidDataException("Invalid pre-import rejection checkpoint.");
                    await PersistAsync("ImportConfiguredRoot", "NotStarted", JsonSerializer.SerializeToElement(new {
                        Phase = "SourceVerification", Code = message.GetProperty("Code").GetString(),
                        ImportContentWriterInvoked = false, ImportReserved = false }), ct).ConfigureAwait(false);
                    State = DebianMountSessionState.OutcomeUnknown; // Earlier mounts still need truthful disposition; never reusable.
                    response = new { Challenge = challenge, Accepted = true };
                    break;
                case "ImportCheckpoint":
                    response = await ImportCheckpointAsync(message, challenge, ct).ConfigureAwait(false);
                    break;
                case "Checkpoint":
                    var state = message.GetProperty("Record").GetProperty(nameof(State)).GetString();
                    if (_action == DebianMountSessionAction.TransferUserData &&
                        (state == "IntentDurable" && _userDataStep != 1 || state == "AppliedAndVerified" && !_userDataVerified))
                        throw new InvalidDataException("UserData baseline/result required.");
                    if (_action == DebianMountSessionAction.GenerateInitramfs &&
                        (state == "IntentDurable" && _initramfsStep != 1 || state == "AppliedAndVerified" && !_initramfsVerified))
                        throw new InvalidDataException("Initramfs baseline/result required.");
                    if (_action == DebianMountSessionAction.ConfigureCore &&
                        (state == "IntentDurable" && _configurationStep != 1 || state == "AppliedAndVerified" && !_configurationVerified))
                        throw new InvalidDataException("Configuration baseline/result required.");
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
        if (StorageSmoke is not null && _action == DebianMountSessionAction.Inspect && !_result ||
            State != DebianMountSessionState.Active || _action is null || result.GetProperty(nameof(SessionId)).GetGuid() != SessionId ||
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
                import.GetProperty("BuildId").GetGuid() != SourcePlan!.BuildId || import.GetProperty("DerivationId").GetGuid() != SourcePlan.DerivationId ||
                import.GetProperty("DescriptorSha256").GetString() != SourcePlan.DescriptorSha256 ||
                (SourcePlan.Transport == "Chunked" && (import.GetProperty("Transport").GetString() != "Chunked" ||
                    import.GetProperty("TransportManifestSha256").GetString() != SourcePlan.TransportManifestSha256)) ||
                import.GetProperty("Qualification").GetString() != "DevelopmentImportOnly")
                throw new InvalidDataException("Import handoff differs from reopened result.");
        }
        if (_action == DebianMountSessionAction.ConfigureCore && (!_configurationVerified ||
            result.GetProperty("ConfigurationResult").GetProperty("Reference").GetString() != _configurationReference))
            throw new InvalidDataException("Configuration handoff differs from durable result.");
        if (_action == DebianMountSessionAction.GenerateInitramfs && (!_initramfsVerified ||
            result.GetProperty("InitramfsResult").GetProperty("Reference").GetString() != _initramfsReference ||
            result.GetProperty("InitramfsResult").GetProperty("Sha256").GetString() != _importPreviousHash))
            throw new InvalidDataException("Initramfs handoff differs from durable result.");
        if (_action == DebianMountSessionAction.TransferUserData && (!_userDataVerified ||
            result.GetProperty("UserDataResult").GetProperty("Reference").GetString() != _userDataResultReference ||
            result.GetProperty("UserDataResult").GetProperty("Sha256").GetString() != _importPreviousHash))
            throw new InvalidDataException("UserData handoff differs from durable result.");
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

    private async Task<object> ConfigurationCheckpointAsync(JsonElement message, Guid challenge, CancellationToken ct)
    {
        if (ConfigurationPlan is null || _action != DebianMountSessionAction.ConfigureCore || !_sourceAuthenticated ||
            DateTimeOffset.UtcNow >= _sourceExpires || _importJournal is null || Leases is null || _configurationVerified)
            throw new InvalidDataException("Configuration checkpoint outside active authority.");
        var record = message.GetProperty("Record"); var outcome = record.GetProperty("Outcome").GetString();
        var step = record.GetProperty("Step").GetString();
        if (_configurationStep >= ConfigurationSteps.Length || step != ConfigurationSteps[_configurationStep] ||
            outcome is not ("IntentDurable" or "AppliedAndVerified" or "OutcomeUnknown") ||
            outcome == "IntentDurable" && _configurationStepIntent || outcome != "IntentDurable" && !_configurationStepIntent)
            throw new InvalidDataException("Configuration effect sequence changed.");
        if (!_importReserved)
        {
            _importReserved = true;
            await _importJournal.BeginNewAsync(GenerationId, _planHash, ct).ConfigureAwait(false);
        }
        if (outcome == "AppliedAndVerified")
        {
            var observed = record.GetProperty("ObserverEvidence");
            if (DebianDeploymentPlanning.TextHash(observed.GetRawText()) != record.GetProperty("ObserverEvidenceSha256").GetString() ||
                observed.GetProperty("OperationId").GetGuid() != ConfigurationPlan.OperationId ||
                observed.GetProperty(nameof(PlanSha256)).GetString() != _planHash)
                throw new InvalidDataException("Independent configuration observer required.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 3, GenerationId, SessionId, PlanSha256 = _planHash,
            ConfigurationPlan.OperationId, LabStorage!.Provenance, Predecessor, Sequence = _importSequence, PreviousSha256 = _importPreviousHash,
            Leases.Bindings, Leases.ProtectedStateSha256, Step = step, Outcome = outcome, Evidence = record });
        var reference = await _importJournal.AppendDurablyAsync(GenerationId, bytes, ct).ConfigureAwait(false);
        var reopenedConfiguration = await _importJournal.ReopenAsync(reference, ct).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(reopenedConfiguration))
            throw new IOException("Configuration checkpoint reopen mismatch.");
        _importPreviousHash = DebianConfiguredRootArtifacts.Digest(bytes); _importSequence++;
        _configurationStepIntent = outcome == "IntentDurable";
        if (outcome == "AppliedAndVerified") _configurationStep++;
        if (_configurationStep == ConfigurationSteps.Length) { _configurationVerified = true; _configurationReference = reference; }
        if (outcome == "OutcomeUnknown") State = DebianMountSessionState.OutcomeUnknown;
        return new { Challenge = challenge, Accepted = true, Reference = reference, Sha256 = _importPreviousHash };
    }

    private async Task<object> ImportCheckpointAsync(JsonElement message, Guid challenge, CancellationToken ct)
    {
        if (_action != DebianMountSessionAction.ImportConfiguredRoot || SourcePlan is null || _importJournal is null ||
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
                observed.GetProperty(nameof(PlanSha256)).GetString() != _planHash || observed.GetProperty("ManifestSha256").GetString() != SourcePlan.ManifestSha256 ||
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
                observed.GetProperty(nameof(PlanSha256)).GetString() != _planHash || observed.GetProperty("ManifestSha256").GetString() != SourcePlan.ManifestSha256)
                throw new InvalidDataException("Failed import requires independent partial mutation evidence.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, GenerationId, SessionId, PlanSha256 = _planHash,
            SourcePlan.BuildId, SourcePlan.DerivationId, SourcePlan.DescriptorSha256, SourcePlan.ManifestSha256, SourcePlan.ContentSha256,
            SourcePlan.Transport, SourcePlan.TransportManifestSha256,
            Leases.ProtectedStateSha256, Leases.Bindings, Sequence = _importSequence, PreviousSha256 = _importPreviousHash,
            Operation = "ImportConfiguredRoot", Outcome = outcome, Evidence = evidence });
        if (LabStorage is not null)
        {
            var tagged = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
            tagged["SchemaVersion"] = 2;
            tagged["Provenance"] = JsonSerializer.SerializeToNode(LabStorage.Provenance);
            bytes = JsonSerializer.SerializeToUtf8Bytes(tagged);
        }
        var reference = await _importJournal.AppendDurablyAsync(GenerationId, bytes, ct).ConfigureAwait(false);
        var reopened = await _importJournal.ReopenAsync(reference, ct).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(reopened)) throw new IOException("Import checkpoint reopen mismatch.");
        _importPreviousHash = DebianConfiguredRootArtifacts.Digest(bytes); _importSequence++;
        _importIntent = true;
        if (outcome == "AppliedAndVerified") { _importVerified = true; _importResultReference = reference; }
        if (outcome is "Failed" or "OutcomeUnknown") State = DebianMountSessionState.OutcomeUnknown;
        return new { Challenge = challenge, Accepted = true, Reference = reference, Sha256 = _importPreviousHash };
    }

    private static Observation<ImmutableArray<InstallerLabGuestDiskV1>> ReadGuest(JsonElement message) =>
        Observations.Available(JsonSerializer.Deserialize<ImmutableArray<InstallerLabGuestDiskV1>>(message.GetProperty("GuestDisks")));

    private Task RejectObservationAsync(ObservationAvailability availability, string code, CancellationToken ct) =>
        PersistAsync("CanonicalLeaseObservation", "Rejected", JsonSerializer.SerializeToElement(new { Availability = availability.ToString(), Code = code }), ct);

    private async Task PersistAsync(string action, string state, JsonElement? evidence, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, GenerationId, SessionId, PlanSha256 = _planHash,
            Sequence = _sequence, PreviousSha256 = _previousHash, Action = action, State = state,
            ImportResultReference = _importResultReference, Evidence = evidence });
        if (LabStorage is not null)
            bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 2, GenerationId, SessionId, PlanSha256 = _planHash,
                LabStorage.Provenance, ImportResultReference = _importResultReference, Sequence = _sequence, PreviousSha256 = _previousHash, Action = action, State = state, Evidence = evidence });
        if (ConfigurationPlan is not null)
            bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 3, GenerationId, SessionId, PlanSha256 = _planHash,
                LabStorage!.Provenance, ConfigurationPlan.OperationId, Predecessor, ConfigurationResultReference = _configurationReference,
                Sequence = _sequence, PreviousSha256 = _previousHash, Action = action, State = state, Evidence = evidence });
        if (InitramfsPlan is not null)
            bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 4, GenerationId, SessionId, PlanSha256 = _planHash,
                LabStorage!.Provenance, InitramfsPlan.OperationId, InitramfsPlan.ConfigurationSha256, InitramfsPlan.ConfigurationCloseSha256,
                InitramfsResultReference = _initramfsReference, Sequence = _sequence, PreviousSha256 = _previousHash,
                Action = action, State = state, Evidence = evidence });
        if (UserDataPlan is not null)
            bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 5, GenerationId, SessionId, PlanSha256 = _planHash,
                LabStorage!.Provenance, UserDataPlan.OperationId, UserDataPlan.InitramfsSha256, UserDataPlan.InitramfsCloseSha256,
                UserDataResultReference = _userDataResultReference, Sequence = _sequence, PreviousSha256 = _previousHash,
                Action = action, State = state, Evidence = evidence });
        var reference = await _journal.AppendDurablyAsync(GenerationId, bytes, ct).ConfigureAwait(false);
        var reopened = await _journal.ReopenAsync(reference, ct).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(reopened)) throw new IOException("Session checkpoint reopen mismatch.");
        _previousHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        LastResultReference = reference; LastResultSha256 = _previousHash;
        _sequence++;
    }
}
