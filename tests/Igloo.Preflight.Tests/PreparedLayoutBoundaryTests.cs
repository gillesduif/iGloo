using System.Collections.Immutable;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight.CommunityPreparation;
using Igloo.Preflight.CommunityRecovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class PreparedLayoutBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "igloo-layout-test-" + Guid.NewGuid().ToString("D"));

    [Fact]
    public void StagedContentCheckpointRetainsTheSameEspForPermanentLinuxUse()
    {
        var checkpoint = Planned();
        var partitions = checkpoint.Plan.Space.Allocations.Where(a => a.CreateInWindows).Select(a =>
            new PreparedPartitionV1(a.Role, new CanonicalVolumeIdentityV1(checkpoint.Plan.TargetDisk, Guid.NewGuid(), Guid.NewGuid(),
                a.PartitionType, a.OffsetBytes, a.SizeBytes, a.FileSystem))).ToImmutableArray();
        var esp = partitions.Single(p => p.Role == PreparationRole.LinuxEsp).Identity;
        ImmutableArray<CanonicalFileIdentityV1> files =
            [File(PreparedLayoutRules.ShimPath), File(PreparedLayoutRules.GrubPath), File(@"\EFI\iGloo\grub.cfg")];
        var staged = checkpoint with
        {
            State = PreparationState.ContentStagedAndVerified,
            CreationReceipts = partitions,
            Layout = new(1, checkpoint.Plan, partitions, "1234-ABCD", files),
        };
        var reference = Store().PersistAndReopen(staged);
        var reopened = Store().Reopen(reference);
        Assert.Equal(PreparationState.ContentStagedAndVerified, reopened.State);
        Assert.Equal(esp, reopened.Layout!.Partitions.Single(p => p.Role == PreparationRole.LinuxEsp).Identity);
        Assert.Equal(files.ToArray(), reopened.Layout.BootFiles.ToArray());
        Assert.Equal(ObservationAvailability.Unsupported, DedicatedEspPreparationSupport.Production.Availability);
        CanonicalFileIdentityV1 File(string path) => new(esp.VolumeGuid, path, 100, new string('A', 64));
    }

    [Fact]
    public void DurablePlanReopensIndependentlyWithoutLocatorOrLabelAuthority()
    {
        var checkpoint = Planned();
        var reference = Store().PersistAndReopen(checkpoint);
        var reopened = Store().Reopen(reference);
        Assert.Equal(checkpoint.Plan.GenerationId, reopened.Plan.GenerationId);
        Assert.Equal(checkpoint.Plan.TargetDisk, reopened.Plan.TargetDisk);
        Assert.Equal(checkpoint.Plan.WindowsEsp, reopened.Plan.WindowsEsp);
        Assert.Equal(PreparationState.Planned, reopened.State);
        var text = Encoding.UTF8.GetString(new CommunityRecoveryArtifactStore(_root).Reopen(reference.ArtifactId));
        Assert.DoesNotContain("DriveLetter", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DiskNumber", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PartitionNumber", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Label", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactTamperingAndGenerationSubstitutionFailClosed()
    {
        var reference = Store().PersistAndReopen(Planned());
        Assert.Throws<InvalidDataException>(() => Store().Reopen(reference with { GenerationId = Guid.NewGuid() }));
        var file = Assert.Single(Directory.GetFiles(_root));
        File.AppendAllText(file, " ");
        Assert.Throws<InvalidDataException>(() => Store().Reopen(reference));
    }

    [Fact]
    public void IndependentReopenFailureIsNotASuccessfulCheckpoint()
    {
        var store = new PreparedLayoutStore(new FailedReopen());
        Assert.Throws<IOException>(() => store.PersistAndReopen(Planned()));
    }

    [Fact]
    public void CreatedStateRequiresAllOwnedReceipts()
    {
        Assert.Throws<InvalidDataException>(() => Store().PersistAndReopen(Planned() with { State = PreparationState.CreatedAndVerified }));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void PlannedStateCannotSmuggleAStagedLayout()
    {
        var plan = Planned();
        Assert.Throws<InvalidDataException>(() => Store().PersistAndReopen(plan with
        {
            Layout = new(1, plan.Plan, [], "1234-ABCD", []),
        }));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("Fedora KDE", "inst.stage2=hd:LABEL=OEMDRV: inst.ks=hd:LABEL=OEMDRV:/ks.cfg")]
    [InlineData("Debian", "auto=true priority=critical preseed/file=/preseed.cfg iso-scan/filename=/debian.iso")]
    [InlineData("Linux Mint Cinnamon", "automatic-ubiquity file=/preseed.cfg boot=casper iso-scan/filename=/mint.iso")]
    public async Task UnboundInstallerCannotReachShrinkOrLegacyStaging(string title, string cmdline)
    {
        var resize = new RejectResize();
        var service = new DirectInstallService(resize, NullLogger<DirectInstallService>.Instance);
        // Nonexistent paths intentionally prove that the gate precedes ISO mounting, shell-state
        // changes and any preparation IO, as well as shrink/create/format. This test is not elevated.
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => service.PrepareAsync(0, 1,
            "does-not-exist.iso", "does-not-exist-staging", new InstallerBootSpec { MenuTitle = title, KernelCmdline = cmdline }));
        Assert.Contains("exact installer ESP binding", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, resize.Calls);
        Assert.Equal(ObservationAvailability.Unsupported, DedicatedEspPreparationSupport.Production.Availability);
    }

    private PreparedLayoutStore Store() => new(new CommunityRecoveryArtifactStore(_root));

    [Fact]
    public void CompletePartitionReceiptsReopenWithoutInventingRootVolume()
    {
        var (binding, _) = Igloo.TestData.InstallationOwnershipFixture.Create(true);
        var layout = binding.Layout;
        var checkpoint = new PreparationCheckpointV1(1, PreparationState.ContentStagedAndVerified,
            layout.Plan, layout.Partitions, layout) { StorageOwnership = layout.StorageOwnership };
        var reference = Store().PersistAndReopen(checkpoint);
        var reopened = Store().Reopen(reference);
        Assert.Equal(layout.StorageOwnership!.CreatedPartitions.ToArray(), reopened.StorageOwnership!.CreatedPartitions.ToArray());
        Assert.DoesNotContain(reopened.CreationReceipts, r => r.Role == PreparationRole.LinuxRoot);
        Assert.Single(reopened.StorageOwnership.CreatedPartitions, r => r.Role == PreparationRole.LinuxRoot);
        Assert.Equal(Core.Abstractions.ObservationAvailability.Available, PreparedStorageOwnership.VerifyStructure(reopened.Layout!).Availability);
    }

    [Fact]
    public void IndividualPartitionReceiptCanBeDurableBeforeFormattingOrCreatingTheRest()
    {
        var (binding, _) = Igloo.TestData.InstallationOwnershipFixture.Create();
        var ownership = binding.Layout.StorageOwnership! with
        { CreatedPartitions = [binding.Layout.StorageOwnership!.CreatedPartitions[0]] };
        var checkpoint = new PreparationCheckpointV1(1, PreparationState.CreationInProgress, binding.Layout.Plan, [], null)
        { StorageOwnership = ownership };
        var reopened = Store().Reopen(Store().PersistAndReopen(checkpoint));
        Assert.Single(reopened.StorageOwnership!.CreatedPartitions);
        Assert.Null(reopened.Layout);
        Assert.Throws<InvalidDataException>(() => Store().PersistAndReopen(checkpoint with { State = PreparationState.CreatedAndVerified }));
    }
    private static PreparationCheckpointV1 Planned()
    {
        const ulong mib = PreparationSpacePlanning.Alignment;
        var disk = new CanonicalDiskIdentityV1("eui.fixture", 8, 17, Guid.NewGuid(), 100000 * mib, 512, 4096);
        var windows = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(), PreparationSpacePlanning.EspType, mib, 260 * mib, "FAT32");
        var space = PreparationSpacePlanning.Plan(new(40000 * mib, 40000 * mib, 20000 * mib, 1000 * mib, 0, 4096, false)).Value;
        return new(1, PreparationState.Planned, new(1, Guid.NewGuid(), disk, windows, [windows.PartitionGuid], space), [], null);
    }

    private sealed class FailedReopen : ICommunityRecoveryArtifactStore
    {
        public void PersistNew(Guid artifactId, ReadOnlySpan<byte> artifact) { }
        public byte[] Reopen(Guid artifactId) => throw new IOException("fresh read failed");
    }
    private sealed class RejectResize : IPartitionResizeService
    {
        public int Calls { get; private set; }
        public Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("unexpected resize observation"); }
        public Task ShrinkAsync(int diskNumber, long linuxSizeBytes, IProgress<string>? progress = null, CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("unexpected mutation"); }
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.GetFiles(_root)) File.Delete(file);
        Directory.Delete(_root, recursive: false);
    }
}
