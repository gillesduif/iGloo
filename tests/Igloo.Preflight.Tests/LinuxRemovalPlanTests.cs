using System.Collections.Immutable;
using System.Text;
using Igloo.Core.Abstractions;
using Xunit;
using Entry = Igloo.Preflight.EfiBootEntries.BootEntryTarget;

namespace Igloo.Preflight.Tests;

// Issue #160 and Beta Track B-01: a removal is decided on one layout and never reaches past the selected disks.
public sealed class LinuxRemovalPlanTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * 1024 * 1024;
    private const string EspType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    private const string MsrType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    private const string BasicType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    private const string RecoveryType = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
    private const string LinuxType = "{0fc63daf-8483-4772-8e79-3d69d8477de4}";
    private const string SwapType = "{0657fd6d-a4ab-43c4-84e5-0933c84b4f4f}";
    private const string LuksType = "{ca7d7ccb-63ed-4c53-861c-1742536059cc}";
    private const string ZfsType = "{6a898cc3-1dd2-11b2-99a6-080020736631}";

    private static readonly Guid Esp0 = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    private static readonly Guid Esp1 = Guid.Parse("a0000000-0000-4000-8000-000000000002");
    private static readonly Guid Seed0 = Guid.Parse("a0000000-0000-4000-8000-000000000003");
    private static readonly Guid Data2 = Guid.Parse("a0000000-0000-4000-8000-000000000004");
    private static readonly Guid Gone = Guid.Parse("a0000000-0000-4000-8000-0000000000ff");

    private static readonly WindowsStorageRow EspRow0 = Row(0, 1, MiB, 100 * MiB, EspType, Esp0);
    private static readonly WindowsStorageRow Msr0 = Row(0, 2, 101 * MiB, 16 * MiB, MsrType);
    private static readonly WindowsStorageRow Windows0 = Row(0, 3, 117 * MiB, 60 * GiB, BasicType);
    private static readonly WindowsStorageRow RootA = Row(0, 4, 61 * GiB, 30 * GiB, LinuxType);
    private static readonly WindowsStorageRow SwapA = Row(0, 5, 91 * GiB, 4 * GiB, SwapType);
    private static readonly WindowsStorageRow SeedRow0 = Row(0, 6, 95 * GiB, 4 * GiB, BasicType, Seed0);
    private static readonly WindowsStorageRow Recovery0 = Row(0, 7, 127 * GiB, GiB - MiB, RecoveryType);
    private static readonly WindowsStorageRow EspRow1 = Row(1, 1, MiB, 600 * MiB, EspType, Esp1);
    private static readonly WindowsStorageRow RootB = Row(1, 2, GiB, 200 * GiB, LinuxType);
    private static readonly WindowsStorageRow DataRow2 = Row(2, 1, MiB, 400 * GiB, BasicType, Data2);

    private static readonly WindowsStorageRow[] Layout =
        [EspRow0, Msr0, Windows0, RootA, SwapA, SeedRow0, Recovery0, EspRow1, RootB, DataRow2];

    private static readonly Entry[] Entries =
    [
        new(0x0000, "Windows Boot Manager", Esp0),
        new(0x0001, "debian", Esp0),
        new(0x0002, "Fedora", Esp1),
        new(0x0003, "ubuntu", Data2),
        new(0x0080, "iGloo distribution installer", Seed0),
        new(0x0081, "iGloo distribution installer", Data2),
        new(0x0082, "iGloo distribution installer", Gone),
        new(0x0083, "iGloo distribution installer", null),
        new(0x0004, "Linux Firmware Updater", Gone),
    ];

    private static LinuxInstallation InstallA(ushort? entry = null) => new("Debian", 0, "disk zero",
        [Reported(RootA, 4), Reported(SwapA, 5)], 34 * GiB, entry);

    private static LinuxInstallation InstallB(ushort? entry = null) => new("Fedora", 1, "disk one",
        [Reported(RootB, 2)], 200 * GiB, entry);

    [Fact]
    public void A_removal_never_plans_anything_on_a_disk_that_was_not_selected()
    {
        var plan = LinuxRemovalPlanner.Plan([InstallB()], [], removingAllLinux: false, Layout, Entries);

        Assert.Equal([(1U, RootB)], plan.Partitions.Select(p => (p.Disk, p.Row)));
        Assert.Equal(0, plan.UnmatchedPartitions);
        Assert.Empty(plan.BootEntries.Where(index => index != 0x0082));
        Assert.Empty(plan.BootCleanupDisks);
        Assert.Null(plan.SkippedBootCleanup);
    }

    [Fact]
    public void The_paired_entry_goes_with_its_installation_only_when_it_points_at_that_disk()
    {
        var paired = LinuxRemovalPlanner.Plan([InstallB(entry: 0x0002)], [], false, Layout, Entries);
        var pairedWithAnotherDisk = LinuxRemovalPlanner.Plan([InstallB(entry: 0x0001)], [], false, Layout, Entries);

        Assert.Contains((ushort)0x0002, paired.BootEntries);
        Assert.DoesNotContain((ushort)0x0001, pairedWithAnotherDisk.BootEntries);
        Assert.DoesNotContain((ushort)0x0002, pairedWithAnotherDisk.BootEntries);
    }

    [Fact]
    public void Removing_every_installation_cleans_only_the_disks_they_were_on()
    {
        var plan = LinuxRemovalPlanner.Plan([InstallA(), InstallB()], [], removingAllLinux: true, Layout, Entries);

        Assert.Equal([SwapA, RootA, RootB], plan.Partitions.Select(p => p.Row));
        Assert.Equal([0U, 1U], plan.BootCleanupDisks);
        Assert.Null(plan.SkippedBootCleanup);
        // Chosen by device path: the "ubuntu" entry on disk 2 and the dangling updater entry stay.
        Assert.Equal(new ushort[] { 0x0001, 0x0002, 0x0080, 0x0082 }, plan.BootEntries.Order());
    }

    [Theory]
    [InlineData(ZfsType, "is of a type iGloo does not recognise")]
    [InlineData(LuksType, "is a Linux partition outside this removal")]
    public void A_partition_iGloo_cannot_place_keeps_the_boot_files_and_linux_entries(string gptType, string reason)
    {
        var stranger = Row(2, 2, 401 * GiB, 50 * GiB, gptType);

        var plan = LinuxRemovalPlanner.Plan([InstallA(entry: 0x0001), InstallB(entry: 0x0002)], [], true,
            [.. Layout, stranger], Entries);

        Assert.Equal(3, plan.Partitions.Count);
        Assert.Empty(plan.BootCleanupDisks);
        Assert.Equal("partition 2 on disk 2 " + reason, plan.SkippedBootCleanup);
        // A known Linux type still lets the paired entries go; an unknown type keeps every Linux entry.
        Assert.Equal(gptType == LuksType ? new ushort[] { 0x0001, 0x0002, 0x0080, 0x0082 } : [0x0080, 0x0082],
            plan.BootEntries.Order());
    }

    [Fact]
    public void A_reported_partition_that_is_no_longer_there_is_not_deleted()
    {
        var moved = InstallA() with { Partitions = [Reported(RootA, 4) with { OffsetBytes = 62 * GiB }, Reported(SwapA, 5)] };
        var retyped = InstallB() with { Partitions = [Reported(RootB, 2) with { GptType = BasicType }] };

        var plan = LinuxRemovalPlanner.Plan([moved, retyped], [], removingAllLinux: true, Layout, Entries);

        Assert.Equal([SwapA], plan.Partitions.Select(p => p.Row));
        Assert.Equal(2, plan.UnmatchedPartitions);
        Assert.Empty(plan.BootCleanupDisks);
        Assert.Equal("a reported Linux partition is no longer where System Check found it", plan.SkippedBootCleanup);
        Assert.Equal(new ushort[] { 0x0080, 0x0082 }, plan.BootEntries.Order());
    }

    [Fact]
    public void An_iGloo_entry_goes_with_its_seed_or_when_its_partition_is_gone()
    {
        var plan = LinuxRemovalPlanner.Plan([], [(0U, SeedRow0)], removingAllLinux: false, Layout, Entries);

        Assert.Equal([SeedRow0], plan.Partitions.Select(p => p.Row));
        Assert.Equal(new ushort[] { 0x0080, 0x0082 }, plan.BootEntries.Order());
        Assert.Empty(plan.BootCleanupDisks);
    }

    [Fact]
    public void Windows_and_linux_partition_types_are_told_apart_from_the_rest()
    {
        foreach (var type in new[] { EspType, MsrType, BasicType, RecoveryType })
            Assert.True(LinuxRemovalPlanner.IsWindowsPartition(Row(0, 1, MiB, GiB, type)));
        Assert.False(LinuxRemovalPlanner.IsWindowsPartition(Row(0, 1, MiB, GiB, ZfsType)));
        Assert.False(LinuxRemovalPlanner.IsWindowsPartition(Row(0, 1, MiB, GiB, LinuxType)));
        Assert.True(LinuxRemovalPlanner.IsLinuxPartition(Row(0, 1, MiB, GiB, LuksType)));
        Assert.True(LinuxRemovalPlanner.IsWindowsPartition(MbrRow(0x07)));
        Assert.False(LinuxRemovalPlanner.IsWindowsPartition(MbrRow(0x83)));
        Assert.False(LinuxRemovalPlanner.IsLinuxPartition(MbrRow(0x83)));
        Assert.False(LinuxRemovalPlanner.IsWindowsPartition(MbrRow(null)));
    }

    [Fact]
    public void The_partition_of_a_boot_entry_is_read_from_its_hard_drive_node()
    {
        Assert.Equal(Seed0, EfiBootEntries.ParsePartitionGuid(LoadOption("iGloo distribution installer", Seed0, signatureType: 2)));
        Assert.Null(EfiBootEntries.ParsePartitionGuid(LoadOption("legacy", Seed0, signatureType: 1)));
        Assert.Null(EfiBootEntries.ParsePartitionGuid(LoadOption("no disk", null, signatureType: 2)));
        Assert.Null(EfiBootEntries.ParsePartitionGuid(LoadOption("cut", Seed0, signatureType: 2)[..40]));
        Assert.Null(EfiBootEntries.ParsePartitionGuid([1, 0, 0, 0]));
    }

    [Fact]
    public void Boot_entries_are_enumerated_with_their_target_partition()
    {
        var firmware = new FirmwareFake
        {
            Entries =
            {
                [0x0001] = LoadOption("debian", Esp0, 2),
                [0x0080] = LoadOption("iGloo distribution installer", Seed0, 2),
                [0x0090] = LoadOption("PXE", null, 2),
            },
        };

        var targets = EfiBootEntries.EnumerateTargetsObserved(firmware).ToList();

        List<Entry> expected = [new(0x0001, "debian", Esp0), new(0x0080, "iGloo distribution installer", Seed0),
            new(0x0090, "PXE", null)];
        Assert.Equal(expected, targets);
    }

    private static PartitionInfo Reported(WindowsStorageRow row, int index) => new(index, "Unknown",
        Convert.ToInt64(row["Size"], System.Globalization.CultureInfo.InvariantCulture), null, false, false, 0,
        Convert.ToInt64(row["Offset"], System.Globalization.CultureInfo.InvariantCulture), (string?)row["GptType"]);

    private static WindowsStorageRow Row(uint disk, uint number, long offset, long size, string gptType, Guid? guid = null) =>
        new(new Dictionary<string, object?>
        {
            ["DiskNumber"] = disk, ["PartitionNumber"] = number, ["Offset"] = (ulong)offset, ["Size"] = (ulong)size,
            ["GptType"] = gptType, ["Guid"] = (guid ?? Guid.NewGuid()).ToString("B"),
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));

    private static WindowsStorageRow MbrRow(ushort? mbrType) =>
        new(new Dictionary<string, object?>
        {
            ["DiskNumber"] = 3U, ["PartitionNumber"] = 1U, ["Offset"] = (ulong)MiB, ["Size"] = (ulong)GiB,
            ["GptType"] = null, ["Guid"] = null, ["MbrType"] = mbrType,
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));

    private static byte[] LoadOption(string description, Guid? partition, byte signatureType)
    {
        using var path = new MemoryStream();
        if (partition is { } id)
        {
            path.Write([4, 1, 42, 0]);
            path.Write(BitConverter.GetBytes(4U));
            path.Write(BitConverter.GetBytes(2048UL));
            path.Write(BitConverter.GetBytes(8388608UL));
            path.Write(id.ToByteArray());
            path.Write([2, signatureType]);
        }
        var file = Encoding.Unicode.GetBytes("\\EFI\\BOOT\\BOOTX64.EFI\0");
        path.Write([4, 4]);
        path.Write(BitConverter.GetBytes((ushort)(4 + file.Length)));
        path.Write(file);
        path.Write([0x7F, 0xFF, 4, 0]);

        using var option = new MemoryStream();
        option.Write(BitConverter.GetBytes(1U));
        option.Write(BitConverter.GetBytes((ushort)path.Length));
        option.Write(Encoding.Unicode.GetBytes(description + "\0"));
        option.Write(path.ToArray());
        return option.ToArray();
    }

    private sealed class FirmwareFake : IWindowsFirmwareReader
    {
        public Dictionary<ushort, byte[]> Entries { get; } = [];

        public FirmwareVariableObservation ReadBootOrder(int bufferBytes = 4096) => new(ImmutableArray.Create<byte>(0x01, 0x00), 0);

        public FirmwareVariableObservation ReadBootEntry(ushort index, int bufferBytes = 4096) =>
            Entries.TryGetValue(index, out var raw) ? new(ImmutableArray.Create(raw), 0) : new(null, 203);
    }
}
