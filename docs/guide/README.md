# iGloo — Step-by-step visual guide

This folder holds the click-by-click walkthrough of a full migration, used by the
main README and (later) the website. Until all captures exist, this file doubles
as the **shot list**: capture these during any normal VM test run.

## Capture conventions

- PNG for stills, `docs/guide/assets/NN-name.png` (numbered = ordered).
- GIFs only where motion matters (progress, reboot handoff); keep under ~8 MB.
- 1920×1080 VM display, 100% scaling, default Windows theme.
- **Redact:** real names, Wi-Fi SSIDs, serial numbers. Use a test user
  ("Alex Janssens") in demos.
- Windows-side: `Win+Shift+S` or ShareX (ShareX records GIFs too).
  Installer/Linux-side: VM host screenshot (VMware: `Ctrl+Alt+PrtScn` or the
  Capture menu — the guest can't screenshot its own boot).

## Shot list

### Act 1 — In Windows (the wizard)
| # | Capture | What it must show |
|---|---------|-------------------|
| 01 | `01-welcome.png` | First screen, "Leave the ice age" branding |
| 02 | `02-preflight.png` | Pre-flight checks all green (UEFI, disk space, BitLocker) |
| 03 | `03-distro-catalog.png` | Distro picker with all four distros visible |
| 04 | `04-migration-setup.png` | Folder selection + suggested Linux apps (checkboxes) |
| 05 | `05-account.png` | Username/password/keyboard/locale page |
| 06 | `06-download-verify.gif` | Download progress → "Verifying GPG signature…" moment |
| 07 | `07-staging.png` | Partition/staging progress page |
| 08 | `08-ready-to-reboot.png` | Final "ready" page before reboot |

### Act 2 — The unattended install (nothing to do — that's the point)
| # | Capture | What it must show |
|---|---------|-------------------|
| 09 | `09-grub-menu.png` | GRUB with "Install <distro> (iGloo)" entry |
| 10 | `10-installer-running.png` | Unattended installer mid-run, zero dialogs waiting |
| 11 | `11-first-boot-wait.png` | First boot: the pause before login (agent working) |

### Act 3 — Landing (the payoff)
| # | Capture | What it must show |
|---|---------|-------------------|
| 12 | `12-grub-dualboot.png` | GRUB showing BOTH Linux and Windows Boot Manager |
| 13 | `13-desktop-files.png` | File manager open on Documents/Pictures — the user's files |
| 14 | `14-wifi.png` | Wi-Fi menu showing known networks already present |
| 15 | `15-apps.png` | App menu with the migrated app selections installed |
| 16 | `16-windows-still-boots.png` | Booted back into Windows: untouched |

### Hero GIF
`00-hero.gif` — the whole journey compressed to ~20 s: wizard clicks → reboot →
installer timelapse → login → files present. Assemble from the stills/clips
above (ScreenToGif or ffmpeg). This is the top-of-README asset.

## Assembly

Once captures exist, this README becomes the guide itself: one H2 per act,
each image followed by one or two plain-language sentences ("iGloo now checks
your computer. Green means go."). Written for someone who has never installed
an operating system — no jargon, no acronyms without explanation.
