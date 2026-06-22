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

    /// <summary>
    /// Saved Wi-Fi networks exported from Windows. Pre-seeded into the kickstart
    /// (so the netinstall can connect automatically) and written as NetworkManager
    /// connection profiles by the first-boot agent. PSKs are redacted from the
    /// on-disk manifest once the agent has applied them (same as <see cref="MigrationUser.LinuxPassword"/>).
    /// </summary>
    [JsonPropertyName("wifiNetworks")]
    public IReadOnlyList<WifiNetwork> WifiNetworks { get; init; } = Array.Empty<WifiNetwork>();

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
    /// it directly - no locked account, no SDDM autologin workaround required.
    /// The kickstart file lives on a temporary FAT32 partition that is deleted after
    /// installation, so the brief plaintext exposure is acceptable.
    /// </summary>
    [JsonPropertyName("linuxPassword")] public string? LinuxPassword { get; init; }
}

public sealed record FileMigrationPlan
{
    [JsonPropertyName("stagingPath")] public required string StagingPath { get; init; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; init; }

    /// <summary>
    /// Destination folder names only (e.g. "Documents", "Downloads"). Kept for
    /// display and backward compatibility; the copy logic uses <see cref="Folders"/>.
    /// </summary>
    [JsonPropertyName("includedFolders")] public IReadOnlyList<string> IncludedFolders { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Folders to migrate, each pairing the Linux destination name with the
    /// source path relative to the Windows user profile. This resolves OneDrive
    /// Known Folder Move: Documents/Pictures/Desktop are often physically at
    /// <c>OneDrive/Documents</c> etc., not directly under the profile. The
    /// kickstart <c>%post</c> joins <see cref="MigrationFolder.SourceRelativePath"/>
    /// onto the mounted Windows home instead of guessing the location by name.
    /// </summary>
    [JsonPropertyName("folders")] public IReadOnlyList<MigrationFolder> Folders { get; init; } = Array.Empty<MigrationFolder>();
}

/// <summary>One folder to migrate: a Linux destination name plus the real source location.</summary>
public sealed record MigrationFolder
{
    /// <summary>Destination folder name in the Linux home directory (e.g. "Documents").</summary>
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>
    /// Source path relative to the Windows user profile, using forward slashes.
    /// Resolved on the Windows side via the Known Folder API, so OneDrive-redirected
    /// folders appear as e.g. <c>"OneDrive/Documents"</c> while non-redirected ones
    /// are just <c>"Downloads"</c>.
    /// </summary>
    [JsonPropertyName("sourceRelativePath")] public required string SourceRelativePath { get; init; }
}

public sealed record BrowserMigration
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>
    /// Rendering engine: <c>"gecko"</c> (Firefox/Zen/Waterfox - profile folder is
    /// OS-portable, saved passwords included via NSS) or <c>"chromium"</c>
    /// (Chrome/Edge/Brave/… - passwords are DPAPI-bound to the Windows account and
    /// not portable; Phase 1 records but does not migrate these).
    /// </summary>
    [JsonPropertyName("engine")] public string Engine { get; init; } = "unknown";

    /// <summary>
    /// Source profile-root path relative to the Windows user profile, forward
    /// slashes (e.g. <c>"AppData/Roaming/Mozilla/Firefox"</c>). Empty when the
    /// browser is not migrated in this phase. Resolved on the Windows side so
    /// AppData redirection is honoured.
    /// </summary>
    [JsonPropertyName("sourceRelativePath")] public string SourceRelativePath { get; init; } = "";

    /// <summary>
    /// Destination path relative to the Linux <c>$HOME</c>, forward slashes
    /// (e.g. <c>".mozilla/firefox"</c>, <c>".zen"</c>). Empty when not migrated.
    /// </summary>
    [JsonPropertyName("destRelativePath")] public string DestRelativePath { get; init; } = "";

    /// <summary>Legacy USB-staging path. Unused by direct install (copy is from NTFS in %post).</summary>
    [JsonPropertyName("profileStagingPath")] public string ProfileStagingPath { get; init; } = "";

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

public sealed record WifiNetwork
{
    /// <summary>The network name (SSID).</summary>
    [JsonPropertyName("ssid")] public required string Ssid { get; init; }

    /// <summary>
    /// Security type, normalised for the agent:
    /// <c>"wpa-psk"</c> (WPA/WPA2/WPA3 personal - uses <see cref="Psk"/>),
    /// <c>"open"</c>    (no password), or
    /// <c>"unsupported"</c> (enterprise/802.1X - recorded for reference but not auto-applied).
    /// </summary>
    [JsonPropertyName("security")] public string Security { get; init; } = "wpa-psk";

    /// <summary>
    /// Pre-shared key in plaintext for <c>wpa-psk</c> networks; null for open or
    /// unsupported networks. Redacted by the first-boot agent after the
    /// NetworkManager profile has been written.
    /// </summary>
    [JsonPropertyName("psk")] public string? Psk { get; init; }

    /// <summary>
    /// True for the network Windows is currently connected to. The kickstart
    /// pre-seeds this one into its <c>network --essid --wpakey</c> directive so
    /// the netinstall has connectivity without manual entry in Anaconda.
    /// </summary>
    [JsonPropertyName("isPrimary")] public bool IsPrimary { get; init; }

    /// <summary>True when the SSID is non-broadcast (hidden).</summary>
    [JsonPropertyName("hidden")] public bool Hidden { get; init; }
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
