using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Preparation;
using Igloo.Core.Services;

namespace Igloo.Distro.FedoraKde;

public sealed class FedoraKdePlugin : IDistroPlugin, IReviewedInstallerStorageProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string Id => "fedora-kde";

    public DistroMetadata Metadata { get; }

    private readonly Uri? _stage2Url;

    private static string ContentDirectory()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(FedoraKdePlugin).Assembly.Location)
            ?? AppContext.BaseDirectory;
        // The dev registry loads the DLL from distros/fedora-kde; a build that
        // references multiple providers keeps each provider's assets below its
        // own name so neither metadata nor agent.py can be overwritten.
        return string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(assemblyDirectory)),
            "fedora-kde", StringComparison.OrdinalIgnoreCase)
            ? assemblyDirectory
            : Path.Join(assemblyDirectory, "fedora-kde");
    }

    public FedoraKdePlugin()
    {
        var manifestPath = Path.Join(ContentDirectory(), "distro.json");

        DistroManifest? raw = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                raw = JsonSerializer.Deserialize<DistroManifest>(
                    File.ReadAllText(manifestPath), JsonOpts);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Fall through to defaults.
            }
        }

        Metadata = raw is not null ? BuildMetadata(raw) : FallbackMetadata();
        _stage2Url = raw?.Iso?.Stage2Url;
    }

    //   IDistroPlugin                             
    public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var findings = new List<PreflightFinding>();

        // Fedora doesn't ship proprietary NVIDIA drivers in-tree.
        if (string.Equals(report.GpuVendor, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            if (report.SecureBootEnabled)
            {
                // A caveat, not a stop: the alpha installed on NVIDIA machines and the
                // first-boot agent builds the module; with Secure Boot on, the signed
                // nouveau/nova fallback keeps the desktop usable until the key is enrolled.
                findings.Add(new PreflightFinding(
                    Severity: FindingSeverity.Warning,
                    Code: "FEDORA_NVIDIA_SECURE_BOOT_UNSUPPORTED",
                    Message: "Secure Boot is enabled. The proprietary NVIDIA module built on first boot cannot load until its signing key is enrolled; Fedora falls back to the open driver until then.",
                    Remediation: "Enroll the module key when Fedora asks on the next boot, or disable Secure Boot in firmware."));
            }
            findings.Add(new PreflightFinding(
                Severity: FindingSeverity.Info,
                Code: "FEDORA_NVIDIA_RPMFUSION",
                Message: "Your machine has an NVIDIA GPU. Igloo's first-boot agent will install " +
                             "the proprietary drivers from RPM Fusion on first boot. An internet " +
                             "connection is required at first boot.",
                Remediation: null));
        }

        // BitLocker locked is a hard blocker.
        if (report.BitLocker == BitLockerState.EncryptedAndLocked)
        {
            findings.Add(new PreflightFinding(
                Severity: FindingSeverity.Blocker,
                Code: "BITLOCKER_LOCKED",
                Message: "BitLocker is enabled and the volume is currently locked. " +
                             "Igloo cannot prepare an encrypted installation target.",
                Remediation: "Unlock the drive and fully decrypt it in Windows before " +
                             "re-running Igloo."));
        }

        // RAM check.
        if (report.TotalRamBytes < Metadata.MinimumRequirements.MinRamBytes)
        {
            findings.Add(new PreflightFinding(
                Severity: FindingSeverity.Warning,
                Code: "RAM_BELOW_RECOMMENDED",
                Message: $"This machine has {report.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GiB of RAM. " +
                             $"Fedora KDE recommends at least " +
                             $"{Metadata.MinimumRequirements.MinRamBytes / (1024.0 * 1024 * 1024):F0} GiB.",
                Remediation: "Add enough memory to meet Fedora KDE's recommendation before installation."));
        }

        return findings;
    }

    public async Task<InstallerConfig> RenderInstallerConfigAsync(
        MigrationManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.WifiNetworks is null || manifest.WifiNetworks.Any(network => network is null))
            throw new InvalidDataException("Fedora installer Wi-Fi selection is invalid.");
        if (!string.Equals(manifest.DistroId, Id, StringComparison.Ordinal) ||
            manifest.Hardware.TargetDiskBytes <= 0 ||
            manifest.Hardware.TargetDiskGuid is not { } targetGuid || targetGuid == Guid.Empty ||
            manifest.Hardware.InstallMode is not ("replace" or "dual-boot"))
            throw new InvalidDataException(
                "Fedora installer configuration requires this distribution, the selected GPT disk identity and size, and a supported install mode.");
        // v0.2-alpha behaviour: dual boot is the Community default, the first-boot
        // agent handles NVIDIA (CheckCompatibility reports the Secure Boot caveat as
        // a finding), and the primary Wi-Fi network is handed to %pre so a netinstall
        // can reach a mirror; the agent redacts the seed afterwards.

        var templatePath = Path.Join(ContentDirectory(), "kickstart", "ks.cfg.template");

        // A missing template must abort, never silently fall back to a hardcoded layout: the disk
        // partitioning lives in the template, and guessing it risks wiping the whole disk. The
        // template ships next to the plugin (build-copied), so absence means a packaging fault.
        if (!File.Exists(templatePath))
            throw new FileNotFoundException(
                "Fedora kickstart template not found next to the plugin; refusing to build an installer " +
                "configuration because the disk layout is defined there.", templatePath);

        var ks = RenderFromTemplate(await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false), manifest);

        // Force Unix (LF) line endings. The kickstart is executed by bash (%pre /
        // %post) and parsed by Anaconda on the target. A Windows checkout or edit
        // leaves CRLF in the template, and a stray CR is fatal: e.g. the %pre line
        // "} > /tmp/ks-storage.cfg\r" makes bash create a file literally named
        // "ks-storage.cfg<CR>", while Anaconda's "%include /tmp/ks-storage.cfg"
        // strips the CR and looks for "ks-storage.cfg" → "Unable to open input
        // kickstart file: No such file or directory". Normalising here guarantees a
        // valid file no matter how the template was authored or checked out.
        ks = ks.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

        var config = new InstallerConfig(
            FileName: "ks.cfg",
            Contents: Encoding.UTF8.GetBytes(ks),
            Extras: Array.Empty<InstallerConfigExtra>());

        return config;
    }

    public Observation<string> RenderReviewedStorage(InstallationOwnershipV1 ownership,
        Guid expectedGeneration, Observation<InstallerRuntimeInventoryV1> inventory)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var reviewed = PreparedLayoutRules.VerifyReviewedIntent(ownership.Layout.Plan);
        return reviewed.Availability == ObservationAvailability.Available
            ? FedoraOwnedStorageRecipe.Generate(ownership, expectedGeneration, inventory)
            : Observations.Failure<string>(reviewed.Availability, reviewed.Code!);
    }

    // This opt-in renderer is not called by the private alpha installation route. It refuses
    // saved Wi-Fi entirely: neither the kickstart nor the internal manifest handed to first boot
    // may carry an unattended PSK on a reviewed path without cross-OS secret custody.
    public async Task<Observation<InstallerConfig>> RenderReviewedInstallerConfigAsync(MigrationManifest manifest,
        InstallationOwnershipV1 ownership, Guid expectedGeneration,
        Observation<InstallerRuntimeInventoryV1> inventory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(inventory);
        var supported = ValidateReviewedManifest(manifest, ownership);
        if (supported.Availability != ObservationAvailability.Available)
            return Observations.Failure<InstallerConfig>(supported.Availability, supported.Code!);
        var recipe = RenderReviewedStorage(ownership, expectedGeneration, inventory);
        if (recipe.Availability != ObservationAvailability.Available)
            return Observations.Failure<InstallerConfig>(recipe.Availability, recipe.Code!);
        var templatePath = Path.Join(ContentDirectory(), "kickstart", "ks.cfg.template");
        if (!File.Exists(templatePath))
            return Observations.Failure<InstallerConfig>(ObservationAvailability.Unavailable, "FedoraTemplateUnavailable");
        var reviewed = FedoraOwnedStorageRecipe.ReplaceLegacyStorageSection(
            await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false), ownership,
            expectedGeneration, inventory);
        return RenderReviewedConfig(reviewed, manifest);
    }

    public async Task<Observation<InstallerConfig>> RenderDeferredReviewedInstallerConfigAsync(
        MigrationManifest manifest, InstallationOwnershipV1 ownership,
        Guid expectedGeneration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(ownership);
        var supported = ValidateReviewedManifest(manifest, ownership);
        if (supported.Availability != ObservationAvailability.Available)
            return Observations.Failure<InstallerConfig>(supported.Availability, supported.Code!);
        var templatePath = Path.Join(ContentDirectory(), "kickstart", "ks.cfg.template");
        if (!File.Exists(templatePath))
            return Observations.Failure<InstallerConfig>(ObservationAvailability.Unavailable, "FedoraTemplateUnavailable");
        var reviewed = FedoraOwnedStorageRecipe.ReplaceLegacyStorageSectionDeferred(
            await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false), ownership,
            expectedGeneration);
        return RenderReviewedConfig(reviewed, manifest);
    }

    private Observation<bool> ValidateReviewedManifest(MigrationManifest manifest,
        InstallationOwnershipV1 ownership)
    {
        if (manifest.WifiNetworks is null || manifest.WifiNetworks.Any(network => network is null))
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous,
                "FedoraWifiSelectionInvalid");
        if (manifest.DistroId != Id || manifest.Hardware.InstallMode != "dual-boot")
            return Observations.Failure<bool>(ObservationAvailability.Unsupported, "FedoraReviewedModeUnsupported");
        if (manifest.Hardware.SecureBootEnabled &&
            string.Equals(manifest.Hardware.GpuVendor, "nvidia", StringComparison.OrdinalIgnoreCase))
            return Observations.Failure<bool>(ObservationAvailability.Unsupported,
                "FedoraNvidiaSecureBootUnsupported");
        if (manifest.Hardware.TargetDiskGuid != ownership.Layout.Plan.TargetDisk.GptDiskGuid ||
            manifest.Hardware.TargetDiskBytes <= 0 ||
            (ulong)manifest.Hardware.TargetDiskBytes != ownership.Layout.Plan.TargetDisk.SizeBytes)
            return Observations.Failure<bool>(ObservationAvailability.Ambiguous,
                "FedoraReviewedTargetManifestChanged");
        if (manifest.WifiNetworks.Count != 0)
            return Observations.Failure<bool>(ObservationAvailability.Unsupported,
                "SecureNetworkCredentialHandoffUnavailable");
        return Observations.Available(true);
    }

    private Observation<InstallerConfig> RenderReviewedConfig(Observation<string> reviewed,
        MigrationManifest manifest)
    {
        if (reviewed.Availability != ObservationAvailability.Available)
            return Observations.Failure<InstallerConfig>(reviewed.Availability, reviewed.Code!);
        var rendered = RenderFromTemplate(reviewed.Value, manifest)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        return Observations.Available(new InstallerConfig("ks.cfg", Encoding.UTF8.GetBytes(rendered),
            Array.Empty<InstallerConfigExtra>()));
    }

    public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        var asmDir = ContentDirectory();

        var files = new List<AgentFile>();

        // An install without these files would appear successful but never
        // perform first-boot setup or apply the migrated display layout.
        RequireFile("agent/first-boot.sh", executable: true);
        RequireFile("agent/agent.py", executable: true);
        RequireFile("agent/display-apply.py", executable: true);
        // Shared across families: "agent/" in the build output, "../_shared/agent/"
        // when the plugin DLL is loaded from its source folder.
        if (!TryAddFile("agent/igloo_boot.py", executable: false) &&
            !TryAddFile("../_shared/agent/igloo_boot.py", executable: false))
            throw new FileNotFoundException("Required Fedora boot helper is missing: igloo_boot.py");

        // GRUB theme archives (M15). Binary: must bypass NormalizeCrLf, which
        // rewrites 0x0D bytes and would corrupt the gzip stream.
        // Moved to _shared/grub-theme/; the build output keeps them under agent/.
        foreach (var n in new[] { "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz" })
        {
            bool fromBuildOutput = TryAddRaw($"agent/{n}");
            if (!fromBuildOutput)
                TryAddRaw($"../_shared/grub-theme/{n}");
        }

        return Task.FromResult(new AgentPayload(files));

        void RequireFile(string relative, bool executable)
        {
            if (!TryAddFile(relative, executable))
                throw new FileNotFoundException(
                    $"Required Fedora first-boot payload is missing: {relative}",
                    Path.Join(asmDir, relative));
        }

        bool TryAddRaw(string relative)
        {
            var path = Path.Join(asmDir, relative);
            if (!File.Exists(path))
                return false;
            files.Add(new AgentFile(Path.GetFileName(path), File.ReadAllBytes(path), false));
            return true;
        }

        bool TryAddFile(string relative, bool executable)
        {
            var path = Path.Join(asmDir, relative);
            if (!File.Exists(path))
                return false;

            // Normalize CRLF → LF so shell scripts and Python files execute
            // correctly on Linux.  Windows git checkouts (text=auto) may add
            // \r\n even for files now covered by *.sh / *.py eol=lf rules in
            // .gitattributes - normalising here is belt-and-suspenders.
            var contents = NormalizeCrLf(File.ReadAllBytes(path));

            files.Add(new AgentFile(
                RelativePath: Path.GetFileName(path),
                Contents: contents,
                Executable: executable));
            return true;
        }
    }

    public InstallerBootSpec GetInstallerBootSpec() => new()
    {
        MenuTitle = "Install Fedora KDE (Igloo)",
        // Anaconda reads ks.cfg and its local stage-2 (install.img) from the
        // exact Windows-observed OEMDRV GPT partition. A label lookup could
        // choose an unrelated volume with the same label.
        // Do NOT blacklist nouveau on the INSTALLER cmdline or at install
        // time on the installed system.
        //
        // The first boot of the installed system runs before the NVIDIA
        // driver exists; a premature blacklist drops that boot onto the
        // plain EFI framebuffer (black screen on a 4K panel) whenever the
        // driver is not ready yet (no internet, akmod build error, Secure
        // Boot rejecting the unsigned module).
        //
        // The blacklist is still REQUIRED later: RPM Fusion's modprobe.d
        // nouveau blacklist does not apply inside the initramfs (dracut
        // builds it host-only), so without rd.driver.blacklist= on the
        // cmdline nouveau/nova_core keeps loading and fights nvidia for the
        // GPU - the RTX 5070 bare-metal kernel panic. The first-boot agent
        // (agent.py, ensure_nvidia_kernel_cmdline) adds the cmdline args via
        // grubby once the nvidia module is verified built for every kernel.
        KernelCmdline = "inst.stage2=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}: inst.ks=hd:/dev/disk/by-partuuid/{SEED_PARTUUID}:/ks.cfg inst.geoloc=0",
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
            // Anaconda stage-2 squashfs copied to the bound OEMDRV partition.
            new IsoFileStage("images/install.img", "images/install.img", Required: true),
        ],
    };

    //   Private helpers                            

    private static byte[] NormalizeCrLf(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)'\r') < 0)
            return bytes;

        using var buf = new MemoryStream(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r')
            {
                // Emit a single LF and skip the following LF if CRLF pair.
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

    private string RenderFromTemplate(string template, MigrationManifest m)
    {
        ValidateSelectedPackages(m.SuggestedPackages);

        // These values appear in kickstart directives as well as comments and
        // shell assignments. Reject line breaks and directive delimiters before
        // substitution, so a display name or locale cannot add a new command.
        var linuxUsername = SafeInstallerToken(m.User.PreferredLinuxUsername,
            @"\A[a-z_][a-z0-9_.-]{0,31}\z", "Linux username");
        var fullName = SafeInstallerToken(
            string.IsNullOrWhiteSpace(m.User.FullName) ? linuxUsername : m.User.FullName,
            @"\A[\p{L}\p{M}\p{N}\p{Pd} .,'’()]{1,128}\z", "display name");
        var locale = SafeInstallerToken(m.User.Locale,
            @"\A[A-Za-z0-9_.@-]{1,64}\z", "locale");
        var keymap = SafeInstallerToken(m.User.Keymap,
            @"\A[A-Za-z0-9_+.,()\-]{1,64}\z", "keyboard layout");
        var timezone = SafeInstallerToken(m.User.Timezone,
            @"\A[A-Za-z0-9_+\-]+(?:/[A-Za-z0-9_+\-]+)*\z", "time zone");

        // Included folder names as a space-separated list (informational / logging).
        var selectedFolders = m.Files.Folders.Count > 0
            ? m.Files.Folders.Select(folder => SafeStagedFolderName(folder.Name)).ToArray()
            : m.Files.IncludedFolders.Select(SafeStagedFolderName).ToArray();
        var folderList = string.Join(" ", selectedFolders);
        var stagedFolderNames = string.Join("\n", selectedFolders);

        // Selected profiles must have been copied to an exact OEMDRV-relative
        // staging path by Core. A username alone cannot prove an NTFS source.
        foreach (var browser in m.Browsers)
        {
            if (string.IsNullOrEmpty(browser.SourceRelativePath) ||
                string.IsNullOrEmpty(browser.ProfileStagingPath))
                throw new InvalidDataException("A selected Fedora browser profile has no staged source.");
            if (!string.Equals(browser.Engine, "gecko", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(browser.Engine, "chromium", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected Fedora browser engine is unsupported.");
            if (string.Equals(browser.Engine, "gecko", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(browser.DestRelativePath))
                throw new InvalidDataException("A selected Gecko profile has no Linux destination.");
        }

        var geckoBrowsers = m.Browsers
            .Where(b => string.Equals(b.Engine, "gecko", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(b.SourceRelativePath)
                     && !string.IsNullOrEmpty(b.DestRelativePath))
            .ToArray();
        var stagedBrowserMap = string.Join("\n", geckoBrowsers
            .Select(b => $"{SafeStagedBrowserPath(b)}|{SafeBrowserPath(b.DestRelativePath)}"));

        // Chromium map: one "Browser Name|source/relative" entry. The name is the
        // key the agent maps to a Flatpak id and config directory, which is why
        // this half cannot name its own destination the way the Gecko map does.
        var chromiumBrowsers = m.Browsers
            .Where(b => string.Equals(b.Engine, "chromium", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(b.SourceRelativePath))
            .ToArray();
        var stagedChromiumMap = string.Join("\n", chromiumBrowsers
            .Select(b => $"{SafeBrowserName(b.Name)}|{SafeStagedBrowserPath(b)}"));
        var selectedFileFolders = selectedFolders.Length > 0 ? "1" : "0";

        // The manifest carries a crypt hash, never the chosen plaintext.
        // If somehow empty, fall back to a locked account rather than a passwordless one.
        var password = m.User.LinuxPasswordCrypted;
        string passwordOption;
        if (string.IsNullOrEmpty(password))
        {
            passwordOption = "--lock";
        }
        else
        {
            password = SafeInstallerToken(password,
                @"\A[A-Za-z0-9./$=]{1,512}\z", "Linux password hash");
            var escaped = password
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            passwordOption = $"--password=\"{escaped}\" --iscrypted";
        }

        // Open networks can still be discovered in %pre. Saved PSKs are
        // refused before rendering because ks.cfg is copied to a FAT USB seed.
        var primaryWifi =
            m.WifiNetworks.FirstOrDefault(w => w.IsPrimary && w.Security == "wpa-psk" && !string.IsNullOrEmpty(w.Psk))
            ?? m.WifiNetworks.FirstOrDefault(w => w.IsPrimary && w.Security == "open");

        var wifiSsid = primaryWifi is not null ? ShellDoubleQuote(SafeWifiField(primaryWifi.Ssid)) : "";
        var wifiPsk = primaryWifi?.Psk is { Length: > 0 } k ? ShellDoubleQuote(SafeWifiField(k)) : "";

        // Tab-separated "SSID<TAB>PSK" lines for %pre. At this point every
        // selected PSK must be absent, leaving only open networks for automatic
        // association. Unattended netinstall needs Ethernet or an open network.
        var wifiList = string.Join("\n",
            m.WifiNetworks
                .Where(w => (w.Security == "wpa-psk" && !string.IsNullOrEmpty(w.Psk))
                            || w.Security == "open")
                .Select(w => $"{SafeWifiField(w.Ssid)}\t{(w.Security == "open" ? "" : SafeWifiField(w.Psk!))}"));

        // Install-source directive for the netinstall. Without this Anaconda has
        // no package repository and stops with "Error setting up repositories"
        // (the stage-2 installer environment is local on OEMDRV, but the RPMs
        // must come from the network). See BuildInstallSourceLine for details.
        var installSource = BuildInstallSourceLine(_stage2Url);

        return template
            .Replace("{{INSTALL_SOURCE_URL}}", installSource, StringComparison.Ordinal)
            .Replace("{{LOCALE}}", locale, StringComparison.Ordinal)
            .Replace("{{KEYMAP}}", keymap, StringComparison.Ordinal)
            .Replace("{{XLAYOUT}}", keymap, StringComparison.Ordinal)
            .Replace("{{TIMEZONE}}", timezone, StringComparison.Ordinal)
            .Replace("{{HOSTNAME}}", LinuxHostname.FromMachine(m.User.WindowsComputerName, linuxUsername), StringComparison.Ordinal)
            .Replace("{{WINDOWS_USERNAME}}", ShellDoubleQuote(m.User.WindowsUsername), StringComparison.Ordinal)
            .Replace("{{LINUX_USERNAME}}", linuxUsername, StringComparison.Ordinal)
            .Replace("{{FULL_NAME}}", fullName, StringComparison.Ordinal)
            .Replace("{{PASSWORD_OPTION}}", passwordOption, StringComparison.Ordinal)
            .Replace("{{TARGET_DISK_BYTES}}", m.Hardware.TargetDiskBytes > 0
                                                ? m.Hardware.TargetDiskBytes.ToString(CultureInfo.InvariantCulture)
                                                : "0", StringComparison.Ordinal)
            .Replace("{{TARGET_DISK_GUID}}", m.Hardware.TargetDiskGuid?.ToString("D", CultureInfo.InvariantCulture) ?? "",
                StringComparison.Ordinal)
            .Replace("{{TARGET_DISK_MODEL}}", ShellDoubleQuote(m.Hardware.TargetDiskModel ?? ""), StringComparison.Ordinal)
            .Replace("{{INSTALL_MODE}}", m.Hardware.InstallMode, StringComparison.Ordinal)
            .Replace("{{INCLUDED_FOLDERS}}", ShellDoubleQuote(folderList), StringComparison.Ordinal)
            .Replace("{{STAGED_FOLDER_NAMES}}", stagedFolderNames, StringComparison.Ordinal)
            .Replace("{{STAGED_BROWSER_MAP}}", stagedBrowserMap, StringComparison.Ordinal)
            .Replace("{{STAGED_CHROMIUM_MAP}}", stagedChromiumMap, StringComparison.Ordinal)
            .Replace("{{SELECTED_FILE_FOLDERS}}", selectedFileFolders, StringComparison.Ordinal)
            .Replace("{{WIFI_SSID}}", wifiSsid, StringComparison.Ordinal)
            .Replace("{{WIFI_PSK}}", wifiPsk, StringComparison.Ordinal)
            .Replace("{{WIFI_LIST}}", wifiList, StringComparison.Ordinal);
    }

    private static string BuildInstallSourceLine(Uri? stage2Url)
    {
        if (stage2Url is null || !stage2Url.IsAbsoluteUri ||
            !string.Equals(stage2Url.Scheme, Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Fedora requires a configured HTTPS package source for unattended installation.");

        var stage2 = stage2Url.AbsoluteUri;

        // Extract the Fedora release number from a versioned release tree URL,
        // e.g. https://…/releases/44/Everything/x86_64/os/  →  "44".
        var match = Regex.Match(stage2, @"/releases/(\d+)/", RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var release = match.Groups[1].Value;
            return $"url --metalink=\"https://mirrors.fedoraproject.org/metalink?repo=fedora-{release}&arch=x86_64\"";
        }

        // Non-versioned (e.g. rawhide or a mirror pin): use the tree directly.
        return $"url --url=\"{stage2}\"";
    }

    private static string SafeInstallerToken(string? value, string pattern, string field)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
            throw new InvalidDataException($"Fedora {field} is unsafe for kickstart.");
        return value;
    }

    private static void ValidateSelectedPackages(IReadOnlyList<SuggestedPackage> packages)
    {
        // First boot passes these values to a root package manager. Reject a
        // malformed selected package while rendering, before preparing the USB.
        foreach (var package in packages.Where(package => package.AutoInstall))
        {
            var flatpakId = package.FlatpakId;
            var nativePackage = package.NativePackage;
            if (string.IsNullOrEmpty(flatpakId) && string.IsNullOrEmpty(nativePackage))
                throw new InvalidDataException("A selected Fedora application has no installable package identifier.");
            if (!string.IsNullOrEmpty(flatpakId) &&
                (!Regex.IsMatch(flatpakId, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{2,127}\z", RegexOptions.CultureInvariant) ||
                 flatpakId.Count(character => character == '.') < 2 ||
                 flatpakId.Contains("..", StringComparison.Ordinal)))
                throw new InvalidDataException("A selected Fedora Flatpak identifier is unsafe.");
            if (!string.IsNullOrEmpty(nativePackage) &&
                (!Regex.IsMatch(nativePackage, @"\A[A-Za-z0-9][A-Za-z0-9_.+:-]{0,127}\z", RegexOptions.CultureInvariant) ||
                 nativePackage.Contains("..", StringComparison.Ordinal)))
                throw new InvalidDataException("A selected Fedora RPM identifier is unsafe.");
        }
    }

    private static string ShellDoubleQuote(string value) =>
        (value.Any(char.IsControl)
            ? throw new InvalidDataException("A Fedora installer field contains a control character.")
            : value).Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal)
             .Replace("$", "\\$", StringComparison.Ordinal)
             .Replace("`", "\\`", StringComparison.Ordinal);

    private static string SafeStagedBrowserPath(BrowserMigration browser)
    {
        var expected = "browser-profiles/" + SafeBrowserPath(browser.SourceRelativePath);
        if (!string.Equals(browser.ProfileStagingPath, expected, StringComparison.Ordinal))
            throw new InvalidDataException("A Fedora browser staging path does not match its selected source.");
        return expected;
    }

    private static string SafeStagedFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." or "STAGEDFOLDERS" ||
            name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c, StringComparison.Ordinal)))
            throw new InvalidDataException("A selected Fedora folder has an unsafe staging name.");
        return name;
    }

    private static string SafeBrowserPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Contains('|', StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal) ||
            path.Any(char.IsControl) ||
            path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("A Fedora browser path must be a safe relative path.");
        return path;
    }

    private static string SafeBrowserName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '-' or '_')))
            throw new InvalidDataException("A Fedora browser name must be a safe directory name.");
        return name;
    }

    private static string SafeWifiField(string value)
    {
        if (value.Any(char.IsControl))
            throw new InvalidDataException("Fedora Wi-Fi handoff cannot encode control characters in a kickstart line.");
        return value;
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
        DisplayName = "Fedora KDE",
        Description = "Fedora with the KDE Plasma desktop environment.",
        DefaultDesktopEnvironment = "KDE Plasma",
        InstallerType = InstallerType.Anaconda,
        IsoDownloadUrl = new Uri("https://fedoraproject.org"),
        IsoSha256 = string.Empty,
    };
}
