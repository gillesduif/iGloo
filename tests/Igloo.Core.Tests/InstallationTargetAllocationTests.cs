using Igloo.Core.Models;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class InstallationTargetAllocationTests
{
    private const long MiB = 1024 * 1024;
    private static GptDiskLayout Layout() => new()
    {
        DiskGuid = Guid.NewGuid(), LogicalSectorSize = 512, DiskSizeBytes = 128 * MiB,
        Partitions = [
            new() { PartitionGuid = Guid.NewGuid(), GptType = InstallationTargetValidation.EfiSystemType,
                OffsetBytes = MiB, LengthBytes = 4 * MiB },
            new() { PartitionGuid = Guid.NewGuid(), GptType = InstallationTargetValidation.LinuxFileSystemType,
                OffsetBytes = 20 * MiB, LengthBytes = 20 * MiB },
            new() { PartitionGuid = Guid.NewGuid(), GptType = InstallationTargetValidation.EfiSystemType,
                OffsetBytes = 80 * MiB, LengthBytes = 4 * MiB }],
    };

    [Fact]
    public void Offers_all_gaps_without_adopting_existing_linux_or_selecting_largest()
    {
        var gaps = InstallationTargetAllocation.GetFreeExtents(Layout(), MiB);
        Assert.Equal(new[] { new InstallationFreeExtent(5 * MiB, 15 * MiB),
            new InstallationFreeExtent(40 * MiB, 40 * MiB),
            new InstallationFreeExtent(84 * MiB, 43 * MiB) }, gaps);
    }

    [Fact]
    public void Explicit_smaller_gap_and_second_esp_remain_selected()
    {
        var layout = Layout();
        var selected = InstallationTargetAllocation.GetFreeExtents(layout, MiB)[0];
        var request = InstallationTargetAllocation.Authorize(Guid.NewGuid(), layout, selected,
            8 * MiB, layout.Partitions[2].PartitionGuid);
        Assert.Equal(5 * MiB, request.RootOffsetBytes);
        Assert.Equal(8 * MiB, request.RootLengthBytes);
        Assert.Equal(layout.Partitions[2].PartitionGuid, request.EspPartitionGuid);
        Assert.NotSame(layout.Partitions, request.ExpectedLayout.Partitions);
    }

    [Fact]
    public void Changed_gap_cannot_be_substituted_for_old_selection()
    {
        var layout = Layout();
        var gap = InstallationTargetAllocation.GetFreeExtents(layout, MiB)[0];
        var changed = layout with { Partitions = layout.Partitions.Select((p, i) =>
            i == 1 ? p with { OffsetBytes = 16 * MiB } : p).ToArray() };
        Assert.Throws<InvalidDataException>(() => InstallationTargetAllocation.Authorize(
            Guid.NewGuid(), changed, gap, 8 * MiB, layout.Partitions[0].PartitionGuid));
    }

    [Fact]
    public void Missing_esp_and_unaligned_or_oversized_requests_fail()
    {
        var layout = Layout();
        var gap = InstallationTargetAllocation.GetFreeExtents(layout, MiB)[0];
        Assert.Throws<InvalidDataException>(() => InstallationTargetAllocation.Authorize(
            Guid.NewGuid(), layout, gap, MiB, Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(() => InstallationTargetAllocation.Authorize(
            Guid.NewGuid(), layout, gap, MiB + 512, layout.Partitions[0].PartitionGuid));
        Assert.Throws<InvalidDataException>(() => InstallationTargetAllocation.Authorize(
            Guid.NewGuid(), layout, gap, 16 * MiB, layout.Partitions[0].PartitionGuid));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void Partition_order_and_sector_size_do_not_change_offered_extents(int sectorSize)
    {
        var layout = Layout();
        var reordered = layout with { LogicalSectorSize = sectorSize,
            Partitions = layout.Partitions.Reverse().ToArray() };
        Assert.Equal(InstallationTargetAllocation.GetFreeExtents(layout, MiB),
            InstallationTargetAllocation.GetFreeExtents(reordered, MiB));
    }
}
