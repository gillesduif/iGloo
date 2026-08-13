# Hardware findings register

Every entry below was discovered on physical hardware, not in a VM and not by
reading documentation. Test machine unless noted otherwise: UEFI, NVIDIA RTX
5070 (Blackwell), dual Samsung Odyssey G70D (one landscape, one portrait),
July-August 2026 bare-metal runs of Fedora KDE, Linux Mint Cinnamon and
Debian 13 GNOME.

This document is the central record. The first-boot agents keep short inline
pointers to it at the exact lines that guard each finding; keep both in sync
when either changes.

## Boot chain and NVIDIA drivers

### 1. modprobe.d blacklists never reach the initramfs (kernel panic)

- **Symptom:** kernel panic on first boot after install.
- **Root cause:** RPM Fusion's `/etc/modprobe.d/` blacklist does not reach the
  initramfs. Dracut builds host-only, baking in the display driver loaded at
  build time (nouveau/nova_core). After the NVIDIA module is installed, both
  drivers probe the same GPU; on Blackwell that conflict escalates to a
  kernel oops instead of a clean fallback.
- **Guard:** blacklist via the kernel command line
  (`rd.driver.blacklist` / `modprobe.blacklist`) only, and only once the
  nvidia module is verified built for every installed kernel. Adding it
  earlier (June 2026 regression) leaves the machine without any display
  driver.
- **Where:** `distros/fedora-kde/agent/agent.py`, `blacklist_nouveau`.

### 1b. nova_core claims the GPU when only nouveau is blacklisted

- **Symptom:** Fedora KDE reached a black screen with a cursor after install on
  an RTX 5070; the GPU rendered, so the driver was not the whole story.
- **Root cause:** Fedora ships `nova_core`, the in-tree Rust driver for
  Blackwell, which binds the GPU exactly like nouveau. Blacklisting nouveau
  alone left nova_core to claim it.
- **Guard:** blacklist `nouveau,nova_core` plus `nvidia-drm.modeset=1`, written
  at first boot only, after the nvidia module is confirmed present for every
  installed kernel, and skipped entirely when Secure Boot would reject the
  unsigned module.
- **Where:** `distros/fedora-kde/agent/agent.py`, commits `cfb8ed4`, `490102d`.

### 2. A mid-install kernel update ships without a usable driver

- **Symptom (a):** reboot lands on a brand-new kernel with no nvidia module;
  nouveau dies on Blackwell, leaving a corrupted framebuffer of repeated boot
  logos.
- **Symptom (b):** Wi-Fi "unknown" after install: the new kernel got
  `kernel-modules-core` but not `kernel-modules`.
- **Root cause:** `akmod-nvidia` depends on `kernel-devel-matched`, which
  pulls the LATEST kernel (7.1.5 during the July 2026 run) mid-install.
  GRUB then boots that newest kernel by default.
- **Guard:** run the kernel/driver validation late (after gpu-drivers), and
  pin the GRUB default to the newest kernel with a VERIFIED module.
- **Where:** `distros/fedora-kde/agent/agent.py`, GPU-driver step ordering
  and the grubby pin.

## Display pipeline

### 3. Windows reports rotated pixel dimensions

- **Symptom:** portrait monitors skipped entirely; layout never applied.
- **Root cause:** Windows reports dmPelsWidth/Height in the CURRENT
  orientation (portrait arrives as 2160x3840), but panels only advertise
  landscape modes. Checking 2160x3840 against the mode list matches nothing.
- **Guard:** unrotate dimensions (swap at 90/270) before mode checks.
- **Where:** both agents, display-layout matching (`_match_display_layouts`).

### 4. Rotation direction mapping

- **Finding:** Windows dmDisplayOrientation 270 corresponds to mutter/xrandr
  "right" (user-verified by hand on the dual-Odyssey setup); 90 is the
  mirror. An earlier guess had 270 as "left" and produced mirrored screens.
- **Where:** both agents, rotation mapping tables.

### 5. Kernel DRM connector names vs X RandR names

- **Symptom:** Cinnamon discarded `~/.config/monitors.xml` on first login;
  layout stuck at 60 Hz landscape.
- **Root cause:** the boot-time file uses kernel DRM names (DP-4, HDMI-A-2),
  but Cinnamon on X11 matches against RandR names, which the NVIDIA X driver
  numbers differently (DP-0, HDMI-0). No match means Cinnamon deletes the
  file.
- **Guard:** stage the layout keyed by EDID PnP id and rewrite/apply it from
  inside the first user session (`display-apply.py` via xrandr).
- **Where:** `_stage_display_login_hook`, `display-apply.py`.

### 6. GNOME on Wayland ignores a staged monitors.xml

- **Symptom:** Debian 13 / GNOME 48 session stayed at EDID-preferred 60 Hz
  landscape even though monitors.xml was present.
- **Root cause:** mutter parses and normalizes the file but never applies it
  for the user session.
- **Guard:** GNOME goes through mutter's own D-Bus API with exact mode ids
  from GetCurrentState (`display-apply-gnome.py`); Cinnamon keeps xrandr.
  Dispatch happens on `XDG_CURRENT_DESKTOP` in `display-apply.sh`.

### 7. HDMI connector naming differs between kernel and mutter

- **Finding:** the kernel names HDMI outputs `HDMI-A-N`; mutter and X call
  the same connector `HDMI-N`. Compare normalized on both sides.
- **Where:** `display-apply-gnome.py`, `_norm_connector`.

### 8. Identical monitor models report identical EDID serials

- **Finding:** two Samsung Odyssey G70D's on the test bench carry the same
  EDID serial, so serial alone cannot disambiguate outputs. Matching must
  survive that (vendor+product+connector fallback).
- **Where:** `display-apply-gnome.py`, monitor matching.

### 9. The NVIDIA module can load minutes into boot

- **Finding:** on Debian the nvidia module tainted the kernel at t=179s,
  long after anything ordered `Before=display-manager` has run. Also:
  nouveau cannot bind Blackwell on Debian 13's 6.12, so no DRM connector
  exists until the proprietary driver loads after reboot.
- **Guard:** the second pass explicitly `modprobe nvidia-drm` and waits a
  bounded ~60 s for connectors; first pass installs the second pass when no
  EDID-readable output exists.
- **Where:** `_wait_for_display_outputs`, `_install_display_second_pass`.

### 10. A missing login helper fails silently at every login

- **Symptom:** layout never applied, no error anywhere (Mint run).
- **Guard:** `display-apply.sh` writes an explicit ERROR line to the log when
  the Python helper is missing.
- **Where:** `_stage_display_login_hook`.

## Apt, network and desktop seeding (Debian family)

### 11. Stale apt indices from the install image

- **Symptom:** every package URL 404'd; `check=False` let the agent report
  success while nothing was installed (Mint, 2026-07-30).
- **Guard:** `apt-get update` first; `apt_install` detects transient/stale
  markers, refreshes once and retries before failing.
- **Where:** `apt_install`.

### 12. Link-up is not DNS

- **Symptom:** `nm-online` reported "online" while DNS was still broken; the
  next apt-get update failed on "Could not resolve" for every repo (Mint,
  2026-07-30).
- **Guard:** verify name resolution, not just link state.
- **Where:** network wait before package installs.

### 13. dconf silently ignores seeds without a profile entry

- **Symptom:** keyboard seed ran and logged success; the session still came
  up qwerty (Debian 13).
- **Root cause:** a compiled db under `/etc/dconf/db/local.d` is only read
  when `/etc/dconf/profile/user` names `system-db:local`.
- **Guard:** `_ensure_dconf_local_db` before seeding.

## Known cosmetic issues (accepted, tracked)

- A `grub-common` upgrade can undo the boot-menu patches. The agent edits two
  dpkg conffiles: `/etc/grub.d/10_linux` (drops the "Advanced options" submenu)
  and `/etc/grub.d/30_os-prober` (renames the Windows entry). If dpkg installs
  the maintainer's version, the stock menu returns. dpkg keeps the local file by
  default and the machine stays bootable either way, but the agent only runs at
  first boot, so it does not repair itself. Both patches are marker-guarded and
  log the state on any later `--only boot-menu` run.

- On a mixed portrait+landscape setup the wallpaper fit on the landscape
  screen was zoomed instead of fit (Mint run). Debian handled both correctly.
  Per-monitor fit policy (zoom for portrait, scaled for landscape) is tracked
  as GitHub issue #2.
