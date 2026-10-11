using Igloo.Core.Abstractions;
using Igloo.Preflight;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class DirectInstallSizePlanTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    [Fact]
    public void SmallIsoStaysOnSeedAndContributesToTheSameShrinkUsedByInstaller()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture",
            CopyFullIsoToVolume = true };
        var sizes = DirectInstallService.PlanStorageSizes(spec, 50 * GiB,
            3 * GiB, 17 * MiB, 89 * MiB, 821 * MiB);

        Assert.False(sizes.IsoOnOwnPartition);
        Assert.Equal(3 * GiB + 17 * MiB + 89 * MiB + 821 * MiB + 512 * MiB,
            sizes.OemDrvBytes);
        Assert.Equal(0, sizes.IsoPartitionBytes);
        Assert.Equal(50 * GiB + sizes.OemDrvBytes, sizes.TotalShrinkBytes);
    }

    [Fact]
    public void LargeIsoGetsSeparateNtfsPartitionAtFat32SingleFileBoundary()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture",
            CopyFullIsoToVolume = true };
        var sizes = DirectInstallService.PlanStorageSizes(spec, 50 * GiB,
            4 * GiB, 17 * MiB, 89 * MiB, 821 * MiB);

        Assert.True(sizes.IsoOnOwnPartition);
        Assert.Equal(17 * MiB + 89 * MiB + 821 * MiB + 512 * MiB,
            sizes.OemDrvBytes);
        Assert.Equal(4 * GiB + 256 * MiB, sizes.IsoPartitionBytes);
        Assert.Equal(50 * GiB + sizes.OemDrvBytes + sizes.IsoPartitionBytes,
            sizes.TotalShrinkBytes);
    }

    [Fact]
    public void NonCopyingProviderDoesNotReserveIsoBytes()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture" };
        var sizes = DirectInstallService.PlanStorageSizes(spec, 50 * GiB,
            6 * GiB, 17 * MiB, 89 * MiB, 0);

        Assert.False(sizes.IsoOnOwnPartition);
        Assert.Equal(17 * MiB + 89 * MiB + 512 * MiB, sizes.OemDrvBytes);
        Assert.Equal(0, sizes.IsoPartitionBytes);
    }

    [Fact]
    public void UnrepresentableSizeFailsBeforeAnyStorageAction()
    {
        var spec = new InstallerBootSpec { MenuTitle = "fixture", KernelCmdline = "fixture",
            CopyFullIsoToVolume = true };
        Assert.Throws<OverflowException>(() => DirectInstallService.PlanStorageSizes(spec,
            long.MaxValue, 4 * GiB, 17 * MiB, 89 * MiB, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectInstallService.PlanStorageSizes(
            spec, -1, 0, 0, 0, 0));
    }
}
