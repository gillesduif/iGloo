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
        ("welcome-app",      lambda: install_welcome_app(manifest)),
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
