using System.Text.Json.Serialization;

namespace Igloo.Core.Models;


public sealed record DistroManifest
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }

    [JsonPropertyName("logo")] public string? Logo { get; init; }

    [JsonPropertyName("status")] public string? Status { get; init; }

    
    [JsonIgnore]
    public bool IsAvailable =>
        string.IsNullOrEmpty(Status) || string.Equals(Status, "available", StringComparison.OrdinalIgnoreCase);
    [JsonPropertyName("defaultDesktopEnvironment")] public string? DefaultDesktopEnvironment { get; init; }
    [JsonPropertyName("installerType")] public string? InstallerType { get; init; }
    [JsonPropertyName("iso")] public required DistroIsoSpec Iso { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("screenshots")] public IReadOnlyList<string> Screenshots { get; init; } = [];
    [JsonPropertyName("minimumRequirements")] public DistroRequirements? MinimumRequirements { get; init; }
    [JsonPropertyName("maintainer")] public DistroMaintainer? Maintainer { get; init; }

    [JsonIgnore] public string? SourceDirectory { get; init; }

    
    [JsonIgnore]
    public string? LogoAbsolutePath =>
        Logo is { Length: > 0 } && SourceDirectory is { Length: > 0 }
            ? Path.GetFullPath(Path.Combine(SourceDirectory, Logo))
            : null;
}

public sealed record DistroIsoSpec
{
    [JsonPropertyName("downloadUrl")] public required Uri DownloadUrl { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }

    /// <summary>
    /// Optional regex matching the ISO filename in the signed checksum file. When set, the exact
    /// download filename is resolved from that (GPG-verified) checksum at acquisition time and the
    /// URL is rebuilt against <see cref="DownloadUrl"/>'s directory - so distributions that rotate
    /// their ISO filename each point release (e.g. Debian's "current") keep working with no edit.
    /// </summary>
    [JsonPropertyName("isoFilePattern")] public string? IsoFilePattern { get; init; }
    [JsonPropertyName("gpgSignatureUrl")] public Uri? GpgSignatureUrl { get; init; }
    [JsonPropertyName("gpgKeyUrl")] public Uri? GpgKeyUrl { get; init; }

    [JsonPropertyName("gpgSignedDataUrl")] public Uri? GpgSignedDataUrl { get; init; }

    [JsonPropertyName("gpgKeyFile")] public string? GpgKeyFile { get; init; }

    [JsonPropertyName("gpgKeyFingerprint")] public string? GpgKeyFingerprint { get; init; }

    [JsonPropertyName("stage2Url")] public Uri? Stage2Url { get; init; }
}

public sealed record DistroRequirements
{
    [JsonPropertyName("minRamBytes")] public long MinRamBytes { get; init; }
    [JsonPropertyName("minDiskBytes")] public long MinDiskBytes { get; init; }
    [JsonPropertyName("requiresUefi")] public bool RequiresUefi { get; init; }
    [JsonPropertyName("requires64Bit")] public bool Requires64Bit { get; init; }
}

public sealed record DistroMaintainer
{
    [JsonPropertyName("github")] public string? Github { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
}
