#!/usr/bin/env python3
"""
Igloo first-boot migration agent for the Debian family (Debian / Ubuntu / Mint).

One agent serves all three: it detects the distro at runtime from
/etc/os-release and picks the right driver/codec method. Reads the migration
manifest (copied to /var/lib/igloo/manifest.json by the preseed/autoinstall
late command) and applies post-install configuration:

  - apt update; enable extra components (handled in the installer config)
  - Install GPU drivers      (ubuntu-drivers on Ubuntu/Mint, non-free nvidia on Debian)
  - Install codecs           (ubuntu-restricted-extras / equivalents)
  - Register the Flathub remote and install suggested Flatpaks
  - Write NetworkManager Wi-Fi profiles from the manifest
  - Migrate the user's files from the Windows NTFS partition

Unlike Fedora (whose %post copies user files inside the installer), the Debian
family does the NTFS copy HERE: debian-installer/subiquity run in a minimal
environment without ntfs-3g, whereas the installed system has full tooling.
The Windows partition is still present for dual-boot, so first boot can read it.
"""
from __future__ import annotations

import argparse
import json
import logging
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path
from typing import Any

logger = logging.getLogger("igloo.agent")

APT_ENV = {"DEBIAN_FRONTEND": "noninteractive"}


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def run_cmd(cmd: list[str], *, check: bool = True, timeout: int = 600,
            env: dict[str, str] | None = None) -> subprocess.CompletedProcess:
    """Run a command, log its output, and optionally raise on failure."""
    merged_env = {**os.environ, **(env or {})}
    logger.info("Running: %s", " ".join(cmd))
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, env=merged_env)
    for line in (result.stdout or "").strip().splitlines():
        logger.debug("  stdout: %s", line)
    for line in (result.stderr or "").strip().splitlines():
        logger.debug("  stderr: %s", line)
    if check and result.returncode != 0:
        raise RuntimeError(f"Command failed (exit {result.returncode}): {' '.join(cmd)}\n"
                           f"stderr: {result.stderr.strip()}")
    return result


def os_release() -> dict[str, str]:
    """Parse /etc/os-release into a dict (ID, ID_LIKE, VERSION_ID, …)."""
    data: dict[str, str] = {}
    try:
        for line in Path("/etc/os-release").read_text().splitlines():
            if "=" in line and not line.startswith("#"):
                k, v = line.split("=", 1)
                data[k.strip()] = v.strip().strip('"')
    except Exception:
        logger.exception("Could not read /etc/os-release")
    return data


def distro_id() -> str:
    """Return the distro id: 'ubuntu', 'debian', or 'linuxmint'."""
    return os_release().get("ID", "debian").lower()


def is_ubuntu_like() -> bool:
    """True for Ubuntu and its derivatives (Mint), which have ubuntu-drivers + multiverse."""
    info = os_release()
    return info.get("ID", "").lower() in ("ubuntu", "linuxmint") \
        or "ubuntu" in info.get("ID_LIKE", "").lower()


def apt(args: list[str], *, timeout: int = 600, check: bool = True) -> subprocess.CompletedProcess:
    return run_cmd(["apt-get", "-y", *args], timeout=timeout, check=check, env=APT_ENV)


# ---------------------------------------------------------------------------
# Migration steps
# ---------------------------------------------------------------------------

def apt_update(manifest: dict[str, Any]) -> None:
    """Refresh the package lists before any install step."""
    apt(["update"], timeout=300, check=False)
    logger.info("apt package lists refreshed")


def secure_boot_enabled(manifest: dict[str, Any]) -> bool:
    """Whether UEFI Secure Boot is active on this machine.

    Read from the running system first (the user may have turned it off in firmware
    after Igloo collected the manifest on Windows, which is a common thing to do
    precisely because of the driver problem below). The manifest value is the
    fallback. On total uncertainty we answer True: assuming Secure Boot is ON leads
    to installing a SIGNED driver, which works either way, whereas assuming it is
    OFF can leave a Secure Boot machine with a module the kernel refuses to load.
    """
    res = run_cmd(["mokutil", "--sb-state"], check=False, timeout=60)
    out = (res.stdout or "") + (res.stderr or "")
    if "disabled" in out.lower():
        return False
    if "enabled" in out.lower():
        return True

    # mokutil absent: read the EFI variable directly. Its last byte is the flag.
    for var in Path("/sys/firmware/efi/efivars").glob("SecureBoot-*"):
        try:
            return var.read_bytes()[-1] == 1
        except OSError:
            pass

    manifest_value = manifest.get("hardware", {}).get("secureBootEnabled")
    if isinstance(manifest_value, bool):
        logger.info("Secure Boot state read from the manifest: %s", manifest_value)
        return manifest_value

    logger.warning("Could not determine the Secure Boot state - assuming ENABLED "
                   "(installing a signed driver is safe either way)")
    return True


def install_nvidia_driver_ubuntu(manifest: dict[str, Any]) -> None:
    """Install the NVIDIA driver on Ubuntu/Mint, honouring Secure Boot.

    The right package depends on whether Secure Boot is on, because the two goals
    conflict:

    * Secure Boot ON - the module must be signed by a key the firmware trusts.
      `ubuntu-drivers install` defaults to Canonical's PRE-BUILT SIGNED modules
      (linux-modules-nvidia-*), which load with Secure Boot enabled and need no MOK
      enrollment. Installing an "-open" package by hand instead would pull a DKMS
      build - compiled locally, signed with no trusted key - which Secure Boot then
      refuses to load. That is the "nvidia ... FAILED" + flashing cursor failure.

    * Secure Boot OFF - signing is irrelevant, so we can pick the variant the
      hardware actually needs. NVIDIA's proprietary kernel module does not support
      Blackwell (RTX 50 series) at all, so those cards require an "-open" build or
      they get no driver and fall back to software rendering.

    `ubuntu-drivers devices` only lists drivers applicable to the DETECTED GPU, so
    an "-open" entry appearing there means the open module covers this card. The
    highest version is taken deliberately: Ubuntu's 570-open packaging is known
    broken, while 580-open is the branch that works on RTX 50 series.
    """
    apt(["install", "ubuntu-drivers-common"], timeout=300, check=False)

    listing = (run_cmd(["ubuntu-drivers", "devices"], check=False, timeout=300).stdout or "")
    for line in listing.splitlines():
        if line.strip():
            logger.info("  ubuntu-drivers: %s", line.strip())

    if secure_boot_enabled(manifest):
        logger.info("Secure Boot is ENABLED - installing Canonical's pre-built signed driver "
                    "via ubuntu-drivers (a locally built DKMS module would not load)")
        run_cmd(["ubuntu-drivers", "install"], timeout=1800, check=False, env=APT_ENV)
        _log_nvidia_module_state(manifest)
        return

    open_versions = sorted({int(m) for m in re.findall(r"nvidia-driver-(\d+)-open\b", listing)})
    if open_versions:
        pkg = f"nvidia-driver-{open_versions[-1]}-open"
        logger.info("Secure Boot is disabled - installing %s (open kernel module; "
                    "required by RTX 50 series)", pkg)
        if apt(["install", pkg], timeout=1800, check=False).returncode == 0:
            _log_nvidia_module_state(manifest)
            return
        logger.warning("%s failed to install - falling back to ubuntu-drivers autoinstall", pkg)
    else:
        logger.info("No open-module driver offered for this GPU - using ubuntu-drivers autoinstall")

    run_cmd(["ubuntu-drivers", "autoinstall"], timeout=1800, check=False, env=APT_ENV)
    _log_nvidia_module_state(manifest)


def _log_nvidia_module_state(manifest: dict[str, Any] | None = None) -> None:
    """Record whether a kernel module actually landed AND whether it can load.

    A package-manager exit code of 0 only means packages unpacked; it says nothing
    about a DKMS module having built, nor about the kernel being willing to load it.
    Secure Boot rejects locally built (unsigned) modules at load time, which shows up
    as a red "nvidia ... FAILED" during boot and a desktop stuck on software
    rendering - with nothing in the install log to explain it. Naming that here is
    the difference between a five-minute fix and days of guessing.
    """
    present = run_cmd(["bash", "-c",
                       "ls /lib/modules/$(uname -r)/updates/dkms/nvidia*.ko* 2>/dev/null "
                       "|| ls /lib/modules/$(uname -r)/kernel/drivers/video/nvidia*.ko* 2>/dev/null "
                       "|| modinfo -n nvidia 2>/dev/null"], check=False)
    found = (present.stdout or "").strip()

    if not found:
        logger.error("No NVIDIA kernel module found after install - this GPU will run on the "
                     "fallback framebuffer (software rendering, wrong resolution)")
        return

    logger.info("NVIDIA kernel module present: %s", found.splitlines()[0])

    # Present is not the same as loadable. Try it, and if Secure Boot is what stands
    # in the way, say so explicitly rather than leaving a generic failure.
    if run_cmd(["modprobe", "nvidia"], check=False, timeout=120).returncode == 0:
        logger.info("NVIDIA kernel module loaded successfully")
        return

    if manifest is not None and secure_boot_enabled(manifest):
        logger.error(
            "NVIDIA module is installed but the kernel REFUSED TO LOAD IT, and Secure Boot "
            "is enabled. Secure Boot only loads modules signed by a trusted key, and this "
            "one was built on this machine. Fix: turn Secure Boot off in the firmware "
            "settings, or enrol a Machine Owner Key (MOK) and sign the module.")
    else:
        logger.error("NVIDIA module is installed but failed to load - the GPU will run on "
                     "the fallback framebuffer")


def _debian_packaged_driver_supports_gpu() -> bool:
    """Ask Debian's own nvidia-detect whether the archive has a driver for this card.

    Debian stable's packaged NVIDIA driver is 550.x. Blackwell (RTX 50 series) needs
    570 or newer, so on those cards every Debian-archive driver - including
    backports - simply does not support the GPU, and installing one leaves the
    machine with no acceleration and a fallback-framebuffer resolution.

    nvidia-detect is Debian's own tool for this question, so the answer comes from
    the distribution rather than a hardcoded model list: it prints a line like
    "Your card is not supported by any driver version up to 550.163.01" when the
    card is too new. Anything unexpected is treated as "supported" so the normal
    packaged path stays the default.
    """
    apt(["install", "nvidia-detect"], timeout=300, check=False)
    if shutil.which("nvidia-detect") is None:
        logger.info("nvidia-detect unavailable - assuming the packaged driver is fine")
        return True

    out = (run_cmd(["nvidia-detect"], check=False, timeout=120).stdout or "")
    for line in out.splitlines():
        logger.info("  nvidia-detect: %s", line.strip())
    unsupported = re.search(r"not supported by any driver version", out, re.IGNORECASE)
    if unsupported:
        logger.warning("Debian's packaged NVIDIA driver does not support this GPU")
        return False
    return True


def _add_nvidia_upstream_repo() -> bool:
    """Register NVIDIA's official Debian repository (the one with current drivers).

    Debian's archive lags well behind NVIDIA upstream; this repo is where the
    drivers new enough for recent GPUs actually live. Keyring package first, so apt
    verifies signatures normally instead of trusting an unsigned source.
    """
    codename = os_release().get("VERSION_ID", "13").split(".")[0]
    repo = f"debian{codename}"
    url = (f"https://developer.download.nvidia.com/compute/cuda/repos/"
           f"{repo}/x86_64/cuda-keyring_1.1-1_all.deb")
    deb = "/tmp/cuda-keyring.deb"

    logger.info("Adding NVIDIA's official repository for %s", repo)
    if run_cmd(["curl", "-fsSL", "-o", deb, url], check=False, timeout=300).returncode != 0:
        logger.error("Could not download the NVIDIA repository keyring from %s", url)
        return False
    if run_cmd(["dpkg", "-i", deb], check=False, timeout=120).returncode != 0:
        logger.error("Could not install the NVIDIA repository keyring")
        return False
    apt(["update"], timeout=300, check=False)
    return True


def install_nvidia_driver_debian(manifest: dict[str, Any]) -> None:
    """Install an NVIDIA driver that actually supports this GPU on Debian.

    Prefers Debian's packaged driver (integrated, signed, maintained by Debian).
    Falls back to NVIDIA's official repository only when the packaged driver is too
    old for the card - the situation on RTX 50 series, where Debian ships 550 and
    the GPU needs 570+.

    The fallback installs the **open** kernel module (nvidia-open): Blackwell has no
    proprietary kernel module at all, and the `cuda-drivers` metapackage in that
    repo is known to fail on Debian, so nvidia-open is the package that works.
    """
    if _debian_packaged_driver_supports_gpu():
        logger.info("Installing NVIDIA driver from Debian non-free")
        apt(["install", "nvidia-driver", "firmware-misc-nonfree"], timeout=1200, check=False)
        return

    logger.info("GPU is newer than Debian's packaged driver - using NVIDIA's official repository")
    apt(["install", "firmware-misc-nonfree"], timeout=600, check=False)
    if not _add_nvidia_upstream_repo():
        logger.error("Falling back to Debian's packaged driver; this GPU will likely "
                     "run without acceleration until a newer driver is installed")
        apt(["install", "nvidia-driver", "firmware-misc-nonfree"], timeout=1200, check=False)
        return

    # nvidia-open pulls the matching userspace; the DKMS module builds against the
    # installed kernel headers, so make sure those are present first.
    apt(["install", f"linux-headers-{os.uname().release}"], timeout=600, check=False)
    if apt(["install", "nvidia-open"], timeout=1800, check=False).returncode != 0:
        logger.warning("nvidia-open not available under that name - trying cuda-drivers")
        apt(["install", "cuda-drivers"], timeout=1800, check=False)

    _log_nvidia_module_state(manifest)


def install_gpu_drivers(manifest: dict[str, Any]) -> None:
    """Install the NVIDIA driver if the GPU is NVIDIA."""
    gpu = manifest.get("hardware", {}).get("gpuVendor", "").lower()
    if gpu != "nvidia":
        logger.info("GPU driver: vendor=%r, skipping NVIDIA step", gpu)
        return

    if is_ubuntu_like():
        install_nvidia_driver_ubuntu(manifest)
    else:
        install_nvidia_driver_debian(manifest)

    # The driver blacklists nouveau and builds a new initramfs that only takes
    # effect next boot. Reboot once before the display manager so the first real
    # session comes up cleanly (matches the Fedora behaviour).
    try:
        Path("/var/lib/igloo/.reboot-required").write_text("nvidia-driver-installed\n", encoding="utf-8")
        logger.info("Flagged reboot-required (NVIDIA driver needs a clean boot)")
    except Exception:
        logger.exception("Could not write reboot-required marker (non-fatal)")
    logger.info("NVIDIA driver installed")


def install_codecs(manifest: dict[str, Any]) -> None:
    """Install multimedia codecs / restricted extras."""
    if not manifest.get("hardware", {}).get("needsNonFreeCodecs", True):
        logger.info("Codecs: needsNonFreeCodecs=false, skipping")
        return
    if distro_id() == "linuxmint":
        # Mint ships its own codec metapackage  exactly what its first-run
        # "Install Multimedia Codecs" applet installs.
        apt(["install", "mint-meta-codecs"], timeout=900, check=False)
    elif is_ubuntu_like():
        # EULA-bearing packages need this debconf pre-answer to stay unattended.
        run_cmd(["bash", "-c",
                 "echo ttf-mscorefonts-installer msttcorefonts/accepted-mscorefonts-eula "
                 "select true | debconf-set-selections"], check=False, env=APT_ENV)
        apt(["install", "ubuntu-restricted-extras"], timeout=900, check=False)
    else:
        apt(["install", "libavcodec-extra", "gstreamer1.0-libav",
             "gstreamer1.0-plugins-ugly", "gstreamer1.0-plugins-bad"], timeout=600, check=False)
    logger.info("Multimedia codecs installed")


def ensure_firmware(manifest: dict[str, Any]) -> None:
    """Make sure linux-firmware is present so Wi-Fi/GPU firmware is available.

    The Debian-family analogue of Fedora's kernel-modules self-heal: an
    incomplete netinstall over flaky Wi-Fi can leave firmware packages missing,
    which strands the installed system without a usable wireless device.
    """
    pkg = "linux-firmware" if is_ubuntu_like() else "firmware-linux"
    apt(["install", pkg], timeout=600, check=False)
    logger.info("Ensured firmware package present: %s", pkg)


def enable_os_prober(manifest: dict[str, Any]) -> None:
    """Make Windows appear in the GRUB dual-boot menu.

    GRUB 2.06+ disables os-prober by default (GRUB_DISABLE_OS_PROBER=true), so a
    fresh dual-boot install shows no Windows entry. Enable it and regenerate
    grub.cfg on the booted system, where the Windows partition is fully visible
    (more reliable than detecting it from the installer chroot).
    """
    grub_default = Path("/etc/default/grub")
    try:
        text = grub_default.read_text() if grub_default.exists() else ""
        if "GRUB_DISABLE_OS_PROBER=false" not in text:
            with grub_default.open("a", encoding="utf-8") as f:
                f.write("\nGRUB_DISABLE_OS_PROBER=false\n")
            logger.info("Set GRUB_DISABLE_OS_PROBER=false")
    except Exception:
        logger.exception("Could not edit /etc/default/grub (non-fatal)")

    apt(["install", "os-prober"], timeout=300, check=False)
    if shutil.which("update-grub"):
        run_cmd(["update-grub"], check=False, timeout=300)
    else:
        run_cmd(["grub-mkconfig", "-o", "/boot/grub/grub.cfg"], check=False, timeout=300)
    logger.info("Regenerated GRUB (Windows entry added if a Windows install was found)")


def setup_flathub(manifest: dict[str, Any]) -> None:
    """Install Flatpak (if needed) and register the Flathub remote."""
    if shutil.which("flatpak") is None:
        apt(["install", "flatpak"], timeout=300, check=False)
    run_cmd(["flatpak", "remote-add", "--if-not-exists", "--system",
             "flathub", "https://dl.flathub.org/repo/flathub.flatpakrepo"],
            timeout=120, check=False)
    logger.info("Flathub remote ready")


def install_suggested_packages(manifest: dict[str, Any]) -> None:
    """Install auto-install packages from the manifest (Flatpak + native apt)."""
    pkgs = [p for p in manifest.get("suggestedPackages", []) if p.get("autoInstall")]
    if not pkgs:
        logger.info("No auto-install packages in manifest")
        return

    flatpak_ids = [p["flatpakId"] for p in pkgs if p.get("flatpakId")]
    apt_pkgs = [p["nativePackage"] for p in pkgs if p.get("nativePackage")]

    if flatpak_ids:
        logger.info("Installing Flatpaks: %s", ", ".join(flatpak_ids))
        run_cmd(["flatpak", "install", "-y", "--noninteractive", "flathub", *flatpak_ids],
                timeout=900, check=False)
    if apt_pkgs:
        logger.info("Installing apt packages: %s", ", ".join(apt_pkgs))
        apt(["install", *apt_pkgs], timeout=600, check=False)
    logger.info("Suggested packages installed")


#   User-file migration from the Windows NTFS partition

def ensure_migration_tools(manifest: dict[str, Any]) -> None:
    """Make sure the tools the file-migration step needs are installed.

    On the offline (squashfs) install these may be absent from the live image:
    ntfs-3g mounts the Windows partition and rsync does the copy. Install any that
    are missing now that the network is up (a no-op on images that already ship
    them, and on the netinst-era path where the preseed pulled them in).
    """
    missing = [pkg for pkg, cmd in (("ntfs-3g", "ntfs-3g"), ("rsync", "rsync"))
               if shutil.which(cmd) is None]
    if missing:
        apt(["install", *missing], timeout=600, check=False)
        logger.info("Installed migration tools: %s", ", ".join(missing))
    else:
        logger.info("Migration tools already present")


def _find_windows_home(win_username: str) -> Path | None:
    """Mount NTFS partitions read-only until one with Users/<win_username> is found."""
    mnt = Path("/mnt/igloo_ntfs")
    mnt.mkdir(parents=True, exist_ok=True)

    blk = run_cmd(["lsblk", "-rno", "NAME,FSTYPE"], check=False)
    for line in (blk.stdout or "").splitlines():
        parts = line.split()
        if len(parts) < 2 or parts[1].lower() != "ntfs":
            continue
        dev = f"/dev/{parts[0]}"
        # ntfs-3g read-only; ignore devices that fail to mount.
        if run_cmd(["mount", "-t", "ntfs-3g", "-o", "ro", dev, str(mnt)], check=False).returncode != 0:
            continue
        home = mnt / "Users" / win_username
        if home.is_dir():
            logger.info("Found Windows home on %s: %s", dev, home)
            return home
        run_cmd(["umount", str(mnt)], check=False)
    logger.warning("No Windows home for user %r found on any NTFS partition", win_username)
    return None


def _copy_tree(src: Path, dst: Path) -> None:
    dst.mkdir(parents=True, exist_ok=True)
    # --no-links: skip Windows junctions (My Music/Pictures/Videos, OneDrive links).
    #   ntfs-3g exposes them as symlinks into the temporary NTFS mount, which would
    #   dangle once the agent unmounts it. Real files are copied; the junk links are not.
    # --exclude: Windows-only folder metadata that's meaningless on Linux.
    # rsync tolerates unreadable OneDrive placeholders without aborting the run.
    run_cmd(["rsync", "-a", "--no-links", "--no-perms", "--chmod=ugo=rwX",
             "--exclude=desktop.ini", "--exclude=Thumbs.db",
             f"{src}/", f"{dst}/"], check=False, timeout=3600)


def migrate_user_files(manifest: dict[str, Any]) -> None:
    """Copy the user's selected folders + browser profiles from Windows NTFS."""
    user = manifest.get("user", {})
    linux_user = user.get("preferredLinuxUsername", "")
    win_user = user.get("windowsUsername", "")
    if not linux_user or not win_user:
        logger.info("Missing user names in manifest  skipping file migration")
        return

    user_home = Path("/home") / linux_user
    if not user_home.is_dir():
        logger.warning("User home %s does not exist  skipping file migration", user_home)
        return

    win_home = _find_windows_home(win_user)
    if win_home is None:
        return

    try:
        # Folders: {name, sourceRelativePath} resolved on the Windows side (Known
        # Folder API), so OneDrive-redirected folders carry their real location.
        for folder in manifest.get("files", {}).get("folders", []):
            name = folder.get("name")
            rel = folder.get("sourceRelativePath") or name
            if not name:
                continue
            src = win_home / rel
            if not src.is_dir():
                logger.info("  Skipping folder %s (source %r not found)", name, rel)
                continue
            _copy_tree(src, user_home / name)
            logger.info("  Copied folder %s <- %s", name, rel)

        # Gecko browser profiles: {sourceRelativePath, destRelativePath}.
        for br in manifest.get("browsers", []):
            src_rel = br.get("sourceRelativePath")
            dst_rel = br.get("destRelativePath")
            if not src_rel or not dst_rel:
                continue
            src = win_home / src_rel
            if not src.is_dir():
                logger.info("  Skipping browser profile %s (source not found)", src_rel)
                continue
            _copy_tree(src, user_home / dst_rel)
            logger.info("  Copied browser profile %s -> %s", src_rel, dst_rel)

        # Fix ownership of everything we just dropped in.
        run_cmd(["chown", "-R", f"{linux_user}:{linux_user}", str(user_home)], check=False)
        logger.info("User-file migration complete")
    finally:
        run_cmd(["umount", "/mnt/igloo_ntfs"], check=False)


#   Wi-Fi (NetworkManager keyfiles)  distro-agnostic, reused from Fedora   

def set_user_password(manifest: dict[str, Any]) -> None:
    """Guarantee the user's password is set, via chpasswd, from the manifest.

    The preseed/autoinstall already sets it, but Debian-family *plaintext* password
    preseeding (passwd/user-password) is unreliable across releases  the account is
    created but the password sometimes doesn't take, so the user can't log in.
    Re-applying it here (as root, on first boot, before the display manager) makes it
    deterministic. Runs before redact-manifest, while the plaintext is still present.
    """
    user = manifest.get("user", {})
    username = (user.get("preferredLinuxUsername") or "").strip()
    password = user.get("linuxPassword")
    if not username or not password:
        logger.info("No username/password in manifest - skipping password set")
        return
    proc = subprocess.run(
        ["chpasswd"], input=f"{username}:{password}\n", text=True, capture_output=True
    )
    if proc.returncode == 0:
        logger.info("Password (re)set for user %r", username)
    else:
        logger.error("chpasswd failed for %r: %s", username, (proc.stderr or "").strip())


def set_keyboard(manifest: dict[str, Any]) -> None:
    """Apply the user's keyboard layout from the manifest.

    The preseed sets keyboard-configuration keys, but Ubiquity (Mint) and the
    casper live session don't reliably honour them, so the installed desktop can
    end up on the default (US) layout.

    `localectl set-x11-keymap` is a NO-OP on the entire Debian family: localed
    replies "Setting X11 and console keymaps is not supported in Debian" because
    Debian's keyboard-configuration package owns the config. The authoritative
    file is /etc/default/keyboard  the greeter and every new X/Wayland session
    read it. It is rewritten here, then dpkg-reconfigure/setupcon apply it to
    the console without waiting for a reboot.
    """
    keymap = (manifest.get("user", {}).get("keymap") or "").strip()
    if not keymap:
        logger.info("No keymap in manifest - skipping keyboard set")
        return

    kb_file = Path("/etc/default/keyboard")
    settings = {"XKBMODEL": "pc105", "XKBLAYOUT": keymap,
                "XKBVARIANT": "", "XKBOPTIONS": "", "BACKSPACE": "guess"}
    try:
        if kb_file.exists():
            for line in kb_file.read_text().splitlines():
                key, sep, val = line.partition("=")
                key = key.strip()
                # keep whatever model/options the installer chose; force the layout
                if sep and key in settings and key not in ("XKBLAYOUT", "XKBVARIANT"):
                    settings[key] = val.strip().strip('"')
        kb_file.write_text(
            "# KEYBOARD CONFIGURATION FILE\n"
            "# Consult the keyboard(5) manual page.\n\n"
            + "".join(f'{k}="{v}"\n' for k, v in settings.items()))
        logger.info("Wrote %s with XKBLAYOUT=%r", kb_file, keymap)
    except OSError as exc:
        logger.warning("Could not write %s: %s", kb_file, exc)
        return

    run_cmd(["dpkg-reconfigure", "-f", "noninteractive", "keyboard-configuration"],
            check=False)
    run_cmd(["setupcon", "--save"], check=False)
    logger.info("Keyboard layout set to %r", keymap)


def _nm_keyfile(ssid: str, security: str, psk: str | None, hidden: bool) -> str:
    lines = ["[connection]", f"id={ssid}", f"uuid={uuid.uuid4()}", "type=wifi",
             "autoconnect=true", "", "[wifi]", "mode=infrastructure", f"ssid={ssid}"]
    if hidden:
        lines.append("hidden=true")
    if security == "wpa-psk" and psk:
        lines += ["", "[wifi-security]", "key-mgmt=wpa-psk", f"psk={psk}"]
    lines += ["", "[ipv4]", "method=auto", "", "[ipv6]", "method=auto", ""]
    return "\n".join(lines)


def _safe_filename(ssid: str) -> str:
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", ssid).strip("_")
    return (safe or "wifi") + ".nmconnection"


def migrate_wifi(manifest: dict[str, Any]) -> None:
    nets = manifest.get("wifiNetworks", [])
    if not nets:
        logger.info("No Wi-Fi networks in manifest")
        return
    sysconn = Path("/etc/NetworkManager/system-connections")
    sysconn.mkdir(parents=True, exist_ok=True)
    written = 0
    for net in nets:
        ssid = (net.get("ssid") or "").strip()
        if not ssid:
            continue
        security = net.get("security", "wpa-psk")
        if security == "unsupported":
            logger.info("Skipping enterprise/unsupported network %r", ssid)
            continue
        psk = net.get("psk")
        if security == "wpa-psk" and not psk:
            logger.info("Skipping %r: WPA-PSK network but no key recovered", ssid)
            continue
        path = sysconn / _safe_filename(ssid)
        try:
            path.write_text(_nm_keyfile(ssid, security, psk, bool(net.get("hidden", False))), encoding="utf-8")
            path.chmod(0o600)   # NetworkManager refuses world-readable keyfiles
            written += 1
            logger.info("Wrote NetworkManager profile for %r (%s)", ssid, security)
        except Exception:
            logger.exception("Failed to write Wi-Fi profile for %r (non-fatal)", ssid)
    if written:
        run_cmd(["nmcli", "connection", "reload"], check=False, timeout=60)
    logger.info("Wi-Fi migration complete: %d profile(s) written", written)


def wait_for_network(manifest: dict[str, Any]) -> None:
    """Block (bounded) until the network is up before the apt-dependent steps run.

    On the offline (squashfs) install there is NO network until migrate_wifi  run
    just before this  writes the NetworkManager profiles and NM autoconnects. On a
    wired machine the link is already up, so this returns almost immediately. It is
    best-effort with a hard timeout: if nothing comes up (e.g. a wrong Wi-Fi key)
    we proceed anyway and the network-dependent steps degrade gracefully (they all
    run with check=False).
    """
    if shutil.which("nm-online"):
        # -s waits for NetworkManager to finish starting (all autoconnect attempts);
        # -t bounds the wait.
        run_cmd(["nm-online", "-s", "-t", "120"], check=False, timeout=140)
    else:
        for _ in range(24):  # ~120 s
            if run_cmd(["ping", "-c", "1", "-W", "2", "deb.debian.org"],
                       check=False, timeout=5).returncode == 0:
                break
            time.sleep(5)
    logger.info("Network wait complete")


# === BEGIN AGENT SECTION =================================================
# ---------------------------------------------------------------------------
# Chromium credential import (browser migration Phase 2, ADR-011)
#
# KEEP IN SYNC between distros/_debian-family/agent/agent.py and
# distros/fedora-kde/agent/agent.py (same convention as the Wi-Fi section).
#
# The Windows side decrypts the selected Chromium browsers' saved passwords
# (DPAPI unlocks only in the user's logon session) and stages them inside the
# manifest as browsers[].credentialsBlob: base64 of the envelope
#     "IGCRD001" | salt(16) | nonce(12) | AES-256-GCM(ciphertext)||tag(16)
# keyed by PBKDF2-HMAC-SHA256(linuxPassword, salt, 600_000 iterations).
# This step runs BEFORE redact-manifest, while the plaintext Linux password
# is still present, decrypts the envelope, and inserts the rows into the
# Linux browser's Login Data using Chromium's Linux "v10" encoding.
#
# Everything below is pure stdlib on purpose: the Debian offline first boot
# has no network and cannot install python3-cryptography. The embedded
# pure-Python AES is gated by self_test() against published known-answer
# vectors (FIPS-197, NIST SP 800-38A, McGrew-Viega GCM); any failure disables
# the step rather than risking malformed output on real credentials.
# Credential values are never logged.
# ---------------------------------------------------------------------------

import base64
import hashlib
import hmac as hmac_mod
import json
import os
import sqlite3
import time
import urllib.parse
from pathlib import Path


def _gf_mul(a: int, b: int) -> int:
    """Multiply in GF(2^8) modulo the Rijndael polynomial x^8+x^4+x^3+x+1."""
    p = 0
    for _ in range(8):
        if b & 1:
            p ^= a
        carry = a & 0x80
        a = (a << 1) & 0xFF
        if carry:
            a ^= 0x1B
        b >>= 1
    return p


def _gf_pow(a: int, n: int) -> int:
    r = 1
    while n:
        if n & 1:
            r = _gf_mul(r, a)
        a = _gf_mul(a, a)
        n >>= 1
    return r


def _rotl8(x: int, n: int) -> int:
    return ((x << n) | (x >> (8 - n))) & 0xFF


def _build_sbox() -> list[int]:
    # S-box computed from its definition (GF(2^8) inverse + affine transform):
    # no 256-entry table that could be corrupted in transcription.
    box = []
    for x in range(256):
        inv = 0 if x == 0 else _gf_pow(x, 254)
        box.append(inv ^ _rotl8(inv, 1) ^ _rotl8(inv, 2)
                   ^ _rotl8(inv, 3) ^ _rotl8(inv, 4) ^ 0x63)
    return box


_SBOX = _build_sbox()


def _expand_key(key: bytes) -> list[list[int]]:
    """FIPS-197 key schedule. Returns Nr+1 round keys, each 16 bytes."""
    nk = len(key) // 4
    if nk not in (4, 8):
        raise ValueError("only AES-128 and AES-256 keys are supported")
    nr = nk + 6
    w = [list(key[4 * i:4 * i + 4]) for i in range(nk)]
    rcon = 1
    for i in range(nk, 4 * (nr + 1)):
        t = list(w[i - 1])
        if i % nk == 0:
            t = [_SBOX[t[1]] ^ rcon, _SBOX[t[2]], _SBOX[t[3]], _SBOX[t[0]]]
            rcon = _gf_mul(rcon, 2)
        elif nk == 8 and i % nk == 4:
            t = [_SBOX[b] for b in t]
        w.append([w[i - nk][j] ^ t[j] for j in range(4)])
    return [[b for word in w[4 * r:4 * r + 4] for b in word]
            for r in range(nr + 1)]


def _add_round_key(s: list[int], rk: list[int]) -> list[int]:
    return [s[i] ^ rk[i] for i in range(16)]


def _shift_rows(s: list[int]) -> list[int]:
    # Column-major flat state s[4*c + r]: row r rotates left by r columns.
    return [s[4 * ((c + r) % 4) + r] for c in range(4) for r in range(4)]


def _mix_columns(s: list[int]) -> list[int]:
    out = [0] * 16
    for c in range(4):
        a0, a1, a2, a3 = s[4 * c], s[4 * c + 1], s[4 * c + 2], s[4 * c + 3]
        out[4 * c] = _gf_mul(a0, 2) ^ _gf_mul(a1, 3) ^ a2 ^ a3
        out[4 * c + 1] = a0 ^ _gf_mul(a1, 2) ^ _gf_mul(a2, 3) ^ a3
        out[4 * c + 2] = a0 ^ a1 ^ _gf_mul(a2, 2) ^ _gf_mul(a3, 3)
        out[4 * c + 3] = _gf_mul(a0, 3) ^ a1 ^ a2 ^ _gf_mul(a3, 2)
    return out


def aes_encrypt_block(key: bytes, block: bytes) -> bytes:
    """Encrypt one 16-byte block with AES-128 or AES-256."""
    if len(block) != 16:
        raise ValueError("AES operates on 16-byte blocks")
    rks = _expand_key(key)
    nr = len(rks) - 1
    s = _add_round_key(list(block), rks[0])
    for rnd in range(1, nr):
        s = _mix_columns(_shift_rows([_SBOX[b] for b in s]))
        s = _add_round_key(s, rks[rnd])
    s = _shift_rows([_SBOX[b] for b in s])
    return bytes(_add_round_key(s, rks[nr]))


def aes_cbc_encrypt(key: bytes, iv: bytes, data: bytes) -> bytes:
    """AES-CBC with PKCS#7 padding (Chromium Linux "v10" password encoding)."""
    if len(iv) != 16:
        raise ValueError("CBC IV must be 16 bytes")
    pad = 16 - (len(data) % 16)
    data = data + bytes([pad]) * pad
    out = bytearray()
    prev = iv
    for off in range(0, len(data), 16):
        block = bytes(data[off + i] ^ prev[i] for i in range(16))
        prev = aes_encrypt_block(key, block)
        out += prev
    return bytes(out)


def _gcm_gf_mul(x: int, y: int) -> int:
    """Multiply in GF(2^128) with the GCM bit ordering."""
    r = 0xE1000000000000000000000000000000
    z = 0
    v = y
    for i in range(128):
        if (x >> (127 - i)) & 1:
            z ^= v
        v = (v >> 1) ^ (r if v & 1 else 0)
    return z


def _gcm_ghash(h: int, data: bytes) -> int:
    y = 0
    for off in range(0, len(data), 16):
        block = data[off:off + 16]
        y = _gcm_gf_mul(y ^ int.from_bytes(block, "big"), h)
    return y


def _inc32(ctr: bytes) -> bytes:
    head, tail = ctr[:12], int.from_bytes(ctr[12:], "big")
    return head + ((tail + 1) & 0xFFFFFFFF).to_bytes(4, "big")


def _gcm_gctr(key: bytes, icb: bytes, data: bytes) -> bytes:
    out = bytearray()
    ctr = icb
    for off in range(0, len(data), 16):
        block = data[off:off + 16]
        ks = aes_encrypt_block(key, ctr)
        out += bytes(block[i] ^ ks[i] for i in range(len(block)))
        ctr = _inc32(ctr)
    return bytes(out)


def _pad16(b: bytes) -> bytes:
    return b + b"\x00" * ((-len(b)) % 16)


def aes_gcm_decrypt(key: bytes, nonce: bytes, ct_and_tag: bytes,
                    aad: bytes = b"") -> bytes:
    """Decrypt AES-GCM; raises ValueError on tag mismatch. 96-bit nonces."""
    if len(nonce) != 12:
        raise ValueError("GCM nonce must be 12 bytes")
    if len(ct_and_tag) < 16:
        raise ValueError("GCM input shorter than the tag")
    ct, tag = ct_and_tag[:-16], ct_and_tag[-16:]
    h = int.from_bytes(aes_encrypt_block(key, b"\x00" * 16), "big")
    j0 = nonce + b"\x00\x00\x00\x01"
    lens = (len(aad) * 8).to_bytes(8, "big") + (len(ct) * 8).to_bytes(8, "big")
    s = _gcm_ghash(h, _pad16(aad) + _pad16(ct) + lens)
    expected = _gcm_gctr(key, j0, s.to_bytes(16, "big"))
    if not hmac_mod.compare_digest(expected, tag):
        raise ValueError("GCM tag mismatch (wrong key or corrupted data)")
    return _gcm_gctr(key, _inc32(j0), ct)


def _aes_self_test() -> None:
    """Known-answer tests; must pass before any real credential is touched."""
    # FIPS-197 Appendix C: AES-128 and AES-256 block vectors.
    pt = bytes.fromhex("00112233445566778899aabbccddeeff")
    assert aes_encrypt_block(
        bytes.fromhex("000102030405060708090a0b0c0d0e0f"), pt
    ).hex() == "69c4e0d86a7b0430d8cdb78070b4c55a", "AES-128 block KAT failed"
    assert aes_encrypt_block(
        bytes.fromhex("000102030405060708090a0b0c0d0e0f"
                      "101112131415161718191a1b1c1d1e1f"), pt
    ).hex() == "8ea2b7ca516745bfeafc49904b496089", "AES-256 block KAT failed"

    # NIST SP 800-38A F.2.1: CBC-AES128, first block.
    ct1 = aes_cbc_encrypt(
        bytes.fromhex("2b7e151628aed2a6abf7158809cf4f3c"),
        bytes.fromhex("000102030405060708090a0b0c0d0e0f"),
        bytes.fromhex("6bc1bee22e409f96e93d7e117393172a01"))  # 17B: 1B pad
    assert ct1[:16].hex() == "7649abac8119b246cee98e9b12e9197d", \
        "AES-CBC KAT failed"

    # McGrew & Viega GCM test case 2 (AES-128, no AAD), decrypt direction:
    # the tag must verify and the plaintext must come back exactly.
    k128 = b"\x00" * 16
    iv = b"\x00" * 12
    known_ct = bytes.fromhex("0388dace60b6a392f328c2b971b2fe78"
                             "ab6e47d42cec13bdf53a67b21257bddf")
    assert aes_gcm_decrypt(k128, iv, known_ct) == b"\x00" * 16, \
        "GCM-128 decrypt KAT failed"
    bad = bytearray(known_ct)
    bad[0] ^= 1
    try:
        aes_gcm_decrypt(k128, iv, bytes(bad))
        raise AssertionError("GCM accepted a corrupted ciphertext")
    except ValueError:
        pass

    # GCM test case 5 (AES-256, with AAD), from the GCM revised spec.
    k256 = bytes.fromhex("feffe9928665731c6d6a8f9467308308"
                         "feffe9928665731c6d6a8f9467308308")
    ct5 = bytes.fromhex(
        "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa"
        "8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662"
        "76fc6ece0f4e1768cddf8853bb2d551b")
    pt5 = bytes.fromhex(
        "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a72"
        "1c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39")
    aad5 = bytes.fromhex("feedfacedeadbeeffeedfacedeadbeefabaddad2")
    assert aes_gcm_decrypt(k256, bytes.fromhex("cafebabefacedbaddecaf888"),
                           ct5, aad5) == pt5, "GCM-256 decrypt KAT failed"


_CHROMIUM_ENVELOPE_MAGIC = b"IGCRD001"
_CHROMIUM_PBKDF2_ITERATIONS = 600_000

# Browser display name (as the Windows wizard records it) to the Linux config
# directory relative to the user's home.
_CHROMIUM_LINUX_DIRS = {
    "Google Chrome": ".config/google-chrome",
    "Microsoft Edge": ".config/microsoft-edge",
    "Brave": ".config/BraveSoftware/Brave-Browser",
    "Vivaldi": ".config/vivaldi",
    "Opera": ".config/opera",
}

# Chromium timestamps count microseconds since 1601-01-01 UTC.
_CHROMIUM_EPOCH_OFFSET_US = 11644473600 * 1_000_000

# Classic logins schema. Newer Chromium versions upgrade older databases on
# first launch (password-store migrations only ADD columns), so writing the
# classic form is the compatible choice. Validated in VM testing per
# CONTRIBUTING.md rule 4.
_LOGINS_SCHEMA = """
CREATE TABLE IF NOT EXISTS logins (
    origin_url VARCHAR NOT NULL,
    action_url VARCHAR,
    username_element VARCHAR,
    username_value VARCHAR,
    password_element VARCHAR,
    password_value BLOB,
    submit_element VARCHAR,
    signon_realm VARCHAR NOT NULL,
    date_created INTEGER NOT NULL,
    date_last_used INTEGER NOT NULL DEFAULT 0,
    date_password_modified INTEGER NOT NULL DEFAULT 0,
    blacklisted_by_user INTEGER NOT NULL,
    scheme INTEGER NOT NULL,
    times_used INTEGER NOT NULL DEFAULT 1,
    display_name VARCHAR,
    icon_url VARCHAR,
    federation_url VARCHAR,
    skip_zero_click INTEGER,
    generation_upload_status INTEGER,
    possible_username_pairs BLOB,
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date_synced INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS meta (
    key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY,
    value LONGVARCHAR
);
INSERT OR IGNORE INTO meta(key, value) VALUES ('version', '8');
"""


def _chromium_v10_encrypt(password: str) -> bytes:
    """Encode one password the way Linux Chromium stores it in Login Data:
    "v10" || AES-128-CBC(PKCS7), key PBKDF2-HMAC-SHA1("peanuts", "saltysalt",
    1 iteration), IV of 16 spaces. This scheme is Chromium's documented
    fallback when no desktop keyring holds the key."""
    key = hashlib.pbkdf2_hmac("sha1", b"peanuts", b"saltysalt", 1, 16)
    return b"v10" + aes_cbc_encrypt(key, b" " * 16, password.encode("utf-8"))


def _decrypt_envelope(blob_b64: str, password: str) -> dict:
    raw = base64.b64decode(blob_b64)
    if raw[:8] != _CHROMIUM_ENVELOPE_MAGIC:
        raise ValueError("credential envelope has a bad magic header")
    key = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"),
                              raw[8:24], _CHROMIUM_PBKDF2_ITERATIONS, 32)
    return json.loads(aes_gcm_decrypt(key, raw[24:36], raw[36:]).decode("utf-8"))


def _login_row_values(url: str, username: str, v10_blob: bytes,
                      now_us: int) -> dict:
    """Every column the importer can fill. Columns a given Chromium version
    does not have are dropped by the caller via PRAGMA table_info."""
    parsed = urllib.parse.urlsplit(url)
    signon_realm = f"{parsed.scheme}://{parsed.netloc}/"
    return {
        "origin_url": url,
        "action_url": "",
        "username_element": "",
        "username_value": username,
        "password_element": "",
        "password_value": v10_blob,
        "submit_element": "",
        "signon_realm": signon_realm,
        "date_created": now_us,
        "date_last_used": now_us,
        "date_password_modified": now_us,
        "blacklisted_by_user": 0,
        "scheme": 0,
        "times_used": 1,
        "display_name": "",
        "icon_url": "",
        "federation_url": "",
        "skip_zero_click": 0,
        "generation_upload_status": 0,
        "possible_username_pairs": b"",
        "date_synced": 0,
        # Columns added by newer Chromium versions.
        "moving_blocked_for": b"",
        "sender_name": "",
        "sender_origin": "",
        "sender_profile_image_url": "",
        "date_received": 0,
        "sharing_notification_displayed": 0,
        "keychain_identifier": "",
        "sender_app_id": "",
    }


def _import_into_login_data(db_path: Path, logins: list[dict]) -> int:
    """Insert logins into a Chromium Login Data database. Returns the number
    of rows inserted. Existing (origin_url, username_value) pairs are kept,
    so re-running the agent never duplicates entries."""
    is_new_db = not db_path.exists()
    con = sqlite3.connect(db_path)
    try:
        if is_new_db:
            con.executescript(_LOGINS_SCHEMA)

        # PRAGMA table_info: (cid, name, type, notnull, dflt_value, pk).
        table_cols = {row[1]: row for row in con.execute("PRAGMA table_info(logins)")}
        fillable_probe = _login_row_values("", "", b"", 0)
        required = [name for name, row in table_cols.items()
                    if row[3] and row[4] is None
                    and name not in fillable_probe and name != "id"]
        if required:
            logger.warning("logins table in %s has unsupported NOT NULL columns "
                           "%s - skipping credential import for this browser",
                           db_path, required)
            return 0

        existing = {(r[0], r[1]) for r in con.execute(
            "SELECT origin_url, username_value FROM logins")}

        now_us = int(time.time() * 1_000_000) + _CHROMIUM_EPOCH_OFFSET_US
        inserted = 0
        for login in logins:
            url = login.get("url", "")
            username = login.get("username", "")
            password = login.get("password", "")
            if not url or not username or not password:
                continue
            if (url, username) in existing:
                continue
            values = {k: v for k, v in
                      _login_row_values(url, username,
                                        _chromium_v10_encrypt(password), now_us)
                      .items() if k in table_cols}
            con.execute(
                f"INSERT INTO logins ({', '.join(values)}) "
                f"VALUES ({', '.join('?' * len(values))})",
                tuple(values.values()))
            existing.add((url, username))
            inserted += 1
        con.commit()
        os.chmod(db_path, 0o600)
        return inserted
    finally:
        con.close()


def import_chromium_credentials(manifest: dict) -> None:
    """Migrate staged Chromium credentials into the Linux browsers' Login Data.

    Runs before redact-manifest: the envelope key derives from the plaintext
    linuxPassword, which redaction then erases. Best-effort per browser; a
    failure here must never block first boot."""
    entries = [b for b in manifest.get("browsers", []) if b.get("credentialsBlob")]
    if not entries:
        return

    user = manifest.get("user", {})
    linux_user = (user.get("preferredLinuxUsername") or "").strip()
    password = user.get("linuxPassword")
    if not linux_user or not password:
        logger.info("Chromium credentials staged but no user/password in the "
                    "manifest - skipping credential import")
        return

    try:
        _aes_self_test()
    except AssertionError:
        logger.exception("AES self-test failed - Chromium credential import "
                         "is disabled for this run")
        return

    home = Path("/home") / linux_user
    if not home.is_dir():
        logger.warning("User home %s does not exist - skipping Chromium "
                       "credential import", home)
        return

    for entry in entries:
        name = entry.get("name", "")
        config_rel = _CHROMIUM_LINUX_DIRS.get(name)
        if config_rel is None:
            logger.info("No Linux profile mapping for browser %r - skipping "
                        "credential import", name)
            continue
        try:
            payload = _decrypt_envelope(entry["credentialsBlob"], password)
        except Exception:
            logger.exception("Could not decrypt the credential envelope for "
                             "%s - skipping this browser", name)
            continue

        logins = payload.get("logins", [])
        if not logins:
            continue

        config_dir = home / config_rel
        profile_dir = config_dir / "Default"
        try:
            profile_dir.mkdir(parents=True, exist_ok=True)
            inserted = _import_into_login_data(profile_dir / "Login Data", logins)
            run_cmd(["chown", "-R", f"{linux_user}:{linux_user}",
                     str(config_dir)], check=False)
            logger.info("Imported %d Chromium login(s) for %s", inserted, name)
        except Exception:
            logger.exception("Failed to import Chromium credentials for %s "
                             "(non-fatal)", name)

def redact_manifest(manifest: dict[str, Any]) -> None:
    manifest_path = Path("/var/lib/igloo/manifest.json")
    if not manifest_path.exists():
        return
    try:
        data: dict[str, Any] = json.loads(manifest_path.read_text(encoding="utf-8"))
        changed = False
        if data.get("user", {}).get("linuxPassword") is not None:
            data["user"]["linuxPassword"] = None
            changed = True
        for net in data.get("wifiNetworks", []):
            if net.get("psk") is not None:
                net["psk"] = None
                changed = True
        for br in data.get("browsers", []):
            if br.get("credentialsBlob") is not None:
                br["credentialsBlob"] = None
                changed = True
        if changed:
            manifest_path.write_text(json.dumps(data, indent=2), encoding="utf-8")
            manifest_path.chmod(0o640)
            logger.info("Redacted plaintext secrets from manifest")
    except Exception:
        logger.exception("Failed to redact manifest (non-fatal)")


#   Display layout migration (resolution / refresh rate / rotation / position)

def _edid_identity(edid: bytes) -> dict[str, str] | None:
    """Extract vendor, product code, product name and serial from raw EDID bytes.

    Layout (EDID 1.x): bytes 8-9 hold the manufacturer as three 5-bit letters,
    10-11 the product code (little-endian), 12-15 a numeric serial, and the four
    18-byte descriptors from 0x36 carry the human-readable name (tag 0xFC) and
    serial string (tag 0xFF).
    """
    if len(edid) < 128:
        return None
    raw = (edid[8] << 8) | edid[9]
    vendor = "".join(chr(((raw >> shift) & 0x1F) + 0x40) for shift in (10, 5, 0))
    if not vendor.isalpha():
        return None
    product_code = edid[10] | (edid[11] << 8)
    serial_num = int.from_bytes(edid[12:16], "little")

    name, serial_str = "", ""
    for base in (0x36, 0x48, 0x5A, 0x6C):
        block = edid[base:base + 18]
        if len(block) < 18 or block[0:3] != b"\x00\x00\x00":
            continue
        text = block[5:18].split(b"\n")[0].decode("ascii", "ignore").strip()
        if block[3] == 0xFC:
            name = text
        elif block[3] == 0xFF:
            serial_str = text

    return {
        # Same form Windows reports in its monitor device id, e.g. "GSM5B09" -
        # this is what pairs a Linux connector with a manifest entry.
        "pnp_id": f"{vendor}{product_code:04X}",
        "vendor": vendor,
        "product": name or f"0x{product_code:04X}",
        "serial": serial_str or str(serial_num),
    }


def _connected_outputs() -> list[dict[str, str]]:
    """Every connected DRM connector with its EDID identity (connector name + ids)."""
    outputs = []
    for card in sorted(Path("/sys/class/drm").glob("card*-*")):
        try:
            if (card / "status").read_text().strip() != "connected":
                continue
            edid = (card / "edid").read_bytes()
        except OSError:
            continue
        ident = _edid_identity(edid)
        if not ident:
            continue
        # "card1-DP-1" -> "DP-1", the name compositors use.
        ident["connector"] = card.name.split("-", 1)[1]
        outputs.append(ident)
    return outputs


def _mode_is_supported(connector: str, width: int, height: int) -> bool:
    """Whether the connector advertises this resolution.

    Guard rail: forcing a mode the panel does not advertise is one of the few ways
    this feature could leave the user staring at a black screen, so an unknown mode
    means we leave that output alone rather than gamble.
    """
    modes_file = next(Path("/sys/class/drm").glob(f"card*-{connector}/modes"), None)
    if modes_file is None:
        return True   # cannot tell - do not block on it
    try:
        modes = modes_file.read_text().split()
    except OSError:
        return True
    return f"{width}x{height}" in modes


def migrate_display_layout(manifest: dict[str, Any]) -> None:
    """Reproduce the Windows desktop layout (rotation, refresh rate, position).

    Written as GNOME/Cinnamon's monitors.xml, which Mutter and Muffin both read on
    X11 and Wayland - covering Cinnamon, GNOME and any Mutter-based desktop. KDE
    keeps its own store and is handled separately below.

    Identity is deliberately taken from the LINUX side: the monitorspec is built
    from the connector's own EDID, so it always matches what the compositor reads.
    Only the geometry comes from Windows. Matching the two by PnP id is what keeps a
    two-monitor setup from rotating the wrong screen - display names and ordering
    differ between the systems and are not stable across boots.
    """
    wanted = manifest.get("displays", [])
    if not wanted:
        logger.info("No display layout in the manifest - leaving the desktop defaults")
        return

    outputs = _connected_outputs()
    if not outputs:
        logger.info("No connected outputs with readable EDID - skipping display layout")
        return
    for o in outputs:
        logger.info("Detected output %s: %s (%s)", o["connector"], o["pnp_id"], o["product"])

    by_pnp = {o["pnp_id"]: o for o in outputs}
    logical: list[str] = []
    matched = 0

    for want in wanted:
        pnp = (want.get("pnpId") or "").upper()
        out = by_pnp.get(pnp)
        if out is None:
            logger.info("  Monitor %s from Windows is not attached here - skipped", pnp or "?")
            continue

        width, height = int(want.get("widthPx", 0)), int(want.get("heightPx", 0))
        if width <= 0 or height <= 0:
            continue
        if not _mode_is_supported(out["connector"], width, height):
            logger.warning("  %s does not advertise %dx%d - leaving this output alone",
                           out["connector"], width, height)
            continue

        # Windows reports whole Hz; panels advertise fractional rates (143.998).
        # Mutter tolerates a near match, so the integer value is written as-is.
        rate = int(want.get("refreshHz") or 60) or 60
        rotation = {0: "normal", 90: "right", 180: "inverted", 270: "left"}.get(
            int(want.get("rotationDegrees") or 0), "normal")

        logical.append(
            "    <logicalmonitor>\n"
            f"      <x>{int(want.get('positionX') or 0)}</x>\n"
            f"      <y>{int(want.get('positionY') or 0)}</y>\n"
            "      <scale>1</scale>\n"
            + ("      <primary>yes</primary>\n" if want.get("isPrimary") else "")
            + f"      <transform><rotation>{rotation}</rotation></transform>\n"
            "      <monitor>\n"
            "        <monitorspec>\n"
            f"          <connector>{out['connector']}</connector>\n"
            f"          <vendor>{out['vendor']}</vendor>\n"
            f"          <product>{out['product']}</product>\n"
            f"          <serial>{out['serial']}</serial>\n"
            "        </monitorspec>\n"
            f"        <mode><width>{width}</width><height>{height}</height>"
            f"<rate>{rate}</rate></mode>\n"
            "      </monitor>\n"
            "    </logicalmonitor>\n")
        matched += 1
        logger.info("  %s -> %dx%d@%dHz %s at (%s,%s)", out["connector"], width, height,
                    rate, rotation, want.get("positionX"), want.get("positionY"))

    if matched == 0:
        logger.info("No Windows monitors matched the attached outputs - nothing written")
        return

    xml = ('<monitors version="2">\n  <configuration>\n'
           + "".join(logical) + "  </configuration>\n</monitors>\n")

    username = (manifest.get("user", {}).get("preferredLinuxUsername") or "").strip()
    home = Path("/home") / username if username else None
    if home is None or not home.is_dir():
        logger.warning("User home not found - cannot write the display layout")
        return

    cfg = home / ".config"
    cfg.mkdir(parents=True, exist_ok=True)
    (cfg / "monitors.xml").write_text(xml, encoding="utf-8")
    run_cmd(["chown", "-R", f"{username}:{username}", str(cfg)], check=False)
    logger.info("Wrote %s for %d monitor(s)", cfg / "monitors.xml", matched)

    # The greeter runs as its own user and reads its own copy, so a portrait screen
    # would otherwise still be sideways at the login prompt - the very first thing
    # the user sees. Best-effort: not every display manager uses this path.
    for gdm_dir in (Path("/var/lib/gdm3/.config"), Path("/var/lib/gdm/.config"),
                    Path("/var/lib/lightdm/.config")):
        if gdm_dir.parent.is_dir():
            try:
                gdm_dir.mkdir(parents=True, exist_ok=True)
                (gdm_dir / "monitors.xml").write_text(xml, encoding="utf-8")
                owner = gdm_dir.parent.name
                run_cmd(["chown", "-R", f"{owner}:{owner}", str(gdm_dir)], check=False)
                logger.info("Applied the same layout to the %s greeter", owner)
            except OSError:
                logger.info("Could not write the greeter layout in %s (non-fatal)", gdm_dir)


def install_welcome_app(manifest: dict[str, Any]) -> None:
    autostart_dir = Path("/etc/xdg/autostart")
    autostart_dir.mkdir(parents=True, exist_ok=True)
    script = Path("/opt/igloo/igloo-welcome.sh")
    script.write_text(
        "#!/usr/bin/env bash\n"
        'DONE="$HOME/.igloo-welcome-done"\n'
        '[ -f "$DONE" ] && exit 0\n'
        'notify-send --urgency=normal --expire-time=15000 '
        '"Welcome to Linux!" '
        '"Your files have been migrated from Windows. Logs are in /var/log/igloo/."\n'
        'touch "$DONE"\n',
        encoding="utf-8")
    script.chmod(0o755)
    (autostart_dir / "igloo-welcome.desktop").write_text(
        "[Desktop Entry]\nName=iGloo Welcome\nComment=Migration complete notification\n"
        "Exec=/opt/igloo/igloo-welcome.sh\nIcon=distributor-logo\nTerminal=false\n"
        "Type=Application\nCategories=Utility;\nX-GNOME-Autostart-enabled=true\n"
        "OnlyShowIn=GNOME;KDE;Cinnamon;XFCE;\n",
        encoding="utf-8")
    logger.info("Welcome autostart entry written")


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def configure_logging(log_dir: Path) -> None:
    log_dir.mkdir(parents=True, exist_ok=True)
    fmt = logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s")
    fh = logging.FileHandler(log_dir / "agent.log")
    fh.setFormatter(fmt)
    sh = logging.StreamHandler(sys.stdout)
    sh.setFormatter(fmt)
    root = logging.getLogger()
    root.addHandler(fh)
    root.addHandler(sh)
    root.setLevel(logging.DEBUG)


IGLOO_SEED_LABELS = ("OEMDRV", "CIDATA", "IGLOOISO")


def cleanup_installer_partitions(manifest: dict[str, Any]) -> None:
    """Remove Igloo's temporary installer artifacts from the machine.

    Once this agent has run, the staging partition(s)  OEMDRV/CIDATA with the
    installer config + agent payload (and, for iso-scan/casper distros, the
    multi-gigabyte ISO), plus the dedicated IGLOOISO partition when present 
    serve no further purpose. Leaving them wastes gigabytes and confuses users
    ("what is this OEMDRV drive?"), so the FINAL agent step deletes them, along
    with Igloo's now-dangling one-shot UEFI boot entry.

    Safety rules (BR-01/BR-03, docs/business/business-rules.md):
      * delete ONLY by exact filesystem-label match on Igloo's staging labels 
        never by partition number, position, or size;
      * the partition must sit on the same physical disk as the Linux root;
      * every action is best-effort and logged  any doubt leaves the partition
        in place, never a broken disk.
    The freed space is intentionally left unallocated: it borders the Windows
    partition, so Windows' own Disk Management can extend C: into it.
    """
    # --nofsroot strips the subvolume suffix: without it findmnt returns
    # "/dev/nvme0n1p3[/root]" on a btrfs root, which is not a valid device path 
    # lsblk then fails, `disk` stays empty, and the whole cleanup silently bails,
    # leaving OEMDRV behind to break later installs. Debian/Mint default to ext4
    # (no suffix), but a btrfs install would hit this, so guard it here too.
    src = run_cmd(["findmnt", "-rno", "SOURCE", "--nofsroot", "/"], check=False)
    root_dev = re.sub(r"\[.*\]$", "", (src.stdout or "").strip())
    disk = ""
    if root_dev.startswith("/dev/"):
        r = run_cmd(["lsblk", "-rno", "PKNAME", root_dev], check=False)
        out = (r.stdout or "").strip()
        disk = out.splitlines()[0].strip() if out else ""
    if not disk:
        logger.warning("Could not resolve the root disk - skipping partition cleanup")
        return

    for label in IGLOO_SEED_LABELS:
        by_label = Path("/dev/disk/by-label") / label
        if not by_label.exists():
            continue
        part_dev = os.path.realpath(by_label)
        r = run_cmd(["lsblk", "-rno", "PKNAME", part_dev], check=False)
        parent = (r.stdout or "").strip()
        if parent != disk:
            logger.warning("%s lives on /dev/%s, not root disk /dev/%s - leaving it alone",
                           label, parent, disk)
            continue
        r = run_cmd(["lsblk", "-rno", "PARTN", part_dev], check=False)
        partn = (r.stdout or "").strip()
        if not partn.isdigit():
            m = re.search(r"(\d+)$", part_dev)
            partn = m.group(1) if m else ""
        if not partn:
            logger.warning("Could not determine partition number of %s - skipped", part_dev)
            continue
        run_cmd(["umount", "-A", "-l", part_dev], check=False)
        run_cmd(["sfdisk", "--delete", f"/dev/{disk}", partn], check=False)
        logger.info("Deleted installer partition %s (%s = partition %s on /dev/%s)",
                    label, part_dev, partn, disk)

    # iGloo's one-shot UEFI entry ("iGloo distribution installer") now points at
    # nothing. efibootmgr -B also removes it from BootOrder. Only entries whose
    # description contains "igloo" (case-insensitive, matching the Windows side's
    # BootEntryDescription) are touched; the distro's entry and Windows Boot
    # Manager never are.
    r = run_cmd(["efibootmgr"], check=False)
    for line in (r.stdout or "").splitlines():
        m = re.match(r"^Boot([0-9A-Fa-f]{4})\*?\s+(.*)$", line.strip())
        if m and "igloo" in m.group(2).lower():
            run_cmd(["efibootmgr", "-b", m.group(1), "-B"], check=False)
            logger.info("Removed stale UEFI boot entry Boot%s (%s)", m.group(1), m.group(2))


def main() -> int:
    p = argparse.ArgumentParser(description="Igloo first-boot agent (Debian family)")
    p.add_argument("--manifest", required=True, type=Path)
    p.add_argument("--log-dir", required=True, type=Path)
    args = p.parse_args()
    configure_logging(args.log_dir)

    logger.info("=== Igloo first-boot agent starting (distro: %s) ===", distro_id())
    try:
        manifest: dict[str, Any] = json.loads(args.manifest.read_text(encoding="utf-8"))
    except Exception:
        logger.exception("Failed to load manifest  cannot continue")
        return 1

    if manifest.get("schemaVersion") != 1:
        logger.error("Unsupported manifest schemaVersion: %r", manifest.get("schemaVersion"))
        return 2

    # Order matters on the offline install: Wi-Fi FIRST (nothing else has network
    # until the migrated profiles are up), then wait for the link, THEN the
    # apt/flatpak steps that need it. set-password/keyboard are offline and run
    # first; redact/cleanup run last (redact must see the plaintext Wi-Fi key).
    steps: list[tuple[str, Any]] = [
        ("set-password",     lambda: set_user_password(manifest)),
        ("set-keyboard",     lambda: set_keyboard(manifest)),
        ("wifi",             lambda: migrate_wifi(manifest)),
        ("wait-network",     lambda: wait_for_network(manifest)),
        ("apt-update",       lambda: apt_update(manifest)),
        ("gpu-drivers",      lambda: install_gpu_drivers(manifest)),
        ("codecs",           lambda: install_codecs(manifest)),
        ("firmware",         lambda: ensure_firmware(manifest)),
        ("os-prober",        lambda: enable_os_prober(manifest)),
        ("flathub",          lambda: setup_flathub(manifest)),
        ("suggested-pkgs",   lambda: install_suggested_packages(manifest)),
        ("migration-tools",  lambda: ensure_migration_tools(manifest)),
        ("user-files",       lambda: migrate_user_files(manifest)),
        ("display-layout",   lambda: migrate_display_layout(manifest)),
        ("welcome-app",     lambda: install_welcome_app(manifest)),
        # Chromium credentials: needs the plaintext linuxPassword, so it must
        # run before redact-manifest; needs nothing else, so it stays late.
        ("chromium-creds",  lambda: import_chromium_credentials(manifest)),
        ("redact-manifest",  lambda: redact_manifest(manifest)),
        ("cleanup-seed",     lambda: cleanup_installer_partitions(manifest)),
    ]

    failures: list[str] = []
    for name, step in steps:
        logger.info("--- step: %s ---", name)
        try:
            step()
            logger.info("step %s: OK", name)
        except Exception:
            logger.exception("step %s FAILED", name)
            failures.append(name)

    logger.info("=== Igloo first-boot agent done (%d failure(s)) ===", len(failures))
    return 0


if __name__ == "__main__":
    sys.exit(main())
