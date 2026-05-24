

<p align="center">
  <img src="docs/assets/igloo-logo.svg" alt="iGloo" width="220">
</p>

<p align="center">
  <strong>The penguin escapes the iGloo.</strong>
  <br>
  <sub>Because nobody owns a working USB stick anymore.</sub>
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: GPL v2" src="https://img.shields.io/badge/License-GPL_v2-blue.svg"></a>
  <a href="#status"><img alt="Status" src="https://img.shields.io/badge/status-pre--alpha-orange.svg"></a>
  <a href="https://dotnet.microsoft.com/"><img alt=".NET 8 WPF" src="https://img.shields.io/badge/.NET-8.0%20WPF-512BD4?logo=dotnet&logoColor=white"></a>
  <a href="#building"><img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D6?logo=windows&logoColor=white"></a>
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

Pre-alpha. The skeleton, plugin architecture, and manifest contract are done. The functional engine (system detection, ISO acquisition, partition prep, boot orchestration) is being implemented milestone by milestone. Don't run this on a machine you care about yet.

If you've worked on Wubi, Operese, Calamares, Anaconda, EasyBCD, or any partition-resize tool and have battle scars to share, open an issue. The project is at the stage where architectural input is more valuable than code.

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

```
┌────────────────────────────┐         ┌────────────────────────────┐
│   Windows (iGloo.exe)      │         │   Linux (first-boot)       │
├────────────────────────────┤         ├────────────────────────────┤
│ 1. Pre-flight check        │         │                            │
│ 2. Pick distro from catalog│         │                            │
│ 3. Download + verify ISO   │         │                            │
│ 4. Stage user files        │         │                            │
│ 5. Shrink Windows partition│         │                            │
│ 6. Write OEMDRV volume     │  ────►  │ 7. Anaconda runs unattended│
│    (kickstart + manifest   │         │ 8. First-boot agent picks  │
│     + agent)               │         │    up the manifest         │
│ 7. Boot manager entry      │         │ 9. Migrate files, browsers │
│ 8. Reboot                  │         │ 10. Install codecs/drivers │
└────────────────────────────┘         │ 11. Welcome screen         │
                                       └────────────────────────────┘
```

The Windows half prepares. The Linux half commits. The two halves communicate through a `migration-manifest.json` written to a FAT32 volume labelled `OEMDRV`, which Anaconda has auto-detected for years. Full spec in [`docs/architecture.md`](docs/architecture.md).

## Roadmap

| Milestone | Scope                                                                                        | Status         |
|-----------|----------------------------------------------------------------------------------------------|----------------|
| M1        | Skeleton, plugin architecture, manifest contract                                             | ✅ Done        |
| M2        | Pre-flight detection (BitLocker, Secure Boot, partitions, GPU, TPM)                          | ✅ Done        |
| M3        | ISO acquisition with resumable download, SHA-256 and GPG verification                        | ✅ Done        |
| M4        | Migration setup, file staging, manifest generation, plugin invocation                        | ✅ Done        |
| M5        | USB writer — raw ISO write, GRUB patch (FAT16/32 + ISO9660), OEMDRV partition, file copy    | ✅ Done        |
| M6        | Disk selection UI + kickstart safety (target disk, bounded clearpart, %pre detection)        | 🚧 In progress |
| M7        | First-boot agent for Fedora KDE (RPM Fusion, codecs, NVIDIA drivers, welcome screen)        | 🚧 In progress |
| M8        | Direct install — no USB: shrink Windows partition, write OEMDRV on internal disk, WBM entry | Planned        |
| M9        | Closed beta across firmware / Secure Boot / BitLocker / encryption combinations              | Planned        |
| M10       | v1.0 public release, Fedora KDE                                                              | Planned        |
| M11       | Linux Mint as second distro, validates the plugin abstractions                               | Planned        |

## Building

### Requirements

- Windows 10 1809+ or Windows 11
- .NET 8 SDK (no Windows App SDK required)

### Build

```powershell
git clone https://github.com/gillesduif/iGloo.git
cd iGloo
dotnet restore
dotnet build
dotnet run --project src/Igloo.App
```

iGloo runs as the invoking user. Operations that require elevation (partition resize, boot manager modification, BitLocker suspension) request a UAC prompt at the point they are invoked.

> **Note:** the C# namespaces and project names use `Igloo` (PascalCase) rather than `iGloo`. This is C# convention — identifiers don't start with a lowercase letter. The product is "iGloo"; the code is `Igloo`. Same pattern Apple uses for iPhone/IPhone.

## Safety

iGloo writes to your partition table and your boot manager. That class of operation has exactly one acceptable failure mode: clean abort with no damage. Here's how the project gets there.

Before any destructive step, iGloo backs up the partition table, the full BCD store, and the contents of the EFI System Partition. These backups live in a known location and can be used to restore the prior state.

The Linux installer is registered as a one-time boot entry, not the default. If it fails to launch for any reason, the next reboot returns to Windows. No infinite boot loop.

Partition resizing uses Windows' own `Resize-Partition` (via the `MSFT_Partition` WMI class). No custom partitioning logic, because that's exactly how Paragon and AOMEI have historically broken filesystems.

A bootable rescue USB image is generated during the staging phase, before anything destructive happens. If the machine ends up in a state that won't boot, you have a recovery path that doesn't require another working computer.

All operations are logged to `%LOCALAPPDATA%\iGloo\logs` with enough detail to do post-mortem analysis on a bricked machine. Sensitive data is excluded.

## Adding a distribution

The full guide is in [`distros/README.md`](distros/README.md). Short version:

1. Copy `distros/_template/` to `distros/<your-distro-id>/`.
2. Fill in `distro.json` with metadata: name, description, ISO URL, SHA-256, GPG signature URL, hardware tags, screenshots.
3. Implement `IDistroPlugin` in a plugin assembly in the same directory.
4. Provide an installer-driver template for your distro's installer (kickstart for Anaconda, preseed for Ubiquity, JSON config for Calamares, etc.).
5. Provide a first-boot agent (typically a Bash entry point and a Python implementation) that applies the migration manifest.
6. Open a PR.

The Fedora KDE plugin in [`distros/fedora-kde/`](distros/fedora-kde/) is the reference implementation. Copy from it.

## Repository structure

```
iGloo/
├── src/
│   ├── Igloo.App/             # WPF desktop app (entry point, wizard UI, DI wiring)
│   ├── Igloo.Core/            # Plugin abstractions and manifest models
│   ├── Igloo.Preflight/       # Windows system detection (WMI-based)
│   ├── Igloo.Iso/             # ISO download, SHA-256 + GPG verification
│   ├── Igloo.Migration/       # File staging service (copy user folders to temp dir)
│   └── Igloo.UsbWriter/       # Raw ISO write, GRUB patch, OEMDRV partition creation
├── distros/
│   ├── _template/             # Starting point for new distros
│   └── fedora-kde/            # Reference implementation
│       ├── distro.json        # Metadata (name, ISO URL, SHA-256, tags)
│       ├── FedoraKdePlugin.cs # IDistroPlugin implementation
│       ├── kickstart/         # ks.cfg.template for Anaconda
│       └── agent/             # first-boot.sh + agent.py migration agent
├── tests/                     # xUnit test suites
├── docs/
│   ├── architecture.md
│   └── decisions/             # Architecture Decision Records
└── .github/workflows/         # CI
```

## Contributing

The areas where help is most useful right now:

- Architectural review by anyone who's shipped a Linux installer, a partition tool, or boot-loader code on Windows
- Distro plugins beyond Fedora KDE
- Testing on real hardware across firmware types, Secure Boot states, and BitLocker configurations
- ADR contributions in `docs/decisions/` for places where alternatives were considered

For substantial changes, open an issue first. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the rest.

## License

GPL-2.0-only. Same license as the Linux kernel. A tool that repartitions disks and rewrites boot managers should not be allowed to become closed-source. Full text in [`LICENSE`](LICENSE).

## Credits

iGloo is maintained by @gillesduif, an individual contributor who got tired of digging through a USB-stick drawer. This is the origin story of most good open-source projects. If you want to help shape iGloo's direction, the issue tracker is open.

Thanks to the Fedora Project and Red Hat for Fedora KDE and Anaconda, to the Linux kernel community for the foundation everything sits on, and to Linus Torvalds for the kernel and the license.

---

<sub>iGloo is an independent open-source project and is not affiliated with Red Hat, Inc., the Fedora Project, or Linus Torvalds. "Fedora" is a registered trademark of Red Hat, Inc. "Linux" is a registered trademark of Linus Torvalds.</sub>
