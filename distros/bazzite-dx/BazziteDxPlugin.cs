using System.Globalization;
using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;

namespace Igloo.Distro.BazziteDx;

/// <summary>
/// Bazzite DX, installed unattended from the Fedora Everything netinstall.
/// </summary>
/// <remarks>
/// Bazzite ships no installer that reads a kickstart: its ISO is a live session that starts
/// Anaconda interactively. The Fedora netinstall boots the same Anaconda through the OEMDRV
/// pipeline Fedora KDE already uses, and the kickstart's <c>ostreecontainer</c> command
/// deploys the Bazzite DX image straight from the registry instead of installing packages.
/// </remarks>
public sealed class BazziteDxPlugin : IDistroPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // Used when distro.json is missing or does not name the images.
    internal const string DefaultImage = "ghcr.io/ublue-os/bazzite-dx:stable";
    internal const string DefaultNvidiaImage = "ghcr.io/ublue-os/bazzite-dx-nvidia:stable";

    // bazzite-hardware-setup renames the host to "bazzite" at first boot when
    // `hostname | wc -m` exceeds 20 (distrobox breaks on long names). wc -m counts the
    // newline, so 19 characters is the longest name that survives.
    internal const int MaxHostnameLength = 19;

    public string Id => "bazzite-dx";

    public DistroMetadata Metadata { get; }

    private readonly string _image;
    private readonly string _nvidiaImage;

    public BazziteDxPlugin()
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var manifestPath = Path.Join(asmDir, "distro.json");

        DistroManifest? raw = null;
        ImageRefs? images = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                raw = JsonSerializer.Deserialize<DistroManifest>(json, JsonOpts);
                images = JsonSerializer.Deserialize<ImageRefs>(json, JsonOpts);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Fall through to defaults.
            }
        }

        Metadata = raw is not null ? BuildMetadata(raw) : FallbackMetadata();
        _image = ValidImageOr(images?.ContainerImage, DefaultImage);
        _nvidiaImage = ValidImageOr(images?.ContainerImageNvidia, DefaultNvidiaImage);
    }

    //   IDistroPlugin

    public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var findings = new List<PreflightFinding>();

        // The image's bootloader is EFI only (bootupd); Bazzite has no BIOS install path.
        if (!report.IsUefi)
        {
            findings.Add(new PreflightFinding(
                Severity: FindingSeverity.Blocker,
                Code: "BAZZITE_REQUIRES_UEFI",
                Message: "Bazzite DX only installs on machines that boot in UEFI mode. This machine " +
                         "boots in legacy BIOS mode.",
                Remediation: "Switch the firmware to UEFI mode (this also requires a GPT disk), or " +
                             "pick a distribution that supports legacy BIOS."));
        }

        if (report.SecureBootEnabled)
        {
            findings.Add(new PreflightFinding(
                Severity: FindingSeverity.Warning,
                Code: "BAZZITE_SECURE_BOOT_KEY",
                Message: "Secure Boot is on. Bazzite's kernel is signed with the Universal Blue key, " +
                         "which the firmware does not trust yet. At the first reboot a blue \"MOK " +
                         "management\" screen appears: choose \"Enroll MOK\", then \"Continue\", then " +
                         "\"Yes\", and type the password universalblue.",
                Remediation: "Follow the MOK screen once, or turn Secure Boot off in the firmware " +
                             "settings before installing."));
        }

        if (IsNvidia(report.GpuVendor))
        {
            findings.Add(new PreflightFinding(
                Severity: FindingSeverity.Info,
                Code: "BAZZITE_NVIDIA_IMAGE",
                Message: "Your machine has an NVIDIA GPU, so iGloo installs the Bazzite DX NVIDIA " +
                         "image, which already contains NVIDIA's open kernel driver. That driver " +
                         "supports GeForce GTX 16-series, RTX 20-series and newer cards.",
                Remediation: null));
        }

        if (report.BitLocker == BitLockerState.EncryptedAndLocked)
        {
            findings.Add(new PreflightFinding(
                Severity: FindingSeverity.Blocker,
                Code: "BITLOCKER_LOCKED",
                Message: "BitLocker is enabled and the volume is currently locked. " +
                         "Igloo cannot resize a locked encrypted volume.",
                Remediation: "Unlock the drive in Windows, or suspend BitLocker protection before " +
                             "re-running Igloo."));
        }

        if (report.TotalRamBytes < Metadata.MinimumRequirements.MinRamBytes)
        {
            findings.Add(new PreflightFinding(
                Severity: FindingSeverity.Warning,
                Code: "RAM_BELOW_RECOMMENDED",
                Message: $"This machine has {report.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GiB of RAM. " +
                         $"Bazzite DX recommends at least " +
                         $"{Metadata.MinimumRequirements.MinRamBytes / (1024.0 * 1024 * 1024):F0} GiB.",
                Remediation: "Installation will proceed, but containers and games may run short of memory."));
        }

        return findings;
    }

    public async Task<InstallerConfig> RenderInstallerConfigAsync(
        MigrationManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var templatePath = Path.Join(asmDir, "kickstart", "ks.cfg.template");

        // Same rule as Fedora KDE: the disk layout lives in the template, so a missing
        // template aborts rather than guessing a layout.
        if (!File.Exists(templatePath))
            throw new FileNotFoundException(
                "Bazzite DX kickstart template not found next to the plugin; refusing to build an " +
                "installer configuration because the disk layout is defined there.", templatePath);

        var ks = RenderFromTemplate(await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false), manifest);

        // bash runs %pre / %post and a stray CR is fatal there (see FedoraKdePlugin).
        ks = ks.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

        return new InstallerConfig(
            FileName: "ks.cfg",
            Contents: Encoding.UTF8.GetBytes(ks),
            Extras: Array.Empty<InstallerConfigExtra>());
    }

    public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;

        var files = new List<AgentFile>();

        TryAddFile("agent/first-boot.sh", "first-boot.sh", executable: true);
        TryAddFile("agent/agent.py", "agent.py", executable: true);
        // The linked files exist under agent/ in the build output only. The app loads the
        // plugin DLL from distros/bazzite-dx/, so the source tree is the fallback.
        if (!TryAddFile("agent/fedora_agent.py", "fedora_agent.py", executable: false))
            TryAddFile("../fedora-kde/agent/agent.py", "fedora_agent.py", executable: false);
        if (!TryAddFile("agent/display-apply.py", "display-apply.py", executable: true))
            TryAddFile("../fedora-kde/agent/display-apply.py", "display-apply.py", executable: true);
        if (!TryAddFile("agent/igloo_boot.py", "igloo_boot.py", executable: false))
            TryAddFile("../_shared/agent/igloo_boot.py", "igloo_boot.py", executable: false);

        // No GRUB theme archives: Bazzite's grub.cfg is owned by the image, not grub-mkconfig.

        return Task.FromResult(new AgentPayload(files));

        bool TryAddFile(string relative, string targetName, bool executable)
        {
            var path = Path.Join(asmDir, relative);
            if (!File.Exists(path))
                return false;

            files.Add(new AgentFile(
                RelativePath: targetName,
                Contents: NormalizeCrLf(File.ReadAllBytes(path)),
                Executable: executable));
            return true;
        }
    }

    public InstallerBootSpec GetInstallerBootSpec() => new()
    {
        MenuTitle = "Install Bazzite DX (Igloo)",
        // Same boot as Fedora KDE: the netinstall kernel, a local stage-2 on OEMDRV and the
        // kickstart from the same volume. No nouveau blacklist: the NVIDIA image sets its
        // own kernel arguments.
        KernelCmdline = "inst.stage2=hd:LABEL={LABEL}: inst.ks=hd:LABEL={LABEL}:/ks.cfg inst.geoloc=0",
        KernelIsoPaths =
        [
            "boot/x86_64/loader/linux",   // Fedora 44+
            "images/pxeboot/vmlinuz",     // Fedora 40–43
            "isolinux/vmlinuz",           // Fedora ≤39
        ],
        InitrdIsoPaths =
        [
            "boot/x86_64/loader/initrd",
            "images/pxeboot/initrd.img",
            "isolinux/initrd.img",
        ],
        ExtraIsoFiles =
        [
            new IsoFileStage("images/install.img", "images/install.img", Required: false),
        ],
    };

    //   Helpers

    /// <summary>The image this machine gets: the NVIDIA build when the GPU needs it.</summary>
    internal string ImageFor(string? gpuVendor) => IsNvidia(gpuVendor) ? _nvidiaImage : _image;

    private static bool IsNvidia(string? gpuVendor) =>
        string.Equals(gpuVendor, "nvidia", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The configured image reference when it is safe to paste into the kickstart, otherwise
    /// <paramref name="fallback"/>.
    /// </summary>
    /// <remarks>
    /// The reference lands unquoted on an Anaconda command line and inside a %post shell
    /// command, so it is limited to the characters an OCI reference can contain.
    /// </remarks>
    internal static string ValidImageOr(string? image, string fallback)
    {
        // A leading "-" would read as an option to bootc in %post.
        if (string.IsNullOrWhiteSpace(image) || image[0] == '-')
            return fallback;
        foreach (var c in image)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or ':' or '@'))
                return fallback;
        }
        return image;
    }

    /// <summary>The hostname capped to what Bazzite keeps (see <see cref="MaxHostnameLength"/>).</summary>
    internal static string BazziteHostname(string hostname)
    {
        if (hostname.Length <= MaxHostnameLength)
            return hostname;
        var capped = hostname[..MaxHostnameLength].TrimEnd('-');
        return capped.Length == 0 ? "bazzite" : capped;
    }

    private string RenderFromTemplate(string template, MigrationManifest m)
    {
        var folderList = string.Join(" ", m.Files.IncludedFolders);

        // One "DestName|source/relative/path" per line for the %post copy loop.
        var folderMap = m.Files.Folders.Count > 0
            ? string.Join("\n", m.Files.Folders.Select(f => $"{f.Name}|{f.SourceRelativePath}"))
            : string.Join("\n", m.Files.IncludedFolders.Select(n => $"{n}|{n}"));

        // Gecko profiles: "source/relative|dest/relative".
        var browserMap = string.Join("\n",
            m.Browsers
                .Where(b => !string.IsNullOrEmpty(b.SourceRelativePath)
                         && !string.IsNullOrEmpty(b.DestRelativePath))
                .Select(b => $"{b.SourceRelativePath}|{b.DestRelativePath}"));

        // Chromium profiles: "Browser Name|source/relative"; the agent places them.
        var chromiumMap = string.Join("\n",
            m.Browsers
                .Where(b => string.Equals(b.Engine, "chromium", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrEmpty(b.SourceRelativePath))
                .Select(b => $"{b.Name}|{b.SourceRelativePath}"));

        // A $6$ crypt hash; an empty one locks the account rather than leaving it passwordless.
        var password = m.User.LinuxPasswordCrypted;
        string passwordOption;
        if (string.IsNullOrEmpty(password))
        {
            passwordOption = "--lock";
        }
        else
        {
            var escaped = password
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            passwordOption = $"--password=\"{escaped}\" --iscrypted";
        }

        // "SSID<TAB>PSK" per saved network, for the %pre auto-connect loop. The netinstall
        // needs the network for the image pull, which is several gigabytes.
        var wifiList = string.Join("\n",
            m.WifiNetworks
                .Where(w => (w.Security == "wpa-psk" && !string.IsNullOrEmpty(w.Psk))
                            || w.Security == "open")
                .Select(w => $"{w.Ssid}\t{(w.Security == "open" ? "" : w.Psk)}"));

        var hostname = BazziteHostname(
            LinuxHostname.FromMachine(m.User.WindowsComputerName, m.User.PreferredLinuxUsername));

        return template
            .Replace("{{CONTAINER_IMAGE}}", ImageFor(m.Hardware.GpuVendor), StringComparison.Ordinal)
            .Replace("{{SECURE_BOOT}}", m.Hardware.SecureBootEnabled ? "1" : "0", StringComparison.Ordinal)
            .Replace("{{LOCALE}}", m.User.Locale, StringComparison.Ordinal)
            .Replace("{{XLAYOUT}}", m.User.Keymap, StringComparison.Ordinal)
            .Replace("{{TIMEZONE}}", m.User.Timezone, StringComparison.Ordinal)
            .Replace("{{HOSTNAME}}", hostname, StringComparison.Ordinal)
            .Replace("{{WINDOWS_USERNAME}}", ShellDoubleQuote(m.User.WindowsUsername), StringComparison.Ordinal)
            .Replace("{{LINUX_USERNAME}}", m.User.PreferredLinuxUsername, StringComparison.Ordinal)
            .Replace("{{FULL_NAME}}", KickstartQuote(m.User.FullName ?? m.User.PreferredLinuxUsername), StringComparison.Ordinal)
            .Replace("{{PASSWORD_OPTION}}", passwordOption, StringComparison.Ordinal)
            .Replace("{{TARGET_DISK_BYTES}}", m.Hardware.TargetDiskBytes > 0
                                                ? m.Hardware.TargetDiskBytes.ToString(CultureInfo.InvariantCulture)
                                                : "0", StringComparison.Ordinal)
            .Replace("{{TARGET_DISK_MODEL}}", ShellDoubleQuote(m.Hardware.TargetDiskModel ?? ""), StringComparison.Ordinal)
            .Replace("{{INSTALL_MODE}}", m.Hardware.InstallMode, StringComparison.Ordinal)
            .Replace("{{INCLUDED_FOLDERS}}", folderList, StringComparison.Ordinal)
            .Replace("{{FOLDER_MAP}}", folderMap, StringComparison.Ordinal)
            .Replace("{{BROWSER_MAP}}", browserMap, StringComparison.Ordinal)
            .Replace("{{CHROMIUM_MAP}}", chromiumMap, StringComparison.Ordinal)
            .Replace("{{WIFI_LIST}}", wifiList, StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="value"/> made inert inside a bash double-quoted string. Windows user
    /// names may contain <c>$</c> and backticks, and %post runs as root.
    /// </summary>
    internal static string ShellDoubleQuote(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal)
             .Replace("$", "\\$", StringComparison.Ordinal)
             .Replace("`", "\\`", StringComparison.Ordinal);

    /// <summary><paramref name="value"/> safe inside a double-quoted kickstart option (shlex rules).</summary>
    internal static string KickstartQuote(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

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
            {
                buf.WriteByte(bytes[i]);
            }
        }
        return buf.ToArray();
    }

    private static DistroMetadata BuildMetadata(DistroManifest raw)
    {
        var req = raw.MinimumRequirements;
        return new DistroMetadata
        {
            DisplayName = raw.DisplayName,
            Description = raw.Description,
            DefaultDesktopEnvironment = raw.DefaultDesktopEnvironment ?? "KDE Plasma",
            InstallerType = InstallerType.Anaconda,
            IsoDownloadUrl = raw.Iso.DownloadUrl,
            IsoSha256 = raw.Iso.Sha256,
            IsoGpgSignatureUrl = raw.Iso.GpgSignatureUrl,
            IsoGpgKeyUrl = raw.Iso.GpgKeyUrl,
            Tags = raw.Tags,
            Screenshots = raw.Screenshots,
            MinimumRequirements = req is not null
                ? new HardwareRequirements
                {
                    MinRamBytes = req.MinRamBytes,
                    MinDiskBytes = req.MinDiskBytes,
                    RequiresUefi = req.RequiresUefi,
                    Requires64Bit = req.Requires64Bit,
                }
                : new HardwareRequirements(),
        };
    }

    private static DistroMetadata FallbackMetadata() => new()
    {
        DisplayName = "Bazzite DX",
        Description = "Bazzite with the KDE Plasma desktop and developer tooling.",
        DefaultDesktopEnvironment = "KDE Plasma",
        InstallerType = InstallerType.Anaconda,
        IsoDownloadUrl = new Uri("https://download.fedoraproject.org/pub/fedora/linux/releases/"),
        IsoSha256 = string.Empty,
        MinimumRequirements = new HardwareRequirements
        {
            MinRamBytes = 8L * 1024 * 1024 * 1024,
            MinDiskBytes = 64L * 1024 * 1024 * 1024,
            RequiresUefi = true,
        },
    };

    /// <summary>The Bazzite-only fields of distro.json.</summary>
    private sealed record ImageRefs
    {
        [System.Text.Json.Serialization.JsonPropertyName("containerImage")]
        public string? ContainerImage { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("containerImageNvidia")]
        public string? ContainerImageNvidia { get; init; }
    }
}
