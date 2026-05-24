using Igloo.Core.Models;

namespace Igloo.Core.Abstractions;

/// <summary>
/// The contract every Linux distribution implements to be installable by Igloo.
///
/// One plugin per distro. Plugins live in the top-level <c>distros/</c> directory of the repository
/// and are discovered at startup. A distro contribution is a self-contained folder:
///   distros/&lt;distro-id&gt;/
///     distro.json            – metadata (name, ISO URL, checksum, hardware tags)
///     Igloo.Distro.X.csproj  – plugin assembly implementing IDistroPlugin
///     installer/...          – installer-driver config templates (kickstart, preseed, Calamares...)
///     agent/...              – first-boot agent for this distro
///
/// The plugin model means adding a distro never requires changes to Igloo.Core, Igloo.App, or any
/// other shared code. The boundary is this interface. If your distro can be installed by
/// implementing these methods, it works with Igloo.
///
/// Stability: this interface is the public API of the plugin system. Breaking changes here force
/// every distro plugin to update. Bump the major version of Igloo when changing it.
/// </summary>
public interface IDistroPlugin
{
    /// <summary>Stable, lowercased, hyphenated identifier. Must match the folder name under <c>distros/</c>.</summary>
    string Id { get; }

    /// <summary>Static metadata loaded from <c>distro.json</c>: display name, description, screenshots, tags.</summary>
    DistroMetadata Metadata { get; }

    /// <summary>
    /// Decide whether this distro is installable on the user's current machine, given the pre-flight
    /// report. Returns a list of distro-specific compatibility findings; an empty list means
    /// "yes, this distro will work here". Blockers should be returned with <see cref="FindingSeverity.Blocker"/>.
    /// </summary>
    /// <example>
    /// A Fedora plugin might return a warning if the GPU is NVIDIA (extra driver step needed),
    /// a Mint plugin might not. A distro that doesn't support Secure Boot would return a blocker
    /// if Secure Boot is enabled and the user hasn't agreed to disable it.
    /// </example>
    IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report);

    /// <summary>
    /// Render the installer-driver configuration that will be placed on the OEMDRV volume.
    /// Different distros use different installers:
    ///   – kickstart for Anaconda (Fedora, RHEL, CentOS, AlmaLinux, Rocky)
    ///   – preseed for Ubiquity (Ubuntu, Mint, Pop!_OS, Zorin, elementary)
    ///   – Calamares config for openSUSE, EndeavourOS, KaOS, Manjaro
    ///   – AutoYaST for openSUSE
    /// The plugin owns this detail entirely. Igloo just takes the rendered bytes and writes them
    /// where the installer expects to find them.
    /// </summary>
    Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default);

    /// <summary>
    /// Provide the first-boot agent payload for this distro. This is the bash/python/whatever code
    /// that runs once on the freshly-installed system to apply the migration manifest.
    /// Returned as a set of files with relative target paths.
    /// </summary>
    Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default);
}

/// <summary>Static metadata about a distro, loaded from its <c>distro.json</c> file.</summary>
public sealed record DistroMetadata
{
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string DefaultDesktopEnvironment { get; init; }
    public required InstallerType InstallerType { get; init; }
    public required Uri IsoDownloadUrl { get; init; }
    public required string IsoSha256 { get; init; }
    public Uri? IsoGpgSignatureUrl { get; init; }
    public Uri? IsoGpgKeyUrl { get; init; }

    /// <summary>"best for new users", "gaming", "development", "older hardware", "privacy", etc.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Screenshots { get; init; } = Array.Empty<string>();

    public HardwareRequirements MinimumRequirements { get; init; } = new();

    /// <summary>Free-text maintainer info shown to users for accountability.</summary>
    public string? Maintainer { get; init; }
}

public sealed record HardwareRequirements
{
    public long MinRamBytes { get; init; } = 2L * 1024 * 1024 * 1024;     // 2 GB
    public long MinDiskBytes { get; init; } = 20L * 1024 * 1024 * 1024;   // 20 GB
    public bool RequiresUefi { get; init; } = false;
    public bool Requires64Bit { get; init; } = true;
}

public enum InstallerType
{
    Anaconda,    // Fedora family. Driver: kickstart.
    Ubiquity,    // Ubuntu family. Driver: preseed.
    Calamares,   // openSUSE, EndeavourOS, etc. Driver: Calamares JSON config.
    AutoYaST,    // openSUSE Leap (alternative). Driver: AutoYaST XML.
    Subiquity,   // Ubuntu Server / newer Ubuntu desktop. Driver: cloud-init autoinstall.
    Custom       // Distro provides its own installer; plugin handles it end-to-end.
}

/// <summary>Rendered installer-driver configuration, ready to write to OEMDRV.</summary>
public sealed record InstallerConfig(
    string FileName,
    byte[] Contents,
    IReadOnlyList<InstallerConfigExtra> Extras);

public sealed record InstallerConfigExtra(string RelativePath, byte[] Contents);

/// <summary>Files comprising the first-boot agent for a distro.</summary>
public sealed record AgentPayload(IReadOnlyList<AgentFile> Files);

public sealed record AgentFile(string RelativePath, byte[] Contents, bool Executable);
