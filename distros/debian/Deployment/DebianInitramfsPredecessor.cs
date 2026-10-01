using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// Exact completed image successor, not a new neutral import or effect capability.
public sealed class DebianVerifiedInitramfs
{
    private static readonly JsonSerializerOptions NativeAsciiObservation = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    // Native observer digests bind canonical ASCII JSON. Journal serialization
    // escapes '+' as \u002B without changing the observation. Raw journal bytes
    // still pass their independent chain hash; never rewrite historical records.
    private static bool ObservationHashMatches(JsonElement observation, string? expected)
    {
        if (DebianDeploymentPlanning.TextHash(observation.GetRawText()) == expected) return true;
        var native = JsonSerializer.Serialize(observation, NativeAsciiObservation);
        return native.All(c => c <= 127) && DebianDeploymentPlanning.TextHash(native) == expected;
    }
    private static readonly string[] Steps = ["Baseline", "Generation", "Candidate", "Publication", "Verify"];
    private static readonly string[] Unmounts = ["UnmountPayload", "UnmountRoot"];
    private DebianVerifiedInitramfs(DebianLabInitramfsPlanV1 plan, DebianVerifiedConfiguration configured,
        string planHash, string result, string close, JsonElement observation)
    {
        Plan = plan; Configured = configured; PlanSha256 = planHash; ResultSha256 = result;
        CloseSha256 = close; Observation = observation;
    }
    public DebianLabInitramfsPlanV1 Plan { get; }
    public DebianVerifiedConfiguration Configured { get; }
    public string PlanSha256 { get; }
    public string ResultSha256 { get; }
    public string CloseSha256 { get; }
    public JsonElement Observation { get; }

    public static DebianVerifiedInitramfs Reopen(ValidatedInstallationStorage storage, DebianVerifiedConfiguration configured,
        DebianLabInitramfsPlanV1 plan, ImmutableArray<byte[]> sessionRecords, ImmutableArray<byte[]> effectRecords,
        string expectedResult, string expectedClose)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(plan);
        var planHash = plan.Fingerprint(storage, configured);
        var sessions = DebianVerifiedImport.Chain(sessionRecords, storage.Provenance, 4);
        var effects = DebianVerifiedImport.Chain(effectRecords, storage.Provenance, 4);
        if (effects.Length != Steps.Length * 2 || sessions.Length < 3 ||
            DebianConfiguredRootArtifacts.Digest(effectRecords[^1]) != expectedResult ||
            DebianConfiguredRootArtifacts.Digest(sessionRecords[^1]) != expectedClose)
            throw new InvalidDataException("Completed initramfs chains required.");
        var session = sessions[0].GetProperty("SessionId").GetGuid();
        if (session == Guid.Empty) throw new InvalidDataException("Initramfs session absent.");
        foreach (var record in sessions.Concat(effects))
            if (record.GetProperty("OperationId").GetGuid() != plan.OperationId || record.GetProperty("SessionId").GetGuid() != session ||
                record.GetProperty(nameof(PlanSha256)).GetString() != planHash ||
                record.GetProperty("ConfigurationSha256").GetString() != configured.ResultSha256 ||
                record.GetProperty("ConfigurationCloseSha256").GetString() != configured.CloseSha256)
                throw new InvalidDataException("Initramfs predecessor lineage changed.");
        var preserved = DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new { storage.Provenance, storage.Preserved }));
        var bindings = effects[0].GetProperty("Bindings").GetRawText();
        var currentBindings = JsonSerializer.Deserialize<ImmutableArray<InstallerBlockBindingV1>>(bindings);
        var previousBindings = JsonSerializer.Deserialize<ImmutableArray<InstallerBlockBindingV1>>(configured.TargetBindings);
        if (currentBindings.IsDefault || currentBindings.Length != 3) throw new InvalidDataException("Initramfs lease set missing.");
        for (var i = 0; i < 3; i++)
        {
            var current = currentBindings[i]; var previous = previousBindings[i];
            if (current.Role != previous.Role || current.Access != previous.Access || current.Partition is not null ||
                current.StoragePartition != previous.StoragePartition || current.FileSystem != previous.FileSystem ||
                current.FileSystemUuid != previous.FileSystemUuid ||
                current.CanonicalSha256 != DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new {
                    storage.Provenance, current.Role, Partition = current.StoragePartition,
                    FileSystem = storage.Receipts[i == 0 ? 2 : i - 1].FileSystem, current.Access })))
                throw new InvalidDataException("Initramfs target differs from configured predecessor.");
        }
        string? imageHash = null; long imageLength = 0;
        for (var i = 0; i < effects.Length; i++)
        {
            var r = effects[i]; var step = Steps[i / 2];
            if (r.GetProperty("Step").GetString() != step || r.GetProperty("Outcome").GetString() != (i % 2 == 0 ? "IntentDurable" : "AppliedAndVerified") ||
                r.GetProperty("Bindings").GetRawText() != bindings || r.GetProperty("ProtectedStateSha256").GetString() != preserved)
                throw new InvalidDataException("Initramfs target or effect order changed.");
            if (i % 2 == 0) continue;
            var e = r.GetProperty("Evidence"); var o = e.GetProperty("ObserverEvidence");
            if (!ObservationHashMatches(o, e.GetProperty("ObserverEvidenceSha256").GetString()) ||
                o.GetProperty("OperationId").GetGuid() != plan.OperationId || o.GetProperty(nameof(PlanSha256)).GetString() != planHash ||
                o.GetProperty("Step").GetString() != step || o.GetProperty("ConfiguredEntriesSha256").GetString() != plan.ConfiguredEntriesSha256 ||
                o.GetProperty("PackageStateSha256").GetString() != configured.Observation.GetProperty("PackageStateSha256").GetString())
                throw new InvalidDataException("Initramfs independent observation changed.");
            if (step == "Baseline") continue;
            var image = o.GetProperty("Image"); var hash = image.GetProperty("Sha256").GetString(); var length = image.GetProperty("Length").GetInt64();
            if (!DebianDeploymentPlanning.Hash(hash) || length is <= 0 or > 536870912 || imageHash is not null && (imageHash != hash || imageLength != length))
                throw new InvalidDataException("Initramfs image changed between effects.");
            imageHash = hash; imageLength = length;
            if (step == "Generation" && (e.GetProperty("HelperEvidence").GetProperty("State").GetString() != "Exited" ||
                e.GetProperty("HelperEvidence").GetProperty("ExitCode").GetInt32() != 0))
                throw new InvalidDataException("Initramfs generator did not complete.");
            if (step is "Candidate" or "Verify")
                if (!o.GetProperty("CompleteImageQualification").GetBoolean() ||
                    o.GetProperty("ImageSemantics").GetProperty("ImageSha256").GetString() != hash ||
                    o.GetProperty("ImageSemantics").GetProperty("ImageLength").GetInt64() != length)
                    throw new InvalidDataException("Complete image qualification missing.");
            if (step == "Publication")
            {
                var publication = effects[i - 1].GetProperty("Evidence").GetProperty("Publication");
                var written = e.GetProperty("Publication");
                if (publication.GetProperty("Destination").GetString() != DebianLabInitramfsPlanV1.Destination ||
                    publication.GetProperty("Policy").GetString() != "CreateNewInitramfsImageV1" ||
                    publication.GetProperty("Uid").GetInt32() != 0 || publication.GetProperty("Gid").GetInt32() != 0 ||
                    publication.GetProperty("Mode").GetInt32() != 384 ||
                    publication.GetProperty("Candidate").GetProperty("Sha256").GetString() != hash ||
                    publication.GetProperty("Candidate").GetProperty("Length").GetInt64() != length ||
                    written.GetProperty("Sha256").GetString() != hash || written.GetProperty("Length").GetInt64() != length)
                    throw new InvalidDataException("Initramfs publication differs.");
            }
        }
        var close = sessions[^1];
        if (close.GetProperty("Action").GetString() != "Close" || close.GetProperty("State").GetString() != "AppliedAndVerified" ||
            close.GetProperty("InitramfsResultReference").GetString() != $"{effects.Length - 1:D8}-{expectedResult}.json")
            throw new InvalidDataException("Initramfs Close or handoff missing.");
        var unmounts = sessions.Where(r => r.GetProperty("Action").GetString()!.StartsWith("Unmount", StringComparison.Ordinal) &&
            r.GetProperty("State").GetString() == "AppliedAndVerified").ToArray();
        if (!unmounts.Select(r => r.GetProperty("Action").GetString()).SequenceEqual(Unmounts))
            throw new InvalidDataException("Initramfs exact teardown missing.");
        foreach (var r in unmounts)
        {
            var e = r.GetProperty("Evidence"); var path = e.GetProperty("Path").GetString();
            var before = e.GetProperty("Before").GetProperty("Mounts").EnumerateArray().ToArray();
            var after = e.GetProperty("After").GetProperty("Mounts").EnumerateArray().ToArray();
            if (before.Count(m => m.GetProperty("Path").GetString() == path) != 1 || after.Any(m => m.GetProperty("Path").GetString() == path) ||
                !before.Where(m => m.GetProperty("Path").GetString() != path).Select(m => m.GetRawText()).SequenceEqual(after.Select(m => m.GetRawText())))
                throw new InvalidDataException("Initramfs teardown delta changed.");
        }
        return new(plan, configured, planHash, expectedResult, expectedClose, effects[^1].GetProperty("Evidence").GetProperty("ObserverEvidence").Clone());
    }
}
