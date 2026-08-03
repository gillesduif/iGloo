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
        var manifestPath = Path.Combine(asmDir, "distro.json");

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

        // The Debian Live image we install from carries non-free firmware, so Wi-Fi
        // and most GPUs work out of the box. NVIDIA still needs the proprietary
        // driver, installed by the first-boot agent from the non-free components.
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
        var templatePath = Path.Combine(asmDir, "preseed", "preseed.cfg.template");

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
        Add("display-apply.py", true);            // Cinnamon/X11 display-layout applier (Mint)
        Add("display-apply-gnome.py", true);      // GNOME/Wayland applier via mutter D-Bus
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
        // OFFLINE install via the Debian *Live* ISO + d-i's live-installer, the
        // same casper-style mechanism the Mint plugin uses. We copy the whole Live
        // ISO onto OEMDRV and let d-i's iso-scan loop-mount it
        // (iso-scan/filename=/debian.iso); live-installer then copies the ISO's
        // live/filesystem.squashfs onto the target  no mirror, no network. The
        // preseed is injected into the initrd so the install is fully unattended.
        // iso-scan/copy_iso_to_ram: copy the ISO into RAM and UNMOUNT the OEMDRV
        // partition, so the partitioner can rewrite the table of the same disk the
        // ISO lives on (otherwise: "unable to inform the kernel … in use"). Needs
        // RAM >= ISO size (~3.5 GB Live GNOME)  see the 4 GiB minimum in distro.json.
        // locale/keymap MUST be here, not only in the preseed. debian-installer asks
        // for language and country in localechooser, which runs BEFORE most preseed
        // processing - so a preseeded debian-installer/locale arrives too late and the
        // "unattended" install stops on its very first screen asking for a country.
        // The command line is the one place d-i reads them early enough. Both tokens
        // are substituted from the migration manifest by DirectInstallService.
        KernelCmdline =
            "auto=true priority=critical preseed/file=/preseed.cfg " +
            "locale={LOCALE} keymap={KEYMAP} " +
            "iso-scan/filename=/debian.iso iso-scan/copy_iso_to_ram=true " +
            // The install is fully unattended (auto=true priority=critical), so the
            // frontend only decides what the USER SEES while it runs. The text
            // frontend scrolls a wall of console output, which reads as a crash to
            // someone arriving from Windows; the GTK frontend shows a plain progress
            // screen. Nothing is asked either way. Paired with the gtk initrd below -
            // the two must match, since the text initrd has no GTK frontend in it.
            "DEBIAN_FRONTEND=gtk ---",
        // Fallback only  the primary kernel/initrd come from KernelUrl/InitrdUrl
        // below. The Live ISO's own install.amd initrd runs cdrom-detect (whole
        // devices), so it can't find an .iso FILE on a partition; that is exactly
        // why we boot the hd-media pair instead.
        KernelIsoPaths = ["install.amd/vmlinuz"],
        InitrdIsoPaths = ["install.amd/initrd.gz"],
        ExtraIsoFiles = Array.Empty<IsoFileStage>(),
        CopyFullIsoToVolume = true,         // iso-scan loop-mounts the whole ISO
        IsoVolumeFileName = "debian.iso",
        // The hd-media kernel+initrd run iso-scan (find an .iso file on a partition)
        // and load the rest of d-i  including the live-installer udeb  from the
        // scanned Live ISO's pool. Keep this pinned to the same release train as the
        // Live ISO (trixie = Debian 13); a wildly different installer build could
        // fail to load the Live ISO's udebs.
        KernelUrl = new Uri("https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/vmlinuz"),
        // gtk/ variant: same hd-media installer, but carrying the graphical frontend
        // (~68 MB vs ~30 MB). Larger, and it needs a working framebuffer - if a board
        // ever fails to bring one up, swapping this back to hd-media/initrd.gz and
        // DEBIAN_FRONTEND=text above restores the text installer.
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

        var password = m.User.LinuxPassword;

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
