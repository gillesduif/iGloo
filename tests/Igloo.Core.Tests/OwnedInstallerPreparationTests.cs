using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class OwnedInstallerPreparationTests
{
    [Fact]
    public async Task Renders_only_after_actual_receipt_is_serialized_and_layout_reread()
    {
        using var fixture = new Fixture();
        var storage = new Storage(fixture.Claim);
        var plugin = new Plugin();
        var result = await new OwnedInstallerPreparation(storage).PrepareAsync(plugin, fixture.Request, fixture.Path);
        Assert.Equal(2, storage.Reads);
        Assert.Equal(fixture.Claim.RootPartitionGuid, plugin.Rendered!.InstallationTarget!.RootPartitionGuid);
        Assert.Equal(result.ManifestJson, await File.ReadAllTextAsync(fixture.Path));
        var wire = InstallationTargetManifest.ReadClaim(result.ManifestJson, fixture.Claim.InstallationId);
        Assert.Equal(fixture.Claim.EspPartitionGuid, wire.EspPartitionGuid);
    }

    [Theory]
    [InlineData("unpublished")]
    [InlineData("changed-layout")]
    [InlineData("wrong-root")]
    public async Task Inconsistent_preparation_cannot_invoke_renderer(string failure)
    {
        using var fixture = new Fixture();
        var storage = new Storage(fixture.Claim) { Failure = failure };
        var plugin = new Plugin();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OwnedInstallerPreparation(storage).PrepareAsync(plugin, fixture.Request, fixture.Path));
        Assert.Null(plugin.Rendered);
    }

    [Fact]
    public async Task Unsupported_boot_fails_before_partition_preparation()
    {
        using var fixture = new Fixture();
        var storage = new Storage(fixture.Claim);
        await Assert.ThrowsAsync<NotSupportedException>(() => new OwnedInstallerPreparation(storage)
            .PrepareAsync(new Plugin { UnsupportedBoot = true }, fixture.Request, fixture.Path));
        Assert.False(storage.Created);
    }

    [Fact]
    public async Task Old_manifest_cannot_create_target()
    {
        using var fixture = new Fixture();
        var node = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Path))!.AsObject();
        node.Remove("installationId");
        await File.WriteAllTextAsync(fixture.Path, node.ToJsonString());
        var storage = new Storage(fixture.Claim);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OwnedInstallerPreparation(storage)
            .PrepareAsync(new Plugin(), fixture.Request, fixture.Path));
        Assert.False(storage.Created);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task Unsupported_requirement_cannot_create_target(int requirement)
    {
        using var fixture = new Fixture();
        var storage = new Storage(fixture.Claim);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OwnedInstallerPreparation(storage)
            .PrepareAsync(new Plugin { TargetRequirement = (InstallationTargetRequirement)requirement },
                fixture.Request, fixture.Path));
        Assert.False(storage.Created);
    }

    private sealed class Storage(InstallationTargetClaim claim) : IInstallationTargetPreparer
    {
        public string? Failure { get; init; }
        public int Reads { get; private set; }
        public bool Created { get; private set; }
        public Task<GptDiskLayout> ReadLayoutAsync(Guid diskGuid, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(Failure == "changed-layout"
                ? claim.Disk with { DiskGuid = Guid.NewGuid() } : claim.Disk);
        }
        public async Task<InstallationTargetClaim> PrepareAsync(InstallationTargetRequest request,
            string manifestPath, CancellationToken ct = default)
        {
            Created = true;
            var digest = await InstallationTargetManifest.ValidatePendingAsync(manifestPath, request.InstallationId, ct);
            if (Failure != "unpublished")
                await InstallationTargetManifest.WriteClaimAsync(manifestPath, claim, digest, ct);
            return Failure == "wrong-root" ? claim with { RootPartitionGuid = Guid.NewGuid() } : claim;
        }
    }

    private sealed class Plugin : IDistroPlugin, IInstallationTargetConsumer
    {
        public string Id => "deepin";
        public InstallationTargetRequirement TargetRequirement { get; init; } = InstallationTargetRequirement.CreatedRootAndEsp;
        public bool UnsupportedBoot { get; init; }
        public MigrationManifest? Rendered { get; private set; }
        public DistroMetadata Metadata => throw new NotSupportedException();
        public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report) => [];
        public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public InstallerBootSpec GetInstallerBootSpec() => UnsupportedBoot ? throw new NotSupportedException() :
            new() { MenuTitle = "Fixture", KernelCmdline = "fixture" };
        public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default)
        {
            Rendered = manifest;
            return Task.FromResult(new InstallerConfig("fixture.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)), []));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        public InstallationTargetClaim Claim { get; }
        public InstallationTargetRequest Request { get; }
        public Fixture()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "tests/fixtures/installation-target.json")))
                directory = directory.Parent;
            var json = File.ReadAllText(System.IO.Path.Combine(directory!.FullName, "tests/fixtures/installation-target.json"));
            var node = JsonNode.Parse(json)!.AsObject();
            Claim = node["installationTarget"]!.Deserialize<InstallationTargetClaim>()!;
            var root = Claim.Disk.Partitions.Single(p => p.PartitionGuid == Claim.RootPartitionGuid);
            Request = new()
            {
                InstallationId = Claim.InstallationId, EspPartitionGuid = Claim.EspPartitionGuid,
                ExpectedLayout = Claim.Disk with { Partitions = Claim.Disk.Partitions.Where(p => p != root).ToArray() },
                RootOffsetBytes = root.OffsetBytes, RootLengthBytes = root.LengthBytes,
            };
            node.Remove("installationTarget");
            File.WriteAllText(Path, node.ToJsonString());
        }
        public void Dispose() => File.Delete(Path);
    }
}
