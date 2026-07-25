using Igloo.Core.Models;

namespace Igloo.Core.Abstractions;

public interface IDistroPlugin
{
    
    string Id { get; }

    
    DistroMetadata Metadata { get; }

    /// <example>
    /// A Fedora plugin might return a warning if the GPU is NVIDIA (extra driver step needed),
    /// a Mint plugin might not. A distro that doesn't support Secure Boot would return a blocker
    /// if Secure Boot is enabled and the user hasn't agreed to disable it.
    /// </example>
    IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report);

    Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default);

    Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default);

    InstallerBootSpec GetInstallerBootSpec();
}

public sealed record InstallerBootSpec
{
    
    public required string MenuTitle { get; init; }

    public string VolumeLabel { get; init; } = "OEMDRV";

    public required string KernelCmdline { get; init; }

    public IReadOnlyList<string> KernelIsoPaths { get; init; } = Array.Empty<string>();

    
    public IReadOnlyList<string> InitrdIsoPaths { get; init; } = Array.Empty<string>();

    public IReadOnlyList<IsoFileStage> ExtraIsoFiles { get; init; } = Array.Empty<IsoFileStage>();

    public ConfigDelivery ConfigDelivery { get; init; } = ConfigDelivery.OemDrvLabel;

    public string? InitrdConfigPath { get; init; }

    public bool CopyFullIsoToVolume { get; init; }

    
    public string? IsoVolumeFileName { get; init; }

    public Uri? KernelUrl { get; init; }

    
    public Uri? InitrdUrl { get; init; }

    public bool PreCreateRootPartition { get; init; }
}


public enum ConfigDelivery
{
    OemDrvLabel,

    InjectIntoInitrd,
}


public sealed record IsoFileStage(string IsoRelativePath, string OemDrvRelativePath, bool Required);


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

    
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Screenshots { get; init; } = Array.Empty<string>();

    public HardwareRequirements MinimumRequirements { get; init; } = new();

    
    public string? Maintainer { get; init; }
}

public sealed record HardwareRequirements
{
    public long MinRamBytes { get; init; } = 2L * 1024 * 1024 * 1024;     // 2 GB
    public long MinDiskBytes { get; init; } = 20L * 1024 * 1024 * 1024;   // 20 GB
    public bool RequiresUefi { get; init; }
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


public sealed record InstallerConfig(
    string FileName,
    ReadOnlyMemory<byte> Contents,
    IReadOnlyList<InstallerConfigExtra> Extras);

public sealed record InstallerConfigExtra(string RelativePath, ReadOnlyMemory<byte> Contents);


public sealed record AgentPayload(IReadOnlyList<AgentFile> Files);

public sealed record AgentFile(string RelativePath, ReadOnlyMemory<byte> Contents, bool Executable);
