using Igloo.Core.Abstractions;
using Igloo.Preflight;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class DirectInstallSpaceEstimateTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;
    private static InstallerBootSpec Spec => new()
    {
        MenuTitle = "Fixture", KernelCmdline = "fixture", CopyFullIsoToVolume = true,
    };

    [Fact]
    public void ReportedVmCapacityReservesInstallerAndPendingWindowsStagingCopies()
    {
        var estimate = DirectInstallService.PlanSpaceEstimate(Spec, 28954 * MiB,
            3635 * MiB, 11 * MiB, 138 * MiB, 0, 414 * MiB);

        Assert.Equal(4710 * MiB, estimate.InstallerBytes);
        Assert.Equal(414 * MiB, estimate.StagingBytes);
        Assert.Equal(23, (estimate.ShrinkableBytes - estimate.InstallerBytes -
            estimate.StagingBytes) / GiB);
        var priorSelection = DirectInstallService.PlanStorageSizes(Spec, 27 * GiB,
            3635 * MiB, 11 * MiB, 138 * MiB, 0, 414 * MiB);
        Assert.Equal(32358 * MiB, priorSelection.TotalShrinkBytes);
        Assert.True(priorSelection.TotalShrinkBytes > estimate.ShrinkableBytes);
    }

    [Fact]
    public void LargeIsoIncludesBothInstallerPartitionsAndUsesWriterArithmetic()
    {
        var estimate = DirectInstallService.PlanSpaceEstimate(Spec, 50 * GiB,
            6 * GiB, 12 * MiB, 150 * MiB, 0, 2 * GiB);
        var writer = DirectInstallService.PlanStorageSizes(Spec, 25 * GiB,
            6 * GiB, 12 * MiB, 150 * MiB, 0, 2 * GiB);

        Assert.True(writer.IsoOnOwnPartition);
        Assert.Equal(writer.OemDrvBytes + writer.IsoPartitionBytes, estimate.InstallerBytes);
        Assert.Equal(writer.TotalShrinkBytes - 25 * GiB, estimate.InstallerBytes);
        Assert.Equal(2 * GiB, estimate.StagingBytes);
    }

    [Fact]
    public void InstallerThatDoesNotCopyFullIsoReservesOnlyDeclaredPayload()
    {
        var spec = Spec with { CopyFullIsoToVolume = false };
        var estimate = DirectInstallService.PlanSpaceEstimate(spec, 50 * GiB,
            6 * GiB, 12 * MiB, 100 * MiB, 850 * MiB, 25 * MiB);

        Assert.Equal((12 + 100 + 850 + 25 + 512) * MiB, estimate.InstallerBytes);
        Assert.Equal(25 * MiB, estimate.StagingBytes);
    }

    [Fact]
    public void LargestFreeExtentIgnoresAlignmentGapsAndFindsTheRemovedLinuxInstall()
    {
        // The 25 October lab disk after the Debian root was removed: Windows, the old
        // seed and Debian's boot partition sit in front of the 46 GiB gap, swap and
        // recovery behind it. The gap - not Windows' remaining shrink room - is the
        // space the next install should use.
        const long diskSize = 128L * GiB;
        var partitions = new List<(long Offset, long Size)>
        {
            (1 * MiB, 300 * MiB),                 // recovery
            (301 * MiB, 100 * MiB),               // Windows ESP
            (401 * MiB, 68 * GiB),                // C:
            (401 * MiB + 68 * GiB, 8 * GiB),      // old OEMDRV
            (401 * MiB + 76 * GiB, 977 * MiB),    // Debian boot
            // 46 GiB gap
            (diskSize - 4 * GiB, 2 * GiB + 600 * MiB), // swap
            (diskSize - GiB, 892 * MiB),          // recovery
        };
        var largest = DirectInstallService.LargestFreeExtentBytes(partitions, diskSize);
        var expectedGap = (diskSize - 4 * GiB) - (401 * MiB + 76 * GiB + 977 * MiB) - MiB;
        Assert.Equal(expectedGap, largest);
        Assert.True(largest > 40L * GiB);

        // A fully allocated disk reports nothing; the 1 MiB leading gap is alignment, not space.
        Assert.Equal(0, DirectInstallService.LargestFreeExtentBytes(
            [(1 * MiB, diskSize - 1 * MiB)], diskSize));
        // Trailing free space counts.
        Assert.Equal(20 * GiB - MiB, DirectInstallService.LargestFreeExtentBytes(
            [(1 * MiB, diskSize - 20 * GiB - 1 * MiB)], diskSize));
        Assert.Equal(0, DirectInstallService.LargestFreeExtentBytes([], 0));
    }
}
