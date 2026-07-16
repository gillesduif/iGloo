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

        // Ubuntu's no-USB path boots the installer with `toram`: the ~6 GB
        // installer image is copied INTO MEMORY so the partition holding it can
        // be released while this same disk is repartitioned. On a machine with
        // too little RAM that doesn't fail cleanly — the live session OOMs
        // mid-install, after disk changes have already begun. Block up front:
        // by the time it would fail, the user experience is already ruined.
        // (The floor exists because the no-USB path boots with `toram`: the ~6 GB
        // installer image is copied into memory so its partition can be released
        // while this same disk is repartitioned. Below the floor, casper silently
        // skips toram and the install fails mid-flight. The user-facing text
        // deliberately omits the mechanism — needs + options only.)
        if (report.TotalRamBytes < ToramMinRamBytes)
            findings.Add(new PreflightFinding(FindingSeverity.Blocker, "UBUNTU_RAM_TORAM",
                $"Ubuntu needs at least {ToramMinRamBytes / (1024L * 1024 * 1024)} GB of memory."));
        return findings;
    }

    // toram budget: ~6 GB ISO in RAM + live desktop session + subiquity/curtin
    // working set. 10 GiB is the tested floor; below it the installer OOMs.
    private const long ToramMinRamBytes = 10L * 1024 * 1024 * 1024;

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
        // Ubuntu Desktop is a casper live ISO (like Mint): casper MUST find a live
        // filesystem or boot hangs retry-prompting on a dead console.
        //  * boot=casper + iso-scan/filename → loop-mount the whole ISO from the
        //    CIDATA partition (which is why the full ISO is copied there).
        //  * layerfs-path: 23.04+ desktop ISOs ship LAYERED squashfs images; when
        //    ISO-booting, casper must be told which layer stack to assemble (the
        //    ISO's own grub.cfg carries the same argument — keep in sync with it).
        //  * toram: copy the live medium to RAM so the ISO partition can be
        //    RELEASED before curtin repartitions this same disk — without it,
        //    partprobe fails with EBUSY (the medium partition is still mounted)
        //    and the install aborts. casper leaks the loop device + /isodevice
        //    mount even with toram (LP #684280); autoinstall early-commands in
        //    user-data.template clean those up. Needs RAM ≥ ISO size + ~4 GB.
        KernelCmdline =
            "autoinstall boot=casper toram layerfs-path=minimal.standard.live.squashfs " +
            "iso-scan/filename=/ubuntu.iso ---",
        KernelIsoPaths = ["casper/vmlinuz"],
        InitrdIsoPaths = ["casper/initrd"],
        ExtraIsoFiles  = Array.Empty<IsoFileStage>(),
        CopyFullIsoToVolume = true,          // casper loop-mounts the whole ISO
        IsoVolumeFileName   = "ubuntu.iso",
        // curtin must never ADD a partition (full-GPT rewrite + kernel reload →
        // fails while live media occupies this disk). Igloo pre-creates root;
        // the autoinstall config preserves everything and only formats it.
        PreCreateRootPartition = true,
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

        // Dual-boot: install into the unallocated space Igloo already carved next
        // to Windows.
        //
        // CRITICAL (data-loss): NEVER use a `layout:` preset here. Every subiquity
        // layout (direct / lvm / zfs) consumes the WHOLE disk and wipes Windows.
        // The only way to install alongside is an explicit curtin `config:` list.
        //
        // CRITICAL (data-loss, learned the hard way): curtin storage VERSION 2 is
        // AUTHORITATIVE — the config describes the disk's complete final state, and
        // any partition on the disk that is NOT declared gets DELETED (curtin wipes
        // its superblock and drops it from the table). Declaring only the ESP + root
        // made curtin start erasing the undeclared partitions — including Windows.
        // Therefore the config must list EVERY existing partition, preserve: true,
        // each with its real number/offset/size (subiquity also crashes on any
        // partition lacking offset/size: `int + None` in assign_omitted_offsets).
        //
        // Igloo enumerates the just-partitioned disk and expands the token below into
        // the FULL partition list (see DirectInstallService.SubstituteGeometryTokens):
        //  * EVERY partition: preserve: true + exact number/offset/size/type/uuid
        //    (Windows ESP/MSR/C:, Igloo's seed + ISO partitions, Windows Recovery,
        //    AND the root partition Igloo pre-created — PreCreateRootPartition).
        //    curtin adds nothing → writes no partition table → no disklabel
        //    rewrite, no renumbering, no partprobe on a busy disk.
        //  * the ESP additionally gets id 'esp' + grub_device: true and is reused for
        //    GRUB (mounted at /boot/efi) — preserved, never reformatted;
        //  * root (recognised by its Linux-filesystem GPT type) gets wipe:
        //    superblock + a fresh ext4 format + mount at / — contents replaced,
        //    table entry untouched.
        return string.Join("\n", new[]
        {
            "  storage:",
            "    config:",
            "      - {type: disk, id: disk0, match: {size: largest}, preserve: true, ptable: gpt}",
            "{{IGLOO_STORAGE_PARTITIONS}}",
            "      - {type: format, id: esp-fs, volume: esp, fstype: fat32, preserve: true}",
            "      - {type: format, id: root-fs, volume: root, fstype: ext4}",
            "      - {type: mount, id: root-mnt, device: root-fs, path: /}",
            "      - {type: mount, id: esp-mnt, device: esp-fs, path: /boot/efi}",
        });
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
