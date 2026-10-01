using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class DebianConfigurationContinuationTests
{
    [Theory]
    [InlineData("GenerationInputs", true)]
    [InlineData("Publication", true)]
    [InlineData("Handoff", true)]
    [InlineData("private arbitrary exception details", false)]
    public void InitramfsStoppedLocationIsClosedAndSecretSafe(string location, bool accepted)
    {
        var message = JsonSerializer.SerializeToElement(new { Location = location });
        if (accepted) Assert.Equal(location, DebianMountSessionAuthority.InitramfsFailureLocation(message));
        else Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.InitramfsFailureLocation(message));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("failed")]
    [InlineData("missing-step")]
    [InlineData("observer")]
    [InlineData("close")]
    [InlineData("operation")]
    [InlineData("teardown")]
    public void ConfiguredSuccessorRequiresV5ObservationAndSeparateCompletedChains(string mutation)
    {
        var f = Predecessor("valid");
        var prior = DebianVerifiedImport.Reopen(f.Storage, f.Session, f.Import, f.ImportHash, f.CloseHash);
        var e = f.Evidence;
        var operation = Guid.NewGuid();
        var storage = InstallationStorage.ContinueLabSameTarget(f.Storage, Guid.NewGuid(), operation, f.ImportHash,
            new('C', 64), new('D', 64), e.CreatedBackings, e.ReopenedBackings,
            LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted), Observations.Available(e.GuestDisks)).Value;
        var plan = new DebianCoreConfigurationPlanV1(1, operation, storage.Provenance, f.ImportHash, f.CloseHash,
            new('C', 64), "igloo-lab-config", "iglootest", 1000, "en_US.UTF-8", "Etc/UTC", "us");
        var authority = DebianMountSessionAuthority.ForLabConfiguration(storage, prior, plan,
            new DebianDeploymentFixture.Journal(), new DebianDeploymentFixture.Journal(),
            new(prior.BuildId, prior.DescriptorSha256, new('B', 64), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)));
        var effects = ImmutableArray.CreateBuilder<byte[]>();
        var sessions = ImmutableArray.CreateBuilder<byte[]>();
        var inventory = LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted);
        var leases = InstallerBlockLeases.Acquire(storage, authority.SessionId, DateTimeOffset.UtcNow, inventory,
            Observations.Available(LabStorageFixture.Numbers(inventory.Value)), Observations.Available(e.GuestDisks)).Value;
        var bindings = leases.Bindings;
        void Add(ImmutableArray<byte[]>.Builder chain, Dictionary<string, object?> fields)
        {
            fields["SchemaVersion"] = 3; fields["GenerationId"] = storage.Provenance.GenerationId;
            fields["SessionId"] = authority.SessionId; fields["PlanSha256"] = authority.PlanSha256;
            fields["Provenance"] = storage.Provenance; fields["Predecessor"] = prior;
            fields["OperationId"] = mutation == "operation" ? Guid.NewGuid() : operation;
            fields["Sequence"] = chain.Count; fields["PreviousSha256"] = chain.Count == 0 ? null : Hash(chain[^1]);
            chain.Add(JsonSerializer.SerializeToUtf8Bytes(fields));
        }
        foreach (var step in new[] { "Baseline", "Files", "Debconf", "Exim", "Tls", "Locale", "User", "Credential", "Sudo", "Verify" })
        {
            if (mutation == "missing-step" && step == "Tls") continue;
            var observation = JsonSerializer.SerializeToElement(new { OperationId = operation, authority.PlanSha256,
                ProfileStep = step, MachineIdentity = "FirstBootPending", FilesystemDeltaSha256 = new string('D', 64),
                PackageStateSha256 = new string('E', 64) });
            foreach (var outcome in new[] { "IntentDurable", mutation == "failed" ? "OutcomeUnknown" : "AppliedAndVerified" })
                Add(effects, new() { ["Step"] = step, ["Outcome"] = outcome, ["Bindings"] = bindings,
                    ["ProtectedStateSha256"] = leases.ProtectedStateSha256, ["Evidence"] = new { ObserverEvidence = observation,
                        ObserverEvidenceSha256 = mutation == "observer" ? new string('F', 64) : Hash(JsonSerializer.SerializeToUtf8Bytes(observation)) } });
        }
        var rootMount = new { Id = 1, Path = "/root-fixture" }; var payloadMount = new { Id = 2, Path = "/payload-fixture" };
        if (mutation != "teardown")
            Add(sessions, new() { ["Action"] = "UnmountPayload", ["State"] = "AppliedAndVerified", ["Evidence"] = new {
                Path = payloadMount.Path, Before = new { Mounts = new[] { rootMount, payloadMount } }, After = new { Mounts = new[] { rootMount } } } });
        Add(sessions, new() { ["Action"] = "UnmountRoot", ["State"] = "AppliedAndVerified", ["Evidence"] = new {
            Path = rootMount.Path, Before = new { Mounts = new[] { rootMount } }, After = new { Mounts = Array.Empty<object>() } } });
        Add(sessions, new() { ["Action"] = "Close", ["State"] = mutation == "close" ? "OutcomeUnknown" : "AppliedAndVerified",
            ["ConfigurationResultReference"] = $"{effects.Count - 1:D8}-{Hash(effects[^1])}.json" });
        DebianVerifiedConfiguration Reopen() => DebianVerifiedConfiguration.Reopen(storage, prior, plan,
            sessions.ToImmutable(), effects.ToImmutable(), Hash(effects[^1]), Hash(sessions[^1]));
        if (mutation == "valid")
        {
            var verified = Reopen();
            Assert.Equal(prior.ResultSha256, verified.Imported.ResultSha256);
            Assert.Equal(plan, verified.Plan);
            Assert.Equal("Verify", verified.Observation.GetProperty("ProfileStep").GetString());
            var auth = new InstallerLabInitramfsAuthorizationV1(1, "OneConfiguredCheckpointInitramfs", Guid.NewGuid(), Guid.NewGuid(),
                e.GenerationId, verified.ResultSha256, verified.CloseSha256, new('A',64), new('B',64), new('C',64));
            var copies = e.CreatedBackings.Select((b,i) => new InstallerLabCopyV1(b.Serial, b.HostDevice, b.HostInode,
                b.HostDevice, b.HostInode + 1000, b.Length, i == 0 ? auth.TargetSha256 : auth.JournalSha256,
                i == 0 ? auth.TargetSha256 : auth.JournalSha256)).ToImmutableArray();
            var backings = e.CreatedBackings.Select((b,i) => b with { HostInode = copies[i].DestinationInode }).ToImmutableArray();
            var next = InstallationStorage.ContinueLabConfiguredCheckpoint(storage,
                new(auth, new('D',64), copies), new('D',64), new('E',64), backings, backings, inventory,
                Observations.Available(e.GuestDisks)).Value;
            var candidate = JsonSerializer.SerializeToElement(new { Policy = DebianLabInitramfsPlanV1.Policy,
                KernelRelease = DebianLabInitramfsPlanV1.KernelRelease, ConfigurationResultSha256 = verified.ResultSha256,
                RootUuid = next.Receipts[2].FileSystem.Uuid, Output = DebianLabInitramfsPlanV1.Destination, Modules = "most",
                Resume = "none", Compression = "gzip", CpuVendor = "AuthenticAMD", StorageTopology = "VirtioBlockExt4",
                InputClosureSha256 = new string('A',64), HookSetSha256 = new string('B',64) });
            var imagePlan = new DebianLabInitramfsPlanV1(1, auth.OperationId, next.Provenance, verified.ResultSha256,
                verified.CloseSha256, auth.RetentionSha256, next.Continuation!.DerivationSha256!, new('D',64), candidate, new('F',64));
            Assert.Equal(64, imagePlan.Fingerprint(next, verified).Length);
            Assert.Throws<InvalidDataException>(() => (imagePlan with { ConfigurationSha256 = new('0',64) }).Fingerprint(next, verified));
            Assert.Throws<InvalidDataException>(() => (imagePlan with { OperationId = Guid.NewGuid() }).Fingerprint(next, verified));
            Assert.Throws<InvalidDataException>(() => imagePlan.Fingerprint(storage, verified));
            Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.ForLabConfiguration(next, prior, plan,
                new DebianDeploymentFixture.Journal(), new DebianDeploymentFixture.Journal(),
                new(prior.BuildId, prior.DescriptorSha256, new('B',64), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1))));
            Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.ForLabStorageSmoke(next, new DebianDeploymentFixture.Journal()));
            var imageAuthority = DebianMountSessionAuthority.ForLabInitramfs(next, verified, imagePlan,
                new DebianDeploymentFixture.Journal(), new DebianDeploymentFixture.Journal(),
                new(prior.BuildId, prior.DescriptorSha256, new('B',64), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)));
            Assert.Null(imageAuthority.ConfigurationPlan);
            Assert.Null(imageAuthority.SourcePlan);
            Assert.NotNull(imageAuthority.InitramfsPlan);
            // The production serializers feed the cross-language adapter fixture.
            // These are synthetic validated storage facts, never native authority.
            var toolHashes = ImmutableSortedDictionary<string, string>.Empty.Add("/fixture/tool", new('F',64));
            var runtime = new DebianSessionRuntimeV1("/fixture/tool", "/fixture/tool", "/fixture/tool", "/fixture/tool",
                "/fixture/tool", "/fixture/tool", toolHashes);
            var wirePlan = imagePlan with { ExecutionSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(toolHashes)) };
            var wireAuthority = DebianMountSessionAuthority.ForLabInitramfs(next, verified, wirePlan,
                new DebianDeploymentFixture.Journal(), new DebianDeploymentFixture.Journal(),
                new(prior.BuildId, prior.DescriptorSha256, new('B',64), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)));
            var wireLeases = InstallerBlockLeases.Acquire(next, wireAuthority.SessionId, DateTimeOffset.UtcNow, inventory,
                Observations.Available(LabStorageFixture.Numbers(inventory.Value)), Observations.Available(e.GuestDisks)).Value;
            DebianInitramfsPredecessorTests.Check(next, verified, wirePlan, wireLeases, e);
            var requestBytes = DebianNativeMountSession.SerializeRequest(wireAuthority, runtime, 12345);
            var responseBytes = JsonSerializer.Serialize(DebianMountSessionAuthority.LeaseResponse(Guid.NewGuid(), wireLeases));
            using var response = JsonDocument.Parse(responseBytes);
            var serializedBindings = response.RootElement.GetProperty("Leases").GetProperty("Bindings");
            Assert.Equal(new[] { 0, 1, 2 }, serializedBindings.EnumerateArray().Select(b => b.GetProperty("Role").GetInt32()));
            Assert.Equal(new[] { 1, 0, 0 }, serializedBindings.EnumerateArray().Select(b => b.GetProperty("Access").GetInt32()));
            Assert.Equal(candidate.GetProperty("RootUuid").GetString()!.ToUpperInvariant(), serializedBindings[0].GetProperty("FileSystemUuid").GetString());
            var export = Environment.GetEnvironmentVariable("IGLOO_INITRAMFS_WIRE_FIXTURE");
            if (!string.IsNullOrEmpty(export))
            {
                Directory.CreateDirectory(export);
                File.WriteAllText(Path.Combine(export, "request.json"), requestBytes);
                File.WriteAllText(Path.Combine(export, "lease-response.json"), responseBytes);
            }

        }
        else Assert.Throws<InvalidDataException>(Reopen);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt")]
    [InlineData("failed")]
    [InlineData("teardown")]
    [InlineData("target")]
    [InlineData("missing")]
    [InlineData("generation")]
    [InlineData("geometry")]
    [InlineData("access")]
    public void CompletedPredecessorRequiresBothChainsAndExactTeardown(string mutation)
    {
        var fixture = Predecessor(mutation);
        if (mutation == "valid")
        {
            var verified = DebianVerifiedImport.Reopen(fixture.Storage, fixture.Session, fixture.Import, fixture.ImportHash, fixture.CloseHash);
            Assert.Equal(fixture.Storage.Provenance, verified.Provenance);
            Assert.Equal(fixture.ImportHash, verified.ResultSha256);
        }
        else Assert.Throws<InvalidDataException>(() => DebianVerifiedImport.Reopen(fixture.Storage, fixture.Session, fixture.Import,
            mutation == "corrupt" ? new('F', 64) : fixture.ImportHash, fixture.CloseHash));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("retag")]
    [InlineData("plan")]
    [InlineData("pin")]
    [InlineData("expired")]
    [InlineData("operation")]
    [InlineData("shared-store")]
    [InlineData("transformations")]
    [InlineData("v3-transformations")]
    [InlineData("v4-transformations")]
    public async Task SuccessorIsSeparateAndCannotImportOrBecomeProduction(string mutation)
    {
        var f = Predecessor("valid");
        var prior = DebianVerifiedImport.Reopen(f.Storage, f.Session, f.Import, f.ImportHash, f.CloseHash);
        var e = f.Evidence;
        var operation = Guid.NewGuid();
        var storage = InstallationStorage.ContinueLabSameTarget(f.Storage, Guid.NewGuid(), operation, f.ImportHash,
            new('C', 64), new('D', 64), e.CreatedBackings, e.ReopenedBackings,
            LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted), Observations.Available(e.GuestDisks)).Value;
        var plan = new DebianCoreConfigurationPlanV1(1, operation, storage.Provenance, f.ImportHash, f.CloseHash,
            new('C', 64), "igloo-lab-config", "iglootest", 1000, "en_US.UTF-8", "Etc/UTC", "us");
        var pin = new DebianRootDevelopmentPinV1(prior.BuildId, prior.DescriptorSha256, new('B', 64),
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        if (mutation == "retag") storage = f.Storage;
        if (mutation == "plan") plan = plan with { PredecessorImportSha256 = new('F', 64) };
        if (mutation == "pin") pin = pin with { BuildId = Guid.NewGuid() };
        if (mutation == "expired") pin = pin with { NotAfterUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
        if (mutation == "operation") plan = plan with { OperationId = Guid.NewGuid() };
        if (mutation == "transformations") plan = plan with { TransformationPolicy = "debian-trixie-core-configuration-v2" };
        if (mutation == "v3-transformations") plan = plan with { TransformationPolicy = "debian-trixie-core-configuration-v3" };
        if (mutation == "v4-transformations") plan = plan with { TransformationPolicy = "debian-trixie-core-configuration-v4" };
        var journal = new DebianDeploymentFixture.Journal();
        var effects = mutation == "shared-store" ? journal : new DebianDeploymentFixture.Journal();
        if (mutation != "valid")
        {
            Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.ForLabConfiguration(storage, prior, plan, journal, effects, pin));
            return;
        }
        var authority = DebianMountSessionAuthority.ForLabConfiguration(storage, prior, plan, journal, effects, pin);
        Assert.Null(authority.SourcePlan);
        Assert.Null(authority.StorageSmoke);
        Assert.Equal(e.GenerationId, authority.GenerationId);
        await authority.BeginAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => authority.BeginAction(DebianMountSessionAction.ImportConfiguredRoot));
        Assert.Throws<InvalidOperationException>(() => authority.BeginAction(DebianMountSessionAction.TransferUserData));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.BeginAsync(CancellationToken.None));
        using var persisted = JsonDocument.Parse(Assert.Single(journal.Checkpoints));
        Assert.Equal(3, persisted.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(operation, persisted.RootElement.GetProperty("OperationId").GetGuid());
        Assert.Equal(f.ImportHash, persisted.RootElement.GetProperty("Predecessor").GetProperty("ResultSha256").GetString());
        Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.ForLabStorageSmoke(storage, journal));
    }

    private static Fixture Predecessor(string mutation)
    {
        var e = LabStorageFixture.Create(true); var storage = LabStorageFixture.Validate(e);
        var inventory = LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted);
        var session = Guid.NewGuid();
        var leases = InstallerBlockLeases.Acquire(storage, session, DateTimeOffset.UtcNow, inventory,
            Observations.Available(LabStorageFixture.Numbers(inventory.Value)), Observations.Available(e.GuestDisks)).Value;
        var bindings = leases.Bindings;
        if (mutation == "target") bindings = bindings.SetItem(0, bindings[0] with { FileSystemUuid = Guid.NewGuid().ToString("D") });
        if (mutation == "geometry") bindings = bindings.SetItem(0, bindings[0] with { StoragePartition = bindings[0].StoragePartition! with { OffsetBytes = 512 } });
        if (mutation == "access") bindings = bindings.SetItem(1, bindings[1] with { Access = InstallerBlockAccess.ReadWrite });
        var plan = new string('A', 64); var build = Guid.NewGuid(); var derivation = Guid.NewGuid();
        var imported = ImmutableArray.CreateBuilder<byte[]>(); var sessions = ImmutableArray.CreateBuilder<byte[]>();
        var observation = JsonSerializer.SerializeToElement(new { Filesystem = "fixture-independent" });
        void Add(ImmutableArray<byte[]>.Builder chain, Dictionary<string, object?> fields)
        {
            fields["SchemaVersion"] = 2; fields["GenerationId"] = mutation == "generation" ? Guid.NewGuid() : e.GenerationId;
            fields["SessionId"] = session; fields["PlanSha256"] = plan; fields["Provenance"] = storage.Provenance;
            fields["Sequence"] = chain.Count; fields["PreviousSha256"] = chain.Count == 0 ? null : Hash(chain[^1]);
            chain.Add(JsonSerializer.SerializeToUtf8Bytes(fields));
        }
        foreach (var outcome in new[] { "IntentDurable", mutation == "failed" ? "OutcomeUnknown" : "AppliedAndVerified" })
            Add(imported, new() { ["Outcome"] = outcome, ["Bindings"] = bindings, ["BuildId"] = build, ["DerivationId"] = derivation,
                ["DescriptorSha256"] = new string('B', 64), ["ManifestSha256"] = new string('C', 64), ["ContentSha256"] = new string('D', 64),
                ["TransportManifestSha256"] = new string('E', 64), ["Evidence"] = new { ObserverEvidence = observation,
                    ObserverEvidenceSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(observation)) } });
        var rootMount = new { Id = 1, Path = "/root-fixture" }; var payloadMount = new { Id = 2, Path = "/payload-fixture" };
        if (mutation != "teardown")
            Add(sessions, new() { ["Action"] = "UnmountPayload", ["State"] = "AppliedAndVerified", ["Evidence"] = new {
                Path = payloadMount.Path, Before = new { Mounts = new[] { rootMount, payloadMount } }, After = new { Mounts = new[] { rootMount } } } });
        Add(sessions, new() { ["Action"] = "UnmountRoot", ["State"] = "AppliedAndVerified", ["Evidence"] = new {
            Path = rootMount.Path, Before = new { Mounts = new[] { rootMount } }, After = new { Mounts = Array.Empty<object>() } } });
        var importHash = Hash(imported[^1]);
        Add(sessions, new() { ["Action"] = "Close", ["State"] = "AppliedAndVerified", ["ImportResultReference"] = $"00000001-{importHash}.json" });
        return new(storage, e, sessions.ToImmutable(), mutation == "missing" ? [] : imported.ToImmutable(), importHash, Hash(sessions[^1]));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record Fixture(ValidatedInstallationStorage Storage, InstallerLabStorageEvidenceV1 Evidence,
        ImmutableArray<byte[]> Session, ImmutableArray<byte[]> Import, string ImportHash, string CloseHash);
}
