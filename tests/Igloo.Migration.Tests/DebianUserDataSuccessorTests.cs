using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;

namespace Igloo.Migration.Tests;

internal static class DebianUserDataSuccessorTests
{
    private static string Hash(byte[] raw) => DebianConfiguredRootArtifacts.Digest(raw);
    internal static void Check(ValidatedInstallationStorage previousStorage, DebianVerifiedInitramfs previous, InstallerLabStorageEvidenceV1 evidence)
    {
        var (manifest, entries, _) = DebianUserDataTests.Fixture();
        var operation = Guid.NewGuid(); var attempt = Guid.NewGuid();
        var transfer = DebianUserData.Serialize(DebianUserData.Plan(manifest, previousStorage.Provenance.GenerationId, operation, 1000, 1000, entries));
        var auth = new InstallerLabUserDataAuthorizationV1(1, "OneInitramfsCheckpointSelectedDocuments", attempt, operation,
            previousStorage.Provenance.GenerationId, previous.ResultSha256, previous.CloseSha256, new('A', 64), new('B', 64), new('C', 64), Hash(transfer));
        var backings = previousStorage.Acquisition.Disks.Select(d => d.Backing).ToImmutableArray();
        var copies = backings.Select((b, i) => new InstallerLabCopyV1(b.Serial, b.HostDevice, b.HostInode,
            b.HostDevice, b.HostInode + 10000, b.Length, i == 0 ? auth.TargetSha256 : auth.JournalSha256,
            i == 0 ? auth.TargetSha256 : auth.JournalSha256)).ToImmutableArray();
        backings = backings.Select((b, i) => b with { HostInode = copies[i].DestinationInode }).ToImmutableArray();
        var delivery = JsonSerializer.SerializeToUtf8Bytes(new { ExplicitSyntheticEffectBoundary = true });
        var derivation = new InstallerLabUserDataDerivationV1(auth, new('D', 64), copies, Hash(delivery), new('E', 64));
        var inventory = LinuxInstallerInventoryProtocol.Parse(evidence.Transitions[^1].Formatted);
        Observation<ValidatedInstallationStorage> Derive(InstallerLabUserDataDerivationV1 d) => InstallationStorage.ContinueLabInitramfsCheckpoint(
            previousStorage, d, new('D', 64), new('F', 64), backings, backings, inventory, Observations.Available(evidence.GuestDisks));
        var storage = Derive(derivation).Value;
        Assert.Equal(5, storage.Provenance.Version);
        Assert.NotEqual(ObservationAvailability.Available, Derive(derivation with { Authorization = auth with { Scope = "OneConfiguredCheckpointInitramfs" } }).Availability);
        Assert.NotEqual(ObservationAvailability.Available, Derive(derivation with { Copies = copies.SetItem(0, copies[0] with { SourceInode = copies[0].SourceInode + 1 }) }).Availability);
        Assert.NotEqual(ObservationAvailability.Available, Derive(derivation with { StagedTargetSha256 = auth.TargetSha256 }).Availability);
        Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.ForLabStorageSmoke(storage, new DebianDeploymentFixture.Journal()));
        var folder = "/fixture/userdata-tools"; var tool = Path.Combine(Path.GetDirectoryName(folder + "/session_entry.py")!, "deployment_journal.py");
        var hashes = ImmutableSortedDictionary<string, string>.Empty.Add(tool, new('F', 64)).Add(folder + "/collector.py", new('E', 64));
        foreach (var name in new[] { "python", "gate", "session_entry.py", "bwrap", "observer" }) hashes = hashes.Add(folder + "/" + name, new('A', 64));
        var runtime = new DebianSessionRuntimeV1(folder + "/python", folder + "/gate", folder + "/session_entry.py",
            folder + "/collector.py", folder + "/bwrap", folder + "/observer", hashes);
        var plan = new DebianLabUserDataPlanV1(1, operation, storage.Provenance, previous.ResultSha256, previous.CloseSha256,
            auth.RetentionSha256, storage.Continuation!.DerivationSha256!, derivation, transfer, delivery, Hash(JsonSerializer.SerializeToUtf8Bytes(hashes)));
        var parent = "/lab-journal/userdata/" + attempt;
        var store = new DebianJournalStoreWitnessV1(parent + "/effects", 1, 2, 3, 4, 5, "EXT4");
        var session = new DebianLinuxDeploymentJournal(parent + "/session", tool, hashes[tool], store with { Path = parent + "/session", Inode = 6 }, runtime);
        var effects = new DebianLinuxDeploymentJournal(store.Path, tool, hashes[tool], store, runtime);
        var imported = previous.Configured.Imported;
        var pin = new DebianRootDevelopmentPinV1(imported.BuildId, imported.DescriptorSha256, new('B', 64), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var authority = DebianMountSessionAuthority.ForLabUserData(storage, previous, plan, session, effects, store, pin);
        Assert.Null(authority.InitramfsPlan); Assert.Null(authority.ConfigurationPlan); Assert.Null(authority.SourcePlan);
        Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.ForLabUserData(storage, previous, plan with { InitramfsSha256 = new('F', 64) }, session, effects, store, pin));
        Assert.Throws<InvalidDataException>(() => DebianMountSessionAuthority.ForLabUserData(storage, previous, plan, session, effects, store, pin with { NotAfterUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }));
        Assert.Throws<InvalidDataException>(() => plan.Fingerprint(previousStorage, previous));
        var leases = InstallerBlockLeases.Acquire(storage, authority.SessionId, DateTimeOffset.UtcNow, inventory,
            Observations.Available(LabStorageFixture.Numbers(inventory.Value)), Observations.Available(evidence.GuestDisks)).Value;
        var context = DebianMountSessionAuthority.SerializeUserDataContext(plan, leases, authority.PlanSha256);
        var export = Environment.GetEnvironmentVariable("IGLOO_USERDATA_SESSION_WIRE");
        if (string.IsNullOrEmpty(export)) return;
        Directory.CreateDirectory(export);
        File.WriteAllText(Path.Combine(export, "request.json"), DebianNativeMountSession.SerializeRequest(authority, runtime, 12345));
        File.WriteAllBytes(Path.Combine(export, "context.json"), context);
        File.WriteAllBytes(Path.Combine(export, "plan.json"), transfer);
    }
}
