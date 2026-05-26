
<p align="center">
<<<<<<< HEAD
  <img src="ocs/assets/iGloo-Logo.svg" alt="iGloo" width="220">
=======
  <img src="docs/assets/igloo.svg" alt="iGloo" width="220">
>>>>>>> d2f8000 (Update logo image source in README.md)
</p>

<p align="center">
  <strong>The penguin escapes the iGloo.</strong>
  <br>
  <sub>Because nobody owns a working USB stick anymore.</sub>
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: GPL v2" src="https://img.shields.io/badge/License-GPL_v2-blue.svg"></a>
  <a href="#status"><img alt="Status" src="https://img.shields.io/badge/status-alpha-yellow.svg"></a>
  <a href="https://dotnet.microsoft.com/"><img alt=".NET 8 WPF" src="https://img.shields.io/badge/.NET-8.0%20WPF-512BD4?logo=dotnet&logoColor=white"></a>
  <a href="#building"><img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D6?logo=windows&logoColor=white"></a>
  <a href="distros/fedora-kde/agent/"><img alt="Python 3" src="https://img.shields.io/badge/agent-Python%203-3776AB?logo=python&logoColor=white"></a>
  <a href="distros/fedora-kde/"><img alt="Fedora KDE" src="https://img.shields.io/badge/distro-Fedora%20KDE%2044-51A2DA?logo=fedora&logoColor=white"></a>
</p>

<p align="center">
  <a href="docs/">Docs</a> ·
  <a href="docs/architecture.md">Architecture</a> ·
  <a href="distros/">Distributions</a> ·
  <a href="#contributing">Contributing</a> ·
  <a href="#roadmap">Roadmap</a>
</p>

---

iGloo is a Windows app that installs Linux. You pick a distro from a catalog, click through a wizard, and reboot. The machine comes back up on Linux with your files, browser profiles, and a sane set of replacement apps already in place. No USB stick required.

## Status

Alpha. The full pipeline is working end-to-end on VMware: preflight detection, ISO acquisition with GPG verification, file staging, partition management, GRUB boot orchestration, fully unattended Anaconda installation, and first-boot configuration. Fedora KDE 44 installs as a dual-boot alongside Windows without a USB drive. The user's Linux password is collected in the wizard and set directly by the kickstart. User files (Desktop, Documents, Downloads, …) are copied from the Windows NTFS partition during install. The GRUB menu shows both operating systems on first boot. After the first login the first-boot agent enables RPM Fusion, installs multimedia codecs and NVIDIA drivers, registers Flathub, and installs any Linux apps the user selected in the wizard.

The remaining work before v1.0 is real-hardware testing across different firmware configurations (M9).

Don't run this on a production machine yet, but it is well past the "does it even boot" phase.

If you've worked on Wubi, Operese, Calamares, Anaconda, EasyBCD, or any partition-resize tool and have battle scars to share, open an issue. The project is at the stage where real-hardware testing feedback is more valuable than new features.

## What makes iGloo different

Every previous Windows-to-Linux installer has been tied to a single distribution:

| Tool         | Distro    | Status                 |
|--------------|-----------|------------------------|
| Wubi         | Ubuntu    | Discontinued           |
| Operese      | Kubuntu   | Active, single-distro  |
| Mint Stick   | Mint      | Active, single-distro  |
| **iGloo**    | **Any**   | **In development**     |

iGloo is **distro-agnostic by design**. The catalog of supported distributions lives in `distros/` and is community-owned. Adding a new distro is a pull request, not a code change.

For v1 the target is Fedora KDE. Linus daily-drives Fedora; KDE Plasma is the desktop environment closest to what Windows users already know. Other distros follow once the plugin pattern is proven on this one.

## How it works

iGloo supports two installation paths:

### Path A - Direct install (no USB)

```
┌──────────────────────────────────┐         ┌──────────────────────────────┐
│   Windows (iGloo.exe)            │         │   Linux installer            │
├──────────────────────────────────┤         ├──────────────────────────────┤
│ 1. Pre-flight check              │         │                              │
│ 2. Pick distro from catalog      │         │                              │
│ 3. Download & GPG-verify ISO     │         │                              │
│ 4. Stage user files              │         │                              │
│ 5. Shrink Windows partition      │         │                              │
│ 6. Carve FAT32 OEMDRV partition  │  ────►  │ 8. UEFI → shim → GRUB       │
│    on internal disk              │         │ 9. Anaconda picks up ks.cfg  │
│    (kernel, initrd, shim, GRUB,  │         │    from OEMDRV automatically │
│     grub.cfg, ks.cfg, manifest,  │         │ 10. Unattended dual-boot     │
│     migration agent)             │         │     install from network     │
│ 7. Write one-shot UEFI NVRAM     │         │ 11. First-boot agent applies │
│    entry → reboot                │         │     migration manifest       │
└──────────────────────────────────┘         └──────────────────────────────┘
```

### Path B - USB install

```
┌──────────────────────────────────┐         ┌──────────────────────────────┐
│   Windows (iGloo.exe)            │         │   Linux installer            │
├──────────────────────────────────┤         ├──────────────────────────────┤
│ 1–4. Same as above               │         │                              │
│ 5. Raw-write ISO to USB          │  ────►  │ 6. Boot from USB             │
│ 6. Write OEMDRV partition on USB │         │ 7. Anaconda picks up ks.cfg  │
│    (kickstart + manifest + agent)│         │    from OEMDRV automatically │
│                                  │         │ 8. First-boot agent applies  │
│                                  │         │    migration manifest        │
└──────────────────────────────────┘         └──────────────────────────────┘
```

The two halves communicate through a `migration-manifest.json` on a FAT32 volume labelled `OEMDRV`, which Anaconda has auto-detected for years. Full spec in [`docs/architecture.md`](docs/architecture.md).

## Roadmap

| Milestone | Scope                                                                                        | Status            |
|-----------|----------------------------------------------------------------------------------------------|-------------------|
| M1        | Skeleton, plugin architecture, manifest contract                                             | ✅ Done           |
| M2        | Pre-flight detection (BitLocker, Secure Boot, partitions, GPU, TPM)                          | ✅ Done           |
| M3        | ISO acquisition - resumable download, SHA-256 auto-resolved from GPG-signed CHECKSUM file    | ✅ Done           |
| M4        | Migration setup, file staging, manifest generation, plugin invocation                        | ✅ Done           |
| M5        | USB writer - raw ISO write, GRUB patch, OEMDRV partition, file copy                         | ✅ Done           |
| M6        | Disk selection UI + kickstart safety (target disk detection, bounded clearpart, %pre)        | ✅ Done           |
| M7        | First-boot agent for Fedora KDE (RPM Fusion, codecs, NVIDIA drivers, welcome screen)        | ✅ Done           |
| M8        | Direct install - no USB: FAT32 OEMDRV on internal disk, GRUB from ISO, one-shot UEFI entry  | ✅ Done           |
| M9        | Closed beta across firmware / Secure Boot / BitLocker / encryption combinations              | Planned           |
| M10       | v1.0 public release, Fedora KDE                                                              | Planned           |
| M11       | Linux Mint as second distro, validates the plugin abstractions                               | Planned           |

**M8 complete.** The full direct-install pipeline is working end-to-end on VMware:

- Anaconda boots from the internal FAT32 OEMDRV partition (no USB required).
- The kickstart runs unattended: partitions the disk, installs the full KDE Plasma 6 desktop from the network.
- Anaconda stage2 (`images/install.img`, ~870 MiB) is copied from the ISO to OEMDRV - no unreliable 862 MB mirror download at boot time.
- os-prober is enabled and `grub2-mkconfig` is re-run in `%post` so the GRUB menu shows both Fedora and Windows.
- The user's Linux password is collected during the iGloo wizard and written into the kickstart - no locked account, no SDDM autologin workaround.
- User files (Documents, Downloads, Desktop, …) are copied directly from the Windows NTFS partition during Anaconda's `%post` - no staging to OEMDRV required.

**M7 complete.** The first-boot agent (`distros/fedora-kde/agent/`) runs on the freshly-installed Linux system via a systemd one-shot service:

- Enables RPM Fusion free + nonfree for the detected Fedora version.
- Installs multimedia codecs via RPM Fusion. Detects DNF 5 (Fedora 41+) vs DNF 4 and uses the correct group-install syntax for each (`dnf install @multimedia` on DNF 5, `dnf groupupdate multimedia` on DNF 4).
- Installs NVIDIA proprietary drivers from RPM Fusion (`akmod-nvidia` + `akmods --force`) when the manifest records an NVIDIA GPU.
- Registers the Flathub remote system-wide (not present by default on Fedora netinstall).
- Installs any Linux apps the user selected in the wizard as Flatpak or DNF packages.
- Drops an XDG autostart entry that fires a `notify-send` welcome notification on first login.
- Redacts `linuxPassword` from `/var/lib/igloo/manifest.json` after use - the plaintext password served its purpose during kickstart and should not persist on disk.

The agent is written in Python 3 (pre-installed via the kickstart `%packages` section). Each step is best-effort: a failure is logged to `/var/log/igloo/agent.log` and the remaining steps continue. The `.done` marker at `/var/lib/igloo/.done` prevents re-runs.

The wizard detects installed Windows applications and suggests Linux equivalents (VLC, Spotify, Discord, Steam, OBS Studio, VSCode, and 18 others). The user opts in per-app on the Migration Setup page; selections are recorded in `migration-manifest.json` and installed by the agent on first boot.

**Next up - M9** (closed beta): real-hardware testing across firmware types, Secure Boot configurations, and BitLocker states.

## Building

### Requirements

- Windows 10 1809+ or Windows 11
- .NET 8 SDK (no Windows App SDK required)
- Administrator privileges (partition resize and UEFI NVRAM writes require elevation)

### Build

```powershell
git clone https://github.com/gillesduif/iGloo.git
cd iGloo
dotnet restore
dotnet build
dotnet run --project src/Igloo.App
```

The app requests a UAC elevation prompt at startup because several operations - partition resize, UEFI NVRAM entry registration, EFI partition writes - require a high-integrity token. The installer reads the EFI System Partition to find `\EFI\fedora\` paths and carves a new FAT32 partition on the target disk.

### Testing the suggested packages wizard step

1. Make sure at least one of the 22 mapped apps is installed on your Windows machine (VLC, Spotify, Discord, Steam, VSCode, etc.).
2. Run the app and navigate to the **Configure your Linux setup** step.
3. The **Suggested Linux apps** section should appear listing detected matches. Adjust the checkboxes.
4. Complete the wizard through File Staging. Inspect the generated manifest:
   ```
   %LOCALAPPDATA%\Igloo\staging\fedora-kde\migration-manifest.json
   ```
   The `suggestedPackages` array should contain the selected apps with `"autoInstall": true`.

### Testing the first-boot agent on the installed system

After a successful install, boot into Fedora and check the agent log:

```bash
# Was the service triggered?
systemctl status igloo-first-boot

# Full agent output
cat /var/log/igloo/agent.log

# Verify RPM Fusion repos are active
dnf repolist | grep fusion

# Verify Flathub remote is registered
flatpak remotes

# Verify the password was redacted from the manifest
# (file is chmod 640 root:root after redaction - sudo required)
sudo grep linuxPassword /var/lib/igloo/manifest.json
# → should print:  "linuxPassword": null
```

To re-run the agent after a first boot (e.g. when iterating on `agent.py`):

```bash
sudo rm /var/lib/igloo/.done
sudo python3 /opt/igloo/agent.py \
    --manifest /var/lib/igloo/manifest.json \
    --log-dir  /var/log/igloo
```

To test with a crafted manifest without reinstalling, copy and edit `/var/lib/igloo/manifest.json`, then point `--manifest` at the copy.

> **Note:** the C# namespaces and project names use `Igloo` (PascalCase) rather than `iGloo`. This is C# convention - identifiers don't start with a lowercase letter. The product is "iGloo"; the code is `Igloo`. Same pattern Apple uses for iPhone/IPhone.

## Safety

iGloo writes to your partition table and your boot manager. That class of operation has exactly one acceptable failure mode: clean abort with no damage. Here's how the project gets there.

**One-shot UEFI entry.** The Fedora installer is registered as `BootNext` - a one-time NVRAM variable the firmware clears after a single use. If the installer fails to launch for any reason, the next reboot returns to Windows. No infinite boot loop.

**OEMDRV on a separate partition.** The installer's FAT32 partition is distinct from both Windows and the future Linux partition. If something goes wrong during installation, Windows is untouched.

**Windows partition resize via Windows itself.** iGloo uses `Resize-Partition` via the `MSFT_Partition` WMI class - the same mechanism Disk Management uses. No custom partitioning logic, because that's exactly how Paragon and AOMEI have historically broken filesystems.

**Kickstart bounds checking.** The `%pre` script in the kickstart matches the target disk by exact byte size and model string before issuing any `clearpart` command. If no match is found, it falls back to the largest non-removable disk rather than guessing.

All operations are logged to `%LOCALAPPDATA%\Igloo\logs` with enough detail to do post-mortem analysis. Sensitive data is excluded.

## Repository structure

```
iGloo/
├── src/
│   ├── Igloo.App/             # WPF desktop app (entry point, wizard UI, DI wiring)
│   ├── Igloo.Core/            # Plugin abstractions, manifest models, service contracts
│   ├── Igloo.Preflight/       # Windows system detection (WMI) + direct-install service
│   │                          #   DirectInstallService: partition carving, ISO extraction,
│   │                          #   GRUB config, UEFI NVRAM registration
│   │                          #   WindowsAppScanner: registry scan → Linux app suggestions
│   ├── Igloo.Iso/             # ISO download (resumable), SHA-256 + GPG verification
│   │                          #   SHA-256 auto-resolved from GPG-signed CHECKSUM file
│   ├── Igloo.Migration/       # File staging (copy user folders to temp dir)
│   └── Igloo.UsbWriter/       # Raw ISO write, GRUB patch, OEMDRV partition creation
├── distros/
│   ├── _schema/               # distro.json JSON Schema (validated in CI)
│   ├── _template/             # Starting point for new distro contributions
│   └── fedora-kde/            # Reference implementation (Fedora 44 KDE, Anaconda)
│       ├── distro.json        # Metadata: name, ISO URL, CHECKSUM URL, GPG key, stage2 URL
│       ├── FedoraKdePlugin.cs # IDistroPlugin: compatibility checks, kickstart rendering
│       ├── kickstart/         # ks.cfg.template - dual-boot, OEMDRV detection, os-prober
│       └── agent/             # first-boot.sh (systemd entry point)
│                              # agent.py (Python 3 - RPM Fusion, codecs, GPU drivers,
│                              #           Flathub, suggested packages, welcome notif,
│                              #           manifest password redaction)
├── tests/                     # xUnit test suites
├── docs/
│   ├── architecture.md
│   └── decisions/             # Architecture Decision Records
└── .github/workflows/         # CI
```

## Adding a distribution

The full guide is in [`distros/README.md`](distros/README.md). Short version:

1. Copy `distros/_template/` to `distros/<your-distro-id>/`.
2. Fill in `distro.json`: name, description, ISO URL, GPG CHECKSUM URL, GPG key URL, `stage2Url` (for netinstall), hardware tags, screenshots.
3. Implement `IDistroPlugin` in a plugin assembly in the same directory.
4. Provide an installer-driver template for your distro's installer (kickstart for Anaconda, preseed for Ubiquity, Calamares config, etc.).
5. Provide a first-boot agent that reads `/var/lib/igloo/manifest.json` and applies it.
6. Open a PR.

The Fedora KDE plugin in [`distros/fedora-kde/`](distros/fedora-kde/) is the reference implementation. The SHA-256 hash does not need to be hardcoded - iGloo auto-resolves it from the GPG-signed CHECKSUM file at download time.

## Contributing

The areas where help is most useful right now:

- **Real-hardware testing** across firmware types (AMI, Phoenix, Insyde), Secure Boot states, and BitLocker configurations. VMware Workstation is the current test environment; real hardware will surface different failure modes.
- **Distro plugins** beyond Fedora KDE - Linux Mint (M11) and Ubuntu are the most-requested.
- **Architectural review** by anyone who's shipped a Linux installer, a partition tool, or boot-loader code on Windows.
- **ADR contributions** in `docs/decisions/` for places where alternatives were considered.

For substantial changes, open an issue first. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the rest.

## License

GPL-2.0-only. Same license as the Linux kernel. A tool that repartitions disks and rewrites boot managers should not be allowed to become closed-source. Full text in [`LICENSE`](LICENSE).

## Credits

iGloo is maintained by [@gillesduif](https://github.com/gillesduif), an individual contributor who got tired of digging through a USB-stick drawer. This is the origin story of most good open-source projects.

Thanks to the Fedora Project and Red Hat for Fedora KDE and Anaconda; to the shim and GRUB2 communities for the boot chain that makes this possible; and to the Linux kernel community for the foundation everything sits on.

---

<sub>iGloo is an independent open-source project and is not affiliated with Red Hat, Inc., the Fedora Project, or Linus Torvalds. "Fedora" is a registered trademark of Red Hat, Inc. "Linux" is a registered trademark of Linus Torvalds.</sub>
