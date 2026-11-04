using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Distro.LinuxmintCinnamon;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class MintArtifactStagingTests
{
    [Fact]
    public void MintPathRequiresUefiAndKeepsSecureBootAvailable()
    {
        var plugin = new LinuxmintCinnamonPlugin();
        Assert.True(plugin.Metadata.MinimumRequirements.RequiresUefi);
        var report = new PreflightReport
        {
            IsUefi = true, SecureBootEnabled = true, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "none",
            TotalRamBytes = 8L * 1024 * 1024 * 1024, Findings = [],
        };
        Assert.DoesNotContain(plugin.CheckCompatibility(report), finding =>
            finding.Severity == FindingSeverity.Blocker);
    }

    [Fact]
    public async Task RealMintProviderStagesInstallerConfigAndCompleteFirstBootPayload()
    {
        var staging = Path.Join(Path.GetTempPath(), "igloo-mint-artifacts-" + Guid.NewGuid());
        Directory.CreateDirectory(staging);
        try
        {
            var plugin = new LinuxmintCinnamonPlugin();
            var manifest = Manifest(staging);
            var boot = plugin.GetInstallerBootSpec();
            Assert.Contains("iso-scan/filename=/mint.iso toram ---", boot.KernelCmdline, StringComparison.Ordinal);
            Assert.DoesNotContain("iso-scan/copy_iso_to_ram", boot.KernelCmdline, StringComparison.Ordinal);
            Assert.False(boot.MigrationDataOnSeed);

            await PluginArtifactWriter.WriteAsync(plugin, manifest, staging, NullLogger.Instance);

            var preseed = await File.ReadAllTextAsync(Path.Join(staging, "preseed.cfg"));
            Assert.Contains("d-i netcfg/get_hostname string mint-lab", preseed, StringComparison.Ordinal);
            Assert.Contains("d-i passwd/user-password-crypted password $6$fixture$hashed", preseed, StringComparison.Ordinal);
            // v0.2-alpha partitioning: Ubiquity's biggest_free into the gap iGloo left,
            // with partman-auto/method unset (setting it means whole-disk).
            Assert.Contains("d-i partman-auto/init_automatically_partition select biggest_free", preseed, StringComparison.Ordinal);
            Assert.DoesNotContain("d-i partman-auto/method", preseed, StringComparison.Ordinal);
            Assert.DoesNotContain("partman/early_command", preseed, StringComparison.Ordinal);
            Assert.DoesNotContain("igloo-partman.sh", preseed, StringComparison.Ordinal);
            Assert.DoesNotContain("igloo-no-selected-disk", preseed, StringComparison.Ordinal);
            // Two-phase bootstrap (ADR-012): success_command records the seed identity
            // from the kernel command line and writes the first-boot bootstrap; the seed
            // is mounted by exact PARTUUID at first boot and verified there.
            Assert.Contains("ubiquity ubiquity/success_command string", preseed, StringComparison.Ordinal);
            Assert.Contains("igloo.seedpartuuid=", preseed, StringComparison.Ordinal);
            Assert.Contains("igloo.seeddiskguid=", preseed, StringComparison.Ordinal);
            Assert.Contains("/target/var/lib/igloo/selected-seed-partuuid", preseed, StringComparison.Ordinal);
            Assert.Contains("/dev/disk/by-partuuid/", preseed, StringComparison.Ordinal);
            Assert.Contains("igloo-agent/igloo-verify-seed.py /opt/igloo/igloo-verify-seed.py", preseed, StringComparison.Ordinal);
            Assert.Contains("python3 /opt/igloo/igloo-verify-seed.py", preseed, StringComparison.Ordinal);
            Assert.Contains("igloo-seed-identity.json /var/lib/igloo/seed-identity.json", preseed, StringComparison.Ordinal);
            Assert.Contains("Before=display-manager.service", preseed, StringComparison.Ordinal);
            Assert.Contains("exec /opt/igloo/first-boot.sh", preseed, StringComparison.Ordinal);
            // Debconf unescapes backslash-n inside command values to a physical
            // newline, which breaks Ubiquity's line protocol before partman runs.
            Assert.DoesNotContain("\\n", preseed, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", preseed, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', preseed);
            foreach (var hook in new[] { "igloo-partman.sh", "igloo-bound-free-choices.sh",
                         "igloo-bound-free-do-option.sh", "igloo-bound-efi-fstab.sh" })
                Assert.False(File.Exists(Path.Join(staging, hook)), hook);

            var agentDirectory = Path.Join(staging, "igloo-agent");
            string[] expected = ["agent.py", "first-boot.sh", "display-apply.py", "display-apply-gnome.py",
                "igloo_boot.py", "igloo-first-boot.service", "igloo-verify-seed.py",
                "igloo-bootstrap.sh", "igloo-bootstrap.service",
                "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz"];
            Assert.Equal(expected.Order(), Directory.GetFiles(agentDirectory).Select(Path.GetFileName).Order());
            foreach (var file in expected)
                Assert.True(new FileInfo(Path.Join(agentDirectory, file)).Length > 0, file);
            Assert.Contains("set -euo pipefail", await File.ReadAllTextAsync(Path.Join(agentDirectory, "first-boot.sh")),
                StringComparison.Ordinal);
            Assert.DoesNotContain('\r', await File.ReadAllTextAsync(Path.Join(agentDirectory, "first-boot.sh")));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public async Task PreseedRejectsLineInjectionAndUnhashedPassword()
    {
        var plugin = new LinuxmintCinnamonPlugin();
        var manifest = Manifest("fixture");
        var injected = manifest with { User = manifest.User with { FullName = "Mint User\nd-i partman-auto/method string regular" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(injected));

        var unhashed = manifest with { User = manifest.User with { LinuxPasswordCrypted = "plaintext" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(unhashed));

        var continued = manifest with { User = manifest.User with { FullName = "Mint User\\" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(continued));

        var replace = manifest with { Hardware = manifest.Hardware with { InstallMode = "replace" } };
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.RenderInstallerConfigAsync(replace));

        var missingTarget = manifest with { Hardware = manifest.Hardware with { TargetDiskGuid = null } };
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderInstallerConfigAsync(missingTarget));
    }

    [Fact]
    public async Task ReviewedMintRenderingValidatesTheExtentAndRendersTheSamePreseed()
    {
        var plugin = new LinuxmintCinnamonPlugin();
        var manifest = Manifest("fixture");
        var reviewed = new ReviewedInstallerFreeExtentV1(Guid.NewGuid(),
            manifest.Hardware.TargetDiskGuid!.Value,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            5906628608, 37043027456, 21474836480);
        var config = await plugin.RenderReviewedInstallerConfigAsync(manifest, reviewed);
        var legacy = await plugin.RenderInstallerConfigAsync(manifest);
        Assert.Empty(config.Extras);
        Assert.Equal(legacy.Contents.ToArray(), config.Contents.ToArray());

        var wrongDisk = reviewed with { TargetDiskGuid = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.RenderReviewedInstallerConfigAsync(manifest, wrongDisk));
        var outsideDisk = reviewed with { SizeBytes = 40000000000 };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.RenderReviewedInstallerConfigAsync(manifest, outsideDisk));
        var belowMinimum = reviewed with { RootMinimumBytes = 1 };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.RenderReviewedInstallerConfigAsync(manifest, belowMinimum));
    }

    private static MigrationManifest Manifest(string staging) => new()
    {
        DistroId = "linuxmint-cinnamon",
        User = new()
        {
            WindowsUsername = "winuser",
            WindowsComputerName = "MINT-LAB",
            PreferredLinuxUsername = "mintuser",
            FullName = "Mint User",
            LinuxPasswordCrypted = "$6$fixture$hashed",
        },
        Files = new() { StagingPath = staging },
        Hardware = new()
        {
            InstallMode = "dual-boot",
            TargetDiskGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            TargetDiskBytes = 42949672960,
        },
    };
}
