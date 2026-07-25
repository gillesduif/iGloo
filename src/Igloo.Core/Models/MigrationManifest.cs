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
