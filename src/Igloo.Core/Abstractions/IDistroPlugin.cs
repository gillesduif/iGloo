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

    /// <summary>
    /// Describe how the no-USB direct installer must boot this distro: the kernel
    /// command line, where the kernel/initrd live on the ISO, and any extra ISO
    /// files to stage onto OEMDRV. This lets <c>DirectInstallService</c> drive any
    /// installer (Anaconda kickstart, debian-installer preseed, subiquity
    /// autoinstall) without distro-specific branching in shared code. The
    /// installer-config file rendered by <see cref="RenderInstallerConfigAsync"/>
    /// is always copied to the OEMDRV root, so the cmdline can reference it there.
    /// </summary>
    InstallerBootSpec GetInstallerBootSpec();
}

/// <summary>
/// Boot recipe for the no-USB direct installer. The token <c>{LABEL}</c> in
/// <see cref="KernelCmdline"/> is replaced with the OEMDRV FAT32 volume label at
/// render time (it is created dynamically per run).
/// </summary>
public sealed record InstallerBootSpec
{
    /// <summary>GRUB menu-entry title shown if the boot menu is displayed.</summary>
    public required string MenuTitle { get; init; }

    /// <summary>
    /// FAT32 label for the boot/config volume Igloo creates. Default <c>OEMDRV</c>
    /// (Anaconda auto-scans it). Ubuntu's subiquity wants <c>CIDATA</c> so cloud-init's
    /// NoCloud datasource auto-detects the <c>user-data</c>/<c>meta-data</c> seed.
    /// GRUB locates the kernel/initrd by this label too.
    /// </summary>
    public string VolumeLabel { get; init; } = "OEMDRV";

    /// <summary>
    /// Kernel command line appended after the kernel path. Use <c>{LABEL}</c> for the
    /// OEMDRV volume label. Examples:
    ///   Anaconda:  <c>inst.stage2=hd:LABEL={LABEL}: inst.ks=hd:LABEL={LABEL}:/ks.cfg</c>
    ///   d-i:       <c>auto=true priority=critical preseed/file=/run/oemdrv/preseed.cfg</c>
    ///   subiquity: <c>autoinstall "ds=nocloud;s=/cdrom/"</c>
    /// </summary>
    public required string KernelCmdline { get; init; }

    /// <summary>
    /// ISO-relative candidate paths for the kernel, in priority order. The first that
    /// exists is used. Empty falls back to a recursive scan for <c>vmlinuz</c>/<c>linux</c>.
    /// </summary>
    public IReadOnlyList<string> KernelIsoPaths { get; init; } = Array.Empty<string>();

    /// <summary>ISO-relative candidate paths for the initrd, in priority order.</summary>
    public IReadOnlyList<string> InitrdIsoPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Extra files to copy from the mounted ISO onto OEMDRV before booting
    /// (e.g. Anaconda's <c>images/install.img</c> stage-2 squashfs).
    /// </summary>
    public IReadOnlyList<IsoFileStage> ExtraIsoFiles { get; init; } = Array.Empty<IsoFileStage>();

    /// <summary>
    /// How the rendered installer config (kickstart / preseed / autoinstall) is
    /// delivered to the installer at boot. The config file is always also copied
    /// to the OEMDRV root under its <see cref="InstallerConfig.FileName"/>.
    /// </summary>
    public ConfigDelivery ConfigDelivery { get; init; } = ConfigDelivery.OemDrvLabel;

    /// <summary>
    /// When <see cref="ConfigDelivery"/> is <see cref="ConfigDelivery.InjectIntoInitrd"/>,
    /// the path the config is injected at inside the initramfs root (e.g.
    /// <c>preseed.cfg</c>, referenced by the cmdline as <c>preseed/file=/preseed.cfg</c>).
    /// </summary>
    public string? InitrdConfigPath { get; init; }

    /// <summary>
    /// When true, the entire source ISO is copied onto the OEMDRV volume as
    /// <see cref="IsoVolumeFileName"/>. Required for installers whose payload IS the
    /// ISO (debian-installer <c>iso-scan</c>, Ubuntu/Mint <c>casper</c>), as opposed
    /// to a separable stage-2 like Anaconda's <c>install.img</c>. The cmdline points
    /// the installer at it (e.g. <c>iso-scan/filename=/debian.iso</c>). The partition
    /// is sized to fit the ISO automatically.
    /// </summary>
    public bool CopyFullIsoToVolume { get; init; }

    /// <summary>File name to give the copied ISO on the OEMDRV root (e.g. <c>debian.iso</c>).</summary>
    public string? IsoVolumeFileName { get; init; }

    /// <summary>
    /// When set, the kernel is DOWNLOADED from this URL instead of extracted from the
    /// ISO. Debian needs this: the netinst ISO's initrd runs <c>cdrom-detect</c> (CDs
    /// only), but installing from an <c>.iso</c> file on a partition requires the
    /// <c>hd-media</c> kernel+initrd (which run <c>iso-scan</c>). Pairs with
    /// <see cref="InitrdUrl"/> and <see cref="CopyFullIsoToVolume"/>.
    /// </summary>
    public Uri? KernelUrl { get; init; }

    /// <summary>Download URL for the initrd (see <see cref="KernelUrl"/>). The config is still injected into it.</summary>
    public Uri? InitrdUrl { get; init; }

    /// <summary>
    /// When true, Igloo pre-creates the Linux root partition from Windows
    /// (diskpart, GPT type "Linux filesystem") so the installer only ever REUSES
    /// existing partitions. Required for subiquity/curtin: asking curtin to ADD a
    /// partition makes it rewrite the entire GPT (new disklabel GUID, renumbered
    /// entries) and then reload the kernel's partition table, which is impossible while
    /// the live-media partitions on this same disk are in use. With nothing to
    /// add, curtin writes no table at all and only formats the pre-made root.
    /// Installers with native free-space logic (Anaconda, partman biggest_free)
    /// MUST keep this false: they claim the gap themselves, and a pre-made
    /// partition would defeat their free-space selection.
    /// </summary>
    public bool PreCreateRootPartition { get; init; }
}

/// <summary>How the unattended installer config reaches the installer at boot.</summary>
public enum ConfigDelivery
{
    /// <summary>
    /// The installer auto-scans for the config on the FAT32 volume labelled
    /// <c>OEMDRV</c> (Anaconda's behaviour; the cmdline can reference
    /// <c>hd:LABEL=OEMDRV:/…</c>). No initrd modification needed.
    /// </summary>
    OemDrvLabel,

    /// <summary>
    /// The config is appended into the initrd as a concatenated gzipped cpio
    /// member at <see cref="InstallerBootSpec.InitrdConfigPath"/>. This is the
    /// standard fully-unattended delivery for debian-installer / Ubiquity preseed.
    /// </summary>
    InjectIntoInitrd,
}

/// <summary>One file to stage from the mounted ISO onto the OEMDRV partition.</summary>
public sealed record IsoFileStage(string IsoRelativePath, string OemDrvRelativePath, bool Required);

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
    Anaconda,        // Fedora family. Driver: kickstart.
    DebianInstaller, // Debian. Driver: preseed (debian-installer / partman).
    Ubiquity,        // Older Ubuntu / Linux Mint. Driver: preseed (automatic-ubiquity).
    Calamares,       // openSUSE, EndeavourOS, etc. Driver: Calamares JSON config.
    AutoYaST,        // openSUSE Leap (alternative). Driver: AutoYaST XML.
    Subiquity,       // Ubuntu Server / newer Ubuntu desktop. Driver: cloud-init autoinstall.
    Custom           // Distro provides its own installer; plugin handles it end-to-end.
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
