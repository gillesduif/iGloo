using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Core.Models;

namespace Igloo.Distro.Debian.Deployment;

public sealed record DebianUserDataEntryV1(string Source, string Destination, string Kind, long Length, string? Sha256);
public sealed record DebianUserDataSourceEntryV1(string Path, string Kind, long Length, string? Sha256);
public sealed record DebianUserDataPlanV1(int SchemaVersion, Guid GenerationId, Guid OperationId, string Scope,
    string ManifestSha256, string Username, int Uid, int Gid, string Home, FileMigrationPlan Selection,
    string CollisionPolicy, ImmutableArray<DebianUserDataEntryV1> Entries);

// Plan data is not acquisition authority. Only the explicit fixture composition is
// implemented; canonical source acquisition and production dispatch stay closed.
public static class DebianUserData
{
    public const string Scope = "SelectedDocumentTreesV1";
    private static readonly string[] Folders = ["Documents", "Downloads", "Pictures", "Desktop", "Music", "Videos"];
    private static readonly string[] SensitiveNames = [".ssh", ".gnupg", ".pki", "passwd", "shadow", "group", "gshadow", "sudoers"];
    private static string Key(string path) => path.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static bool Relative(string path) => !string.IsNullOrEmpty(path) && path.Length <= 512 &&
        path.Normalize(NormalizationForm.FormC).All(c => c < 256) &&
        path.Split('/').Length <= 16 && path.Split('/').All(p => p.Length > 0 && p is not "." and not ".." && !SensitiveNames.Contains(p, StringComparer.OrdinalIgnoreCase) &&
            !p.Any(c => char.IsControl(c) || c is '\\' or ':') && !p.EndsWith(' ') && !p.EndsWith('.'));

    public static DebianUserDataPlanV1 Plan(MigrationManifest manifest, Guid generation, Guid operation,
        int uid, int gid, ImmutableArray<DebianUserDataSourceEntryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var user = manifest.User.PreferredLinuxUsername;
        var folders = manifest.Files.Folders;
        if (manifest.SchemaVersion != 1 || manifest.DistroId != "debian" || generation == Guid.Empty || operation == Guid.Empty ||
            uid < 1000 || gid < 1000 || uid > 60000 || gid > 60000 ||
            !System.Text.RegularExpressions.Regex.IsMatch(user, "^[a-z][a-z0-9_-]{0,30}$") ||
            manifest.Browsers.Count != 0 || manifest.Wallpaper is not null || manifest.AccountPicture is not null ||
            folders.Count is < 1 or > 6 || folders.Any(f => !Folders.Contains(f.Name) || !Relative(f.SourceRelativePath)) ||
            folders.Select(f => f.Name).Distinct().Count() != folders.Count ||
            !manifest.Files.IncludedFolders.Order().SequenceEqual(folders.Select(f => f.Name).Order()) ||
            inventory.IsDefaultOrEmpty || inventory.Length > 1024)
            throw new InvalidDataException("Unsupported UserData selection or account.");
        var mapped = ImmutableArray.CreateBuilder<DebianUserDataEntryV1>();
        foreach (var item in inventory)
        {
            if (!Relative(item.Path) || item.Kind is not ("File" or "Directory") || item.Length < 0 ||
                item.Kind == "Directory" && (item.Length != 0 || item.Sha256 is not null) ||
                item.Kind == "File" && !DebianDeploymentPlanning.Hash(item.Sha256))
                throw new InvalidDataException("Unsupported selected source entry.");
            var matches = folders.Where(f => item.Path == f.SourceRelativePath || item.Path.StartsWith(f.SourceRelativePath + "/", StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("Ambiguous or unselected source entry.");
            var folder = matches[0];
            mapped.Add(new(item.Path, folder.Name + item.Path[folder.SourceRelativePath.Length..], item.Kind, item.Length, item.Sha256));
        }
        var entries = mapped.OrderBy(e => e.Destination, StringComparer.Ordinal).ToImmutableArray();
        var total = entries.Sum(e => e.Length);
        if (total is <= 0 or > 33554432 || total != manifest.Files.TotalBytes ||
            entries.Select(e => Key(e.Destination)).Distinct().Count() != entries.Length ||
            entries.Select(e => Key(e.Source)).Distinct().Count() != entries.Length ||
            folders.Any(f => !entries.Any(e => e.Source == f.SourceRelativePath && e.Kind == "Directory")) ||
            entries.Any(e => e.Destination.Contains('/', StringComparison.Ordinal) && !entries.Any(p => p.Destination == e.Destination[..e.Destination.LastIndexOf('/')] && p.Kind == "Directory")))
            throw new InvalidDataException("Incomplete or colliding UserData inventory.");
        // Reuse the shared selected-folder contract, without transmitting unrelated
        // passwords/browser/network secrets or interpreting stagingPath as authority.
        var selection = manifest.Files with { StagingPath = "" };
        return new(1, generation, operation, Scope, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest))),
            user, uid, gid, "/home/" + user, selection, "CreateNewSelectedTrees", entries);
    }

    public static byte[] Serialize(DebianUserDataPlanV1 plan) => JsonSerializer.SerializeToUtf8Bytes(plan);

    public static bool ValidateFixtureResult(DebianUserDataPlanV1 plan, byte[] receipt, byte[] evidence)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            using var r = JsonDocument.Parse(receipt);
            using var e = JsonDocument.Parse(evidence);
            var value = e.RootElement;
            return DebianFirstBootEvidence.ValidCompletionReceipt(r.RootElement, plan.GenerationId, "UserData") &&
                r.RootElement.GetProperty("EvidenceSha256").GetString() == Convert.ToHexString(SHA256.HashData(evidence)) &&
                value.GetProperty("SchemaVersion").GetInt32() == 2 &&
                value.GetProperty("Scope").GetString() == "FixtureOnlySelectedDocuments" &&
                value.GetProperty("State").GetString() == "AppliedAndVerified" &&
                value.GetProperty("GenerationId").GetGuid() == plan.GenerationId && value.GetProperty("OperationId").GetGuid() == plan.OperationId &&
                value.GetProperty("PlanSha256").GetString() == Convert.ToHexString(SHA256.HashData(Serialize(plan))) &&
                value.GetProperty("Files").GetInt32() == plan.Entries.Count(x => x.Kind == "File") &&
                value.GetProperty("Bytes").GetInt64() == plan.Entries.Sum(x => x.Length) &&
                DebianDeploymentPlanning.Hash(value.GetProperty("ObservationSha256").GetString()) &&
                DebianDeploymentPlanning.Hash(value.GetProperty("IntentSha256").GetString()) &&
                DebianDeploymentPlanning.Hash(value.GetProperty("BindingSha256").GetString());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return false; }
    }
}
