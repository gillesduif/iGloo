using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Distro.LinuxmintCinnamon;

public sealed class LinuxmintCinnamonPlugin : IDistroPlugin
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
        var path = Path.Join(asmDir, "distro.json");
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
                "Unlock the drive in Windows, or suspend BitLocker protection before re-running Igloo."));
        if (report.TotalRamBytes < Metadata.MinimumRequirements.MinRamBytes)
            findings.Add(new PreflightFinding(FindingSeverity.Warning, "RAM_BELOW_RECOMMENDED",
                $"This machine has {report.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GiB of RAM. " +
                $"Mint recommends at least {Metadata.MinimumRequirements.MinRamBytes / (1024.0 * 1024 * 1024):F0} GiB.",
                "Installation will proceed but the desktop may feel sluggish."));
        return findings;
    }

    public async Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var templatePath = Path.Join(asmDir, "preseed", "preseed.cfg.template");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("preseed.cfg.template missing from the Mint plugin output.");

        var preseed = RenderFromTemplate(await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false), manifest)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

        var config = new InstallerConfig("preseed.cfg", Encoding.UTF8.GetBytes(preseed), Array.Empty<InstallerConfigExtra>());
        return config;
    }

    public async Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var agentDir = Directory.Exists(Path.Join(asmDir, "agent"))
            ? Path.Join(asmDir, "agent")
            : Path.Join(asmDir, "..", "_debian-family", "agent");

        // igloo_boot.py is shared across families, so it sits in _shared/agent/
        // rather than next to the family agent.
        var sharedDir = Directory.Exists(Path.Join(asmDir, "agent"))
            ? Path.Join(asmDir, "agent")
            : Path.Join(asmDir, "..", "_shared", "agent");

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
            if (File.Exists(p))
                files.Add(new AgentFile(name, NormalizeCrLf(await File.ReadAllBytesAsync(p, ct).ConfigureAwait(false)), exe));
        }
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
            if (File.Exists(p))
                files.Add(new AgentFile(name, await File.ReadAllBytesAsync(p, ct).ConfigureAwait(false), false));
        }
        return new AgentPayload(files);
    }

    public InstallerBootSpec GetInstallerBootSpec() => new()
    {
        MenuTitle = "Install Linux Mint (Igloo)",
        // Mint is a casper live ISO. casper's initrd already has iso-scan (no
        // hd-media download needed, unlike Debian's netinst): it loop-mounts the
        // whole ISO from the OEMDRV partition (iso-scan/filename) and copies it to
        // RAM so the partitioner can repartition the same disk (copy_iso_to_ram).
        KernelCmdline =
            "automatic-ubiquity noprompt file=/preseed.cfg boot=casper " +
            "iso-scan/filename=/mint.iso iso-scan/copy_iso_to_ram=true ---",
        KernelIsoPaths = ["casper/vmlinuz"],
        InitrdIsoPaths = ["casper/initrd.lz", "casper/initrd"],
        ExtraIsoFiles = Array.Empty<IsoFileStage>(),
        CopyFullIsoToVolume = true,        // casper loop-mounts the whole ISO
        IsoVolumeFileName = "mint.iso",
        // Fully-unattended Ubiquity reads the preseed from the initrd (file=/preseed.cfg).
        ConfigDelivery = ConfigDelivery.InjectIntoInitrd,
        InitrdConfigPath = "preseed.cfg",
    };

    //   Rendering                                

    private static string RenderFromTemplate(string template, MigrationManifest m) => template
        .Replace("{{LOCALE}}", m.User.Locale, StringComparison.Ordinal)
        .Replace("{{KEYMAP}}", m.User.Keymap, StringComparison.Ordinal)
        .Replace("{{TIMEZONE}}", m.User.Timezone, StringComparison.Ordinal)
        .Replace("{{HOSTNAME}}", m.User.PreferredLinuxUsername + "-pc", StringComparison.Ordinal)
        .Replace("{{LINUX_USERNAME}}", m.User.PreferredLinuxUsername, StringComparison.Ordinal)
        .Replace("{{FULL_NAME}}", m.User.FullName ?? m.User.PreferredLinuxUsername, StringComparison.Ordinal)
        .Replace("{{PASSWORD}}", m.User.LinuxPasswordCrypted ?? "", StringComparison.Ordinal)
        .Replace("{{INSTALL_MODE}}", m.Hardware.InstallMode, StringComparison.Ordinal);

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
