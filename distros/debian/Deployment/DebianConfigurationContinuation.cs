using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// Only a verified completed import can be consumed. A DTO or sanitized report is
// never a capability; every retained chain member is reopened against its name/hash.
public sealed class DebianVerifiedImport
{
    private static readonly string[] UnmountOrder = ["UnmountPayload", "UnmountRoot"];
    private DebianVerifiedImport(InstallationStorageProvenanceV1 provenance, string result, string close,
        string descriptor, Guid build, Guid derivation, string manifest, string content, string transport)
    {
        Provenance = provenance; ResultSha256 = result; CloseSha256 = close; DescriptorSha256 = descriptor;
        BuildId = build; DerivationId = derivation; ManifestSha256 = manifest; ContentSha256 = content; TransportManifestSha256 = transport;
    }

    public InstallationStorageProvenanceV1 Provenance { get; }
    public string ResultSha256 { get; }
    public string CloseSha256 { get; }
    public string DescriptorSha256 { get; }
    public Guid BuildId { get; }
    public Guid DerivationId { get; }
    public string ManifestSha256 { get; }
    public string ContentSha256 { get; }
    public string TransportManifestSha256 { get; }

    public static DebianVerifiedImport Reopen(ValidatedInstallationStorage storage,
        ImmutableArray<byte[]> sessionRecords, ImmutableArray<byte[]> importRecords,
        string expectedImportSha256, string expectedCloseSha256)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.Provenance is not { Provider: "IsolatedFileBackedLab", Version: 2, Scope: "ConfiguredRootImport" } ||
            !DebianDeploymentPlanning.Hash(expectedImportSha256) || !DebianDeploymentPlanning.Hash(expectedCloseSha256))
            throw new InvalidDataException("Completed lab import authority required.");
        var sessions = Chain(sessionRecords, storage.Provenance);
        var imports = Chain(importRecords, storage.Provenance);
        var end = sessions[^1]; var imported = imports[^1];
        var importHash = DebianConfiguredRootArtifacts.Digest(importRecords[^1]);
        if (importHash != expectedImportSha256 || DebianConfiguredRootArtifacts.Digest(sessionRecords[^1]) != expectedCloseSha256 ||
            end.GetProperty("Action").GetString() != "Close" || end.GetProperty("State").GetString() != "AppliedAndVerified" ||
            imported.GetProperty("Outcome").GetString() != "AppliedAndVerified" ||
            imports[0].GetProperty("Outcome").GetString() != "IntentDurable" ||
            end.GetProperty("ImportResultReference").GetString() != $"{imports.Length - 1:D8}-{importHash}.json" ||
            end.GetProperty("SessionId").GetGuid() != imported.GetProperty("SessionId").GetGuid() ||
            end.GetProperty("PlanSha256").GetString() != imported.GetProperty("PlanSha256").GetString())
            throw new InvalidDataException("Import or close binding rejected.");
        var unmounts = sessions.Where(r => r.GetProperty("Action").GetString()!.StartsWith("Unmount", StringComparison.Ordinal) &&
            r.GetProperty("State").GetString() == "AppliedAndVerified").ToArray();
        if (!unmounts.Select(r => r.GetProperty("Action").GetString()).SequenceEqual(UnmountOrder))
            throw new InvalidDataException("Exact predecessor teardown required.");
        foreach (var record in unmounts)
        {
            var evidence = record.GetProperty("Evidence"); var path = evidence.GetProperty("Path").GetString();
            var before = evidence.GetProperty("Before").GetProperty("Mounts").EnumerateArray().ToArray();
            var removed = before.Single(m => m.GetProperty("Path").GetString() == path);
            var after = evidence.GetProperty("After").GetProperty("Mounts").EnumerateArray();
            if (!before.Where(m => m.GetProperty("Id").GetInt64() != removed.GetProperty("Id").GetInt64()).Select(m => m.GetRawText())
                .SequenceEqual(after.Select(m => m.GetRawText()), StringComparer.Ordinal))
                throw new InvalidDataException("Predecessor mount delta changed.");
        }
        var observation = imported.GetProperty("Evidence");
        if (DebianDeploymentPlanning.TextHash(observation.GetProperty("ObserverEvidence").GetRawText()) !=
            observation.GetProperty("ObserverEvidenceSha256").GetString()) throw new InvalidDataException("Predecessor observer changed.");
        var bindings = JsonSerializer.Deserialize<ImmutableArray<InstallerBlockBindingV1>>(imported.GetProperty("Bindings"));
        var indices = new[] { 2, 0, 1 };
        if (bindings.Length != 3) throw new InvalidDataException("Predecessor target binding missing.");
        for (var i = 0; i < bindings.Length; i++)
        {
            var receipt = storage.Receipts[indices[i]]; var b = bindings[i];
            var parent = receipt.Creation.Parent.Disk; var partition = receipt.Creation.Partition;
            var expected = new InstallationPartitionV1(new(parent.GptDiskGuid, parent.SizeBytes, parent.LogicalSectorSize),
                partition.PartitionGuid, partition.PartitionType, partition.OffsetBytes, partition.SizeBytes);
            if ((int)b.Role != i || b.Partition is not null || b.StoragePartition != expected ||
                b.Access != (i == 0 ? InstallerBlockAccess.ReadWrite : InstallerBlockAccess.ReadOnly) ||
                b.FileSystem != receipt.FileSystem.Type ||
                !string.Equals(b.FileSystemUuid, receipt.FileSystem.Uuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Predecessor target differs from preparation.");
        }
        return new(storage.Provenance, importHash, expectedCloseSha256, imported.GetProperty(nameof(DescriptorSha256)).GetString()!,
            imported.GetProperty(nameof(BuildId)).GetGuid(), imported.GetProperty(nameof(DerivationId)).GetGuid(),
            imported.GetProperty(nameof(ManifestSha256)).GetString()!, imported.GetProperty(nameof(ContentSha256)).GetString()!,
            imported.GetProperty(nameof(TransportManifestSha256)).GetString()!);
    }

    internal static ImmutableArray<JsonElement> Chain(ImmutableArray<byte[]> bytes, InstallationStorageProvenanceV1 provenance, int schemaVersion = 2)
    {
        if (bytes.IsDefaultOrEmpty || bytes.Length > 20000) throw new InvalidDataException("Missing or excessive predecessor chain.");
        var result = ImmutableArray.CreateBuilder<JsonElement>(); string? previous = null; string? plan = null; Guid session = default;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i].Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized predecessor record.");
            using var document = JsonDocument.Parse(bytes[i]); var r = document.RootElement;
            Unique(r);
            var p = JsonSerializer.Deserialize<InstallationStorageProvenanceV1>(r.GetProperty(nameof(Provenance)));
            plan ??= r.GetProperty("PlanSha256").GetString();
            if (i == 0) session = r.GetProperty("SessionId").GetGuid();
            if (r.GetProperty("SchemaVersion").GetInt32() != schemaVersion || p != provenance ||
                r.GetProperty("GenerationId").GetGuid() != provenance.GenerationId || r.GetProperty("Sequence").GetInt32() != i ||
                r.GetProperty("PreviousSha256").GetString() != previous || r.GetProperty("PlanSha256").GetString() != plan ||
                r.GetProperty("SessionId").GetGuid() != session || session == Guid.Empty)
                throw new InvalidDataException("Predecessor chain changed.");
            previous = DebianConfiguredRootArtifacts.Digest(bytes[i]); result.Add(r.Clone());
        }
        return result.ToImmutable();
    }

    internal static void Unique(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate predecessor field."); Unique(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Unique(item);
    }
}

public sealed record DebianCoreConfigurationPlanV1(int SchemaVersion, Guid OperationId,
    InstallationStorageProvenanceV1 Provenance, string PredecessorImportSha256, string PredecessorCloseSha256,
    string CheckpointSha256, string Hostname, string Username, uint UserId, string Locale, string Timezone, string Keyboard)
{
    public string TransformationPolicy { get; init; } = "debian-trixie-core-configuration-v5";

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DerivationSha256 { get; init; }

    internal string Fingerprint(ValidatedInstallationStorage storage, DebianVerifiedImport predecessor)
    {
        if (SchemaVersion != 1 || TransformationPolicy != "debian-trixie-core-configuration-v5" ||
            OperationId == Guid.Empty || Provenance != storage.Provenance ||
            storage.Continuation != new InstallationContinuationV1(OperationId, PredecessorImportSha256, CheckpointSha256) { DerivationSha256 = DerivationSha256 } ||
            Provenance is not { Provider: "IsolatedFileBackedLab", Version: 3, Scope: "CoreConfiguration" } ||
            Provenance.GenerationId != predecessor.Provenance.GenerationId || PredecessorImportSha256 != predecessor.ResultSha256 ||
            PredecessorCloseSha256 != predecessor.CloseSha256 || !DebianDeploymentPlanning.Hash(CheckpointSha256) ||
            Hostname != "igloo-lab-config" || Username != "iglootest" || UserId != 1000 ||
            Locale != "en_US.UTF-8" || Timezone != "Etc/UTC" || Keyboard != "us")
            throw new InvalidDataException("Unsupported configuration continuation or profile.");
        return DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new { Plan = this, Predecessor = predecessor }));
    }
}
