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
import platform
import re
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

# Shipped alongside this file in /opt/igloo; the script's own directory is first
# on sys.path. Optional: the boot menu is cosmetic and must never take the agent
# down with it.
try:
    import igloo_boot
except ImportError:
    igloo_boot = None
from typing import Any

logger = logging.getLogger("igloo.agent")

# LC_ALL=C keeps apt/network error output in English so failure detection
# ("Failed to fetch", "Could not resolve") is locale-independent.
APT_ENV = {"DEBIAN_FRONTEND": "noninteractive", "LC_ALL": "C"}


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


# Substrings (LC_ALL=C output) that mean the package index is stale or the
# network/DNS is broken. Both are worth one refresh-and-retry before giving up.
_APT_TRANSIENT_MARKERS = ("404", "Failed to fetch", "Could not resolve", "Temporary failure")


def apt_update_once(*, timeout: int = 300) -> bool:
    """Run apt-get update once, returning True if it looks like it succeeded."""
    res = apt(["update"], timeout=timeout, check=False)
    out = ((res.stdout or "") + (res.stderr or ""))
    ok = res.returncode == 0 and not any(m in out for m in ("Failed to fetch", "Could not resolve"))
    if not ok:
        logger.warning("apt-get update incomplete (rc=%d): %s",
                        res.returncode, _last_lines(out))
    return ok


def _last_lines(text: str, count: int = 3) -> str:
    lines = [ln for ln in text.splitlines() if ln.strip()]
    return " | ".join(lines[-count:]) if lines else "(no output)"


def apt_install(args: list[str], *, timeout: int = 900, check: bool = True) -> subprocess.CompletedProcess:
    """apt-get install with one self-healing retry.

    Bare-metal finding (Mint, 2026-07-30): apt fell back to stale indices from
    the install image, every package URL 404'd, and check=False let the agent
    report success while nothing was installed. If the failure looks like a
    stale index or a network hiccup, refresh once and retry before failing.
    """
    res = apt(["install", *args], timeout=timeout, check=False)
    if res.returncode == 0:
        return res
    out = (res.stdout or "") + (res.stderr or "")
    if any(m in out for m in _APT_TRANSIENT_MARKERS):
        logger.warning("apt install hit a transient/stale-index error (%s) - refreshing indices and retrying once",
                        _last_lines(out))
        apt_update_once()
        res = apt(["install", *args], timeout=timeout, check=False)
    if check and res.returncode != 0:
        raise RuntimeError(f"apt-get install {' '.join(args)} failed (rc={res.returncode}): "
                            f"{_last_lines((res.stdout or '') + (res.stderr or ''))}")
    return res


# ---------------------------------------------------------------------------
# Migration steps
# ---------------------------------------------------------------------------

def apt_update(manifest: dict[str, Any]) -> None:
    """Run apt-get update, retrying once if the first attempt fails."""
    for attempt in range(1, 7):
        if apt_update_once():
            logger.info("apt package lists refreshed")
            return
        if attempt < 6:
            logger.info("apt update attempt %d/6 failed - retrying in 10s", attempt)
            time.sleep(10)
    logger.error("apt package lists could NOT be refreshed - indices may be stale; "
                "install steps will self-heal where possible but may fail")


def secure_boot_enabled(manifest: dict[str, Any]) -> bool:
    """Detect whether Secure Boot is enabled in the firmware."""
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
            pass  # expected: try next source (fallback mechanism)

    manifest_value = manifest.get("hardware", {}).get("secureBootEnabled")
    if isinstance(manifest_value, bool):
        logger.info("Secure Boot state read from the manifest: %s", manifest_value)
        return manifest_value

    logger.warning("Could not determine the Secure Boot state - assuming ENABLED "
                    "(installing a signed driver is safe either way)")
    return True


def install_nvidia_driver_ubuntu(manifest: dict[str, Any]) -> bool:
    """Install the NVIDIA driver on Ubuntu/Mint, honouring Secure Boot."""
    apt_install(["ubuntu-drivers-common"], timeout=300, check=False)

    listing = (run_cmd(["ubuntu-drivers", "devices"], check=False, timeout=300).stdout or "")
    for line in listing.splitlines():
        if line.strip():
            logger.info("  ubuntu-drivers: %s", line.strip())

    if secure_boot_enabled(manifest):
        logger.info("Secure Boot is ENABLED - installing Canonical's pre-built signed driver "
                    "via ubuntu-drivers (a locally built DKMS module would not load)")
        run_cmd(["ubuntu-drivers", "install"], timeout=1800, check=False, env=APT_ENV)
        return _log_nvidia_module_state(manifest)

    open_versions = sorted({int(m) for m in re.findall(r"nvidia-driver-(\d+)-open\b", listing)})
    if open_versions:
        pkg = f"nvidia-driver-{open_versions[-1]}-open"
        logger.info("Secure Boot is disabled - installing %s (open kernel module; "
                    "required by RTX 50 series)", pkg)
        if apt_install([pkg], timeout=1800, check=False).returncode == 0:
            return _log_nvidia_module_state(manifest)
        logger.warning("%s failed to install - falling back to ubuntu-drivers autoinstall", pkg)
    else:
        logger.info("No open-module driver offered for this GPU - using ubuntu-drivers autoinstall")

    run_cmd(["ubuntu-drivers", "autoinstall"], timeout=1800, check=False, env=APT_ENV)
    return _log_nvidia_module_state(manifest)


def _log_nvidia_module_state(manifest: dict[str, Any] | None = None) -> bool:
    """Check whether the NVIDIA kernel module is present and loadable."""
    present = run_cmd(["bash", "-c",
                        "ls /lib/modules/$(uname -r)/updates/dkms/nvidia*.ko* 2>/dev/null "
                        "|| ls /lib/modules/$(uname -r)/kernel/drivers/video/nvidia*.ko* 2>/dev/null "
                        "|| modinfo -n nvidia 2>/dev/null"], check=False)
    found = (present.stdout or "").strip()

    if not found:
        logger.error("No NVIDIA kernel module found after install - this GPU will run on the fallback framebuffer (software rendering, wrong resolution)")
        return False

    logger.info("NVIDIA kernel module present: %s", found.splitlines()[0])


    if run_cmd(["modprobe", "nvidia"], check=False, timeout=120).returncode == 0:
        logger.info("NVIDIA kernel module loaded successfully")
        return True

    if manifest is not None and secure_boot_enabled(manifest):
        logger.error(
            "NVIDIA module is installed but the kernel REFUSED TO LOAD IT and Secure Boot is enabled. Secure Boot only loads modules signed by a trusted key, and this one was built on this machine. Fix: turn Secure Boot off in the firmware "
            "settings or enrol a Machine Owner Key (MOK) and sign the module.")
    else:
        logger.error("NVIDIA module is installed but failed to load - the GPU will run on "
                    "the fallback framebuffer")
    return True


def _debian_packaged_driver_supports_gpu() -> bool:
    """Check whether Debian's packaged nvidia-driver supports the detected GPU."""
    apt_install(["nvidia-detect"], timeout=300, check=False)
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
    """Add NVIDIA's official CUDA repository to get the latest driver."""
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


def install_nvidia_driver_debian(manifest: dict[str, Any]) -> bool:
    """Install the NVIDIA driver on Debian, honouring Secure Boot."""
    if _debian_packaged_driver_supports_gpu():
        logger.info("Installing NVIDIA driver from Debian non-free")
        apt_install(["nvidia-driver", "firmware-misc-nonfree"], timeout=1200, check=False)
        return _log_nvidia_module_state(manifest)

    logger.info("GPU is newer than Debian's packaged driver - using NVIDIA's official repository")
    apt_install(["firmware-misc-nonfree"], timeout=600, check=False)
    if not _add_nvidia_upstream_repo():
        logger.error("Falling back to Debian's packaged driver; this GPU will likely "
                     "run without acceleration until a newer driver is installed")
        apt_install(["nvidia-driver", "firmware-misc-nonfree"], timeout=1200, check=False)
        return _log_nvidia_module_state(manifest)

    # nvidia-open pulls the matching userspace; the DKMS module builds against the
    # installed kernel headers, so make sure those are present first.
    apt_install([f"linux-headers-{platform.release()}"], timeout=600, check=False)
    if apt_install(["nvidia-open"], timeout=1800, check=False).returncode != 0:
        logger.warning("nvidia-open not available under that name - trying cuda-drivers")
        apt_install(["cuda-drivers"], timeout=1800, check=False)

    return _log_nvidia_module_state(manifest)


def install_gpu_drivers(manifest: dict[str, Any]) -> None:
    """Install the NVIDIA driver if the GPU is NVIDIA."""
    gpu = manifest.get("hardware", {}).get("gpuVendor", "").lower()
    if gpu != "nvidia":
        logger.info("GPU driver: vendor=%r, skipping NVIDIA step", gpu)
        return

    if is_ubuntu_like():
        module_found = install_nvidia_driver_ubuntu(manifest)
    else:
        module_found = install_nvidia_driver_debian(manifest)

    # Bare-metal finding (Mint, 2026-07-30): a stale apt index made every package
    # 404, check=False swallowed it, and the agent reported "gpu-drivers: OK"
    # while the machine booted into software rendering. No module = this step
    # FAILED, and the summary must say so.
    if not module_found:
        raise RuntimeError(
            "NVIDIA driver install finished but no kernel module exists - the GPU "
            "would boot into software rendering. Check the apt output above for "
            "404/DNS failures or a DKMS build error.")

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
        # Mint ships its own codec metapackage  exactly what its first-run  "Install Multimedia Codecs" applet installs.
        apt_install(["mint-meta-codecs"], timeout=900, check=False)
    elif is_ubuntu_like():
        # EULA-bearing packages need this debconf pre-answer to stay unattended.
        run_cmd(["bash", "-c",
                "echo ttf-mscorefonts-installer msttcorefonts/accepted-mscorefonts-eula "
                "select true | debconf-set-selections"], check=False, env=APT_ENV)
        apt_install(["ubuntu-restricted-extras"], timeout=900, check=False)
    else:
        apt_install(["libavcodec-extra", "gstreamer1.0-libav",
            "gstreamer1.0-plugins-ugly", "gstreamer1.0-plugins-bad"], timeout=600, check=False)
    logger.info("Multimedia codecs installed")


def ensure_firmware(manifest: dict[str, Any]) -> None:
    """Make sure the firmware package is present (Debian: firmware-linux, Ubuntu: linux-firmware)."""
    pkg = "linux-firmware" if is_ubuntu_like() else "firmware-linux"
    apt_install([pkg], timeout=600, check=False)
    logger.info("Ensured firmware package present: %s", pkg)


def enable_os_prober(manifest: dict[str, Any]) -> None:
    """Enable os-prober so GRUB finds Windows and other OSes on the next update."""
    grub_default = Path("/etc/default/grub")
    try:
        text = grub_default.read_text() if grub_default.exists() else ""
        if "GRUB_DISABLE_OS_PROBER=false" not in text:
            with grub_default.open("a", encoding="utf-8") as f:
                f.write("\nGRUB_DISABLE_OS_PROBER=false\n")
            logger.info("Set GRUB_DISABLE_OS_PROBER=false")
    except Exception:
        logger.exception("Could not edit /etc/default/grub (non-fatal)")

    apt_install(["os-prober"], timeout=300, check=False)
    if shutil.which("update-grub"):
        run_cmd(["update-grub"], check=False, timeout=300)
    else:
        run_cmd(["grub-mkconfig", "-o", "/boot/grub/grub.cfg"], check=False, timeout=300)
    logger.info("Regenerated GRUB (Windows entry added if a Windows install was found)")


_INITRAMFS_CONF = Path("/etc/initramfs-tools/initramfs.conf")


def _shrink_initramfs() -> None:
    """Rebuild the initrd with MODULES=dep so GRUB has far less to read.

    GRUB reads the initrd through EFI Block I/O in 4 KB chunks, so the boot
    stalls in proportion to its size: ~50 s for a stock ~80 MB initrd on the
    reference machine. MODULES=dep ships only the drivers this hardware needs.

    Restores MODULES=most and regenerates when the new image is missing, empty
    or not smaller - a machine that cannot mount its own root is unbootable.
    """
    if not _INITRAMFS_CONF.exists():
        logger.info("initramfs-tools not present - leaving the initrd alone")
        return
    text = _INITRAMFS_CONF.read_text(encoding="utf-8")
    if re.search(r"^MODULES=dep\s*$", text, re.MULTILINE):
        logger.info("initrd already builds with MODULES=dep")
        return

    img = Path(f"/boot/initrd.img-{platform.release()}")
    before = img.stat().st_size if img.exists() else 0
    original = text

    patched, count = re.subn(r"^MODULES=.*$", "MODULES=dep", text, count=1, flags=re.MULTILINE)
    if count == 0:
        patched = text.rstrip("\n") + "\nMODULES=dep\n"
    _INITRAMFS_CONF.write_text(patched, encoding="utf-8")

    res = run_cmd(["update-initramfs", "-u"], check=False, timeout=600)
    after = img.stat().st_size if img.exists() else 0
    if res.returncode != 0 or after == 0 or (before and after >= before):
        logger.error("initrd rebuild failed or did not shrink (%d -> %d bytes) - reverting",
                     before, after)
        _INITRAMFS_CONF.write_text(original, encoding="utf-8")
        run_cmd(["update-initramfs", "-u"], check=False, timeout=600)
        return
    logger.info("initrd rebuilt with MODULES=dep: %d -> %d bytes (%d%% smaller)",
                before, after, 100 - (after * 100 // before) if before else 0)



#   Boot menu (M15): implementation lives in _shared/agent/igloo_boot.py

def configure_boot_menu(manifest: dict[str, Any]) -> None:
    """Theme the menu, boot the last-used OS by default, rename the Windows entry."""
    _shrink_initramfs()
    if igloo_boot is None:
        logger.error("igloo_boot.py not staged in /opt/igloo - the menu stays stock")
        return
    igloo_boot.configure_boot_menu(manifest, igloo_boot.debian_family(run_cmd, logger))


def setup_flathub(manifest: dict[str, Any]) -> None:
    """Install Flatpak (if needed) and register the Flathub remote."""
    if shutil.which("flatpak") is None:
        apt_install(["flatpak"], timeout=300, check=False)
    run_cmd(["flatpak", "remote-add", "--if-not-exists", "--system", "flathub", "https://dl.flathub.org/repo/flathub.flatpakrepo"],
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
        apt_install([*apt_pkgs], timeout=600, check=False)
    logger.info("Suggested packages installed")


#   User-file migration from the Windows NTFS partition

def ensure_migration_tools(manifest: dict[str, Any]) -> None:
    """Make sure ntfs-3g and rsync are present for the migration step."""
    missing = [pkg for pkg, cmd in (("ntfs-3g", "ntfs-3g"), ("rsync", "rsync"))
    if shutil.which(cmd) is None]
    if missing:
        apt_install([*missing], timeout=600, check=False)
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
    run_cmd(["rsync", "-a", "--no-links", "--no-perms", "--chmod=ugo=rwX",
    "--exclude=desktop.ini", "--exclude=Thumbs.db",
    f"{src}/", f"{dst}/"], check=False, timeout=3600)


#   Gecko browser profiles

# KEEP IN SYNC between distros/_debian-family/agent/agent.py and
# distros/fedora-kde/agent/agent.py (same convention as the Wi-Fi section).
_GECKO_PROFILE_ROOTS = (".mozilla/firefox", ".zen", ".waterfox")

# A Gecko profile lives under $HOME, and Flatpak gives an app without host
# filesystem access its own $HOME - so a Flatpak build never reads ~/.mozilla.
_GECKO_FLATPAKS = {
    ".mozilla/firefox": "org.mozilla.firefox",
    ".zen": "app.zen_browser.zen",
    ".waterfox": "net.waterfox.waterfox",
}


def _flatpak_installed(app_id: str) -> bool:
    if shutil.which("flatpak") is None:
        return False
    return run_cmd(["flatpak", "info", app_id], check=False).returncode == 0


def _flatpak_home(app_id: str, user_home: Path) -> Path | None:
    """The private $HOME Flatpak gives this app, or None when it sees the real one."""
    res = run_cmd(["flatpak", "info", "--show-permissions", app_id], check=False)
    if res.returncode != 0:
        return None
    for line in (res.stdout or "").splitlines():
        key, sep, value = line.partition("=")
        if sep and key.strip() == "filesystems":
            # "home", "host" and their :ro / :rw forms all mean the real home.
            if any(f.split(":")[0] in ("home", "host") for f in value.split(";")):
                return None
    return user_home / ".var" / "app" / app_id


_FIREFOX_COMMANDS = ("firefox", "firefox-esr")


def _version_major(text: str) -> int | None:
    match = re.search(r"\d+", text or "")
    return int(match.group()) if match else None


def _installed_firefox_major() -> int | None:
    """Major version of the Firefox the distribution ships, if any."""
    for cmd in _FIREFOX_COMMANDS:
        if shutil.which(cmd) is None:
            continue
        major = _version_major(run_cmd([cmd, "--version"], check=False).stdout or "")
        if major is not None:
            return major
    return None


def _profile_firefox_major(root: Path) -> int | None:
    """Newest Firefox that ever opened the copied profile, per compatibility.ini."""
    best: int | None = None
    for depth in ("*/compatibility.ini", "*/*/compatibility.ini"):
        for stamp in root.glob(depth):
            try:
                text = stamp.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            for line in text.splitlines():
                key, sep, value = line.partition("=")
                if sep and key.strip() == "LastVersion":
                    major = _version_major(value)
                    if major is not None and (best is None or major > best):
                        best = major
    return best


def ensure_matching_firefox(user_home: Path) -> None:
    """Add Flathub's Firefox when the distribution's build is older than the profile.

    Debian ships firefox-esr, which trails the Windows release channel by whole
    versions and refuses a profile a newer build has opened. Mint and Fedora ship
    release, so they compare equal and no second Firefox is installed. Must run
    before the version stamps are cleared, since those carry the answer.
    """
    root = user_home / ".mozilla" / "firefox"
    app_id = _GECKO_FLATPAKS[".mozilla/firefox"]
    if not root.is_dir() or _flatpak_installed(app_id):
        return

    profile_major = _profile_firefox_major(root)
    installed_major = _installed_firefox_major()
    if profile_major is None or installed_major is None:
        logger.info("Firefox versions unknown (profile=%s installed=%s) - keeping "
                    "the distribution build", profile_major, installed_major)
        return
    if profile_major <= installed_major:
        logger.info("This distribution ships Firefox %d and the profile is from "
                    "%d - no second build needed", installed_major, profile_major)
        return

    logger.info("Profile is from Firefox %d but this distribution ships %d - "
                "installing %s", profile_major, installed_major, app_id)
    run_cmd(["flatpak", "install", "-y", "--noninteractive", "flathub", app_id],
            check=False, timeout=1800)

    # Only now that it is really there: hiding the packaged launcher before
    # knowing the download worked would leave the user with no browser at all.
    if not _flatpak_installed(app_id):
        logger.warning("%s did not install - keeping the distribution build as it "
                       "is, the profile stays where the packaged Firefox reads it",
                       app_id)
        return
    _hide_packaged_firefox()
    _set_default_browser(user_home, f"{app_id}.desktop")


# The distribution's own Firefox, whose launcher is hidden once the Flathub build
# holds the migrated profile. Debian names it firefox-esr.
_PACKAGED_FIREFOX_DESKTOPS = ("firefox-esr.desktop", "firefox.desktop")


def _hide_packaged_firefox(
        system_dir: Path = Path("/usr/share/applications"),
        target_dir: Path = Path("/usr/local/share/applications")) -> None:
    """Take the distribution's Firefox out of the menu without touching apt.

    Two Firefoxes in the launcher and no way to tell which one holds your data
    is worse than one. This writes a NoDisplay copy into /usr/local/share, which
    XDG_DATA_DIRS ranks above /usr/share, so the package's own file is untouched
    and deleting one override brings the entry back.
    """
    for name in _PACKAGED_FIREFOX_DESKTOPS:
        source = system_dir / name
        if not source.is_file():
            continue
        try:
            sections = _ini_split(source.read_text(encoding="utf-8", errors="replace"))
            out: list[str] = []
            for header, body in sections:
                if header:
                    out.append(header)
                # NoDisplay belongs to [Desktop Entry]; the file's action groups
                # must keep theirs, so this cannot just be appended at the end.
                body = [ln for ln in body
                        if ln.partition("=")[0].strip() != "NoDisplay"]
                out.extend(body)
                if header == "[Desktop Entry]":
                    out.append("NoDisplay=true")
            target_dir.mkdir(parents=True, exist_ok=True)
            (target_dir / name).write_text("\n".join(out).strip() + "\n",
                                           encoding="utf-8")
            logger.info("Hid %s behind a NoDisplay override in %s", name, target_dir)
        except OSError:
            logger.warning("Could not hide %s - the user will see two Firefoxes", name)


def _set_default_browser(user_home: Path, desktop_id: str) -> None:
    """Point the user's http/https handlers at the browser holding their data."""
    handlers = ("x-scheme-handler/http", "x-scheme-handler/https", "text/html")
    path = user_home / ".config" / "mimeapps.list"
    try:
        text = path.read_text(encoding="utf-8", errors="replace") if path.is_file() else ""
        sections = _ini_split(text) if text else [("", [])]

        kept: list[tuple[str, list[str]]] = []
        seen_defaults = False
        for header, body in sections:
            if header == "[Default Applications]":
                seen_defaults = True
                body = [ln for ln in body
                        if ln.partition("=")[0].strip() not in handlers]
                body = [ln for ln in body if ln.strip()]
                body += [f"{h}={desktop_id}" for h in handlers]
            kept.append((header, body))
        if not seen_defaults:
            kept.append(("[Default Applications]",
                         [f"{h}={desktop_id}" for h in handlers]))

        out = "\n".join("\n".join(([h] if h else []) + b) for h, b in kept)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(out.strip() + "\n", encoding="utf-8")
        logger.info("Set %s as the default browser in %s", desktop_id, path)
    except OSError:
        logger.warning("Could not set the default browser - both Firefoxes will "
                       "answer links")


def relocate_gecko_profiles(user_home: Path) -> None:
    """Move each copied profile into the Flatpak home of the browser that reads it.

    Both directories sit in the user's home, so this is a rename, not a second
    copy of a profile that can run to hundreds of megabytes.
    """
    for rel, app_id in _GECKO_FLATPAKS.items():
        src = user_home / rel
        if not src.is_dir() or not _flatpak_installed(app_id):
            continue
        home = _flatpak_home(app_id, user_home)
        if home is None:
            continue
        dst = home / rel
        if dst.exists():
            logger.info("%s already exists - leaving %s where it is", dst, src)
            continue
        try:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(src), str(dst))
            logger.info("Moved %s into the Flatpak home at %s", src, dst)
        except OSError:
            logger.exception("Could not move %s to %s - the Flatpak build will "
                             "start with an empty profile", src, dst)


def _ini_split(text: str) -> list[tuple[str, list[str]]]:
    """Split an INI file into (header, body) pairs; the preamble gets header ""."""
    sections: list[tuple[str, list[str]]] = [("", [])]
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            sections.append((stripped, []))
        else:
            sections[-1][1].append(line)
    return sections


def _ini_get(body: list[str], key: str) -> str:
    for line in body:
        name, sep, value = line.partition("=")
        if sep and name.strip().lower() == key:
            return value.strip().replace("\\", "/")
    return ""


def _normalise_profiles_ini(ini: Path) -> None:
    """Promote the profile Windows was using to the one Linux opens by default."""
    sections = _ini_split(ini.read_text(encoding="utf-8", errors="replace"))

    # [InstallHASH] is keyed on the Windows install directory, so no Linux build
    # ever matches it. Firefox then falls back to the legacy Default=1 flag, which
    # on a Windows profiles.ini usually sits on an old and empty profile.
    wanted = ""
    for header, body in sections:
        if header.lower().startswith("[install"):
            wanted = _ini_get(body, "default") or wanted
    if not wanted:
        logger.info("%s has no install section - left unchanged", ini)
        return

    kept: list[tuple[str, list[str]]] = []
    target = -1
    for header, body in sections:
        low = header.lower()
        # Both sections name Windows paths and mean nothing here.
        if low.startswith("[install") or low.startswith("[backgroundtasksprofiles"):
            continue
        if low.startswith("[profile"):
            body = [ln for ln in body
                    if ln.partition("=")[0].strip().lower() != "default"]
            if _ini_get(body, "path") == wanted:
                target = len(kept)
        kept.append((header, body))

    if target < 0:
        logger.warning("%s names %s but has no matching profile section", ini, wanted)
        return

    header, body = kept[target]
    while body and not body[-1].strip():
        body.pop()
    kept[target] = (header, body + ["Default=1", ""])

    text = "\n".join("\n".join(([h] if h else []) + b) for h, b in kept)
    ini.write_text(text.strip() + "\n", encoding="utf-8")
    logger.info("%s now opens %s by default", ini, wanted)


# Windows paths in compatibility.ini. Firefox reads LastPlatformDir to decide
# whether a profile belongs to the running install; absent means "yes, it does".
_WINDOWS_INSTALL_KEYS = ("LastPlatformDir", "LastAppDir")


def _clear_windows_install_paths(root: Path) -> None:
    """Strip the Windows install paths from every copied compatibility.ini.

    The file itself has to stay. Firefox only adopts a migrated profile when
    compatibility.ini exists and does not name a foreign install directory -
    deleting the whole file makes it start a brand new profile instead, which is
    exactly what happened on the Debian run of 2026-08-21. LastOSABI keeps this
    off genuine Linux profiles, whose paths are already correct.
    """
    for depth in ("*/compatibility.ini", "*/*/compatibility.ini"):
        for stamp in root.glob(depth):
            try:
                text = stamp.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            if "WINNT" not in text:
                continue
            lines = text.splitlines()
            kept = [ln for ln in lines
                    if ln.partition("=")[0].strip() not in _WINDOWS_INSTALL_KEYS]
            if len(kept) == len(lines):
                continue
            stamp.write_text("\n".join(kept).strip() + "\n", encoding="utf-8")
            logger.info("Cleared the Windows install paths in %s", stamp)


_STARTUP_PAGE_PREF = "browser.startup.page"


def _restore_session_on_next_start(root: Path) -> None:
    """Have the migrated profiles reopen the tabs that were open on Windows.

    Written into prefs.js, not user.js: user.js re-applies its value at every
    start, which would stop the user from ever changing this back. Only profiles
    that came from Windows are touched, and only until Firefox rewrites the
    stamp on its first run, so this fires once.
    """
    for depth in ("*/compatibility.ini", "*/*/compatibility.ini"):
        for stamp in root.glob(depth):
            try:
                if "WINNT" not in stamp.read_text(encoding="utf-8", errors="replace"):
                    continue
                prefs = stamp.parent / "prefs.js"
                lines = (prefs.read_text(encoding="utf-8", errors="replace").splitlines()
                         if prefs.is_file() else [])
                kept = [ln for ln in lines
                        if not ln.startswith(f'user_pref("{_STARTUP_PAGE_PREF}"')]
                kept.append(f'user_pref("{_STARTUP_PAGE_PREF}", 3);')
                prefs.write_text("\n".join(kept).strip() + "\n", encoding="utf-8")
                logger.info("Set %s=3 in %s so the migrated tabs reopen",
                            _STARTUP_PAGE_PREF, prefs)
            except OSError:
                logger.warning("Could not set %s in %s - the tabs are there but "
                               "will not reopen on their own",
                               _STARTUP_PAGE_PREF, stamp.parent)


def normalise_gecko_profiles(user_home: Path) -> None:
    """Make the copied Firefox/Zen/Waterfox profiles usable on Linux."""
    homes = [user_home]
    homes += [user_home / ".var" / "app" / a for a in dict.fromkeys(_GECKO_FLATPAKS.values())]
    for home in homes:
        for rel in _GECKO_PROFILE_ROOTS:
            ini = home / rel / "profiles.ini"
            if not ini.is_file():
                continue
            try:
                _normalise_profiles_ini(ini)
                _clear_windows_install_paths(ini.parent)
                _restore_session_on_next_start(ini.parent)
            except OSError:
                logger.warning("Could not normalise %s - the browser may open an "
                               "empty profile", ini)


# Portable files in a Chromium profile: no master key is involved, so they cross
# as they are. Cookies come along verbatim too - the login hook re-encrypts the
# values in place, which leaves the schema a real Chromium's instead of one we
# would have to reconstruct. See docs/reference/browser-migration.md.
_CHROMIUM_PLAIN_FILES = (
    "Bookmarks",
    "History",
    "Favicons",
    "Top Sites",
    "Network/Cookies",
)

# Open tabs. Chromium keeps them in a directory of Session_* / Tabs_* files, not
# in one database, so this half is copied whole. A Gecko browser needs no
# equivalent: its session store sits inside the profile root that already moves.
_CHROMIUM_PLAIN_DIRS = ("Sessions",)


# Where the Fedora kickstart leaves the files it lifted off the Windows
# partition. It runs before any Flatpak exists and so cannot know the
# destination; the agent resolves that once the browsers are installed.
_CHROMIUM_STAGE_DIR = Path("/var/lib/igloo/chromium")


def _place_chromium_files(name: str, src_default: Path, user_home: Path) -> None:
    """Put one profile's portable files where the installed browser will read them."""
    mapping = _CHROMIUM_LINUX_DIRS.get(name)
    if mapping is None:
        logger.info("  No Linux profile mapping for %r - skipping its files", name)
        return
    app_id, config_rel, binaries = mapping

    for root in _chromium_config_homes(app_id, binaries, user_home):
        dst = root / config_rel / "Default"
        copied = []
        for rel in _CHROMIUM_PLAIN_FILES:
            source = src_default / rel
            if not source.is_file():
                continue
            target = dst / rel
            try:
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
                target.chmod(0o600)
                copied.append(rel)
            except OSError:
                logger.warning("  Could not place %s for %s", rel, name)

        for rel in _CHROMIUM_PLAIN_DIRS:
            source = src_default / rel
            if not source.is_dir():
                continue
            try:
                shutil.copytree(source, dst / rel, dirs_exist_ok=True)
                copied.append(f"{rel}/")
            except OSError:
                logger.warning("  Could not place %s for %s", rel, name)

        if copied:
            logger.info("  Placed %s profile files in %s: %s",
                        name, dst, ", ".join(copied))


def _copy_chromium_profile(name: str, src_root: Path, user_home: Path) -> None:
    """Copy one Chromium profile's portable files off the Windows partition."""
    # Default only: a "Profile N" would need an entry in Local State to be
    # reachable, and Local State is machine state we deliberately do not carry.
    src = src_root / "Default"
    if not src.is_dir():
        logger.info("  Skipping %s (no Default profile under %s)", name, src_root)
        return
    _place_chromium_files(name, src, user_home)


def place_staged_chromium_profiles(user_home: Path) -> None:
    """Move the Chromium files an installer staged into the profile that reads them.

    Used by the distributions whose installer, not the agent, reads the Windows
    partition. A no-op everywhere else, since the staging directory only exists
    when something put files in it.
    """
    if not _CHROMIUM_STAGE_DIR.is_dir():
        return
    for staged in sorted(_CHROMIUM_STAGE_DIR.iterdir()):
        if not staged.is_dir():
            continue
        _place_chromium_files(staged.name, staged, user_home)
        shutil.rmtree(staged, ignore_errors=True)


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

        # Browser profiles. Gecko carries {sourceRelativePath, destRelativePath}
        # and moves wholesale; Chromium carries only the source, because its
        # Linux destination depends on whether the browser is a Flatpak.
        for br in manifest.get("browsers", []):
            src_rel = br.get("sourceRelativePath")
            if not src_rel:
                continue
            src = win_home / src_rel
            if not src.is_dir():
                logger.info("  Skipping browser profile %s (source not found)", src_rel)
                continue

            if (br.get("engine") or "").lower() == "chromium":
                _copy_chromium_profile(br.get("name", ""), src, user_home)
                continue

            dst_rel = br.get("destRelativePath")
            if not dst_rel:
                continue
            _copy_tree(src, user_home / dst_rel)
            logger.info("  Copied browser profile %s -> %s", src_rel, dst_rel)

        # Order matters: the version stamps decide whether a second Firefox is
        # needed, the move has to happen before the profiles are rewritten, and
        # normalise clears those stamps last.
        ensure_matching_firefox(user_home)
        relocate_gecko_profiles(user_home)
        normalise_gecko_profiles(user_home)

        # Fix ownership of everything we just dropped in.
        run_cmd(["chown", "-R", f"{linux_user}:{linux_user}", str(user_home)], check=False)
        logger.info("User-file migration complete")
    finally:
        run_cmd(["umount", "/mnt/igloo_ntfs"], check=False)


#   Wi-Fi (NetworkManager keyfiles)  distro-agnostic, reused from Fedora   

def set_user_password(manifest: dict[str, Any]) -> None:
    """Set the user's password from the manifest hash, if present."""
    user = manifest.get("user", {})
    username = (user.get("preferredLinuxUsername") or "").strip()
    crypted = user.get("linuxPasswordCrypted")
    if not username or not crypted:
        logger.info("No username/password hash in manifest - skipping password set")
        return
    proc = subprocess.run(
        # -e takes the $6$ crypt hash as-is, so no plaintext passes through here.
        ["chpasswd", "-e"], input=f"{username}:{crypted}\n", text=True, capture_output=True
    )
    if proc.returncode == 0:
        logger.info("Password (re)set for user %r", username)
    else:
        logger.error("chpasswd failed for %r: %s", username, (proc.stderr or "").strip())


def _ensure_dconf_local_db() -> None:
    """Make sure /etc/dconf/profile/user names the system database."""
    profile = Path("/etc/dconf/profile/user")
    try:
        content = profile.read_text(encoding="utf-8") if profile.exists() else ""
        if "system-db:local" not in content:
            profile.parent.mkdir(parents=True, exist_ok=True)
            if not content.strip():
                content = "user-db:user\n"
            elif not content.endswith("\n"):
                content += "\n"
            profile.write_text(content + "system-db:local\n", encoding="utf-8")
            logger.info("Added system-db:local to %s", profile)
    except OSError:
        logger.warning("Could not adjust %s - the GNOME default may not apply", profile)


def set_keyboard(manifest: dict[str, Any]) -> None:
    """Set the system keyboard layout from the manifest, and seed GNOME defaults."""
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

    try:
        dconf_dir = Path("/etc/dconf/db/local.d")
        dconf_dir.mkdir(parents=True, exist_ok=True)
        (dconf_dir / "00-igloo-keyboard").write_text(
            "[org/gnome/desktop/input-sources]\n"
            f"sources=[('xkb', '{keymap}')]\n"
            "xkb-options=@as []\n",
            encoding="utf-8")

        # A system dconf db is only consulted when the dconf profile names it
        # (see _ensure_dconf_local_db).
        _ensure_dconf_local_db()

        if run_cmd(["dconf", "update"], check=False, timeout=60).returncode == 0:
            logger.info("Seeded GNOME input source %r via a dconf default", keymap)
        else:
            logger.info("dconf not available - GNOME default skipped (non-fatal)")
    except OSError:
        logger.info("Could not write the dconf keyboard default (non-fatal)")

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
    """Wait until the network is up and DNS resolves the archive host."""
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

    # Link-up is not the same as usable: wait (bounded) until DNS actually
    # resolves the archive host, because apt is the very next thing that runs.
    dns_host = "archive.ubuntu.com" if is_ubuntu_like() else "deb.debian.org"
    dns_ok = False
    for attempt in range(1, 7):  # ~60 s
        if run_cmd(["getent", "hosts", dns_host], check=False, timeout=10).returncode == 0:
            dns_ok = True
            break
        logger.info("DNS not resolving %s yet (attempt %d/6) - waiting 10s", dns_host, attempt)
        time.sleep(10)
    if dns_ok:
        logger.info("Network wait complete (link up, DNS resolves %s)", dns_host)
    else:
        logger.error("Network wait complete but DNS still does NOT resolve %s - apt steps will retry/self-heal but may fail; check router/DHCP DNS", dns_host)


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
#
# The key is the user's Linux password, which the first-boot agent does not
# have and must not have: putting it in the manifest would leave it in clear
# text on the FAT32 seed partition. So the work is split in two. As root at
# first boot, stage_credential_import() moves the envelopes into the user's
# own home and installs a login hook. At the user's first graphical login,
# run_user_credential_import() asks for the password once, decrypts, inserts
# the rows into the Linux browser's Login Data using Chromium's Linux "v10"
# encoding, and deletes the envelopes.
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
import shutil
import sqlite3
import subprocess
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

# Browser display name (as the Windows wizard records it) to its Flathub id, its
# config directory relative to XDG_CONFIG_HOME, and the commands a distro package
# installs. iGloo installs these as Flatpaks, but the user may already run a
# packaged build, so both forms have to be reachable.
_CHROMIUM_LINUX_DIRS = {
    "Google Chrome": ("com.google.Chrome", "google-chrome",
                      ("google-chrome", "google-chrome-stable")),
    "Microsoft Edge": ("com.microsoft.Edge", "microsoft-edge",
                       ("microsoft-edge", "microsoft-edge-stable")),
    "Brave": ("com.brave.Browser", "BraveSoftware/Brave-Browser",
              ("brave-browser", "brave")),
    "Vivaldi": ("com.vivaldi.Vivaldi", "vivaldi",
                ("vivaldi", "vivaldi-stable")),
    "Opera": ("com.opera.Opera", "opera", ("opera",)),
}


def _native_config_home(home: Path | None) -> Path:
    # XDG_CONFIG_HOME is only meaningful for the user's own session. The
    # first-boot agent runs as root and passes the home explicitly, where
    # root's environment would point at the wrong place entirely.
    if home is not None:
        return home / ".config"
    return Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config")


def _chromium_config_homes(app_id: str, binaries: tuple[str, ...],
                           home: Path | None = None) -> list[Path]:
    """Every config root this browser could read, most likely first.

    Flatpak overrides XDG_CONFIG_HOME, so a Flatpak build never sees ~/.config
    and a packaged build never sees ~/.var/app. Picking one means guessing, and
    a wrong guess writes the passwords where nothing reads them - so write to
    each root that has a matching install, and to ~/.config when neither does.
    """
    base = home or Path.home()
    roots: list[Path] = []
    if shutil.which("flatpak") and subprocess.run(
            ["flatpak", "info", app_id],
            capture_output=True, text=True, check=False).returncode == 0:
        roots.append(base / ".var" / "app" / app_id / "config")
    if any(shutil.which(b) for b in binaries):
        roots.append(_native_config_home(home))
    return roots or [_native_config_home(home)]

# Chromium timestamps count microseconds since 1601-01-01 UTC.
_CHROMIUM_EPOCH_OFFSET_US = 11644473600 * 1_000_000

# The logins table exactly as Chromium's own builder produces it at schema
# version 31, and labelled 31 - the two must agree. Labelling a modern table as
# an old version is what emptied Brave on 2026-08-21: the stored version drives
# LoginDatabase::MigrateDatabase, which then runs "ALTER TABLE logins ADD COLUMN
# possible_username_pairs" (a version 19 step) against a column that is already
# there, the migration fails, Init returns false and the caller recreates the
# file from scratch.
#
# 31 is chosen because every later step only ADDS columns (37, 39, 41, 42, 43),
# so any Brave from 31 onwards migrates this table upwards cleanly. Chromium
# creates the logins table before migrating and insecure_credentials /
# password_notes after it, so the tables we leave out are not our problem.
# See docs/reference/browser-migration.md.
_LOGINS_SCHEMA_VERSION = 31
_LOGINS_SCHEMA = f"""
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
    blacklisted_by_user INTEGER NOT NULL,
    scheme INTEGER NOT NULL,
    password_type INTEGER,
    times_used INTEGER,
    form_data BLOB,
    display_name VARCHAR,
    icon_url VARCHAR,
    federation_url VARCHAR,
    skip_zero_click INTEGER,
    generation_upload_status INTEGER,
    possible_username_pairs BLOB,
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    date_last_used INTEGER NOT NULL DEFAULT 0,
    moving_blocked_for BLOB,
    date_password_modified INTEGER NOT NULL DEFAULT 0,
    UNIQUE (origin_url, username_element, username_value, password_element,
            signon_realm)
);
CREATE INDEX IF NOT EXISTS logins_signon ON logins (signon_realm);
CREATE TABLE IF NOT EXISTS meta (
    key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY,
    value LONGVARCHAR
);
INSERT OR IGNORE INTO meta(key, value)
    VALUES ('version', '{_LOGINS_SCHEMA_VERSION}'),
           ('last_compatible_version', '{_LOGINS_SCHEMA_VERSION}');
"""


def _chromium_v10_encrypt_bytes(data: bytes) -> bytes:
    """Encode a value the way Linux Chromium stores it: "v10" ||
    AES-128-CBC(PKCS7), key PBKDF2-HMAC-SHA1("peanuts", "saltysalt", 1
    iteration), IV of 16 spaces. This scheme is Chromium's documented fallback
    when no desktop keyring holds the key."""
    key = hashlib.pbkdf2_hmac("sha1", b"peanuts", b"saltysalt", 1, 16)
    return b"v10" + aes_cbc_encrypt(key, b" " * 16, data)


def _chromium_v10_encrypt(password: str) -> bytes:
    return _chromium_v10_encrypt_bytes(password.encode("utf-8"))


# The cookie jar moved under Network/ in Chromium 77; both layouts still exist.
_COOKIE_DB_NAMES = ("Network/Cookies", "Cookies")


def _reencrypt_cookies(profile_dir: Path, cookies: list[dict]) -> int:
    """Rewrite a copied Cookies database so the Linux browser can read it.

    The file came off the Windows profile verbatim, so its rows are still
    encrypted under the Windows master key and its schema is a real Chromium's
    rather than one we reconstruct. Only encrypted_value changes. Rows we have
    no plaintext for are deleted: a cookie the browser cannot decrypt keeps the
    user logged out while looking like it should not.
    """
    db = next((profile_dir / n for n in _COOKIE_DB_NAMES
               if (profile_dir / n).is_file()), None)
    if db is None:
        return 0

    con = sqlite3.connect(db)
    try:
        updated = 0
        for c in cookies:
            try:
                value = base64.b64decode(c.get("value", ""))
            except (ValueError, TypeError):
                continue
            cur = con.execute(
                "UPDATE cookies SET encrypted_value = ? "
                "WHERE host_key = ? AND name = ? AND path = ?",
                (_chromium_v10_encrypt_bytes(value), c.get("host", ""),
                 c.get("name", ""), c.get("path", "")))
            updated += cur.rowcount

        # Both platforms use the "v10" prefix, so the rows we rewrote cannot be
        # told apart from the ones we did not. Name them instead.
        con.execute("CREATE TEMP TABLE igloo_keep (h TEXT, n TEXT, p TEXT)")
        con.executemany(
            "INSERT INTO igloo_keep VALUES (?, ?, ?)",
            [(c.get("host", ""), c.get("name", ""), c.get("path", "")) for c in cookies])
        con.execute(
            "DELETE FROM cookies WHERE NOT EXISTS ("
            "  SELECT 1 FROM igloo_keep k WHERE k.h = cookies.host_key"
            "    AND k.n = cookies.name AND k.p = cookies.path)")
        con.commit()
        return updated
    finally:
        con.close()


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
        "password_type": 0,
        "times_used": 1,
        "form_data": b"",
        "display_name": "",
        "icon_url": "",
        "federation_url": "",
        "skip_zero_click": 0,
        "generation_upload_status": 0,
        "possible_username_pairs": b"",
        "moving_blocked_for": b"",
        # Columns added after version 31; dropped again for a database that
        # predates them, by the PRAGMA table_info filter in the caller.
        "sender_email": "",
        "sender_name": "",
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


# Where the envelopes wait between first boot and the user's first login,
# and how many logins we keep asking before giving up and deleting them.
_CRED_STORE_REL = ".local/share/igloo/credentials.json"
_CRED_MAX_ATTEMPTS = 3


def stage_credential_import(manifest: dict) -> None:
    """Move the staged credential envelopes into the user's home and install a
    login hook that imports them.

    Runs as root at first boot, before redact-manifest. Deliberately does not
    decrypt: the envelope key is the user's Linux password, which is not in the
    manifest. See docs/reference/password-hashing.md."""
    entries = [b for b in manifest.get("browsers", []) if b.get("credentialsBlob")]
    if not entries:
        return

    linux_user = (manifest.get("user", {}).get("preferredLinuxUsername") or "").strip()
    if not linux_user:
        logger.info("Chromium credentials staged but no user in the manifest "
                    "- skipping credential import")
        return

    home = Path("/home") / linux_user
    if not home.is_dir():
        logger.warning("User home %s does not exist - skipping Chromium "
                       "credential import", home)
        return

    try:
        store = home / _CRED_STORE_REL
        store.parent.mkdir(parents=True, exist_ok=True)
        store.write_text(json.dumps({
            "attempts": 0,
            "browsers": [{"name": e.get("name", ""), "blob": e["credentialsBlob"]}
                         for e in entries],
        }), encoding="utf-8")
        store.chmod(0o600)
        # Chown from .local down, not just the igloo directory: mkdir(parents=True) ran
        # as root, so any level it had to create is root-owned. Leaving .local that way
        # locks the user out of their own ~/.local/state - which breaks gnome-keyring,
        # xdg-user-dirs and every autostart hook, not only ours.
        run_cmd(["chown", "-R", f"{linux_user}:{linux_user}",
                 str(home / ".local")], check=False)

        hook = Path("/opt/igloo/credential-import.sh")
        hook.write_text(
            "#!/bin/sh\n"
            "# iGloo credential import - retries at each login until the\n"
            "# envelope store is consumed, then does nothing.\n"
            'STORE="$HOME/' + _CRED_STORE_REL + '"\n'
            '[ -f "$STORE" ] || exit 0\n'
            "exec python3 /opt/igloo/agent.py --import-credentials\n",
            encoding="utf-8")
        hook.chmod(0o755)

        autostart = Path("/etc/xdg/autostart/igloo-credential-import.desktop")
        autostart.write_text(
            "[Desktop Entry]\n"
            "Name=iGloo Browser Passwords\n"
            "Comment=Import the browser passwords migrated from Windows\n"
            "Exec=/opt/igloo/credential-import.sh\n"
            "Icon=dialog-password\n"
            "Terminal=false\n"
            "Type=Application\n"
            "X-GNOME-Autostart-enabled=true\n",
            encoding="utf-8")

        logger.info("Staged %d credential envelope(s) for %s and installed the "
                    "login hook", len(entries), linux_user)
    except OSError:
        logger.exception("Could not stage the credential import (non-fatal)")


def _ask_password() -> tuple[bool, str | None]:
    """Ask the desktop for the account password.

    Returns (asked, password). asked is False when neither dialog tool exists,
    which is different from the user cancelling."""
    attempts = [
        (["zenity", "--password",
          "--title=iGloo", "--timeout=300"], None),
        (["kdialog", "--password",
          "Enter your account password to import the browser passwords "
          "migrated from Windows."], None),
    ]
    for argv, _ in attempts:
        if shutil.which(argv[0]) is None:
            continue
        try:
            res = subprocess.run(argv, capture_output=True, text=True, timeout=310)
        except (OSError, subprocess.SubprocessError):
            logger.exception("%s failed while asking for the password", argv[0])
            return True, None
        if res.returncode != 0:
            return True, None
        return True, res.stdout.rstrip("\n")
    return False, None


def _run_boot_order_mode() -> int:
    """Entry point for --fix-boot-order: re-assert the UEFI boot order, then exit.

    Windows Boot Manager puts itself back at the front on updates and on some
    ordinary boots, and when it does the firmware runs bootmgfw.efi directly -
    shim never loads, the menu never appears, and the machine looks like Linux
    was never installed. The first-boot agent already did this once, but once is
    not enough for something that keeps happening, so a unit runs this every
    boot. Never returns non-zero: a failure here must not mark the unit failed.
    """
    logging.basicConfig(level=logging.INFO,
                        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
    if igloo_boot is None:
        logger.error("igloo_boot.py is not staged in /opt/igloo - boot order untouched")
        return 0
    try:
        igloo_boot.put_self_first_in_boot_order(
            igloo_boot.debian_family(run_cmd, logger))
    except Exception:
        logger.exception("Could not re-assert the UEFI boot order (non-fatal)")
    return 0


def _run_user_mode() -> int:
    """Entry point for --import-credentials: logging the user can actually write
    to, then the import. Never returns non-zero - a failed import must not make
    the desktop report a broken autostart entry."""
    import logging

    state = Path.home() / ".local" / "state"
    try:
        state.mkdir(parents=True, exist_ok=True)
        handler: logging.Handler = logging.FileHandler(state / "igloo-credentials.log")
    except OSError:
        handler = logging.StreamHandler()
    handler.setFormatter(
        logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s"))
    root = logging.getLogger()
    root.addHandler(handler)
    root.setLevel(logging.INFO)

    try:
        return run_user_credential_import()
    except Exception:
        logger.exception("Credential import failed")
        return 0


def run_user_credential_import() -> int:
    """Import the staged Chromium credentials. Runs as the user at login."""
    store = Path.home() / _CRED_STORE_REL
    if not store.is_file():
        return 0

    try:
        data = json.loads(store.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        logger.exception("Credential store is unreadable - removing it")
        store.unlink(missing_ok=True)
        return 0

    attempts = int(data.get("attempts", 0)) + 1
    entries = data.get("browsers", [])
    if not entries or attempts > _CRED_MAX_ATTEMPTS:
        logger.info("Giving up on the credential import after %d attempt(s)",
                    attempts - 1)
        store.unlink(missing_ok=True)
        return 0

    try:
        _aes_self_test()
    except AssertionError:
        logger.exception("AES self-test failed - credential import disabled")
        store.unlink(missing_ok=True)
        return 0

    asked, password = _ask_password()
    if not asked:
        logger.warning("Neither zenity nor kdialog is installed - cannot ask "
                       "for the password, leaving the envelopes in place")
        return 0
    if not password:
        data["attempts"] = attempts
        store.write_text(json.dumps(data), encoding="utf-8")
        logger.info("Password prompt cancelled (attempt %d of %d)",
                    attempts, _CRED_MAX_ATTEMPTS)
        return 0

    imported = 0
    wrong_password = False
    for entry in entries:
        name = entry.get("name", "")
        mapping = _CHROMIUM_LINUX_DIRS.get(name)
        if mapping is None:
            logger.info("No Linux profile mapping for browser %r - skipping", name)
            continue
        app_id, config_rel, binaries = mapping
        try:
            payload = _decrypt_envelope(entry["blob"], password)
        except Exception:
            # A bad tag is indistinguishable from a corrupt envelope here, but
            # a wrong password is overwhelmingly the likelier cause.
            wrong_password = True
            logger.warning("Could not decrypt the envelope for %s", name)
            continue

        logins = payload.get("logins", [])
        cookies = payload.get("cookies", [])
        if not logins and not cookies:
            continue
        # Counted per browser, not per root: the same logins written to both a
        # Flatpak and a packaged install is one migration, not two.
        landed = 0
        for root in _chromium_config_homes(app_id, binaries):
            profile_dir = root / config_rel / "Default"
            try:
                if logins:
                    profile_dir.mkdir(parents=True, exist_ok=True)
                    landed = max(landed,
                                 _import_into_login_data(profile_dir / "Login Data", logins))
                    logger.info("Imported %s logins into %s", name, profile_dir)
            except Exception:
                logger.exception("Failed to import credentials for %s into %s "
                                 "(non-fatal)", name, root)
            try:
                # Cookies are a separate failure domain: the jar being unusable
                # must not cost the passwords that came from the same profile.
                if cookies:
                    rewritten = _reencrypt_cookies(profile_dir, cookies)
                    logger.info("Re-encrypted %d %s cookie(s) in %s",
                                rewritten, name, profile_dir)
            except Exception:
                logger.exception("Failed to re-encrypt cookies for %s in %s "
                                 "(non-fatal)", name, profile_dir)
        imported += landed

    if wrong_password and imported == 0:
        data["attempts"] = attempts
        store.write_text(json.dumps(data), encoding="utf-8")
        logger.info("Nothing decrypted (attempt %d of %d) - will ask again at "
                    "the next login", attempts, _CRED_MAX_ATTEMPTS)
        return 0

    store.unlink(missing_ok=True)
    logger.info("Imported %d browser login(s); credential store removed", imported)
    return 0


# === END AGENT SECTION ===================================================


def redact_manifest(manifest: dict[str, Any]) -> None:
    manifest_path = Path("/var/lib/igloo/manifest.json")
    if not manifest_path.exists():
        return
    try:
        data: dict[str, Any] = json.loads(manifest_path.read_text(encoding="utf-8"))
        changed = False
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
    """Parse the EDID blob and return a dict with the monitor's identity."""
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
    """Check if a given width x height is advertised by the DRM connector."""
    modes_file = next(Path("/sys/class/drm").glob(f"card*-{connector}/modes"), None)
    if modes_file is None:
        return True   # cannot tell - do not block on it
    try:
        modes = modes_file.read_text().split()
    except OSError:
        return True
    return f"{width}x{height}" in modes


# True when the agent runs via --only (currently: the post-driver display
# second pass). Lets steps behave differently on a re-run vs the first pass.
_SECOND_PASS = False


def _install_display_second_pass(reason: str) -> None:
    """Install a systemd unit to re-run the display-layout step after the"""
    unit = Path("/etc/systemd/system/igloo-display-layout.service")
    try:
        unit.write_text(
            "[Unit]\n"
            "Description=iGloo display layout (post-driver second pass)\n"
            "Before=display-manager.service\n"
            "ConditionPathExists=/var/lib/igloo/manifest.json\n"
            "ConditionPathExists=!/var/lib/igloo/.display-done\n"
            "\n"
            "[Service]\n"
            "Type=oneshot\n"
            "ExecStart=/usr/bin/env python3 /opt/igloo/agent.py "
            "--manifest /var/lib/igloo/manifest.json --log-dir /var/log/igloo "
            "--only display-layout\n"
            "ExecStartPost=/usr/bin/touch /var/lib/igloo/.display-done\n"
            "RemainAfterExit=yes\n"
            "\n"
            "[Install]\n"
            "WantedBy=multi-user.target\n",
            encoding="utf-8")
        wants = Path("/etc/systemd/system/multi-user.target.wants")
        wants.mkdir(parents=True, exist_ok=True)
        link = wants / unit.name
        if not link.exists():
            link.symlink_to(unit)
        logger.info("Installed the display-layout second pass (%s)", reason)
    except OSError:
        logger.exception("Could not install the display-layout second pass (non-fatal)")


def _wait_for_display_outputs(manifest: dict[str, Any]) -> list[dict[str, Any]]:
    """Connected DRM outputs, with a bounded wait for late NVIDIA connectors."""
    outputs = _connected_outputs()
    if not outputs and _SECOND_PASS:
        if manifest.get("hardware", {}).get("gpuVendor", "").lower() == "nvidia":
            logger.info("Second pass: no EDID yet - loading the NVIDIA module explicitly")
            run_cmd(["modprobe", "nvidia-drm"], check=False, timeout=120)
            for attempt in range(1, 13):  # ~60 s
                time.sleep(5)
                outputs = _connected_outputs()
                if outputs:
                    logger.info("DRM connectors appeared after %d attempt(s)", attempt)
                    break
                logger.info("Waiting for DRM connectors (attempt %d/12)", attempt)
    return outputs


def _match_display_layouts(
    wanted: list[dict[str, Any]], outputs: list[dict[str, Any]]
) -> tuple[list[str], list[dict[str, Any]], int]:
    """Match Windows-reported monitors to live outputs by EDID PnP id.

    Returns (monitors.xml logical-monitor fragments, staged layout for the
    login-time appliers, match count).
    """
    # A pool per PnP id, not one output per id: two panels of the same model
    # report the same id and the same EDID serial, and a dict would collapse them
    # onto whichever came last - putting both logical monitors on one connector.
    # Each output is consumed once, in order, so identical panels still pair up.
    by_pnp: dict[str, list[dict[str, Any]]] = {}
    for o in outputs:
        by_pnp.setdefault(o["pnp_id"], []).append(o)
    logical: list[str] = []
    cinnamon_layout: list[dict[str, Any]] = []
    matched = 0

    # Mutter discards the whole file over one negative coordinate - shift to 0,0.
    origin_x = min((int(w.get("positionX") or 0) for w in wanted), default=0)
    origin_y = min((int(w.get("positionY") or 0) for w in wanted), default=0)

    for want in wanted:
        pnp = (want.get("pnpId") or "").upper()
        pool = by_pnp.get(pnp)
        if not pool:
            logger.info("  Monitor %s from Windows is not attached here - skipped", pnp or "?")
            continue
        out = pool.pop(0)

        width, height = int(want.get("widthPx", 0)), int(want.get("heightPx", 0))
        if width <= 0 or height <= 0:
            continue
 
        rotation_deg = int(want.get("rotationDegrees") or 0)
        mode_w, mode_h = (height, width) if rotation_deg in (90, 270) else (width, height)
        if not _mode_is_supported(out["connector"], mode_w, mode_h):
            logger.warning("  %s does not advertise %dx%d - leaving this output alone",
                            out["connector"], mode_w, mode_h)
            continue

        rate = int(want.get("refreshHz") or 60) or 60

        rotation = {0: "normal", 90: "left", 180: "inverted", 270: "right"}.get(
            rotation_deg, "normal")

        logical.append(
            "    <logicalmonitor>\n"
            f"      <x>{int(want.get('positionX') or 0) - origin_x}</x>\n"
            f"      <y>{int(want.get('positionY') or 0) - origin_y}</y>\n"
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
            f"        <mode><width>{mode_w}</width><height>{mode_h}</height>"
            f"<rate>{rate}</rate></mode>\n"
            "      </monitor>\n"
            "    </logicalmonitor>\n")
        matched += 1

        cinnamon_layout.append({
            "pnpId": pnp,
            "vendor": out["vendor"],
            "product": out["product"],
            "serial": out["serial"],
            "connector": out["connector"],
            "width": mode_w,
            "height": mode_h,
            "rate": rate,
            "rotation": {0: "none", 90: "left", 180: "inverted", 270: "right"}.get(
                rotation_deg, "none"),
            "x": int(want.get("positionX") or 0) - origin_x,
            "y": int(want.get("positionY") or 0) - origin_y,
            "primary": bool(want.get("isPrimary")),
            "scalePercent": int(want.get("scalePercent") or 100) or 100,
        })
        logger.info("  %s -> %dx%d@%dHz %s at (%s,%s)", out["connector"], mode_w, mode_h,
                    rate, rotation, want.get("positionX"), want.get("positionY"))

    return logical, cinnamon_layout, matched


# Mutter and muffin read the same schema under different names - both, or Mint forgets.
_MONITOR_CONFIG_NAMES = ("monitors.xml", "cinnamon-monitors.xml")


def _write_user_monitors_xml(username: str, xml: str, matched: int) -> bool:
    """Write the monitor layout into the user's ~/.config. False if the home is missing."""
    home = Path("/home") / username if username else None
    if home is None or not home.is_dir():
        logger.warning("User home not found - cannot write the display layout")
        return False

    cfg = home / ".config"
    cfg.mkdir(parents=True, exist_ok=True)
    for name in _MONITOR_CONFIG_NAMES:
        (cfg / name).write_text(xml, encoding="utf-8")
    run_cmd(["chown", "-R", f"{username}:{username}", str(cfg)], check=False)
    logger.info("Wrote the layout for %d monitor(s) into %s", matched, cfg)
    return True


def _write_greeter_monitors_xml(xml: str) -> None:
    # The greeter runs as its own user and reads its own copy, so a portrait screen
    # would otherwise still be sideways at the login prompt - the very first thing
    # the user sees. Best-effort: not every display manager uses this path.
    for gdm_dir in (Path("/var/lib/gdm3/.config"), Path("/var/lib/gdm/.config"),
                    Path("/var/lib/lightdm/.config")):
        home = gdm_dir.parent
        if not home.is_dir():
            continue
        try:
            # Take the uid/gid from the home directory itself. The account name does
            # not follow the directory name - Debian's gdm3 runs as "Debian-gdm" - and
            # guessing it leaves this .config owned by root, which stops gdm booting.
            st = home.stat()
            gdm_dir.mkdir(parents=True, exist_ok=True)
            os.chown(gdm_dir, st.st_uid, st.st_gid)
            for name in _MONITOR_CONFIG_NAMES:
                target = gdm_dir / name
                target.write_text(xml, encoding="utf-8")
                os.chown(target, st.st_uid, st.st_gid)
            logger.info("Applied the same layout to the greeter in %s (uid %d)",
                        home, st.st_uid)
        except OSError:
            logger.exception("Could not write the greeter layout in %s (non-fatal)", gdm_dir)


def _stage_display_login_hook(cinnamon_layout: list[dict[str, Any]]) -> None:
    """Write the Cinnamon/GNOME login-time applier and its autostart hook."""
    try:
        layout_path = Path("/opt/igloo/display-layout.json")
        layout_path.write_text(json.dumps(cinnamon_layout, indent=2), encoding="utf-8")
        layout_path.chmod(0o644)

        apply_sh = Path("/opt/igloo/display-apply.sh")
        apply_sh.write_text(
            "#!/usr/bin/env bash\n"
            "# iGloo display layout.\n"
            "# Runs at EVERY login, not once. The layout only survives a logout\n"
            "# if the compositor's own stored configuration matches the attached\n"
            "# monitors, and on this hardware it does not - two panels sharing an\n"
            "# EDID serial. Re-asserting each login is what the user actually\n"
            "# needs; the marker below is left for the log bundle to read.\n"
            'DONE_MARKER="$HOME/.config/.igloo-display-done"\n'
            'mkdir -p "$HOME/.local/state"\n'
            'case " $XDG_CURRENT_DESKTOP " in\n'
            '  *GNOME*) HELPER=/opt/igloo/display-apply-gnome.py ;;\n'
            '  *)       HELPER=/opt/igloo/display-apply.py ;;\n'
            "esac\n"
            'LOG="$HOME/.local/state/igloo-display.log"\n'
            'echo "[$(date +%F\\ %T)] start: desktop=$XDG_CURRENT_DESKTOP '
            'session=$XDG_SESSION_TYPE helper=$HELPER" >> "$LOG"\n'
            'if [ ! -f "$HELPER" ]; then\n'
            '  echo "[$(date +%F\\ %T)] ERROR: $HELPER is missing" >> "$LOG"\n'
            "  exit 0\n"
            "fi\n"
            "# Capture the helper's own output: without this a failure leaves no trace\n"
            "# and the next boot is diagnosed blind.\n"
            'if python3 "$HELPER" --layout /opt/igloo/display-layout.json >> "$LOG" 2>&1; then\n'
            '  echo "[$(date +%F\\ %T)] applied" >> "$LOG"\n'
            '  touch "$DONE_MARKER"\n'
            "else\n"
            '  echo "[$(date +%F\\ %T)] FAILED with exit $? - will retry next login" >> "$LOG"\n'
            "fi\n",
            encoding="utf-8",
        )
        apply_sh.chmod(0o755)

        autostart = Path("/etc/xdg/autostart/igloo-display-layout.desktop")
        autostart.write_text(
            "[Desktop Entry]\n"
            "Name=iGloo Display Layout\n"
            "Comment=Apply the migrated Windows monitor layout (resolution, rotation, position)\n"
            "Exec=/opt/igloo/display-apply.sh\n"
            "Icon=preferences-desktop-display\n"
            "Terminal=false\n"
            "Type=Application\n"
            "X-GNOME-Autostart-enabled=true\n"
            "OnlyShowIn=X-Cinnamon;Cinnamon;GNOME;\n",
            encoding="utf-8",
        )
        logger.info("Staged the login display-layout hook (Cinnamon xrandr / GNOME D-Bus)")
    except OSError:
        logger.exception("Could not stage the Cinnamon display-layout hook (non-fatal)")


def migrate_display_layout(manifest: dict[str, Any]) -> None:
    """Map Windows desktop layout to GNOME/Cinnamon monitors.xml via EDID/PnP IDs.
    This guarantees stable screen assignment. (KDE is handled separately).
    """
    wanted = manifest.get("displays", [])
    if not wanted:
        logger.info("No display layout in the manifest - leaving the desktop defaults")
        return

    outputs = _wait_for_display_outputs(manifest)
    if not outputs:
        logger.info("No connected outputs with readable EDID - skipping display layout")
        _install_display_second_pass("no EDID-readable outputs on this boot")
        return
    for o in outputs:
        logger.info("Detected output %s: %s (%s)", o["connector"], o["pnp_id"], o["product"])

    logical, cinnamon_layout, matched = _match_display_layouts(wanted, outputs)
    if matched == 0:
        logger.info("No Windows monitors matched the attached outputs - nothing written")
        return

    # One configuration per layout mode. Mutter picks a stored configuration by
    # the mode the session runs in, so a file that names none matches nothing and
    # is silently ignored - which is why the layout used to fall back on the next
    # login. Mutter writes both itself; this mirrors its own output. The two are
    # identical because the scale is 1, where logical and physical pixels agree.
    body = "".join(logical)
    xml = ('<monitors version="2">\n'
           + "".join(f"  <configuration>\n    <layoutmode>{mode}</layoutmode>\n"
                     f"{body}  </configuration>\n"
                     for mode in ("physical", "logical"))
           + "</monitors>\n")

    username = (manifest.get("user", {}).get("preferredLinuxUsername") or "").strip()
    if not _write_user_monitors_xml(username, xml, matched):
        return
    _write_greeter_monitors_xml(xml)
    _stage_display_login_hook(cinnamon_layout)

    if Path("/var/lib/igloo/.reboot-required").exists():
        _install_display_second_pass("a driver reboot is pending - DRM landscape will change")


def migrate_wallpaper(manifest: dict[str, Any]) -> None:
    """Apply staged Windows wallpaper via system-wide GNOME/Cinnamon dconf defaults.
    The image is saved to the user's Pictures folder.
    """
    wp = manifest.get("wallpaper") or {}
    fname = (wp.get("fileName") or "").strip()
    if not fname:
        logger.info("No wallpaper in the manifest - keeping the distro default")
        return
    src = Path("/opt/igloo") / fname
    if not src.is_file():
        logger.warning("Manifest names wallpaper %r but %s is missing - skipped", fname, src)
        return

    username = (manifest.get("user", {}).get("preferredLinuxUsername") or "").strip()
    home = Path("/home") / username if username else None
    if home is None or not home.is_dir():
        logger.warning("User home not found - cannot install the wallpaper")
        return

    try:
        pictures = home / "Pictures"
        pictures.mkdir(parents=True, exist_ok=True)
        dst = pictures / f"wallpaper{src.suffix or '.jpg'}"
        dst.write_bytes(src.read_bytes())
        run_cmd(["chown", "-R", f"{username}:{username}", str(pictures)], check=False)
        logger.info("Wallpaper installed at %s", dst)
    except OSError:
        logger.exception("Could not copy the wallpaper into the user home (non-fatal)")
        return

    uri = f"file://{dst}"
    try:
        dconf_dir = Path("/etc/dconf/db/local.d")
        dconf_dir.mkdir(parents=True, exist_ok=True)
        (dconf_dir / "00-igloo-wallpaper").write_text(
            "[org/gnome/desktop/background]\n"
            f"picture-uri='{uri}'\n"
            f"picture-uri-dark='{uri}'\n"
            "picture-options='zoom'\n"
            "[org/cinnamon/desktop/background]\n"
            f"picture-uri='{uri}'\n"
            "picture-options='zoom'\n",
            encoding="utf-8")
        _ensure_dconf_local_db()
        if run_cmd(["dconf", "update"], check=False, timeout=60).returncode == 0:
            logger.info("Seeded the desktop wallpaper via a dconf default (GNOME + Cinnamon)")
        else:
            logger.info("dconf not available - wallpaper file installed but not set (non-fatal)")
    except OSError:
        logger.info("Could not write the dconf wallpaper default (non-fatal)")


def migrate_account_picture(manifest: dict[str, Any]) -> None:
    """Install the staged Windows account picture as the user's Linux avatar.

    Written to three places because no single one covers every greeter:
    /var/lib/AccountsService/icons/<user> is what GDM and SDDM read, the Icon= line
    in /var/lib/AccountsService/users/<user> is the metadata KDE writes, and ~/.face
    covers the greeters that look in the home directory.
    """
    pic = manifest.get("accountPicture") or {}
    fname = (pic.get("fileName") or "").strip()
    if not fname:
        logger.info("No account picture in the manifest - keeping the distro default")
        return

    src = Path("/opt/igloo") / fname
    if not src.is_file():
        logger.warning("Manifest names account picture %r but %s is missing - skipped",
                       fname, src)
        return

    username = (manifest.get("user", {}).get("preferredLinuxUsername") or "").strip()
    home = Path("/home") / username if username else None
    if home is None or not home.is_dir():
        logger.warning("User home not found - cannot install the account picture")
        return

    data = src.read_bytes()

    try:
        icons = Path("/var/lib/AccountsService/icons")
        icons.mkdir(parents=True, exist_ok=True)
        icon_path = icons / username
        icon_path.write_bytes(data)
        icon_path.chmod(0o644)

        users_dir = Path("/var/lib/AccountsService/users")
        users_dir.mkdir(parents=True, exist_ok=True)
        _set_accountsservice_icon(users_dir / username, icon_path)
        logger.info("Account picture installed at %s", icon_path)
    except OSError:
        logger.exception("Could not write the AccountsService avatar (non-fatal)")

    try:
        face = home / ".face"
        face.write_bytes(data)
        face.chmod(0o644)
        run_cmd(["chown", f"{username}:{username}", str(face)], check=False)
        logger.info("Account picture also written to %s", face)
    except OSError:
        logger.exception("Could not write ~/.face (non-fatal)")


def _set_accountsservice_icon(user_file: Path, icon_path: Path) -> None:
    """Set Icon= in the AccountsService user file, keeping any other keys intact."""
    lines: list[str] = []
    if user_file.is_file():
        lines = user_file.read_text(encoding="utf-8").splitlines()

    if "[User]" not in lines:
        lines = ["[User]"] + lines

    out: list[str] = []
    written = False
    for line in lines:
        if line.startswith("Icon="):
            if not written:
                out.append(f"Icon={icon_path}")
                written = True
            continue
        out.append(line)
        if line == "[User]" and not written:
            out.append(f"Icon={icon_path}")
            written = True

    user_file.write_text("\n".join(out) + "\n", encoding="utf-8")
    user_file.chmod(0o600)

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
    """Safely remove temporary Igloo installer partitions and UEFI boot entries.
    Strictly follows BR-01/BR-03 safety rules for exact label-based deletion."""

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


    r = run_cmd(["efibootmgr"], check=False)
    for line in (r.stdout or "").splitlines():
        m = re.match(r"^Boot([0-9A-Fa-f]{4})\*?\s+(.*)$", line.strip())
        if m and "igloo" in m.group(2).lower():
            run_cmd(["efibootmgr", "-b", m.group(1), "-B"], check=False)
            logger.info("Removed stale UEFI boot entry Boot%s (%s)", m.group(1), m.group(2))


def main() -> int:
    p = argparse.ArgumentParser(description="Igloo first-boot agent (Debian family)")
    # Not required=True: --import-credentials is a separate, unprivileged mode
    # that reads neither of them.
    p.add_argument("--manifest", type=Path)
    p.add_argument("--log-dir", type=Path)
    p.add_argument("--only", default=None,
                    help="Comma-separated step names to run instead of the full pass "
                        "(used by the post-driver display-layout second pass).")
    p.add_argument("--import-credentials", action="store_true",
                    help="Run as the logged-in user from the autostart hook: ask "
                        "for the account password once and import the staged "
                        "browser credentials. Needs neither --manifest nor root.")
    p.add_argument("--fix-boot-order", action="store_true",
                    help="Put the entry we booted from back at the front of the "
                        "UEFI boot order. Runs on every boot, because Windows "
                        "reasserts itself there. Needs neither --manifest nor a "
                        "log directory.")
    args = p.parse_args()

    if args.import_credentials:
        return _run_user_mode()
    if args.fix_boot_order:
        return _run_boot_order_mode()
    if args.manifest is None or args.log_dir is None:
        p.error("--manifest and --log-dir are required for the first-boot pass")

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
        ("boot-menu",        lambda: configure_boot_menu(manifest)),
        ("flathub",          lambda: setup_flathub(manifest)),
        ("suggested-pkgs",   lambda: install_suggested_packages(manifest)),
        ("migration-tools",  lambda: ensure_migration_tools(manifest)),
        ("user-files",       lambda: migrate_user_files(manifest)),
        ("display-layout",   lambda: migrate_display_layout(manifest)),
        ("wallpaper",        lambda: migrate_wallpaper(manifest)),
        ("account-picture",  lambda: migrate_account_picture(manifest)),
        ("welcome-app",      lambda: install_welcome_app(manifest)),
        ("stage-credentials", lambda: stage_credential_import(manifest)),
        ("redact-manifest",  lambda: redact_manifest(manifest)),
        ("cleanup-seed",     lambda: cleanup_installer_partitions(manifest)),
    ]

    if args.only:
        global _SECOND_PASS
        _SECOND_PASS = True
        wanted_steps = {s.strip() for s in args.only.split(",") if s.strip()}
        steps = [(n, s) for n, s in steps if n in wanted_steps]
        logger.info("Running only step(s): %s", ", ".join(n for n, _ in steps) or "(none)")

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
