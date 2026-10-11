using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// A completed lab-only file stage, never InstalledFirstBootPending or an
// authorization to bind inputs, enable, start, or issue DeploymentContent.
public sealed class DebianVerifiedAgentStage
{
    private static readonly string[] Steps = ["Baseline", "Publication", "Verify"];
    private DebianVerifiedAgentStage(DebianLabAgentStagePlanV1 plan, DebianVerifiedUserData predecessor,
        string planHash, string resultHash, string closeHash, JsonElement observation)
    {
        Plan = plan; Predecessor = predecessor; PlanSha256 = planHash;
        ResultSha256 = resultHash; CloseSha256 = closeHash; Observation = observation;
    }
    public DebianLabAgentStagePlanV1 Plan { get; }
    public DebianVerifiedUserData Predecessor { get; }
    public string PlanSha256 { get; }
    public string ResultSha256 { get; }
    public string CloseSha256 { get; }
    public JsonElement Observation { get; }
    public static string Lifecycle => "StagedInputsPending";

    public static DebianVerifiedAgentStage Reopen(ValidatedInstallationStorage storage,
        DebianVerifiedUserData predecessor, DebianLabAgentStagePlanV1 plan,
        ImmutableArray<byte[]> sessionRecords, ImmutableArray<byte[]> effectRecords,
        string expectedResult, string expectedClose)
    {
        ArgumentNullException.ThrowIfNull(storage); ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(plan);
        var planHash = plan.Fingerprint(storage, predecessor);
        var sessions = DebianVerifiedImport.Chain(sessionRecords, storage.Provenance, 6);
        var effects = DebianVerifiedImport.Chain(effectRecords, storage.Provenance, 6);
        if (effects.Length != Steps.Length * 2 || sessions.Length < 3 ||
            DebianConfiguredRootArtifacts.Digest(effectRecords[^1]) != expectedResult ||
            DebianConfiguredRootArtifacts.Digest(sessionRecords[^1]) != expectedClose)
            throw new InvalidDataException("Completed agent stage chains required.");
        var session = sessions[0].GetProperty("SessionId").GetGuid();
        if (session == Guid.Empty || sessions.Any(r => r.GetProperty("OperationId").GetGuid() != plan.OperationId ||
            r.GetProperty(nameof(PlanSha256)).GetString() != planHash ||
            r.GetProperty("UserDataSha256").GetString() != predecessor.ResultSha256 ||
            r.GetProperty("UserDataCloseSha256").GetString() != predecessor.CloseSha256) ||
            effects.Any(r => r.GetProperty("OperationId").GetGuid() != plan.OperationId ||
            r.GetProperty(nameof(PlanSha256)).GetString() != planHash ||
            r.GetProperty("UserDataSha256").GetString() != predecessor.ResultSha256 ||
            r.GetProperty("UserDataCloseSha256").GetString() != predecessor.CloseSha256))
            throw new InvalidDataException("Agent stage predecessor lineage changed.");
        var protectedState = DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(
            new { storage.Provenance, storage.Preserved }));
        var bindings = effects[0].GetProperty("Bindings").GetRawText();
        var currentBindings = JsonSerializer.Deserialize<ImmutableArray<InstallerBlockBindingV1>>(bindings);
        var previousBindings = JsonSerializer.Deserialize<ImmutableArray<InstallerBlockBindingV1>>(
            predecessor.Initramfs.Configured.TargetBindings);
        if (currentBindings.IsDefault || currentBindings.Length != 3 || previousBindings.IsDefault ||
            previousBindings.Length != 3)
            throw new InvalidDataException("Agent stage lease set missing.");
        for (var i = 0; i < 3; i++)
        {
            var current = currentBindings[i]; var previous = previousBindings[i];
            if (current.Role != previous.Role || current.Access != previous.Access || current.Partition is not null ||
                current.StoragePartition != previous.StoragePartition || current.FileSystem != previous.FileSystem ||
                current.FileSystemUuid != previous.FileSystemUuid ||
                current.CanonicalSha256 != DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new {
                    storage.Provenance, current.Role, Partition = current.StoragePartition,
                    FileSystem = storage.Receipts[i == 0 ? 2 : i - 1].FileSystem, current.Access })))
                throw new InvalidDataException("Agent stage target differs from UserData predecessor.");
        }
        string? stagedFilesystem = null;
        for (var i = 0; i < effects.Length; i++)
        {
            var record = effects[i]; var step = Steps[i / 2];
            if (record.GetProperty("Step").GetString() != step ||
                record.GetProperty("Outcome").GetString() != (i % 2 == 0 ? "IntentDurable" : "AppliedAndVerified") ||
                record.GetProperty("Bindings").GetRawText() != bindings ||
                record.GetProperty("ProtectedStateSha256").GetString() != protectedState)
                throw new InvalidDataException("Agent stage effect order or target changed.");
            if (i == 2)
            {
                var intent = record.GetProperty("Evidence").GetProperty("PublicationIntent");
                var files = intent.GetProperty("Files").Deserialize<DebianAgentFileV1[]>();
                if (intent.GetProperty("Policy").GetString() != "CreateNewOfflineAgentStageV1" ||
                    intent.GetProperty(nameof(PlanSha256)).GetString() !=
                        DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(plan.Stage)) ||
                    intent.GetProperty("SourceSha256").GetString() != DebianMountSessionAuthority.AgentStageSourceSha256() ||
                    files is null || !files.SequenceEqual(plan.Stage.Files) ||
                    !intent.GetProperty("Before").GetProperty("WorkerAbsent").GetBoolean() ||
                    !intent.GetProperty("Before").GetProperty("UnitAbsent").GetBoolean() ||
                    !intent.GetProperty("Before").GetProperty("ConfigurationAbsent").GetBoolean() ||
                    !intent.GetProperty("Before").GetProperty("EnableLinkAbsent").GetBoolean())
                    throw new InvalidDataException("Agent stage publication intent changed.");
            }
            if (i % 2 == 0) continue;
            var evidence = record.GetProperty("Evidence"); var observed = evidence.GetProperty("ObserverEvidence");
            var hash = observed.GetProperty("FilesystemSha256").GetString();
            if (DebianDeploymentPlanning.TextHash(observed.GetRawText()) != evidence.GetProperty("ObserverEvidenceSha256").GetString() ||
                observed.GetProperty("OperationId").GetGuid() != plan.OperationId ||
                observed.GetProperty("SessionId").GetGuid() != session ||
                observed.GetProperty(nameof(PlanSha256)).GetString() != planHash ||
                observed.GetProperty("Step").GetString() != step ||
                observed.GetProperty("PackageStateSha256").GetString() != predecessor.Observation.GetProperty("PackageStateSha256").GetString() ||
                !DebianDeploymentPlanning.Hash(hash))
                throw new InvalidDataException("Agent stage independent observation changed.");
            if (step == "Baseline")
            {
                if (hash != predecessor.Observation.GetProperty("FilesystemSha256").GetString())
                    throw new InvalidDataException("Agent stage baseline changed.");
            }
            else
            {
                var readback = observed.GetProperty("StageReadback").Deserialize<DebianAgentOfflineReadbackV1>()!;
                var accepted = DebianAgentProfiles.VerifyLabOfflineStage(plan.Stage, Observations.Available(readback));
                if (accepted.Availability != ObservationAvailability.Available ||
                    !observed.GetProperty("AddedPaths").EnumerateArray().Select(p => p.GetString()).SequenceEqual(
                        [DebianAgentProfiles.UnitPath, "/usr/lib/igloo", DebianAgentProfiles.AgentPath]) ||
                    stagedFilesystem is not null && stagedFilesystem != hash ||
                    hash == predecessor.Observation.GetProperty("FilesystemSha256").GetString() ||
                    !observed.GetProperty("UserDataAndConfiguredInitramfsPreserved").GetBoolean() ||
                    !observed.GetProperty("ConfigurationAbsent").GetBoolean() ||
                    !observed.GetProperty("EnableLinkAbsent").GetBoolean())
                    throw new InvalidDataException("Agent stage installed result changed.");
                stagedFilesystem = hash;
            }
        }
        VerifySessionActions(sessions);
        var close = sessions[^1];
        if (close.GetProperty("Action").GetString() != "Close" ||
            close.GetProperty("State").GetString() != "AppliedAndVerified" ||
            close.GetProperty("AgentStageResultReference").GetString() != $"{effects.Length - 1:D8}-{expectedResult}.json" ||
            !sessions.Where(r => r.GetProperty("Action").GetString()!.StartsWith("Unmount", StringComparison.Ordinal) &&
                r.GetProperty("State").GetString() == "AppliedAndVerified")
                .Select(r => r.GetProperty("Action").GetString()).SequenceEqual(["UnmountPayload", "UnmountRoot"]))
            throw new InvalidDataException("Agent stage exact Close missing.");
        foreach (var unmount in sessions.Where(r => r.GetProperty("Action").GetString()!.StartsWith("Unmount", StringComparison.Ordinal) &&
            r.GetProperty("State").GetString() == "AppliedAndVerified"))
        {
            var evidence = unmount.GetProperty("Evidence"); var path = evidence.GetProperty("Path").GetString();
            var before = evidence.GetProperty("Before").GetProperty("Mounts").EnumerateArray().ToArray();
            var after = evidence.GetProperty("After").GetProperty("Mounts").EnumerateArray().ToArray();
            if (before.Count(m => m.GetProperty("Path").GetString() == path) != 1 ||
                after.Any(m => m.GetProperty("Path").GetString() == path) ||
                !before.Where(m => m.GetProperty("Path").GetString() != path).Select(m => m.GetRawText())
                    .SequenceEqual(after.Select(m => m.GetRawText())))
                throw new InvalidDataException("Agent stage exact mount delta changed.");
        }
        return new(plan, predecessor, planHash, expectedResult, expectedClose,
            effects[^1].GetProperty("Evidence").GetProperty("ObserverEvidence").Clone());
    }

    internal static void VerifySessionActions(ImmutableArray<JsonElement> sessions)
    {
        string[] actions = ["CreatePrivateSupervisor", "AcquireLeases", "PrepareImportMountpoints",
            "MountRoot", "MountPayload", "StageOfflineAgent", "UnmountPayload", "UnmountRoot", "Close"];
        if (sessions.Length != actions.Length * 2 ||
            !sessions.Select(r => (r.GetProperty("Action").GetString(), r.GetProperty("State").GetString()))
                .SequenceEqual(actions.SelectMany(a => new (string?, string?)[] {
                    (a, "IntentDurable"), (a, "AppliedAndVerified") })))
            throw new InvalidDataException("Agent stage session action chain incomplete.");
    }
}
