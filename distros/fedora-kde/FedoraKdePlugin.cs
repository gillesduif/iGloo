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
    /// Parameterless constructor — reads <c>distro.json</c> from the same directory as this DLL.
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

        Metadata = raw is not null ? BuildMetadata(raw) : FallbackMetadata();
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
            files.Add(new AgentFile(
                RelativePath: Path.GetFileName(path),
                Contents:     File.ReadAllBytes(path),
                Executable:   executable));
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string RenderFromTemplate(string template, MigrationManifest m)
    {
        // Included folder names as a space-separated list for the %post shell loop.
        var folderList = string.Join(" ", m.Files.IncludedFolders);

        // Password: use the user's chosen password (--plaintext).
        // If somehow empty, fall back to a locked account so we don't create
        // a passwordless account — the first-boot agent should handle recovery.
        var password       = m.User.LinuxPassword;
        var passwordOption = !string.IsNullOrEmpty(password)
            ? $"--password={password} --plaintext"
            : "--lock";

        return template
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
            .Replace("{{INCLUDED_FOLDERS}}",  folderList);
    }

    private static string RenderInline(MigrationManifest m)
    {
        var diskBytes  = m.Hardware.TargetDiskBytes > 0 ? m.Hardware.TargetDiskBytes.ToString() : "0";
        var folderList = string.Join(" ", m.Files.IncludedFolders);
        var username   = m.User.PreferredLinuxUsername;
        var fullName   = m.User.FullName ?? username;

        return $@"# Generated by Igloo for Fedora KDE
graphical
lang {m.User.Locale}
keyboard --vckeymap={m.User.Keymap} --xlayouts='{m.User.Keymap}'
timezone {m.User.Timezone} --utc
network --bootproto=dhcp --device=link --activate --hostname={username}-pc
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
