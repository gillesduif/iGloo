using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// A completed configuration is a different predecessor from an unmodified import.
// This readback capability alone authorizes no storage or helper operation.
public sealed class DebianVerifiedConfiguration
{
    private static readonly string[] Steps = ["Baseline", "Files", "Debconf", "Exim", "Tls", "Locale", "User", "Credential", "Sudo", "Verify"];
    private static readonly string[] UnmountOrder = ["UnmountPayload", "UnmountRoot"];
    private static readonly int[] ReceiptOrder = [2, 0, 1];
    private DebianVerifiedConfiguration(DebianCoreConfigurationPlanV1 plan, DebianVerifiedImport imported,
        string result, string close, string planHash, JsonElement observation, string targetBindings)
    {
        Plan = plan;
        Imported = imported;
        ResultSha256 = result;
        CloseSha256 = close;
        PlanSha256 = planHash;
        Observation = observation;
        TargetBindings = targetBindings;
    }

    public DebianCoreConfigurationPlanV1 Plan { get; }
    public DebianVerifiedImport Imported { get; }
    public string ResultSha256 { get; }
    public string CloseSha256 { get; }
    public string PlanSha256 { get; }
    public JsonElement Observation { get; }
    internal string TargetBindings { get; }

    public static DebianVerifiedConfiguration Reopen(ValidatedInstallationStorage storage, DebianVerifiedImport imported,
        DebianCoreConfigurationPlanV1 plan, ImmutableArray<byte[]> sessionRecords, ImmutableArray<byte[]> effectRecords,
        string expectedResultSha256, string expectedCloseSha256)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(imported);
        ArgumentNullException.ThrowIfNull(plan);
        var planHash = plan.Fingerprint(storage, imported);
        var sessions = DebianVerifiedImport.Chain(sessionRecords, storage.Provenance, 3);
        var effects = DebianVerifiedImport.Chain(effectRecords, storage.Provenance, 3);
        if (effects.Length != Steps.Length * 2 ||
            DebianConfiguredRootArtifacts.Digest(effectRecords[^1]) != expectedResultSha256 ||
            DebianConfiguredRootArtifacts.Digest(sessionRecords[^1]) != expectedCloseSha256)
            throw new InvalidDataException("Completed configuration chains required.");
        var sessionId = sessions[0].GetProperty("SessionId").GetGuid();
        var inherited = JsonSerializer.SerializeToElement(imported);
        foreach (var record in sessions.Concat(effects))
        {
            if (record.GetProperty("OperationId").GetGuid() != plan.OperationId ||
                record.GetProperty("SessionId").GetGuid() != sessionId ||
                record.GetProperty(nameof(PlanSha256)).GetString() != planHash ||
                record.GetProperty("Predecessor").GetRawText() != inherited.GetRawText())
                throw new InvalidDataException("Configured predecessor lineage changed.");
        }
        var bindings = effects[0].GetProperty("Bindings").GetRawText();
        var preserved = effects[0].GetProperty("ProtectedStateSha256").GetString();
        var targetBindings = JsonSerializer.Deserialize<ImmutableArray<InstallerBlockBindingV1>>(bindings);
        if (targetBindings.IsDefault || targetBindings.Length != 3 ||
            preserved != DebianConfiguredRootArtifacts.Digest(JsonSerializer.SerializeToUtf8Bytes(new { storage.Provenance, storage.Preserved })))
            throw new InvalidDataException("Configured predecessor target binding missing.");
        for (var i = 0; i < targetBindings.Length; i++)
        {
            var receipt = storage.Receipts[ReceiptOrder[i]];
            var partition = receipt.Creation.Partition;
            var disk = receipt.Creation.Parent.Disk;
            var expected = new InstallationPartitionV1(new(disk.GptDiskGuid, disk.SizeBytes, disk.LogicalSectorSize),
                partition.PartitionGuid, partition.PartitionType, partition.OffsetBytes, partition.SizeBytes);
            var binding = targetBindings[i];
            if ((int)binding.Role != i || binding.Partition is not null || binding.StoragePartition != expected ||
                binding.Access != (i == 0 ? InstallerBlockAccess.ReadWrite : InstallerBlockAccess.ReadOnly) ||
                binding.FileSystem != receipt.FileSystem.Type ||
                !string.Equals(binding.FileSystemUuid, receipt.FileSystem.Uuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Configured predecessor target differs from preparation lineage.");
        }
        for (var i = 0; i < effects.Length; i++)
        {
            var record = effects[i];
            if (record.GetProperty("Step").GetString() != Steps[i / 2] ||
                record.GetProperty("Outcome").GetString() != (i % 2 == 0 ? "IntentDurable" : "AppliedAndVerified") ||
                record.GetProperty("Bindings").GetRawText() != bindings ||
                record.GetProperty("ProtectedStateSha256").GetString() != preserved)
                throw new InvalidDataException("Configuration is incomplete or has inconsistent target evidence.");
            if (i % 2 == 0)
                continue;
            var evidence = record.GetProperty("Evidence");
            var observed = evidence.GetProperty("ObserverEvidence");
            if (DebianDeploymentPlanning.TextHash(observed.GetRawText()) != evidence.GetProperty("ObserverEvidenceSha256").GetString() ||
                observed.GetProperty("OperationId").GetGuid() != plan.OperationId ||
                observed.GetProperty(nameof(PlanSha256)).GetString() != planHash)
                throw new InvalidDataException("Independent configuration observation changed.");
        }
        var close = sessions[^1];
        if (close.GetProperty("Action").GetString() != "Close" || close.GetProperty("State").GetString() != "AppliedAndVerified" ||
            close.GetProperty("ConfigurationResultReference").GetString() != $"{effects.Length - 1:D8}-{expectedResultSha256}.json")
            throw new InvalidDataException("Configuration handoff or close missing.");
        var unmounts = sessions.Where(r => r.GetProperty("Action").GetString()!.StartsWith("Unmount", StringComparison.Ordinal) &&
            r.GetProperty("State").GetString() == "AppliedAndVerified").ToArray();
        if (!unmounts.Select(r => r.GetProperty("Action").GetString()).SequenceEqual(UnmountOrder))
            throw new InvalidDataException("Configured predecessor exact teardown missing.");
        foreach (var record in unmounts)
        {
            var e = record.GetProperty("Evidence");
            var path = e.GetProperty("Path").GetString();
            var before = e.GetProperty("Before").GetProperty("Mounts").EnumerateArray().ToArray();
            var removed = before.Where(m => m.GetProperty("Path").GetString() == path).ToArray();
            if (removed.Length != 1 || !before.Where(m => m.GetProperty("Id").GetInt64() != removed[0].GetProperty("Id").GetInt64())
                .Select(m => m.GetRawText()).SequenceEqual(e.GetProperty("After").GetProperty("Mounts").EnumerateArray().Select(m => m.GetRawText())))
                throw new InvalidDataException("Configured predecessor mount delta changed.");
        }
        var final = effects[^1].GetProperty("Evidence").GetProperty("ObserverEvidence");
        if (final.GetProperty("ProfileStep").GetString() != "Verify" || final.GetProperty("MachineIdentity").GetString() != "FirstBootPending" ||
            !DebianDeploymentPlanning.Hash(final.GetProperty("FilesystemDeltaSha256").GetString()!) ||
            !DebianDeploymentPlanning.Hash(final.GetProperty("PackageStateSha256").GetString()!))
            throw new InvalidDataException("Configured-state result missing.");
        return new(plan, imported, expectedResultSha256, expectedCloseSha256, planHash, final.Clone(), bindings);
    }
}
