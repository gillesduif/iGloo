using System.Text;
using FluentAssertions;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Xunit;

namespace Igloo.Distro.BazziteDx.Tests;

public class BazziteDxPluginTests
{
    private static readonly BazziteDxPlugin Plugin = new();

    private static MigrationManifest Manifest(
        string gpuVendor = "amd",
        bool secureBoot = false,
        string installMode = "replace",
        string? computerName = "DESKTOP-Living",
        string? passwordHash = "$6$rounds=200000$salt$hash") => new()
        {
            DistroId = "bazzite-dx",
            User = new MigrationUser
            {
                WindowsUsername = "Gilles",
                WindowsComputerName = computerName,
                PreferredLinuxUsername = "gilles",
                FullName = "Gilles D",
                Locale = "fr_BE.UTF-8",
                Timezone = "Europe/Brussels",
                Keymap = "be",
                LinuxPasswordCrypted = passwordHash,
            },
            Files = new FileMigrationPlan
            {
                StagingPath = "",
                IncludedFolders = ["Documents", "Pictures"],
                Folders =
                [
                    new MigrationFolder { Name = "Documents", SourceRelativePath = "OneDrive/Documents" },
                    new MigrationFolder { Name = "Pictures", SourceRelativePath = "Pictures" },
                ],
            },
            Browsers =
            [
                new BrowserMigration
                {
                    Name = "Firefox", Engine = "gecko",
                    SourceRelativePath = "AppData/Roaming/Mozilla/Firefox",
                    DestRelativePath = ".mozilla/firefox",
                },
                new BrowserMigration
                {
                    Name = "Google Chrome", Engine = "chromium",
                    SourceRelativePath = "AppData/Local/Google/Chrome/User Data",
                },
            ],
            WifiNetworks =
            [
                new WifiNetwork { Ssid = "Home Net", Security = "wpa-psk", Psk = "s3cret", IsPrimary = true },
            ],
            Hardware = new HardwareProfile
            {
                GpuVendor = gpuVendor,
                SecureBootEnabled = secureBoot,
                InstallMode = installMode,
                TargetDiskBytes = 512_110_190_592,
                TargetDiskModel = "Samsung SSD 990 PRO",
            },
        };

    private static async Task<string> RenderAsync(MigrationManifest manifest)
    {
        var config = await Plugin.RenderInstallerConfigAsync(manifest);
        config.FileName.Should().Be("ks.cfg");
        return Encoding.UTF8.GetString(config.Contents.Span);
    }

    private static PreflightReport Report(bool uefi = true, bool secureBoot = false,
        string gpu = "amd", long ramBytes = 16L * 1024 * 1024 * 1024) => new()
        {
            IsUefi = uefi,
            SecureBootEnabled = secureBoot,
            TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted,
            Disks = [],
            GpuVendor = gpu,
            TotalRamBytes = ramBytes,
            Findings = [],
        };

    //   Metadata

    [Fact]
    public void Metadata_comes_from_distro_json()
    {
        Plugin.Id.Should().Be("bazzite-dx");
        Plugin.Metadata.DisplayName.Should().Be("Bazzite DX");
        Plugin.Metadata.InstallerType.Should().Be(InstallerType.Anaconda);
        Plugin.Metadata.MinimumRequirements.RequiresUefi.Should().BeTrue();
        Plugin.Metadata.IsoDownloadUrl.AbsoluteUri.Should().Contain("Fedora-Everything-netinst");
    }

    //   Kickstart

    [Fact]
    public async Task Kickstart_deploys_the_image_instead_of_installing_packages()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().Contain(
            "ostreecontainer --url=ghcr.io/ublue-os/bazzite-dx:stable --transport=registry --no-signature-verification");
        ks.Should().NotMatchRegex(@"(?m)^%packages", "Anaconda rejects a package section next to ostreecontainer");
        ks.Should().NotMatchRegex(@"(?m)^url ", "the image is the install source");
    }

    [Fact]
    public async Task Nvidia_machines_get_the_nvidia_image()
    {
        var ks = await RenderAsync(Manifest(gpuVendor: "NVIDIA"));

        ks.Should().Contain("ostreecontainer --url=ghcr.io/ublue-os/bazzite-dx-nvidia:stable ");
        ks.Should().Contain("bootc switch --mutate-in-place --enforce-container-sigpolicy --transport registry ghcr.io/ublue-os/bazzite-dx-nvidia:stable");
    }

    [Fact]
    public async Task Kickstart_switches_to_signed_updates()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().Contain("bootc switch --mutate-in-place --enforce-container-sigpolicy --transport registry ghcr.io/ublue-os/bazzite-dx:stable");
    }

    [Fact]
    public async Task Every_placeholder_is_filled()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().NotContain("{{");
        ks.Should().NotContain("}}");
    }

    [Fact]
    public async Task Kickstart_has_unix_line_endings()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().NotContain("\r");
    }

    [Fact]
    public async Task User_gets_the_crypted_password_and_wheel()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().Contain(
            "user --name=gilles --groups=wheel --gecos=\"Gilles D\" --password=\"$6$rounds=200000$salt$hash\" --iscrypted");
    }

    [Fact]
    public async Task Missing_password_locks_the_account()
    {
        var ks = await RenderAsync(Manifest(passwordHash: null));

        ks.Should().Contain("user --name=gilles --groups=wheel --gecos=\"Gilles D\" --lock");
    }

    [Fact]
    public async Task Locale_keyboard_timezone_and_hostname_are_rendered()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().Contain("lang fr_BE.UTF-8");
        ks.Should().Contain("keyboard --xlayouts='be'");
        ks.Should().Contain("timezone Europe/Brussels --utc");
        ks.Should().Contain("--hostname=desktop-living");
    }

    [Fact]
    public async Task Long_hostnames_are_capped_to_what_bazzite_keeps()
    {
        var ks = await RenderAsync(Manifest(computerName: "Workstation-Of-The-Accounting-Team"));

        ks.Should().Contain("--hostname=workstation-of-the\n");
    }

    [Theory]
    [InlineData("replace", "INSTALL_MODE=\"replace\"")]
    [InlineData("dual-boot", "INSTALL_MODE=\"dual-boot\"")]
    public async Task Install_mode_reaches_the_partitioning_script(string mode, string expected)
    {
        var ks = await RenderAsync(Manifest(installMode: mode));

        ks.Should().Contain(expected);
        ks.Should().Contain("TARGET_BYTES=\"512110190592\"");
        ks.Should().Contain("echo \"clearpart --none\"");
        ks.Should().NotContain("echo \"clearpart --linux", "it deletes every other Linux install on the disk");
    }

    [Fact]
    public async Task Migration_maps_are_rendered_into_their_heredocs()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().Contain("Documents|OneDrive/Documents\nPictures|Pictures\nFOLDERMAP");
        ks.Should().Contain("AppData/Roaming/Mozilla/Firefox|.mozilla/firefox\nBROWSERMAP");
        ks.Should().Contain("Google Chrome|AppData/Local/Google/Chrome/User Data\nCHROMIUMMAP");
        ks.Should().Contain("Home Net\ts3cret\nWIFILIST");
    }

    [Fact]
    public async Task User_files_land_under_var_on_the_image_based_system()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().Contain("USER_HOME=\"$SYSIMAGE/var/home/$USERNAME\"");
        ks.Should().Contain("echo \"ghcr.io/ublue-os/bazzite-dx:stable\" > \"$SYSIMAGE/var/lib/igloo/image\"");
    }

    [Fact]
    public async Task First_boot_unit_waits_for_bazzite_hardware_setup()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().Contain("After=bazzite-hardware-setup.service");
        ks.Should().Contain("Before=display-manager.service");
        ks.Should().Contain("ExecStart=/opt/igloo/first-boot.sh");
    }

    [Fact]
    public async Task Passwordless_autologin_is_turned_off_and_plasma_preselected()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().Contain("ln -sf /dev/null \"$SYSIMAGE/etc/systemd/system/bazzite-autologin.service\"");
        ks.Should().Contain("Session=/usr/share/wayland-sessions/plasma.desktop");
        ks.Should().NotContain("desktop_autologin");
    }

    [Fact]
    public async Task Files_written_from_the_installer_are_relabelled()
    {
        var ks = await RenderAsync(Manifest());

        ks.Should().Contain("restorecon -RF /etc/selinux /etc/systemd/system /var/home /var/opt /var/lib/igloo");
    }

    [Theory]
    [InlineData(true, "SECURE_BOOT=\"1\"")]
    [InlineData(false, "SECURE_BOOT=\"0\"")]
    public async Task Secure_boot_key_enrollment_follows_the_windows_state(bool secureBoot, string expected)
    {
        var ks = await RenderAsync(Manifest(secureBoot: secureBoot));

        ks.Should().Contain(expected);
    }

    [Fact]
    public async Task Windows_values_cannot_run_commands_in_the_root_post_script()
    {
        var manifest = Manifest() with
        {
            User = Manifest().User with { WindowsUsername = "a$(reboot)`id`\"b", FullName = "Jo \"JJ\" D" },
            Hardware = Manifest().Hardware with { TargetDiskModel = "Disk $(rm -rf /)" },
        };

        var ks = await RenderAsync(manifest);

        ks.Should().Contain("WINDOWS_USERNAME=\"a\\$(reboot)\\`id\\`\\\"b\"");
        ks.Should().Contain("TARGET_MODEL=\"Disk \\$(rm -rf /)\"");
        ks.Should().Contain("--gecos=\"Jo \\\"JJ\\\" D\"");
    }

    //   Image references

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ghcr.io/ublue-os/bazzite-dx:stable; rm -rf /")]
    [InlineData("ghcr.io/ublue-os/bazzite-dx:$(reboot)")]
    [InlineData("ghcr.io/ublue-os/bazzite dx:stable")]
    [InlineData("--transport=oci-archive:/tmp/x")]
    public void Unsafe_image_references_fall_back_to_the_default(string? image)
    {
        BazziteDxPlugin.ValidImageOr(image, BazziteDxPlugin.DefaultImage)
            .Should().Be(BazziteDxPlugin.DefaultImage);
    }

    [Theory]
    [InlineData("ghcr.io/ublue-os/bazzite-dx:stable")]
    [InlineData("ghcr.io/ublue-os/bazzite-dx-nvidia:stable-42.20260901")]
    [InlineData("ghcr.io/ublue-os/bazzite-dx@sha256:d63c82406dfb8c0dbd451b579150175fad27a39f9a835f4811d00906b61853b8")]
    public void Valid_image_references_are_kept(string image)
    {
        BazziteDxPlugin.ValidImageOr(image, BazziteDxPlugin.DefaultImage).Should().Be(image);
    }

    [Theory]
    [InlineData("desktop-living", "desktop-living")]
    [InlineData("nineteen-characters", "nineteen-characters")]
    [InlineData("exactly-twenty-chars", "exactly-twenty-char")]
    [InlineData("eighteen-character-x", "eighteen-character")]
    public void Hostname_is_capped_without_a_trailing_hyphen(string input, string expected)
    {
        BazziteDxPlugin.BazziteHostname(input).Should().Be(expected);
    }

    //   Preflight

    [Fact]
    public void Legacy_bios_is_a_blocker()
    {
        Plugin.CheckCompatibility(Report(uefi: false))
            .Should().Contain(f => f.Code == "BAZZITE_REQUIRES_UEFI" && f.Severity == FindingSeverity.Blocker);
    }

    [Fact]
    public void Secure_boot_warns_about_the_mok_enrollment()
    {
        var finding = Plugin.CheckCompatibility(Report(secureBoot: true))
            .Single(f => f.Code == "BAZZITE_SECURE_BOOT_KEY");

        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Message.Should().Contain("universalblue");
    }

    [Fact]
    public void Nvidia_gets_an_info_finding_about_the_image()
    {
        Plugin.CheckCompatibility(Report(gpu: "nvidia"))
            .Should().Contain(f => f.Code == "BAZZITE_NVIDIA_IMAGE" && f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void A_clean_uefi_machine_has_no_findings()
    {
        Plugin.CheckCompatibility(Report()).Should().BeEmpty();
    }

    [Fact]
    public void Low_ram_is_a_warning()
    {
        Plugin.CheckCompatibility(Report(ramBytes: 4L * 1024 * 1024 * 1024))
            .Should().Contain(f => f.Code == "RAM_BELOW_RECOMMENDED" && f.Severity == FindingSeverity.Warning);
    }

    //   Agent payload and boot

    [Fact]
    public async Task Agent_payload_ships_the_bazzite_agent_with_the_fedora_steps_it_imports()
    {
        var payload = await Plugin.GetAgentPayloadAsync();
        var names = payload.Files.Select(f => f.RelativePath).ToList();

        names.Should().BeEquivalentTo(
            ["first-boot.sh", "agent.py", "fedora_agent.py", "display-apply.py", "igloo_boot.py"]);
        payload.Files.Single(f => f.RelativePath == "first-boot.sh").Executable.Should().BeTrue();
        payload.Files.Single(f => f.RelativePath == "agent.py").Executable.Should().BeTrue();
        foreach (var file in payload.Files)
            file.Contents.ToArray().Should().NotContain((byte)'\r', file.RelativePath);

        var agent = Encoding.UTF8.GetString(payload.Files.Single(f => f.RelativePath == "agent.py").Contents.Span);
        agent.Should().Contain("import fedora_agent as base");
        var fedora = Encoding.UTF8.GetString(payload.Files.Single(f => f.RelativePath == "fedora_agent.py").Contents.Span);
        fedora.Should().Contain("ROOT_MOUNT = \"/\"", "the Bazzite agent points it at /sysroot");
    }

    [Fact]
    public void Boot_spec_uses_the_fedora_netinstall_with_the_kickstart_on_oemdrv()
    {
        var spec = Plugin.GetInstallerBootSpec();

        spec.VolumeLabel.Should().Be("OEMDRV");
        spec.KernelCmdline.Should().Contain("inst.ks=hd:LABEL={LABEL}:/ks.cfg");
        spec.KernelCmdline.Should().Contain("inst.stage2=hd:LABEL={LABEL}:");
        spec.KernelCmdline.Should().NotContain("nouveau");
        spec.KernelIsoPaths.Should().Contain("images/pxeboot/vmlinuz");
        spec.ExtraIsoFiles.Should().Contain(f => f.IsoRelativePath == "images/install.img");
    }
}
