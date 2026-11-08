<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/readme-hero-dark.png">
    <img src="docs/assets/readme-hero-light.png" alt="iGloo" width="444">
  </picture>
</p>

<p align="center">
  <a href="https://github.com/gillesduif/iGloo/releases"><img alt="Latest release" src="https://img.shields.io/github/v/release/gillesduif/iGloo?include_prereleases&label=release"></a>
  <a href="https://github.com/gillesduif/iGloo/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/gillesduif/iGloo/total"></a>
  <a href="https://github.com/gillesduif/iGloo/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/gillesduif/iGloo/actions/workflows/ci.yml/badge.svg"></a>
  <a href="#install"><img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%20(1809%2B)%20%7C%2011-0078D6?logo=windows&logoColor=white"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/gillesduif/iGloo"></a>
</p>

# iGloo

iGloo moves Windows PCs to Linux, for individuals and for organisations.

The desktop wizard, **iGloo Community**, shrinks the Windows partition, installs
a Linux distribution next to Windows from within Windows, carries files, browser
sessions, Wi-Fi networks and display layout across, and installs the right GPU
driver on first boot. No USB stick needed, no terminal. **Fleet**, **One** and
the **CLI** share the same engine and are in development.

**[Download](#install)** · [Products](#products) · [Distribution status](#distribution-status) · [Documentation](#documentation) · [Roadmap](#roadmap) · [Contributing](#contributing-and-security)

## Products

| | Product | What it is | State |
|---|---|---|---|
| <img src="docs/assets/iGlooCommunity.svg" width="56" alt=""> | **[Community](docs/05%20-%20Community/Community%20Overview.md)** | Desktop wizard. Installs a distribution next to Windows and migrates the user's data and settings. | **0.2-alpha** released |
| <img src="docs/assets/iGlooFleet.svg" width="56" alt=""> | **[Fleet](https://github.com/gillesduif/iGloo-Fleet)** | Endpoint administration for organisations: an agent per endpoint, a server control plane and a web console to migrate a Windows fleet to Linux and manage it afterwards. Own repository, source-available. | In development |
| <img src="docs/assets/iGlooAI.svg" width="56" alt=""> | **[One](docs/06%20-%20One/iGloo%20One%20Overview.md)** | Conversational shell with the Tux mascot. Inspects the machine through the CLI; no AI model integrated yet. | Early development |
| <img src="docs/assets/iGlooCLI.svg" width="56" alt=""> | **[CLI](docs/03%20-%20CLI%20and%20Command%20Contract/CLI%20and%20Command%20Contract%20Overview.md)** | `Igloo.Cli.exe`: human commands, `--json` output, JSON Lines `invoke --machine` transport. 19 public capabilities, none destructive. | In development |

All four sit on one **Platform**: the Core engine, its safety gates and the
distribution plugins. Frontends propose; Core validates against live machine
state and executes. Nothing partitions a disk, writes a boot entry or runs an
installer outside Core; the Fleet Server approves and orchestrates, and only
the Agent on the endpoint executes, through the same CLI. See
[product boundaries](docs/architecture/product-boundaries.md) and
[ADR-013](docs/decisions/013-product-family-and-platform.md).

## What Community does

- **Dual-boot install.** Shrinks Windows, installs beside it from Windows, and removes it cleanly again, partitions and boot entries included.
- **User data.** Files and profile data land in the matching Linux home directories.
- **Browsers.** Open tabs and saved logins carry over. Firefox profiles move as they are; Chromium passwords are re-encrypted for the Linux account.
- **System settings.** Wi-Fi networks, keyboard layout, wallpaper and the per-monitor display layout.
- **Graphics drivers.** Detects the GPU and installs drivers, NVIDIA included, before the first desktop.
- **Applications.** Linux alternatives for the Windows applications it detects.

Every ISO is verified against its SHA-256 checksum and a GPG signature from a
pinned key; a failed check aborts the install.

## Install

Download `iGloo-Setup-<version>.exe` from [Releases](https://github.com/gillesduif/iGloo/releases)
and run it on Windows 10 (1809 or later) or Windows 11. The installer is not
code-signed, so SmartScreen warns; the SHA-256 of every build is in its release notes.

> [!CAUTION]
> iGloo is alpha software. It shrinks partitions, writes to the internal disk and registers UEFI boot entries. Back up first, and do not run it on a machine you cannot afford to reinstall. NVIDIA systems currently need Secure Boot disabled. See the [safety model](docs/reference/safety-model.md).

## Distribution status

| Distribution | Installer stack | Validation |
|---|---|---|
| **Linux Mint Cinnamon** | Ubiquity, preseed (casper) | Physical hardware, 0.2-alpha |
| **Fedora KDE** | Anaconda, kickstart | Physical hardware, 0.2-alpha. Open: [first-login setup screen still appears](https://github.com/gillesduif/iGloo/issues/1) |
| **Debian 13** | debian-installer with live-installer, preseed | Physical hardware, 0.2-alpha |
| **Ubuntu** | Subiquity, autoinstall | Parked since July 2026 to ship the beta with the three above. [Status](distros/ubuntu/STATUS.md), [ADR-009](docs/decisions/009-park-ubuntu-ship-beta-with-three.md) |

15 more distributions are catalog-only entries: metadata and logo, not
installable. Hardware runs: [hardware findings register](docs/reference/hardware-findings.md).
Evidence rules: [support matrix](docs/08%20-%20Distribution%20Providers/Distribution%20Support%20Matrix.md).
A new distribution is one folder and no core changes: [distros/](distros/README.md).

## Documentation

- [Documentation vault](docs/README.md): project map, architecture, decisions and evidence. Obsidian; start at the Project Dashboard.
- [How iGloo works](docs/reference/operation.md)
- [Safety and security model](docs/reference/safety-model.md)
- [Building from source](docs/reference/building.md)
- [Adding a distribution](docs/reference/adding-a-distribution.md)
- [Comparison with other tools](docs/reference/comparison.md)
- [Repository structure](docs/reference/repository-structure.md)

## Roadmap

Milestones, no dates: one maintainer, and the hardware matrix sets the pace.
Now: Linux detection and removal, and the boot menu. Next: pre-install snapshot
and rollback, the finished UI, a closed beta, then 1.0. [ROADMAP.md](ROADMAP.md)
is the index; scope lives on the [milestones](https://github.com/gillesduif/iGloo/milestones).

## Contributing and security

Most wanted: physical hardware test reports, distribution plugins and
architecture review. Open an issue before substantial changes and read
[CONTRIBUTING.md](CONTRIBUTING.md); runtime changes need a disposable-VM run.
Questions go to [Discussions](https://github.com/gillesduif/iGloo/discussions).

Security problems go through GitHub's private vulnerability reporting, never a
public issue: [SECURITY.md](SECURITY.md).

## License

- Community, the engine, the CLI, One and the distribution plugins: [GPL-3.0-or-later](LICENSE).
- The command contract and process client, `src/Igloo.Command.Contracts` and `src/Igloo.Cli.Client`: Apache-2.0, published as NuGet packages so any tool can talk to the CLI.
- Fleet, in [its own repository](https://github.com/gillesduif/iGloo-Fleet): Functional Source License (FSL-1.1-ALv2) plus a commercial license.

[ADR-016](docs/decisions/016-fleet-source-available-and-contract-apache.md) records the split.

---

> Discussed on the WAN Show (Linus Media Group), July 2026:
> ["Maybe It's Time to Leave Windows"](https://youtu.be/AkvetmzCh_M?t=354)

<sub>iGloo is an independent open-source project and is not affiliated with
Red Hat, Inc., the Fedora Project, Debian, Linux Mint, Canonical Ltd. or
Linus Torvalds. "Fedora", "Ubuntu" and "Linux" are trademarks of their
respective owners.</sub>
