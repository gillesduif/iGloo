using System.Text.Json.Serialization;

namespace Igloo.Core.Models;

/// <summary>Deserialised representation of a <c>distro.json</c> manifest file.</summary>
public sealed record DistroManifest
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }

    /// <summary>
    /// Relative path (inside the distro's directory) to a PNG logo, ≥1024px on the longest
    /// edge. Required by the schema; nullable here so a missing logo degrades to the
    /// generated placeholder cover instead of failing the catalog load.
    /// </summary>
    [JsonPropertyName("logo")] public string? Logo { get; init; }

    /// <summary>
    /// Catalog availability: "available" (default) or "coming-soon". Coming-soon entries
    /// are shown in the picker but cannot be installed (no plugin yet).
    /// </summary>
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>True when this distro can actually be installed (has plugin support).</summary>
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

    /// <summary>
    /// Absolute path of the directory this manifest was loaded from. Stamped by
    /// <c>DistroLoader</c>; never part of the JSON contract. Used to resolve relative
    /// asset paths such as <see cref="Logo"/> and <see cref="Screenshots"/>.
    /// </summary>
    [JsonIgnore] public string? SourceDirectory { get; init; }

    /// <summary>Absolute path of the logo file, or null when no valid logo is declared.</summary>
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
    [JsonPropertyName("gpgSignatureUrl")] public Uri? GpgSignatureUrl { get; init; }
    [JsonPropertyName("gpgKeyUrl")] public Uri? GpgKeyUrl { get; init; }

    /// <summary>
    /// Detached-signature model only (Debian / Ubuntu): URL of the plain checksum
    /// data file (<c>SHA256SUMS</c>) that <see cref="GpgSignatureUrl"/> signs. When
    /// null, <see cref="GpgSignatureUrl"/> is a Fedora-style clear-signed CHECKSUM.
    /// </summary>
    [JsonPropertyName("gpgSignedDataUrl")] public Uri? GpgSignedDataUrl { get; init; }

    /// <summary>
    /// Path (relative to the distro folder) of the bundled, trusted signing key —
    /// e.g. <c>keys/debian-cd.asc</c>. Preferred over <see cref="GpgKeyUrl"/>: the
    /// trust anchor ships with the app instead of being fetched from a keyserver.
    /// </summary>
    [JsonPropertyName("gpgKeyFile")] public string? GpgKeyFile { get; init; }

    /// <summary>
    /// Pinned full 160-bit fingerprint of the signing key (spaces/colons ignored).
    /// When set, the key that signs the checksum file MUST match it, defeating
    /// 64-bit key-ID forgery and a malicious keyserver. Establish it out-of-band.
    /// </summary>
    [JsonPropertyName("gpgKeyFingerprint")] public string? GpgKeyFingerprint { get; init; }

    /// <summary>
    /// URL of the Anaconda stage-2 OS tree (e.g. the Fedora mirror's <c>/os/</c> path).
    /// Required for netinstall: without it Anaconda cannot locate the installer payload.
    /// Format: <c>https://…/os/</c> (directory, trailing slash).
    /// </summary>
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
