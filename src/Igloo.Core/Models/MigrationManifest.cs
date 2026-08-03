using System.Text.Json.Serialization;

namespace Igloo.Core.Models;

public sealed record MigrationManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("generatedAtUtc")]
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("iglooVersion")]
    public string IglooVersion { get; init; } = "0.0.1-alpha";

    
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

    [JsonPropertyName("wifiNetworks")]
    public IReadOnlyList<WifiNetwork> WifiNetworks { get; init; } = Array.Empty<WifiNetwork>();

    [JsonPropertyName("hardware")]
    public required HardwareProfile Hardware { get; init; }

    /// <summary>The Windows desktop layout, reproduced on Linux by the first-boot agent.</summary>
    [JsonPropertyName("displays")]
    public IReadOnlyList<DisplayLayout> Displays { get; init; } = Array.Empty<DisplayLayout>();

    /// <summary>
    /// The Windows desktop wallpaper, reproduced on Linux. Null when the user had no
    /// migratable image (solid colour, slideshow, or the file was unreadable) - the
    /// agent then leaves the distro default alone.
    /// </summary>
    [JsonPropertyName("wallpaper")]
    public WallpaperMigration? Wallpaper { get; init; }
}

/// <summary>
/// The wallpaper image carried to Linux. The file itself sits next to this manifest
/// on the installer seed (staging root); <see cref="FileName"/> is the name the
/// first-boot agent looks for.
/// </summary>
public sealed record WallpaperMigration
{
    /// <summary>Staging-relative file name, e.g. <c>igloo-wallpaper.jpg</c>.</summary>
    [JsonPropertyName("fileName")] public required string FileName { get; init; }

    /// <summary>Where the image lived on Windows - diagnostics only.</summary>
    [JsonPropertyName("originalPath")] public string? OriginalPath { get; init; }
}

/// <summary>One monitor's geometry, as Windows was driving it.</summary>
/// <remarks>
/// <c>pnpId</c> is the cross-OS identity: Windows and Linux name and order displays
/// differently and neither order is stable, but the monitor's EDID reads the same on
/// both. The agent derives the same id from /sys/class/drm/*/edid to know which physical
/// screen each entry describes - without it, a two-monitor setup would eventually rotate
/// the wrong one.
/// </remarks>
public sealed record DisplayLayout
{
    [JsonPropertyName("pnpId")] public string? PnpId { get; init; }
    [JsonPropertyName("widthPx")] public int WidthPx { get; init; }
    [JsonPropertyName("heightPx")] public int HeightPx { get; init; }
    [JsonPropertyName("refreshHz")] public int RefreshHz { get; init; }

    /// <summary>Clockwise rotation in degrees: 0, 90, 180 or 270.</summary>
    [JsonPropertyName("rotationDegrees")] public int RotationDegrees { get; init; }

    [JsonPropertyName("positionX")] public int PositionX { get; init; }
    [JsonPropertyName("positionY")] public int PositionY { get; init; }

    /// <summary>
    /// Windows display scaling in percent (100 = none, 150 = 150%). 0/omitted = unknown,
    /// treat as 100. The first-boot agent needs it twice: to convert Windows' PHYSICAL
    /// pixel positions into KWin's LOGICAL ones, and to set the output's scale factor so
    /// the desktop actually looks the way it did on Windows.
    /// </summary>
    [JsonPropertyName("scalePercent")] public int ScalePercent { get; init; }

    [JsonPropertyName("isPrimary")] public bool IsPrimary { get; init; }
}

public sealed record MigrationUser
{
    [JsonPropertyName("windowsUsername")] public required string WindowsUsername { get; init; }
    [JsonPropertyName("preferredLinuxUsername")] public required string PreferredLinuxUsername { get; init; }
    [JsonPropertyName("fullName")] public string? FullName { get; init; }
    [JsonPropertyName("locale")] public string Locale { get; init; } = "en_US.UTF-8";
    [JsonPropertyName("timezone")] public string Timezone { get; init; } = "UTC";
    [JsonPropertyName("keymap")] public string Keymap { get; init; } = "us";

    [JsonPropertyName("linuxPassword")] public string? LinuxPassword { get; init; }
}

public sealed record FileMigrationPlan
{
    [JsonPropertyName("stagingPath")] public required string StagingPath { get; init; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; init; }

    [JsonPropertyName("includedFolders")] public IReadOnlyList<string> IncludedFolders { get; init; } = Array.Empty<string>();

    [JsonPropertyName("folders")] public IReadOnlyList<MigrationFolder> Folders { get; init; } = Array.Empty<MigrationFolder>();
}


public sealed record MigrationFolder
{
    
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("sourceRelativePath")] public required string SourceRelativePath { get; init; }
}

public sealed record BrowserMigration
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("engine")] public string Engine { get; init; } = "unknown";

    [JsonPropertyName("sourceRelativePath")] public string SourceRelativePath { get; init; } = "";

    [JsonPropertyName("destRelativePath")] public string DestRelativePath { get; init; } = "";

    [JsonPropertyName("profileStagingPath")] public string ProfileStagingPath { get; init; } = "";

    [JsonPropertyName("includesPasswords")] public bool IncludesPasswords { get; init; }

    // Base64 envelope of the browser's decrypted logins, re-encrypted under a
    // key derived from the user's Linux password (ADR-011). Null when nothing
    // migratable was found; the first-boot agents null it out during redaction.
    [JsonPropertyName("credentialsBlob")] public string? CredentialsBlob { get; init; }
}

public sealed record SuggestedPackage
{
    [JsonPropertyName("windowsAppName")] public required string WindowsAppName { get; init; }

    [JsonPropertyName("linuxAppName")] public string? LinuxAppName { get; init; }

    
    [JsonPropertyName("flatpakId")] public string? FlatpakId { get; init; }

    
    [JsonPropertyName("nativePackage")] public string? NativePackage { get; init; }

    
    [JsonPropertyName("autoInstall")] public bool AutoInstall { get; init; }
}

public sealed record WifiNetwork
{
    
    [JsonPropertyName("ssid")] public required string Ssid { get; init; }

    [JsonPropertyName("security")] public string Security { get; init; } = "wpa-psk";

    [JsonPropertyName("psk")] public string? Psk { get; init; }

    [JsonPropertyName("isPrimary")] public bool IsPrimary { get; init; }

    
    [JsonPropertyName("hidden")] public bool Hidden { get; init; }
}

public sealed record HardwareProfile
{
    [JsonPropertyName("gpuVendor")] public string GpuVendor { get; init; } = "unknown";
    [JsonPropertyName("needsNonFreeCodecs")] public bool NeedsNonFreeCodecs { get; init; } = true;
    [JsonPropertyName("secureBootEnabled")] public bool SecureBootEnabled { get; init; }
    [JsonPropertyName("firmwareType")] public string FirmwareType { get; init; } = "uefi";

    [JsonPropertyName("targetDiskModel")] public string? TargetDiskModel { get; init; }

    [JsonPropertyName("targetDiskBytes")] public long TargetDiskBytes { get; init; }

    [JsonPropertyName("installMode")] public string InstallMode { get; init; } = "replace";

    [JsonPropertyName("linuxPartitionSizeGb")] public int LinuxPartitionSizeGb { get; init; }
}
