using FluentAssertions;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Host;
using Xunit;

namespace Igloo.Community.App.Tests;

public class DiskSelectionViewModelTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static DiskInfo Disk(string id, long totalGb, bool isSystem, long shrinkableGb = 0) =>
        new(id, $"Disk {id}", totalGb * Gb, 0, "GPT",
            [new PartitionInfo(1, "NTFS", totalGb * Gb, "C", IsSystem: isSystem, IsBoot: isSystem,
                ShrinkableBytes: shrinkableGb * Gb)])
        { GptDiskGuid = Guid.Parse("3b5af5a4-1c98-4ef3-9658-f1d5f6db7e35") };

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

    private static DistroManifest Mint() => new()
    {
        Id = "linuxmint-cinnamon",
        DisplayName = "Linux Mint",
        Description = "Fixture",
        Iso = new DistroIsoSpec { DownloadUrl = new Uri("https://example.invalid/mint.iso"), Sha256 = "" },
        SupportedInstallModes = ["dual-boot"],
        MinimumRequirements = new DistroRequirements { MinDiskBytes = 20 * Gb },
    };

    private sealed class Preparation : IInstallationPreparation
    {
        public Func<string, Observation<bool>>? Availability { get; init; }
        public int MeasurementCalls { get; private set; }
        public Observation<bool> PreparationAvailability => Observations.Available(true);
        public Observation<bool> GetDistroPreparationAvailability(string distroId) =>
            Availability?.Invoke(distroId) ?? PreparationAvailability;

        public Task<DirectInstallSpaceEstimate> MeasureSpaceAsync(string distroId,
            DiskInfo targetDisk, IsoAcquisitionResult verifiedIso, FileStagingRequest selectedFiles,
            CancellationToken cancellationToken = default)
        {
            MeasurementCalls++;
            return Task.FromResult(
                new DirectInstallSpaceEstimate(targetDisk.Partitions
                    .Where(p => p.IsBoot && p.FileSystem == "NTFS")
                    .Select(p => p.ShrinkableBytes).DefaultIfEmpty(0).Max(),
                    Gb + (1L << 20) + 1, 0));
        }

        public Task PrepareAsync(string distroId, DiskInfo targetDisk, long linuxSizeBytes,
            IsoAcquisitionResult verifiedIso, string stagingDirectory, Uri? stage2Url,
            IProgress<DirectInstallProgress>? progress, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Disk selection must not prepare an installation.");

        public Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Disk selection must not register a boot entry.");
    }

    private static void Prepare(DiskSelectionViewModel vm, PreflightReport report,
        DistroManifest? distro = null)
    {
        distro ??= Mint() with
        {
            Id = "debian",
            SupportedInstallModes = ["dual-boot", "replace"],
            MinimumRequirements = new DistroRequirements { MinDiskBytes = 25 * Gb },
        };
        vm.Prepare(report, distro,
            new IsoAcquisitionResult(@"C:\verified\installer.iso", true, true, 4 * Gb,
                new string('A', 64)),
            new FileStagingRequest(distro.Id, []));
    }

    private static void SelectOnly(DiskSelectionViewModel vm) =>
        vm.SelectedItem = Assert.Single(vm.DiskItems);

    [Fact]
    public void Disks_under_twenty_gb_are_filtered_out()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(Disk("small", 16, isSystem: false), Disk("big", 500, isSystem: false)));

        vm.DiskItems.Should().ContainSingle().Which.Disk.DeviceId.Should().Be("big");
    }

    [Fact]
    public void System_disk_is_ranked_first_but_requires_explicit_selection()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(
            Disk("data", 2000, isSystem: false),
            Disk("system", 500, isSystem: true, shrinkableGb: 100)));

        vm.DiskItems[0].Disk.DeviceId.Should().Be("system");
        vm.SelectedDisk.Should().BeNull();
        vm.CanProceed.Should().BeFalse();
        vm.SelectedItem = vm.DiskItems[0];
        vm.SelectedDisk!.DeviceId.Should().Be("system");
        Prepare(vm, Report(
            Disk("data", 2000, isSystem: false),
            Disk("system", 500, isSystem: true, shrinkableGb: 100)));
        vm.SelectedDisk.Should().BeNull();
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Only_available_disk_is_selected_and_measured_automatically_on_each_visit()
    {
        var vm = new DiskSelectionViewModel(new Preparation());
        Prepare(vm, Report(Disk("system", 500, isSystem: true, shrinkableGb: 100)));

        vm.SelectedItem.Should().BeSameAs(Assert.Single(vm.DiskItems));
        vm.HasSpaceEstimate.Should().BeTrue();
        vm.LinuxSizeGb.Should().Be(50);
        vm.CanProceed.Should().BeTrue();

        Prepare(vm, Report(Disk("system", 500, isSystem: true, shrinkableGb: 30)));
        vm.SelectedItem.Should().BeSameAs(Assert.Single(vm.DiskItems));
        vm.LinuxSizeGb.Should().Be(28);
        vm.CanProceed.Should().BeTrue();

        Prepare(vm, Report());
        vm.SelectedItem.Should().BeNull();
        vm.HasSpaceEstimate.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Dual_boot_is_the_default_when_the_system_disk_has_room()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(Disk("system", 500, isSystem: true, shrinkableGb: 100)));
        SelectOnly(vm);

        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.LinuxSizeGb.Should().Be(50, "default is 50 GiB when shrinkable space allows");
        vm.CanProceed.Should().BeTrue();
    }

    [Fact]
    public void Insufficient_Windows_space_cannot_fall_back_to_disk_replacement()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(Disk("system", 500, isSystem: true, shrinkableGb: 0)));
        SelectOnly(vm);

        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.NeedsMoreDualBootSpace.Should().BeTrue();
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Missing_disk_identity_cannot_fall_back_to_disk_replacement()
    {
        var vm = new DiskSelectionViewModel(new Preparation());
        Prepare(vm, Report(Disk("system", 500, isSystem: true) with { GptDiskGuid = null }));
        SelectOnly(vm);

        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Dual_boot_is_unavailable_without_the_selected_disk_gpt_identity()
    {
        var vm = new DiskSelectionViewModel(new Preparation());
        Prepare(vm, Report(Disk("system", 500, isSystem: true, shrinkableGb: 80) with
        {
            GptDiskGuid = null
        }));
        SelectOnly(vm);

        vm.CanDualBoot.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
        vm.UnsupportedModeAction.Should().Be(
            "Choose another disk whose identity Windows can verify, then run the system check again.");
    }

    [Fact]
    public void Unsupported_disk_message_names_the_next_action()
    {
        var vm = new DiskSelectionViewModel(new Preparation());
        Prepare(vm, Report(Disk("mbr", 500, isSystem: true) with { PartitionStyle = "MBR" }), Mint());
        SelectOnly(vm);
        vm.UnsupportedModeAction.Should().Be("Choose a GPT disk, then continue.");

        Prepare(vm, Report(Disk("data", 500, isSystem: false)), Mint());
        SelectOnly(vm);
        vm.UnsupportedModeAction.Should().Be("Select the Windows system disk for dual boot.");
    }

    [Fact]
    public void Default_linux_size_reserves_measured_installer_space()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(Disk("system", 500, isSystem: true, shrinkableGb: 30)));
        SelectOnly(vm);

        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.MaxProvisionalLinuxGb.Should().Be(28);
        vm.LinuxSliderMinimumGb.Should().Be(vm.MinLinuxGb);
        vm.LinuxSliderMaximumGb.Should().Be(28);
        vm.LinuxSizeGb.Should().Be(28,
            "the measured installer reservation must fit alongside Linux root");
        vm.LinuxSizeGb = 30;
        vm.CanProceed.Should().BeFalse("the full shrinkable space cannot be Linux root");
    }

    [Fact]
    public void A_tight_mint_system_volume_cannot_enter_staging_with_no_esp_room()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(Disk("system", 100, isSystem: true, shrinkableGb: 21)),
            Mint());
        SelectOnly(vm);

        vm.CanDualBoot.Should().BeFalse();
        vm.NeedsMoreDualBootSpace.Should().BeTrue();
        vm.LinuxSizeGb.Should().Be(0);
        vm.LinuxSliderMinimumGb.Should().Be(0);
        vm.LinuxSliderMaximumGb.Should().Be(0);
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Linux_size_bytes_uses_binary_gigabytes()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(Disk("system", 500, isSystem: true, shrinkableGb: 100)));
        SelectOnly(vm);
        vm.LinuxSizeGb = 64;

        vm.LinuxSizeBytes.Should().Be(64L * Gb);
    }

    [Fact]
    public void Available_provider_uses_only_dual_boot()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(Disk("system", 100, isSystem: true, shrinkableGb: 40)), Mint());
        SelectOnly(vm);

        vm.CanDualBoot.Should().BeTrue();
        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.CanProceed.Should().BeTrue();
    }

    [Fact]
    public void Direct_dual_boot_selection_is_available_for_review_and_Core_admission()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(Disk("system", 100, isSystem: true, shrinkableGb: 40)), Mint());
        SelectOnly(vm);

        vm.CanDualBoot.Should().BeTrue();
        vm.NeedsMoreDualBootSpace.Should().BeFalse();
        vm.NoSupportedMode.Should().BeFalse();
        vm.CanProceed.Should().BeTrue();
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(24, true)]
    public void Mint_dual_boot_uses_its_declared_twenty_gib_root_minimum(
        int shrinkableGb, bool canProceed)
    {
        var vm = new DiskSelectionViewModel(new Preparation());
        Prepare(vm, Report(Disk("system", 100, isSystem: true,
            shrinkableGb: shrinkableGb)), Mint());
        SelectOnly(vm);

        vm.MinLinuxGb.Should().Be(20);
        vm.CanDualBoot.Should().Be(canProceed);
        vm.CanProceed.Should().Be(canProceed);
    }

    [Fact]
    public void Mint_with_no_shrinkable_system_volume_shows_only_the_space_action()
    {
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(Disk("system", 100, isSystem: true)), Mint());
        SelectOnly(vm);

        vm.NoSupportedMode.Should().BeFalse();
        vm.NeedsMoreDualBootSpace.Should().BeTrue();
        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Replace_only_provider_is_blocked_without_measuring_or_offering_replacement()
    {
        var preparation = new Preparation();
        var vm = new DiskSelectionViewModel(preparation);
        var replaceOnly = Mint() with
        {
            Id = "fedora-kde",
            SupportedInstallModes = ["replace"],
        };

        Prepare(vm, Report(Disk("system", 100, isSystem: true)), replaceOnly);
        SelectOnly(vm);

        preparation.MeasurementCalls.Should().Be(0);
        vm.NeedsMoreDualBootSpace.Should().BeFalse();
        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.CanProceed.Should().BeFalse();
        vm.ShowDiskAction.Should().BeTrue();
        vm.DiskActionMessage.Should().Contain("always keeps Windows");
    }

    [Theory]
    [InlineData("fedora-kde", "FedoraInstallerRootBindingUnavailable", "Fedora")]
    [InlineData("linuxmint-cinnamon", "MintDedicatedEspBindingUnavailable", "Linux Mint")]
    public void Unsupported_dual_boot_stops_before_measurement_without_selecting_replacement(
        string distroId, string code, string displayName)
    {
        var preparation = new Preparation
        {
            Availability = id => id == distroId
                ? Observations.Failure<bool>(ObservationAvailability.Unsupported, code)
                : Observations.Available(true),
        };
        var vm = new DiskSelectionViewModel(preparation);
        var fedora = Mint() with
        {
            Id = distroId,
            SupportedInstallModes = ["dual-boot", "replace"],
        };
        var report = Report(Disk("system", 100, isSystem: true, shrinkableGb: 40));

        Prepare(vm, report, fedora);
        SelectOnly(vm);

        preparation.MeasurementCalls.Should().Be(0);
        vm.CanDualBoot.Should().BeFalse();
        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.NeedsMoreDualBootSpace.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
        vm.ShowDiskAction.Should().BeTrue();
        vm.DiskActionMessage.Should().Contain(displayName + " dual boot is not available")
            .And.Contain("choose Debian");

        Prepare(vm, report, Mint() with { Id = "debian" });
        SelectOnly(vm);
        vm.CanDualBoot.Should().BeTrue();
        vm.CanProceed.Should().BeTrue();
        preparation.MeasurementCalls.Should().Be(1);
        vm.ShowDiskAction.Should().BeFalse();
    }

    [Theory]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.AccessDenied)]
    public void Unavailable_direct_preparation_does_not_start_measurement(ObservationAvailability state)
    {
        var preparation = new Preparation
        {
            Availability = _ => Observations.Failure<bool>(state, "PreparationUnavailable"),
        };
        var vm = new DiskSelectionViewModel(preparation);

        Prepare(vm, Report(Disk("system", 100, isSystem: true, shrinkableGb: 40)), Mint());

        preparation.MeasurementCalls.Should().Be(0);
        vm.CanDualBoot.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
        vm.NeedsMoreDualBootSpace.Should().BeFalse();
        vm.ShowDiskAction.Should().BeTrue();
        vm.DiskActionMessage.Should().Contain("Update or repair iGloo");
    }

    [Fact]
    public void Dual_boot_allocation_cannot_exceed_the_selected_volume_limit()
    {
        var vm = new DiskSelectionViewModel(new Preparation());
        Prepare(vm, Report(Disk("system", 100, isSystem: true, shrinkableGb: 30)));
        SelectOnly(vm);

        vm.LinuxSizeGb = 31;

        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Data_partition_shrinkability_does_not_enable_system_dual_boot()
    {
        var disk = new DiskInfo("system", "System", 100 * Gb, 0, "GPT",
        [
            new PartitionInfo(1, "FAT32", 1 * Gb, "EFI", IsSystem: true, IsBoot: false),
            new PartitionInfo(2, "NTFS", 50 * Gb, "Windows", IsSystem: false, IsBoot: true),
            new PartitionInfo(3, "NTFS", 49 * Gb, "Data", IsSystem: false, IsBoot: false,
                ShrinkableBytes: 35 * Gb),
        ]);
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(disk), Mint());
        SelectOnly(vm);

        vm.CanDualBoot.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public void Legacy_partition_table_cannot_be_selected_for_uefi_dual_boot()
    {
        var disk = Disk("system", 100, isSystem: true, shrinkableGb: 40) with
        {
            PartitionStyle = "MBR"
        };
        var vm = new DiskSelectionViewModel(new Preparation());

        Prepare(vm, Report(disk), Mint());
        SelectOnly(vm);

        vm.CanDualBoot.Should().BeFalse();
        vm.CanProceed.Should().BeFalse();
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("data")]
    [InlineData("efi-only")]
    public void Missing_Windows_partition_blocks_with_an_action_before_measurement(string fixture)
    {
        var preparation = new Preparation();
        var vm = new DiskSelectionViewModel(preparation);
        var disk = Disk("disk", 100, isSystem: false, shrinkableGb: 60);
        if (fixture == "efi-only")
            disk = disk with { Partitions = [new PartitionInfo(1, "FAT32", Gb, "EFI", true, false)] };

        Prepare(vm, fixture == "empty" ? Report() : Report(disk));

        vm.HasEligibleWindowsDisk.Should().BeFalse();
        vm.WindowsDiskAction.Should().Contain("keeps Windows").And.Contain("system check again");
        vm.InstallMode.Should().Be(DiskInstallMode.DualBoot);
        vm.CanProceed.Should().BeFalse();
        preparation.MeasurementCalls.Should().Be(0);
    }
}
