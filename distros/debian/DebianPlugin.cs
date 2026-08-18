using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Distro.Debian;

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
        var manifestPath = Path.Join(asmDir, "distro.json");

        var raw = TryLoadManifest(manifestPath);
        Metadata = raw is not null ? BuildMetadata(raw) : FallbackMetadata();
    }

    private static DistroManifest? TryLoadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<DistroManifest>(File.ReadAllText(manifestPath), JsonOpts);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    //   IDistroPlugin                             

    public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var findings = new List<PreflightFinding>();

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
                "Unlock the drive in Windows or suspend BitLocker protection before re-running Igloo."));
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

    public async Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var templatePath = Path.Join(asmDir, "preseed", "preseed.cfg.template");

        var preseed = File.Exists(templatePath)
            ? RenderFromTemplate(await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false), manifest)
            : throw new FileNotFoundException("preseed.cfg.template missing from the Debian plugin output.");

        // The preseed is executed by debian-installer (busybox) on Linux. Force LF
        // so a Windows checkout's CRLF never breaks the late_command shell. (Same
        // class of bug that broke the Fedora kickstart.)
        preseed = preseed.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

        var config = new InstallerConfig(
            FileName: "preseed.cfg",
            Contents: Encoding.UTF8.GetBytes(preseed),
            Extras: Array.Empty<InstallerConfigExtra>());

        return config;
    }

    public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        // "agent/" in the build output, or the shared "_debian-family/agent/" when
        // the plugin DLL is loaded from its source folder (distros/<id>/ root).
        var agentDir = Directory.Exists(Path.Join(asmDir, "agent"))
            ? Path.Join(asmDir, "agent")
            : Path.Join(asmDir, "..", "_debian-family", "agent");

        // igloo_boot.py is shared across families, so it sits in _shared/agent/
        // rather than next to the family agent.
        var sharedDir = Directory.Exists(Path.Join(asmDir, "agent"))
            ? Path.Join(asmDir, "agent")
            : Path.Join(asmDir, "..", "_shared", "agent");
        // The theme archives moved to _shared/grub-theme/; keep the build-output
        // location as the first candidate.
        var themeDir = Path.Join(asmDir, "..", "_shared", "grub-theme");

        var files = new List<AgentFile>();
        void AddFrom(string dir, string name, bool exe)
        {
            var p = Path.Join(dir, name);
            if (File.Exists(p))
                files.Add(new AgentFile(name, NormalizeCrLf(File.ReadAllBytes(p)), exe));
        }
        void Add(string name, bool exe) => AddFrom(agentDir, name, exe);

        Add("first-boot.sh", true);
        Add("agent.py", true);
        Add("display-apply.py", true);            // Cinnamon/X11 display-layout applier (Mint)
        Add("display-apply-gnome.py", true);      // GNOME/Wayland applier via mutter D-Bus
        AddFrom(sharedDir, "igloo_boot.py", false);
        Add("igloo-first-boot.service", false);   // shipped to OEMDRV; the late hook installs it

        // GRUB theme archives (M17 boot menu). Binary: must bypass NormalizeCrLf,
        // which rewrites 0x0D bytes and would corrupt the gzip stream.
        void AddRaw(string name)
        {
            var p = Path.Join(agentDir, name);
            if (!File.Exists(p))
                p = Path.Join(themeDir, name);
            if (File.Exists(p))
                files.Add(new AgentFile(name, File.ReadAllBytes(p), false));
        }
        AddRaw("grub-theme-stylish-1080p.tar.gz");
        AddRaw("grub-theme-stylish-4k.tar.gz");

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
        // locale/keymap must be on the command line: localechooser runs before preseed processing.
        KernelCmdline =
            "auto=true priority=critical preseed/file=/preseed.cfg " +
            "locale={LOCALE} keymap={KEYMAP} " +
            "iso-scan/filename=/debian.iso iso-scan/copy_iso_to_ram=true " +
            // Must pair with the gtk initrd below the text initrd has no GTK frontend.
            "DEBIAN_FRONTEND=gtk ---",
        // Fallback only the hd-media pair from KernelUrl/InitrdUrl is primary.
        KernelIsoPaths = ["install.amd/vmlinuz"],
        InitrdIsoPaths = ["install.amd/initrd.gz"],
        ExtraIsoFiles = Array.Empty<IsoFileStage>(),
        CopyFullIsoToVolume = true,
        IsoVolumeFileName = "debian.iso",
        // Keep pinned to the same release train as the Live ISO (trixie = Debian 13).
        KernelUrl = new Uri("https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/vmlinuz"),
        InitrdUrl = new Uri("https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/gtk/initrd.gz"),
        ConfigDelivery = ConfigDelivery.InjectIntoInitrd,
        InitrdConfigPath = "preseed.cfg",
    };

    //   Rendering                                

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

        var password = m.User.LinuxPasswordCrypted;

        return template
            .Replace("{{LOCALE}}", m.User.Locale, StringComparison.Ordinal)
            .Replace("{{KEYMAP}}", m.User.Keymap, StringComparison.Ordinal)
            .Replace("{{TIMEZONE}}", m.User.Timezone, StringComparison.Ordinal)
            .Replace("{{HOSTNAME}}", m.User.PreferredLinuxUsername + "-pc", StringComparison.Ordinal)
            .Replace("{{WINDOWS_USERNAME}}", m.User.WindowsUsername, StringComparison.Ordinal)
            .Replace("{{LINUX_USERNAME}}", m.User.PreferredLinuxUsername, StringComparison.Ordinal)
            .Replace("{{FULL_NAME}}", m.User.FullName ?? m.User.PreferredLinuxUsername, StringComparison.Ordinal)
            .Replace("{{PASSWORD}}", password, StringComparison.Ordinal)
            .Replace("{{INSTALL_MODE}}", m.Hardware.InstallMode, StringComparison.Ordinal)
            .Replace("{{INCLUDED_FOLDERS}}", folderList, StringComparison.Ordinal)
            .Replace("{{FOLDER_MAP}}", folderMap, StringComparison.Ordinal)
            .Replace("{{BROWSER_MAP}}", browserMap, StringComparison.Ordinal);
    }

    private static byte[] NormalizeCrLf(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)'\r') < 0)
            return bytes;
        using var buf = new MemoryStream(bytes.Length);
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

    //   Metadata                                ─

    private static DistroMetadata BuildMetadata(DistroManifest raw) => new()
    {
        DisplayName = raw.DisplayName,
        Description = raw.Description,
        DefaultDesktopEnvironment = raw.DefaultDesktopEnvironment ?? "GNOME",
        InstallerType = InstallerType.DebianInstaller,
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
