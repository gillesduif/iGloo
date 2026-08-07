# iGloo: Leave the Ice Age

**One-click migration from Windows to Linux, for people who have never seen a terminal.**

---

## The problem

Windows 10 support ended on 14 October 2025. An estimated **240 million PCs** cannot
upgrade to Windows 11 (Canalys): perfectly working hardware that is now unsupported.
That is a security problem, an e-waste problem, and a digital-sovereignty problem the
EU is actively trying to solve (see Schleswig-Holstein's public-sector Linux migration).

Linux is the obvious answer, but the migration itself is the barrier. Today it demands
a USB stick, firmware settings, partitioning decisions, and the confidence to risk your
family photos. That excludes exactly the people who need it most.

## The solution

iGloo is a Windows application. The user downloads it, answers a few questions
(which folders to keep, dual-boot or replace), and clicks install. iGloo:

- shrinks the Windows partition and installs Linux **without a USB stick**,
  directly from Windows, with no boot media and no firmware knowledge;
- verifies every downloaded ISO against **pinned GPG signatures** (own trust store,
  hardened against keyserver substitution);
- migrates the user's **files, Wi-Fi profiles, browser data, and desktop wallpaper**
  automatically;
- on first boot, installs **GPU drivers (including NVIDIA), media codecs, keyboard
  layout, and the per-monitor display layout** before the user ever sees the desktop,
  so it lands feeling like *their* machine.

Windows stays untouched next to it (dual-boot) until the user chooses otherwise, and
iGloo can remove the Linux installation again just as cleanly.

## Why previous attempts failed, and why this one is different

Ubuntu's Wubi and Mint's mint4win proved demand in the 2000s but died under the
maintenance burden of BIOS-era quirks. Two things changed: **UEFI standardised the
boot process**, and iGloo's architecture is built for the chaos that remains. Each
distro is an isolated plugin (boot spec, installer config, first-boot agent), so a
quirk in one distro never destabilises the others. The hard, unglamorous problems
(Secure Boot, NVIDIA, OEM firmware, installer preseeding) are the product.

## Status (August 2026)

- **Three distributions validated end-to-end on physical hardware** (UEFI, NVIDIA
  RTX 5070, dual-monitor): Fedora KDE, Linux Mint, and Debian 13. Per distro:
  unattended installation, dual-boot preservation, NVIDIA driver install, per-monitor
  display layout (resolution, refresh rate, rotation), keyboard layout, wallpaper,
  and file migration.
- The validation round produced two documented root-cause fixes that generalize
  beyond iGloo: a dnf dependency chain (`kernel-devel-matched`) that silently pulls
  a newer kernel during akmods driver installs, and EDID serial collisions across
  identical monitor models that break display matching.
- Ubuntu: pipeline complete, parked on one documented installer defect
  (see `distros/ubuntu/STATUS.md`).
- Version 0.1-alpha ships as a Windows installer built by a one-command,
  fully scripted pipeline.
- Sole developer: Gilles D'huyvetter (Flanders, Belgium).

## What funding buys (12 months)

| Milestone | Outcome |
|---|---|
| M1 | Hardware-matrix beta: 50+ real machines (OEM firmware, Secure Boot, BitLocker edge cases); Ubuntu validated |
| M2 | End-user polish: branded boot menu, migration-report welcome screen, wizard localization (NL/FR/DE) |
| M3 | Safety hardening: pre-install disk snapshot/rollback, telemetry-free failure reporting |
| M4 | 1.0 release + distro partnerships (Mint, Zorin, Tuxedo) as distribution channels |

Requested: **€50,000** (NGI Zero scale): 12 months focused development + test hardware.

## Fit

- **Sovereign Tech Fund:** critical migration infrastructure for public-sector
  Linux adoption.
- **NLnet / NGI Zero Commons:** user autonomy, migration off proprietary platforms,
  open source, EU-based maintainer.
- **VLAIO (ontwikkelingsproject):** Flemish solo founder, innovative software product
  with clear market timing.

---
*Contact: open an issue or discussion at github.com/gillesduif/iGloo*
