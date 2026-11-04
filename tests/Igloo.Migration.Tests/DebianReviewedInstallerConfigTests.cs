using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Distro.Debian;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class DebianReviewedInstallerConfigTests
{
    [Fact]
    public async Task ExactObservedExtentRendersBoundPartmanScripts()
    {
        var plugin = new DebianPlugin();
        Assert.IsAssignableFrom<IReviewedInstallerConfigProvider>(plugin);
        var diskGuid = Guid.NewGuid();
        var manifest = Manifest(diskGuid);
        var extent = new ReviewedInstallerFreeExtentV1(Guid.NewGuid(), diskGuid,
            Guid.NewGuid(), 5_906_628_608, 26_305_609_216, 20UL * 1024 * 1024 * 1024);

        var config = await plugin.RenderReviewedInstallerConfigAsync(manifest, extent);
        var setup = Encoding.UTF8.GetString(config.Extras.Single(extra =>
            extra.RelativePath == "igloo-initrd/igloo-bound-free-setup.sh").Contents.Span);
        Assert.Contains("expected_free_offset=\"5906628608\"", setup, StringComparison.Ordinal);
        Assert.Contains("expected_free_size=\"26305609216\"", setup, StringComparison.Ordinal);
        Assert.Contains(diskGuid.ToString("D"), setup, StringComparison.Ordinal);
        Assert.Contains($"expected_linux_esp=\"{extent.LinuxEspPartitionGuid:D}\"", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("expected_free_offset=\"none\"", setup, StringComparison.Ordinal);
        Assert.Equal(4, config.Extras.Count);

        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderReviewedInstallerConfigAsync(
            manifest, extent with { TargetDiskGuid = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderReviewedInstallerConfigAsync(
            manifest, extent with { GenerationId = Guid.Empty }));
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderReviewedInstallerConfigAsync(
            manifest, extent with { LinuxEspPartitionGuid = Guid.Empty }));
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderReviewedInstallerConfigAsync(
            manifest, extent with { LinuxEspPartitionGuid = diskGuid }));
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderReviewedInstallerConfigAsync(
            manifest, extent with { RootMinimumBytes = extent.SizeBytes + 512 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.RenderReviewedInstallerConfigAsync(
            manifest, extent with { OffsetBytes = extent.OffsetBytes + 512 }));
    }

    private static MigrationManifest Manifest(Guid targetDiskGuid) => new()
    {
        DistroId = "debian",
        User = new MigrationUser
        {
            WindowsUsername = "fixture", PreferredLinuxUsername = "fixture",
            LinuxPasswordCrypted = "$6$salt$hashedpassword",
        },
        Files = new FileMigrationPlan { StagingPath = "fixture" },
        Hardware = new HardwareProfile
        {
            InstallMode = "dual-boot", TargetDiskGuid = targetDiskGuid,
            TargetDiskBytes = 30L * 1024 * 1024 * 1024,
        },
    };
}
