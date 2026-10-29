# Distributions

iGloo migrates a Windows PC to a Linux distribution chosen from a catalog. Each catalog entry is a self-contained plugin under `distros/<id>/`. A plugin declares:

- the installer to boot;
- the unattended installer configuration;
- the preflight checks specific to that distribution;
- the first-boot agent that finishes the migration.

Entries are either **available** (installable) or **coming soon** (shown in the picker with a badge, not selectable).

## Bazzite DX

**Who it is for:** developers who also play games. Bazzite DX is Universal Blue's Bazzite gaming system with the KDE Plasma desktop plus developer tools: VS Code, Docker and Podman, distrobox, libvirt and Homebrew. It is image-based. The system is read-only, and each update replaces the whole system image, so a bad update can be rolled back.

The plain **Bazzite** entry is a separate catalog entry and remains *coming soon*.

### Requirements and preflight

| Condition | Result |
|---|---|
| Legacy BIOS boot | **Blocked**: Bazzite only installs in UEFI mode. |
| Secure Boot on | **Warning**: at the first reboot a blue *MOK management* screen asks the user to enroll the Universal Blue key once (*Enroll MOK* → *Continue* → *Yes*, password `universalblue`). Alternatively, turn Secure Boot off before installing. |
| NVIDIA GPU | **Info**: the NVIDIA edition of Bazzite DX is installed, with NVIDIA's open driver built in (GeForce GTX 16-series, RTX 20-series and newer). |
| BitLocker locked | **Blocked**, as for every distribution. |
| Less than 8 GiB RAM | **Warning**: the install proceeds. |

Recommended disk space: 64 GiB.

### Installation behaviour

- **Network required during the install.** The system image is downloaded while installing. The installer joins the first Wi-Fi network saved on Windows that is in range; a wired connection needs no setup.
- **Unattended.** Language, keyboard, timezone, user account and password come from the Windows side. No setup wizard is shown.
- **Hostname.** The Windows computer name, cut to 19 characters. Bazzite renames longer hostnames to `bazzite`.
- **Disk.** Same rules as every distribution. Dual-boot installs only into the space iGloo freed by shrinking Windows; replace mode wipes the selected disk. The Linux root is a plain XFS partition.
- **Graphics.** The image is chosen from the GPU: the NVIDIA edition for NVIDIA cards, the standard edition otherwise. No driver is installed after boot, so the first session already runs on the right driver.
- **Signed updates.** Once installed, every system update is checked against Universal Blue's signature.
- **Login.** The user logs in with their password at the Plasma login screen. Bazzite DX's default passwordless autologin into Steam Gaming Mode is turned off. Gaming Mode stays available from the session list.

### What is migrated

The same data as on other distributions:

- user folders (Documents, Pictures, …, including OneDrive-redirected ones);
- Firefox / Zen / Waterfox profiles;
- Chromium-family bookmarks, history, open tabs and cookies, plus saved passwords, imported at first login after the user re-enters their password;
- saved Wi-Fi networks;
- display layout, wallpaper and account picture.

### First boot

Before the login screen appears, the first-boot agent:

1. Confirms the system follows the signed Bazzite DX image.
2. Adds the user to the `docker`, `incus-admin` and `libvirt` groups, so containers and virtual machines work without `sudo`.
3. In dual-boot mode, adds Windows to the boot menu and keeps the menu visible at every boot.
4. Keeps Linux first in the UEFI boot order, re-checked at every boot.
5. Installs the apps Bazzite normally ships (Firefox, Okular, Gwenview, Haruna, Flatseal, Warehouse, ProtonPlus, …), then the Windows apps the user chose to replace, **from Flathub only**. Native packages are not layered onto the read-only system. Any that have no Flatpak are listed in the log, to install with Homebrew or in a distrobox.
6. Applies the migrated settings and browser profiles, removes plaintext secrets from the migration manifest, exports the logs to the installer partition, then deletes iGloo's temporary installer partitions.

Logs: `/var/log/igloo/` on Linux. They are also copied to the `igloo-logs` folder of the installer partition, which Windows can read.

### Known limitations

- Not yet validated on physical hardware.
- GNOME editions of Bazzite DX are not offered.
- NVIDIA cards older than the GTX 16-series receive the NVIDIA edition, whose driver does not support them.

---
## Versionning and reference
*This document summarizes product features completed through these evolutions docs:*

- [f-001 - Bazzite DX](../specs/f001-bazzite-DX.md) : Bazzite DX becomes an installable distribution. It installs unattended from the Fedora netinstall, uses the NVIDIA image for NVIDIA GPUs, enforces signed updates, logs in with a password and migrates the same data as Fedora KDE.
