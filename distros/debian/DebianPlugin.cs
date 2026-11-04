using System.Globalization;
using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;
using Igloo.Distro.Debian.Deployment;

namespace Igloo.Distro.Debian;

public sealed class DebianPlugin : IDistroPlugin, IReviewedInstallerConfigProvider
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
        var manifestPath = Path.Join(asmDir, "debian", "distro.json");
        if (!File.Exists(manifestPath))
            manifestPath = Path.Join(asmDir, "distro.json");

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
                "Unlock and fully decrypt the drive in Windows before re-running Igloo."));
        }

        if (report.TotalRamBytes < Metadata.MinimumRequirements.MinRamBytes)
        {
            findings.Add(new PreflightFinding(
                FindingSeverity.Blocker, "DEBIAN_INSTALL_RAM_BELOW_MINIMUM",
                $"This machine has {report.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GiB of RAM. " +
                $"The iGloo Debian Live ISO installer requires at least {Metadata.MinimumRequirements.MinRamBytes / (1024.0 * 1024 * 1024):F0} GiB for its ISO-to-RAM path.",
                "Use a machine with sufficient RAM for the offline installer."));
        }

        return findings;
    }

    // Artifact rendering and this boot recipe are read-only. The Windows physical
    // preparation path still has its own exact-target and dedicated-ESP gates.
    public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default) =>
        RenderInstallerConfigCoreAsync(manifest, null, null, null, ct);

    public Task<InstallerConfig> RenderReviewedInstallerConfigAsync(MigrationManifest manifest,
        ReviewedInstallerFreeExtentV1 installerRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installerRoot);
        var selectedDisk = manifest.Hardware.TargetDiskGuid;
        var selectedBytes = manifest.Hardware.TargetDiskBytes;
        if (installerRoot.GenerationId == Guid.Empty ||
            installerRoot.TargetDiskGuid == Guid.Empty ||
            installerRoot.LinuxEspPartitionGuid == Guid.Empty ||
            installerRoot.LinuxEspPartitionGuid == installerRoot.TargetDiskGuid ||
            selectedDisk != installerRoot.TargetDiskGuid ||
            selectedBytes <= 0 || installerRoot.OffsetBytes == 0 ||
            installerRoot.OffsetBytes % (1024UL * 1024) != 0 ||
            installerRoot.SizeBytes == 0 || installerRoot.SizeBytes % 512 != 0 ||
            installerRoot.RootMinimumBytes < (ulong)Metadata.MinimumRequirements.MinDiskBytes ||
            installerRoot.RootMinimumBytes > installerRoot.SizeBytes ||
            installerRoot.OffsetBytes >= (ulong)selectedBytes ||
            installerRoot.SizeBytes > (ulong)selectedBytes - installerRoot.OffsetBytes)
            throw new InvalidDataException("Debian's reviewed installer extent differs from the selected disk or root requirement.");
        return RenderInstallerConfigCoreAsync(manifest, installerRoot.OffsetBytes,
            installerRoot.SizeBytes, installerRoot.LinuxEspPartitionGuid, ct);
    }

    private async Task<InstallerConfig> RenderInstallerConfigCoreAsync(MigrationManifest manifest,
        ulong? freeOffset, ulong? freeSize, Guid? linuxEspPartitionGuid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.DistroId, Id, StringComparison.Ordinal))
            throw new InvalidDataException("Debian installer manifest has the wrong distribution ID.");
        if (!string.Equals(manifest.Hardware.InstallMode, "dual-boot", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Debian's unattended preseed supports prepared dual-boot free space only.");
        if (manifest.Hardware.TargetDiskGuid is not { } targetGuid || targetGuid == Guid.Empty ||
            manifest.Hardware.TargetDiskBytes <= 0)
            throw new InvalidDataException("Debian installer requires the selected GPT disk identity and exact byte size.");
        if (freeOffset.HasValue != freeSize.HasValue ||
            (freeOffset.HasValue &&
             (freeOffset.Value == 0 || freeSize!.Value == 0 ||
              freeOffset.Value >= (ulong)manifest.Hardware.TargetDiskBytes ||
              freeSize.Value > (ulong)manifest.Hardware.TargetDiskBytes - freeOffset.Value)))
            throw new InvalidDataException("Debian installer requires a complete, in-disk reviewed free extent.");

        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var preseedDir = Path.Join(asmDir, "debian", "preseed");
        if (!Directory.Exists(preseedDir))
            preseedDir = Path.Join(asmDir, "preseed");
        var templatePath = Path.Join(preseedDir, "preseed.cfg.template");
        var template = await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false);
        var setupTemplate = Encoding.UTF8.GetString(NormalizeLineEndings(await ReadRequiredPayloadAsync(
            Path.Join(preseedDir, "igloo-bound-free-setup.sh"), ct).ConfigureAwait(false)));
        var setup = setupTemplate
            .Replace("{{TARGET_DISK_GUID}}", targetGuid.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{TARGET_DISK_BYTES}}", manifest.Hardware.TargetDiskBytes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{INSTALLER_FREE_OFFSET_BYTES}}", freeOffset?.ToString(CultureInfo.InvariantCulture) ?? "none", StringComparison.Ordinal)
            .Replace("{{INSTALLER_FREE_SIZE_BYTES}}", freeSize?.ToString(CultureInfo.InvariantCulture) ?? "none", StringComparison.Ordinal)
            .Replace("{{LINUX_ESP_PARTITION_GUID}}", linuxEspPartitionGuid?.ToString("D", CultureInfo.InvariantCulture) ?? "none", StringComparison.Ordinal)
            .Replace("{{MINIMUM_INSTALL_BYTES}}", Metadata.MinimumRequirements.MinDiskBytes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (setup.Contains("{{", StringComparison.Ordinal))
            throw new InvalidDataException("Debian partman setup contains an unresolved template value.");
        var choices = NormalizeLineEndings(await ReadRequiredPayloadAsync(
            Path.Join(preseedDir, "igloo-bound-free-choices.sh"), ct).ConfigureAwait(false));
        var action = NormalizeLineEndings(await ReadRequiredPayloadAsync(
            Path.Join(preseedDir, "igloo-bound-free-do-option.sh"), ct).ConfigureAwait(false));
        var verifierPath = Path.Join(asmDir, "agent", "igloo-verify-seed.py");
        if (!File.Exists(verifierPath))
            verifierPath = Path.Join(asmDir, "..", "_debian-family", "agent", "igloo-verify-seed.py");
        var seedVerifier = NormalizeLineEndings(await ReadRequiredPayloadAsync(
            verifierPath, ct).ConfigureAwait(false));
        var user = manifest.User;
        var passwordHash = RequiredPreseedValue(user.LinuxPasswordCrypted, "Linux password hash");
        if (!passwordHash.StartsWith("$6$", StringComparison.Ordinal) ||
            passwordHash.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Debian installer requires a SHA-512 crypt password hash.");

        var preseed = template
            .Replace("{{LOCALE}}", RequiredPreseedValue(user.Locale, "locale"), StringComparison.Ordinal)
            .Replace("{{KEYMAP}}", RequiredPreseedValue(user.Keymap, "keymap"), StringComparison.Ordinal)
            .Replace("{{TIMEZONE}}", RequiredPreseedValue(user.Timezone, "timezone"), StringComparison.Ordinal)
            .Replace("{{HOSTNAME}}", LinuxHostname.FromMachine(user.WindowsComputerName, user.PreferredLinuxUsername), StringComparison.Ordinal)
            .Replace("{{LINUX_USERNAME}}", RequiredPreseedValue(user.PreferredLinuxUsername, "username"), StringComparison.Ordinal)
            .Replace("{{FULL_NAME}}", RequiredPreseedValue(user.FullName ?? user.PreferredLinuxUsername, "full name"), StringComparison.Ordinal)
            .Replace("{{PASSWORD}}", passwordHash, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        if (preseed.Contains("{{", StringComparison.Ordinal))
            throw new InvalidDataException("Debian preseed contains an unresolved template value.");
        return new InstallerConfig("preseed.cfg", Encoding.UTF8.GetBytes(preseed),
        [
            new InstallerConfigExtra("igloo-initrd/igloo-bound-free-setup.sh", Encoding.UTF8.GetBytes(setup)),
            new InstallerConfigExtra("igloo-initrd/igloo-bound-free-choices.sh", choices),
            new InstallerConfigExtra("igloo-initrd/igloo-bound-free-do-option.sh", action),
            new InstallerConfigExtra("igloo-initrd/igloo-verify-seed.py", seedVerifier),
        ]);
    }

    public async Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var agentDir = Directory.Exists(Path.Join(asmDir, "agent"))
            ? Path.Join(asmDir, "agent")
            : Path.Join(asmDir, "..", "_debian-family", "agent");
        var sharedDir = Directory.Exists(Path.Join(asmDir, "agent"))
            ? Path.Join(asmDir, "agent")
            : Path.Join(asmDir, "..", "_shared", "agent");
        var themeDir = Path.Join(asmDir, "..", "_shared", "grub-theme");
        var files = new List<AgentFile>();
        foreach (var (dir, name, executable) in new[]
                 {
                     (agentDir, "first-boot.sh", true),
                     (agentDir, "agent.py", true),
                     (agentDir, "display-apply.py", true),
                     (agentDir, "display-apply-gnome.py", true),
                     (sharedDir, "igloo_boot.py", false),
                     (agentDir, "igloo-first-boot.service", false),
                 })
        {
            var payload = await ReadRequiredPayloadAsync(Path.Join(dir, name), ct).ConfigureAwait(false);
            files.Add(new AgentFile(name, NormalizeLineEndings(payload), executable));
        }
        foreach (var name in new[] { "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz" })
        {
            var path = Path.Join(agentDir, name);
            if (!File.Exists(path))
                path = Path.Join(themeDir, name);
            files.Add(new AgentFile(name, await ReadRequiredPayloadAsync(path, ct).ConfigureAwait(false), false));
        }
        return new AgentPayload(files);
    }

    public InstallerBootSpec GetInstallerBootSpec() => new()
    {
        MenuTitle = "Install Debian (Igloo)",
        // localechooser runs before preseed processing.
        KernelCmdline =
            "auto=true priority=critical preseed/file=/preseed.cfg " +
            "locale={LOCALE} keymap={KEYMAP} " +
            "iso-scan/filename=/debian.iso iso-scan/copy_iso_to_ram=true " +
            "DEBIAN_FRONTEND=gtk ---",
        // The hd-media pair is primary. These paths let the preparation code
        // measure the current signed Live ISO without a recursive file scan.
        KernelIsoPaths = ["live/vmlinuz"],
        InitrdIsoPaths = ["live/initrd.img"],
        ExtraIsoFiles = Array.Empty<IsoFileStage>(),
        CopyFullIsoToVolume = true,
        IsoVolumeFileName = "debian.iso",
        // First boot reads selected folders and browser profiles from the Windows
        // volume (ntfs-3g, read-only), as the alpha did; the seed carries no user data.
        MigrationDataOnSeed = false,
        KernelUrl = new Uri("https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/vmlinuz"),
        InitrdUrl = new Uri("https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/gtk/initrd.gz"),
        KernelSha256 = "2b2358b37674d2505350528875bb17afae2a36522a9e8a9417eaca65a7da0e08",
        InitrdSha256 = "5a9afef23f35af5ca68b6898f4d877a6ad434053bd2f738a4e58093736323838",
        ConfigDelivery = ConfigDelivery.InjectIntoInitrd,
        InitrdConfigPath = "preseed.cfg",
        InitrdExtraPaths =
        [
            "igloo-initrd/igloo-bound-free-setup.sh",
            "igloo-initrd/igloo-bound-free-choices.sh",
            "igloo-initrd/igloo-bound-free-do-option.sh",
            "igloo-initrd/igloo-verify-seed.py",
        ],
    };

    private static string RequiredPreseedValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            value.Contains('\\', StringComparison.Ordinal))
            throw new InvalidDataException($"Debian installer {name} must be a nonempty value without line continuations.");
        return value;
    }

    private static async Task<byte[]> ReadRequiredPayloadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required Debian first-boot payload is missing.", path);
        var contents = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (contents.Length == 0)
            throw new InvalidDataException($"Required Debian first-boot payload is empty: {path}");
        return contents;
    }

    private static byte[] NormalizeLineEndings(byte[] contents)
    {
        if (Array.IndexOf(contents, (byte)'\r') < 0)
            return contents;
        using var output = new MemoryStream(contents.Length);
        for (var index = 0; index < contents.Length; index++)
        {
            if (contents[index] == (byte)'\r')
            {
                output.WriteByte((byte)'\n');
                if (index + 1 < contents.Length && contents[index + 1] == (byte)'\n')
                    index++;
            }
            else
                output.WriteByte(contents[index]);
        }
        return output.ToArray();
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
