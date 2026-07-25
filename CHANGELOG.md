# Changelog

All notable changes to iGloo are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project aims to follow [Semantic Versioning](https://semver.org/) once it
reaches a stable release.

## [Unreleased]

### Added
- Community health files: `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`,
  `THIRD-PARTY-NOTICES.md`, issue/PR templates, and continuous integration.

### Changed
- The dual-boot path's final action is now the wizard's shared forward button,
  relabeled **Reboot**, instead of a separate button on the page.
- Theming now merges the WPF Fluent dark ResourceDictionary directly rather than
  using the experimental `ThemeMode` property.

### Fixed
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
