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

    /// <summary>
    /// Plaintext password chosen by the user during the migration setup wizard.
    /// Written into the kickstart <c>user --password</c> directive so Anaconda sets
    /// it directly — no locked account, no SDDM autologin workaround required.
    /// The kickstart file lives on a temporary FAT32 partition that is deleted after
    /// installation, so the brief plaintext exposure is acceptable.
    /// </summary>
    [JsonPropertyName("linuxPassword")] public string? LinuxPassword { get; init; }
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
    /// <summary>Display name of the Windows application that triggered this suggestion
    /// (e.g. "Spotify Music 1.2.x"). Informational only.</summary>
    [JsonPropertyName("windowsAppName")] public required string WindowsAppName { get; init; }

    /// <summary>Display name of the Linux equivalent shown in the wizard and agent logs
    /// (e.g. "Spotify").</summary>
    [JsonPropertyName("linuxAppName")] public string? LinuxAppName { get; init; }

    /// <summary>Flathub app ID to install via <c>flatpak install flathub …</c>.</summary>
    [JsonPropertyName("flatpakId")] public string? FlatpakId { get; init; }

    /// <summary>Native DNF package name. Used when no Flatpak exists or is preferred.</summary>
    [JsonPropertyName("nativePackage")] public string? NativePackage { get; init; }

    /// <summary>True when the user opted in during the migration wizard.</summary>
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

    /// <summary>
    /// How Linux is installed relative to existing data.
    /// <c>"replace"</c> = entire disk erased; <c>"dual-boot"</c> = installed alongside Windows.
    /// </summary>
    [JsonPropertyName("installMode")] public string InstallMode { get; init; } = "replace";

    /// <summary>
    /// Size (in GiB) allocated to Linux when <see cref="InstallMode"/> is <c>"dual-boot"</c>.
    /// Zero when the install mode is <c>"replace"</c>.
    /// </summary>
    [JsonPropertyName("linuxPartitionSizeGb")] public int LinuxPartitionSizeGb { get; init; }
}
