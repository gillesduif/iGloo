using System.Collections.Immutable;
using Igloo.Core.Abstractions;
using Igloo.Core.Preparation;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class PreparedLayoutTests
{
    private const ulong MiB = PreparationSpacePlanning.Alignment;

    [Fact]
    public void DiskHeaderCannotBeUsedAsThePreparationExtent() =>
        Assert.Equal("PreparationExtentIncludesDiskHeader", PreparationSpacePlanning.Plan(new(0, 2000 * MiB, MiB, MiB, 0, 512, false)).Code);

    [Fact]
    public void TamperedAccountingOrWindowsEspOverlapInvalidatesPlan()
    {
        var plan = Plan();
        Assert.False(PreparedLayoutRules.IsValidPlan(plan with { Space = plan.Space with { RequiredBytes = plan.Space.RequiredBytes - MiB } }));
        var overlapping = plan.Space.Allocations.SetItem(0, plan.Space.Allocations[0] with { OffsetBytes = plan.WindowsEsp.OffsetBytes });
        Assert.False(PreparedLayoutRules.IsValidPlan(plan with { Space = plan.Space with { Allocations = overlapping } }));
    }

    [Theory]
    [InlineData(0UL, false, 3)]
    [InlineData(4096UL, false, 4)]
    [InlineData(4096UL, true, 4)]
    public void SpaceIncludesPermanentEspPayloadIsoAndLinux(ulong isoMiB, bool root, int count)
    {
        var result = PreparationSpacePlanning.Plan(new(MiB, 20000 * MiB, 8000 * MiB, 1000 * MiB, isoMiB * MiB, 4096, root));
        Assert.Equal(ObservationAvailability.Available, result.Availability);
        Assert.Equal((1024 + 8000 + 1000 + isoMiB) * MiB, result.Value.RequiredBytes);
        Assert.Equal(count, result.Value.Allocations.Length);
        Assert.Equal(root, result.Value.Allocations.Single(a => a.Role == PreparationRole.LinuxRoot).CreateInWindows);
        Assert.Equal(PreparationSpacePlanning.EspType, result.Value.Allocations[0].PartitionType);
        Assert.Equal("FAT32", result.Value.Allocations[0].FileSystem);
    }

    [Theory]
    [InlineData(0, ObservationAvailability.Available)]
    [InlineData(-1, ObservationAvailability.Unavailable)]
    [InlineData(1, ObservationAvailability.Available)]
    public void ExactCapacityBoundaryIsChecked(long delta, ObservationAvailability expected)
    {
        var available = (ulong)((1024L + 8 + 4) * (long)MiB + delta);
        Assert.Equal(expected, PreparationSpacePlanning.Plan(new(MiB, available, 8 * MiB, 4 * MiB, 0, 512, false)).Availability);
    }

    [Fact]
    public void AlignmentPaddingAndEachRoundedAllocationAreIncluded()
    {
        var result = PreparationSpacePlanning.Plan(new(MiB + 1, 2048 * MiB, MiB + 1, MiB + 1, 1, 4096, false)).Value;
        Assert.Equal(MiB - 1, result.AlignmentPaddingBytes);
        Assert.Equal((1024 + 2 + 2 + 1) * MiB + MiB - 1, result.RequiredBytes);
        Assert.All(result.Allocations, a => Assert.Equal(0UL, a.OffsetBytes % MiB));
    }

    [Theory]
    [InlineData(ulong.MaxValue, 1UL, 1UL)]
    [InlineData(1UL, ulong.MaxValue, 1UL)]
    [InlineData(1UL, 1UL, ulong.MaxValue)]
    public void OverflowFailsClosed(ulong offset, ulong available, ulong linux)
    {
        var observed = PreparationSpacePlanning.Plan(new(offset, available, linux, 1, 0, 512, false));
        Assert.Equal(ObservationAvailability.Ambiguous, observed.Availability);
        Assert.Equal("PreparationSpaceOverflow", observed.Code);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(3000U)]
    public void UnsupportedAlignmentCannotStartAPlan(uint sectorSize) =>
        Assert.Equal(ObservationAvailability.Ambiguous,
            PreparationSpacePlanning.Plan(new(MiB, 2000 * MiB, MiB, MiB, 0, sectorSize, false)).Availability);

    [Fact]
    public void ExistingAllocationsReceiveNoUnprovenReuseCredit()
    {
        // Only the actual remaining extent counts; prior partitions are not reusable by label.
        var result = PreparationSpacePlanning.Plan(new(5000 * MiB, 1024 * MiB, MiB, MiB, 0, 512, false));
        Assert.Equal(ObservationAvailability.Unavailable, result.Availability);
    }

    [Fact]
    public void ProviderResultAndFreshReadbackBindCreatedEsp()
    {
        var plan = Plan(); var identity = Partition(plan, PreparationRole.LinuxEsp);
        var created = PreparedLayoutRules.VerifyCreation(plan, PreparationRole.LinuxEsp, A(identity), A(identity));
        Assert.Equal(ObservationAvailability.Available, created.Availability);
        Assert.NotEqual(plan.WindowsEsp.PartitionGuid, created.Value.Identity.PartitionGuid);
        Assert.Equal(PreparationSpacePlanning.EspType, created.Value.Identity.PartitionType);
    }

    [Theory]
    [InlineData("partition")]
    [InlineData("disk")]
    [InlineData("volume")]
    [InlineData("type")]
    [InlineData("filesystem")]
    [InlineData("offset")]
    public void ChangedCreationIdentityRejected(string change)
    {
        var plan = Plan(); var original = Partition(plan, PreparationRole.LinuxEsp);
        var changed = Change(original, change);
        Assert.Equal(ObservationAvailability.Ambiguous,
            PreparedLayoutRules.VerifyCreation(plan, PreparationRole.LinuxEsp, A(original), A(changed)).Availability);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("filesystem")]
    [InlineData("offset")]
    [InlineData("disk")]
    public void TwoMatchingReadsDoNotProveWrongAllocation(string change)
    {
        var plan = Plan(); var wrong = Change(Partition(plan, PreparationRole.LinuxEsp), change);
        Assert.Equal(ObservationAvailability.Ambiguous,
            PreparedLayoutRules.VerifyCreation(plan, PreparationRole.LinuxEsp, A(wrong), A(wrong)).Availability);
    }

    [Fact]
    public void WindowsEspNeverBecomesAnOwnedAllocation()
    {
        var plan = Plan();
        Assert.Equal(ObservationAvailability.Ambiguous,
            PreparedLayoutRules.VerifyCreation(plan, PreparationRole.LinuxEsp, A(plan.WindowsEsp), A(plan.WindowsEsp)).Availability);
        var alias = Partition(plan, PreparationRole.LinuxEsp) with { PartitionGuid = plan.WindowsEsp.PartitionGuid };
        Assert.Equal(ObservationAvailability.Ambiguous,
            PreparedLayoutRules.VerifyCreation(plan, PreparationRole.LinuxEsp, A(alias), A(alias)).Availability);
    }

    [Theory]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Absent)]
    public void ObservationFailuresNeverBecomeOwnership(ObservationAvailability state)
    {
        var plan = Plan();
        Assert.Equal(state, PreparedLayoutRules.VerifyCreation(plan, PreparationRole.LinuxEsp,
            Observations.Failure<CanonicalVolumeIdentityV1>(state, "provider"), A(Partition(plan, PreparationRole.LinuxEsp))).Availability);
    }

    [Fact]
    public void InterruptedCreationWithoutDurableReceiptIsAmbiguous()
    {
        var plan = Plan();
        var observed = PreparedLayoutRules.AssessOwnership(plan, plan.GenerationId, [], [Partition(plan, PreparationRole.LinuxEsp)], plan.WindowsEsp);
        Assert.Equal(PreparationState.AmbiguousLeftover, observed.State);
        Assert.Equal("UnreceiptedPreparationPartition", observed.Code);
    }

    [Fact]
    public void PlannedAbsentAllocationsAreNotCreatedOrReady()
    {
        var plan = Plan();
        Assert.Equal(PreparationState.Planned, PreparedLayoutRules.AssessOwnership(plan, plan.GenerationId, [], [], plan.WindowsEsp).State);
    }

    [Fact]
    public void ReopenedReceiptMatchesOnlyItsExactGenerationAndPartitionIdentities()
    {
        var plan = Plan(); var receipts = Receipts(plan);
        var current = receipts.Select(r => r.Identity).ToImmutableArray();
        Assert.Equal(PreparationState.CreatedAndVerified, PreparedLayoutRules.AssessOwnership(plan, plan.GenerationId, receipts, current, plan.WindowsEsp).State);
        Assert.Equal(PreparationState.OwnershipUnavailable, PreparedLayoutRules.AssessOwnership(plan, Guid.NewGuid(), receipts, current, plan.WindowsEsp).State);
        Assert.Equal(PreparationState.AmbiguousLeftover, PreparedLayoutRules.AssessOwnership(plan, plan.GenerationId, receipts, current.Add(current[0]), plan.WindowsEsp).State);
    }

    [Theory]
    [InlineData("partition")]
    [InlineData("type")]
    [InlineData("offset")]
    [InlineData("volume")]
    [InlineData("filesystem")]
    public void WindowsEspPreservationIsReverified(string change)
    {
        var plan = Plan(); var receipts = Receipts(plan);
        var result = PreparedLayoutRules.AssessOwnership(plan, plan.GenerationId, receipts,
            receipts.Select(r => r.Identity).ToImmutableArray(), Change(plan.WindowsEsp, change));
        Assert.Equal("WindowsEspChanged", result.Code);
    }

    [Fact]
    public void SameLabelCannotSubstituteAnotherPayloadIdentity()
    {
        var plan = Plan(); var receipts = Receipts(plan);
        // Labels are not even inputs. Same role, geometry, filesystem and disk still cannot replace a GUID.
        var wrong = receipts.Select(r => r.Role == PreparationRole.Payload ? r.Identity with { PartitionGuid = Guid.NewGuid() } : r.Identity).ToImmutableArray();
        Assert.Equal(PreparationState.AmbiguousLeftover, PreparedLayoutRules.AssessOwnership(plan, plan.GenerationId, receipts, wrong, plan.WindowsEsp).State);
    }

    [Fact]
    public void BootContentChecksExactVolumePathLengthAndHash()
    {
        var layout = Layout();
        Assert.Equal(ObservationAvailability.Available, PreparedLayoutRules.VerifyBootFiles(layout, layout.BootFiles.Reverse().ToImmutableArray()).Availability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ChangedShimGrubOrConfigurationIsRejected(int file)
    {
        var layout = Layout();
        var changed = layout.BootFiles.SetItem(file, layout.BootFiles[file] with { Sha256 = new string('B', 64) });
        Assert.Equal("PreparedBootContentChanged", PreparedLayoutRules.VerifyBootFiles(layout, changed).Code);
    }

    [Fact]
    public void MissingConfigAndWindowsEspStagingAreRejected()
    {
        var layout = Layout();
        var missing = layout with { BootFiles = layout.BootFiles.RemoveAt(2) };
        Assert.Equal(ObservationAvailability.Ambiguous, PreparedLayoutRules.VerifyBootFiles(missing, missing.BootFiles).Availability);
        var wrong = layout with { BootFiles = layout.BootFiles.Select(f => f with { VolumeGuid = layout.Plan.WindowsEsp.VolumeGuid }).ToImmutableArray() };
        Assert.Equal(ObservationAvailability.Ambiguous, PreparedLayoutRules.VerifyBootFiles(wrong, wrong.BootFiles).Availability);
    }

    [Fact]
    public void FilesystemUuidCannotBeReplacedWithAVolumeGuidOrLabel()
    {
        var layout = Layout() with { PayloadFileSystemUuid = "OEMDRV" };
        Assert.Equal(ObservationAvailability.Ambiguous, PreparedLayoutRules.VerifyBootFiles(layout, layout.BootFiles).Availability);
    }

    private static Observation<T> A<T>(T value) => Observations.Available(value);
    private static PreparationPlanV1 Plan()
    {
        var disk = new CanonicalDiskIdentityV1("eui.test", 8, 17, Guid.NewGuid(), 100000 * MiB, 512, 4096);
        var windows = new CanonicalVolumeIdentityV1(disk, Guid.NewGuid(), Guid.NewGuid(), PreparationSpacePlanning.EspType, MiB, 260 * MiB, "FAT32");
        return new(1, Guid.NewGuid(), disk, windows, [windows.PartitionGuid],
            PreparationSpacePlanning.Plan(new(40000 * MiB, 40000 * MiB, 20000 * MiB, 1000 * MiB, 0, 4096, false)).Value);
    }

    private static CanonicalVolumeIdentityV1 Partition(PreparationPlanV1 plan, PreparationRole role)
    {
        var allocation = plan.Space.Allocations.Single(a => a.Role == role);
        return new(plan.TargetDisk, Guid.NewGuid(), Guid.NewGuid(), allocation.PartitionType, allocation.OffsetBytes, allocation.SizeBytes, allocation.FileSystem);
    }
    private static ImmutableArray<PreparedPartitionV1> Receipts(PreparationPlanV1 plan) =>
        [new(PreparationRole.LinuxEsp, Partition(plan, PreparationRole.LinuxEsp)), new(PreparationRole.Payload, Partition(plan, PreparationRole.Payload))];
    private static PreparedLayoutV1 Layout()
    {
        var plan = Plan(); var receipts = Receipts(plan); var esp = receipts[0].Identity;
        return new(1, plan, receipts, "1234-ABCD", [File(PreparedLayoutRules.ShimPath), File(PreparedLayoutRules.GrubPath), File(@"\EFI\iGloo\grub.cfg")]);
        CanonicalFileIdentityV1 File(string path) => new(esp.VolumeGuid, path, 1024, new string('A', 64));
    }
    private static CanonicalVolumeIdentityV1 Change(CanonicalVolumeIdentityV1 value, string change) => change switch
    {
        "partition" => value with { PartitionGuid = Guid.NewGuid() },
        "volume" => value with { VolumeGuid = Guid.NewGuid() },
        "disk" => value with { Disk = value.Disk with { GptDiskGuid = Guid.NewGuid() } },
        "type" => value with { PartitionType = PreparationSpacePlanning.BasicDataType },
        "filesystem" => value with { FileSystem = "NTFS" },
        "offset" => value with { OffsetBytes = value.OffsetBytes + MiB },
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };
}
