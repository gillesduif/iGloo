using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class ReviewedPreparationIntentTests
{
    private const ulong MiB = PreparationSpacePlanning.Alignment;

    [Fact]
    public void SameExtentAndSizesProduceSameSemanticPartitionPlan()
    {
        var input = new PreparationSpaceRequestV1(40000 * MiB, 60000 * MiB,
            8000 * MiB, 1000 * MiB, 0, 4096, true);
        var first = PreparationSpacePlanning.Plan(input).Value;
        var second = PreparationSpacePlanning.Plan(input).Value;
        Assert.Equal(first.AlignmentPaddingBytes, second.AlignmentPaddingBytes);
        Assert.Equal(first.RequiredBytes, second.RequiredBytes);
        Assert.Equal(first.Allocations.ToArray(), second.Allocations.ToArray());
    }

    [Fact]
    public void ReviewedPlanBindsExactSourcePrelayoutAndPostShrinkExtent()
    {
        var plan = Create();
        Assert.Equal(ObservationAvailability.Available, PreparedLayoutRules.VerifyReviewedIntent(plan).Availability);
        Assert.Equal(plan.ReviewedIntent!.FreeOffsetBytes, plan.Space.Allocations[0].OffsetBytes);
        Assert.Equal(PreparationRole.LinuxEsp, plan.Space.Allocations[0].Role);
        Assert.Equal(PreparationRole.LinuxRoot, plan.Space.Allocations[^1].Role);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("allocation")]
    [InlineData("artifact")]
    [InlineData("provider")]
    [InlineData("source")]
    [InlineData("prelayout")]
    [InlineData("geometry")]
    [InlineData("extent")]
    public void ChangedReviewedFactIsRefused(string change)
    {
        var plan = Create();
        var intent = plan.ReviewedIntent!;
        intent = change switch
        {
            "target" => intent with { TargetId = Guid.Empty },
            "allocation" => intent with { AllocationId = Guid.Empty },
            "artifact" => intent with { ArtifactSha256 = "bad" },
            "provider" => intent with { ProviderRevisionSha256 = "bad" },
            "source" => intent with { ShrinkSource = intent.ShrinkSource with { PartitionGuid = Guid.NewGuid() } },
            "prelayout" => intent with { BeforePartitions = intent.BeforePartitions.RemoveAt(1) },
            "geometry" => intent with { ResultingSourceBytes = intent.ResultingSourceBytes - MiB },
            "extent" => intent with { FreeOffsetBytes = intent.FreeOffsetBytes + MiB },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.NotEqual(ObservationAvailability.Available,
            PreparedLayoutRules.VerifyReviewedIntent(plan with { ReviewedIntent = intent }).Availability);
    }

    [Fact]
    public void LegacyPlanCannotBePromotedToReviewedExecution()
    {
        var plan = Create() with { ReviewedIntent = null };
        Assert.True(PreparedLayoutRules.IsValidPlan(plan));
        Assert.NotEqual(ObservationAvailability.Available, PreparedLayoutRules.VerifyReviewedIntent(plan).Availability);
    }

    [Fact]
    public void ShrinkReceiptRequiresProviderResultAndIndependentExactReadback()
    {
        var plan = Create();
        var expected = plan.ReviewedIntent!.ShrinkSource with
        { SizeBytes = plan.ReviewedIntent.ResultingSourceBytes };
        var accepted = PreparedLayoutRules.VerifyShrink(plan, Observations.Available(expected),
            Observations.Available(expected));
        Assert.Equal(ObservationAvailability.Available, accepted.Availability);
        Assert.Equal(plan.GenerationId, accepted.Value.GenerationId);
        var moved = expected with { OffsetBytes = expected.OffsetBytes + MiB };
        Assert.Equal("ReviewedShrinkReceiptMismatch", PreparedLayoutRules.VerifyShrink(plan,
            Observations.Available(expected), Observations.Available(moved)).Code);
        Assert.Equal("ReviewedShrinkReadbackUnavailable", PreparedLayoutRules.VerifyShrink(plan,
            Observations.Available(expected), Observations.Failure<CanonicalVolumeIdentityV1>(
                ObservationAvailability.Unavailable, "probe")).Code);
    }

    [Fact]
    public void RecoveryFootprintListsExactStorageRolesButNeverClaimsReadiness()
    {
        var plan = Create();
        var footprint = PreparedLayoutRecoveryFootprint.Derive(plan);
        Assert.Equal(ObservationAvailability.Available, footprint.Availability);
        Assert.NotEmpty(footprint.Value.MissingProofCodes);
        Assert.Equal(plan.ReviewedIntent!.ShrinkSource.PartitionGuid,
            footprint.Value.Mutations.Single(m => m.Surface == PreparedMutationSurface.NtfsResize).PartitionGuid);
        Assert.Equal(plan.Space.Allocations.Select(a => a.Role),
            footprint.Value.Mutations.Where(m => m.Surface == PreparedMutationSurface.PartitionCreate)
                .Select(m => m.Role!.Value));
        Assert.Contains("PartitionTableRestorationUnproven", footprint.Value.MissingProofCodes);
        Assert.NotEqual(ObservationAvailability.Available,
            PreparedLayoutRecoveryFootprint.Derive(plan with { ReviewedIntent = null }).Availability);
    }

    internal static PreparationPlanV1 Create()
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
}
