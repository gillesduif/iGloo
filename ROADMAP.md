# iGloo Roadmap

This roadmap tracks the project by milestone. Dates are deliberately absent:
the project is maintained by one person and the hardware matrix decides the
pace, not the calendar. Status reflects the actual state of the code and its
real-hardware validation, not aspirations.

Legend: ✅ done · 🚧 in progress · 📋 planned

## Done

| Milestone | Scope |
|---|---|
| M1-M8 ✅ | **Core pipeline.** Plugin architecture, pre-flight hardware detection, verified ISO acquisition (SHA-256 + GPG with pinned fingerprints), migration wizard, USB writer, direct install without USB, Fedora first-boot agent |
| M9 ✅ | **Multi-distro expansion.** Generic distro-driven install pipeline (`InstallerBootSpec`), Debian + Mint + Ubuntu plugins, shared Debian-family first-boot agent |
| M10 ✅ | **Security hardening.** GPG keys pinned by full 160-bit fingerprint, offline key bundling, keyserver-substitution resistance |
| M11 ✅ | **Open-source readiness.** Community health files, CI, third-party notices, relicense to GPL-3.0-or-later (ADR-010) |
| M18 ✅ | **Bare-metal validation round (August 2026).** Fedora KDE, Linux Mint and Debian each installed unattended on the same physical machine (UEFI, NVIDIA RTX 5070, two monitors). Validated per distro: dual-boot preservation, NVIDIA driver install, per-monitor display layout (resolution, refresh rate, rotation), keyboard layout (azerty), wallpaper migration, file migration. Two structural defects found and fixed along the way: the Fedora agent's dnf transaction pulled the newest kernel via the `kernel-devel-matched` dependency chain (now pre-satisfied for the running kernel, with the GRUB default pinned to the verified kernel), and duplicate EDID serial numbers across identical monitors broke display matching (now connector-first) |
| M19 ✅ | **Windows installer packaging.** Inno Setup script, self-contained publish pipeline, one-command build script (`installer/build-setup.bat`) that produces `iGloo-Setup-<version>.exe` with a SHA-256 for the release notes. Version 0.1-alpha |

## In progress

| Milestone | Scope | State |
|---|---|---|
| M13 🚧 | **Linux detection and removal.** Detect existing Linux installs and remove them cleanly: delete Linux partitions, remove EFI boot entries, restore the Windows bootloader, grow NTFS back | Removal is implemented and exposed in the app, including free-space reclaim that hands the deleted staging partition's space back to Windows automatically. Real-hardware validation ongoing |
| M15 🚧 | **Closed beta.** Real-hardware matrix: firmware vendors × Secure Boot × BitLocker × GPU, with the three validated distros | The 0.1-alpha release is the first artifact for this matrix. One firmware/GPU combination is green; the matrix broadens from here |
| M12 🚧 | **Step-by-step visual guide.** Screenshots and GIFs of the full journey for the README and the website | Shot list defined in `docs/guide/`; captures are taken during bare-metal validation runs |

## Planned

| Milestone | Scope |
|---|---|
| M17 📋 | **End-user-friendly boot menu.** Replace the stock GRUB menu with a readable, branded boot menu: clear entry names ("Fedora KDE" and "Windows 11", not kernel version strings), boot the last-used operating system by default (GRUB `saved` default), a sensible timeout, and a consistent look across the three shipped distros. The boot menu is the one screen every dual-boot user sees every day; today it looks like a debugging console |
| M14 📋 | **Pre-install safety snapshot and rollback.** Undo a migration in one click. Closes the last "half-state" risk in the register (R-04) |
| M16 📋 | **v1.0 public release.** Alpha first; signed binaries once a code-signing certificate exists (an unsigned executable that repartitions disks trips SmartScreen and looks like malware) |

### After v1.0, in rough priority order

1. **Ubuntu validation.** Most of the pipeline is proven; resumption cost is
   estimated at an afternoon thanks to the engineering dossier in
   [`distros/ubuntu/STATUS.md`](distros/ubuntu/STATUS.md).
2. **Migration-report welcome screen.** The first login already opens a
   welcome entry on the Debian family; extend it to Fedora and let it report
   exactly what was migrated (files, Wi-Fi networks, browser profiles,
   display layout) and what was skipped, with a link to the logs. Confidence
   for the user, fewer "did it work?" reports for the issue tracker.
3. **Boot-menu recovery entry.** A "Return to Windows and undo" boot option
   alongside the normal entries, so a user who panics at the menu has a
   visible way back. Depends on M14.
4. **Catalog activation.** Turn the 15 "coming soon" entries into real
   plugins. The shared Debian-family agent makes apt-based distros the
   cheapest; Arch-family and Atomic distros each need one new installer-stack
   integration first.
5. **Wizard localization.** Dutch, French and German first.
6. **Accessibility pass** across the wizard.
7. **LUKS full-disk encryption option** at install time.
8. **Reproducible builds and signed releases.**
9. **Cross-platform exploration (community request).** iGloo is
   Windows-to-Linux by design. A Linux or macOS edition would be a separate
   product that reuses the plugin model and the agent layer, not a port of
   the wizard. Parked until v1.0 proves the core flow.

## How priorities are set

1. **Data safety outranks everything.** A partitioning fix in one distro
   triggers an audit of all distros in the same change (CONTRIBUTING.md rule 3).
2. **Validation beats features.** A distro only moves to ✅ after a clean
   end-to-end run on real hardware, and the status tables never overstate.
3. **The hardware matrix decides.** New work is sequenced by the risk register
   in `docs/business/risk-register.md`, not by what would demo well.

Want to change this list? Open an issue. Want to work on it? See
[`CONTRIBUTING.md`](CONTRIBUTING.md).
