using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Distro.Debian;

/// <summary>
/// <see cref="IDistroPlugin"/> for Debian (debian-installer + preseed).
///
/// Reference implementation for the Debian family. The rendered config is a
/// classic <c>preseed.cfg</c>; the unattended "%post" equivalent is the preseed
/// <c>d-i preseed/late_command</c>, which copies the Igloo agent + manifest onto
/// the installed system, copies the user's files from the live Windows NTFS
/// partition, and enables the first-boot service.
///
/// The first-boot agent is shared across the Debian family (Debian / Ubuntu /
/// Mint) and detects the distro at runtime via <c>/etc/os-release</c>.
/// </summary>
public sealed class DebianPlugin : IDistroPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string Id => "debian";

    public DistroMetadata Metadata { get; }

    public DebianPlugin()
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var manifestPath = Path.Combine(asmDir, "distro.json");

        DistroManifest? raw = null;
        if (File.Exists(manifestPath))
        {
            try
            { raw = JsonSerializer.Deserialize<DistroManifest>(File.ReadAllText(manifestPath), JsonOpts); }
            catch { /* fall through to defaults */ }
        }

        Metadata = raw is not null ? BuildMetadata(raw) : FallbackMetadata();
    }

    // ── IDistroPlugin ────────────────────────────────────────────────────────

    public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report)
    {
        var findings = new List<PreflightFinding>();

        // Debian ships free firmware by default; the netinst we use includes
        // non-free firmware. NVIDIA still needs the proprietary driver, installed
        // by the first-boot agent from the non-free-firmware/contrib components.
        if (string.Equals(report.GpuVendor, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PreflightFinding(
                FindingSeverity.Info, "DEBIAN_NVIDIA_NONFREE",
                "Your machine has an NVIDIA GPU. Igloo's first-boot agent installs the proprietary " +
                "driver from Debian's non-free components on first boot. An internet connection is " +
                "required at first boot.",
                null));
        }

        if (report.BitLocker == BitLockerState.EncryptedAndLocked)
        {
            findings.Add(new PreflightFinding(
                FindingSeverity.Blocker, "BITLOCKER_LOCKED",
                "BitLocker is enabled and the volume is locked. Igloo cannot resize a locked volume.",
                "Unlock the drive in Windows, or suspend BitLocker protection before re-running Igloo."));
        }

        if (report.TotalRamBytes < Metadata.MinimumRequirements.MinRamBytes)
        {
            findings.Add(new PreflightFinding(
                FindingSeverity.Warning, "RAM_BELOW_RECOMMENDED",
                $"This machine has {report.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GiB of RAM. " +
                $"Debian recommends at least {Metadata.MinimumRequirements.MinRamBytes / (1024.0 * 1024 * 1024):F0} GiB.",
                "Installation will proceed but the desktop may feel sluggish."));
        }

        return findings;
    }

    public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default)
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var templatePath = Path.Combine(asmDir, "preseed", "preseed.cfg.template");

        var preseed = File.Exists(templatePath)
            ? RenderFromTemplate(File.ReadAllText(templatePath), manifest)
            : throw new FileNotFoundException("preseed.cfg.template missing from the Debian plugin output.");

        // The preseed is executed by debian-installer (busybox) on Linux. Force LF
        // so a Windows checkout's CRLF never breaks the late_command shell. (Same
        // class of bug that broke the Fedora kickstart.)
        preseed = preseed.Replace("\r\n", "\n").Replace("\r", "\n");

        var config = new InstallerConfig(
            FileName: "preseed.cfg",
            Contents: Encoding.UTF8.GetBytes(preseed),
            Extras: Array.Empty<InstallerConfigExtra>());

        return Task.FromResult(config);
    }

    public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        // "agent/" in the build output, or the shared "_debian-family/agent/" when
        // the plugin DLL is loaded from its source folder (distros/<id>/ root).
        var agentDir = Directory.Exists(Path.Combine(asmDir, "agent"))
            ? Path.Combine(asmDir, "agent")
            : Path.Combine(asmDir, "..", "_debian-family", "agent");

        var files = new List<AgentFile>();
        void Add(string name, bool exe)
        {
            var p = Path.Combine(agentDir, name);
            if (File.Exists(p))
                files.Add(new AgentFile(name, NormalizeCrLf(File.ReadAllBytes(p)), exe));
        }
        Add("first-boot.sh", true);
        Add("agent.py", true);
        Add("igloo-first-boot.service", false);   // shipped to OEMDRV; the late hook installs it

        if (files.Count == 0)
            files.Add(new AgentFile("first-boot.sh",
                Encoding.UTF8.GetBytes(
                    "#!/usr/bin/env bash\n" +
                    "exec python3 /opt/igloo/agent.py --manifest /var/lib/igloo/manifest.json --log-dir /var/log/igloo\n"),
                Executable: true));

        return Task.FromResult(new AgentPayload(files));
    }

    public InstallerBootSpec GetInstallerBootSpec() => new()
    {
        MenuTitle = "Install Debian (Igloo)",
        // debian-installer expects its installation media (the dists/+pool/ tree),
        // not just kernel+initrd. We copy the whole netinst ISO onto OEMDRV and let
        // d-i's iso-scan loop-mount it (iso-scan/filename=/debian.iso). The preseed
        // is injected into the initrd so the install is fully unattended.
        // iso-scan/copy_iso_to_ram: copy the ISO into RAM and UNMOUNT the OEMDRV
        // partition, so the partitioner can rewrite the table of the same disk the
        // ISO lives on (otherwise: "unable to inform the kernel … in use"). Needs
        // RAM >= ISO size (~700 MB netinst), which is always available here.
        KernelCmdline =
            "auto=true priority=critical preseed/file=/preseed.cfg " +
            "iso-scan/filename=/debian.iso iso-scan/copy_iso_to_ram=true " +
            "DEBIAN_FRONTEND=text ---",
        KernelIsoPaths =
        [
            "install.amd/vmlinuz",  // netinst text installer
            "install.amd/xen/vmlinuz",
            "linux",
        ],
        InitrdIsoPaths =
        [
            "install.amd/initrd.gz",
            "install.amd/gtk/initrd.gz",
            "initrd.gz",
        ],
        ExtraIsoFiles = Array.Empty<IsoFileStage>(),
        CopyFullIsoToVolume = true,         // iso-scan loop-mounts the whole ISO
        IsoVolumeFileName = "debian.iso",
        // The netinst initrd runs cdrom-detect (whole-device CDs only) and can't find
        // an .iso FILE on a partition. The hd-media kernel+initrd run iso-scan, which
        // can. Download them from the Debian mirror (trixie = Debian 13).
        KernelUrl = new Uri("https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/vmlinuz"),
        InitrdUrl = new Uri("https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/initrd.gz"),
        ConfigDelivery = ConfigDelivery.InjectIntoInitrd,
        InitrdConfigPath = "preseed.cfg",
    };

    // ── Rendering ──────────────────────────────────────────────────────────────

    private static string RenderFromTemplate(string template, MigrationManifest m)
    {
        var folderList = string.Join(" ", m.Files.IncludedFolders);

        var folderMap = m.Files.Folders.Count > 0
            ? string.Join("\n", m.Files.Folders.Select(f => $"{f.Name}|{f.SourceRelativePath}"))
            : string.Join("\n", m.Files.IncludedFolders.Select(n => $"{n}|{n}"));

        var browserMap = string.Join("\n",
            m.Browsers
                .Where(b => !string.IsNullOrEmpty(b.SourceRelativePath) && !string.IsNullOrEmpty(b.DestRelativePath))
                .Select(b => $"{b.SourceRelativePath}|{b.DestRelativePath}"));

        var password = m.User.LinuxPassword;

        return template
            .Replace("{{LOCALE}}", m.User.Locale)
            .Replace("{{KEYMAP}}", m.User.Keymap)
            .Replace("{{TIMEZONE}}", m.User.Timezone)
            .Replace("{{HOSTNAME}}", m.User.PreferredLinuxUsername + "-pc")
            .Replace("{{WINDOWS_USERNAME}}", m.User.WindowsUsername)
            .Replace("{{LINUX_USERNAME}}", m.User.PreferredLinuxUsername)
            .Replace("{{FULL_NAME}}", m.User.FullName ?? m.User.PreferredLinuxUsername)
            .Replace("{{PASSWORD}}", password)
            .Replace("{{INSTALL_MODE}}", m.Hardware.InstallMode)
            .Replace("{{INCLUDED_FOLDERS}}", folderList)
            .Replace("{{FOLDER_MAP}}", folderMap)
            .Replace("{{BROWSER_MAP}}", browserMap);
    }

    private static byte[] NormalizeCrLf(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)'\r') < 0)
            return bytes;
        var buf = new MemoryStream(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r')
            {
                buf.WriteByte((byte)'\n');
                if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
                    i++;
            }
            else
                buf.WriteByte(bytes[i]);
        }
        return buf.ToArray();
    }

    // ── Metadata ───────────────────────────────────────────────────────────────

    private static DistroMetadata BuildMetadata(DistroManifest raw) => new()
    {
        DisplayName = raw.DisplayName,
        Description = raw.Description,
        DefaultDesktopEnvironment = raw.DefaultDesktopEnvironment ?? "GNOME",
        InstallerType = InstallerType.DebianInstaller,
        IsoDownloadUrl = new Uri(raw.Iso.DownloadUrl),
        IsoSha256 = raw.Iso.Sha256,
        IsoGpgSignatureUrl = raw.Iso.GpgSignatureUrl is not null ? new Uri(raw.Iso.GpgSignatureUrl) : null,
        IsoGpgKeyUrl = raw.Iso.GpgKeyUrl is not null ? new Uri(raw.Iso.GpgKeyUrl) : null,
        Tags = raw.Tags,
        Screenshots = raw.Screenshots,
        MinimumRequirements = raw.MinimumRequirements is { } req
            ? new HardwareRequirements
            {
                MinRamBytes = req.MinRamBytes,
                MinDiskBytes = req.MinDiskBytes,
                RequiresUefi = req.RequiresUefi,
                Requires64Bit = req.Requires64Bit,
            }
            : new HardwareRequirements(),
    };

    private static DistroMetadata FallbackMetadata() => new()
    {
        DisplayName = "Debian",
        Description = "Debian 13 with the GNOME desktop.",
        DefaultDesktopEnvironment = "GNOME",
        InstallerType = InstallerType.DebianInstaller,
        IsoDownloadUrl = new Uri("https://www.debian.org"),
        IsoSha256 = string.Empty,
    };
}
