using System.Collections.Immutable;
using System.Management;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Layout = Igloo.Preflight.DirectInstallService.LayoutPartition;

namespace Igloo.Preflight.Tests;

// Beta Track B-01/B-02: a layout the installer could misread is refused before Windows is resized.
public sealed class LinuxRegionAdmissionTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * 1024 * 1024;
    private const long Disk = 128 * GiB;
    private const long WindowsOffset = 117 * MiB;
    private const long RecoveryOffset = Disk - GiB;
    private const string EspType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    private const string BasicType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    private const string LinuxType = "{0fc63daf-8483-4772-8e79-3d69d8477de4}";

    private static readonly InstallerTargetRules Debian = new(20 * GiB, ScansAllDisks: false, UsesExistingEsp: false);
    private static readonly InstallerTargetRules Fedora = new(20 * GiB, ScansAllDisks: false, UsesExistingEsp: true);
    private static readonly InstallerTargetRules Mint = new(20 * GiB, ScansAllDisks: true, UsesExistingEsp: true);

    private static readonly Layout Esp = new(MiB, 100 * MiB, IsWindows: false, IsEsp: true);
    private static readonly Layout Msr = new(101 * MiB, 16 * MiB, IsWindows: false, IsEsp: false);
    private static readonly Layout Recovery = new(RecoveryOffset, GiB - MiB, IsWindows: false, IsEsp: false);

    private static Layout Windows(long endOffset) => new(WindowsOffset, endOffset - WindowsOffset, IsWindows: true, IsEsp: false);
    private static Layout Other(long offset, long size, bool esp = false) => new(offset, size, IsWindows: false, IsEsp: esp);

    [Fact]
    public void A_clean_windows_disk_is_shrunk_and_leaves_one_region()
    {
        Layout[] layout = [Esp, Msr, Windows(RecoveryOffset), Recovery];

        var plan = DirectInstallService.PlanLinuxRegion(0, layout, Disk, 34 * GiB, 4 * GiB, false, Debian);

        Assert.True(plan.ShrinksWindows);
        Assert.Equal(new DirectInstallService.FreeRegion(RecoveryOffset - 34 * GiB, 34 * GiB), plan.Region);
        Assert.Equal(30 * GiB, plan.ExpectedLinuxBytes);
    }

    [Fact]
    public void A_shrink_that_would_leave_a_second_region_is_refused()
    {
        // 25 GiB from a removed installation sits behind a leftover boot partition.
        var leftover = Other(RecoveryOffset - 26 * GiB, GiB);
        Layout[] layout = [Esp, Msr, Windows(leftover.Offset), leftover, Recovery];

        var refused = Assert.Throws<InstallationRefusedException>(() =>
            DirectInstallService.PlanLinuxRegion(0, layout, Disk, 54 * GiB, 4 * GiB, false, Debian));

        Assert.Contains("2 unallocated regions of 20 GiB or more", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Windows has not been resized", refused.Message, StringComparison.Ordinal);
        Assert.Contains("System Check", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_space_that_fits_is_used_without_a_shrink()
    {
        var leftover = Other(RecoveryOffset - 26 * GiB, GiB);
        Layout[] layout = [Esp, Msr, Windows(leftover.Offset), leftover, Recovery];

        var plan = DirectInstallService.PlanLinuxRegion(0, layout, Disk, 24 * GiB, 4 * GiB, false, Debian);

        Assert.False(plan.ShrinksWindows);
        Assert.Equal(25 * GiB, plan.Region.Size);
        Assert.Equal(21 * GiB, plan.ExpectedLinuxBytes);
    }

    // The wizard states the outcome from the estimate; it has to be the outcome the plan reaches on the disk.
    [Theory]
    [InlineData(16, true)]
    [InlineData(20, true)]
    [InlineData(30, false)]
    public void The_estimate_states_the_outcome_the_plan_reaches(int requestedGiB, bool spaceLeftByARemovedLinux)
    {
        var leftover = Other(RecoveryOffset - 26 * GiB, GiB);
        Layout[] layout = spaceLeftByARemovedLinux
            ? [Esp, Msr, Windows(leftover.Offset), leftover, Recovery]
            : [Esp, Msr, Windows(RecoveryOffset), Recovery];
        // The measurement reports the extent the preparation compares with, which is an alignment margin under the raw gap.
        var extent = DirectInstallService.LargestFreeExtentBytes(layout.Select(p => (p.Offset, p.Size)).ToList(), Disk);
        var estimate = new DirectInstallSpaceEstimate(100 * GiB, 4 * GiB, 0) { UnallocatedBytes = extent };

        var stated = estimate.Allocate(requestedGiB * GiB);
        var plan = DirectInstallService.PlanLinuxRegion(0, layout, Disk, (requestedGiB + 4) * GiB, 4 * GiB, false, Debian);

        Assert.Equal(plan.ShrinksWindows, stated.ResizesWindows);
        Assert.InRange(plan.ExpectedLinuxBytes - stated.LinuxBytes, 0, 2 * MiB);
    }

    // The r19 run: 50 GiB asked, 100 GiB unallocated, 4224 MiB of installer partition; Debian's three partitions took 95.88 GiB.
    [Fact]
    public void A_request_below_the_unallocated_space_is_given_all_of_it()
    {
        var estimate = new DirectInstallSpaceEstimate(100 * GiB, 4224 * MiB, 0) { UnallocatedBytes = 100 * GiB };

        var stated = estimate.Allocate(50 * GiB);

        Assert.False(stated.ResizesWindows);
        Assert.Equal(100 * GiB - 4224 * MiB, stated.LinuxBytes);
        Assert.InRange(1024458752L + 96607404032 + 5314183168, stated.LinuxBytes - 2 * MiB, stated.LinuxBytes + 2 * MiB);
        Assert.True(estimate.Allocate(96 * GiB).ResizesWindows);
        Assert.True((estimate with { UnallocatedBytes = 0 }).Allocate(50 * GiB).ResizesWindows);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void A_second_region_counts_from_exactly_the_minimum(long bytesBelowMinimum, bool refused)
    {
        var gap = 20 * GiB - bytesBelowMinimum;
        var leftover = Other(RecoveryOffset - gap - GiB, GiB);
        Layout[] layout = [Esp, Msr, Windows(leftover.Offset), leftover, Recovery];

        void Plan() { _ = DirectInstallService.PlanLinuxRegion(0, layout, Disk, 54 * GiB, 4 * GiB, false, Debian); }

        if (refused) Assert.Throws<InstallationRefusedException>(Plan);
        else Plan();
    }

    [Fact]
    public void A_reused_installer_partition_needs_a_region_that_is_still_free()
    {
        var seed = Other(RecoveryOffset - 34 * GiB, 4 * GiB);
        var halfInstalled = Other(seed.Offset + seed.Size, 30 * GiB);
        Layout[] layout = [Esp, Msr, Windows(seed.Offset), seed, halfInstalled, Recovery];

        var refused = Assert.Throws<InstallationRefusedException>(() =>
            DirectInstallService.PlanLinuxRegion(0, layout, Disk, 34 * GiB, 4 * GiB, true, Debian));

        Assert.Contains("no unallocated region of 20 GiB or more", refused.Message, StringComparison.Ordinal);
        Assert.Contains("System Check", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reused_installer_partition_keeps_the_region_whole()
    {
        var seed = Other(RecoveryOffset - 34 * GiB, 4 * GiB);
        Layout[] layout = [Esp, Msr, Windows(seed.Offset), seed, Recovery];

        var plan = DirectInstallService.PlanLinuxRegion(0, layout, Disk, 34 * GiB, 4 * GiB, true, Debian);

        Assert.False(plan.ShrinksWindows);
        Assert.Equal(30 * GiB, plan.ExpectedLinuxBytes);
    }

    [Fact]
    public void A_second_esp_stops_the_installers_that_would_pick_one()
    {
        // Debian's own ESP from an earlier installation (issue #227) next to its root.
        var debianEsp = Other(RecoveryOffset - 40 * GiB, GiB, esp: true);
        var debianRoot = Other(debianEsp.Offset + debianEsp.Size, 39 * GiB);
        Layout[] layout = [Esp, Msr, Windows(debianEsp.Offset), debianEsp, debianRoot, Recovery];

        Assert.True(DirectInstallService.PlanLinuxRegion(0, layout, Disk, 34 * GiB, 4 * GiB, false, Debian).ShrinksWindows);
        foreach (var rules in new[] { Fedora, Mint })
        {
            var refused = Assert.Throws<InstallationRefusedException>(() =>
                DirectInstallService.PlanLinuxRegion(0, layout, Disk, 34 * GiB, 4 * GiB, false, rules));
            Assert.Contains("2 EFI system partitions", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Windows has not been resized", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_shrink_needs_exactly_one_windows_partition_larger_than_the_plan()
    {
        Layout[] dataDisk = [Other(MiB, Disk - 2 * MiB)];
        Layout[] smallWindows = [Esp, Msr, Windows(WindowsOffset + 30 * GiB), Other(WindowsOffset + 30 * GiB, 90 * GiB), Recovery];

        foreach (var layout in new[] { dataDisk, smallWindows })
        {
            var refused = Assert.Throws<InstallationRefusedException>(() =>
                DirectInstallService.PlanLinuxRegion(3, layout, Disk, 34 * GiB, 4 * GiB, false, Debian));
            Assert.Contains("Disk 3 has no unallocated region of 34.0 GiB", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unusable_inputs_are_refused_rather_than_guessed()
    {
        Layout[] layout = [Esp, Msr, Windows(RecoveryOffset), Recovery];

        Assert.Throws<InvalidOperationException>(() => DirectInstallService.PlanLinuxRegion(0, layout, 0, 34 * GiB, 4 * GiB, false, Debian));
        Assert.Throws<InvalidOperationException>(() => DirectInstallService.PlanLinuxRegion(0, layout, Disk, 0, 0, false, Debian));
        Assert.Throws<InvalidOperationException>(() => DirectInstallService.PlanLinuxRegion(0, layout, Disk, 4 * GiB, 34 * GiB, false, Debian));
        Assert.Throws<InvalidOperationException>(() => DirectInstallService.PlanLinuxRegion(0, layout, Disk, 34 * GiB, 4 * GiB, false, Debian with { MinimumRegionBytes = 0 }));
    }

    [Fact]
    public void Free_regions_are_the_raw_gaps_in_offset_order()
    {
        var regions = DirectInstallService.FreeRegions([(60 * GiB, 10 * GiB), (MiB, 10 * GiB), (5 * GiB, GiB)], 100 * GiB);

        Assert.Equal(
        [
            new DirectInstallService.FreeRegion(0, MiB),
            new DirectInstallService.FreeRegion(10 * GiB + MiB, 50 * GiB - MiB),
            new DirectInstallService.FreeRegion(70 * GiB, 30 * GiB),
        ], regions);
        Assert.Empty(DirectInstallService.FreeRegions([], 0));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void A_region_on_another_disk_rivals_from_the_planned_size_up(long bytesBelowPlanned, bool refused)
    {
        var elsewhere = new[] { (1, new DirectInstallService.FreeRegion(MiB, 30 * GiB - bytesBelowPlanned)) };

        void Check() { DirectInstallService.RequireNoRivalRegion(0, 30 * GiB, elsewhere); }

        if (!refused) { Check(); return; }
        var error = Assert.Throws<InstallationRefusedException>(Check);
        Assert.Contains("Disk 1 has an unallocated region of 30.0 GiB", error.Message, StringComparison.Ordinal);
        Assert.Contains("Disconnect disk 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_layout_is_refused_before_the_resizer_is_called()
    {
        var storage = new StorageFake();
        var leftoverOffset = RecoveryOffset - 26 * GiB;
        storage.Add(2, Disk, 17, Row(MiB, 100 * MiB, EspType), Row(WindowsOffset, leftoverOffset - WindowsOffset, BasicType, boot: true, letter: 'C'),
            Row(leftoverOffset, GiB, LinuxType), Row(RecoveryOffset, GiB - MiB, BasicType));
        var resizer = new ResizerFake();
        var service = new DirectInstallService(resizer, NullLogger<DirectInstallService>.Instance, storage);

        var refused = Assert.Throws<InstallationRefusedException>(() => service.ReserveLinuxSpace(2, 50 * GiB,
            new(false, 4 * GiB, 0, 54 * GiB), Spec(), Debian, null, default));

        Assert.Contains("2 unallocated regions", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, resizer.ShrinkCalls);
    }

    [Fact]
    public void A_clean_layout_is_shrunk_by_the_plan_and_fitting_space_is_not()
    {
        var storage = new StorageFake();
        storage.Add(2, Disk, 17, Row(MiB, 100 * MiB, EspType), Row(WindowsOffset, RecoveryOffset - WindowsOffset, BasicType, boot: true, letter: 'C'),
            Row(RecoveryOffset, GiB - MiB, BasicType));
        var resizer = new ResizerFake();
        var service = new DirectInstallService(resizer, NullLogger<DirectInstallService>.Instance, storage);

        service.ReserveLinuxSpace(2, 30 * GiB, new(false, 4 * GiB, 0, 34 * GiB), Spec(), Debian, null, default);

        Assert.Equal([34 * GiB], resizer.Shrunk);

        var roomy = new StorageFake();
        roomy.Add(2, Disk, 17, Row(MiB, 100 * MiB, EspType), Row(WindowsOffset, 60 * GiB, BasicType, boot: true, letter: 'C'),
            Row(RecoveryOffset, GiB - MiB, BasicType));
        var untouched = new ResizerFake();
        new DirectInstallService(untouched, NullLogger<DirectInstallService>.Instance, roomy)
            .ReserveLinuxSpace(2, 30 * GiB, new(false, 4 * GiB, 0, 34 * GiB), Spec(), Debian, null, default);

        Assert.Equal(0, untouched.ShrinkCalls);
    }

    [Fact]
    public void A_larger_region_on_another_disk_stops_only_the_installer_that_scans_every_disk()
    {
        StorageFake Storage(uint otherBus)
        {
            var storage = new StorageFake();
            storage.Add(2, Disk, 17, Row(MiB, 100 * MiB, EspType), Row(WindowsOffset, RecoveryOffset - WindowsOffset, BasicType, boot: true, letter: 'C'),
                Row(RecoveryOffset, GiB - MiB, BasicType));
            storage.Add(5, 500 * GiB, otherBus, Row(MiB, 100 * GiB, BasicType, letter: 'D'));
            return storage;
        }
        void Reserve(StorageFake storage, ResizerFake resizer, InstallerTargetRules rules) =>
            new DirectInstallService(resizer, NullLogger<DirectInstallService>.Instance, storage)
                .ReserveLinuxSpace(2, 30 * GiB, new(false, 4 * GiB, 0, 34 * GiB), Spec(), rules, null, default);

        var mint = new ResizerFake();
        var refused = Assert.Throws<InstallationRefusedException>(() => Reserve(Storage(11), mint, Mint));
        Assert.Contains("Disk 5 has an unallocated region of 400.0 GiB", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, mint.ShrinkCalls);

        var mountedImage = new ResizerFake();
        Reserve(Storage(15), mountedImage, Mint);
        Assert.Equal(1, mountedImage.ShrinkCalls);

        var debian = new ResizerFake();
        Reserve(Storage(11), debian, Debian);
        Assert.Equal(1, debian.ShrinkCalls);
    }

    [Fact]
    public void An_unreadable_layout_is_refused_before_the_resizer_is_called()
    {
        var storage = new StorageFake { PartitionError = new ManagementException("fixture") };
        storage.Add(2, Disk, 17, Row(MiB, 100 * MiB, EspType));
        var resizer = new ResizerFake();
        var service = new DirectInstallService(resizer, NullLogger<DirectInstallService>.Instance, storage);

        var refused = Assert.Throws<InstallationRefusedException>(() => service.ReserveLinuxSpace(2, 30 * GiB,
            new(false, 4 * GiB, 0, 34 * GiB), Spec(), Debian, null, default));

        Assert.Contains("could not be read", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, resizer.ShrinkCalls);
    }

    [Fact]
    public void A_seed_this_run_removes_first_counts_as_unallocated_space()
    {
        var storage = new StorageFake();
        var seedOffset = RecoveryOffset - 38 * GiB;
        storage.Add(2, Disk, 17, Row(MiB, 100 * MiB, EspType), Row(WindowsOffset, seedOffset - WindowsOffset, BasicType, boot: true, letter: 'C'),
            Row(seedOffset, 8 * GiB, BasicType, letter: 'I'), Row(RecoveryOffset, GiB - MiB, BasicType));
        var service = new DirectInstallService(new ResizerFake(), NullLogger<DirectInstallService>.Instance, storage);

        var removed = service.RequireSingleLinuxRegion(2, 34 * GiB, 4 * GiB, false, ['I'], Debian);

        Assert.False(removed.ShrinksWindows);
        Assert.Equal(38 * GiB, removed.Region.Size);
        // Left in place, the same seed would split the disk into two regions.
        Assert.Throws<InstallationRefusedException>(() => service.RequireSingleLinuxRegion(2, 34 * GiB, 4 * GiB, false, [], Debian));
    }

    [Fact]
    public void A_pre_created_root_partition_keeps_the_earlier_decision()
    {
        var storage = new StorageFake();
        var leftoverOffset = RecoveryOffset - 26 * GiB;
        storage.Add(2, Disk, 17, Row(MiB, 100 * MiB, EspType), Row(WindowsOffset, leftoverOffset - WindowsOffset, BasicType, boot: true, letter: 'C'),
            Row(leftoverOffset, GiB, LinuxType), Row(RecoveryOffset, GiB - MiB, BasicType));
        var resizer = new ResizerFake();
        var service = new DirectInstallService(resizer, NullLogger<DirectInstallService>.Instance, storage);

        service.ReserveLinuxSpace(2, 50 * GiB, new(false, 4 * GiB, 0, 54 * GiB),
            Spec() with { PreCreateRootPartition = true }, Mint, null, default);

        Assert.Equal(1, resizer.ShrinkCalls);
    }

    private static InstallerBootSpec Spec() => new() { MenuTitle = "fixture", KernelCmdline = "fixture" };

    private static WindowsStorageRow Row(long offset, long size, string gptType, bool boot = false, char letter = '\0') =>
        new(new Dictionary<string, object?>
        {
            ["Offset"] = (ulong)offset, ["Size"] = (ulong)size, ["GptType"] = gptType,
            ["IsBoot"] = boot, ["DriveLetter"] = letter,
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));

    private sealed class StorageFake : IWindowsStorageReader
    {
        private readonly Dictionary<int, (WindowsStorageRow Disk, WindowsStorageRow[] Partitions)> _disks = [];

        public Exception? PartitionError { get; init; }

        public void Add(int number, long size, uint busType, params WindowsStorageRow[] partitions) =>
            _disks[number] = (new(new Dictionary<string, object?>
            {
                ["Number"] = (uint)number, ["Size"] = (ulong)size, ["BusType"] = busType,
            }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)), partitions);

        public WindowsStorageBatch ReadDisks() => new(_disks.Values.Select(disk => disk.Disk).ToArray());

        public WindowsStorageBatch ReadPartitions(int? diskNumber = null, int? partitionNumber = null, bool efiOnly = false) =>
            new(_disks[diskNumber!.Value].Partitions, PartitionError);

        public WindowsStorageBatch ReadVolumes(char driveLetter) => throw new InvalidOperationException("Volumes must not be read.");

        public WindowsObservation<WindowsStorageRow> ReadSupportedSize(WindowsStorageRow partition, bool explicitParameters = false) =>
            throw new InvalidOperationException("Supported size must not be read.");
    }

    private sealed class ResizerFake : IPartitionResizeService
    {
        public List<long> Shrunk { get; } = [];
        public int ShrinkCalls => Shrunk.Count;

        public Task<long> GetShrinkableSpaceAsync(int diskNumber, CancellationToken ct = default) =>
            throw new InvalidOperationException("Shrinkable space must not be read.");

        public Task ShrinkAsync(int diskNumber, long linuxSizeBytes, IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            Shrunk.Add(linuxSizeBytes);
            return Task.CompletedTask;
        }
    }
}
