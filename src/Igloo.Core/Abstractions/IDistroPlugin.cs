using Igloo.Core.Models;
using Igloo.Core.Preparation;

namespace Igloo.Core.Abstractions;

public interface IDistroPlugin
{
    
    string Id { get; }

    
    DistroMetadata Metadata { get; }

    /// <summary>Distro-specific preflight findings, on top of the generic checks.</summary>
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

// Opt-in provider contract for a storage recipe bound to independently observed owned
// partitions. It is not an execution capability; the legacy RenderInstallerConfigAsync
// remains private/transitional until Windows and recovery can honor this same plan.
public interface IReviewedInstallerStorageProvider
{
    Observation<string> RenderReviewedStorage(InstallationOwnershipV1 ownership,
        Guid expectedGeneration, Observation<InstallerRuntimeInventoryV1> inventory);

    Task<Observation<InstallerConfig>> RenderReviewedInstallerConfigAsync(
        MigrationManifest manifest, InstallationOwnershipV1 ownership,
        Guid expectedGeneration, Observation<InstallerRuntimeInventoryV1> inventory,
        CancellationToken ct = default);

    // A Windows-side declaration for a receipted owned root. The generated
    // installer must acquire and verify its own fresh device inventory before
    // emitting any storage commands; rendering does not authorize execution.
    Task<Observation<InstallerConfig>> RenderDeferredReviewedInstallerConfigAsync(
        MigrationManifest manifest, InstallationOwnershipV1 ownership,
        Guid expectedGeneration, CancellationToken ct = default);
}

// A read-only result from the reviewed Windows checkpoint and complete GPT
// observation after Windows-created partitions have been receipted. The record
// alone is not execution authority; the consumer must reobserve its checkpoint.
public sealed record ReviewedInstallerFreeExtentV1(Guid GenerationId, Guid TargetDiskGuid,
    Guid LinuxEspPartitionGuid, ulong OffsetBytes, ulong SizeBytes, ulong RootMinimumBytes);

public interface IReviewedInstallerConfigProvider
{
    Task<InstallerConfig> RenderReviewedInstallerConfigAsync(MigrationManifest manifest,
        ReviewedInstallerFreeExtentV1 installerRoot, CancellationToken ct = default);
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

    // Provider-rendered InstallerConfig.Extras to append to an injected initrd.
    // Each staging-relative source is placed at the initrd root by basename.
    public IReadOnlyList<string> InitrdExtraPaths { get; init; } = Array.Empty<string>();

    public bool CopyFullIsoToVolume { get; init; }

    // Bound by Core from the selected provider metadata for the final installer
    // invocation. A caller-supplied path alone never identifies trusted media.
    public string? IsoSha256 { get; init; }

    
    public string? IsoVolumeFileName { get; init; }

    public Uri? KernelUrl { get; init; }

    public string? KernelSha256 { get; init; }

    
    public Uri? InitrdUrl { get; init; }

    public string? InitrdSha256 { get; init; }

    public bool PreCreateRootPartition { get; init; }

    /// <summary>
    /// Whether the selected user folders and staged browser profiles (the
    /// <c>files/</c> and <c>browser-profiles/</c> staging namespaces) are copied onto
    /// the installer seed partition. The v0.2-alpha flow - and the Debian-family
    /// first-boot agent - read them from the Windows NTFS volume instead, so the
    /// seed holds only config, agent payload, media and the ISO. Providers whose
    /// installer-time scripts copy user data from the seed (Fedora kickstart) keep
    /// the default.
    /// </summary>
    public bool MigrationDataOnSeed { get; init; } = true;
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
