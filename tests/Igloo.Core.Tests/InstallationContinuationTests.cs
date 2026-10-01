using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.TestData;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class InstallationContinuationTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("source-inode")]
    [InlineData("destination-alias")]
    [InlineData("bytes")]
    [InlineData("scope")]
    [InlineData("generation")]
    [InlineData("operation")]
    [InlineData("authorization")]
    public void InitramfsCopyRequiresConfiguredLineageAndExactSourceBacking(string change)
    {
        var e = LabStorageFixture.Create(true); var imported = LabStorageFixture.Validate(e);
        var inventory = LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted);
        var configured = InstallationStorage.ContinueLabSameTarget(imported, Guid.NewGuid(), Guid.NewGuid(),
            new('A',64), new('B',64), new('C',64), e.CreatedBackings, e.ReopenedBackings, inventory,
            Observations.Available(e.GuestDisks)).Value;
        var authorization = new InstallerLabInitramfsAuthorizationV1(1, "OneConfiguredCheckpointInitramfs", Guid.NewGuid(),
            Guid.NewGuid(), e.GenerationId, new('D',64), new('E',64), new('F',64), new('A',64), new('B',64));
        if (change == "scope") authorization = authorization with { Scope = "CoreConfiguration" };
        if (change == "generation") authorization = authorization with { GenerationId = Guid.NewGuid() };
        if (change == "operation") authorization = authorization with { OperationId = configured.Continuation!.OperationId };
        var copies = e.CreatedBackings.Select((b,i) => new InstallerLabCopyV1(b.Serial, b.HostDevice, b.HostInode,
            b.HostDevice, b.HostInode + 1000, b.Length, i == 0 ? authorization.TargetSha256 : authorization.JournalSha256,
            i == 0 ? authorization.TargetSha256 : authorization.JournalSha256)).ToImmutableArray();
        if (change == "source-inode") copies = copies.SetItem(0, copies[0] with { SourceInode = 99999 });
        if (change == "destination-alias") copies = copies.SetItem(0, copies[0] with { DestinationInode = copies[0].SourceInode });
        if (change == "bytes") copies = copies.SetItem(0, copies[0] with { ReopenedSha256 = new('F',64) });
        var backings = e.CreatedBackings.Select((b,i) => b with { HostInode = copies[i].DestinationInode }).ToImmutableArray();
        var derivation = new InstallerLabInitramfsDerivationV1(authorization, new('C',64), copies);
        var result = InstallationStorage.ContinueLabConfiguredCheckpoint(configured, derivation,
            change == "authorization" ? new('0',64) : new('C',64), new('C',64), backings, backings, inventory,
            Observations.Available(e.GuestDisks));
        if (change != "valid") { Assert.NotEqual(ObservationAvailability.Available, result.Availability); return; }
        Assert.Equal("InitramfsImage", result.Value.Provenance.Scope);
        Assert.Equal(4, result.Value.Provenance.Version);
        Assert.Equal(configured.Receipts, result.Value.Receipts);
        Assert.NotEqual(ObservationAvailability.Available, InstallationStorage.ContinueLabConfiguredCheckpoint(imported,
            derivation, new('C',64), new('C',64), backings, backings, inventory, Observations.Available(e.GuestDisks)).Availability);
        Assert.NotEqual(ObservationAvailability.Available, InstallationStorage.ContinueLabConfiguredCheckpoint(result.Value,
            derivation, new('C',64), new('C',64), backings, backings, inventory, Observations.Available(e.GuestDisks)).Availability);
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("alias")]
    [InlineData("hash")]
    [InlineData("checkpoint")]
    [InlineData("authorization")]
    [InlineData("generation")]
    [InlineData("scope")]
    [InlineData("mixed")]
    [InlineData("old-operation")]
    public void DerivedAttemptRequiresIndependentEqualCopiesAndSeparateAuthorization(string change)
    {
        var e = LabStorageFixture.Create(true); var old = LabStorageFixture.Validate(e);
        var inventory = LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted);
        var failed = Guid.NewGuid();
        var authorization = new InstallerLabDerivedAuthorizationV1(1, "OneCheckpointDerivedCoreConfiguration", Guid.NewGuid(),
            Guid.NewGuid(), e.GenerationId, failed, new('A',64), new('B',64), new('C',64), new('D',64), new('E',64));
        if (change == "generation") authorization = authorization with { GenerationId = Guid.NewGuid() };
        if (change == "scope") authorization = authorization with { Scope = "StorageSmoke" };
        if (change == "old-operation") authorization = authorization with { OperationId = failed };
        var copies = e.CreatedBackings.Select((b,i) => new InstallerLabCopyV1(b.Serial, 1, (ulong)(100+i), 1,
            (ulong)(200+i), b.Length, i == 0 ? authorization.TargetSha256 : authorization.JournalSha256,
            i == 0 ? authorization.TargetSha256 : authorization.JournalSha256)).ToImmutableArray();
        if (change == "alias") copies = copies.SetItem(0, copies[0] with { DestinationInode = e.CreatedBackings[0].HostInode });
        if (change == "hash") copies = copies.SetItem(0, copies[0] with { ReopenedSha256 = new('F',64) });
        if (change == "mixed") copies = copies.SetItem(0, copies[0] with { SourceSha256 = authorization.JournalSha256 });
        var backings = e.CreatedBackings.Select((b,i) => b with { HostInode = copies[i].DestinationInode }).ToImmutableArray();
        var derivation = new InstallerLabDerivationV1(authorization, new('F',64),
            change == "checkpoint" ? new('F',64) : authorization.CheckpointSha256, copies);
        var result = InstallationStorage.ContinueLabCheckpoint(old, authorization, derivation,
            change == "authorization" ? new('0',64) : new('F',64), new('F',64), backings, backings, inventory, Observations.Available(e.GuestDisks));
        if (change != "valid") { Assert.NotEqual(ObservationAvailability.Available, result.Availability); return; }
        Assert.Equal(old.Receipts, result.Value.Receipts);
        Assert.NotNull(result.Value.Continuation!.DerivationSha256);
        Assert.NotEqual(ObservationAvailability.Available, InstallationStorage.ContinueLabSameTarget(old,
            authorization.AttemptId, authorization.OperationId, authorization.ImportSha256, authorization.CheckpointSha256,
            new('F',64), backings, backings, inventory, Observations.Available(e.GuestDisks)).Availability);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("inode")]
    [InlineData("journal-inode")]
    [InlineData("scope")]
    [InlineData("checkpoint")]
    [InlineData("inventory")]
    public void SuccessorKeepsLineageAndRequiresSameNominatedTarget(string change)
    {
        var e = LabStorageFixture.Create(change != "scope"); var old = LabStorageFixture.Validate(e);
        var inventory = LinuxInstallerInventoryProtocol.Parse(e.Transitions[^1].Formatted);
        var backings = e.CreatedBackings.Select(b => b with { LaunchSha256 = new('C', 64) }).ToImmutableArray();
        if (change == "inode") backings = backings.SetItem(0, backings[0] with { HostInode = 999 });
        if (change == "journal-inode") backings = backings.SetItem(1, backings[1] with { HostInode = 999 });
        if (change == "inventory") inventory = Observations.Failure<InstallerRuntimeInventoryV1>(ObservationAvailability.Unsupported, "NonGptDiskVisible");
        var result = InstallationStorage.ContinueLabSameTarget(old, Guid.NewGuid(), Guid.NewGuid(), new('D', 64),
            change == "checkpoint" ? "" : new('E', 64), new('F', 64), backings, backings, inventory, Observations.Available(e.GuestDisks));
        if (change != "valid") { Assert.NotEqual(ObservationAvailability.Available, result.Availability); return; }
        Assert.Equal(e.GenerationId, result.Value.Provenance.GenerationId);
        Assert.Equal("CoreConfiguration", result.Value.Provenance.Scope);
        Assert.Equal(3, result.Value.Provenance.Version);
        Assert.Equal(old.Receipts, result.Value.Receipts);
        Assert.Equal(old.Preserved, result.Value.Preserved);
        Assert.NotEqual(old.Provenance, result.Value.Provenance);
    }
}
