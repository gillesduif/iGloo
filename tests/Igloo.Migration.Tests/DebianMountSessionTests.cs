using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;
using static Igloo.Migration.Tests.DebianDeploymentFixture;

namespace Igloo.Migration.Tests;

public sealed class DebianMountSessionTests
{
    private sealed class Harness
    {
        internal TargetRootFixture.State Storage { get; set; } = TargetRootFixture.Create();
        internal Journal Journal { get; } = new();
        internal DebianMountSessionAuthority Session { get; }
        internal DebianMountSessionAction Action { get; set; } = DebianMountSessionAction.AcquireLeases;
        internal long Sequence { get; set; }
        internal Journal ImportJournal { get; } = new();
        internal byte[]? Descriptor { get; }
        internal byte[]? TransportManifest { get; }
        internal Harness(bool import = false, bool chunked = false)
        {
            if (!import) { Session = new(Plan(Storage), Journal); return; }
            var artifact = DebianConfiguredRootTests.Artifact();
            var now = DateTimeOffset.UtcNow;
            artifact = artifact with { SchemaVersion = 2, CreatedAtUtc = now.AddMinutes(-1), SupportedUntilUtc = now.AddDays(1),
                Attestation = artifact.Attestation with { VerifiedAtUtc = now,
                    Neutralization = new("debian-trixie-neutralization-2026-09-28-v2", Guid.NewGuid(), artifact.BuildId,
                        Hash, Hash, Hash, Hash, Hash, Hash, Hash, Hash, true, true, true) } };
            Descriptor = DebianConfiguredRootArtifacts.Serialize(artifact);
            var plan = new DebianConfiguredRootImportPlanV1(Storage.Ownership, Storage.Root, artifact.BuildId,
                artifact.Attestation.Neutralization!.DerivationId, DebianConfiguredRootArtifacts.Digest(Descriptor),
                artifact.PackageSet.PolicySha256, artifact.Manifest.Sha256, artifact.Content.Sha256, artifact.Content.Length);
            if (chunked)
            {
                TransportManifest = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Type = "Chunked", plan.BuildId,
                    plan.DerivationId, plan.DescriptorSha256, plan.ManifestSha256, plan.ContentLength, plan.ContentSha256,
                    ChunkSize = DebianRootTransports.ChunkSize,
                    Chunks = new[] { new { Index = 0, Name = "root.content.0000", Length = plan.ContentLength, Sha256 = plan.ContentSha256 } } });
                plan = plan with { Transport = "Chunked", TransportManifestSha256 = DebianConfiguredRootArtifacts.Digest(TransportManifest) };
            }
            Session = DebianMountSessionAuthority.ForDevelopmentImport(plan, Journal, ImportJournal,
                new(artifact.BuildId, plan.DescriptorSha256, plan.PolicySha256, now.AddMinutes(-1), now.AddDays(1)));
        }
        internal async Task StartAsync()
        {
            await Session.BeginAsync(CancellationToken.None);
            await Session.ObserveSupervisorAsync(JsonSerializer.SerializeToElement(new { Session.SessionId, Kind = "SessionReady",
                Observation = new { Namespace = "mnt:[fixture-private]", Mounts = new[] { new { Propagation = Array.Empty<string>() } } } }),
                "mnt:[fixture-host]", CancellationToken.None);
        }
        internal JsonElement Message(string kind, object? record = null, Guid? challenge = null) => JsonSerializer.SerializeToElement(new
        {
            Session.SessionId, Session.GenerationId, Session.PlanSha256, Action = Action.ToString(),
            Kind = kind, Challenge = challenge ?? Guid.NewGuid(), ObservationSequence = Sequence,
            Inventory = Inventory(Storage.Inventory), DeviceNumbers = Storage.Mounts.DeviceNumbers, Record = record,
        });
        internal async Task InventoryAsync()
        {
            Sequence++;
            _ = await Session.AcceptEventAsync(Message("Inventory"), CancellationToken.None);
        }
        internal async Task CheckpointAsync(string state) =>
            _ = await Session.AcceptEventAsync(Message("Checkpoint", new { State = state }), CancellationToken.None);
        internal async Task BeginImportAsync()
        {
            await StartAsync();
            foreach (var action in new[] { DebianMountSessionAction.AcquireLeases, DebianMountSessionAction.PrepareImportMountpoints,
                DebianMountSessionAction.MountRoot, DebianMountSessionAction.MountPayload }) await RunAsync(action);
            Action = DebianMountSessionAction.ImportConfiguredRoot; Session.BeginAction(Action); await InventoryAsync();
        }
        internal Task<JsonElement> AuthenticateAsync(byte[]? descriptor = null, byte[]? transport = null) => Session.AcceptEventAsync(JsonSerializer.SerializeToElement(new
        {
            Session.SessionId, Session.GenerationId, Session.PlanSha256, Action = Action.ToString(),
            Kind = "AuthenticateImportSource", Challenge = Guid.NewGuid(), Descriptor = descriptor ?? Descriptor,
            TransportManifest = transport ?? TransportManifest,
        }), CancellationToken.None);
        internal Task<JsonElement> ImportCheckpointAsync(object record) => Session.AcceptEventAsync(Message("ImportCheckpoint", record), CancellationToken.None);
        internal async Task RunAsync(DebianMountSessionAction action)
        {
            Action = action; Session.BeginAction(action);
            if (action != DebianMountSessionAction.Close) await InventoryAsync();
            if (action != DebianMountSessionAction.Inspect)
            {
                await CheckpointAsync("IntentDurable");
                if (action != DebianMountSessionAction.Close) await InventoryAsync();
                await CheckpointAsync("AppliedAndVerified");
            }
            Session.CompleteAction(JsonSerializer.SerializeToElement(new { Session.SessionId, Action = action.ToString(), State = "AppliedAndVerified" }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportUsesOwnPlanCanonicalResolverAndSeparateReopenedJournal(bool chunked)
    {
        var h = new Harness(true, chunked); await h.BeginImportAsync();
        Assert.Equal(3, h.Session.Leases!.Bindings.Length);
        Assert.DoesNotContain("Credential", h.Session.ImportPlan!.Fingerprint(), StringComparison.Ordinal);
        await h.AuthenticateAsync();
        await h.ImportCheckpointAsync(new { Outcome = "IntentDurable" });
        await h.CheckpointAsync("IntentDurable");
        await h.InventoryAsync();
        await h.ImportCheckpointAsync(new { Outcome = "Progress", FilesWritten = 64 });
        var observed = new { h.Session.GenerationId, h.Session.SessionId, h.Session.PlanSha256,
            h.Session.ImportPlan.ManifestSha256, FilesystemSha256 = Hash, PackageStateSha256 = Hash, NeutralStateSha256 = Hash };
        var result = await h.ImportCheckpointAsync(new { Outcome = "AppliedAndVerified", FilesystemSha256 = Hash,
            PackageStateSha256 = Hash, NeutralStateSha256 = Hash, ObserverEvidence = observed,
            ObserverEvidenceSha256 = DebianDeploymentPlanning.TextHash(JsonSerializer.Serialize(observed)) });
        await h.CheckpointAsync("AppliedAndVerified");
        h.Session.CompleteAction(JsonSerializer.SerializeToElement(new { h.Session.SessionId, Action = h.Action.ToString(), State = "AppliedAndVerified",
            ImportResult = new { Reference = result.GetProperty("Reference").GetString(), Sha256 = result.GetProperty("Sha256").GetString(),
                h.Session.GenerationId, h.Session.PlanSha256, h.Session.ImportPlan.BuildId, h.Session.ImportPlan.DerivationId,
                h.Session.ImportPlan.DescriptorSha256, h.Session.ImportPlan.Transport, h.Session.ImportPlan.TransportManifestSha256,
                Qualification = "DevelopmentImportOnly" } }));
        Assert.Throws<InvalidOperationException>(() => h.Session.BeginAction(DebianMountSessionAction.ImportConfiguredRoot));
        Assert.Equal(3, h.ImportJournal.Checkpoints.Count);
        foreach (var action in new[] { DebianMountSessionAction.UnmountPayload, DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close })
            await h.RunAsync(action);
        Assert.Equal(DebianMountSessionState.Closed, h.Session.State);
        Assert.Equal(ObservationAvailability.Unsupported, DebianDeploymentSupport.Production.Availability);
    }

    [Fact]
    public async Task ChunkEnvelopeMismatchPreventsImportReservation()
    {
        var h = new Harness(true, true); await h.BeginImportAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => h.AuthenticateAsync(transport: [123, 125]));
        Assert.Empty(h.ImportJournal.Checkpoints);
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
    }

    [Fact]
    public async Task ImportPhaseCannotMountEspOrForgeSuccessWithoutImportReadback()
    {
        var h = new Harness(true); await h.BeginImportAsync();
        Assert.Throws<InvalidOperationException>(() => h.Session.BeginAction(DebianMountSessionAction.MountLinuxEsp));
        await h.AuthenticateAsync(); await h.ImportCheckpointAsync(new { Outcome = "IntentDurable" });
        await h.CheckpointAsync("IntentDurable"); await h.InventoryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => h.CheckpointAsync("AppliedAndVerified"));
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("reopen")]
    [InlineData("observer")]
    [InlineData("failure")]
    public async Task ImportFailureRetainsIntentAndNeverPromotesInstallation(string fault)
    {
        var h = new Harness(true); await h.BeginImportAsync();
        if (fault == "source")
            await Assert.ThrowsAsync<InvalidDataException>(() => h.AuthenticateAsync([1, 2, 3]));
        else
        {
            await h.AuthenticateAsync();
            if (fault == "reopen")
            {
                h.ImportJournal.FailReopen = true;
                await Assert.ThrowsAsync<IOException>(() => h.ImportCheckpointAsync(new { Outcome = "IntentDurable" }));
            }
            else
            {
                await h.ImportCheckpointAsync(new { Outcome = "IntentDurable" });
                await h.CheckpointAsync("IntentDurable"); await h.InventoryAsync();
                if (fault == "failure") await h.ImportCheckpointAsync(new { Outcome = "OutcomeUnknown" });
                else await Assert.ThrowsAsync<KeyNotFoundException>(() => h.ImportCheckpointAsync(new { Outcome = "AppliedAndVerified" }));
            }
            Assert.NotEmpty(h.ImportJournal.Checkpoints);
        }
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
        Assert.Throws<InvalidOperationException>(() => h.Session.BeginAction(DebianMountSessionAction.ImportConfiguredRoot));
    }

    [Theory]
    [InlineData("premature")]
    [InlineData("duplicate")]
    [InlineData("unbounded")]
    public async Task ImportProgressCannotAdvanceOutsideDeclaredSequence(string fault)
    {
        var h = new Harness(true); await h.BeginImportAsync(); await h.AuthenticateAsync();
        await h.ImportCheckpointAsync(new { Outcome = "IntentDurable" });
        if (fault != "premature") await h.CheckpointAsync("IntentDurable");
        if (fault == "duplicate") await h.ImportCheckpointAsync(new { Outcome = "Progress", FilesWritten = 64 });
        await Assert.ThrowsAsync<InvalidDataException>(() => h.ImportCheckpointAsync(new { Outcome = "Progress", FilesWritten = fault == "unbounded" ? 500001 : 64 }));
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
    }

    [Fact]
    public async Task RealArtifactSingleFileTransportStopsBeforeSessionReservation()
    {
        var h = new Harness(true);
        var plan = h.Session.ImportPlan! with { ContentLength = 4641457180 };
        var authority = DebianMountSessionAuthority.ForDevelopmentImport(plan, h.Journal, h.ImportJournal,
            new(plan.BuildId, plan.DescriptorSha256, plan.PolicySha256, Now, Now.AddDays(7)));
        Assert.Equal("ConfiguredRootContentExceedsFat32SingleFile", plan.TransportSupport.Code);
        await Assert.ThrowsAsync<NotSupportedException>(() => authority.BeginAsync(CancellationToken.None));
        Assert.Empty(h.Journal.Checkpoints); Assert.Empty(h.ImportJournal.Checkpoints);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("format")]
    [InlineData("root")]
    public void ImportPlanCannotReplaceFormattedRootReceipt(string change)
    {
        var h = new Harness(true); var plan = h.Session.ImportPlan!;
        plan = plan with { Root = change switch {
            "generation" => plan.Root with { GenerationId = Guid.NewGuid() },
            "format" => plan.Root with { FileSystem = "FAT32" },
            _ => plan.Root with { Partition = plan.Root.Partition with { PartitionGuid = Guid.NewGuid() } },
        } };
        Assert.Throws<InvalidDataException>(plan.Fingerprint);
    }

    [Fact]
    public async Task NativeNumericNamespaceIdentityIsComparedToParent()
    {
        var h = new Harness(); await h.Session.BeginAsync(CancellationToken.None);
        var ready = JsonSerializer.SerializeToElement(new { h.Session.SessionId, Kind = "SessionReady",
            Observation = new { Namespace = 123UL, Mounts = new[] { new { Propagation = Array.Empty<string>() } } } });
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Session.ObserveSupervisorAsync(ready, "mnt:[123]", CancellationToken.None));
    }

    [Fact]
    public void NativeImportJournalRequiresPersistentWitness()
    {
        var h = new Harness(true);
        var journal = new DebianLinuxDeploymentJournal("/run/volatile", "/tool", Hash);
        Assert.Throws<NotSupportedException>(() => DebianMountSessionAuthority.ForDevelopmentImport(h.Session.ImportPlan!, journal,
            h.ImportJournal, new(h.Session.ImportPlan!.BuildId, h.Session.ImportPlan.DescriptorSha256, h.Session.ImportPlan.PolicySha256, Now, Now.AddDays(7))));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("esp")]
    [InlineData("payload")]
    [InlineData("missing")]
    [InlineData("wrong-uuid")]
    public void JournalCannotWriteIntoTargetProtectedOrUnobservedStorage(string change)
    {
        var h = new Harness(true);
        var rootDevice = h.Storage.Mounts.DeviceNumbers.Single(n => n.DevicePath == h.Storage.Inventory.Partitions.Single(
            p => p.PartitionGuid == h.Storage.Root.Partition.PartitionGuid).DevicePath);
        var locator = change switch {
            "esp" => h.Storage.Mounts.DeviceNumbers.First(n => n != rootDevice && h.Storage.Inventory.Partitions.Any(p => p.DevicePath == n.DevicePath)),
            "payload" => h.Storage.Mounts.DeviceNumbers.Last(),
            "missing" => rootDevice with { Minor = 999 },
            _ => rootDevice,
        };
        var observed = JsonSerializer.SerializeToElement(new { Inventory = Inventory(h.Storage.Inventory), DeviceNumbers = h.Storage.Mounts.DeviceNumbers,
            Stores = new[] { new DebianJournalStoreWitnessV1("/journal/session", 1, 1, 1, locator.Major, locator.Minor, "EXT4"),
                new DebianJournalStoreWitnessV1("/journal/import", 1, 2, 1, locator.Major, locator.Minor, "EXT4") } });
        Assert.Throws<InvalidDataException>(() => DebianImportJournalStorage.Verify(h.Session.ImportPlan!, "/journal/session", "/journal/import",
            change == "wrong-uuid" ? Guid.NewGuid() : h.Storage.Root.FileSystemUuid, observed));
    }

    [Fact]
    public void JournalWitnessRequiresSeparatelyNominatedPersistentRuntimeFilesystem()
    {
        var h = new Harness(true);
        var uuid = Guid.NewGuid();
        var disk = new InstallerRuntimeDiskV1("/dev/runtime", Guid.NewGuid(), 8UL * 1024 * 1024 * 1024, 512);
        var partition = new InstallerRuntimePartitionV1("/dev/runtime1", disk.DevicePath, Guid.NewGuid(),
            h.Storage.Root.Partition.PartitionType, 1024 * 1024, 7UL * 1024 * 1024 * 1024,
            Observations.Available(new InstallerFileSystemV1("EXT4", uuid.ToString("D"))));
        var inventory = h.Storage.Inventory with { Disks = h.Storage.Inventory.Disks.Add(disk), Partitions = h.Storage.Inventory.Partitions.Add(partition) };
        var numbers = h.Storage.Mounts.DeviceNumbers.Add(new(disk.DevicePath, 240, 0)).Add(new(partition.DevicePath, 240, 1));
        var stores = new[] { new DebianJournalStoreWitnessV1("/journal/session", 61441, 1, 100, 240, 1, "EXT4"),
            new DebianJournalStoreWitnessV1("/journal/import", 61441, 2, 100, 240, 1, "EXT4") };
        var observed = JsonSerializer.SerializeToElement(new { Inventory = Inventory(inventory), DeviceNumbers = numbers, Stores = stores });
        var actual = DebianImportJournalStorage.Verify(h.Session.ImportPlan!, stores[0].Path, stores[1].Path, uuid, observed);
        Assert.Equal(stores, actual);
        Assert.Throws<InvalidDataException>(() => DebianImportJournalStorage.Verify(h.Session.ImportPlan!, stores[0].Path, stores[1].Path, Guid.NewGuid(), observed));
    }

    private static object Inventory(InstallerRuntimeInventoryV1 inventory) => new
    {
        schemaVersion = 1, availability = "Available",
        disks = inventory.Disks.Select(d => new { devicePath = d.DevicePath, gptDiskGuid = d.GptDiskGuid, sizeBytes = d.SizeBytes, logicalSectorSize = d.LogicalSectorSize }),
        partitions = inventory.Partitions.Select(p => new { devicePath = p.DevicePath, diskDevicePath = p.DiskDevicePath,
            partitionGuid = p.PartitionGuid, partitionType = p.PartitionType, offsetBytes = p.OffsetBytes, sizeBytes = p.SizeBytes,
            fileSystem = p.FileSystem.Availability == ObservationAvailability.Available ?
                (object)new { availability = "Available", type = p.FileSystem.Value.Type, uuid = p.FileSystem.Value.Uuid } : new { availability = p.FileSystem.Availability.ToString(), code = p.FileSystem.Code } }),
        externalFileSystems = Array.Empty<object>(),
    };

    [Fact]
    public async Task SessionJoinsCanonicalLeasesFreshObservationsAndReopenedJournal()
    {
        var h = new Harness(); await h.StartAsync();
        foreach (var action in new[] { DebianMountSessionAction.AcquireLeases, DebianMountSessionAction.MountRoot,
            DebianMountSessionAction.MountLinuxEsp, DebianMountSessionAction.MountPayload, DebianMountSessionAction.Inspect,
            DebianMountSessionAction.UnmountLinuxEsp, DebianMountSessionAction.UnmountPayload, DebianMountSessionAction.UnmountRoot, DebianMountSessionAction.Close })
            await h.RunAsync(action);
        Assert.Equal(DebianMountSessionState.Closed, h.Session.State);
        Assert.Equal(18, h.Journal.Checkpoints.Count);
        Assert.Equal(3, h.Session.Leases!.Bindings.Length);
        Assert.Equal(ObservationAvailability.Unsupported, DebianDeploymentSupport.Production.Availability);
        for (var i = 0; i < h.Journal.Checkpoints.Count; i++)
        {
            using var record = JsonDocument.Parse(h.Journal.Checkpoints[i]);
            Assert.Equal(i, record.RootElement.GetProperty("Sequence").GetInt32());
            Assert.Equal(h.Session.SessionId, record.RootElement.GetProperty("SessionId").GetGuid());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Session.BeginAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReplayedInventoryChallengePoisonsSession()
    {
        var h = new Harness(); await h.StartAsync(); h.Session.BeginAction(h.Action); h.Sequence++;
        var message = h.Message("Inventory");
        await h.Session.AcceptEventAsync(message, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Session.AcceptEventAsync(message, CancellationToken.None));
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
    }

    [Fact]
    public async Task StaleSequenceWithNewChallengeRejected()
    {
        var h = new Harness(); await h.StartAsync(); h.Session.BeginAction(h.Action);
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Session.AcceptEventAsync(h.Message("Inventory"), CancellationToken.None));
    }

    [Fact]
    public async Task MountCannotPrecedeLeaseAcquisition()
    {
        var h = new Harness(); await h.StartAsync();
        Assert.Throws<InvalidOperationException>(() => h.Session.BeginAction(DebianMountSessionAction.MountRoot));
    }

    [Fact]
    public async Task ResultWithoutFreshAfterObservationIsRejected()
    {
        var h = new Harness(); await h.StartAsync(); h.Session.BeginAction(h.Action);
        await h.InventoryAsync(); await h.CheckpointAsync("IntentDurable");
        await Assert.ThrowsAsync<InvalidDataException>(() => h.CheckpointAsync("AppliedAndVerified"));
    }

    [Fact]
    public async Task IntentWithoutFreshOwnershipIsRejected()
    {
        var h = new Harness(); await h.StartAsync(); h.Session.BeginAction(h.Action);
        await Assert.ThrowsAsync<InvalidDataException>(() => h.CheckpointAsync("IntentDurable"));
    }

    [Fact]
    public async Task ReopenFailureStopsAcknowledgementAndRetainsIntent()
    {
        var h = new Harness(); await h.StartAsync(); h.Session.BeginAction(h.Action); await h.InventoryAsync();
        h.Journal.FailReopen = true;
        await Assert.ThrowsAsync<IOException>(() => h.CheckpointAsync("IntentDurable"));
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
        Assert.Equal(3, h.Journal.Checkpoints.Count);
        Assert.Throws<InvalidOperationException>(() => h.Session.BeginAction(DebianMountSessionAction.AcquireLeases));
    }

    [Fact]
    public async Task CanonicalIdentityChangeAfterIntentStopsSession()
    {
        var h = new Harness(); await h.StartAsync(); h.Session.BeginAction(h.Action); await h.InventoryAsync(); await h.CheckpointAsync("IntentDurable");
        var root = h.Storage.Inventory.Partitions.Single(p => p.PartitionGuid == h.Storage.Root.Partition.PartitionGuid);
        h.Storage = h.Storage with { Inventory = h.Storage.Inventory with { Partitions = h.Storage.Inventory.Partitions.Replace(root, root with { SizeBytes = root.SizeBytes - 512 }) } };
        await Assert.ThrowsAsync<InvalidDataException>(h.InventoryAsync);
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
    }

    [Fact]
    public async Task TeardownCannotSilentlyStartAnotherDeployment()
    {
        var h = new Harness(); await h.StartAsync(); await h.RunAsync(DebianMountSessionAction.AcquireLeases);
        await h.RunAsync(DebianMountSessionAction.MountRoot); await h.RunAsync(DebianMountSessionAction.UnmountRoot);
        Assert.Throws<InvalidOperationException>(() => h.Session.BeginAction(DebianMountSessionAction.MountRoot));
        await h.RunAsync(DebianMountSessionAction.Close);
    }

    [Fact]
    public async Task DirtySessionCannotCloseOrUnmountRootOutOfOrder()
    {
        var h = new Harness(); await h.StartAsync(); await h.RunAsync(DebianMountSessionAction.AcquireLeases);
        await h.RunAsync(DebianMountSessionAction.MountRoot); await h.RunAsync(DebianMountSessionAction.MountLinuxEsp);
        Assert.Throws<InvalidOperationException>(() => h.Session.BeginAction(DebianMountSessionAction.Close));
        Assert.Throws<InvalidOperationException>(() => h.Session.BeginAction(DebianMountSessionAction.UnmountRoot));
    }

    [Fact]
    public async Task SupervisorMustBeIndependentlyObservedBeforeAnyDeviceAction()
    {
        var h = new Harness(); await h.Session.BeginAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => h.Session.BeginAction(DebianMountSessionAction.AcquireLeases));
    }

    [Theory]
    [InlineData("same-namespace")]
    [InlineData("propagation")]
    [InlineData("wrong-session")]
    public async Task ChangedSupervisorReadbackRejectsSession(string change)
    {
        var h = new Harness(); await h.Session.BeginAsync(CancellationToken.None);
        var message = JsonSerializer.SerializeToElement(new { SessionId = change == "wrong-session" ? Guid.NewGuid() : h.Session.SessionId,
            Kind = "SessionReady", Observation = new { Namespace = change == "same-namespace" ? "mnt:[host]" : "mnt:[private]",
                Mounts = new[] { new { Propagation = change == "propagation" ? new[] { "shared:1" } : [] } } } });
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Session.ObserveSupervisorAsync(message, "mnt:[host]", CancellationToken.None));
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
    }

    [Fact]
    public async Task LostResultDoesNotAuthorizeForwardProgress()
    {
        var h = new Harness(); await h.StartAsync(); h.Session.BeginAction(h.Action);
        await h.InventoryAsync(); await h.CheckpointAsync("IntentDurable");
        Assert.Throws<InvalidDataException>(() => h.Session.CompleteAction(JsonSerializer.SerializeToElement(new
            { h.Session.SessionId, Action = h.Action.ToString(), State = "OutcomeUnknown" })));
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
        Assert.Equal(3, h.Journal.Checkpoints.Count);
    }

    [Fact]
    public async Task UnavailableCanonicalObservationIsDurablyDistinctFromAbsence()
    {
        var h = new Harness(); await h.StartAsync(); h.Session.BeginAction(h.Action);
        var message = JsonSerializer.SerializeToElement(new { h.Session.SessionId, h.Session.GenerationId, h.Session.PlanSha256,
            Action = h.Action.ToString(), Kind = "Inventory", Challenge = Guid.NewGuid(), ObservationSequence = 1,
            Inventory = new { schemaVersion = 1, availability = "AccessDenied", code = "fixture-denied" }, DeviceNumbers = Array.Empty<LinuxDeviceNumberV1>() });
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Session.AcceptEventAsync(message, CancellationToken.None));
        using var checkpoint = JsonDocument.Parse(h.Journal.Checkpoints[^1]);
        Assert.Equal("AccessDenied", checkpoint.RootElement.GetProperty("Evidence").GetProperty("Availability").GetString());
    }

    [Fact]
    public async Task TransportCannotRunBeforeItsSupervisorStarts()
    {
        var h = new Harness();
        await using var transport = new DebianNativeMountSession(h.Session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.PerformAsync(DebianMountSessionAction.MountRoot, CancellationToken.None));
        Assert.Empty(h.Journal.Checkpoints);
    }

    [Fact]
    public async Task RejectedStartupDisposesSessionAndCannotRetry()
    {
        var h = new Harness();
        await using var transport = new DebianNativeMountSession(h.Session);
        await Assert.ThrowsAsync<ArgumentNullException>(() => transport.StartAsync(null!, CancellationToken.None));
        Assert.Equal(DebianMountSessionState.OutcomeUnknown, h.Session.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.StartAsync(null!, CancellationToken.None));
    }
}
