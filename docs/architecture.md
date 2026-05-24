# Architecture

## End-to-end flow

1. **Igloo.exe launches** on Windows. UAC elevates immediately.
2. **Plugin discovery.** `Igloo.Core.Plugins.DistroRegistry` scans the `distros/` directory next to the executable and loads each distro plugin into its own AssemblyLoadContext.
3. **Pre-flight check** (`Igloo.Preflight`). Reports BitLocker, Secure Boot, UEFI/BIOS, TPM, disks, GPU. Surfaces blockers and warnings.
4. **Distro selection.** User picks from the catalog. Each plugin's `CheckCompatibility(report)` is called to add distro-specific findings (e.g. NVIDIA on Fedora requires RPM Fusion).
5. **ISO acquisition** (`Igloo.Iso`). Resumable download, SHA256 verification, GPG verification against the distro's pinned key.
6. **Migration scope.** User picks folders to migrate, browser profiles, suggested apps to auto-install.
7. **Pre-destructive backup.** Partition table, BCD, EFI System Partition contents.
8. **Partition prep.** Non-destructive NTFS shrink. **The most likely step to fail** — see below.
9. **Staging.** Selected files copied to staging on the new free space. `migration-manifest.json` generated.
10. **Installer config rendering.** The chosen plugin's `RenderInstallerConfigAsync(manifest)` produces the kickstart / preseed / Calamares config.
11. **Agent payload rendering.** The plugin's `GetAgentPayloadAsync()` produces the first-boot agent files.
12. **OEMDRV volume creation.** Small FAT32 volume labelled `OEMDRV`, contains the rendered installer config, the agent payload, and the manifest.
13. **Boot manager entry.** Windows Boot Manager gets a "boot once" entry pointing at the downloaded ISO. A failure does not trap the user in a boot loop.
14. **Reboot.** Installer auto-discovers the OEMDRV volume, runs unattended.
15. **First boot.** The systemd unit the installer dropped runs the agent. The agent applies the manifest, enables extra repos, installs codecs/drivers, copies user files, imports browsers, installs suggested apps.
16. **Welcome.** A welcome app launches on first graphical login.

## The dangerous steps

Three steps can brick the machine. They get disproportionate engineering attention.

### Step 8 — Partition shrink
NTFS shrink from a running system can fail in ways that leave the filesystem inconsistent. We use Windows' own `Resize-Partition` (via `MSFT_Partition` WMI) rather than rolling our own. If shrink fails or the target size isn't achievable due to unmovable files, we abort with a clear message and do not proceed. **We do not attempt to move unmovable files** — that's how Paragon and AOMEI break filesystems.

### Step 13 — Boot manager modification
A wrong BCD edit can leave the machine unbootable. Mitigations:
- Full BCD export to backup before any modification.
- New entry added as "boot once" (`/bootsequence`, not `/default`).
- Rescue USB generation is offered before this step so the user has a recovery path.

### Step 14 — Reboot into the installer
If the installer fails partway through, the machine is in a half-installed state. Our installer config deliberately doesn't touch the Windows partitions (only the new free space), so the worst case is Windows still boots. The installer's logs are written to the OEMDRV volume during `%post` so they're recoverable from Windows after a failed install.

## Plugin architecture

Distro support is a plugin model. Igloo.Core defines `IDistroPlugin`; each distro lives in its own folder under `distros/` with its own assembly. The core never references concrete distros.

Three reasons:
- **Zero-code distro additions.** A community contributor adds Mint or Zorin without touching `src/`.
- **Per-distro versioning.** Fedora plugin and Mint plugin can release independently.
- **Isolation.** A buggy plugin can't break other distros or the core — each loads into a separate AssemblyLoadContext.

The `_template/` folder is the documentation-by-example for contributors.

## Milestones

| ID | Scope | Status |
|----|-------|--------|
| M1 | Skeleton + plugin architecture + manifest contract | **DONE (this commit)** |
| M2 | Pre-flight detection working on real Windows machines | Next |
| M3 | ISO acquisition end-to-end (download, SHA256, GPG) | After M2 |
| M4 | Staging + manifest generation + plugin invocation | After M3 |
| M5 | First-boot agent for Fedora KDE end-to-end (VM-tested) | Parallel to M3/M4 |
| M6 | OEMDRV volume creation + boot manager entry | After M4 + M5 |
| M7 | Closed beta on real hardware (UEFI/BIOS × SB on/off × BitLocker × GPU vendors) | After M6 |
| M8 | v1.0 public release, Fedora KDE only | After M7 |
| M9 | Second distro (Linux Mint) to validate the plugin abstractions | After M8 |

## Why this two-half architecture

Two halves (Windows-side preparer, Linux-side committer) talking through a staging volume is the only design that handles the fundamental constraint: **we cannot do all the destructive work from a running Windows system.** Partition operations, filesystem creation, and bootloader installation require the target regions to be either unmounted or accessed from a different OS entirely. So Windows stages; Linux commits.

The alternative — driving everything from a WinPE-style preboot environment — is what commercial tools attempt, and it's what breaks them when firmware or driver quirks intervene. Anaconda already knows how to install Fedora. We don't need to reinvent that.

## See also

- `decisions/001-language-and-ui.md` — C# / .NET 8 / WinUI 3 / unpackaged
- `decisions/002-fedora-as-reference.md` — Fedora KDE as the reference plugin
- `decisions/003-runtime-injection.md` — runtime kickstart injection over remastered ISO
- `decisions/004-plugin-architecture.md` — distro support as a plugin model
- `decisions/005-license-gpl2.md` — GPL-2.0-only, matching Linux and Git
