using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class InstallerLabAcquisitionTests
{
    private static readonly Guid Run = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");
    private static readonly string Hash = new('A', 64);
    private static readonly InstallerRuntimeDiskV1 Disk = new("/dev/vda", Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"), 16UL << 30, 512);
    private static readonly InstallerLabBackingV1 Backing = new("IGLOO-LAB-TARGET", 16UL << 30, 1, 2, Hash, new('B', 64));
    private static InstallerRuntimeInventoryV1 Inventory => new([Disk], []);

    private static Observation<InstallerLabAcquisitionV1> Correlate(InstallerRuntimeInventoryV1? inventory = null,
        InstallerLabBackingV1? reopened = null, ImmutableArray<InstallerLabGuestDiskV1>? guest = null) =>
        InstallerLabAcquisition.Correlate(Run, Hash, [Backing], [reopened ?? Backing],
            Observations.Available(inventory ?? Inventory), Observations.Available(guest ?? [new(Disk.DevicePath, Backing.Serial, 512)]));

    [Fact]
    public void CorrelationRetainsLabProvenanceWithoutInventingWindowsFields()
    {
        var result = Correlate();
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        Assert.Equal(Run, result.Value.LabRunId);
        Assert.Equal(Backing, result.Value.Disks.Single().Backing);
        // Distinct types: there is no lab-to-Windows disk/volume conversion.
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("VolumeGuid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("UniqueIdFormat", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BusType", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsRecoveryValidationDoesNotAcceptUnobservedProviderFields()
    {
        // Deliberately invalid negative fixture. Production code has no such conversion.
        var disk = new Igloo.Core.Recovery.CanonicalDiskIdentityV1("", 0, 0,
            Disk.GptDiskGuid, Disk.SizeBytes, 512, 512);
        var volume = new Igloo.Core.Recovery.CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.Empty,
            PreparationSpacePlanning.EspType, 1024 * 1024, 256 * 1024 * 1024, "FAT32");
        Assert.False(Igloo.Core.Recovery.CanonicalRecoveryIdentity.IsValid(volume));
    }

    [Theory]
    [InlineData("inode")]
    [InlineData("launch")]
    [InlineData("length")]
    public void ChangedHostBindingIsRejected(string field)
    {
        var changed = field switch { "inode" => Backing with { HostInode = 3 },
            "launch" => Backing with { LaunchSha256 = new('C', 64) }, _ => Backing with { Length = 8UL << 30 } };
        Assert.Equal(ObservationAvailability.Ambiguous, Correlate(reopened: changed).Availability);
    }

    [Fact]
    public void MissingExtraDuplicateOrSubstitutedGuestIsRejected()
    {
        Assert.Equal(ObservationAvailability.Ambiguous, Correlate(guest: []).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, Correlate(guest: [new("/dev/vdb", Backing.Serial, 512)]).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, Correlate(guest: [new("/dev/vda", "OTHER", 512)]).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, Correlate(inventory: new([Disk, Disk], [])).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, Correlate(inventory: new([Disk, Disk with { DevicePath = "/dev/vdb", GptDiskGuid = Guid.NewGuid() }], [])).Availability);
    }

    [Theory]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unavailable)]
    public void FailedWholeAcquisitionDoesNotBecomeFilteredSuccess(ObservationAvailability availability)
    {
        var result = InstallerLabAcquisition.Correlate(Run, Hash, [Backing], [Backing],
            Observations.Failure<InstallerRuntimeInventoryV1>(availability, "NonGptDiskVisible"),
            Observations.Available<ImmutableArray<InstallerLabGuestDiskV1>>([new("/dev/vda", Backing.Serial, 512)]));
        Assert.Equal(availability, result.Availability);
    }

    [Fact]
    public void CreationRequiresActualUnformattedBeforeAfterTransition()
    {
        var partition = new InstallerRuntimePartitionV1("/dev/vda1", "/dev/vda", Guid.NewGuid(),
            PreparationSpacePlanning.LinuxDataType, 1024 * 1024, 1024 * 1024,
            Observations.Failure<InstallerFileSystemV1>(ObservationAvailability.Absent, "NoRecognizedFilesystemSignature"));
        var after = Inventory with { Partitions = [partition] };
        var acquisition = Correlate().Value;
        Observation<InstallerLabPartitionReceiptV1> Verify(InstallerRuntimeInventoryV1 before, InstallerRuntimeInventoryV1 readback,
            Guid generation) => InstallerLabAcquisition.VerifyCreation(acquisition, generation, partition,
                Observations.Available(before), Observations.Available(readback), Hash, Hash, Hash);
        var generation = Guid.NewGuid();
        Assert.Equal(ObservationAvailability.Available, Verify(Inventory, after, generation).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, Verify(after, after, generation).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, Verify(Inventory, after, Guid.Empty).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, Verify(Inventory, after with { Partitions =
            [partition with { OffsetBytes = 2 * 1024 * 1024 }] }, generation).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, Verify(Inventory, after with { Partitions =
            [partition with { FileSystem = Observations.Available(new InstallerFileSystemV1("EXT4", Guid.NewGuid().ToString())) }] }, generation).Availability);

        var created = Verify(Inventory, after, generation).Value;
        var fs = new InstallerFileSystemV1("EXT4", Guid.NewGuid().ToString("D"));
        var formatted = after with { Partitions = [partition with { FileSystem = Observations.Available(fs) }] };
        var receipt = InstallerLabAcquisition.VerifyFormat(created, generation, fs, Observations.Available(after),
            Observations.Available(formatted), Hash, Hash, Hash);
        Assert.Equal(ObservationAvailability.Available, receipt.Availability);
        Assert.Equal(created, receipt.Value.Creation);
        Assert.Equal(ObservationAvailability.Ambiguous, InstallerLabAcquisition.VerifyFormat(created, Guid.NewGuid(), fs,
            Observations.Available(after), Observations.Available(formatted), Hash, Hash, Hash).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, InstallerLabAcquisition.VerifyFormat(created, generation, fs,
            Observations.Available(formatted), Observations.Available(formatted), Hash, Hash, Hash).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, InstallerLabAcquisition.VerifyFormat(created, generation,
            fs with { Uuid = Guid.NewGuid().ToString("D") }, Observations.Available(after),
            Observations.Available(formatted), Hash, Hash, Hash).Availability);
    }
}
