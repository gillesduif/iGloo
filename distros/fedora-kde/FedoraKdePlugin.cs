using System.Text;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Distro.FedoraKde;

/// <summary>
/// Reference implementation of <see cref="IDistroPlugin"/> for Fedora KDE.
///
/// Loaded at runtime by <c>DistroRegistry</c> via <c>AssemblyLoadContext</c>. The plugin uses a
/// parameterless constructor and reads its own <c>distro.json</c> from the assembly's output
/// directory so no injection wiring is needed.
/// </summary>
public sealed class FedoraKdePlugin : IDistroPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
    };

    public string Id => "fedora-kde";

    public DistroMetadata Metadata { get; }

    /// <summary>
    /// Anaconda stage-2 OS tree URL from <c>distro.json</c> (<c>iso.stage2Url</c>).
    /// Used to render the kickstart install-source directive so the netinstall
    /// knows where to fetch packages without the user configuring it interactively.
    /// </summary>
    private readonly string? _stage2Url;

    /// <summary>
    /// Parameterless constructor - reads <c>distro.json</c> from the same directory as this DLL.
    /// </summary>
    public FedoraKdePlugin()
    {
        var asmDir       = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var manifestPath = Path.Combine(asmDir, "distro.json");

        DistroManifest? raw = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                raw = JsonSerializer.Deserialize<DistroManifest>(
                    File.ReadAllText(manifestPath), JsonOpts);
            }
            catch
            {
                // Fall through to defaults.
            }
        }

        Metadata   = raw is not null ? BuildMetadata(raw) : FallbackMetadata();
        _stage2Url = raw?.Iso?.Stage2Url;
    }

    // ── IDistroPlugin ────────────────────────────────────────────────────────

    public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report)
    {
        var findings = new List<PreflightFinding>();

        // Fedora doesn't ship proprietary NVIDIA drivers in-tree.
        if (string.Equals(report.GpuVendor, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PreflightFinding(
                Severity:    FindingSeverity.Info,
                Code:        "FEDORA_NVIDIA_RPMFUSION",
                Message:     "Your machine has an NVIDIA GPU. Igloo's first-boot agent will install " +
                             "the proprietary drivers from RPM Fusion on first boot. An internet " +
                             "connection is required at first boot.",
                Remediation: null));
        }

        // BitLocker locked is a hard blocker.
        if (report.BitLocker == BitLockerState.EncryptedAndLocked)
        {
            findings.Add(new PreflightFinding(
                Severity:    FindingSeverity.Blocker,
                Code:        "BITLOCKER_LOCKED",
                Message:     "BitLocker is enabled and the volume is currently locked. " +
                             "Igloo cannot resize a locked encrypted volume.",
                Remediation: "Unlock the drive in Windows, or suspend BitLocker protection before " +
                             "re-running Igloo."));
        }

        // RAM check.
        if (report.TotalRamBytes < Metadata.MinimumRequirements.MinRamBytes)
        {
            findings.Add(new PreflightFinding(
                Severity:    FindingSeverity.Warning,
                Code:        "RAM_BELOW_RECOMMENDED",
                Message:     $"This machine has {report.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GiB of RAM. " +
                             $"Fedora KDE recommends at least " +
                             $"{Metadata.MinimumRequirements.MinRamBytes / (1024.0 * 1024 * 1024):F0} GiB.",
                Remediation: "Installation will proceed but the desktop may feel sluggish."));
        }

        return findings;
    }

    public Task<InstallerConfig> RenderInstallerConfigAsync(
        MigrationManifest manifest, CancellationToken ct = default)
    {
        var asmDir       = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;
        var templatePath = Path.Combine(asmDir, "kickstart", "ks.cfg.template");

        string ks = File.Exists(templatePath)
            ? RenderFromTemplate(File.ReadAllText(templatePath), manifest)
            : RenderInline(manifest);

        // Force Unix (LF) line endings. The kickstart is executed by bash (%pre /
        // %post) and parsed by Anaconda on the target. A Windows checkout or edit
        // leaves CRLF in the template, and a stray CR is fatal: e.g. the %pre line
        // "} > /tmp/ks-storage.cfg\r" makes bash create a file literally named
        // "ks-storage.cfg<CR>", while Anaconda's "%include /tmp/ks-storage.cfg"
        // strips the CR and looks for "ks-storage.cfg" → "Unable to open input
        // kickstart file: No such file or directory". Normalising here guarantees a
        // valid file no matter how the template was authored or checked out.
        ks = ks.Replace("\r\n", "\n").Replace("\r", "\n");

        var config = new InstallerConfig(
            FileName: "ks.cfg",
            Contents: Encoding.UTF8.GetBytes(ks),
            Extras:   Array.Empty<InstallerConfigExtra>());

        return Task.FromResult(config);
    }

    public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
    {
        var asmDir = Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;

        var files = new List<AgentFile>();

        // Include first-boot.sh and agent.py if they exist on disk.
        TryAddFile("agent/first-boot.sh", executable: true);
        TryAddFile("agent/agent.py",       executable: true);

        // Fallback stub if the scripts aren't bundled.
        if (files.Count == 0)
        {
            files.Add(new AgentFile(
                RelativePath: "first-boot.sh",
                Contents:     Encoding.UTF8.GetBytes(
                    "#!/usr/bin/env bash\n" +
                    "exec python3 /opt/igloo/agent.py --manifest /var/lib/igloo/manifest.json\n"),
                Executable:   true));
        }

        return Task.FromResult(new AgentPayload(files));

        void TryAddFile(string relative, bool executable)
        {
            var path = Path.Combine(asmDir, relative);
            if (!File.Exists(path)) return;

            // Normalize CRLF → LF so shell scripts and Python files execute
            // correctly on Linux.  Windows git checkouts (text=auto) may add
            // \r\n even for files now covered by *.sh / *.py eol=lf rules in
            // .gitattributes - normalising here is belt-and-suspenders.
            var contents = NormalizeCrLf(File.ReadAllBytes(path));

            files.Add(new AgentFile(
                RelativePath: Path.GetFileName(path),
                Contents:     contents,
                Executable:   executable));
        }
    }

    /// <summary>
    /// Replaces every bare CR or CRLF sequence with a single LF.
    /// Returns the original array if no CR bytes are present (fast path).
    /// </summary>
    private static byte[] NormalizeCrLf(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)'\r') < 0)
            return bytes;

        var buf = new System.IO.MemoryStream(bytes.Length);
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

    // ── Private helpers ──────────────────────────────────────────────────────

    private string RenderFromTemplate(string template, MigrationManifest m)
    {
        // Included folder names as a space-separated list (informational / logging).
        var folderList = string.Join(" ", m.Files.IncludedFolders);

        // Folder map for the %post copy loop: one "DestName|source/relative/path"
        // entry per line. The relative path was resolved on the Windows side
        // (Known Folder API), so OneDrive-redirected folders carry their real
        // location (e.g. "OneDrive/Documents") instead of being guessed by name.
        // Fall back to the name list if a manifest predates the Folders field.
        var folderMap = m.Files.Folders.Count > 0
            ? string.Join("\n", m.Files.Folders.Select(f => $"{f.Name}|{f.SourceRelativePath}"))
            : string.Join("\n", m.Files.IncludedFolders.Select(n => $"{n}|{n}"));

        // Browser map for the %post copy loop: one "source/relative|dest/relative"
        // entry per migratable browser profile. Only Gecko browsers (Firefox / Zen
        // / Waterfox) carry non-empty paths - their profile roots are OS-portable
        // and include saved passwords. Chromium browsers were recorded with empty
        // paths (DPAPI-bound passwords, Phase 2) and are skipped here.
        var browserMap = string.Join("\n",
            m.Browsers
                .Where(b => !string.IsNullOrEmpty(b.SourceRelativePath)
                         && !string.IsNullOrEmpty(b.DestRelativePath))
                .Select(b => $"{b.SourceRelativePath}|{b.DestRelativePath}"));

        // Password: use the user's chosen password (--plaintext).
        // If somehow empty, fall back to a locked account so we don't create
        // a passwordless account - the first-boot agent should handle recovery.
        var password       = m.User.LinuxPassword;
        var passwordOption = !string.IsNullOrEmpty(password)
            ? $"--password={password} --plaintext"
            : "--lock";

        // Wi-Fi pre-seed for Anaconda: pick the network Windows was connected to
        // (preferring a WPA-PSK one with a recovered key). The %pre script finds
        // the wireless interface and emits a `network --essid --wpakey` directive
        // into /tmp/ks-network.cfg, which the template %include-s. SSID/PSK are
        // escaped for safe embedding in a bash double-quoted assignment.
        var primaryWifi =
            m.WifiNetworks.FirstOrDefault(w => w.IsPrimary && w.Security == "wpa-psk" && !string.IsNullOrEmpty(w.Psk))
            ?? m.WifiNetworks.FirstOrDefault(w => w.IsPrimary && w.Security == "open");

        var wifiSsid = primaryWifi is not null ? ShellDoubleQuote(primaryWifi.Ssid) : "";
        var wifiPsk  = primaryWifi?.Psk is { Length: > 0 } k ? ShellDoubleQuote(k) : "";

        // Every saved Wi-Fi network (WPA-PSK with a recovered key, or open) as
        // tab-separated "SSID<TAB>PSK" lines for the %pre auto-connect loop. That
        // loop brings the radio up, scans, and connects to the first one that is
        // actually in range - so the netinstall associates automatically instead
        // of Anaconda falling back to a manual Wi-Fi prompt. Tab-delimited so
        // SSIDs/PSKs containing spaces survive; the lines live inside a quoted
        // heredoc in %pre, so no shell escaping is needed here.
        var wifiList = string.Join("\n",
            m.WifiNetworks
                .Where(w => (w.Security == "wpa-psk" && !string.IsNullOrEmpty(w.Psk))
                            || w.Security == "open")
                .Select(w => $"{w.Ssid}\t{(w.Security == "open" ? "" : w.Psk)}"));

        // Install-source directive for the netinstall. Without this Anaconda has
        // no package repository and stops with "Error setting up repositories"
        // (the stage-2 installer environment is local on OEMDRV, but the RPMs
        // must come from the network). See BuildInstallSourceLine for details.
        var installSource = BuildInstallSourceLine(_stage2Url);

        return template
            .Replace("{{INSTALL_SOURCE_URL}}", installSource)
            .Replace("{{LOCALE}}",            m.User.Locale)
            .Replace("{{KEYMAP}}",            m.User.Keymap)
            .Replace("{{XLAYOUT}}",           m.User.Keymap)
            .Replace("{{TIMEZONE}}",          m.User.Timezone)
            .Replace("{{HOSTNAME}}",          m.User.PreferredLinuxUsername + "-pc")
            .Replace("{{WINDOWS_USERNAME}}",  m.User.WindowsUsername)
            .Replace("{{LINUX_USERNAME}}",    m.User.PreferredLinuxUsername)
            .Replace("{{FULL_NAME}}",         m.User.FullName ?? m.User.PreferredLinuxUsername)
            .Replace("{{PASSWORD_OPTION}}",   passwordOption)
            .Replace("{{TARGET_DISK_BYTES}}", m.Hardware.TargetDiskBytes > 0
                                                ? m.Hardware.TargetDiskBytes.ToString()
                                                : "0")
            .Replace("{{TARGET_DISK_MODEL}}", m.Hardware.TargetDiskModel ?? "")
            .Replace("{{INSTALL_MODE}}",      m.Hardware.InstallMode)
            .Replace("{{INCLUDED_FOLDERS}}",  folderList)
            .Replace("{{FOLDER_MAP}}",        folderMap)
            .Replace("{{BROWSER_MAP}}",       browserMap)
            .Replace("{{WIFI_SSID}}",         wifiSsid)
            .Replace("{{WIFI_PSK}}",          wifiPsk)
            .Replace("{{WIFI_LIST}}",         wifiList);
    }

    /// <summary>
    /// Builds the Anaconda kickstart install-source command from the distro's
    /// <c>stage2Url</c>.
    ///
    /// Fedora's mirror network is exposed through a metalink service that returns
    /// the closest, currently-healthy mirror - this is exactly the "Closest
    /// mirror" option in Anaconda's GUI, which is the most reliable source.
    /// When the stage2Url points at a versioned release tree
    /// (<c>…/releases/&lt;ver&gt;/Everything/x86_64/os/</c>) we derive that version
    /// and emit a metalink <c>url</c> line for it. If the version can't be parsed
    /// we fall back to a direct <c>url --url=</c> against the tree, and if no
    /// stage2Url is configured at all we emit a commented-out placeholder so the
    /// kickstart still parses (Anaconda will then prompt interactively).
    /// </summary>
    private static string BuildInstallSourceLine(string? stage2Url)
    {
        if (string.IsNullOrWhiteSpace(stage2Url))
            return "# url: no stage2Url configured in distro.json - Anaconda will prompt for a source";

        // Extract the Fedora release number from a versioned release tree URL,
        // e.g. https://…/releases/44/Everything/x86_64/os/  →  "44".
        var match = System.Text.RegularExpressions.Regex.Match(
            stage2Url, @"/releases/(\d+)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var release = match.Groups[1].Value;
            return $"url --metalink=\"https://mirrors.fedoraproject.org/metalink?repo=fedora-{release}&arch=x86_64\"";
        }

        // Non-versioned (e.g. rawhide or a mirror pin): use the tree directly.
        return $"url --url=\"{stage2Url}\"";
    }

    /// <summary>
    /// Escapes a value for safe placement inside a bash double-quoted string
    /// (e.g. <c>VAR="…"</c>): backslash, double-quote, dollar and backtick.
    /// </summary>
    private static string ShellDoubleQuote(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("$",  "\\$")
             .Replace("`",  "\\`");

    private string RenderInline(MigrationManifest m)
    {
        var diskBytes     = m.Hardware.TargetDiskBytes > 0 ? m.Hardware.TargetDiskBytes.ToString() : "0";
        var folderList    = string.Join(" ", m.Files.IncludedFolders);
        var username      = m.User.PreferredLinuxUsername;
        var fullName      = m.User.FullName ?? username;
        var installSource = BuildInstallSourceLine(_stage2Url);

        return $@"# Generated by Igloo for Fedora KDE
graphical
lang {m.User.Locale}
keyboard --vckeymap={m.User.Keymap} --xlayouts='{m.User.Keymap}'
timezone {m.User.Timezone} --utc
network --bootproto=dhcp --device=link --activate --hostname={username}-pc
{installSource}
rootpw --lock
user --name={username} --groups=wheel --gecos=""{fullName}"" --lock

%pre --interpreter=/bin/bash
TARGET_BYTES=""{diskBytes}""
TARGET_DISK=""""
for dev in /sys/block/sd* /sys/block/nvme*n1 /sys/block/vd*; do
  [ -e ""$dev/size"" ] || continue
  [ ""$(cat $dev/removable 2>/dev/null)"" = ""1"" ] && continue
  size_bytes=$(( $(cat $dev/size) * 512 ))
  if [ ""$size_bytes"" = ""$TARGET_BYTES"" ]; then TARGET_DISK=$(basename $dev); break; fi
done
[ -z ""$TARGET_DISK"" ] && TARGET_DISK=$(lsblk -d -o NAME,SIZE,TYPE,TRAN -b -n | awk '$3==""disk""&&$4!=""usb""' | sort -k2 -rn | head -1 | awk '{{print $1}}')
echo ""bootloader --location=mbr --boot-drive=$TARGET_DISK"" > /tmp/ks-storage.cfg
echo ""ignoredisk --only-use=$TARGET_DISK"" >> /tmp/ks-storage.cfg
echo ""clearpart --drives=$TARGET_DISK --all --initlabel"" >> /tmp/ks-storage.cfg
echo ""autopart --type=lvm"" >> /tmp/ks-storage.cfg
%end

%include /tmp/ks-storage.cfg

%packages
@^kde-desktop-environment
@standard
python3 rsync git
%end

reboot

%post --nochroot --interpreter=/bin/bash --log=/mnt/sysimage/var/log/igloo-post.log
set -euo pipefail
OEMDRV=""/run/install/repo""
SYSIMAGE=""/mnt/sysimage""
USERNAME=""{username}""
USER_HOME=""$SYSIMAGE/home/$USERNAME""
install -d -m 0755 ""$SYSIMAGE/opt/igloo"" ""$SYSIMAGE/var/lib/igloo"" ""$SYSIMAGE/var/log/igloo""
[ -d ""$OEMDRV/igloo-agent"" ] && cp -a ""$OEMDRV/igloo-agent/."" ""$SYSIMAGE/opt/igloo/"" && chmod +x ""$SYSIMAGE/opt/igloo/first-boot.sh"" 2>/dev/null || true
[ -f ""$OEMDRV/migration-manifest.json"" ] && cp ""$OEMDRV/migration-manifest.json"" ""$SYSIMAGE/var/lib/igloo/manifest.json""
if [ -d ""$USER_HOME"" ] && [ -d ""$OEMDRV/files"" ]; then
  for folder in ""$OEMDRV/files""/*/; do
    [ -d ""$folder"" ] || continue
    fname=$(basename ""$folder"")
    mkdir -p ""$USER_HOME/$fname""
    cp -a ""$folder/."" ""$USER_HOME/$fname/""
  done
  UID_NUM=$(grep ""^$USERNAME:"" ""$SYSIMAGE/etc/passwd"" 2>/dev/null | cut -d: -f3)
  GID_NUM=$(grep ""^$USERNAME:"" ""$SYSIMAGE/etc/passwd"" 2>/dev/null | cut -d: -f4)
  [ -n ""${{UID_NUM:-}}"" ] && [ -n ""${{GID_NUM:-}}"" ] && chown -R ""$UID_NUM:$GID_NUM"" ""$USER_HOME""
fi
cat > ""$SYSIMAGE/etc/systemd/system/igloo-first-boot.service"" <<'UNIT'
[Unit]
Description=Igloo first-boot migration agent
After=network-online.target
Wants=network-online.target
ConditionPathExists=/var/lib/igloo/manifest.json
ConditionPathExists=!/var/lib/igloo/.done
[Service]
Type=oneshot
ExecStart=/opt/igloo/first-boot.sh
StandardOutput=append:/var/log/igloo/first-boot.log
StandardError=append:/var/log/igloo/first-boot.log
[Install]
WantedBy=multi-user.target
UNIT
chroot ""$SYSIMAGE"" systemctl enable igloo-first-boot.service || \
  ln -sf /etc/systemd/system/igloo-first-boot.service ""$SYSIMAGE/etc/systemd/system/multi-user.target.wants/igloo-first-boot.service""
%end
";
    }

    private static DistroMetadata BuildMetadata(DistroManifest raw)
    {
        var req = raw.MinimumRequirements;
        return new DistroMetadata
        {
            DisplayName               = raw.DisplayName,
            Description               = raw.Description,
            DefaultDesktopEnvironment = raw.DefaultDesktopEnvironment ?? "KDE Plasma",
            InstallerType             = InstallerType.Anaconda,
            IsoDownloadUrl            = new Uri(raw.Iso.DownloadUrl),
            IsoSha256                 = raw.Iso.Sha256,
            IsoGpgSignatureUrl        = raw.Iso.GpgSignatureUrl is not null ? new Uri(raw.Iso.GpgSignatureUrl) : null,
            IsoGpgKeyUrl              = raw.Iso.GpgKeyUrl       is not null ? new Uri(raw.Iso.GpgKeyUrl)       : null,
            Tags                      = raw.Tags,
            Screenshots               = raw.Screenshots,
            MinimumRequirements       = req is not null
                ? new HardwareRequirements
                  {
                      MinRamBytes  = req.MinRamBytes,
                      MinDiskBytes = req.MinDiskBytes,
                      RequiresUefi = req.RequiresUefi,
                      Requires64Bit = req.Requires64Bit,
                  }
                : new HardwareRequirements(),
        };
    }

    private static DistroMetadata FallbackMetadata() => new()
    {
        DisplayName               = "Fedora KDE",
        Description               = "Fedora with the KDE Plasma desktop environment.",
        DefaultDesktopEnvironment = "KDE Plasma",
        InstallerType             = InstallerType.Anaconda,
        IsoDownloadUrl            = new Uri("https://fedoraproject.org"),
        IsoSha256                 = string.Empty,
    };
}
