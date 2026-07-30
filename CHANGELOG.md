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
- Linux Mint (Cinnamon on X11): migrated display layout (refresh rate,
  rotation) never applied, and `~/.config/monitors.xml` had vanished by first
  login. The first-boot agent runs as root before any X server exists, so it
  can only know KERNEL DRM connector names (DP-4, HDMI-A-2) and wrote those
  into monitors.xml - correct for Wayland compositors. But Cinnamon's settings
  daemon matches monitorspecs against RANDR output names, and the NVIDIA X
  driver names those differently (DP-0, HDMI-0); the configuration matched
  nothing and Cinnamon discarded it, leaving 60 Hz and no rotation (NVIDIA
  driver itself installed fine - bare-metal logs, RTX 5070). The agent now
  also stages the layout keyed by EDID PnP id and registers a Cinnamon-only
  autostart hook: a new `display-apply.py` runs at the first user login, maps
  PnP ids to RandR names via the EDID blocks in `xrandr --verbose`, rewrites
  monitors.xml with those names (so the layout persists), and applies the
  layout immediately with one xrandr call - mirroring the Fedora KDE kscreen
  hook. `display-apply.py` is added to the agent payload of the Mint, Ubuntu
  and Debian plugins (their payload lists are explicit, not directory globs),
  AND to the bootstrap copy step in all three installer templates - that step
  hardcoded only agent.py + first-boot.sh, so the helper silently never
  reached /opt/igloo and the login hook failed against a missing file on every
  login (third Mint bare-metal run). The login wrapper now logs that missing
  file itself instead of failing silently, and igloo-collect gathers the whole
  hook chain (/opt/igloo listing, staged layout, autostart entry, per-user
  helper log and done-marker, session desktop/type); its monitors.xml capture
  now iterates real user homes instead of $HOME, which is /root under sudo.
- Debian family (Linux Mint, Debian, Ubuntu): NVIDIA driver silently not
  installed on first boot when DNS hiccups, leaving the desktop on software
  rendering (one "unknown display", locked settings, 60 Hz, second monitor
  dead) while the agent reported "gpu-drivers: OK". Root cause chain, proven
  from bare-metal logs on the RTX 5070 machine: `nm-online` reported the link
  up while DNS was still broken, so `apt-get update` failed to resolve every
  repository - yet exits 0 on W:-only failures, so the agent logged "package
  lists refreshed"; the subsequent driver install then used the stale indices
  shipped in the install image, every package URL 404'd, and `check=False`
  swallowed that too. The agent is hardened end to end: `wait_for_network` now
  also waits (bounded) for DNS to actually resolve the archive host;
  `apt-get update` retries with honest failure detection (English output via
  `LC_ALL=C`, scanning for fetch/resolve errors instead of trusting the exit
  code) and no longer claims "refreshed" when it is not; package installs
  self-heal - on 404/fetch/DNS errors they refresh indices once and retry; and
  the gpu-drivers step now FAILS hard when no NVIDIA kernel module exists
  afterwards instead of reporting success (a present-but-refused module, e.g.
  Secure Boot, stays an explicit ERROR log with the firmware/MOK fix).
- Fedora KDE: "Gaps between screens are not supported" and wrong monitor
  positions when Windows display scaling was above 100%. KWin positions
  outputs in LOGICAL pixels, but the manifest carried Windows' PHYSICAL pixel
  positions - with 150% scaling on a 3840-wide primary, a second monitor at
  physical x=3840 landed far past the primary's 2560 logical width, leaving a
  gap KWin refuses, and no scale was set at all. Windows scale is now captured
  end to end: `DisplayLayoutReader` reads per-monitor scale via
  EnumDisplayMonitors + `GetScaleFactorForMonitor` (SHCore), the manifest
  carries it as `displays[].scalePercent`, the agent stages it in
  `display-layout.json`, and `display-apply.py` both sets
  `output.<conn>.scale.<factor>` and converts positions to logical pixels
  (divided by the primary's scale - exact for uniform scaling, the common
  case; mixed per-monitor scales are approximated).
- Debian family (Linux Mint, Debian, Ubuntu): the shared agent's display-layout
  step carried the two rotation bugs that were fixed in the Fedora KDE agent
  after RTX 5070 bare-metal validation. The rotation direction map was
  inverted (Windows 270° was mapped to "left"; hardware-validated as "right"),
  and the mode-support check - plus the `<mode>` block itself - used Windows'
  ROTATED pixel dimensions (2160x3840), which panels never advertise, so
  portrait monitors were skipped outright. The agent now checks and writes the
  unrotated mode and uses the validated direction map, matching the Fedora
  agent's behaviour.
- Fedora KDE: post-install setup wizard greeting the user on first boot. The
  kickstart never specified `firstboot`, so the default `--enable` applied and
  Anaconda's "Configure Initial Setup" task left `initial-setup.service`
  active: the GTK first-boot wizard showed its language / keyboard / timezone
  pages (all pre-filled from the kickstart) with extra Next clicks before the
  login screen. The kickstart now sets `firstboot --disable` - everything the
  wizard asks is already configured, so it is pure friction on a migration
  that should boot straight to the desktop.
- Fedora KDE: display layout (rotation, position) never applied. Three stacked
  bugs, all confirmed against the RTX 5070 bare-metal logs:
  - Portrait monitors were skipped outright: Windows reports rotated pixel
    dimensions (2160x3840) but panels only advertise landscape modes - the
    mode-support check looked for the rotated mode and gave up ("HDMI-A-2 does
    not advertise 2160x3840"). The check now uses the unrotated mode.
  - The rotation direction map was inverted: hardware-validated as Windows
    270° -> KDE "right" (was "left"), 90° -> "left".
  - The layout was only written as `monitors.xml`, which KWin does not read -
    on Fedora KDE the whole step was inert. The agent now additionally stages
    `/opt/igloo/display-layout.json` and a KDE-only XDG autostart hook; the
    new `display-apply.py` payload runs at first Plasma login, re-resolves
    EDID PnP ids to current connector names (they change across
    kernels/drivers: DP-4 under nouveau became DP-1 under nvidia), and
    applies mode + rotation + position + primary in one atomic
    `kscreen-doctor` call, retrying on later logins until it succeeds.
- Fedora KDE: no Wi-Fi after the first-boot agent's driver reboot (bare-metal
  RTX 5070, kernel 7.1.5). The `kernel-modules` self-heal ran BEFORE
  `gpu-drivers`, but installing `akmod-nvidia` pulls `kernel-devel-matched` -
  which dragged a whole new kernel (core + modules-core, but NOT
  `kernel-modules`, the package with the Wi-Fi drivers) into the transaction
  one minute after the check had certified the kernel set complete. The agent
  rebooted onto the new kernel: system fine (nvidia built for it), but no
  wireless device existed at all. The step now runs after every dnf
  transaction that can pull a kernel; it only ever installs exact-version
  `kernel-modules-<kver>` packages, so the late placement is safe.
- Fedora KDE: kernel panic on NVIDIA bare metal (RTX 5070). The nouveau
  cmdline blacklist had been removed on the theory that RPM Fusion's
  `/etc/modprobe.d/` blacklist alone handles the driver takeover - but that
  blacklist never reaches the initramfs (dracut builds it host-only, baking in
  whichever display driver was loaded at build time). nouveau/nova_core kept
  loading at every boot alongside the freshly-installed nvidia module, and the
  two drivers fighting over the same Blackwell GPU panicked the kernel. The
  first-boot agent now adds
  `rd.driver.blacklist=nouveau,nova_core modprobe.blacklist=nouveau,nova_core
  nvidia-drm.modeset=1` via `grubby --update-kernel=ALL` - but only AFTER the
  nvidia module is verified built for every installed kernel, and never when
  Secure Boot would reject the unsigned module. This keeps the two properties
  that the previous attempts each broke: the first boot always has a working
  display driver (no premature blacklist), and every later boot hands the GPU
  to nvidia alone (no initramfs-loaded nouveau/nova_core).
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
