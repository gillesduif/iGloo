using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Distro.LinuxmintCinnamon;

/// <summary>
/// <see cref="IDistroPlugin"/> for Linux Mint Cinnamon (Ubiquity / automatic-ubiquity).
///
/// Mint installs from a casper live ISO via Ubiquity. Fully-unattended Ubiquity
/// is driven by a preseed (honouring most debian-installer keys plus
/// <c>ubiquity/*</c> ones) loaded with <c>automatic-ubiquity file=/preseed.cfg</c>,
/// where the preseed is injected into the initrd. <c>ubiquity/success_command</c>
/// is the late hook that bootstraps the Igloo agent.
///
/// Shares the Debian-family first-boot agent.
/// </summary>
public sealed class LinuxmintCinnamonPlugin : IDistroPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
    };

    public string Id => "linuxmint-cinnamon";
    public DistroMetadata Metadata { get; }

    public LinuxmintCinnamonPlugin()
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var path   = Path.Combine(asmDir, "distro.json");
        DistroManifest? raw = null;
        if (File.Exists(path))
        {
            try { raw = JsonSerializer.Deserialize<DistroManifest>(File.ReadAllText(path), JsonOpts); }
            catch { /* defaults */ }
        }
        Metadata = raw is not null ? BuildMetadata(raw) : FallbackMetadata();
    }

    public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report)
    {
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

    public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default)
    {
        var asmDir       = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var templatePath = Path.Combine(asmDir, "preseed", "preseed.cfg.template");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("preseed.cfg.template missing from the Mint plugin output.");

        var preseed = RenderFromTemplate(File.ReadAllText(templatePath), manifest)
            .Replace("\r\n", "\n").Replace("\r", "\n");

        var config = new InstallerConfig("preseed.cfg", Encoding.UTF8.GetBytes(preseed), Array.Empty<InstallerConfigExtra>());
        return Task.FromResult(config);
    }

    public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        var asmDir   = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var agentDir = Directory.Exists(Path.Combine(asmDir, "agent"))
            ? Path.Combine(asmDir, "agent")
            : Path.Combine(asmDir, "..", "_debian-family", "agent");

        var files = new List<AgentFile>();
        foreach (var (name, exe) in new[] { ("first-boot.sh", true), ("agent.py", true), ("igloo-first-boot.service", false) })
        {
            var p = Path.Combine(agentDir, name);
            if (File.Exists(p)) files.Add(new AgentFile(name, NormalizeCrLf(File.ReadAllBytes(p)), exe));
        }
        return Task.FromResult(new AgentPayload(files));
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
        ExtraIsoFiles  = Array.Empty<IsoFileStage>(),
        CopyFullIsoToVolume = true,        // casper loop-mounts the whole ISO
        IsoVolumeFileName   = "mint.iso",
        // Fully-unattended Ubiquity reads the preseed from the initrd (file=/preseed.cfg).
        ConfigDelivery   = ConfigDelivery.InjectIntoInitrd,
        InitrdConfigPath = "preseed.cfg",
    };

    // ── Rendering ──────────────────────────────────────────────────────────────

    private static string RenderFromTemplate(string template, MigrationManifest m) => template
        .Replace("{{LOCALE}}",         m.User.Locale)
        .Replace("{{KEYMAP}}",         m.User.Keymap)
        .Replace("{{TIMEZONE}}",       m.User.Timezone)
        .Replace("{{HOSTNAME}}",       m.User.PreferredLinuxUsername + "-pc")
        .Replace("{{LINUX_USERNAME}}", m.User.PreferredLinuxUsername)
        .Replace("{{FULL_NAME}}",      m.User.FullName ?? m.User.PreferredLinuxUsername)
        .Replace("{{PASSWORD}}",       m.User.LinuxPassword ?? "")
        .Replace("{{INSTALL_MODE}}",   m.Hardware.InstallMode);

    private static byte[] NormalizeCrLf(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)'\r') < 0) return bytes;
        var buf = new MemoryStream(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r') { buf.WriteByte((byte)'\n'); if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') i++; }
            else buf.WriteByte(bytes[i]);
        }
        return buf.ToArray();
    }

    private static DistroMetadata BuildMetadata(DistroManifest raw) => new()
    {
        DisplayName               = raw.DisplayName,
        Description               = raw.Description,
        DefaultDesktopEnvironment = raw.DefaultDesktopEnvironment ?? "Cinnamon",
        InstallerType             = InstallerType.Ubiquity,
        IsoDownloadUrl            = new Uri(raw.Iso.DownloadUrl),
        IsoSha256                 = raw.Iso.Sha256,
        IsoGpgSignatureUrl        = raw.Iso.GpgSignatureUrl is not null ? new Uri(raw.Iso.GpgSignatureUrl) : null,
        IsoGpgKeyUrl              = raw.Iso.GpgKeyUrl       is not null ? new Uri(raw.Iso.GpgKeyUrl)       : null,
        Tags                      = raw.Tags,
        Screenshots               = raw.Screenshots,
        MinimumRequirements       = raw.MinimumRequirements is { } req
            ? new HardwareRequirements { MinRamBytes = req.MinRamBytes, MinDiskBytes = req.MinDiskBytes,
                                         RequiresUefi = req.RequiresUefi, Requires64Bit = req.Requires64Bit }
            : new HardwareRequirements(),
    };

    private static DistroMetadata FallbackMetadata() => new()
    {
        DisplayName = "Linux Mint", Description = "Linux Mint with the Cinnamon desktop.",
        DefaultDesktopEnvironment = "Cinnamon", InstallerType = InstallerType.Ubiquity,
        IsoDownloadUrl = new Uri("https://linuxmint.com"), IsoSha256 = string.Empty,
    };
}
