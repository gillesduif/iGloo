using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class DebianUserDataAdmissionTests
{
    private static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));

    [Fact]
    public void ActualDeclarationSerializerPreservesCanonicalNumericLeaseContract()
    {
        var (manifest, entries, files) = DebianUserDataTests.Fixture();
        var name = "OneDrive/Documents/nested/binary.bin";
        files[name] = Enumerable.Range(0, 131073).Select(i => (byte)(i % 251)).ToArray();
        entries = entries.Select(e => e.Path == name ? e with { Length = files[name].Length, Sha256 = Hash(files[name]) } : e).ToImmutableArray();
        manifest = manifest with { Files = manifest.Files with { TotalBytes = files.Values.Sum(b => b.Length) } };
        var evidence = LabStorageFixture.Create(true);
        var storage = LabStorageFixture.Validate(evidence);
        var inventory = LinuxInstallerInventoryProtocol.Parse(evidence.Transitions[^1].Formatted);
        var session = Guid.NewGuid();
        var leases = InstallerBlockLeases.Acquire(storage, session, DateTimeOffset.UtcNow, inventory,
            Observations.Available(LabStorageFixture.Numbers(inventory.Value)), Observations.Available(evidence.GuestDisks)).Value;
        var generation = Guid.Parse(Environment.GetEnvironmentVariable("IGLOO_USERDATA_DECLARATION_GENERATION") ?? evidence.GenerationId.ToString("D"));
        var operation = Guid.Parse(Environment.GetEnvironmentVariable("IGLOO_USERDATA_DECLARATION_OPERATION") ?? Guid.NewGuid().ToString("D"));
        var plan = DebianUserData.Plan(manifest, generation, operation, 1000, 1000, entries);
        var bytes = DebianUserData.Serialize(plan);
        // Explicit synthetic declaration, not a version-5 storage capability.
        // Canonical dispatch must create it from its verified live context.
        var declaration = new DebianUserDataCanonicalDeclarationV1(1, DebianUserDataAdmission.Scope,
            new("IsolatedFileBackedLab", 5, "SelectedDocumentTrees", Guid.NewGuid(), plan.GenerationId, new('A', 64)), session,
            new('B', 64), Hash(bytes), new('C', 64), new('D', 64), new('E', 64), $"userdata/{plan.OperationId:D}/source",
            new(plan.Username, plan.Uid, plan.Gid, plan.Home), leases.Bindings);
        Assert.True(DebianUserDataAdmission.ValidDeclaration(declaration, plan));
        Assert.False(DebianUserDataAdmission.ValidDeclaration(declaration with { Scope = "FixtureOnlySelectedDocuments" }, plan));
        Assert.False(DebianUserDataAdmission.ValidDeclaration(declaration with { SourcePrefix = "runtime/source" }, plan));
        Assert.False(DebianUserDataAdmission.ValidDeclaration(declaration with { Account = declaration.Account with { Uid = 0 } }, plan));
        Assert.False(DebianUserDataAdmission.ValidDeclaration(declaration with { Provenance = declaration.Provenance with { Version = 4 } }, plan));
        Assert.False(DebianUserDataAdmission.ValidDeclaration(declaration with { Bindings = declaration.Bindings.SetItem(0,
            declaration.Bindings[0] with { Access = InstallerBlockAccess.ReadOnly }) }, plan));
        Assert.False(DebianUserDataAdmission.ValidDeclaration(declaration with { Bindings = declaration.Bindings.SetItem(1, declaration.Bindings[0]) }, plan));
        var export = Environment.GetEnvironmentVariable("IGLOO_USERDATA_ADMISSION_EXPORT");
        if (string.IsNullOrEmpty(export)) return;
        Directory.CreateDirectory(export);
        File.WriteAllBytes(Path.Combine(export, "plan.json"), bytes);
        File.WriteAllBytes(Path.Combine(export, "declaration.json"), DebianUserDataAdmission.Serialize(declaration));
        File.WriteAllBytes(Path.Combine(export, "data.json"), JsonSerializer.SerializeToUtf8Bytes(files.ToDictionary(f => f.Key, f => Convert.ToBase64String(f.Value))));
    }

    [Fact]
    public void PythonTerminalAndAdmissionReturnToRealDotNetConsumer()
    {
        var input = Environment.GetEnvironmentVariable("IGLOO_USERDATA_ADMISSION_RETURN");
        if (string.IsNullOrEmpty(input)) return; // External cross-language validation is reported separately.
        var plan = JsonSerializer.Deserialize<DebianUserDataPlanV1>(File.ReadAllBytes(Path.Combine(input, "plan.json")))!;
        var declaration = JsonSerializer.Deserialize<DebianUserDataCanonicalDeclarationV1>(File.ReadAllBytes(Path.Combine(input, "declaration.json")))!;
        var receipt = File.ReadAllBytes(Path.Combine(input, "receipt.json"));
        var bundle = File.ReadAllBytes(Path.Combine(input, "bundle.json"));
        Assert.True(DebianUserDataAdmission.ValidateLabAdmissionStructure(plan, declaration, receipt, bundle));
        Assert.False(DebianUserDataAdmission.ValidateLabAdmissionStructure(plan, declaration with { SessionId = Guid.NewGuid() }, receipt, bundle));
        Assert.False(DebianUserDataAdmission.ValidateLabAdmissionStructure(plan, declaration with { PredecessorSha256 = new('F', 64) }, receipt, bundle));
        Assert.False(DebianUserDataAdmission.ValidateLabAdmissionStructure(plan, declaration with { SourcePrefix = "other" }, receipt, bundle));
        foreach (var file in Directory.EnumerateFiles(input, "rejected-*.json"))
            Assert.False(DebianUserDataAdmission.ValidateLabAdmissionStructure(plan, declaration, receipt, File.ReadAllBytes(file)));
        using var document = JsonDocument.Parse(bundle);
        var result = document.RootElement.GetProperty("Records")[2].GetBytesFromBase64();
        Assert.False(DebianUserData.ValidateFixtureResult(plan, receipt, result));
    }
}
