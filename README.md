   
<p align="center">
  <img src="docs/assets/iGloo.svg" alt="iGloo" width="220">
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
  <a href="distros/"><img alt="Distros" src="https://img.shields.io/badge/distros-Fedora%20·%20Debian%20·%20Mint%20·%20Ubuntu-51A2DA?logo=linux&logoColor=white"></a>
  <a href="#supporting-the-project"><img alt="Sponsor" src="https://img.shields.io/badge/❤-Support%20iGloo-ff69b4"></a>
</p>

<p align="center">
  <a href="docs/">Docs</a> ·
  <a href="docs/architecture.md">Architecture</a> ·
  <a href="distros/">Distributions</a> ·
  <a href="docs/guide/">Step-by-step guide</a> ·
  <a href="#roadmap">Roadmap</a> ·
  <a href="#contributing">Contributing</a>
</p>

---

iGloo is two products in one Windows app:

1. **A Linux installer that needs no USB stick.** Pick a distro from a catalog,
   answer a short wizard, click install. iGloo shrinks Windows, stages the Linux
   installer on the internal disk, and reboots straight into a fully unattended
   installation. Windows stays bootable beside it (dual-boot) unless you choose
   to replace it.
2. **A migration assistant.** Your documents, downloads, pictures, browser
   profiles, and Wi-Fi networks move over automatically. GPU drivers (including
   NVIDIA), multimedia codecs, your keyboard layout, and Linux replacements for
   your Windows apps are installed before you ever see the desktop. You don't
   land on *a* Linux machine — you land on *your* machine.

No USB stick. No BIOS settings. No partitioning questions. No terminal.

## Status

Alpha — but well past "does it even boot":

| Distro | Pipeline | Validation |
|---|---|---|
| **Linux Mint Cinnamon** | Ubiquity / preseed (casper) | ✅ Installs end-to-end on **real hardware** — unattended, dual-boot preserved, agent + file migration verified. GPU driver selection was picking the wrong variant on RTX 50-series; fixed, pending re-test |
| **Fedora KDE** | Anaconda / kickstart | ✅ Installs end-to-end on **real hardware** — dual-boot beside Windows, file + Wi-Fi migration. ⚠️ Open: NVIDIA driver on a multi-kernel install (see below) |
| **Debian 13** | debian-installer + live-installer / preseed (offline Live image) | 🚧 Reworked to an **offline** install (copies the Live image's squashfs — no network needed until first boot) after the netinst path failed on a Wi-Fi-only machine. Boots and installs on real hardware; under active testing |
| **Ubuntu** | subiquity / autoinstall (cloud-init) | 🚧 In development — most of the pipeline proven (6 GB ISO staging, casper/toram boot, autoinstall, partition preservation); parked on a final installer/disk-release quirk. Full engineering dossier in [`distros/ubuntu/STATUS.md`](distros/ubuntu/STATUS.md) |

### Known constraint: Secure Boot + NVIDIA

Secure Boot only loads kernel modules signed by a key the firmware trusts. NVIDIA's
driver is built **on your machine** (DKMS/akmods), so the module it produces is
unsigned and the kernel refuses it — the install succeeds, then the desktop comes up
without acceleration, at the wrong resolution, with `nvidia … FAILED` during boot.
The bootloader is unaffected (shim and GRUB are Microsoft-signed), which is what
makes this so easy to misread as a driver bug.

iGloo warns about this in the system check when it finds an NVIDIA GPU with Secure
Boot on. Two ways through:

- **Turn Secure Boot off** in firmware — simplest, and what the warning suggests
- **Enrol a MOK** and sign the module — keeps Secure Boot on; more steps

Mint is the exception: Ubuntu ships **pre-built Canonical-signed** NVIDIA modules, so
with Secure Boot on iGloo installs those instead and no key enrolment is needed.

ISO downloads are verified with SHA-256 **and** GPG signatures checked against
signing keys pinned by full 160-bit fingerprint (bundled offline where the distro
permits) — see [Security](#safety--security).

Don't run this on a production machine yet. If you've worked on Wubi, Operese,
Calamares, Anaconda, EasyBCD, or any partition-resize tool and have battle scars
to share, open an issue — real-hardware testing feedback is currently more
valuable than new features.

## What makes iGloo different

Every previous Windows-to-Linux installer was tied to a single distribution and
stopped at "Linux is installed":

| Tool | Distro | Migrates your data? | Status |
|---|---|---|---|
| Wubi | Ubuntu | No | Discontinued |
| Operese | Kubuntu | Partially | Active, single-distro |
| Mint Stick | Mint | No | Active, single-distro |
| **iGloo** | **Any** | **Files, Wi-Fi, browser, drivers, apps** | **In development** |

iGloo is **distro-agnostic by design**. Each distribution is a self-contained
plugin in `distros/` — a declarative boot spec, an installer-config template, and
a first-boot agent. Four distros across three unrelated installer stacks
(Anaconda, debian-installer, Ubiquity/casper, subiquity) run on the same pipeline
with zero pipeline changes. Adding a distro is a pull request, not a fork.

## How it works

```
┌──────────────────────────────────┐         ┌──────────────────────────────┐
│   Windows (iGloo.exe)            │         │   Linux installer            │
├──────────────────────────────────┤         ├──────────────────────────────┤
│ 1. Pre-flight check              │         │                              │
│ 2. Pick distro from catalog      │         │                              │
│ 3. Download ISO; verify SHA-256  │         │                              │
│    + GPG (pinned fingerprint)    │         │                              │
│ 4. Wizard → migration manifest   │         │                              │
│ 5. Shrink Windows partition      │         │                              │
│ 6. Carve FAT32 staging partition │  ────►  │ 8. UEFI → GRUB → installer   │
│    (kernel, initrd, installer    │         │ 9. Unattended install        │
│     config, manifest, agent,     │         │    (kickstart / preseed /    │
│     full ISO when needed)        │         │     autoinstall)             │
│ 7. One-shot UEFI boot entry →    │         │ 10. First boot: agent runs   │
│    reboot                        │         │     before the login screen  │
└──────────────────────────────────┘         │     → drivers, codecs, files,│
                                             │     Wi-Fi, apps, keyboard    │
                                             └──────────────────────────────┘
```

A USB path (raw ISO write + staging partition on the stick) also exists for
machines where direct install isn't possible.

The Windows side and the Linux side communicate through a single
`migration-manifest.json` on the FAT32 staging volume. The first-boot agent runs
as a systemd oneshot *before the display manager*, so setup completes before the
first login. Full details in [`docs/architecture.md`](docs/architecture.md).

## See it in action

*Coming with the beta: a full click-by-click walkthrough with screenshots and
GIFs of the whole journey — wizard → reboot → unattended install → first login
with your files in place. Shot list and capture instructions live in
[`docs/guide/`](docs/guide/).*

## Roadmap

### Done
| Milestone | Scope |
|---|---|
| M1–M8 | Core pipeline: plugin architecture, preflight, verified ISO acquisition, migration wizard, USB writer, direct install (no USB), Fedora first-boot agent |
| M9 | Multi-distro expansion: generic distro-driven install pipeline (`InstallerBootSpec`), Debian + Mint + Ubuntu plugins, shared Debian-family agent |
| M10 | Security hardening: GPG keys pinned by full fingerprint, offline key bundling, keyserver-substitution resistance |

### In progress
| Milestone | Scope |
|---|---|
| M15 | **Closed beta**: real-hardware matrix (firmware vendors × Secure Boot × BitLocker × GPU) with the three validated distros |
| M12 | Step-by-step visual guide (screenshots/GIFs of the full journey) |

### Planned
| Milestone | Scope |
|---|---|
| M13 | **Linux detection & removal.** Detect existing Linux installs and offer clean, safe removal: delete Linux partitions, remove EFI boot entries, restore the Windows bootloader, grow NTFS back. Today this requires a technician; leaving Linux should be as easy as trying it — that's what makes trying it a safe decision. |
| M14 | Pre-install safety snapshot & rollback (undo a migration in one click) |
| M16 | v1.0 public release |
| Later | Ubuntu validation (parked; see [`distros/ubuntu/STATUS.md`](distros/ubuntu/STATUS.md)), wizard localization (NL/FR/DE first), accessibility pass, LUKS full-disk encryption option, reproducible builds + signed releases |

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

The app requests a UAC elevation prompt at startup because several operations —
partition resize, UEFI NVRAM entry registration, EFI partition writes — require a
high-integrity token.

### Verifying an install (any Debian-family distro)

After first boot:

```bash
systemctl status igloo-first-boot          # did the agent service run?
sudo cat /var/log/igloo/bootstrap.log      # first-boot bootstrap trace (Mint/Ubuntu)
sudo cat /var/log/igloo/first-boot.log     # full agent output
ls -la ~/Documents ~/Downloads             # migrated files
sudo grep linuxPassword /var/lib/igloo/manifest.json   # → "linuxPassword": null (redacted)
```

To re-run the agent while iterating:

```bash
sudo rm /var/lib/igloo/.done
sudo python3 /opt/igloo/agent.py --manifest /var/lib/igloo/manifest.json --log-dir /var/log/igloo
```

> **Note:** the C# namespaces use `Igloo` (PascalCase) rather than `iGloo` — C#
> identifiers don't start lowercase. The product is "iGloo"; the code is `Igloo`.

## Safety & security

iGloo writes to your partition table and your boot manager. That class of
operation has exactly one acceptable failure mode: clean abort with no damage.

**One-shot UEFI entry.** The installer boots via `BootNext` — a one-time NVRAM
variable the firmware clears after a single use. If the installer fails to launch,
the next reboot returns to Windows. No boot loop.

**Windows resize via Windows itself.** Partition shrink uses `Resize-Partition`
(the same MSFT WMI mechanism Disk Management uses). No custom NTFS logic.

**Free-space-only partitioning.** Unattended installers are configured to install
*only* into the space iGloo freed — and, after a hard lesson, tested against the
installer defaults that silently escalate to whole-disk wipes.

**Verified downloads.** Every ISO is checked against its SHA-256 **and** its GPG
signature; signing keys are pinned by full 160-bit fingerprint and bundled with
the app where the distro's policy allows. Short key IDs are never trusted.

**Traceable unattended phases.** Every unattended step writes a persistent
execution trace to disk (`/var/log/igloo*`), so any failure is diagnosable
after the fact.

All Windows-side operations are logged to `%LOCALAPPDATA%\Igloo\logs`. Sensitive
data is excluded; the Linux-side manifest redacts the password after first use.

## Repository structure

```
iGloo/
├── src/
│   ├── Igloo.App/             # WPF desktop app (wizard UI, DI wiring)
│   ├── Igloo.Core/            # Plugin abstractions (IDistroPlugin, InstallerBootSpec),
│   │                          #   manifest models, service contracts
│   ├── Igloo.Preflight/       # Windows detection (WMI) + DirectInstallService:
│   │                          #   partition carving, kernel/initrd staging, initrd
│   │                          #   config injection, GRUB config, UEFI registration
│   ├── Igloo.Iso/             # Resumable download, SHA-256 + GPG verification
│   │                          #   (pinned fingerprints, bundled keys)
│   ├── Igloo.Migration/       # File staging (user folders → staging volume)
│   └── Igloo.UsbWriter/       # USB path: raw ISO write + staging partition
├── distros/
│   ├── _schema/               # distro.json JSON Schema (validated in CI)
│   ├── _template/             # Starting point for new distro contributions
│   ├── _debian-family/        # Shared first-boot agent for Debian/Mint/Ubuntu
│   ├── fedora-kde/            # Anaconda / kickstart (reference implementation)
│   ├── debian/                # debian-installer + live-installer / preseed (offline Live image)
│   ├── linuxmint-cinnamon/    # Ubiquity / preseed (casper live ISO)
│   └── ubuntu/                # subiquity / autoinstall (cloud-init NoCloud)
├── tests/                     # xUnit test suites
├── docs/
│   ├── architecture.md
│   ├── guide/                 # Step-by-step visual guide (shot list + captures)
│   ├── whitepaper/            # Technical white paper (draft)
│   └── decisions/             # Architecture Decision Records
└── .github/workflows/         # CI
```

## Adding a distribution

The full guide is in [`distros/README.md`](distros/README.md). Short version:

1. Copy `distros/_template/` to `distros/<your-distro-id>/`.
2. Fill in `distro.json`: name, ISO URL, checksum/signature URLs, GPG key file +
   **full fingerprint**, hardware tags, screenshots.
3. Implement `IDistroPlugin` and declare your `InstallerBootSpec` (kernel cmdline,
   artifact paths, config delivery — see the four existing plugins for patterns
   across Anaconda, d-i, Ubiquity, and subiquity).
4. Provide an installer-config template (kickstart / preseed / autoinstall).
5. Provide (or reuse) a first-boot agent that applies `manifest.json`.
6. Open a PR.

## Contributing

Most useful right now:

- **Real-hardware testing** across firmware types (AMI, Phoenix, Insyde),
  Secure Boot states, and BitLocker configurations.
- **Distro plugins** — the Debian-family agent makes apt-based distros cheap to add.
- **Architectural review** by anyone who's shipped an installer, partition tool,
  or boot-loader code.
- **ADR contributions** in `docs/decisions/`.

For substantial changes, open an issue first. See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Supporting the project

iGloo is built by a single developer, and real-hardware testing eats laptops.
If you want to help millions of stranded Windows 10 machines find a second life:

- ⭐ **Star the repo** — visibility is currency for a trust-critical tool.
- 🐛 **Test and report** — a failing install log from your hardware is worth money.
- 💶 **Donate** — *[GitHub Sponsors / Ko-fi / Liberapay links coming with the
  public beta — badge placeholders above]*.
- 🏢 **Partner** — distro maintainer, refurbisher, or public-sector migration
  project? Open an issue or reach out directly.

## License

GPL-2.0-only. Same license as the Linux kernel. A tool that repartitions disks
and rewrites boot managers should not be allowed to become closed-source.
Full text in [`LICENSE`](LICENSE).

## Credits

iGloo is maintained by [@gillesduif](https://github.com/gillesduif), an individual
contributor who got tired of digging through a USB-stick drawer. This is the
origin story of most good open-source projects.

Thanks to the Fedora Project, Debian, Linux Mint, and Canonical for the
distributions and installers; to the shim and GRUB2 communities for the boot
chain; and to the Linux kernel community for the foundation everything sits on.

---

<sub>iGloo is an independent open-source project and is not affiliated with Red
Hat, Inc., the Fedora Project, Debian, Linux Mint, Canonical Ltd., or Linus
Torvalds. "Fedora", "Ubuntu", and "Linux" are trademarks of their respective
owners.</sub>
