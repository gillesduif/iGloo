using FluentAssertions;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.Core.Tests;

public class ManifestGeneratorServiceTests
{
    private static readonly PreflightReport UefiReport = new()
    {
        IsUefi = true,
        SecureBootEnabled = true,
        TpmPresent = true,
        BitLocker = BitLockerState.NotEncrypted,
        Disks = [],
        GpuVendor = "nvidia",
        TotalRamBytes = 16L * 1024 * 1024 * 1024,
        Findings = [],
    };

    private static readonly FileStagingResult Staging =
        new(@"C:\Igloo\staging", 123_456L, 42);

    private static UserSetup MinimalUser() => new()
    {
        WindowsUsername = "Winnie",
        LinuxUsername = "winnie",
        LinuxPassword = "hunter2hunter2",
    };

    [Fact]
    public void Dual_boot_mode_maps_to_dual_boot_string_and_keeps_linux_size()
    {
        var manifest = ManifestGeneratorService.Generate(
            "debian", MinimalUser(), UefiReport, Staging,
            targetDisk: new DiskInfo("id", "Samsung SSD", 500_000_000_000L, 0, "GPT", []),
            installMode: DiskInstallMode.DualBoot, linuxSizeGb: 80);

        manifest.Hardware.InstallMode.Should().Be("dual-boot");
        manifest.Hardware.LinuxPartitionSizeGb.Should().Be(80);
        manifest.Hardware.TargetDiskModel.Should().Be("Samsung SSD");
        manifest.Hardware.TargetDiskBytes.Should().Be(500_000_000_000L);
    }

    [Fact]
    public void Replace_mode_maps_to_replace_string_and_zeroes_linux_size()
    {
        var manifest = ManifestGeneratorService.Generate(
            "debian", MinimalUser(), UefiReport, Staging,
            installMode: DiskInstallMode.ReplaceDisk, linuxSizeGb: 80);

        manifest.Hardware.InstallMode.Should().Be("replace");
        manifest.Hardware.LinuxPartitionSizeGb.Should().Be(0, "size only applies to dual-boot");
        manifest.Hardware.TargetDiskBytes.Should().Be(0, "no target disk was supplied");
    }

    [Fact]
    public void Uefi_and_bios_reports_map_to_firmware_type_strings()
    {
        var uefi = ManifestGeneratorService.Generate("d", MinimalUser(), UefiReport, Staging);
        var bios = ManifestGeneratorService.Generate("d", MinimalUser(), UefiReport with { IsUefi = false }, Staging);

        uefi.Hardware.FirmwareType.Should().Be("uefi");
        bios.Hardware.FirmwareType.Should().Be("bios");
    }

    [Fact]
    public void Bare_browser_names_fall_back_to_minimal_browser_entries()
    {
        var setup = MinimalUser() with { SelectedBrowserNames = ["Firefox", "Edge"] };

        var manifest = ManifestGeneratorService.Generate("d", setup, UefiReport, Staging);

        manifest.Browsers.Should().HaveCount(2);
        manifest.Browsers[0].Name.Should().Be("Firefox");
        manifest.Browsers[0].ProfileStagingPath.Should().BeEmpty();
        manifest.Browsers[0].IncludesPasswords.Should().BeFalse();
    }

    [Fact]
    public void Rich_browser_list_wins_over_bare_names()
    {
        var rich = new BrowserMigration { Name = "Firefox", Engine = "gecko" };
        var setup = MinimalUser() with
        {
            SelectedBrowsers = [rich],
            SelectedBrowserNames = ["ShouldBeIgnored"],
        };

        var manifest = ManifestGeneratorService.Generate("d", setup, UefiReport, Staging);

        manifest.Browsers.Should().ContainSingle().Which.Should().BeSameAs(rich);
    }

    [Fact]
    public void User_identity_passes_through_and_the_password_is_hashed()
    {
        var manifest = ManifestGeneratorService.Generate(
            "fedora-kde", MinimalUser(), UefiReport, Staging);

        manifest.DistroId.Should().Be("fedora-kde");
        manifest.User.WindowsUsername.Should().Be("Winnie");
        manifest.User.PreferredLinuxUsername.Should().Be("winnie");
        manifest.User.LinuxPasswordCrypted.Should().StartWith("$6$rounds=200000$");
        manifest.Files.StagingPath.Should().Be(Staging.StagingDirectory);
        manifest.Files.TotalBytes.Should().Be(Staging.TotalBytesCopied);
    }

    [Fact]
    public void Never_writes_the_plain_text_password_into_the_manifest()
    {
        var manifest = ManifestGeneratorService.Generate(
            "fedora-kde", MinimalUser(), UefiReport, Staging);

        System.Text.Json.JsonSerializer.Serialize(manifest)
            .Should().NotContain("hunter2hunter2",
                "the manifest lands on a FAT32 partition that has no access control");
    }
}
