
<p align="center">
  <img src="docs/assets/iGloo.svg" alt="iGloo" width="220">
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: GPL v3" src="https://img.shields.io/badge/License-GPL_v3-blue.svg"></a>
  <a href="#status"><img alt="Status" src="https://img.shields.io/badge/status-alpha-yellow.svg"></a>
  <a href="https://dotnet.microsoft.com/"><img alt=".NET WPF" src="https://img.shields.io/badge/.NET-9.0%20WPF-512BD4?logo=dotnet&logoColor=white"></a>
  <a href="#building-from-source"><img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D6?logo=windows&logoColor=white"></a>
  <a href="distros/"><img alt="Distros" src="https://img.shields.io/badge/distros-4%20pipelines%20%2019%20catalog%20entries-51A2DA?logo=linux&logoColor=white"></a>
</p>

<p align="center">
  <a href="docs/architecture.md">Architecture</a> 
  <a href="distros/">Distributions</a> 
  <a href="ROADMAP.md">Roadmap</a> 
  <a href="CONTRIBUTING.md">Contributing</a> 
  <a href="SECURITY.md">Security</a>
</p>

# iGloo

iGloo is a professional deployment and migration engine designed to
transition Windows environments to Linux natively, without external media.

Unlike traditional single-purpose installers iGloo unifies automated disk
partitioning comprehensive user data migration, hardware configuration and
full system rollback into a single, automated workflow.

## Key Capabilities

- **Native Deployment Mechanism:** Automates low-level disk repartitioning to
  shrink the host Windows partition, allocate a temporary recovery partition,
  and stage the target Linux installation image directly on internal storage.
- **Automated Data Migration:** Detects and transfers user profile data,
  documents, the desktop wallpaper, active browser sessions, saved login
  credentials and localized Wi-Fi configurations directly to the new home
  directories. Gecko-based
  browsers (Mozilla Firefox, Zen Browser, Waterfox) migrate at profile level,
  which carries saved passwords with the profile. Chromium-based browsers
  (Google Chrome, Microsoft Edge, Brave, Vivaldi, Opera) get saved passwords
  decrypted on Windows and re-encrypted for the target Linux account. Browsers
  that enforce App-Bound Encryption (Chrome 127 and later, current Edge and
  Brave builds) are detected and skipped.
- **Pre-Configuration Engine:** Configures the target system bootloader,
  identifies compatible graphics drivers (including NVIDIA), registers native
  keyboard layouts, reproduces the Windows display layout (per-monitor
  resolution, refresh rate, rotation) and queues native Linux alternatives for
  detected Windows applications.
- **Bi-Directional Lifecycle Management:** Detects existing iGloo-deployed
  Linux instances to provide a clean, automated removal pipeline that safely
  deletes Linux partitions, purges EFI boot entries and reclaims unallocated
  space back into the Windows partition.

## Status

**iGloo is alpha software. It modifies the partition table and the boot
configuration. Back up all important data before running it. Do not run it on
production machines.**

| Distribution | Installer stack | Validation state |
|---|---|---|
| **Linux Mint Cinnamon** | Ubiquity / preseed (casper) | Validated end-to-end on physical hardware (NVIDIA RTX 5070): unattended installation, dual-boot preservation, NVIDIA driver installation, display layout (resolution, refresh rate, rotation), keyboard layout, wallpaper and file migration. |
| **Fedora KDE** | Anaconda / kickstart | Validated end-to-end on physical hardware (NVIDIA RTX 5070): dual-boot beside Windows, NVIDIA driver built for the running kernel (the agent pre-installs the matching `kernel-devel-matched` so the akmods dependency chain pulls no surprise kernel), the GRUB default pinned to the verified kernel, display layout, wallpaper and file migration. |
| **Debian 13** | debian-installer + live-installer / preseed | Validated end-to-end on physical hardware (NVIDIA RTX 5070): offline installation that copies the Live image squashfs and requires no network until first boot, dual-boot preservation, display layout, keyboard layout (azerty), wallpaper and file migration. |
| **Ubuntu** | subiquity / autoinstall (cloud-init) | In development. ISO staging, casper/toram boot, autoinstall delivery and partition preservation are individually validated. Parked on an installer disk-release defect. Full analysis in [`distros/ubuntu/STATUS.md`](distros/ubuntu/STATUS.md). |

The application catalog lists 15 additional distributions with the status
"coming soon". These entries contain metadata and logos only. Only the four
distributions listed above implement a complete installation pipeline.

### Secure Boot and NVIDIA GPUs

Secure Boot loads only kernel modules signed by a key that the firmware
trusts. The NVIDIA driver is compiled locally on the target machine
(DKMS/akmods), so the resulting module is unsigned and the kernel rejects it.
The installation completes, but the desktop starts without hardware
acceleration at a reduced resolution. The bootloader is unaffected because
shim and GRUB carry Microsoft signatures, so this condition is frequently
misdiagnosed as a driver defect.

The pre-flight check detects the combination of an NVIDIA GPU and enabled
Secure Boot and displays a warning. Two resolutions are available:

- Disable Secure Boot in the firmware setup. This is the recommended option.
- Enrol a Machine Owner Key (MOK) and sign the module. This retains Secure
  Boot and requires additional steps.

Linux Mint is an exception. Ubuntu publishes pre-built NVIDIA modules signed
by Canonical, so iGloo installs those modules when Secure Boot is enabled and
no key enrolment is required.

## Comparison with existing tools

Earlier Windows-to-Linux installers were each tied to a single distribution
and did not migrate user data:

| Tool | Distribution | Data migration | Maintenance state |
|---|---|---|---|
| Wubi | Ubuntu | None | Discontinued |
| Operese | Kubuntu | Partial | Active, single distribution |
| Mint Stick | Linux Mint | None | Active, single distribution |
| **iGloo** | **Any supported distribution** | **Files, Wi-Fi networks, browser profiles, drivers, applications** | **In development** |

Each distribution is a self-contained plugin under `distros/` that consists of
a declarative boot specification, an installer configuration template and a
first-boot agent. Four distributions across four unrelated installer stacks
(Anaconda, debian-installer, Ubiquity/casper, subiquity) execute on the same
pipeline without pipeline modifications.

iGloo also supports the reverse operation. It detects an existing Linux
installation and removes it: it deletes the Linux partitions, removes the EFI
boot entries, restores the Windows bootloader and extends the Windows volume
into the resulting unallocated space.

## Operation

```
┌──────────────────────────────────┐         ┌──────────────────────────────┐
│   Windows (iGloo.exe)            │         │   Linux installer            │
├──────────────────────────────────┤         ├──────────────────────────────┤
│ 1. Pre-flight hardware check     │         │                              │
│ 2. Distribution selection        │         │                              │
│ 3. ISO download; SHA-256 and     │         │                              │
│    GPG verification (pinned      │         │                              │
│    fingerprint)                  │         │                              │
│ 4. Wizard → migration manifest   │         │                              │
│ 5. Windows partition shrink      │         │                              │
│ 6. Staging partition creation    │  ────►  │ 8. UEFI → GRUB → installer   │
│    (kernel, initrd, installer    │         │ 9. Unattended installation   │
│     configuration, manifest,     │         │    (kickstart / preseed /    │
│     agent, full ISO if required) │         │     autoinstall)             │
│ 7. One-shot UEFI boot entry →    │         │ 10. First boot: agent runs   │
│    reboot                        │         │     before the login screen  │
└──────────────────────────────────┘         └──────────────────────────────┘
```

The Windows component and the Linux component exchange state through a single
`migration-manifest.json` file on the FAT32 staging volume. The first-boot
agent executes as a systemd oneshot unit ordered before the display manager,
so configuration completes before the first login. A fallback USB path (raw
ISO write plus staging partition on removable media) is available for machines
where direct installation is not possible. The complete description is in
[`docs/architecture.md`](docs/architecture.md).

## Roadmap

The full plan with milestone definitions and prioritization criteria is in
[`ROADMAP.md`](ROADMAP.md). Summary:

- **Completed:** core pipeline (M1-M8), multi-distribution expansion (M9),
  ISO verification hardening (M10), open-source readiness including the
  GPL-3.0 relicensing (M11), a bare-metal validation round for Fedora KDE,
  Linux Mint and Debian on NVIDIA hardware (M18), and the Windows installer
  packaging (M19).
- **In progress:** Linux detection and removal (M13), closed beta on a
  physical-hardware matrix (M15), visual step-by-step guide (M12).
- **Planned:** an end-user-friendly GRUB boot menu (M17), pre-installation
  snapshot and rollback (M14), v1.0 public release (M16), Ubuntu validation,
  wizard localization, accessibility, LUKS encryption option, reproducible
  builds with signed releases.

## Building from source

### Requirements

- Windows 10 version 1809 or later / Windows 11
- .NET 9 SDK (the application targets `net9.0-windows`; the libraries target
  `net8.0` and are built by the same SDK)
- Administrator privileges (partition resize and UEFI NVRAM writes require
  elevation)

### Build and run

```powershell
git clone https://github.com/gillesduif/iGloo.git
cd iGloo
dotnet restore
dotnet build
dotnet run --project src/Igloo.App
```

The application requests UAC elevation at startup because partition resize,
UEFI NVRAM entry registration and EFI partition writes require a
high-integrity token.

### Tests

```powershell
dotnet test
```

Six xUnit test projects cover the safety-critical logic: manifest handling,
ISO verification, partition calculations, installer configuration rendering
and progress reporting. CI executes the build and the test suite on every
push and pull request.

### Verifying an installation

After the first boot of the installed system:

```bash
systemctl status igloo-first-boot          # agent service state
sudo cat /var/log/igloo/first-boot.log     # full agent output
ls -la ~/Documents ~/Downloads             # migrated files
sudo grep linuxPassword /var/lib/igloo/manifest.json   # expected: null (redacted)
```

To re-run the agent during development:

```bash
sudo rm /var/lib/igloo/.done
sudo python3 /opt/igloo/agent.py --manifest /var/lib/igloo/manifest.json --log-dir /var/log/igloo
```

> **Naming note:** the C# namespaces use `Igloo` (PascalCase) because C#
> identifiers cannot start with a lowercase letter. The product name is
> "iGloo" and the code identifier is `Igloo`.

## Safety and security model

iGloo writes to the partition table and the boot manager. The design target
for every failure in this class of operation is a clean abort with no data
modification. The implementation applies the following measures:

- **Uses a one-shot UEFI boot entry.** The installer boots through `BootNext`,
  a one-time NVRAM variable that the firmware clears after a single use. If
  the installer does not start, the next boot returns to Windows.
- **Uses Windows-native partition resize.** The shrink operation calls
  `Resize-Partition`, the same WMI mechanism used by Disk Management. The
  codebase contains no custom NTFS logic.
- **Restricts installer partitioning to unallocated space.** Unattended
  installer configurations target only the unallocated region created by the
  shrink operation and each distribution is tested against installer defaults
  that escalate to whole-disk erasure.
- **Verifies every downloaded ISO image.** Each image is checked against its
  SHA-256 checksum and its GPG signature. Signing keys are pinned by full
  160-bit fingerprint and bundled with the application where distribution
  policy permits. Short key IDs are not accepted. Verification failure aborts
  the installation.
- **Retains a functional display driver fallback.** On NVIDIA systems the
  installed system keeps the nouveau driver active until the proprietary
  driver is installed. A failed proprietary driver build produces a working
  desktop with a logged error instead of a system without display output.
- **Writes persistent execution traces.** Every unattended phase appends to
  logs under `/var/log/igloo*` on the target system and
  `%LOCALAPPDATA%\Igloo\logs` on the Windows host, so failures remain
  diagnosable after the fact.
- **Redacts credentials after first use.** The migration manifest clears the
  Linux account password and the encrypted browser credential blobs after the
  agent consumes them and Wi-Fi key files are written with `0600 root:root`
  permissions. Browser credentials travel as an AES-256-GCM envelope keyed
  from the Linux account password; the design is recorded in
  `docs/decisions/011-chromium-credential-migration.md`.

## Repository structure

```
iGloo/
├── src/
│   ├── Igloo.App/             # WPF application (wizard UI, dependency injection)
│   ├── Igloo.Core/            # Plugin abstractions (IDistroPlugin, InstallerBootSpec),
│   │                          #   manifest models, service contracts
│   ├── Igloo.Preflight/       # Hardware detection (WMI), DirectInstallService
│   │                          #   (partitioning, kernel/initrd staging, UEFI
│   │                          #   registration), LinuxRemovalService
│   ├── Igloo.Iso/             # Resumable download, SHA-256 and GPG verification
│   ├── Igloo.Migration/       # User file staging to the staging volume
│   └── Igloo.UsbWriter/       # USB fallback: raw ISO write + staging partition
├── distros/
│   ├── _schema/               # distro.json JSON Schema (validated in CI)
│   ├── _template/             # Template for new distribution plugins
│   ├── _debian-family/        # Shared first-boot agent for Debian/Mint/Ubuntu
│   ├── fedora-kde/            # Anaconda / kickstart (reference implementation)
│   ├── debian/                # debian-installer + live-installer / preseed
│   ├── linuxmint-cinnamon/    # Ubiquity / preseed (casper live ISO)
│   ├── ubuntu/                # subiquity / autoinstall (in development)
│   └── .../                   # 15 "coming soon" catalog entries
├── tests/                     # Six xUnit test projects
├── docs/
│   ├── architecture.md        # System architecture
│   ├── decisions/             # Architecture Decision Records
│   ├── guide/                 # Visual step-by-step guide (in progress)
│   └── whitepaper/            # Technical white paper (draft)
└── .github/workflows/         # CI
```

## Adding a distribution

The complete guide is in [`distros/README.md`](distros/README.md). The
procedure:

1. Copy `distros/_template/` to `distros/<distribution-id>/`.
2. Complete `distro.json`: display name, ISO URL, checksum and signature URLs,
   GPG key file with full fingerprint, hardware tags, screenshots.
3. Implement `IDistroPlugin` and declare an `InstallerBootSpec` (kernel
   command line, artifact paths, configuration delivery). The four existing
   plugins provide reference implementations for Anaconda, debian-installer,
   Ubiquity and subiquity.
4. Provide an installer configuration template (kickstart, preseed or
   autoinstall).
5. Provide or reuse a first-boot agent that applies the migration manifest.
6. Validate an unattended end-to-end installation in a VM and open a pull
   request.

## Contributing

The project currently prioritizes the following contributions:

- **Physical-hardware testing** across firmware vendors (AMI, Phoenix,
  Insyde), Secure Boot states and BitLocker configurations.
- **Distribution plugins.** The shared Debian-family agent reduces the cost
  of apt-based distributions.
- **Architecture review** from maintainers with installer, partitioning or
  bootloader experience.
- **Architecture Decision Records** in `docs/decisions/`.

Open an issue before starting substantial changes. Build requirements,
analyzer policy and validation rules are defined in
[`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

iGloo is licensed under **GPL-3.0-or-later**. Version 3 was selected because
it is compatible with the Apache-2.0 dependencies distributed with the
application (GPL-2.0 is not) and because it provides an explicit patent grant
and anti-tivoization provisions. The complete rationale is recorded in
[`docs/decisions/010-relicense-gpl3.md`](docs/decisions/010-relicense-gpl3.md).

- License text: [`LICENSE`](LICENSE)
- Copyright notice: [`COPYRIGHT`](COPYRIGHT)
- Third-party dependency licenses: [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)

## Acknowledgments

iGloo is maintained by [@gillesduif](https://github.com/gillesduif).

The project depends on the work of the Fedora Project, Debian, Linux Mint and
Canonical (distributions and installers), the shim and GRUB2 communities
(boot chain) and the Linux kernel community.

In July 2026 iGloo was discussed on the WAN Show (Linus Media Group). The
segment is available as a clip,
["Maybe It's Time to Leave Windows"](https://youtu.be/AkvetmzCh_M?t=354).

---

<sub>iGloo is an independent open-source project and is not affiliated with
Red Hat, Inc., the Fedora Project, Debian, Linux Mint, Canonical Ltd. or
Linus Torvalds. "Fedora", "Ubuntu" and "Linux" are trademarks of their
respective owners.</sub>
