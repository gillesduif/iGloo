using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// A completed, admitted selected-document operation. Reopening these records
// does not grant access to a retained disk or authorize a successor effect.
public sealed class DebianVerifiedUserData
{
    private static readonly string[] Steps = ["Baseline", "Transfer", "Admission", "Verify"];
    private static readonly JsonSerializerOptions NativeAsciiObservation = new()
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private DebianVerifiedUserData(DebianLabUserDataPlanV1 plan, DebianVerifiedInitramfs initramfs,
        string planHash, string resultHash, string closeHash, JsonElement observation,
        byte[] bundle, byte[] receipt, byte[] authority)
    {
        Plan = plan; Initramfs = initramfs; PlanSha256 = planHash; ResultSha256 = resultHash;
        CloseSha256 = closeHash; Observation = observation; AdmissionBundle = bundle;
        AdmissionReceipt = receipt; AdmissionAuthority = authority;
    }

    public DebianLabUserDataPlanV1 Plan { get; }
    public DebianVerifiedInitramfs Initramfs { get; }
    public string PlanSha256 { get; }
    public string ResultSha256 { get; }
    public string CloseSha256 { get; }
    public JsonElement Observation { get; }
    public ReadOnlyMemory<byte> AdmissionBundle { get; }
    public ReadOnlyMemory<byte> AdmissionReceipt { get; }
    public ReadOnlyMemory<byte> AdmissionAuthority { get; }

    public static DebianVerifiedUserData Reopen(ValidatedInstallationStorage storage, DebianVerifiedInitramfs initramfs,
        DebianLabUserDataPlanV1 plan, ImmutableArray<byte[]> sessionRecords, ImmutableArray<byte[]> effectRecords,
        string expectedResult, string expectedClose)
    {
        ArgumentNullException.ThrowIfNull(storage); ArgumentNullException.ThrowIfNull(initramfs);
        ArgumentNullException.ThrowIfNull(plan);
        var planHash = plan.Fingerprint(storage, initramfs);
        var sessions = DebianVerifiedImport.Chain(sessionRecords, storage.Provenance, 5);
        var effects = DebianVerifiedImport.Chain(effectRecords, storage.Provenance, 5);
        if (effects.Length != Steps.Length * 2 || sessions.Length < 3 ||
            DebianConfiguredRootArtifacts.Digest(effectRecords[^1]) != expectedResult ||
            DebianConfiguredRootArtifacts.Digest(sessionRecords[^1]) != expectedClose)
            throw new InvalidDataException("Completed UserData predecessor chains required.");
        var session = sessions[0].GetProperty("SessionId").GetGuid();
        if (session == Guid.Empty) throw new InvalidDataException("UserData session missing.");
        foreach (var record in sessions.Concat(effects))
            if (record.GetProperty("OperationId").GetGuid() != plan.OperationId ||
                record.GetProperty("SessionId").GetGuid() != session ||
                record.GetProperty(nameof(PlanSha256)).GetString() != planHash ||
                record.GetProperty("InitramfsSha256").GetString() != initramfs.ResultSha256 ||
                record.GetProperty("InitramfsCloseSha256").GetString() != initramfs.CloseSha256)
                throw new InvalidDataException("UserData predecessor lineage changed.");
        var protectedState = DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new
            { storage.Provenance, storage.Preserved }));
        var bindings = effects[0].GetProperty("Bindings").GetRawText();
        var parsedBindings = JsonSerializer.Deserialize<ImmutableArray<InstallerBlockBindingV1>>(bindings);
        var previousBindings = JsonSerializer.Deserialize<ImmutableArray<InstallerBlockBindingV1>>(
            initramfs.Configured.TargetBindings);
        if (parsedBindings.IsDefault || parsedBindings.Length != 3 ||
            previousBindings.IsDefault || previousBindings.Length != 3 ||
            parsedBindings[0].Role != InstallerBlockRole.Root || parsedBindings[0].Access != InstallerBlockAccess.ReadWrite ||
            parsedBindings[1].Role != InstallerBlockRole.LinuxEsp || parsedBindings[1].Access != InstallerBlockAccess.ReadOnly ||
            parsedBindings[2].Role != InstallerBlockRole.Payload || parsedBindings[2].Access != InstallerBlockAccess.ReadOnly ||
            parsedBindings.Any(b => b.Partition is not null || b.StoragePartition is null))
            throw new InvalidDataException("UserData predecessor lease set changed.");
        for (var i = 0; i < 3; i++)
        {
            var current = parsedBindings[i]; var earlier = previousBindings[i];
            if (current.Role != earlier.Role || current.Access != earlier.Access ||
                current.StoragePartition != earlier.StoragePartition || current.FileSystem != earlier.FileSystem ||
                current.FileSystemUuid != earlier.FileSystemUuid ||
                current.CanonicalSha256 != DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new {
                    storage.Provenance, current.Role, Partition = current.StoragePartition,
                    FileSystem = storage.Receipts[i == 0 ? 2 : i - 1].FileSystem, current.Access })))
                throw new InvalidDataException("UserData predecessor target differs from initramfs.");
        }
        byte[]? bundle = null, receipt = null, authority = null;
        JsonElement last = default;
        string? package = null;
        for (var i = 0; i < effects.Length; i++)
        {
            var record = effects[i]; var step = Steps[i / 2];
            if (record.GetProperty("Step").GetString() != step ||
                record.GetProperty("Outcome").GetString() != (i % 2 == 0 ? "IntentDurable" : "AppliedAndVerified") ||
                record.GetProperty("Bindings").GetRawText() != bindings ||
                record.GetProperty("ProtectedStateSha256").GetString() != protectedState)
                throw new InvalidDataException("UserData predecessor effect order changed.");
            if (i % 2 == 0) continue;
            var evidence = record.GetProperty("Evidence"); var observed = evidence.GetProperty("ObserverEvidence");
            if (!ObservationHashMatches(observed, evidence.GetProperty("ObserverEvidenceSha256").GetString()))
                throw new InvalidDataException("UserData observation representation changed: " + step);
            if (observed.GetProperty("OperationId").GetGuid() != plan.OperationId ||
                observed.GetProperty("SessionId").GetGuid() != session ||
                observed.GetProperty(nameof(PlanSha256)).GetString() != planHash ||
                observed.GetProperty("Step").GetString() != step ||
                !DebianDeploymentPlanning.Hash(observed.GetProperty("FilesystemSha256").GetString()) ||
                !DebianDeploymentPlanning.Hash(observed.GetProperty("PackageStateSha256").GetString()) ||
                package is not null && package != observed.GetProperty("PackageStateSha256").GetString())
                throw new InvalidDataException("UserData independent observation changed.");
            package = observed.GetProperty("PackageStateSha256").GetString();
            if (package != initramfs.Observation.GetProperty("PackageStateSha256").GetString() ||
                step != "Baseline" && (!observed.GetProperty("ConfiguredAndInitramfsPreserved").GetBoolean() ||
                observed.GetProperty("MachineIdentity").GetString() != "FirstBootPending"))
                throw new InvalidDataException("UserData configured predecessor changed.");
            if (step == "Transfer")
            {
                bundle = evidence.GetProperty("Bundle").GetBytesFromBase64();
                receipt = evidence.GetProperty("Receipt").GetBytesFromBase64();
                using var document = JsonDocument.Parse(bundle);
                DebianVerifiedImport.Unique(document.RootElement);
                authority = document.RootElement.GetProperty("Authority").GetBytesFromBase64();
                var transfer = JsonSerializer.Deserialize<DebianUserDataPlanV1>(plan.Transfer.Span)!;
                var context = JsonSerializer.Deserialize<DebianUserDataCanonicalDeclarationV1>(authority)!;
                if (!DebianUserDataAdmission.ValidateLabAdmissionStructure(transfer, context, receipt, bundle))
                    throw new InvalidDataException("UserData producer admission changed.");
            }
            if (step is "Admission" or "Verify")
            {
                if (bundle is null || receipt is null || authority is null ||
                    !observed.GetProperty("ConfiguredAndInitramfsPreserved").GetBoolean() ||
                    observed.GetProperty("MachineIdentity").GetString() != "FirstBootPending" ||
                    observed.GetProperty("Admission").GetProperty("ReceiptSha256").GetString() != DebianConfiguredRootArtifacts.Digest(receipt) ||
                    observed.GetProperty("Admission").GetProperty("EvidenceSha256").GetString() != DebianConfiguredRootArtifacts.Digest(bundle) ||
                    observed.GetProperty("Admission").GetProperty("AuthoritySha256").GetString() != DebianConfiguredRootArtifacts.Digest(authority) ||
                    observed.GetProperty("Admission").GetProperty(nameof(ResultSha256)).GetString() !=
                        JsonSerializer.Deserialize<JsonElement>(receipt).GetProperty("EvidenceSha256").GetString())
                    throw new InvalidDataException("UserData admitted result changed.");
            }
            last = observed;
        }
        var close = sessions[^1];
        if (close.GetProperty("Action").GetString() != "Close" ||
            close.GetProperty("State").GetString() != "AppliedAndVerified" ||
            close.GetProperty("UserDataResultReference").GetString() != $"{effects.Length - 1:D8}-{expectedResult}.json" ||
            !sessions.Where(s => s.GetProperty("Action").GetString()!.StartsWith("Unmount", StringComparison.Ordinal) &&
                s.GetProperty("State").GetString() == "AppliedAndVerified")
                .Select(s => s.GetProperty("Action").GetString()).SequenceEqual(["UnmountPayload", "UnmountRoot"]))
            throw new InvalidDataException("UserData exact Close/teardown missing.");
        foreach (var unmount in sessions.Where(s => s.GetProperty("Action").GetString()!.StartsWith("Unmount", StringComparison.Ordinal) &&
            s.GetProperty("State").GetString() == "AppliedAndVerified"))
        {
            var evidence = unmount.GetProperty("Evidence"); var path = evidence.GetProperty("Path").GetString();
            var before = evidence.GetProperty("Before").GetProperty("Mounts").EnumerateArray().ToArray();
            var after = evidence.GetProperty("After").GetProperty("Mounts").EnumerateArray().ToArray();
            if (before.Count(m => m.GetProperty("Path").GetString() == path) != 1 ||
                after.Any(m => m.GetProperty("Path").GetString() == path) ||
                !before.Where(m => m.GetProperty("Path").GetString() != path)
                    .Select(m => m.GetRawText()).SequenceEqual(after.Select(m => m.GetRawText())))
                throw new InvalidDataException("UserData exact mount delta changed.");
        }
        return new(plan, initramfs, planHash, expectedResult, expectedClose, last.Clone(), bundle!, receipt!, authority!);
    }

    private static bool ObservationHashMatches(JsonElement observation, string? expected)
    {
        if (DebianDeploymentPlanning.TextHash(observation.GetRawText()) == expected) return true;
        var native = JsonSerializer.Serialize(observation, NativeAsciiObservation);
        // The retained Python producer uses json.dumps(sort_keys=True,
        // ensure_ascii=True). .NET's default encoder changes '+' and uses
        // uppercase Unicode escapes; neither is the producer's representation.
        var ascii = new StringBuilder(native.Length);
        foreach (var rune in native.EnumerateRunes())
        {
            if (rune.Value <= 127) ascii.Append((char)rune.Value);
            else if (rune.Value <= 0xffff) ascii.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
            else
                foreach (var surrogate in char.ConvertFromUtf32(rune.Value))
                    ascii.Append("\\u").Append(((int)surrogate).ToString("x4", CultureInfo.InvariantCulture));
        }
        return DebianDeploymentPlanning.TextHash(ascii.ToString()) == expected;
    }
}
