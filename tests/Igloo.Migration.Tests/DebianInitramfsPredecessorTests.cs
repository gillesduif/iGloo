using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Xunit;

namespace Igloo.Migration.Tests;

internal static class DebianInitramfsPredecessorTests
{
    private static readonly JsonSerializerOptions NativeAscii = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Hash(byte[] raw) => DebianConfiguredRootArtifacts.Digest(raw);
    internal static void Check(ValidatedInstallationStorage storage, DebianVerifiedConfiguration configured,
        DebianLabInitramfsPlanV1 plan, InstallerBlockLeaseSet leases, InstallerLabStorageEvidenceV1 evidence)
    {
        foreach (var fault in new[] { "none", "native-escaped", "native-escaped-wrong-hash", "failed", "target", "predecessor", "observer", "image", "publication",
            "missing", "no-close", "teardown", "wrong-kernel", "rehashed-session" })
        {
            var effects = ImmutableArray.CreateBuilder<byte[]>(); var sessions = ImmutableArray.CreateBuilder<byte[]>();
            var planHash = plan.Fingerprint(storage, configured);
            void Add(ImmutableArray<byte[]>.Builder chain, Dictionary<string, object?> fields)
            {
                fields["SchemaVersion"] = 4; fields["GenerationId"] = storage.Provenance.GenerationId;
                fields["SessionId"] = leases.SessionId; fields["OperationId"] = plan.OperationId;
                fields["PlanSha256"] = planHash; fields["Provenance"] = storage.Provenance;
                fields["ConfigurationSha256"] = fault == "predecessor" ? new string('0', 64) : configured.ResultSha256;
                fields["ConfigurationCloseSha256"] = configured.CloseSha256;
                fields["Sequence"] = chain.Count; fields["PreviousSha256"] = chain.Count == 0 ? null : Hash(chain[^1]);
                chain.Add(JsonSerializer.SerializeToUtf8Bytes(fields));
            }
            foreach (var step in new[] { "Baseline", "Generation", "Candidate", "Publication", "Verify" })
            {
                if (step == "Publication" && fault == "missing") continue;
                var image = new { Length = 1024, Sha256 = new string(fault == "image" && step == "Verify" ? 'F' : 'A', 64) };
                var observation = JsonSerializer.SerializeToElement(new { plan.OperationId, PlanSha256 = planHash, Step = step,
                    plan.ConfiguredEntriesSha256, PackageStateSha256 = configured.Observation.GetProperty("PackageStateSha256").GetString(),
                    Image = image, CompleteImageQualification = fault != "observer",
                    ImageSemantics = new { ImageLength = image.Length, ImageSha256 = image.Sha256,
                        Path = fault.StartsWith("native-escaped", StringComparison.Ordinal) ? DebianLabInitramfsPlanV1.Destination : "/synthetic-image" } });
                var publication = new { Destination = fault == "publication" ? "/etc/other" : DebianLabInitramfsPlanV1.Destination,
                    Policy = "CreateNewInitramfsImageV1", Uid = 0, Gid = 0, Mode = 384, Candidate = image, image.Length, image.Sha256 };
                foreach (var outcome in new[] { "IntentDurable", fault == "failed" ? "OutcomeUnknown" : "AppliedAndVerified" })
                    Add(effects, new() { ["Step"] = step, ["Outcome"] = outcome,
                        ["Bindings"] = fault == "target" ? leases.Bindings.SetItem(0, leases.Bindings[0] with { FileSystemUuid = Guid.NewGuid().ToString() }) : leases.Bindings,
                        ["ProtectedStateSha256"] = leases.ProtectedStateSha256,
                        ["Evidence"] = new { ObserverEvidence = observation, ObserverEvidenceSha256 = fault == "native-escaped-wrong-hash" ? new string('0', 64) : Hash(JsonSerializer.SerializeToUtf8Bytes(observation, fault == "native-escaped"
                            ? NativeAscii : null)),
                            Publication = publication, HelperEvidence = new { State = "Exited", ExitCode = 0 } } });
            }
            var root = new { Id = 1, Path = "/synthetic-root" }; var payload = new { Id = 2, Path = "/synthetic-payload" };
            if (fault != "teardown") Add(sessions, new() { ["Action"] = "UnmountPayload", ["State"] = "AppliedAndVerified",
                ["Evidence"] = new { Path = payload.Path, Before = new { Mounts = new[] { root, payload } }, After = new { Mounts = new[] { root } } } });
            Add(sessions, new() { ["Action"] = "UnmountRoot", ["State"] = "AppliedAndVerified",
                ["Evidence"] = new { Path = root.Path, Before = new { Mounts = new[] { root } }, After = new { Mounts = Array.Empty<object>() } } });
            Add(sessions, new() { ["Action"] = "Close", ["State"] = fault == "no-close" ? "OutcomeUnknown" : "AppliedAndVerified",
                ["InitramfsResultReference"] = $"{effects.Count - 1:D8}-{Hash(effects[^1])}.json" });
            var checkPlan = plan;
            if (fault == "wrong-kernel")
            {
                var c = System.Text.Json.Nodes.JsonNode.Parse(plan.Candidate.GetRawText())!; c["KernelRelease"] = "runtime-kernel";
                checkPlan = plan with { Candidate = JsonSerializer.SerializeToElement(c) };
            }
            var expectedClose = fault == "rehashed-session" ? new string('0', 64) : Hash(sessions[^1]);
            DebianVerifiedInitramfs Reopen() => DebianVerifiedInitramfs.Reopen(storage, configured, checkPlan, sessions.ToImmutable(),
                effects.ToImmutable(), Hash(effects[^1]), expectedClose);
            if (fault is "none" or "native-escaped")
            {
                var verified = Reopen(); Assert.Equal(configured, verified.Configured); Assert.Equal(planHash, verified.PlanSha256);
                DebianUserDataSuccessorTests.Check(storage, verified, evidence);
            }
            else Assert.Throws<InvalidDataException>(Reopen);
        }
    }
}
