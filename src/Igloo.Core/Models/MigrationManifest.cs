using System.Text.Json.Serialization;

namespace Igloo.Core.Models;

/// <summary>
/// The migration manifest is the wire format between Igloo's Windows half and the first-boot agent
/// running on the freshly-installed Linux system. Serialised to JSON and written to OEMDRV.
///
/// Versioning: <see cref="SchemaVersion"/> is checked at runtime by the agent. Bump it for any
/// breaking change, and update every distro plugin's agent in lockstep.
/// </summary>
public sealed record MigrationManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("generatedAtUtc")]
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("iglooVersion")]
    public string IglooVersion { get; init; } = "0.0.1-alpha";

    /// <summary>The distro plugin id this manifest is meant for. Agent rejects mismatches.</summary>
    [JsonPropertyName("distroId")]
    public required string DistroId { get; init; }

    [JsonPropertyName("user")]
    public required MigrationUser User { get; init; }

    [JsonPropertyName("files")]
    public required FileMigrationPlan Files { get; init; }

    [JsonPropertyName("browsers")]
    public IReadOnlyList<BrowserMigration> Browsers { get; init; } = Array.Empty<BrowserMigration>();

    [JsonPropertyName("suggestedPackages")]
    public IReadOnlyList<SuggestedPackage> SuggestedPackages { get; init; } = Array.Empty<SuggestedPackage>();

    [JsonPropertyName("hardware")]
    public required HardwareProfile Hardware { get; init; }
}

public sealed record MigrationUser
{
    [JsonPropertyName("windowsUsername")] public required string WindowsUsername { get; init; }
    [JsonPropertyName("preferredLinuxUsername")] public required string PreferredLinuxUsername { get; init; }
    [JsonPropertyName("fullName")] public string? FullName { get; init; }
    [JsonPropertyName("locale")] public string Locale { get; init; } = "en_US.UTF-8";
    [JsonPropertyName("timezone")] public string Timezone { get; init; } = "UTC";
    [JsonPropertyName("keymap")] public string Keymap { get; init; } = "us";
}

public sealed record FileMigrationPlan
{
    [JsonPropertyName("stagingPath")] public required string StagingPath { get; init; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("includedFolders")] public IReadOnlyList<string> IncludedFolders { get; init; } = Array.Empty<string>();
}

public sealed record BrowserMigration
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("profileStagingPath")] public required string ProfileStagingPath { get; init; }
    [JsonPropertyName("includesPasswords")] public bool IncludesPasswords { get; init; }
}

public sealed record SuggestedPackage
{
    [JsonPropertyName("windowsAppName")] public required string WindowsAppName { get; init; }
    [JsonPropertyName("flatpakId")] public string? FlatpakId { get; init; }
    [JsonPropertyName("nativePackage")] public string? NativePackage { get; init; }
    [JsonPropertyName("autoInstall")] public bool AutoInstall { get; init; }
}

public sealed record HardwareProfile
{
    [JsonPropertyName("gpuVendor")] public string GpuVendor { get; init; } = "unknown";
    [JsonPropertyName("needsNonFreeCodecs")] public bool NeedsNonFreeCodecs { get; init; } = true;
    [JsonPropertyName("secureBootEnabled")] public bool SecureBootEnabled { get; init; }
    [JsonPropertyName("firmwareType")] public string FirmwareType { get; init; } = "uefi";

    /// <summary>
    /// Model string of the disk selected by the user for Linux installation
    /// (e.g. "Samsung SSD 870 EVO 500GB"). Used by the kickstart %pre script
    /// to cross-reference the Linux block device when size alone is ambiguous.
    /// </summary>
    [JsonPropertyName("targetDiskModel")] public string? TargetDiskModel { get; init; }

    /// <summary>
    /// Exact byte capacity of the target disk as reported by Windows WMI.
    /// The kickstart %pre script matches this against /sys/block/*/size * 512
    /// to identify the correct /dev/ device without relying on device-name ordering.
    /// </summary>
    [JsonPropertyName("targetDiskBytes")] public long TargetDiskBytes { get; init; }
}
