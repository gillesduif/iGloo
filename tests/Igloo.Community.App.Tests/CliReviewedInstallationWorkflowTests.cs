using System.IO;
using Igloo.Community.App;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Preparation;
using Igloo.Preflight;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class CliReviewedInstallationWorkflowTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    [Theory]
    [InlineData("debian", 3L * GiB, true, 0L)]
    [InlineData("linuxmint-cinnamon", 5L * GiB, true, 0L)]
    [InlineData("fedora-kde", 3L * GiB, false, 2L * GiB)]
    public void Allocation_requests_the_complete_reviewed_shrink_not_just_Linux_root(
        string distroId, long isoBytes, bool copyFullIso, long stage2Bytes)
    {
        const long rootBytes = 50 * GiB;
        var spec = new InstallerBootSpec
        {
            MenuTitle = distroId,
            KernelCmdline = "fixture",
            CopyFullIsoToVolume = copyFullIso,
        };
        var media = new ReviewedMediaMeasurementsV1((ulong)isoBytes,
            17UL * (ulong)MiB, 89UL * (ulong)MiB, (ulong)stage2Bytes);
        var sizing = ReviewedMediaInspector.PlanStorageSizes(spec, rootBytes,
            media, stagedBytes: 2 * GiB);
        var measured = new ReviewedInstallationShrinkV1(checked(sizing.TotalShrinkBytes +
            (long)PreparationSpacePlanning.LinuxEspBytes +
            (long)PreparationSpacePlanning.Alignment), sizing, 2 * GiB,
            new string('A', 64));
        var planId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var input = CliReviewedInstallationWorkflow.AllocationInput(planId,
            targetId, rootBytes, measured);

        Assert.Equal(planId, input.PlanId);
        Assert.Equal(targetId, input.TargetId);
        Assert.Equal(measured.MinimumShrinkBytes, input.RequestedShrinkBytes);
        Assert.True(input.RequestedShrinkBytes > rootBytes + GiB);
    }

    [Fact]
    public void Allocation_rejects_a_root_only_measurement()
    {
        const long rootBytes = 50 * GiB;
        var measured = new ReviewedInstallationShrinkV1(rootBytes,
            new ReviewedInstallerSizingV1(false, 1 * GiB, 0, rootBytes),
            1, new string('A', 64));

        Assert.Throws<InvalidDataException>(() =>
            CliReviewedInstallationWorkflow.AllocationInput(Guid.NewGuid(),
                Guid.NewGuid(), rootBytes, measured));
    }
}
