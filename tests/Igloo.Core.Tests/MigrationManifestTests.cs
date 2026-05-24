using System.Text.Json;
using FluentAssertions;
using Igloo.Core.Models;
using Xunit;

namespace Igloo.Core.Tests;

public class MigrationManifestTests
{
    /// <summary>
    /// The manifest is the wire format between Igloo's Windows half and the Linux first-boot agent.
    /// If JSON round-tripping breaks, the agent can't read what the Windows side wrote.
    /// This test guards that contract.
    /// </summary>
    [Fact]
    public void Manifest_round_trips_through_json_without_data_loss()
    {
        var original = new MigrationManifest
        {
            DistroId = "fedora-kde",
            User = new MigrationUser
            {
                WindowsUsername = "Gilles",
                PreferredLinuxUsername = "gilles",
                FullName = "Gilles D'huyvetter",
                Locale = "nl_BE.UTF-8",
                Timezone = "Europe/Brussels",
                Keymap = "be"
            },
            Files = new FileMigrationPlan
            {
                StagingPath = @"C:\Igloo\staging\files",
                TotalBytes = 12_345_678_900L,
                IncludedFolders = new[] { "Documents", "Pictures", "Desktop" }
            },
            Hardware = new HardwareProfile
            {
                GpuVendor = "nvidia",
                NeedsNonFreeCodecs = true,
                SecureBootEnabled = true,
                FirmwareType = "uefi"
            }
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<MigrationManifest>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.DistroId.Should().Be("fedora-kde");
        roundTripped.User.PreferredLinuxUsername.Should().Be("gilles");
        roundTripped.User.Keymap.Should().Be("be");
        roundTripped.Files.TotalBytes.Should().Be(12_345_678_900L);
        roundTripped.Hardware.GpuVendor.Should().Be("nvidia");
        roundTripped.SchemaVersion.Should().Be(1);
    }
}
