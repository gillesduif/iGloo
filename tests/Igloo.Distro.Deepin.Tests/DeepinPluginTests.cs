using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Xunit;

namespace Igloo.Distro.Deepin.Tests;

public sealed class DeepinPluginTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Packaged_metadata_loads_the_verified_release_without_fallbacks()
    {
        var plugin = new DeepinPlugin();

        plugin.Id.Should().Be("deepin");
        plugin.Metadata.InstallerType.Should().Be(InstallerType.Custom);
        plugin.Metadata.DefaultDesktopEnvironment.Should().Be("DDE");
        plugin.Metadata.IsoDownloadUrl.AbsoluteUri.Should().Be(
            "https://cdimage.deepin.com/releases/25.2.0/amd64/deepin-desktop-community-25.2.0-amd64.iso");
        plugin.Metadata.IsoSha256.Should().Be(
            "f875c9a605bfe6a8425d1d353a3c1ec755bf37f5b0a3231ca19e2145da0ff450");
        plugin.Metadata.MinimumRequirements.MinRamBytes.Should().Be(4 * GiB);
        plugin.Metadata.MinimumRequirements.MinDiskBytes.Should().Be(64 * GiB);
        plugin.Metadata.MinimumRequirements.RequiresUefi.Should().BeTrue();
        plugin.Metadata.MinimumRequirements.Requires64Bit.Should().BeTrue();
        plugin.Metadata.IsoGpgSignatureUrl.Should().BeNull();

        var manifestPath = Path.Join(Path.GetDirectoryName(typeof(DeepinPlugin).Assembly.Location)!, "distro.json");
        var metadata = JsonSerializer.Deserialize<DistroManifest>(File.ReadAllText(manifestPath));
        metadata!.Status.Should().Be("coming-soon");
    }

    [Theory]
    [InlineData("coming-soon")]
    [InlineData("available")]
    [InlineData(null)]
    public async Task Catalog_status_cannot_unlock_any_execution_endpoint(string? status)
    {
        var data = ValidMetadata();
        data["status"] = status;
        using var file = new MetadataFile(data.ToJsonString());
        var plugin = new DeepinPlugin(file.Path);

        plugin.CheckCompatibility(Report()).Should().ContainSingle(f =>
            f.Code == "DEEPIN_INSTALLATION_UNVERIFIED" && f.Severity == FindingSeverity.Blocker);
        Assert.Throws<NotSupportedException>(plugin.GetInstallerBootSpec)
            .Message.Should().Contain("preserving Windows");
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.RenderInstallerConfigAsync(Manifest()));
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.GetAgentPayloadAsync());
    }

    [Fact]
    public void Compatibility_does_not_duplicate_generic_firmware_or_BitLocker_findings()
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        var plugin = new DeepinPlugin(file.Path);
        var report = Report() with { IsUefi = false, BitLocker = BitLockerState.EncryptedAndLocked };

        plugin.CheckCompatibility(report).Should().ContainSingle()
            .Which.Code.Should().Be("DEEPIN_INSTALLATION_UNVERIFIED");
    }

    [Fact]
    public void Secure_Boot_adds_an_explicit_blocker()
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        var plugin = new DeepinPlugin(file.Path);

        plugin.CheckCompatibility(Report() with { SecureBootEnabled = true }).Should().Contain(f =>
            f.Code == "DEEPIN_SECURE_BOOT" && f.Severity == FindingSeverity.Blocker);
    }

    [Theory]
    [InlineData(0, "DEEPIN_RAM_MINIMUM", FindingSeverity.Blocker)]
    [InlineData(4 * GiB - 1, "DEEPIN_RAM_MINIMUM", FindingSeverity.Blocker)]
    [InlineData(4 * GiB, "DEEPIN_RAM_RECOMMENDED", FindingSeverity.Warning)]
    [InlineData(8 * GiB - 1, "DEEPIN_RAM_RECOMMENDED", FindingSeverity.Warning)]
    public void RAM_findings_respect_minimum_and_recommended_boundaries(
        long bytes, string code, FindingSeverity severity)
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        var plugin = new DeepinPlugin(file.Path);

        plugin.CheckCompatibility(Report() with { TotalRamBytes = bytes })
            .Where(f => f.Code.StartsWith("DEEPIN_RAM_", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Match<PreflightFinding>(f =>
                f.Code == code && f.Severity == severity);
    }

    [Theory]
    [InlineData(8 * GiB)]
    [InlineData(16 * GiB)]
    public void Recommended_RAM_does_not_raise_a_RAM_warning(long bytes)
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        var plugin = new DeepinPlugin(file.Path);

        plugin.CheckCompatibility(Report() with { TotalRamBytes = bytes }).Should().NotContain(f =>
            f.Code.StartsWith("DEEPIN_RAM_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NVIDIA")]
    [InlineData("nvidia")]
    public void NVIDIA_remains_a_warning_without_a_driver_installation_promise(string vendor)
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        var plugin = new DeepinPlugin(file.Path);

        var finding = plugin.CheckCompatibility(Report() with { GpuVendor = vendor })
            .Single(f => f.Code == "DEEPIN_NVIDIA_UNVERIFIED");
        finding.Severity.Should().Be(FindingSeverity.Warning);
        finding.Message.Should().Contain("have not been validated");
    }

    [Theory]
    [InlineData(null, 0, "dual-boot", 0)]
    [InlineData("", 512 * GiB, "dual-boot", 64)]
    [InlineData("{{IGLOO_TARGET_DISK}}", 512 * GiB, "dual-boot", 64)]
    [InlineData("/dev/sda", 512 * GiB, "replace", 64)]
    [InlineData("/dev/nvme0n1", 512 * GiB, "dual-boot", 64)]
    [InlineData("same-model-as-another-disk", 512 * GiB, "dual-boot", 64)]
    public async Task Missing_unsafe_or_ambiguous_target_information_never_produces_a_partition_recipe(
        string? model, long bytes, string mode, int allocation)
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        var plugin = new DeepinPlugin(file.Path);
        var manifest = Manifest();
        manifest = manifest with
        {
            Hardware = manifest.Hardware with
            {
                TargetDiskModel = model,
                TargetDiskBytes = bytes,
                InstallMode = mode,
                LinuxPartitionSizeGb = allocation,
            },
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.RenderInstallerConfigAsync(manifest));
    }

    [Fact]
    public async Task Missing_manifest_sections_or_template_placeholders_cannot_enable_rendering()
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        var plugin = new DeepinPlugin(file.Path);
        var missing = Manifest() with { Hardware = null!, Files = null!, User = null! };
        var placeholders = Manifest() with
        {
            DistroId = "{{DISTRO}}",
            User = Manifest().User with { PreferredLinuxUsername = "{{USERNAME}}", Locale = "{{LOCALE}}" },
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.RenderInstallerConfigAsync(missing));
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.RenderInstallerConfigAsync(placeholders));
    }

    [Fact]
    public async Task Null_inputs_are_rejected_before_inspecting_or_rendering_them()
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        var plugin = new DeepinPlugin(file.Path);

        Assert.Throws<ArgumentNullException>(() => plugin.CheckCompatibility(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => plugin.RenderInstallerConfigAsync(null!));
        Assert.Throws<ArgumentNullException>(() => new DeepinPlugin(null!));
        Assert.Throws<ArgumentException>(() => new DeepinPlugin(" "));
    }

    [Fact]
    public async Task Cancellation_is_honored_without_loading_installer_resources()
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        var plugin = new DeepinPlugin(file.Path);
        var token = new CancellationToken(canceled: true);

        var render = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugin.RenderInstallerConfigAsync(Manifest(), token));
        var payload = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.GetAgentPayloadAsync(token));
        render.CancellationToken.Should().Be(token);
        payload.CancellationToken.Should().Be(token);
    }

    [Fact]
    public async Task Malformed_stale_installer_and_agent_resources_cannot_activate_the_plugin()
    {
        var metadata = ValidMetadata();
        metadata["status"] = "available";
        using var file = new MetadataFile(metadata.ToJsonString());
        var directory = Path.GetDirectoryName(file.Path)!;
        var settings = Path.Join(directory, "settings.ini");
        var agentDirectory = Path.Join(directory, "agent");
        var agent = Path.Join(agentDirectory, "agent.py");
        Directory.CreateDirectory(agentDirectory);
        try
        {
            await File.WriteAllTextAsync(settings, "[broken\nDI_INSTALL_MODE={{MODE}}\nDI_DEVICE_LIST=/dev/sda");
            await File.WriteAllTextAsync(agent, "not a valid Python payload {{MANIFEST}}");
            var plugin = new DeepinPlugin(file.Path);

            Assert.Throws<NotSupportedException>(plugin.GetInstallerBootSpec);
            await Assert.ThrowsAsync<NotSupportedException>(() => plugin.RenderInstallerConfigAsync(Manifest()));
            await Assert.ThrowsAsync<NotSupportedException>(() => plugin.GetAgentPayloadAsync());
        }
        finally
        {
            File.Delete(settings);
            File.Delete(agent);
            Directory.Delete(agentDirectory);
        }
    }

    [Fact]
    public void A_missing_metadata_resource_has_no_fallback()
    {
        using var file = new MetadataFile(ValidMetadata().ToJsonString());
        File.Delete(file.Path);

        Assert.Throws<FileNotFoundException>(() => new DeepinPlugin(file.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"iso\":42}")]
    public void Malformed_or_incomplete_JSON_has_no_fallback(string json)
    {
        using var file = new MetadataFile(json);

        Assert.Throws<JsonException>(() => new DeepinPlugin(file.Path));
    }

    [Fact]
    public void A_null_metadata_document_has_no_fallback()
    {
        using var file = new MetadataFile("null");

        Assert.Throws<InvalidDataException>(() => new DeepinPlugin(file.Path));
    }

    [Theory]
    [InlineData("id", "null")]
    [InlineData("id", "\"debian\"")]
    [InlineData("displayName", "\" \"")]
    [InlineData("description", "null")]
    [InlineData("installerType", "\"DebianInstaller\"")]
    [InlineData("installerType", "\"Calamares\"")]
    [InlineData("defaultDesktopEnvironment", "\"GNOME\"")]
    [InlineData("iso", "null")]
    [InlineData("iso.downloadUrl", "null")]
    [InlineData("iso.downloadUrl", "\"relative.iso\"")]
    [InlineData("iso.downloadUrl", "\"http://example.invalid/deepin.iso\"")]
    [InlineData("iso.downloadUrl", "\"https://example.invalid/download\"")]
    [InlineData("iso.downloadUrl", "\"https://user@example.invalid/deepin.iso\"")]
    [InlineData("iso.sha256", "null")]
    [InlineData("iso.sha256", "\"\"")]
    [InlineData("iso.sha256", "\"abcd\"")]
    [InlineData("iso.sha256", "\"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"")]
    [InlineData("minimumRequirements", "null")]
    [InlineData("minimumRequirements.minRamBytes", "0")]
    [InlineData("minimumRequirements.minDiskBytes", "0")]
    [InlineData("minimumRequirements.minDiskBytes", "-1")]
    [InlineData("minimumRequirements.requiresUefi", "false")]
    [InlineData("minimumRequirements.requires64Bit", "false")]
    [InlineData("tags", "null")]
    [InlineData("screenshots", "null")]
    public void Invalid_metadata_values_are_rejected(string path, string jsonValue)
    {
        var data = ValidMetadata();
        var parts = path.Split('.');
        var owner = parts.Length == 1 ? data : data[parts[0]]!.AsObject();
        owner[parts[^1]] = JsonNode.Parse(jsonValue);
        using var file = new MetadataFile(data.ToJsonString());

        Assert.Throws<InvalidDataException>(() => new DeepinPlugin(file.Path));
    }

    private static JsonObject ValidMetadata() => JsonNode.Parse("""
        {
          "id": "deepin", "displayName": "deepin", "description": "Test metadata",
          "status": "coming-soon", "defaultDesktopEnvironment": "DDE", "installerType": "Custom",
          "iso": {
            "downloadUrl": "https://example.invalid/deepin.iso",
            "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
          },
          "minimumRequirements": {
            "minRamBytes": 4294967296, "minDiskBytes": 68719476736,
            "requiresUefi": true, "requires64Bit": true
          }
        }
        """)!.AsObject();

    private static PreflightReport Report() => new()
    {
        IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
        BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "Intel",
        TotalRamBytes = 8 * GiB, Findings = [],
    };

    private static MigrationManifest Manifest() => new()
    {
        DistroId = "deepin",
        User = new MigrationUser { WindowsUsername = "user", PreferredLinuxUsername = "user" },
        Files = new FileMigrationPlan { StagingPath = "C:\\Igloo\\staging" },
        Hardware = new HardwareProfile
        {
            TargetDiskModel = "test-disk", TargetDiskBytes = 512 * GiB,
            InstallMode = "dual-boot", LinuxPartitionSizeGb = 64,
        },
    };

    private sealed class MetadataFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Join(
            System.IO.Path.GetTempPath(), "igloo-deepin-metadata-" + Guid.NewGuid().ToString("N"));

        internal string Path { get; }

        internal MetadataFile(string contents)
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Join(_directory, "distro.json");
            File.WriteAllText(Path, contents);
        }

        public void Dispose()
        {
            File.Delete(Path);
            Directory.Delete(_directory);
        }
    }
}
