using System.Collections.Immutable;
using System.Management;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

// Beta Track B-01: a seed label nominates a partition; only iGloo's own files make it removable.
public sealed class InstallerSeedOwnershipTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * 1024 * 1024;
    private const string EspType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    private const string BasicType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    private const string SeedVolume = @"\\?\Volume{5d0a6f0e-1c0f-4d3b-9a44-6a3f0c8f4d10}\";
    private const long SeedOffset = 80 * GiB;
    private const long SeedSize = 4 * GiB;

    private static readonly WindowsStorageRow Esp = Row(1, MiB, 100 * MiB, EspType, system: true);
    private static readonly WindowsStorageRow Windows = Row(3, 117 * MiB, 79 * GiB, BasicType, boot: true, letter: 'C');
    private static readonly WindowsStorageRow Seed = Row(4, SeedOffset, SeedSize, BasicType, letter: 'I', volume: SeedVolume);

    private static bool Ours(string root) => root == SeedVolume;

    [Fact]
    public void A_seed_with_iGloo_files_on_the_windows_disk_is_owned()
    {
        var owned = InstallerSeedOwnership.FindOwnedSeed([Esp, Windows, Seed], SeedOffset, SeedSize, Ours);

        Assert.Same(Seed, owned);
    }

    [Fact]
    public void A_label_without_iGloo_files_is_not_owned()
    {
        var probed = new List<string>();

        var owned = InstallerSeedOwnership.FindOwnedSeed([Esp, Windows, Seed], SeedOffset, SeedSize,
            root => { probed.Add(root); return false; });

        Assert.Null(owned);
        Assert.Equal([SeedVolume], probed);
    }

    [Fact]
    public void A_seed_on_a_disk_that_does_not_boot_windows_is_not_owned()
    {
        var dataVolume = Row(1, MiB, 79 * GiB, BasicType, letter: 'D');

        Assert.Null(InstallerSeedOwnership.FindOwnedSeed([dataVolume, Seed], SeedOffset, SeedSize, _ => true));
    }

    [Fact]
    public void A_partition_that_moved_or_changed_size_since_the_report_is_not_owned()
    {
        Assert.Null(InstallerSeedOwnership.FindOwnedSeed([Esp, Windows, Seed], SeedOffset + MiB, SeedSize, _ => true));
        Assert.Null(InstallerSeedOwnership.FindOwnedSeed([Esp, Windows, Seed], SeedOffset, SeedSize + MiB, _ => true));
        Assert.Null(InstallerSeedOwnership.FindOwnedSeed([Esp, Windows], SeedOffset, SeedSize, _ => true));
    }

    [Fact]
    public void Only_an_ordinary_data_partition_with_a_reachable_volume_can_be_owned()
    {
        var asEsp = Row(4, SeedOffset, SeedSize, EspType, letter: 'I', volume: SeedVolume);
        var asSystem = Row(4, SeedOffset, SeedSize, BasicType, system: true, letter: 'I', volume: SeedVolume);
        var unmounted = Row(4, SeedOffset, SeedSize, BasicType);

        foreach (var candidate in new[] { asEsp, asSystem, unmounted })
            Assert.Null(InstallerSeedOwnership.FindOwnedSeed([Esp, Windows, candidate], SeedOffset, SeedSize, _ => true));
        Assert.Null(InstallerSeedOwnership.FindOwnedSeed([Esp, Windows], 117 * MiB, 79 * GiB, _ => true));
    }

    [Fact]
    public void The_volume_path_is_preferred_and_the_drive_letter_is_the_fallback()
    {
        Assert.Equal(SeedVolume, InstallerSeedOwnership.VolumeRoot(Seed));
        Assert.Equal(@"I:\", InstallerSeedOwnership.VolumeRoot(Row(4, SeedOffset, SeedSize, BasicType, letter: 'I')));
        Assert.Null(InstallerSeedOwnership.VolumeRoot(Row(4, SeedOffset, SeedSize, BasicType)));
    }

    [Theory]
    [InlineData("OEMDRV", true)]
    [InlineData("oemdrv", true)]
    [InlineData("CIDATA", true)]
    [InlineData("IGLOOISO", true)]
    [InlineData("Data", false)]
    [InlineData(null, false)]
    public void Seed_labels_are_matched_without_case(string? label, bool expected) =>
        Assert.Equal(expected, InstallerSeedOwnership.IsSeedLabel(label));

    [Fact]
    public void System_check_lists_only_an_owned_seed_on_the_windows_disk()
    {
        var storage = new StorageFake();
        storage.Add(0, Esp, Windows, Seed);
        storage.Add(1, Row(1, MiB, 79 * GiB, BasicType, letter: 'D'), Row(2, SeedOffset, SeedSize, BasicType, letter: 'J', volume: SeedVolume));
        DiskInfo Disk(int number, bool boot) => new($@"\\.\PHYSICALDRIVE{number}", "fixture", 128 * GiB, 0, "GPT",
        [
            new PartitionInfo(boot ? 3 : 1, "NTFS", 79 * GiB, boot ? "Windows" : "Data", false, boot, 0, boot ? 117 * MiB : MiB, BasicType),
            new PartitionInfo(boot ? 4 : 2, "FAT32", SeedSize, "OEMDRV", false, false, 0, SeedOffset, BasicType),
        ]);
        WindowsPreflightChecker Checker(Func<string, bool> probe) =>
            new(NullLogger<WindowsPreflightChecker>.Instance, storage, new BitLockerFake()) { SeedMarkerProbe = probe };

        var (installs, owned) = Checker(Ours).BuildLinuxInventory([Disk(0, boot: true), Disk(1, boot: false)]);
        var (_, driverDisk) = Checker(_ => false).BuildLinuxInventory([Disk(0, boot: true)]);

        Assert.Empty(installs);
        var leftover = Assert.Single(owned);
        Assert.Equal(0U, leftover.DiskNumber);
        Assert.Equal(4, leftover.Partition.Index);
        Assert.Empty(driverDisk);
    }

    [Fact]
    public void Removal_proves_ownership_again_on_the_live_layout()
    {
        var storage = new StorageFake();
        storage.Add(0, Esp, Windows, Seed);
        var reported = new SeedLeftover(0, "fixture", new PartitionInfo(4, "FAT32", SeedSize, "OEMDRV", false, false, 0, SeedOffset, BasicType));
        LinuxRemovalService Removal(Func<string, bool> probe, IWindowsStorageReader reader) =>
            new(NullLogger<LinuxRemovalService>.Instance, reader) { SeedMarkerProbe = probe };

        Assert.Same(Seed, Removal(Ours, storage).FindOwnedSeed(reported));
        Assert.Null(Removal(_ => false, storage).FindOwnedSeed(reported));
        Assert.Null(Removal(Ours, storage).FindOwnedSeed(reported with { Partition = reported.Partition with { Label = "Data" } }));
        Assert.Null(Removal(Ours, storage).FindOwnedSeed(reported with { Partition = reported.Partition with { OffsetBytes = SeedOffset - GiB } }));
        Assert.Null(Removal(Ours, new StorageFake { Error = new ManagementException("fixture") }).FindOwnedSeed(reported));
    }

    private static WindowsStorageRow Row(int number, long offset, long size, string gptType, bool boot = false,
        bool system = false, char letter = '\0', string? volume = null) =>
        new(new Dictionary<string, object?>
        {
            ["PartitionNumber"] = (uint)number, ["Offset"] = (ulong)offset, ["Size"] = (ulong)size, ["GptType"] = gptType,
            ["IsBoot"] = boot, ["IsSystem"] = system, ["DriveLetter"] = letter,
            ["AccessPaths"] = volume is null ? null : new[] { letter + @":\", volume },
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));

    private sealed class StorageFake : IWindowsStorageReader
    {
        private readonly Dictionary<int, WindowsStorageRow[]> _partitions = [];

        public Exception? Error { get; init; }

        public void Add(int disk, params WindowsStorageRow[] partitions) => _partitions[disk] = partitions;

        public WindowsStorageBatch ReadDisks() => throw new InvalidOperationException("Disks must not be read.");

        public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null, bool efiOnly = false) =>
            new(_partitions.TryGetValue(diskNumber ?? -1, out var rows) ? rows : [], Error);

        public WindowsStorageBatch ReadVolumes(char driveLetter) => throw new InvalidOperationException("Volumes must not be read.");

        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition, bool explicitParameters = false) =>
            throw new InvalidOperationException("Supported size must not be read.");
    }

    private sealed class BitLockerFake : IWindowsBitLockerReader
    {
        public WindowsStorageBatch ReadByDriveLetter(char driveLetter) => new([]);
        public Observation<BitLockerVolumeObservation> ReadExactVolume(VolumeObservation volume) =>
            Observations.Failure<BitLockerVolumeObservation>(ObservationAvailability.Unavailable, "fixture");
    }
}
