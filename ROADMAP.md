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

## In progress

| Milestone | Scope | State |
|---|---|---|
| M13 🚧 | **Linux detection and removal.** Detect existing Linux installs and remove them cleanly: delete Linux partitions, remove EFI boot entries, restore the Windows bootloader, grow NTFS back | Removal is implemented and exposed in the app. Free-space reclaim after install (handing the deleted staging partition's space back to Windows automatically) is landing. Real-hardware validation ongoing |
| M15 🚧 | **Closed beta.** Real-hardware matrix: firmware vendors × Secure Boot × BitLocker × GPU, with the three validated distros | Blocked on the same bare-metal passes that close the open items below |
| M12 🚧 | **Step-by-step visual guide.** Screenshots and GIFs of the full journey for the README and the website | Shot list defined in `docs/guide/`; captures are taken during VM validation runs |

### Open engineering items being burned down now

- **Fedora KDE bare-metal re-test.** The nouveau blacklist that caused a black
  first boot on NVIDIA hardware has been removed (the driver takeover now
  relies on RPM Fusion's own modprobe.d blacklist, with nouveau as the
  working fallback). Needs a clean end-to-end pass on the RTX 50-series
  machine before Fedora returns to fully validated.
- **Mint GPU driver re-test.** The RTX 50-series driver-variant fix needs its
  own confirmation run.
- **Debian offline install soak.** The live-image squashfs path boots and
  installs on real hardware; it needs more cycles across machines before the
  🚧 comes off the status table.

## Planned

| Milestone | Scope |
|---|---|
| M14 📋 | **Pre-install safety snapshot and rollback.** Undo a migration in one click. Closes the last "half-state" risk in the register (R-04) |
| M16 📋 | **v1.0 public release.** Source-only alpha first; signed binaries once a code-signing certificate exists (an unsigned executable that repartitions disks trips SmartScreen and looks like malware) |

### After v1.0, in rough priority order

1. **Ubuntu validation.** Most of the pipeline is proven; resumption cost is
   estimated at an afternoon thanks to the engineering dossier in
   [`distros/ubuntu/STATUS.md`](distros/ubuntu/STATUS.md).
2. **Catalog activation.** Turn the 16 "coming soon" entries into real
   plugins. The shared Debian-family agent makes apt-based distros the
   cheapest; Arch-family and Atomic distros each need one new installer-stack
   integration first.
3. **Wizard localization.** Dutch, French and German first.
4. **Accessibility pass** across the wizard.
5. **LUKS full-disk encryption option** at install time.
6. **Reproducible builds and signed releases.**

## How priorities are set

1. **Data safety outranks everything.** A partitioning fix in one distro
   triggers an audit of all distros in the same change (CONTRIBUTING.md rule 3).
2. **Validation beats features.** A distro only moves to ✅ after a clean
   end-to-end run on real hardware, and the status tables never overstate.
3. **The hardware matrix decides.** New work is sequenced by the risk register
   in `docs/business/risk-register.md`, not by what would demo well.

Want to change this list? Open an issue. Want to work on it? See
[`CONTRIBUTING.md`](CONTRIBUTING.md).
