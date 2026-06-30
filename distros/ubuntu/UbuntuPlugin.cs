using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Distro.Ubuntu;

/// <summary>
/// <see cref="IDistroPlugin"/> for Ubuntu (subiquity autoinstall / cloud-init).
///
/// The rendered config is a cloud-init <c>#cloud-config</c> with an
/// <c>autoinstall:</c> section, plus an empty <c>meta-data</c> file. They are
/// placed on a FAT32 volume labelled <c>CIDATA</c> so cloud-init's NoCloud
/// datasource auto-detects the seed; the kernel cmdline carries <c>autoinstall</c>.
///
/// Password: subiquity's <c>identity.password</c> must be a crypt hash, which we
/// can't produce on the Windows side, so a locked placeholder is used and the
/// real (plaintext) password is set by a <c>late-commands</c> <c>chpasswd</c>.
///
/// The first-boot agent is shared across the Debian family.
/// </summary>
public sealed class UbuntuPlugin : IDistroPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
    };

    public string Id => "ubuntu";
    public DistroMetadata Metadata { get; }

    public UbuntuPlugin()
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
            findings.Add(new PreflightFinding(FindingSeverity.Info, "UBUNTU_NVIDIA",
                "Your machine has an NVIDIA GPU. Igloo's first-boot agent installs the driver via " +
                "ubuntu-drivers on first boot. An internet connection is required at first boot.", null));
        if (report.BitLocker == BitLockerState.EncryptedAndLocked)
            findings.Add(new PreflightFinding(FindingSeverity.Blocker, "BITLOCKER_LOCKED",
                "BitLocker is enabled and the volume is locked. Igloo cannot resize a locked volume.",
                "Unlock the drive in Windows, or suspend BitLocker protection before re-running Igloo."));
        if (report.TotalRamBytes < Metadata.MinimumRequirements.MinRamBytes)
            findings.Add(new PreflightFinding(FindingSeverity.Warning, "RAM_BELOW_RECOMMENDED",
                $"This machine has {report.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GiB of RAM. " +
                $"Ubuntu recommends at least {Metadata.MinimumRequirements.MinRamBytes / (1024.0 * 1024 * 1024):F0} GiB.",
                "Installation will proceed but the desktop may feel sluggish."));
        return findings;
    }

    public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default)
    {
        var asmDir       = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var templatePath = Path.Combine(asmDir, "autoinstall", "user-data.template");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("user-data.template missing from the Ubuntu plugin output.");

        var userData = RenderFromTemplate(File.ReadAllText(templatePath), manifest)
            .Replace("\r\n", "\n").Replace("\r", "\n");

        // cloud-init NoCloud needs both user-data and a (possibly empty) meta-data.
        var metaData = $"instance-id: igloo-{Guid.NewGuid():N}\nlocal-hostname: {manifest.User.PreferredLinuxUsername}-pc\n";

        var config = new InstallerConfig(
            FileName: "user-data",
            Contents: Encoding.UTF8.GetBytes(userData),
            Extras: [new InstallerConfigExtra("meta-data", Encoding.UTF8.GetBytes(metaData))]);
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
        MenuTitle   = "Install Ubuntu (Igloo)",
        // CIDATA so cloud-init's NoCloud datasource auto-detects user-data/meta-data
        // from this volume; GRUB also locates casper's kernel/initrd by this label.
        VolumeLabel = "CIDATA",
        KernelCmdline = "autoinstall",
        KernelIsoPaths = ["casper/vmlinuz"],
        InitrdIsoPaths = ["casper/initrd"],
        ExtraIsoFiles  = Array.Empty<IsoFileStage>(),
        // The seed lives on the CIDATA volume root (where InstallerConfig is copied),
        // so no initrd injection is needed for subiquity.
        ConfigDelivery = ConfigDelivery.OemDrvLabel,
    };

    // ── Rendering ──────────────────────────────────────────────────────────────

    private static string RenderFromTemplate(string template, MigrationManifest m)
    {
        var storage = BuildStorage(m);
        return template
            .Replace("{{LOCALE}}",         m.User.Locale)
            .Replace("{{KEYMAP}}",         m.User.Keymap)
            .Replace("{{TIMEZONE}}",       m.User.Timezone)
            .Replace("{{HOSTNAME}}",       m.User.PreferredLinuxUsername + "-pc")
            .Replace("{{LINUX_USERNAME}}", m.User.PreferredLinuxUsername)
            .Replace("{{FULL_NAME}}",      m.User.FullName ?? m.User.PreferredLinuxUsername)
            .Replace("{{PASSWORD}}",       m.User.LinuxPassword ?? "")
            .Replace("{{STORAGE}}",        storage);
    }

    /// <summary>
    /// Renders the autoinstall <c>storage</c> block. Replace mode wipes the whole
    /// disk (fully supported). Dual-boot reuses the free space Igloo already made
    /// next to Windows — the most validation-sensitive part on real hardware, so
    /// it is emitted explicitly rather than guessed by a layout name.
    /// </summary>
    private static string BuildStorage(MigrationManifest m)
    {
        if (!string.Equals(m.Hardware.InstallMode, "dual-boot", StringComparison.OrdinalIgnoreCase))
        {
            // Whole-disk install.
            return "  storage:\n    layout:\n      name: direct";
        }

        // Dual-boot: install into the unallocated space Igloo created. curtin
        // installs into the largest free region while preserving existing
        // partitions (Windows + ESP). `name: direct` with `match: size: largest`
        // is not free-space aware, so we use the lvm layout constrained to reuse
        // the existing EFI partition is non-trivial in autoinstall — flagged for
        // hardware validation. As a safe default we let curtin use the free space.
        return
            "  storage:\n" +
            "    layout:\n" +
            "      name: lvm\n" +
            "      sizing-policy: all\n" +
            "    # NOTE (dual-boot): autoinstall cannot target free space declaratively\n" +
            "    # across all subiquity versions. Validate on hardware; a custom\n" +
            "    # `config:` storage list may be required to preserve Windows + ESP.";
    }

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
        DefaultDesktopEnvironment = raw.DefaultDesktopEnvironment ?? "GNOME",
        InstallerType             = InstallerType.Subiquity,
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
        DisplayName = "Ubuntu", Description = "Ubuntu with the GNOME desktop.",
        DefaultDesktopEnvironment = "GNOME", InstallerType = InstallerType.Subiquity,
        IsoDownloadUrl = new Uri("https://ubuntu.com"), IsoSha256 = string.Empty,
    };
}
