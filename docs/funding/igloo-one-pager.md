# iGloo — Leave the Ice Age

**One-click migration from Windows to Linux, for people who have never seen a terminal.**

---

## The problem

Windows 10 support ended on 14 October 2025. An estimated **240 million PCs** cannot
upgrade to Windows 11 (Canalys) — perfectly working hardware that is now unsupported:
a security problem, an e-waste problem, and a digital-sovereignty problem the EU is
actively trying to solve (see Schleswig-Holstein's public-sector Linux migration).

Linux is the obvious answer, but the migration itself is the barrier. Today it demands
a USB stick, BIOS settings, partitioning decisions, and the confidence to risk your
family photos. That excludes exactly the people who need it most.

## The solution

iGloo is a Windows application. The user downloads it, answers a few questions
(which folders to keep, dual-boot or replace), and clicks install. iGloo:

- shrinks the Windows partition and installs Linux **without a USB stick** —
  directly from Windows, no boot media, no BIOS knowledge;
- verifies every downloaded ISO against **pinned GPG signatures** (own trust store,
  hardened against keyserver substitution);
- migrates the user's **files, Wi-Fi profiles, and browser data** automatically;
- on first boot, installs **GPU drivers (incl. NVIDIA), media codecs, and keyboard/locale**
  before the user ever sees the desktop — it lands feeling like *their* machine.

Windows stays untouched next to it (dual-boot) until the user chooses otherwise.

## Why previous attempts failed — and why this one is different

Ubuntu's Wubi and Mint's mint4win proved demand in the 2000s but died under the
maintenance burden of BIOS-era quirks. Two things changed: **UEFI standardised the
boot process**, and iGloo's architecture is built for the chaos that remains — each
distro is an isolated plugin (boot spec, installer config, first-boot agent), so a
quirk in one distro never destabilises the others. The hard, unglamorous problems
(Secure Boot, NVIDIA, OEM firmware, installer preseeding) are the product.

## Status (July 2026)

- **Fedora KDE: validated end-to-end on real hardware** — dual-boot beside Windows,
  NVIDIA RTX driver install, OneDrive/file migration, Wi-Fi handover.
- **Debian: validated end-to-end in VM.** Linux Mint: final validation in progress.
  Ubuntu: in development. All three share one Debian-family first-boot agent.
- Architecture: .NET/WPF app, distro-plugin model, no-USB direct-install pipeline
  (partition staging + initrd config injection + UEFI boot registration).
- Sole developer: Gilles D'huyvetter (Flanders, Belgium).

## What funding buys (12 months)

| Milestone | Outcome |
|---|---|
| M1 | Mint + Ubuntu validated; public open-source release (trust requires auditability) |
| M2 | Hardware-matrix beta: 50+ real machines (OEM firmware, Secure Boot, BitLocker edge cases) |
| M3 | Safety hardening: pre-install disk snapshot/rollback, telemetry-free failure reporting |
| M4 | 1.0 release + distro partnerships (Mint, Zorin, Tuxedo) as distribution channels |

Requested: **€50,000** (NGI Zero scale) — 12 months focused development + test hardware.

## Fit

- **NLnet / NGI Zero Commons:** user autonomy, migration off proprietary platforms,
  open source, EU-based maintainer.
- **Sovereign Tech Fund:** critical migration infrastructure for public-sector
  Linux adoption.
- **VLAIO (ontwikkelingsproject):** Flemish solo founder, innovative software product
  with clear market timing.

---
*Contact: Gilles D'huyvetter · ipadireview@gmail.com · github.com/gillesduif/iGloo*
