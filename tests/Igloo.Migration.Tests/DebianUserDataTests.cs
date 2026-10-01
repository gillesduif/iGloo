using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Core.Models;
using Igloo.Distro.Debian.Deployment;
using Xunit;
namespace Igloo.Migration.Tests;

public sealed class DebianUserDataTests
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static (MigrationManifest, ImmutableArray<DebianUserDataSourceEntryV1>, Dictionary<string, byte[]>) Fixture()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["OneDrive/Documents/nested/hello world.txt"] = Encoding.UTF8.GetBytes("Non-personal document fixture\n"),
            ["OneDrive/Documents/empty.txt"] = [],
            ["OneDrive/Documents/résumé.txt"] = Encoding.UTF8.GetBytes("Synthetic Unicode contents\n"),
            ["OneDrive/Documents/nested/binary.bin"] = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()
        };
        var inventory = new[] { "OneDrive/Documents", "OneDrive/Documents/nested" }.Select(p => new DebianUserDataSourceEntryV1(p, "Directory", 0, null))
            .Concat(files.Select(f => new DebianUserDataSourceEntryV1(f.Key, "File", f.Value.Length, Hash(f.Value)))).ToImmutableArray();
        var manifest = new MigrationManifest
        {
            GeneratedAtUtc = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
            DistroId = "debian",
            User = new() { WindowsUsername = "fixture-source", PreferredLinuxUsername = "iglootest" },
            Hardware = new(),
            Files = new()
            {
                StagingPath = "fixture-only-not-authority",
                TotalBytes = files.Sum(f => f.Value.Length),
                IncludedFolders = ["Documents"],
                Folders = [new() { Name = "Documents", SourceRelativePath = "OneDrive/Documents" }]
            }
        };
        return (manifest, inventory, files);
    }
    [Fact]
    public void SharedSelectionSerializerAndFixtureResultConsumer()
    {
        var (manifest, inventory, files) = Fixture();
        var operation = Guid.Parse(Environment.GetEnvironmentVariable("IGLOO_USERDATA_FIXTURE_OPERATION") ?? "0f6ddccd-0277-4403-8ac9-401470607ace");
        var plan = DebianUserData.Plan(manifest, Guid.Parse("3ea4aa3e-3336-4574-a7fb-963bd649df27"), operation, 1000, 1000, inventory);
        var raw = DebianUserData.Serialize(plan);
        Assert.All(plan.Entries, e => Assert.StartsWith("Documents", e.Destination, StringComparison.Ordinal));
        Assert.Contains(plan.Entries, e => e.Destination == "Documents/résumé.txt");
        using var document = JsonDocument.Parse(raw);
        Assert.Equal("OneDrive/Documents", document.RootElement.GetProperty("Selection").GetProperty("folders")[0].GetProperty("sourceRelativePath").GetString());
        var export = Environment.GetEnvironmentVariable("IGLOO_USERDATA_WIRE_FIXTURE");
        if (!string.IsNullOrEmpty(export))
        {
            Directory.CreateDirectory(export);
            File.WriteAllBytes(Path.Combine(export, "plan.json"), raw);
            File.WriteAllBytes(Path.Combine(export, "data.json"), JsonSerializer.SerializeToUtf8Bytes(files.ToDictionary(f => f.Key, f => Convert.ToBase64String(f.Value))));
        }
        var evidence = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 2,
            Scope = "FixtureOnlySelectedDocuments",
            State = "AppliedAndVerified",
            plan.GenerationId,
            plan.OperationId,
            PlanSha256 = Hash(raw),
            Files = files.Count,
            Bytes = files.Sum(f => f.Value.Length),
            ObservationSha256 = new string('A', 64),
            IntentSha256 = new string('B', 64),
            BindingSha256 = new string('C', 64)
        });
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, plan.GenerationId, Kind = "UserData", State = "AppliedAndVerified", EvidenceSha256 = Hash(evidence) });
        Assert.True(DebianUserData.ValidateFixtureResult(plan, receipt, evidence));
        Assert.False(DebianUserData.ValidateFixtureResult(plan with { GenerationId = Guid.NewGuid() }, receipt, evidence));
        var older = System.Text.Json.Nodes.JsonNode.Parse(evidence)!;
        older["SchemaVersion"] = 1;
        var olderBytes = Encoding.UTF8.GetBytes(older.ToJsonString());
        var olderReceipt = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, plan.GenerationId, Kind = "UserData", State = "AppliedAndVerified", EvidenceSha256 = Hash(olderBytes) });
        Assert.False(DebianUserData.ValidateFixtureResult(plan, olderReceipt, olderBytes));
        var native = Environment.GetEnvironmentVariable("IGLOO_USERDATA_NATIVE_RESULT");
        if (!string.IsNullOrEmpty(native))
            Assert.True(DebianUserData.ValidateFixtureResult(plan, File.ReadAllBytes(Path.Combine(native, "receipt.json")), File.ReadAllBytes(Path.Combine(native, "result.json"))));
    }
    [Theory]
    [InlineData("browser")]
    [InlineData("escape")]
    [InlineData("collision")]
    [InlineData("missing-directory")]
    [InlineData("bytes")]
    [InlineData("type")]
    [InlineData("selection")]
    public void UnsupportedRequiredWorkAndAmbiguousMappingsReject(string change)
    {
        var (m, entries, _) = Fixture();
        if (change == "browser")
            m = m with { Browsers = [new() { Name = "required-browser" }] };
        if (change == "escape")
            entries = entries.SetItem(2, entries[2] with { Path = "../escape" });
        if (change == "collision")
            entries = entries.Add(entries[2] with { Path = entries[2].Path.ToUpperInvariant() });
        if (change == "missing-directory")
            entries = entries.RemoveAt(1);
        if (change == "bytes")
            m = m with { Files = m.Files with { TotalBytes = 1 } };
        if (change == "type")
            entries = entries.SetItem(2, entries[2] with { Kind = "Link" });
        if (change == "selection")
            m = m with { Files = m.Files with { IncludedFolders = ["Desktop"] } };
        Assert.Throws<InvalidDataException>(() => DebianUserData.Plan(m, Guid.NewGuid(), Guid.NewGuid(), 1000, 1000, entries));
    }
}
