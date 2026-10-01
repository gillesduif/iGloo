using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Preparation;

namespace Igloo.Distro.Debian.Deployment;

// Wire data only. Neither deserialization nor structural receipt validation is
// a storage capability, a session authorization or production readiness.
public sealed record DebianUserDataAccountV1(string Username, int Uid, int Gid, string Home);
public sealed record DebianUserDataCanonicalDeclarationV1(int SchemaVersion, string Scope,
    InstallationStorageProvenanceV1 Provenance, Guid SessionId, string PlanSha256, string TransferSha256,
    string PredecessorSha256, string PredecessorCloseSha256, string DeliverySha256, string SourcePrefix,
    DebianUserDataAccountV1 Account, ImmutableArray<InstallerBlockBindingV1> Bindings);

public static class DebianUserDataAdmission
{
    public const string Scope = "CanonicalLabSelectedDocumentsV1";
    public const string InputDirectory = "/var/lib/igloo/first-boot-input";
    public const string ReceiptName = "userdata.json";
    public const string EvidenceName = "userdata-evidence.json";
    private static readonly string[] ContextFields = ["Accounts", "AuthoritySha256", "DestinationBeforeSha256",
        "HomeIdentity", "SourceBeforeSha256", "SourceIdentity", "TargetIdentity"];
    private static readonly string[] ObservationFlags = ["SourceUnchanged", "UnrelatedDestinationUnchanged", "ContentAndOwnershipVerified"];
    private static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));

    public static byte[] Serialize(DebianUserDataCanonicalDeclarationV1 declaration) => JsonSerializer.SerializeToUtf8Bytes(declaration);

    public static bool ValidDeclaration(DebianUserDataCanonicalDeclarationV1 declaration, DebianUserDataPlanV1 transfer)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(transfer);
        if (declaration.SchemaVersion != 1 || declaration.Scope != Scope ||
            declaration.Provenance is not { Provider: "IsolatedFileBackedLab", Version: 5, Scope: "SelectedDocumentTrees" } ||
            declaration.Provenance.GenerationId != transfer.GenerationId || declaration.Provenance.RunId == Guid.Empty ||
            declaration.SessionId == Guid.Empty || transfer.GenerationId == Guid.Empty || transfer.OperationId == Guid.Empty ||
            transfer.Scope != DebianUserData.Scope || declaration.TransferSha256 != Hash(DebianUserData.Serialize(transfer)) ||
            declaration.SourcePrefix != $"userdata/{transfer.OperationId:D}/source" ||
            declaration.Account != new DebianUserDataAccountV1(transfer.Username, transfer.Uid, transfer.Gid, transfer.Home) ||
            new[] { declaration.PlanSha256, declaration.PredecessorSha256, declaration.PredecessorCloseSha256,
                declaration.DeliverySha256, declaration.Provenance.EvidenceSha256 }.Any(h => !DebianDeploymentPlanning.Hash(h)) ||
            declaration.Bindings.IsDefault || declaration.Bindings.Length != 3)
            return false;
        for (var i = 0; i < 3; i++)
        {
            var b = declaration.Bindings[i];
            if ((int)b.Role != i || b.Access != (i == 0 ? InstallerBlockAccess.ReadWrite : InstallerBlockAccess.ReadOnly) ||
                b.Partition is not null || b.StoragePartition is null || b.FileSystem != (i == 0 ? "EXT4" : "FAT32") ||
                !System.Text.RegularExpressions.Regex.IsMatch(b.FileSystemUuid ?? "", i == 0
                    ? "^[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}$" : "^[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}$"))
                return false;
        }
        return declaration.Bindings.Select(b => b.FileSystemUuid).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3 &&
            declaration.Bindings.Select(b => b.StoragePartition).Distinct().Count() == 3;
    }

    // A lab-only consumer for independently reopened admission bytes. The caller
    // must still establish protected placement and the live validated context.
    // It deliberately cannot emit DeploymentContent, Enrollment or FirstBootSucceeded.
    public static bool ValidateLabAdmissionStructure(DebianUserDataPlanV1 plan,
        DebianUserDataCanonicalDeclarationV1 expected, byte[] receipt, byte[] evidence)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(evidence);
        try
        {
            if (!ValidDeclaration(expected, plan) || evidence.Length is <= 0 or > 1048576 || receipt.Length is <= 0 or > 1048576)
                return false;
            using var bundleDocument = JsonDocument.Parse(evidence);
            using var receiptDocument = JsonDocument.Parse(receipt);
            var bundle = bundleDocument.RootElement;
            DebianVerifiedImport.Unique(bundle);
            DebianVerifiedImport.Unique(receiptDocument.RootElement);
            var authority = bundle.GetProperty("Authority").GetBytesFromBase64();
            var transfer = bundle.GetProperty("Transfer").GetBytesFromBase64();
            if (bundle.EnumerateObject().Count() != 5 || bundle.GetProperty("SchemaVersion").GetInt32() != 1 ||
                bundle.GetProperty("Scope").GetString() != Scope || !authority.AsSpan().SequenceEqual(Serialize(expected)) ||
                !transfer.AsSpan().SequenceEqual(DebianUserData.Serialize(plan))) return false;
            var records = bundle.GetProperty("Records").EnumerateArray().Select(r => r.GetBytesFromBase64()).ToArray();
            if (records.Length != 3 || records.Any(r => r.Length is <= 0 or > 1048576)) return false;
            var parsed = records.Select(r => JsonSerializer.Deserialize<JsonElement>(r)).ToArray();
            foreach (var record in parsed) DebianVerifiedImport.Unique(record);
            var names = records.Select((r, i) => $"{i:D8}-{Hash(r)}.json").ToArray();
            string[] states = ["IntentDurable", "IndependentObservation", "AppliedAndVerified"];
            for (var i = 0; i < 3; i++)
            {
                var r = parsed[i];
                if (r.GetProperty("SchemaVersion").GetInt32() != 2 || r.GetProperty("Scope").GetString() != Scope ||
                    r.GetProperty("GenerationId").GetGuid() != plan.GenerationId || r.GetProperty("OperationId").GetGuid() != plan.OperationId ||
                    r.GetProperty("State").GetString() != states[i] || r.GetProperty("PlanSha256").GetString() != Hash(transfer) ||
                    r.GetProperty("AuthoritySha256").GetString() != Hash(authority) || i > 0 &&
                    (r.GetProperty("IntentReference").GetString() != names[0] || r.GetProperty("IntentSha256").GetString() != Hash(records[0])))
                    return false;
            }
            // These exact values are already encoded canonically by the native
            // producer. Keep nested raw values; reserializing can alter Unicode.
            var context = "{" + string.Join(',', ContextFields.Select(k => JsonSerializer.Serialize(k) + ":" + parsed[0].GetProperty(k).GetRawText())) + "}";
            var binding = DebianDeploymentPlanning.TextHash(context);
            var observation = parsed[1].GetProperty("Observation");
            var result = parsed[2];
            if (result.GetProperty("BindingSha256").GetString() != binding || observation.GetProperty("BindingSha256").GetString() != binding ||
                result.GetProperty("ObservationReference").GetString() != names[1] || result.GetProperty("ObservationSha256").GetString() != Hash(records[1]) ||
                observation.GetProperty("GenerationId").GetGuid() != plan.GenerationId || observation.GetProperty("OperationId").GetGuid() != plan.OperationId ||
                observation.GetProperty("PlanSha256").GetString() != Hash(transfer) ||
                new[] { result, observation }.Any(r => r.GetProperty("Files").GetInt32() != plan.Entries.Count(e => e.Kind == "File") ||
                    r.GetProperty("Bytes").GetInt64() != plan.Entries.Sum(e => e.Length)) ||
                ObservationFlags.Any(k => !observation.GetProperty(k).GetBoolean()))
                return false;
            return DebianFirstBootEvidence.ValidCompletionReceipt(receiptDocument.RootElement, plan.GenerationId, "UserData") &&
                receiptDocument.RootElement.GetProperty("EvidenceSha256").GetString() == Hash(records[2]);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or InvalidDataException)
        { return false; }
    }
}
