using FluentAssertions;
using Igloo.App.ViewModels;
using Igloo.Core.Abstractions;
using Xunit;

namespace Igloo.App.Tests;

/// <summary>
/// Characterization tests for <see cref="DiskSelectionViewModel"/>: disk filtering,
/// system-disk preference, and the dual-boot/replace defaulting rules that decide
/// which install pipeline the wizard branches into.
/// </summary>
public class DiskSelectionViewModelTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static DiskInfo Disk(string id, long totalGb, bool isSystem, long shrinkableGb = 0) =>
        new(id, $"Disk {id}", totalGb * Gb, 0, "GPT",
            [new PartitionInfo(1, "NTFS", totalGb * Gb, "C", IsSystem: isSystem, IsBoot: isSystem,
                ShrinkableBytes: shrinkableGb * Gb)]);

    private static PreflightReport Report(params DiskInfo[] disks) => new()
    {
        IsUefi = true,
        SecureBootEnabled = false,
        TpmPresent = true,
        BitLocker = BitLockerState.NotEncrypted,
        Disks = disks,
        GpuVendor = "intel",
        TotalRamBytes = 8 * Gb,
        Findings = [],
    };

    [Fact]
    public void Disks_under_twenty_gb_are_filtered_out()
    {
        var vm = new DiskSelectionViewModel();

        vm.Prepare(Report(Disk("small", 16, isSystem: false), Disk("big", 500, isSystem: false)));

        vm.DiskItems.Should().ContainSingle().Which.Disk.DeviceId.Should().Be("big");
    }

    [Fact]
    public void System_disk_is_preselected_over_a_larger_data_disk()
    {
        var vm = new DiskSelectionViewModel();

        vm.Prepare(Report(
            Disk("data", 2000, isSystem: false),
            Disk("system", 500, isSystem: true, shrinkableGb: 100)));

        vm.SelectedDisk!.DeviceId.Should().Be("system");
    }

    [Fact]
    public void Dual_boot_is_the_default_when_the_system_disk_has_room()
    {
        var vm = new DiskSelectionViewModel();

        vm.Prepare(Report(Disk("system", 500, isSystem: true, shrinkableGb: 100)));

        vm.IsInstallModeDualBoot.Should().BeTrue();
        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.LinuxSizeGb.Should().Be(50, "default is 50 GiB when shrinkable space allows");
        vm.CanProceed.Should().BeTrue();
    }

    [Fact]
    public void Replace_is_the_default_when_nothing_can_be_shrunk()
    {
        var vm = new DiskSelectionViewModel();

        vm.Prepare(Report(Disk("system", 500, isSystem: true, shrinkableGb: 0)));

        vm.IsInstallModeDualBoot.Should().BeFalse();
        vm.InstallMode.Should().Be(DiskInstallMode.ReplaceDisk);
        vm.CanProceed.Should().BeTrue("replace mode has no minimum-size requirement");
    }

    [Fact]
    public void Default_linux_size_is_capped_to_the_shrinkable_space()
    {
        var vm = new DiskSelectionViewModel();

        vm.Prepare(Report(Disk("system", 500, isSystem: true, shrinkableGb: 30)));

        vm.IsInstallModeDualBoot.Should().BeTrue("30 GiB clears the 25 GiB floor");
        vm.LinuxSizeGb.Should().Be(30);
    }

    [Fact]
    public void Linux_size_bytes_uses_binary_gigabytes()
    {
        var vm = new DiskSelectionViewModel();

        vm.Prepare(Report(Disk("system", 500, isSystem: true, shrinkableGb: 100)));
        vm.LinuxSizeGb = 64;

        vm.LinuxSizeBytes.Should().Be(64L * Gb);
    }
}
