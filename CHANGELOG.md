# Changelog

All notable changes to iGloo are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project aims to follow [Semantic Versioning](https://semver.org/) once it
reaches a stable release.

## [Unreleased]

### Added
- Community health files: `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`,
  `THIRD-PARTY-NOTICES.md`, issue/PR templates, and continuous integration.
- Linux removal space reclaim: the pre-flight check now grows the Windows
  volume into unallocated space that directly follows it. This closes the loop
  left when the first-boot agent deletes the staging partition from Linux,
  where the NTFS volume in front of it cannot be resized. Users no longer
  finish the job by hand in Disk Management.
- `ROADMAP.md` and `COPYRIGHT` files; ADR-010 records the relicense decision.
- Chromium password migration (browser migration Phase 2, ADR-011). Saved
  logins from Chrome, Edge, Brave, Vivaldi, and Opera are decrypted on Windows
  (DPAPI, current-user scope), re-encrypted into an AES-256-GCM envelope keyed
  from the Linux account password, and carried in the migration manifest as
  `browsers[].credentialsBlob`. The first-boot agents decrypt the envelope and
  insert the logins into the target browser's `Login Data` database using the
  Linux v10 scheme, then redact the blob. Browsers that enforce App-Bound
  Encryption (Chrome 127 and later) are detected and skipped. Adds runtime
  dependencies Microsoft.Data.Sqlite 8.0.4 and
  System.Security.Cryptography.ProtectedData 8.0.0.

### Changed
- Relicensed from GPL-2.0-only to **GPL-3.0-or-later**. GPLv3 is compatible
  with the Apache-2.0 dependencies the app ships (GPL-2.0 is not) and adds an
  explicit patent grant. See `docs/decisions/010-relicense-gpl3.md`.
- The dual-boot path's final action is now the wizard's shared forward button,
  relabeled **Reboot**, instead of a separate button on the page.
- Theming now merges the WPF Fluent dark ResourceDictionary directly rather than
  using the experimental `ThemeMode` property.

### Fixed
- Fedora KDE: removed the nouveau kernel-command-line blacklist on installed
  systems. Meant to give NVIDIA machines a clean first boot, it instead left
  them with no display driver at all (black screen on bare metal) whenever the
  proprietary driver was not ready yet. nouveau is the working fallback; RPM
  Fusion's own modprobe.d blacklist handles the driver takeover once the
  proprietary driver is installed.
- Cleared all Roslyn analyzer warnings under `AnalysisMode=All` with no
  suppressions: modernized P/Invoke to `[LibraryImport]`, added `CultureInfo`
  and `StringComparison` to string/format calls, and narrowed broad exception
  catches to their real types.

## [0.0.1-alpha] - 2026

First tagged pre-release. Unattended, USB-free Linux install with dual-boot,
plus data/Wi-Fi/app migration.

- **Fedora KDE** — validated end-to-end on real hardware (dual-boot, NVIDIA
  driver, file + Wi-Fi migration).
- **Debian 13** and **Linux Mint Cinnamon** — validated end-to-end in a VM.
- **Ubuntu** — in development.

[Unreleased]: https://github.com/gillesduif/iGloo/compare/v0.0.1-alpha...HEAD
[0.0.1-alpha]: https://github.com/gillesduif/iGloo/releases/tag/v0.0.1-alpha
