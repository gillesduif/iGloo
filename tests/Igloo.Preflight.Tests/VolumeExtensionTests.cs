using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class VolumeExtensionTests
{
    private const long MiB = 1024L * 1024;
    private const long DiskBytes = 68719476736;

    // Offsets and sizes of the VMware guest of 14 November.
    private static readonly (long Offset, long Size) Esp = (1048576, 209715200);
    private static readonly (long Offset, long Size) Reserved = (210763776, 16777216);
    private static readonly (long Offset, long Size) Windows = (227540992, 41649438720);
    private static readonly (long Offset, long Size) DebianEsp = (46306164736, 1013972992);
    private static readonly (long Offset, long Size) Recovery = (67781001216, 936378368);

    private static WindowsStorageRow[] Table(params (long Offset, long Size)[] partitions) =>
        [.. partitions.Select(partition => new WindowsStorageRow(ImmutableDictionary.CreateRange(
            [KeyValuePair.Create("Offset", (object?)(ulong)partition.Offset),
             KeyValuePair.Create("Size", (object?)(ulong)partition.Size)])))];

    [Fact]
    public void The_space_of_the_removed_installer_partition_follows_Windows()
    {
        var free = LinuxRemovalService.AdjacentFreeBytes(Table(Esp, Reserved, Windows, DebianEsp, Recovery),
            Windows.Offset, Windows.Size, DiskBytes);

        Assert.Equal(4224 * MiB, free);
    }

    [Fact]
    public void After_the_Linux_partitions_are_gone_the_space_runs_to_the_recovery_partition()
    {
        var grown = Windows.Size + 4224 * MiB;

        var free = LinuxRemovalService.AdjacentFreeBytes(Table(Esp, Reserved, (Windows.Offset, grown), Recovery),
            Windows.Offset, grown, DiskBytes);

        Assert.Equal(20L * 1024 * MiB, free);
    }

    [Fact]
    public void Without_a_later_partition_the_space_ends_before_the_backup_table()
    {
        var free = LinuxRemovalService.AdjacentFreeBytes(Table(Esp, Reserved, Windows),
            Windows.Offset, Windows.Size, DiskBytes);

        Assert.Equal(DiskBytes - MiB - (Windows.Offset + Windows.Size), free);
    }

    [Fact]
    public void A_partition_that_starts_where_the_volume_ends_leaves_nothing_and_a_fraction_of_a_MiB_does_not_count()
    {
        var end = Windows.Offset + Windows.Size;

        Assert.Equal(0, LinuxRemovalService.AdjacentFreeBytes(Table(Windows, (end, 4224 * MiB)),
            Windows.Offset, Windows.Size, DiskBytes));
        Assert.Equal(0, LinuxRemovalService.AdjacentFreeBytes(Table(Windows, (end + MiB - 512, 4224 * MiB)),
            Windows.Offset, Windows.Size, DiskBytes));
        Assert.Equal(3 * MiB, LinuxRemovalService.AdjacentFreeBytes(Table(Windows, (end + 3 * MiB + 4096, 4224 * MiB)),
            Windows.Offset, Windows.Size, DiskBytes));
    }
}
