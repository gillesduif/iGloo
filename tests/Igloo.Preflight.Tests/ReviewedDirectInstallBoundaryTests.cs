using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Igloo.Preflight;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class ReviewedDirectInstallBoundaryTests
{
    private const ulong MiB = PreparationSpacePlanning.Alignment;

    [Fact]
    public async Task ReviewedWindowsEntryChecksBoundSourceAndMediaThenStopsAtDedicatedEspGate()
    {
        var plan = Plan();
        var media = Spec(copyIso: false);
        Assert.Equal(ObservationAvailability.Available,
            DirectInstallService.ReviewPreparedIntent(plan, media, 8000 * (long)MiB).Availability);
        var failure = await Assert.ThrowsAsync<NotSupportedException>(() =>
            DirectInstallService.PrepareReviewedAsync(plan, media, 8000 * (long)MiB));
        Assert.Contains("Dedicated iGloo/Linux ESP preparation is blocked", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewedWindowsEntryRejectsMediaOrSourceChangeBeforeAnyMutation()
    {
        var plan = Plan();
        var tooLarge = Spec(copyIso: true);
        Assert.Equal("ReviewedMediaSizingMismatch",
            DirectInstallService.ReviewPreparedIntent(plan, tooLarge, 8000 * (long)MiB).Code);
        var changed = plan with { ReviewedIntent = plan.ReviewedIntent! with
            { ShrinkSource = plan.ReviewedIntent.ShrinkSource with { PartitionGuid = Guid.NewGuid() } } };
        Assert.NotEqual(ObservationAvailability.Available,
            DirectInstallService.ReviewPreparedIntent(changed, tooLarge, 8000 * (long)MiB).Availability);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            DirectInstallService.PrepareReviewedAsync(changed, tooLarge, 8000 * (long)MiB));
    }

    private static PreparationPlanV1 Plan()
    {
        var disk = new CanonicalDiskIdentityV1("eui.fixture", 8, 17, Guid.NewGuid(), 100000 * MiB, 512, 4096);
        var esp = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(),
            PreparationSpacePlanning.EspType, MiB, 260 * MiB, "FAT32");
        var source = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(),
            PreparationSpacePlanning.BasicDataType, 261 * MiB, 59739 * MiB, "NTFS");
        var space = PreparationSpacePlanning.Plan(new(40000 * MiB, 60000 * MiB,
            8000 * MiB, 1000 * MiB, 0, 4096, true)).Value;
        return new PreparationPlanV1(1, Guid.NewGuid(), disk, esp,
            [esp.PartitionGuid, source.PartitionGuid], space)
        {
            ReviewedIntent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new string('A', 64), new string('B', 64),
                new(5UL * 1024 * 1024 * 1024, 100 * MiB, 100 * MiB, 100 * MiB), source,
                [PreparedStorageOwnership.PartitionOf(esp), PreparedStorageOwnership.PartitionOf(source)],
                20000 * MiB, 39739 * MiB, 40000 * MiB, 60000 * MiB),
        };
    }

    private static InstallerBootSpec Spec(bool copyIso) => new()
    {
        MenuTitle = "Fixture", KernelCmdline = "fixture", CopyFullIsoToVolume = copyIso,
    };
}
