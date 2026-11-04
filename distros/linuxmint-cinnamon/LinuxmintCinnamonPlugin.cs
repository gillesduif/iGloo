using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;

namespace Igloo.Distro.LinuxmintCinnamon;

public sealed class LinuxmintCinnamonPlugin : IDistroPlugin, IReviewedInstallerConfigProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string Id => "linuxmint-cinnamon";
    public DistroMetadata Metadata { get; }

    public LinuxmintCinnamonPlugin()
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var path = Path.Join(asmDir, "linuxmint-cinnamon", "distro.json");
        if (!File.Exists(path))
            path = Path.Join(asmDir, "distro.json");
        var raw = TryLoadManifest(path);
        Metadata = raw is not null ? BuildMetadata(raw) : FallbackMetadata();
    }

    private static DistroManifest? TryLoadManifest(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<DistroManifest>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var findings = new List<PreflightFinding>();
        if (string.Equals(report.GpuVendor, "nvidia", StringComparison.OrdinalIgnoreCase))
            findings.Add(new PreflightFinding(FindingSeverity.Info, "MINT_NVIDIA",
                "Your machine has an NVIDIA GPU. Igloo's first-boot agent installs the driver via " +
                "ubuntu-drivers on first boot. An internet connection is required at first boot.", null));
        if (report.BitLocker == BitLockerState.EncryptedAndLocked)
            findings.Add(new PreflightFinding(FindingSeverity.Blocker, "BITLOCKER_LOCKED",
                "BitLocker is enabled and the volume is locked. Igloo cannot resize a locked volume.",
                "Unlock and fully decrypt the drive in Windows before re-running Igloo."));
        if (report.TotalRamBytes < Metadata.MinimumRequirements.MinRamBytes)
            findings.Add(new PreflightFinding(FindingSeverity.Warning, "RAM_BELOW_RECOMMENDED",
                $"This machine has {report.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GiB of RAM. " +
                $"Mint recommends at least {Metadata.MinimumRequirements.MinRamBytes / (1024.0 * 1024 * 1024):F0} GiB.",
                $"Add memory or use a machine with at least {Metadata.MinimumRequirements.MinRamBytes / (1024.0 * 1024 * 1024):F0} GiB of RAM, then rerun preflight."));
        return findings;
    }

    public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default) =>
        RenderInstallerConfigCoreAsync(manifest, null, ct);

    public Task<InstallerConfig> RenderReviewedInstallerConfigAsync(MigrationManifest manifest,
        ReviewedInstallerFreeExtentV1 installerRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installerRoot);
        var selectedBytes = manifest.Hardware.TargetDiskBytes;
        if (installerRoot.GenerationId == Guid.Empty || installerRoot.TargetDiskGuid == Guid.Empty ||
            installerRoot.LinuxEspPartitionGuid == Guid.Empty ||
            installerRoot.LinuxEspPartitionGuid == installerRoot.TargetDiskGuid ||
            manifest.Hardware.TargetDiskGuid != installerRoot.TargetDiskGuid || selectedBytes <= 0 ||
            installerRoot.OffsetBytes == 0 || installerRoot.OffsetBytes % (1024UL * 1024) != 0 ||
            installerRoot.SizeBytes == 0 || installerRoot.SizeBytes % 512 != 0 ||
            installerRoot.RootMinimumBytes < (ulong)Metadata.MinimumRequirements.MinDiskBytes ||
            installerRoot.RootMinimumBytes > installerRoot.SizeBytes ||
            installerRoot.OffsetBytes >= (ulong)selectedBytes ||
            installerRoot.SizeBytes > (ulong)selectedBytes - installerRoot.OffsetBytes)
            throw new InvalidDataException("Mint's reviewed installer extent differs from the selected disk or root requirement.");
        return RenderInstallerConfigCoreAsync(manifest, installerRoot, ct);
    }

    private async Task<InstallerConfig> RenderInstallerConfigCoreAsync(MigrationManifest manifest,
        ReviewedInstallerFreeExtentV1? installerRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.DistroId, Id, StringComparison.Ordinal))
            throw new InvalidDataException("Mint installer manifest has the wrong distribution ID.");
        if (manifest.Hardware.TargetDiskGuid is not { } targetGuid || targetGuid == Guid.Empty ||
            manifest.Hardware.TargetDiskBytes <= 0)
            throw new InvalidDataException("Mint installer requires the selected GPT disk identity and exact byte size.");

        // v0.2-alpha rendering: the preseed template plus the manifest values. The
        // install-time partman hooks (SHA-pinned igloo-partman.sh, bound free extent,
        // dedicated-ESP fstab selector) that the beta added are gone: they required a
        // reviewed preparation that never shipped and refused every legacy install.
        // Partitioning is stock Ubiquity biggest_free into the free space iGloo left;
        // the success_command writes the two-phase first-boot bootstrap (ADR-012).
        _ = installerRoot;
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var preseedDir = ResolvePreseedDir(asmDir);
        var templatePath = Path.Join(preseedDir, "preseed.cfg.template");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("preseed.cfg.template missing from the Mint plugin output.");
        var preseedTemplate = await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false);
        var preseed = RenderFromTemplate(preseedTemplate, manifest)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        if (preseed.Contains("{{", StringComparison.Ordinal))
            throw new InvalidDataException("Mint preseed contains an unresolved template value.");
        return new InstallerConfig("preseed.cfg", Encoding.UTF8.GetBytes(preseed), Array.Empty<InstallerConfigExtra>());
    }

    public async Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var namespacedAgentDir = Path.Join(asmDir, "linuxmint-cinnamon", "agent");
        var agentDir = Directory.Exists(namespacedAgentDir) ? namespacedAgentDir
            : Directory.Exists(Path.Join(asmDir, "agent")) ? Path.Join(asmDir, "agent")
            : Path.Join(asmDir, "..", "_debian-family", "agent");

        // igloo_boot.py is shared across families, so it sits in _shared/agent/
        // rather than next to the family agent.
        var sharedDir = File.Exists(Path.Join(agentDir, "igloo_boot.py"))
            ? agentDir : Path.Join(asmDir, "..", "_shared", "agent");

        var files = new List<AgentFile>();
        foreach (var (dir, name, exe) in new[]
                 {
                     (agentDir, "first-boot.sh", true),
                     (agentDir, "agent.py", true),
                     (agentDir, "display-apply.py", true),
                     (agentDir, "display-apply-gnome.py", true),
                     (sharedDir, "igloo_boot.py", false),
                     (agentDir, "igloo-first-boot.service", false),
                 })
        {
            var p = Path.Join(dir, name);
            files.Add(new AgentFile(name, NormalizeCrLf(await ReadRequiredPayloadFileAsync(p, ct).ConfigureAwait(false)), exe));
        }
        var verifier = await ReadSeedVerifierAsync(asmDir, ct).ConfigureAwait(false);
        var bootstrapService = NormalizeCrLf(await ReadRequiredPayloadFileAsync(
            Path.Join(ResolvePreseedDir(asmDir), "igloo-bootstrap.service"), ct).ConfigureAwait(false));
        files.Add(new AgentFile("igloo-verify-seed.py", verifier, false));
        files.Add(new AgentFile("igloo-bootstrap.sh", await RenderBootstrapAsync(
            asmDir, verifier, bootstrapService, ct).ConfigureAwait(false), true));
        files.Add(new AgentFile("igloo-bootstrap.service", bootstrapService, false));
        // GRUB theme archives (M17 boot menu). Binary: must bypass NormalizeCrLf,
        // which rewrites 0x0D bytes and would corrupt the gzip stream.
        // The archives moved to _shared/grub-theme/; the build output keeps them
        // next to the agent, so that stays the first candidate.
        var themeDir = Path.Join(asmDir, "..", "_shared", "grub-theme");
        foreach (var name in new[] { "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz" })
        {
            var p = Path.Join(agentDir, name);
            if (!File.Exists(p))
                p = Path.Join(themeDir, name);
            files.Add(new AgentFile(name, await ReadRequiredPayloadFileAsync(p, ct).ConfigureAwait(false), false));
        }
        return new AgentPayload(files);
    }

    private static async Task<byte[]> ReadRequiredPayloadFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required Mint first-boot payload is missing.", path);

        var contents = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (contents.Length == 0)
            throw new InvalidDataException($"Required Mint first-boot payload is empty: {path}");
        return contents;
    }

    private static async Task<byte[]> ReadSeedVerifierAsync(string asmDir, CancellationToken ct)
    {
        var path = Path.Join(asmDir, "linuxmint-cinnamon", "agent", "igloo-verify-seed.py");
        if (!File.Exists(path))
            path = Path.Join(asmDir, "agent", "igloo-verify-seed.py");
        if (!File.Exists(path))
            path = Path.Join(asmDir, "..", "_debian-family", "agent", "igloo-verify-seed.py");
        return NormalizeCrLf(await ReadRequiredPayloadFileAsync(path, ct).ConfigureAwait(false));
    }

    private static async Task<byte[]> RenderBootstrapAsync(string asmDir, byte[] verifier,
        byte[] bootstrapService, CancellationToken ct)
    {
        var path = Path.Join(ResolvePreseedDir(asmDir), "igloo-bootstrap.sh");
        var template = Encoding.UTF8.GetString(NormalizeCrLf(
            await ReadRequiredPayloadFileAsync(path, ct).ConfigureAwait(false)));
        if (!template.Contains("{{IGLOO_SEED_VERIFIER_SHA256}}", StringComparison.Ordinal))
            throw new InvalidDataException("Mint bootstrap is missing the verifier hash binding.");
        if (!template.Contains("{{IGLOO_BOOTSTRAP_SERVICE_SHA256}}", StringComparison.Ordinal))
            throw new InvalidDataException("Mint bootstrap is missing the service hash binding.");
        return Encoding.UTF8.GetBytes(template
            .Replace("{{IGLOO_SEED_VERIFIER_SHA256}}",
                Convert.ToHexString(SHA256.HashData(verifier)), StringComparison.Ordinal)
            .Replace("{{IGLOO_BOOTSTRAP_SERVICE_SHA256}}",
                Convert.ToHexString(SHA256.HashData(bootstrapService)), StringComparison.Ordinal));
    }

    private static string ResolvePreseedDir(string asmDir)
    {
        var namespaced = Path.Join(asmDir, "linuxmint-cinnamon", "preseed");
        return Directory.Exists(namespaced) ? namespaced : Path.Join(asmDir, "preseed");
    }

    public InstallerBootSpec GetInstallerBootSpec() => new()
    {
        MenuTitle = "Install Linux Mint (Igloo)",
        // Mint is a casper live ISO. casper's initrd already has iso-scan (no
        // hd-media download needed, unlike Debian's netinst): it loop-mounts the
        // whole ISO from the OEMDRV partition (iso-scan/filename). The signed
        // Mint casper initrd recognizes toram, which copies /cdrom to tmpfs;
        // iso-scan/copy_iso_to_ram is ignored by this initrd.
        KernelCmdline =
            "automatic-ubiquity noprompt file=/preseed.cfg boot=casper " +
            "iso-scan/filename=/mint.iso toram ---",
        KernelIsoPaths = ["casper/vmlinuz"],
        InitrdIsoPaths = ["casper/initrd.lz", "casper/initrd"],
        ExtraIsoFiles = Array.Empty<IsoFileStage>(),
        CopyFullIsoToVolume = true,        // casper loop-mounts the whole ISO
        MigrationDataOnSeed = false,       // user data stays on NTFS; the shared agent reads it there
        IsoVolumeFileName = "mint.iso",
        // Casper copies only the preseed from the initrd into the live root.
        // The hash-pinned partman helpers stay on OEMDRV until early_command
        // mounts that exact partition by PARTUUID.
        ConfigDelivery = ConfigDelivery.InjectIntoInitrd,
        InitrdConfigPath = "preseed.cfg",
    };

    //   Rendering                                

    private static string RenderFromTemplate(string template, MigrationManifest m)
    {
        var passwordHash = RequirePreseedValue(m.User.LinuxPasswordCrypted, "Linux password hash");
        if (!passwordHash.StartsWith("$6$", StringComparison.Ordinal))
            throw new InvalidDataException("Mint installer requires a SHA-512 crypt password hash.");
        // The reviewed preseed binds one exact free extent; the lab fallback
        // requires one installable gap. Neither path has a whole-disk recipe.
        if (!string.Equals(m.Hardware.InstallMode, "dual-boot", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Mint's unattended installer currently supports dual-boot allocation only.");

        return template
            .Replace("{{LOCALE}}", RequirePreseedValue(m.User.Locale, "locale"), StringComparison.Ordinal)
            .Replace("{{KEYMAP}}", RequirePreseedValue(m.User.Keymap, "keymap"), StringComparison.Ordinal)
            .Replace("{{TIMEZONE}}", RequirePreseedValue(m.User.Timezone, "timezone"), StringComparison.Ordinal)
            .Replace("{{HOSTNAME}}", LinuxHostname.FromMachine(m.User.WindowsComputerName, m.User.PreferredLinuxUsername), StringComparison.Ordinal)
            .Replace("{{LINUX_USERNAME}}", RequirePreseedValue(m.User.PreferredLinuxUsername, "username"), StringComparison.Ordinal)
            .Replace("{{FULL_NAME}}", RequirePreseedValue(m.User.FullName ?? m.User.PreferredLinuxUsername, "full name"), StringComparison.Ordinal)
            .Replace("{{PASSWORD}}", passwordHash, StringComparison.Ordinal);
    }

    private static string RequirePreseedValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) || c == '\\'))
            throw new InvalidDataException($"Mint installer {name} must be a nonempty value without line continuations.");
        return value;
    }

    private static byte[] NormalizeCrLf(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)'\r') < 0)
            return bytes;
        using var buf = new MemoryStream(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r')
            { buf.WriteByte((byte)'\n'); if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') i++; }
            else
                buf.WriteByte(bytes[i]);
        }
        return buf.ToArray();
    }

    private static DistroMetadata BuildMetadata(DistroManifest raw) => new()
    {
        DisplayName = raw.DisplayName,
        Description = raw.Description,
        DefaultDesktopEnvironment = raw.DefaultDesktopEnvironment ?? "Cinnamon",
        InstallerType = InstallerType.Ubiquity,
        IsoDownloadUrl = raw.Iso.DownloadUrl,
        IsoSha256 = raw.Iso.Sha256,
        IsoGpgSignatureUrl = raw.Iso.GpgSignatureUrl,
        IsoGpgKeyUrl = raw.Iso.GpgKeyUrl,
        Tags = raw.Tags,
        Screenshots = raw.Screenshots,
        MinimumRequirements = raw.MinimumRequirements is { } req
            ? new HardwareRequirements
            {
                MinRamBytes = req.MinRamBytes,
                MinDiskBytes = req.MinDiskBytes,
                RequiresUefi = req.RequiresUefi,
                Requires64Bit = req.Requires64Bit
            }
            : new HardwareRequirements(),
    };

    private static DistroMetadata FallbackMetadata() => new()
    {
        DisplayName = "Linux Mint",
        Description = "Linux Mint with the Cinnamon desktop.",
        DefaultDesktopEnvironment = "Cinnamon",
        InstallerType = InstallerType.Ubiquity,
        IsoDownloadUrl = new Uri("https://linuxmint.com"),
        IsoSha256 = string.Empty,
    };
}
