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
    - Migrate staged user files, with read-only Windows NTFS as a legacy fallback

Unlike Fedora (whose %post copies user files inside the installer), the Debian
family does the NTFS copy HERE: debian-installer/subiquity run in a minimal
environment without ntfs-3g, whereas the installed system has full tooling.
The selected folder snapshot on OEMDRV is preferred when present. Browser
profiles use their manifest-bound OEMDRV staging path when supplied; old
manifests can still read them from the Windows partition.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import logging
import os
import platform
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import unicodedata
import uuid
import zlib
from collections import Counter
from pathlib import Path

# Shipped alongside this file in /opt/igloo; the script's own directory is first
# on sys.path. User-session credential import can run without it. Debian
# dual-boot first-boot setup requires its functional Windows menu verification.
try:
    import igloo_boot
except (ImportError, OSError):
    # User-session credential import must still run if an older installation
    # staged this optional root-owned boot helper without read permission.
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
    return os_release().get("ID", "").lower()


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
    return False


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


def _flag_driver_reboot_required(marker: Path) -> None:
    """Durably record the required driver reboot before publishing .done."""
    payload = b"nvidia-driver-installed\n"
    temporary = marker.with_name(marker.name + "." + uuid.uuid4().hex + ".writing")
    try:
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "wb") as output:
            output.write(payload)
            output.flush()
            os.fsync(output.fileno())
        if temporary.read_bytes() != payload:
            raise OSError("Driver reboot request staging readback failed")
        os.replace(temporary, marker)
        if marker.read_bytes() != payload:
            raise OSError("Driver reboot request readback failed")
        directory = os.open(marker.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        temporary.unlink(missing_ok=True)


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
    _flag_driver_reboot_required(Path("/var/lib/igloo/.reboot-required"))
    logger.info("Flagged reboot-required (NVIDIA driver needs a clean boot)")
    logger.info("NVIDIA driver installed")


def install_codecs(manifest: dict[str, Any]) -> None:
    """Install multimedia codecs / restricted extras."""
    if not manifest.get("hardware", {}).get("needsNonFreeCodecs", True):
        logger.info("Codecs: needsNonFreeCodecs=false, skipping")
        return
    if distro_id() == "linuxmint":
        # Mint ships its own codec metapackage  exactly what its first-run  "Install Multimedia Codecs" applet installs.
        apt_install(["mint-meta-codecs"], timeout=900)
    elif is_ubuntu_like():
        # EULA-bearing packages need this debconf pre-answer to stay unattended.
        run_cmd(["bash", "-c",
                "echo ttf-mscorefonts-installer msttcorefonts/accepted-mscorefonts-eula "
                "select true | debconf-set-selections"], env=APT_ENV)
        apt_install(["ubuntu-restricted-extras"], timeout=900)
    else:
        apt_install(["libavcodec-extra", "gstreamer1.0-libav",
            "gstreamer1.0-plugins-ugly", "gstreamer1.0-plugins-bad"], timeout=600)
    logger.info("Multimedia codecs installed")


def ensure_firmware(manifest: dict[str, Any]) -> None:
    """Make sure the firmware package is present (Debian: firmware-linux, Ubuntu: linux-firmware)."""
    pkg = "linux-firmware" if is_ubuntu_like() else "firmware-linux"
    apt_install([pkg], timeout=600)
    logger.info("Ensured firmware package present: %s", pkg)


def _requires_windows_boot_menu(manifest: dict[str, Any]) -> bool:
    return manifest.get("distroId") == "debian" and \
        manifest.get("hardware", {}).get("installMode") == "dual-boot"


def enable_os_prober(manifest: dict[str, Any]) -> None:
    """Enable os-prober so GRUB finds Windows and other OSes on the next update."""
    required = _requires_windows_boot_menu(manifest)
    if required and igloo_boot is None:
        raise RuntimeError("The Windows boot-menu verifier is unavailable; dual-boot setup is incomplete")
    grub_default = Path("/etc/default/grub")
    try:
        text = grub_default.read_text() if grub_default.exists() else ""
        if "GRUB_DISABLE_OS_PROBER=false" not in text:
            with grub_default.open("a", encoding="utf-8") as f:
                f.write("\nGRUB_DISABLE_OS_PROBER=false\n")
            logger.info("Set GRUB_DISABLE_OS_PROBER=false")
    except Exception:
        if required:
            raise
        logger.exception("Could not edit /etc/default/grub (non-fatal)")

    apt_install(["os-prober"], timeout=300, check=required)
    if required:
        # update-grub can hide a failed os-prober behind its own successful exit.
        # Exercise that prerequisite explicitly before accepting its output.
        run_cmd(["os-prober"], timeout=300)
    if shutil.which("update-grub"):
        run_cmd(["update-grub"], check=required, timeout=300)
    else:
        run_cmd(["grub-mkconfig", "-o", "/boot/grub/grub.cfg"], check=required, timeout=300)
    if required:
        igloo_boot.require_windows_boot_entry(Path("/boot/grub/grub.cfg"))
        logger.info("Regenerated GRUB and verified a Windows EFI chainloader entry")
    else:
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
    """Theme the menu, boot Linux by default, and retain the Windows entry."""
    required = _requires_windows_boot_menu(manifest)
    _shrink_initramfs()
    if igloo_boot is None:
        if required:
            raise RuntimeError("The Windows boot-menu verifier is unavailable; dual-boot setup is incomplete")
        logger.error("igloo_boot.py not staged in /opt/igloo - the menu stays stock")
        return
    igloo_boot.configure_boot_menu(manifest, igloo_boot.debian_family(run_cmd, logger),
                                  require_windows=required)


def setup_flathub(manifest: dict[str, Any]) -> None:
    """Install Flatpak (if needed) and register the Flathub remote."""
    if shutil.which("flatpak") is None:
        result = apt_install(["flatpak"], timeout=300, check=False)
        if result.returncode != 0 or shutil.which("flatpak") is None:
            logger.warning("Flatpak is unavailable; selected Flatpak installs will fail")
            return
    result = run_cmd(["flatpak", "remote-add", "--if-not-exists", "--system", "flathub", "https://dl.flathub.org/repo/flathub.flatpakrepo"],
                     timeout=120, check=False)
    if result.returncode != 0:
        logger.warning("Flathub remote could not be registered; selected Flatpak installs will fail")
        return
    logger.info("Flathub remote ready")


def install_suggested_packages(manifest: dict[str, Any]) -> None:
    """Install selected apps and browsers before copying their profiles."""
    declared = manifest.get("suggestedPackages") or []
    if not isinstance(declared, list) or any(not isinstance(p, dict) for p in declared):
        raise ValueError("Suggested packages must be an array of objects")
    pkgs = [p for p in declared if p.get("autoInstall")]
    if any(not p.get("flatpakId") and not p.get("nativePackage") for p in pkgs):
        raise ValueError("Selected application has no installable package identifier")

    flatpak_ids = [p["flatpakId"] for p in pkgs if p.get("flatpakId")]
    apt_pkgs = [p["nativePackage"] for p in pkgs if p.get("nativePackage")]

    # The Windows UI normally adds each selected browser to suggestedPackages.
    # A CLI or older manifest can omit that companion entry while still asking
    # for its profile and encrypted credentials. Use only known browser mappings;
    # never infer an arbitrary package name from the manifest's display text.
    browsers = manifest.get("browsers") or []
    if not isinstance(browsers, list) or any(not isinstance(b, dict) for b in browsers):
        raise ValueError("Selected browsers must be an array of objects")
    gecko_flatpaks = {"Zen Browser": _GECKO_FLATPAKS[".zen"],
                      "Waterfox": _GECKO_FLATPAKS[".waterfox"]}
    for browser in browsers:
        name = browser.get("name")
        if not isinstance(name, str):
            raise ValueError("Selected browser has no valid name")
        chromium = _CHROMIUM_LINUX_DIRS.get(name)
        app_id = chromium[0] if chromium else gecko_flatpaks.get(name)
        if app_id and app_id not in flatpak_ids:
            flatpak_ids.append(app_id)

    if not flatpak_ids and not apt_pkgs:
        logger.info("No auto-install packages or selected browser packages in manifest")
        return

    # The manifest is an input to root package-manager argv. Validate every
    # selected identifier before invoking either manager, including when a
    # valid identifier appears earlier in the same manifest.
    for app_id in flatpak_ids:
        if (not isinstance(app_id, str)
                or re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]{2,127}", app_id) is None
                or app_id.count(".") < 2 or ".." in app_id):
            raise ValueError("Selected Flatpak identifier is unsafe")
    for package in apt_pkgs:
        if (not isinstance(package, str)
                or re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.+:-]{0,127}", package) is None
                or ".." in package):
            raise ValueError("Selected apt package identifier is unsafe")

    if flatpak_ids:
        logger.info("Installing Flatpaks: %s", ", ".join(flatpak_ids))
        run_cmd(["flatpak", "install", "-y", "--noninteractive", "flathub", *flatpak_ids],
                timeout=900)
    if apt_pkgs:
        logger.info("Installing apt packages: %s", ", ".join(apt_pkgs))
        apt_install([*apt_pkgs], timeout=600)
    logger.info("Suggested packages installed")


#   User-file migration from the Windows NTFS partition

def ensure_migration_tools(manifest: dict[str, Any]) -> None:
    """Require NTFS support only for legacy sources; staged files work offline."""
    file_plan = manifest.get("files") or {}
    folders = file_plan.get("folders") or file_plan.get("includedFolders") or []
    browsers = [browser for browser in manifest.get("browsers", [])
                if browser.get("sourceRelativePath") or browser.get("profileStagingPath")]
    needs_ntfs = bool((folders and not _STAGED_FILES_DIR.is_dir()) or
                      any(not browser.get("profileStagingPath") for browser in browsers))
    if not needs_ntfs:
        logger.info("Selected migration uses staged files; no NTFS tools needed")
        return
    if shutil.which("ntfs-3g") is None:
        apt_install(["ntfs-3g"], timeout=600, check=False)
    if shutil.which("ntfs-3g") is None:
        raise RuntimeError("Selected legacy NTFS migration requires ntfs-3g")
    logger.info("Legacy NTFS migration tool is available")


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
    """Copy selected data without an apt dependency or following source links."""
    if not src.is_dir() or src.is_symlink():
        raise RuntimeError("Selected migration source is not a regular directory")
    if dst.is_symlink() or (dst.exists() and not dst.is_dir()):
        raise RuntimeError("Selected migration target is not a regular directory")
    dst.mkdir(parents=True, exist_ok=True)
    excluded = {"desktop.ini", "Thumbs.db"}
    def fail_walk(error: OSError) -> None:
        raise error

    for directory, subdirs, files in os.walk(src, followlinks=False, onerror=fail_walk):
        source_dir = Path(directory)
        target_dir = dst / source_dir.relative_to(src)
        if target_dir.is_symlink() or (target_dir.exists() and not target_dir.is_dir()):
            raise RuntimeError("Selected migration target contains an unsafe directory")
        target_dir.mkdir(parents=True, exist_ok=True)
        target_dir.chmod(0o700)
        safe_subdirs = []
        for name in subdirs:
            source_child = source_dir / name
            target_child = target_dir / name
            if name in excluded or source_child.is_symlink():
                continue
            if target_child.is_symlink() or (target_child.exists() and not target_child.is_dir()):
                raise RuntimeError("Selected migration target contains an unsafe directory")
            safe_subdirs.append(name)
        subdirs[:] = safe_subdirs
        for name in files:
            source_file = source_dir / name
            target_file = target_dir / name
            if name in excluded or source_file.is_symlink():
                continue
            if target_file.is_symlink() or (target_file.exists() and not target_file.is_file()):
                raise RuntimeError("Selected migration target contains an unsafe file")
            source_stat = source_file.lstat()
            if not stat.S_ISREG(source_stat.st_mode):
                raise RuntimeError("Selected migration source contains a special file")
            # A retry may find an old world-readable target. Copy into a
            # private file first, then replace the target in one rename so
            # newly selected content is never written into those old modes.
            temporary: Path | None = None
            try:
                with source_file.open("rb") as input_file:
                    with tempfile.NamedTemporaryFile(
                            mode="wb", prefix=".igloo-file-", dir=target_dir,
                            delete=False) as output:
                        temporary = Path(output.name)
                        shutil.copyfileobj(input_file, output, length=1024 * 1024)
                # Preserve the timestamp that copy2 used to carry, without
                # importing FAT's synthetic executable/public permission bits.
                os.utime(temporary, ns=(source_stat.st_atime_ns, source_stat.st_mtime_ns))
                os.replace(temporary, target_file)
            finally:
                if temporary is not None:
                    temporary.unlink(missing_ok=True)


def _checked_child(root: Path | None, relative: str) -> Path:
    """Keep manifest-controlled paths inside their expected source or target."""
    if root is None or not isinstance(relative, str) or not relative or \
            "\\" in relative or ":" in relative:
        raise RuntimeError("Selected migration path is unsafe")
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or "." in rel.parts:
        raise RuntimeError("Selected migration path is unsafe")
    base = root.resolve()
    candidate = (base / rel).resolve()
    if not candidate.is_relative_to(base):
        raise RuntimeError("Selected migration path escapes its source")
    return candidate


def _checked_child_or_none(root: Path | None, relative: object) -> Path | None:
    """_checked_child for optional sources: None when unsafe, absent or not a directory."""
    if root is None or not isinstance(relative, str) or not relative:
        return None
    try:
        candidate = _checked_child(root, relative)
    except RuntimeError:
        return None
    return candidate if candidate.is_dir() and not candidate.is_symlink() else None


def _ntfs_staging_root(win_home: Path | None, win_user: str, staging_path: object) -> Path | None:
    """Locate the FileStagingService generation on the mounted Windows volume.

    The manifest records the Windows path (C:\\Users\\<user>\\AppData\\Local\\Igloo\\
    staging\\<distro>\\<generation>). Only a path inside the selected user's profile
    is accepted, and only when it still holds the manifest this agent is running
    from, so a stale or foreign generation is never read.
    """
    if win_home is None or not isinstance(staging_path, str) or not staging_path.strip():
        return None
    parts = [part for part in staging_path.replace("\\", "/").split("/") if part]
    if len(parts) < 4 or not parts[0].endswith(":"):
        return None  # not an absolute Windows path (C:\Users\...); nothing to map
    try:
        users_index = next(index for index, part in enumerate(parts) if part.lower() == "users")
    except StopIteration:
        return None
    if len(parts) <= users_index + 2 or parts[users_index + 1].lower() != win_user.lower():
        return None
    relative = "/".join(parts[users_index + 2:])
    root = _checked_child_or_none(win_home, relative)
    if root is None or not (root / "migration-manifest.json").is_file():
        return None
    return root


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
            text = stamp.read_text(encoding="utf-8", errors="replace")
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
            text = stamp.read_text(encoding="utf-8", errors="replace")
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
            except OSError as exc:
                raise RuntimeError(f"Selected Gecko profile could not be normalised: {ini}") from exc


# Portable files in a Chromium profile: no master key is involved, so they cross
# as they are. Cookies come along verbatim too - the login hook re-encrypts the
# values in place, which leaves the schema a real Chromium's instead of one we
# would have to reconstruct. See docs/reference/browser-migration.md.
_CHROMIUM_PLAIN_FILES = (
    "Bookmarks",
    "History",
    "History-wal",
    "Favicons",
    "Favicons-wal",
    "Top Sites",
    "Top Sites-wal",
    "Network/Cookies",
    "Network/Cookies-wal",
)

# Open tabs. Chromium keeps them in a directory of Session_* / Tabs_* files, not
# in one database, so this half is copied whole. A Gecko browser needs no
# equivalent: its session store sits inside the profile root that already moves.
_CHROMIUM_PLAIN_DIRS = ("Sessions",)


# Where the Fedora kickstart leaves the files it lifted off the Windows
# partition. It runs before any Flatpak exists and so cannot know the
# destination; the agent resolves that once the browsers are installed.
_CHROMIUM_STAGE_DIR = Path("/var/lib/igloo/chromium")
_STAGED_SEED_DIR = Path("/run/igloo-seed")
_STAGED_FILES_DIR = _STAGED_SEED_DIR / "files"


def _check_chromium_target_path(directory: Path, user_home: Path) -> None:
    if not directory.is_relative_to(user_home):
        raise RuntimeError("Selected Chromium profile target escapes the user home")
    current = directory
    while True:
        if current.is_symlink():
            raise RuntimeError("Selected Chromium profile target is redirected")
        if current == user_home:
            return
        current = current.parent


def _copy_private_chromium_file(source: Path, target: Path) -> None:
    """Replace a portable profile file without exposing it in old public modes."""
    if source.is_symlink() or source.parent.is_symlink() or not source.is_file():
        raise RuntimeError("Selected Chromium file source is redirected or missing")
    if target.parent.is_symlink() or not target.parent.is_dir() or \
            target.is_symlink() or (target.exists() and not target.is_file()):
        raise RuntimeError("Selected Chromium file target is redirected or unsafe")
    fd, temp_name = tempfile.mkstemp(prefix=".igloo-browser-", dir=target.parent)
    temporary = Path(temp_name)
    try:
        with os.fdopen(fd, "wb") as output, source.open("rb") as input_file:
            shutil.copyfileobj(input_file, output, length=1024 * 1024)
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)


def _place_chromium_files(name: str, src_default: Path, user_home: Path) -> None:
    """Put one profile's portable files where the installed browser will read them."""
    if src_default.is_symlink() or not src_default.is_dir():
        raise RuntimeError("Selected Chromium Default profile is redirected or missing")
    mapping = _CHROMIUM_LINUX_DIRS.get(name)
    if mapping is None:
        raise RuntimeError(f"Selected Chromium browser {name!r} has no Linux profile mapping")
    app_id, config_rel, binaries = mapping
    roots = _chromium_config_homes(app_id, binaries, user_home)
    if not roots:
        # Historical Debian/Mint migration placed portable files in the native
        # profile before Chrome was installed. Keep that selected data available
        # for a later native install; credential import still requires an
        # observed browser and its own Login Data database.
        roots = [_native_config_home(user_home)]
        logger.info("  %s is not installed; staged portable profile for a later native install", name)
    for root in roots:
        dst = root / config_rel / "Default"
        _check_chromium_target_path(dst, user_home)
        if dst.is_symlink() or (dst.exists() and not dst.is_dir()):
            raise RuntimeError("Selected Chromium target is not a regular directory")
        dst.mkdir(parents=True, exist_ok=True)
        dst.chmod(0o700)
        copied = []
        for rel in _CHROMIUM_PLAIN_FILES:
            source = src_default / rel
            if source.is_symlink() or source.parent.is_symlink():
                raise RuntimeError("Selected Chromium file resolves through a link")
            if not source.is_file():
                continue
            if rel.endswith("-wal") and not (src_default / rel[:-4]).is_file():
                raise RuntimeError(f"Selected Chromium WAL has no database: {rel}")
            target = dst / rel
            _check_chromium_target_path(target.parent, user_home)
            target.parent.mkdir(parents=True, exist_ok=True)
            _check_chromium_target_path(target.parent, user_home)
            target.parent.chmod(0o700)
            _copy_private_chromium_file(source, target)
            copied.append(rel)

        for rel in _CHROMIUM_PLAIN_DIRS:
            source = src_default / rel
            if not source.is_dir():
                continue
            _check_chromium_target_path(dst / rel, user_home)
            _copy_tree(source, dst / rel)
            copied.append(f"{rel}/")

        if copied:
            logger.info("  Placed %s profile files in %s: %s",
                        name, dst, ", ".join(copied))


def _copy_chromium_profile(name: str, src_root: Path, user_home: Path) -> None:
    """Copy one Chromium profile's portable files off the Windows partition."""
    # Default only: a "Profile N" would need an entry in Local State to be
    # reachable, and Local State is machine state we deliberately do not carry.
    src = src_root / "Default"
    if not src.is_dir():
        raise RuntimeError(f"Selected Chromium Default profile is absent for {name!r}")
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
    """Copy selected staged folders first, then any NTFS-only browser profiles."""
    user = manifest.get("user", {})
    linux_user = user.get("preferredLinuxUsername", "")
    win_user = user.get("windowsUsername", "")
    file_plan = manifest.get("files") or {}
    folders = file_plan.get("folders") or [
        {"name": name, "sourceRelativePath": name}
        for name in file_plan.get("includedFolders", [])
    ]
    browsers = [browser for browser in manifest.get("browsers", [])
                if browser.get("sourceRelativePath") or browser.get("profileStagingPath")]
    selected = bool(folders or browsers)
    if not selected:
        logger.info("No selected user files or browser profiles to migrate")
        return
    if not linux_user:
        raise RuntimeError("Selected file migration has no target user")

    user_home = Path("/home") / linux_user
    if not user_home.is_dir():
        raise RuntimeError("Selected file migration target home does not exist")

    # Sources, in order of preference:
    #   1. the seed (OEMDRV) copy, when the Windows side put user data there;
    #   2. the FileStagingService generation on the Windows volume
    #      (manifest files.stagingPath, reached read-only over ntfs-3g) - the
    #      snapshot Data Migration made, including SQLite-safe browser copies;
    #   3. the live Windows folders / browser profiles (the v0.2-alpha path).
    # The Debian family no longer carries user data on the seed, so 2 and 3 are
    # the normal case; 1 remains for seeds written by other providers.
    staged = _STAGED_FILES_DIR.is_dir()
    seed_browser = {browser.get("profileStagingPath"): bool(browser.get("profileStagingPath")) and
                    _checked_child_or_none(_STAGED_SEED_DIR, browser.get("profileStagingPath")) is not None
                    for browser in browsers}
    need_ntfs = bool((folders and not staged) or
                     any(not seed_browser.get(browser.get("profileStagingPath")) for browser in browsers))
    if need_ntfs and not win_user:
        raise RuntimeError("Selected NTFS migration has no Windows user")
    win_home = _find_windows_home(win_user) if need_ntfs else None
    if need_ntfs and win_home is None:
        raise RuntimeError("Selected file migration source home was not found")
    ntfs_staging = _ntfs_staging_root(win_home, win_user, file_plan.get("stagingPath")) if win_home else None
    if ntfs_staging is not None:
        logger.info("Using the Windows staging snapshot at %s", ntfs_staging)

    try:
        # Folders: {name, sourceRelativePath} resolved on the Windows side (Known
        # Folder API), so OneDrive-redirected folders carry their real location.
        for folder in folders:
            name = folder.get("name")
            rel = folder.get("sourceRelativePath") or name
            if not name:
                raise RuntimeError("Selected folder has no name")
            if not isinstance(name, str) or name in ("", ".", "..") or "/" in name or "\\" in name:
                raise RuntimeError("Selected folder has an unsafe name")
            if staged:
                src = _checked_child(_STAGED_FILES_DIR, name)
            elif ntfs_staging is not None and _checked_child_or_none(ntfs_staging / "files", name) is not None:
                src = _checked_child(ntfs_staging / "files", name)
            else:
                src = _checked_child(win_home, rel)
            if not src.is_dir():
                raise RuntimeError(f"Selected folder source {name!r} was not found")
            _copy_tree(src, user_home / name)
            logger.info("  Copied folder %s <- %s", name, src)

        # Browser profiles. Gecko carries {sourceRelativePath, destRelativePath}
        # and moves wholesale; Chromium carries only the source, because its
        # Linux destination depends on whether the browser is a Flatpak.
        for br in browsers:
            staged_rel = br.get("profileStagingPath")
            src_rel = br.get("sourceRelativePath")
            if staged_rel:
                if not isinstance(src_rel, str):
                    raise RuntimeError("Staged browser profile has no source binding")
                source_parts = src_rel.replace("\\", "/").split("/")
                if any(not part or part in (".", "..") or ":" in part for part in source_parts):
                    raise RuntimeError("Staged browser profile source is unsafe")
                expected = "browser-profiles/" + "/".join(source_parts)
                if staged_rel != expected:
                    raise RuntimeError("Staged browser profile path does not match its selected source")
            if staged_rel and seed_browser.get(staged_rel):
                src = _checked_child(_STAGED_SEED_DIR, staged_rel)
            elif staged_rel and ntfs_staging is not None and \
                    _checked_child_or_none(ntfs_staging, staged_rel) is not None:
                src = _checked_child(ntfs_staging, staged_rel)
            else:
                if not isinstance(src_rel, str) or not src_rel:
                    raise RuntimeError("Selected browser profile has no source")
                src = _checked_child(win_home, src_rel)
            if not src.is_dir():
                raise RuntimeError(f"Selected browser profile source {staged_rel or src_rel!r} was not found")

            if (br.get("engine") or "").lower() == "chromium":
                _copy_chromium_profile(br.get("name", ""), src, user_home)
                continue

            dst_rel = br.get("destRelativePath")
            if not dst_rel:
                raise RuntimeError("Selected browser profile has no destination")
            _copy_tree(src, _checked_child(user_home, dst_rel))
            logger.info("  Copied browser profile %s -> %s", src_rel, dst_rel)

        # Order matters: the version stamps decide whether a second Firefox is
        # needed, the move has to happen before the profiles are rewritten, and
        # normalise clears those stamps last.
        ensure_matching_firefox(user_home)
        relocate_gecko_profiles(user_home)
        normalise_gecko_profiles(user_home)

        # Fix ownership of everything we just dropped in.
        run_cmd(["chown", "-R", f"{linux_user}:{linux_user}", str(user_home)])
        logger.info("User-file migration complete")
    finally:
        if need_ntfs:
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
        # A failed password set must fail the first-boot service. Keep command
        # output out of the exception: it can contain account information.
        raise RuntimeError("Encrypted user password could not be applied")


def _ensure_dconf_local_db(*, required: bool = False) -> None:
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
    except OSError as exc:
        if required:
            raise RuntimeError("Selected wallpaper dconf profile could not be configured") from exc
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


def _nm_string(value: str) -> str:
    """Escape GLib keyfile backslashes and significant leading spaces."""
    leading_spaces = len(value) - len(value.lstrip(" "))
    return r"\s" * leading_spaces + value[leading_spaces:].replace("\\", r"\\")


def _nm_keyfile(ssid: str, security: str, psk: str | None, hidden: bool) -> str:
    connection_uuid = uuid.uuid5(uuid.NAMESPACE_URL, "igloo:wifi:" + ssid)
    lines = [
        "[connection]", f"id={_nm_string(ssid)}", f"uuid={connection_uuid}", "type=wifi",
        f"autoconnect={'false' if security == 'wpa-psk' and not psk else 'true'}",
        "", "[wifi]", "mode=infrastructure", f"ssid={_nm_string(ssid)}",
    ]
    if security == "wpa-psk":
        lines.append("security=802-11-wireless-security")
    if hidden:
        lines.append("hidden=true")
    if security == "wpa-psk":
        lines += ["", "[wifi-security]", "key-mgmt=wpa-psk"]
        if psk:
            lines.append(f"psk={_nm_string(psk)}")
        else:
            # NetworkManager asks a user-session secret agent when connecting.
            lines.append("psk-flags=1")
    lines += ["", "[ipv4]", "method=auto", "", "[ipv6]", "method=auto", ""]
    return "\n".join(lines)


def _safe_filename(ssid: str) -> str:
    """Keep distinct SSIDs distinct even when their readable stems collide."""
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", ssid).strip("_")[:48]
    digest = hashlib.sha256(ssid.encode("utf-8")).hexdigest()
    return f"igloo-{safe or 'wifi'}-{digest}.nmconnection"


def _write_wifi_profile(path: Path, content: bytes) -> None:
    """Publish a private, read-back keyfile without exposing a PSK at 0644."""
    try:
        existing = path.lstat()
    except FileNotFoundError:
        existing = None
    if existing is not None:
        if not stat.S_ISREG(existing.st_mode) or stat.S_IMODE(existing.st_mode) != 0o600:
            raise RuntimeError(f"Wi-Fi profile destination is unsafe: {path}")
        if path.read_bytes() != content:
            raise RuntimeError(f"Existing Wi-Fi profile differs from selected input: {path}")
        return

    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".writing")
    try:
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "wb") as output:
            os.fchmod(output.fileno(), 0o600)
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        if temporary.read_bytes() != content:
            raise RuntimeError(f"Wi-Fi profile staging readback failed: {path}")
        # Atomic no-clobber publication refuses a concurrently appearing path.
        os.link(temporary, path)
        if path.read_bytes() != content or stat.S_IMODE(path.lstat().st_mode) != 0o600:
            raise RuntimeError(f"Wi-Fi profile readback failed: {path}")
        if hasattr(os, "O_DIRECTORY"):
            directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
    finally:
        temporary.unlink(missing_ok=True)


def migrate_wifi(manifest: dict[str, Any]) -> None:
    """Install selected NetworkManager profiles, retaining retry input on failure."""
    nets = manifest.get("wifiNetworks", [])
    if not nets:
        logger.info("No Wi-Fi networks in manifest")
        return

    planned: list[tuple[str, str, bytes]] = []
    names: set[str] = set()
    ssids: set[str] = set()
    for net in nets:
        if not isinstance(net, dict):
            raise ValueError("Selected Wi-Fi profile is malformed")
        ssid = net.get("ssid")
        if not isinstance(ssid, str):
            raise ValueError("Selected Wi-Fi SSID is malformed")
        if not ssid:
            continue
        if any(ord(char) < 32 or ord(char) == 127 for char in ssid):
            raise ValueError("Selected Wi-Fi SSID contains a keyfile control character")
        security = net.get("security", "wpa-psk")
        if security == "unsupported":
            logger.info("Skipping enterprise/unsupported network %r", ssid)
            continue
        if security not in ("open", "wpa-psk"):
            raise ValueError("Selected Wi-Fi security is unsupported")
        psk = net.get("psk")
        if psk is not None and (not isinstance(psk, str) or any(
                ord(char) < 32 or ord(char) == 127 for char in psk)):
            raise ValueError("Selected Wi-Fi key contains a keyfile control character")
        if ssid in ssids:
            raise ValueError("Selected Wi-Fi SSID appears more than once")
        ssids.add(ssid)
        name = _safe_filename(ssid)
        if name in names:
            raise ValueError("Selected Wi-Fi filenames collide")
        names.add(name)
        planned.append((ssid, name, _nm_keyfile(
            ssid, security, psk, bool(net.get("hidden", False))).encode("utf-8")))

    if not planned:
        logger.info("Wi-Fi migration complete: no supported selected profiles")
        return
    sysconn = Path("/etc/NetworkManager/system-connections")
    sysconn.mkdir(parents=True, exist_ok=True)
    if not stat.S_ISDIR(sysconn.lstat().st_mode):
        raise RuntimeError("NetworkManager profile directory is redirected")
    for ssid, name, content in planned:
        _write_wifi_profile(sysconn / name, content)
        logger.info("Wrote NetworkManager profile for %r", ssid)
    # A failed reload is a failed migration. The original manifest and seed
    # remain available, and an exact next-boot retry reuses the private files.
    run_cmd(["nmcli", "connection", "reload"], timeout=60)
    logger.info("Wi-Fi migration complete: %d profile(s) written", len(planned))


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
    """Config roots for installed copies of this browser, most likely first.

    Flatpak overrides XDG_CONFIG_HOME, so a Flatpak build never sees ~/.config
    and a packaged build never sees ~/.var/app. Write to each root with a
    matching install; an absent browser keeps selected data for retry.
    """
    base = home or Path.home()
    roots: list[Path] = []
    if shutil.which("flatpak") and subprocess.run(
            ["flatpak", "info", app_id],
            capture_output=True, text=True, check=False).returncode == 0:
        roots.append(base / ".var" / "app" / app_id / "config")
    if any(shutil.which(b) for b in binaries):
        roots.append(_native_config_home(home))
    return roots

# Chromium timestamps count microseconds since 1601-01-01 UTC.
_CHROMIUM_EPOCH_OFFSET_US = 11644473600 * 1_000_000

# Chrome's Login Data includes related metadata and security tables. An old
# hand-built version-31 logins/meta pair passed SQLite readback but Chrome 155
# refused to migrate it. Import only after the installed browser creates its
# own complete database, leaving the encrypted envelope retryable until then.
_CHROMIUM_NATIVE_DB_TABLES = frozenset({
    "logins", "meta", "insecure_credentials",
    "sync_entities_metadata", "sync_model_metadata",
})


def _chromium_login_db_ready(db_path: Path) -> bool:
    if db_path.is_symlink() or not db_path.is_file():
        return False
    try:
        con = sqlite3.connect(db_path.resolve().as_uri() + "?mode=ro", uri=True)
        try:
            tables = {row[0] for row in con.execute(
                "SELECT name FROM sqlite_master WHERE type='table'")}
            if not _CHROMIUM_NATIVE_DB_TABLES.issubset(tables):
                return False
            meta = dict(con.execute("SELECT key, value FROM meta WHERE key IN "
                                    "('version', 'last_compatible_version')"))
            version = int(meta["version"])
            compatible = int(meta["last_compatible_version"])
            return version >= 31 and 0 < compatible <= version
        finally:
            con.close()
    except (OSError, sqlite3.Error, ValueError, KeyError, TypeError):
        return False


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


def _chromium_credential_profile_ready(profile_dir: Path) -> bool:
    """A browser-created login DB or a copied cookie jar can be imported."""
    if _chromium_login_db_ready(profile_dir / "Login Data"):
        return True
    return any((profile_dir / name).is_file() for name in _COOKIE_DB_NAMES)


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
    if not _chromium_login_db_ready(db_path):
        raise RuntimeError(
            f"Browser-created Login Data is unavailable or incomplete: {db_path}")
    # SQLite's ordinary connect silently creates absent files. The browser
    # profile must remain present between the readiness probe and insertion.
    con = sqlite3.connect(db_path.resolve().as_uri() + "?mode=rw", uri=True)
    try:
        # PRAGMA table_info: (cid, name, type, notnull, dflt_value, pk).
        table_cols = {row[1]: row for row in con.execute("PRAGMA table_info(logins)")}
        fillable_probe = _login_row_values("", "", b"", 0)
        required = [name for name, row in table_cols.items()
                    if row[3] and row[4] is None
                    and name not in fillable_probe and name != "id"]
        if required:
            raise RuntimeError(f"logins table in {db_path} has unsupported "
                               f"required columns {required}")

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
_CRED_DONE_NAME = "credentials.done.json"
_CRED_MAX_ATTEMPTS = 3


def _persist_credential_retry(store: Path, data: dict) -> None:
    """Replace the owner-only encrypted retry store without truncating it."""
    payload = json.dumps(data).encode("utf-8")
    temporary = store.with_name(store.name + "." + uuid.uuid4().hex + ".writing")
    try:
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "wb") as output:
            output.write(payload)
            output.flush()
            os.fsync(output.fileno())
        if temporary.read_bytes() != payload:
            raise OSError("Credential retry staging readback failed")
        os.replace(temporary, store)
        if store.read_bytes() != payload:
            raise OSError("Credential retry readback failed")
        directory = os.open(store.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        temporary.unlink(missing_ok=True)


def _safe_credential_store_path(home: Path) -> tuple[list[Path], Path]:
    """Create only the expected private directories; refuse link/mount redirection."""
    if home.parent.is_symlink() or home.is_symlink() or not home.is_dir():
        raise RuntimeError("The credential target home is redirected or missing")
    home_info = home.stat()
    if home_info.st_mode & 0o022:
        raise RuntimeError("The credential target home is writable by another user")
    home_device = home_info.st_dev
    directories = []
    current = home
    for name in (".local", "share", "igloo"):
        current = current / name
        try:
            observed = current.lstat()
        except FileNotFoundError:
            current.mkdir(mode=0o700)
            observed = current.lstat()
        if not stat.S_ISDIR(observed.st_mode) or observed.st_dev != home_device or \
                observed.st_uid not in (0, home_info.st_uid) or observed.st_mode & 0o022:
            raise RuntimeError("The credential store directory is redirected")
        directories.append(current)
    store = current / "credentials.json"
    try:
        observed = store.lstat()
    except FileNotFoundError:
        pass
    else:
        if not stat.S_ISREG(observed.st_mode) or observed.st_dev != home_device or \
                observed.st_uid not in (0, home_info.st_uid):
            raise RuntimeError("The existing credential store is redirected")
    return directories, store


def _read_private_credential_json(path: Path, kind: str) -> dict:
    """Read one owner-only credential record without following a redirected path."""
    before = path.lstat()
    if not stat.S_ISREG(before.st_mode) or stat.S_IMODE(before.st_mode) != 0o600:
        raise RuntimeError(f"{kind} is not a private regular file")
    fd = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
    with os.fdopen(fd, "rb") as stream:
        opened = os.fstat(stream.fileno())
        if (opened.st_dev, opened.st_ino, opened.st_uid) != \
                (before.st_dev, before.st_ino, before.st_uid) or \
                not stat.S_ISREG(opened.st_mode) or stat.S_IMODE(opened.st_mode) != 0o600:
            raise RuntimeError(f"{kind} changed during read")
        payload = stream.read()
    after = path.lstat()
    if (after.st_dev, after.st_ino, after.st_uid) != \
            (before.st_dev, before.st_ino, before.st_uid) or \
            not stat.S_ISREG(after.st_mode) or stat.S_IMODE(after.st_mode) != 0o600:
        raise RuntimeError(f"{kind} changed during read")
    try:
        data = json.loads(payload)
    except (UnicodeError, ValueError) as error:
        raise RuntimeError(f"{kind} is malformed") from error
    if not isinstance(data, dict):
        raise RuntimeError(f"{kind} is malformed")
    return data


def _credential_selection_sha256(entries: list[dict[str, str]]) -> str:
    canonical = json.dumps(entries, sort_keys=True, separators=(",", ":"),
                           ensure_ascii=False).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def _validate_existing_credential_retry(store: Path,
                                        expected: list[dict[str, str]]) -> int:
    """Preserve only the remaining envelopes from a prior first-boot pass."""
    data = _read_private_credential_json(store, "Existing credential retry store")
    keys = {"attempts", "browsers"}
    if "selectionSha256" in data:
        keys.add("selectionSha256")
    if set(data) != keys or \
            type(data["attempts"]) is not int or \
            not 0 <= data["attempts"] <= _CRED_MAX_ATTEMPTS or \
            not isinstance(data["browsers"], list) or not data["browsers"]:
        raise RuntimeError("Existing credential retry store is malformed")
    if "selectionSha256" in data and \
            data["selectionSha256"] != _credential_selection_sha256(expected):
        raise RuntimeError("Existing credential retry store differs from the selected envelopes")
    remaining = data["browsers"]
    if any(not isinstance(entry, dict) or set(entry) != {"name", "blob"} or
           not isinstance(entry["name"], str) or
           not isinstance(entry["blob"], str) or not entry["blob"]
           for entry in remaining):
        raise RuntimeError("Existing credential retry store is malformed")
    original = Counter((entry["name"], entry["blob"]) for entry in expected)
    retained = Counter((entry["name"], entry["blob"]) for entry in remaining)
    if any(count > original[pair] for pair, count in retained.items()):
        raise RuntimeError("Existing credential retry store differs from the selected envelopes")
    return len(remaining)


def _validate_finished_credential_receipt(receipt: Path, expected_hash: str,
                                          home: Path) -> str:
    observed = receipt.lstat()
    if observed.st_dev != home.stat().st_dev or \
            observed.st_uid not in (0, home.stat().st_uid):
        raise RuntimeError("Credential completion receipt is redirected")
    data = _read_private_credential_json(receipt, "Credential completion receipt")
    if set(data) != {"version", "selectionSha256", "status"} or \
            data["version"] != 1 or data["selectionSha256"] != expected_hash or \
            data["status"] not in ("consumed", "exhausted"):
        raise RuntimeError("Credential completion receipt differs from selected envelopes")
    return data["status"]


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
        raise RuntimeError("Chromium credentials have no target user")
    if re.fullmatch(r"[a-z_][a-z0-9_.-]{0,31}", linux_user) is None:
        raise RuntimeError("Chromium credentials have an unsafe target user")

    home = Path("/home") / linux_user
    if not home.is_dir() or home.is_symlink():
        raise RuntimeError("Chromium credential target home does not exist")

    try:
        import pwd

        account = pwd.getpwnam(linux_user)
        if account.pw_dir != str(home) or home.stat().st_uid != account.pw_uid:
            raise RuntimeError("The credential target account does not own its home")
        directories, store = _safe_credential_store_path(home)
        selected = [{"name": e.get("name", ""), "blob": e["credentialsBlob"]}
                    for e in entries]
        if any(not isinstance(entry["name"], str) or
               not isinstance(entry["blob"], str) or not entry["blob"]
               for entry in selected):
            raise RuntimeError("Chromium credential envelope selection is invalid")
        selection_hash = _credential_selection_sha256(selected)
        receipt = store.with_name(_CRED_DONE_NAME)
        if receipt.exists() or receipt.is_symlink():
            status = _validate_finished_credential_receipt(receipt, selection_hash, home)
            if store.exists():
                before = store.lstat()
                _validate_existing_credential_retry(store, selected)
                after = store.lstat()
                if (before.st_dev, before.st_ino, before.st_uid) != \
                        (after.st_dev, after.st_ino, after.st_uid):
                    raise RuntimeError("Credential retry store changed before retirement")
                store.unlink()
                _fsync_directory(store.parent)
            logger.info("Preserved %s credential import from a prior first-boot pass", status)
        elif store.exists():
            remaining = _validate_existing_credential_retry(store, selected)
            logger.info("Preserved %d remaining credential envelope(s) from a prior "
                        "first-boot pass", remaining)
        else:
            _persist_credential_retry(store, {"attempts": 0, "browsers": selected,
                                            "selectionSha256": selection_hash})
        if _safe_credential_store_path(home)[1] != store:
            raise RuntimeError("The credential store path changed during staging")
        for path in [*directories, *([store] if store.exists() else []),
                     *([receipt] if receipt.exists() else [])]:
            os.chown(path, account.pw_uid, account.pw_gid, follow_symlinks=False)

        hook = Path("/opt/igloo/credential-import.sh")
        hook.write_text(
            "#!/bin/sh\n"
            "# iGloo credential import - retries at each login until the\n"
            "# envelope store is consumed, then does nothing.\n"
            'STORE="$HOME/' + _CRED_STORE_REL + '"\n'
            '[ -f "$STORE" ] || exit 0\n'
            # Distribution welcome windows start after XDG autostart and can
            # cover an earlier password dialog. Open ours after they settle.
            'sleep 12\n'
            # Debian's GNOME Initial Setup and Mint Welcome can remain open
            # until the user closes them. Wait for their actual window process
            # so the password dialog is not hidden behind the first-run UI.
            "while command -v pgrep >/dev/null 2>&1 && "
            "pgrep -u \"$(id -u)\" -f '[g]nome-initial-setup --existing-user|"
            "[/]usr/lib/linuxmint/mintwelcome/mintwelcome[.]py' "
            ">/dev/null 2>&1; do\n"
            '    [ -f "$STORE" ] || exit 0\n'
            '    sleep 2\n'
            'done\n'
            '[ -f "$STORE" ] || exit 0\n'
            # GNOME opens its overview on a new account's first login. A
            # Zenity dialog is visible there, but keystrokes still go to the
            # overview search. Close it through GNOME Shell's session D-Bus
            # property and verify the readback before asking for a password.
            # Keep the encrypted store for the next login if Shell is not
            # ready; never invite a password into an unfocused dialog.
            'case ":${XDG_CURRENT_DESKTOP:-}:" in\n'
            '    *:GNOME:*)\n'
            '        command -v gdbus >/dev/null 2>&1 || exit 0\n'
            '        attempt=0\n'
            '        while [ "$attempt" -lt 15 ]; do\n'
            '            overview=$(gdbus call --session --dest org.gnome.Shell '
            '--object-path /org/gnome/Shell --method '
            'org.freedesktop.DBus.Properties.Get '
            'org.gnome.Shell OverviewActive 2>/dev/null) || overview=\n'
            '            case "$overview" in\n'
            "                *'<false>'*) break ;;\n"
            "                *'<true>'*)\n"
            '                    gdbus call --session --dest org.gnome.Shell '
            '--object-path /org/gnome/Shell --method '
            'org.freedesktop.DBus.Properties.Set '
            "org.gnome.Shell OverviewActive '<false>' "
            '>/dev/null 2>&1 || true\n'
            '                    ;;\n'
            '            esac\n'
            '            attempt=$((attempt + 1))\n'
            '            sleep 2\n'
            '        done\n'
            '        [ "$attempt" -lt 15 ] || exit 0\n'
            '        sleep 1\n'
            '        ;;\n'
            'esac\n'
            '[ -f "$STORE" ] || exit 0\n'
            "exec python3 /opt/igloo/agent.py --import-credentials\n",
            encoding="utf-8")
        hook.chmod(0o755)

        autostart = Path("/etc/xdg/autostart/igloo-credential-import.desktop")
        _write_credential_autostart(autostart)

        logger.info("Staged %d credential envelope(s) for %s and installed the "
                    "login hook", len(entries), linux_user)
    except OSError:
        logger.exception("Could not stage the credential import")
        raise


def _write_credential_autostart(autostart: Path) -> None:
    """Make the system XDG hook readable despite first boot's private umask."""
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
    autostart.chmod(0o644)


def _ask_password() -> tuple[bool, str | None]:
    """Ask the desktop for the account password.

    Returns (asked, password). asked is False when neither dialog tool exists,
    which is different from the user cancelling."""
    attempts = [
        (["zenity", "--forms",
          "--title=Import saved browser passwords",
          "--text=Enter your Linux account password to import saved browser passwords.",
          "--add-password=Linux account password", "--timeout=300"], None),
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


def _finish_credential_import(store: Path, data: dict, status: str) -> None:
    """Commit a private completion receipt before retiring encrypted inputs."""
    selection_hash = data.get("selectionSha256")
    if selection_hash is None:
        # Legacy stores did not carry the full selected inventory. A receipt
        # from a partially imported legacy store will fail closed if first
        # boot later retries against the original complete manifest.
        selection_hash = _credential_selection_sha256(data.get("browsers", []))
    if not isinstance(selection_hash, str) or not re.fullmatch(
            r"[0-9a-f]{64}", selection_hash):
        raise RuntimeError("Credential selection binding is invalid")
    receipt = store.with_name(_CRED_DONE_NAME)
    _persist_credential_retry(receipt, {"version": 1,
                                        "selectionSha256": selection_hash,
                                        "status": status})
    store.unlink()
    _fsync_directory(store.parent)


def _read_user_credential_store(store: Path, home: Path) -> dict:
    """Reject damaged or redirected retry input before it can be retired."""
    data = _read_private_credential_json(store, "Credential store")
    observed = store.lstat()
    owner = home.stat()
    if observed.st_dev != owner.st_dev or observed.st_uid != owner.st_uid:
        raise RuntimeError("Credential store has an unexpected owner or device")
    keys = {"attempts", "browsers"}
    if "selectionSha256" in data:
        keys.add("selectionSha256")
        if not isinstance(data["selectionSha256"], str) or not re.fullmatch(
                r"[0-9a-f]{64}", data["selectionSha256"]):
            raise RuntimeError("Credential store has an invalid selection binding")
    if set(data) != keys or type(data["attempts"]) is not int or \
            not 0 <= data["attempts"] <= _CRED_MAX_ATTEMPTS or \
            not isinstance(data["browsers"], list) or not data["browsers"] or \
            any(not isinstance(entry, dict) or set(entry) != {"name", "blob"} or
                not isinstance(entry["name"], str) or
                not isinstance(entry["blob"], str) or not entry["blob"]
                for entry in data["browsers"]):
        raise RuntimeError("Credential store is malformed")
    return data


def run_user_credential_import() -> int:
    """Import the staged Chromium credentials. Runs as the user at login."""
    home = Path.home()
    store = home / _CRED_STORE_REL
    if not store.is_file():
        return 0

    try:
        data = _read_user_credential_store(store, home)
    except (OSError, RuntimeError):
        logger.exception("Credential store is invalid - retaining it for diagnosis")
        return 0

    attempts = data["attempts"] + 1
    entries = data["browsers"]
    receipt = store.with_name(_CRED_DONE_NAME)
    if receipt.exists() or receipt.is_symlink():
        selection_hash = data.get("selectionSha256")
        if selection_hash is None:
            selection_hash = _credential_selection_sha256(entries)
        _validate_finished_credential_receipt(receipt, selection_hash, Path.home())
        store.unlink()
        _fsync_directory(store.parent)
        return 0
    if attempts > _CRED_MAX_ATTEMPTS:
        logger.info("Giving up on the credential import after %d attempt(s)",
                    attempts - 1)
        _finish_credential_import(store, data, "exhausted")
        return 0

    try:
        _aes_self_test()
    except AssertionError:
        logger.exception("AES self-test failed - credential import disabled")
        return 0

    # An installed browser still needs to create its own Login Data first.
    # A copied Cookies jar can be imported without a Login Data database.
    # Retain encrypted envelopes without prompting until one target is ready.
    if not any(
            (mapping := _CHROMIUM_LINUX_DIRS.get(entry.get("name", "")))
            and any(_chromium_credential_profile_ready(
                        root / mapping[1] / "Default")
                    for root in _chromium_config_homes(mapping[0], mapping[2]))
            for entry in entries):
        logger.info("Open the selected browser once, close it, then sign in "
                    "again to finish password import; encrypted credentials "
                    "retained until its native Login Data is ready")
        return 0

    asked, password = _ask_password()
    if not asked:
        logger.warning("Neither zenity nor kdialog is installed - cannot ask "
                       "for the password, leaving the envelopes in place")
        return 0
    if not password:
        logger.info("Password prompt closed or failed; retaining credential envelopes "
                    "without consuming an attempt")
        return 0

    imported = 0
    wrong_password = False
    write_failed = False
    remaining = []
    for entry in entries:
        name = entry.get("name", "")
        mapping = _CHROMIUM_LINUX_DIRS.get(name)
        if mapping is None:
            logger.warning("No Linux profile mapping for browser %r - retaining envelope", name)
            write_failed = True
            remaining.append(entry)
            continue
        app_id, config_rel, binaries = mapping
        try:
            payload = _decrypt_envelope(entry["blob"], password)
        except Exception:
            # A bad tag is indistinguishable from a corrupt envelope here, but
            # a wrong password is overwhelmingly the likelier cause.
            wrong_password = True
            remaining.append(entry)
            logger.warning("Could not decrypt the envelope for %s", name)
            continue

        logins = payload.get("logins", [])
        cookies = payload.get("cookies", [])
        if not logins and not cookies:
            continue
        # Counted per browser, not per root: the same logins written to both a
        # Flatpak and a packaged install is one migration, not two.
        landed = 0
        browser_failed = False
        roots = list(_chromium_config_homes(app_id, binaries))
        if not roots:
            browser_failed = True
            logger.warning("No installed %s profile root accepts the selected credentials", name)
        for root in roots:
            profile_dir = root / config_rel / "Default"
            try:
                if logins:
                    profile_dir.mkdir(parents=True, exist_ok=True)
                    landed = max(landed,
                                 _import_into_login_data(profile_dir / "Login Data", logins))
                    logger.info("Imported %s logins into %s", name, profile_dir)
            except Exception:
                browser_failed = True
                logger.exception("Failed to import credentials for %s into %s "
                                 "(non-fatal)", name, root)
            try:
                # Cookies are a separate failure domain: the jar being unusable
                # must not cost the passwords that came from the same profile.
                if cookies:
                    rewritten = _reencrypt_cookies(profile_dir, cookies)
                    if rewritten != len(cookies):
                        raise RuntimeError("Selected browser cookies are missing from the copied profile")
                    logger.info("Re-encrypted %d %s cookie(s) in %s",
                                rewritten, name, profile_dir)
            except Exception:
                browser_failed = True
                logger.exception("Failed to re-encrypt cookies for %s in %s "
                                 "(non-fatal)", name, profile_dir)
        imported += landed
        if browser_failed:
            write_failed = True
            remaining.append(entry)

    if remaining:
        data["browsers"] = remaining
        if wrong_password and not write_failed:
            data["attempts"] = attempts
        _persist_credential_retry(store, data)
        logger.info("Imported %d browser login(s); %d encrypted browser "
                    "envelope(s) retained for retry", imported, len(remaining))
        return 0

    _finish_credential_import(store, data, "consumed")
    logger.info("Imported %d browser login(s); credential store removed", imported)
    return 0


# === END AGENT SECTION ===================================================


def _redacted_manifest_bytes(source: bytes) -> bytes:
    data: dict[str, Any] = json.loads(source.decode("utf-8"))
    user = data.get("user")
    if isinstance(user, dict):
        user.pop("linuxPasswordCrypted", None)
    for net in data.get("wifiNetworks", []):
        if isinstance(net, dict):
            net.pop("psk", None)
    for browser in data.get("browsers", []):
        if isinstance(browser, dict):
            browser.pop("credentialsBlob", None)
    return (json.dumps(data, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _fsync_directory(path: Path) -> None:
    fd = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def _atomic_copy(source: Path, destination: Path, *, private: bool) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=".igloo-", dir=destination.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as target, source.open("rb") as origin:
            shutil.copyfileobj(origin, target, 1024 * 1024)
            target.flush()
            os.fsync(target.fileno())
        if private:
            temporary.chmod(0o600)
        os.replace(temporary, destination)
        _fsync_directory(destination.parent)
    finally:
        temporary.unlink(missing_ok=True)


def _discard_bound_seed_temporary(destination: Path, identity_digest: str,
                                  relative_name: str) -> Path:
    """Remove only the exact redacted-byte FAT temp named by the journal."""
    suffix = hashlib.sha256((identity_digest + ":" + relative_name).encode("ascii")).hexdigest()[:32]
    temporary = destination.with_name(".igloo-redact-" + suffix)
    if temporary.is_symlink():
        raise RuntimeError("Bound OEMDRV redaction temporary is redirected")
    if temporary.exists():
        if not temporary.is_file() or temporary.stat().st_nlink != 1:
            raise RuntimeError("Bound OEMDRV redaction temporary is ambiguous")
        # The private journal was durable before the first FAT write. This
        # exact name can contain only a partial copy of planned redacted bytes
        # after SIGKILL or power loss; the protected source is hash-checked.
        temporary.unlink()
        _fsync_directory(destination.parent)
    return temporary


def _replace_bound_seed_file(source: Path, destination: Path, expected_hash: str,
                             identity_digest: str, relative_name: str) -> None:
    """Publish redacted bytes with one journal-derived, retryable FAT temp."""
    temporary = _discard_bound_seed_temporary(destination, identity_digest, relative_name)
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(fd, "wb") as target, source.open("rb") as origin:
            shutil.copyfileobj(origin, target, 1024 * 1024)
            target.flush()
            os.fsync(target.fileno())
        if _sha256_file(temporary) != expected_hash:
            raise RuntimeError("Bound OEMDRV redaction temporary differs from protected source")
        os.replace(temporary, destination)
        _fsync_directory(destination.parent)
    finally:
        temporary.unlink(missing_ok=True)


def _atomic_bytes(data: bytes, destination: Path, *, private: bool) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=".igloo-", dir=destination.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as target:
            target.write(data)
            target.flush()
            os.fsync(target.fileno())
        if private:
            temporary.chmod(0o600)
        os.replace(temporary, destination)
        _fsync_directory(destination.parent)
    finally:
        temporary.unlink(missing_ok=True)


def _redacted_preseed_bytes(source: bytes, password_hash: bytes) -> bytes:
    pattern = rb"(?m)^(d-i passwd/user-password-crypted password )([^\r\n]+)"
    matches = list(re.finditer(pattern, source))
    if len(matches) != 1 or matches[0].group(2) != password_hash:
        raise RuntimeError("Bound preseed does not contain the expected password hash")
    # Keep the installer config parseable while locking a reused account. The
    # replacement is the same length so a cpio entry's size remains valid.
    replacement = b"!" + b" " * (len(password_hash) - 1)
    start, end = matches[0].span(2)
    return source[:start] + replacement + source[end:]


def _redacted_initrd_overlay(source: Path, destination: Path, password_hash: bytes) -> None:
    """Replace only DirectInstallService's final gzip/newc preseed member."""
    size = source.stat().st_size
    with source.open("rb") as stream:
        stream.seek(max(0, size - 4 * 1024 * 1024))
        tail = stream.read()
    candidate = None
    for index in range(len(tail) - 3, -1, -1):
        if tail[index:index + 3] != b"\x1f\x8b\x08":
            continue
        try:
            decoder = zlib.decompressobj(31)
            cpio = decoder.decompress(tail[index:]) + decoder.flush()
        except zlib.error:
            continue
        if decoder.eof and not decoder.unused_data and cpio.startswith(b"070701"):
            candidate = (size - len(tail) + index, cpio)
            break
    if candidate is None:
        raise RuntimeError("Bound initrd has no final injected gzip/newc preseed")
    prefix_length, cpio = candidate
    if len(cpio) < 110 or cpio[:6] != b"070701":
        raise RuntimeError("Injected preseed cpio header is invalid")
    try:
        payload_size = int(cpio[54:62], 16)
        name_size = int(cpio[94:102], 16)
    except ValueError as error:
        raise RuntimeError("Injected preseed cpio lengths are invalid") from error
    name_start = 110
    data_start = (name_start + name_size + 3) & ~3
    data_end = data_start + payload_size
    if (name_size < 2 or cpio[name_start:name_start + name_size] != b"preseed.cfg\0"
            or data_end > len(cpio)):
        raise RuntimeError("Injected initrd entry is not preseed.cfg")
    replacement = _redacted_preseed_bytes(cpio[data_start:data_end], password_hash)
    redacted_cpio = cpio[:data_start] + replacement + cpio[data_end:]
    destination.parent.mkdir(parents=True, exist_ok=True)
    with source.open("rb") as origin, destination.open("wb") as target:
        remaining = prefix_length
        while remaining:
            block = origin.read(min(1024 * 1024, remaining))
            if not block:
                raise RuntimeError("Initrd changed while preparing redaction")
            target.write(block)
            remaining -= len(block)
        target.write(gzip.compress(redacted_cpio, mtime=0))
        target.flush()
        os.fsync(target.fileno())


def _verify_seed_block_identity(seed_mount: Path, disk_guid: str, partition_guid: str) -> None:
    if not seed_mount.is_mount():
        raise RuntimeError("OEMDRV seed is not mounted")
    source = run_cmd(["findmnt", "-rn", "-o", "SOURCE", "--mountpoint", str(seed_mount)]).stdout.strip()
    if not source.startswith("/dev/") or "\n" in source:
        raise RuntimeError("Mounted OEMDRV has no unique block source")
    exact = Path("/dev/disk/by-partuuid") / partition_guid
    if not exact.exists() or not stat.S_ISBLK(exact.stat().st_mode):
        raise RuntimeError("Bound seed PARTUUID device is unavailable")
    if os.stat(source).st_rdev != exact.stat().st_rdev or seed_mount.stat().st_dev != exact.stat().st_rdev:
        raise RuntimeError("Mounted OEMDRV device differs from bound PARTUUID")
    parent = run_cmd(["lsblk", "-dnro", "PKNAME", source]).stdout.strip()
    if not parent or "/" in parent or "\n" in parent:
        raise RuntimeError("Mounted OEMDRV has no unique parent disk")
    observed_disk = run_cmd(["lsblk", "-dnro", "PTUUID", f"/dev/{parent}"]).stdout.strip()
    if observed_disk.lower() != disk_guid.lower():
        raise RuntimeError("Mounted OEMDRV parent disk differs from bound GPT disk")


_SEED_SECRET_FILES = {"migration-manifest.json", "preseed.cfg", "igloo-boot/initrd"}
_COMMON_SEED_AGENT_FILES = {
    "agent.py", "first-boot.sh", "display-apply.py", "display-apply-gnome.py",
    "igloo_boot.py", "igloo-first-boot.service",
    "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz",
}
_MINT_SEED_AGENT_FILES = _COMMON_SEED_AGENT_FILES | {
    "igloo-bootstrap.sh", "igloo-bootstrap.service", "igloo-verify-seed.py",
}
# Inventory bounds cap memory while reading an untrusted seed; they do not decide how
# much a user may migrate. They must match SeedIdentityWriter on the Windows side
# (1,000,000 files / 2,000,000 entries / 256,000,000 path characters).
_MAX_SEED_USER_FILES = 1_000_000
_MAX_SEED_USER_ENTRIES = 2_000_000
_MAX_SEED_USER_ROOTS = 1_024
_MAX_SEED_USER_PATH_CHARACTERS = 256_000_000


def _bound_seed_agent_paths(seed_mount: Path, distro_id: str) -> set[str]:
    expected = (_COMMON_SEED_AGENT_FILES if distro_id == "debian" else
                _MINT_SEED_AGENT_FILES if distro_id == "linuxmint-cinnamon" else None)
    if expected is None:
        raise RuntimeError("Unsupported Debian-family OEMDRV agent distribution")
    directory = seed_mount / "igloo-agent"
    if directory.is_symlink() or not directory.is_dir():
        raise RuntimeError("OEMDRV agent directory is missing or redirected")
    observed = set()
    for entry in directory.iterdir():
        if entry.is_symlink() or not entry.is_file():
            raise RuntimeError("OEMDRV agent inventory contains a redirected or nested entry")
        observed.add(entry.name)
    if observed != expected:
        raise RuntimeError("OEMDRV agent inventory differs from provider payload")
    return {"igloo-agent/" + name for name in expected}


def _bound_seed_media_paths(seed_mount: Path, manifest: dict[str, Any]) -> set[str]:
    wallpaper = manifest.get("wallpaper") or {}
    picture = manifest.get("accountPicture") or {}
    if not isinstance(wallpaper, dict) or not isinstance(picture, dict):
        raise RuntimeError("Selected OEMDRV media manifest is invalid")
    selected = set()
    wallpaper_name = wallpaper.get("fileName") or ""
    picture_name = picture.get("fileName") or ""
    if wallpaper_name:
        if not isinstance(wallpaper_name, str) or not re.fullmatch(
                r"igloo-wallpaper\.[a-z0-9]{2,8}", wallpaper_name, re.IGNORECASE):
            raise RuntimeError("Selected OEMDRV wallpaper name is unsafe")
        selected.add(wallpaper_name)
    if picture_name:
        if picture_name != "igloo-avatar.jpg":
            raise RuntimeError("Selected OEMDRV account picture name is unsafe")
        selected.add(picture_name)
    for entry in seed_mount.iterdir():
        if entry.name.lower().startswith(("igloo-wallpaper.", "igloo-avatar.")) and \
                entry.name not in selected:
            raise RuntimeError("OEMDRV contains unselected migration media")
    return selected


def _safe_seed_user_segment(segment: str) -> bool:
    base = segment.split(".", 1)[0].upper()
    reserved = base in {"CON", "PRN", "AUX", "NUL"} or re.fullmatch(
        r"(?:COM|LPT)[1-9]", base) is not None
    return (bool(segment) and not segment.isspace() and segment not in (".", "..")
            and segment == unicodedata.normalize("NFC", segment) and not reserved
            and not segment.endswith((" ", "."))
            and all(unicodedata.category(char) != "Cc" and
                    char not in '<>":|?*/\\' for char in segment))


def _safe_seed_user_path(path: str) -> bool:
    return (isinstance(path, str) and 0 < len(path) <= 1024 and
            "\\" not in path and all(_safe_seed_user_segment(part)
                                      for part in path.split("/")))


def _bound_seed_user_paths(seed_mount: Path, manifest: dict[str, Any]) -> set[str]:
    roots: set[str] = set()
    root_aliases: set[str] = set()

    def add_root(path: str) -> None:
        if not _safe_seed_user_path(path):
            raise RuntimeError("Selected OEMDRV user-data path is unsafe")
        alias = unicodedata.normalize("NFC", path).casefold()
        if alias in root_aliases:
            raise RuntimeError("Selected OEMDRV user-data roots are ambiguous")
        root_aliases.add(alias)
        roots.add(path)
        if len(roots) > _MAX_SEED_USER_ROOTS:
            raise RuntimeError("Selected OEMDRV user-data roots are excessive")

    files = manifest.get("files")
    if files is None:
        files = {}
    if not isinstance(files, dict):
        raise RuntimeError("Selected OEMDRV folder metadata is invalid")
    folders = files.get("folders")
    if folders is None:
        folders = []
    if not isinstance(folders, list):
        raise RuntimeError("Selected OEMDRV folder metadata is invalid")
    if folders:
        if any(not isinstance(folder, dict) or not isinstance(folder.get("name"), str)
               for folder in folders):
            raise RuntimeError("Selected OEMDRV folder metadata is invalid")
        folder_names = [folder["name"] for folder in folders]
    else:
        folder_names = files.get("includedFolders")
        if folder_names is None:
            folder_names = []
        if not isinstance(folder_names, list) or any(
                not isinstance(name, str) for name in folder_names):
            raise RuntimeError("Selected OEMDRV folder metadata is invalid")
    staging_path = files.get("stagingPath")
    if staging_path is not None and not isinstance(staging_path, str):
        raise RuntimeError("Selected OEMDRV folder staging metadata is invalid")
    # Bind folders only when the seed actually carries them; a staging path in the
    # manifest alone means they are read from the Windows volume at first boot.
    if (seed_mount / "files").exists():
        for name in folder_names:
            if not _safe_seed_user_segment(name):
                raise RuntimeError("Selected OEMDRV folder name is unsafe")
            add_root("files/" + name)

    browsers = manifest.get("browsers")
    if browsers is None:
        browsers = []
    if not isinstance(browsers, list):
        raise RuntimeError("Selected OEMDRV browser metadata is invalid")
    for browser in browsers:
        if not isinstance(browser, dict):
            raise RuntimeError("Selected OEMDRV browser metadata is invalid")
        staged = browser.get("profileStagingPath")
        if staged is None or staged == "":
            continue
        source = browser.get("sourceRelativePath")
        if not isinstance(staged, str) or not isinstance(source, str):
            raise RuntimeError("Selected OEMDRV browser has no source binding")
        source = source.replace("\\", "/")
        if not _safe_seed_user_path(source) or staged != "browser-profiles/" + source:
            raise RuntimeError("Selected OEMDRV browser path differs from its selected source")
        if (seed_mount / staged).is_dir():
            add_root(staged)

    observed: set[str] = set()
    observed_directories: set[str] = set()
    aliases: set[str] = set()
    entries = 0
    path_characters = 0
    for namespace in ("files", "browser-profiles"):
        namespace_root = seed_mount / namespace
        chosen = {root for root in roots if root.startswith(namespace + "/")}
        if namespace_root.is_symlink() or (namespace_root.exists() and
                                           (not namespace_root.is_dir() or not chosen)):
            raise RuntimeError("OEMDRV contains an unselected or redirected user-data root")
        if not namespace_root.is_dir():
            if chosen:
                raise RuntimeError("Selected OEMDRV user-data root is missing")
            continue
        pending = [namespace_root]
        while pending:
            for entry in pending.pop().iterdir():
                if entry.is_symlink():
                    raise RuntimeError("Selected OEMDRV user data contains a redirected entry")
                relative = entry.relative_to(seed_mount).as_posix()
                if not _safe_seed_user_path(relative):
                    raise RuntimeError("Selected OEMDRV user-data path is unsafe")
                alias = unicodedata.normalize("NFC", relative).casefold()
                entries += 1
                if alias in aliases or entries > _MAX_SEED_USER_ENTRIES:
                    raise RuntimeError("Selected OEMDRV user-data inventory is ambiguous or excessive")
                aliases.add(alias)
                if entry.is_dir():
                    if not any(relative == root or relative.startswith(root + "/") or
                               root.startswith(relative + "/") for root in chosen):
                        raise RuntimeError("OEMDRV contains an unselected user-data directory")
                    observed_directories.add(relative)
                    pending.append(entry)
                elif entry.is_file():
                    if not any(relative.startswith(root + "/") for root in chosen):
                        raise RuntimeError("OEMDRV contains an unselected user-data file")
                    path_characters += len(relative)
                    if len(observed) >= _MAX_SEED_USER_FILES or \
                            path_characters > _MAX_SEED_USER_PATH_CHARACTERS:
                        raise RuntimeError("Selected OEMDRV user-data inventory is too large")
                    observed.add(relative)
                else:
                    raise RuntimeError("Selected OEMDRV user data contains a special entry")
    for root in roots:
        selected = seed_mount / root
        if selected.is_symlink() or not selected.is_dir():
            raise RuntimeError("Selected OEMDRV user-data directory is missing or redirected")
    bound_directories: set[str] = set()
    for path in roots:
        parts = path.split("/")
        bound_directories.update("/".join(parts[:index])
                                 for index in range(1, len(parts) + 1))
    for path in observed:
        parts = path.split("/")
        bound_directories.update("/".join(parts[:index])
                                 for index in range(1, len(parts)))
    for directory in observed_directories:
        if directory not in bound_directories:
            raise RuntimeError("Selected OEMDRV user data contains an unbound empty directory")
    return observed


def redact_seed(manifest: dict[str, Any], manifest_path: Path,
                seed_mount: Path = Path("/run/igloo-seed")) -> None:
    """Scrub only identity-bound OEMDRV files after migration succeeds.

    The installed-root backup and journal survive until first-boot.sh has
    durably marked success. A retry accepts either the original bound hash or
    the exact hash of a completed redaction, never an arbitrary changed file.
    """
    private_root = manifest_path.parent
    protected_identity = private_root / "seed-identity.json"
    seed_identity = seed_mount / "igloo-seed-identity.json"
    if not protected_identity.is_file() or not seed_identity.is_file():
        raise RuntimeError("Exact OEMDRV identity binding is missing")
    protected_bytes = protected_identity.read_bytes()
    if protected_bytes != seed_identity.read_bytes():
        raise RuntimeError("Installed and mounted OEMDRV identity copies differ")
    if stat.S_IMODE(protected_identity.stat().st_mode) & 0o077:
        raise RuntimeError("Installed OEMDRV identity copy is not private")
    binding = json.loads(protected_bytes)
    if binding.get("version") != 1:
        raise RuntimeError("Unsupported OEMDRV identity binding")
    try:
        disk_guid = str(uuid.UUID(binding["diskGuid"]))
        partition_guid = str(uuid.UUID(binding["partitionGuid"]))
    except (KeyError, ValueError, TypeError) as error:
        raise RuntimeError("Invalid OEMDRV GPT identity binding") from error
    _verify_seed_block_identity(seed_mount, disk_guid, partition_guid)
    records = binding.get("files")
    if not isinstance(records, list):
        raise RuntimeError("OEMDRV identity has no bound files")
    agent_paths = _bound_seed_agent_paths(seed_mount, manifest.get("distroId"))
    media_paths = _bound_seed_media_paths(seed_mount, manifest)
    user_paths = _bound_seed_user_paths(seed_mount, manifest)
    allowed_paths = _SEED_SECRET_FILES | agent_paths | media_paths | user_paths
    bound_hashes: dict[str, str] = {}
    for record in records:
        if not isinstance(record, dict) or record.get("path") not in allowed_paths:
            raise RuntimeError("OEMDRV identity names an unexpected file")
        name, digest = record["path"], record.get("sha256")
        if name in bound_hashes or not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
            raise RuntimeError("OEMDRV identity contains a duplicate or invalid file hash")
        bound_hashes[name] = digest.lower()
    if not agent_paths.issubset(bound_hashes):
        raise RuntimeError("OEMDRV identity omits exact agent file bindings")
    if not media_paths.issubset(bound_hashes):
        raise RuntimeError("OEMDRV identity omits selected media bindings")
    if not user_paths.issubset(bound_hashes):
        raise RuntimeError("OEMDRV identity omits selected user-data bindings")
    for name in agent_paths:
        source = seed_mount / name
        if source.is_symlink() or not source.is_file() or _sha256_file(source) != bound_hashes[name]:
            raise RuntimeError(f"Bound OEMDRV agent file changed: {name}")
    for name in media_paths:
        source = seed_mount / name
        if source.is_symlink() or not source.is_file() or _sha256_file(source) != bound_hashes[name]:
            raise RuntimeError(f"Bound OEMDRV selected media changed: {name}")
    for name in user_paths:
        source = seed_mount / name
        if source.is_symlink() or not source.is_file() or _sha256_file(source) != bound_hashes[name]:
            raise RuntimeError(f"Bound OEMDRV selected user data changed: {name}")
    hashes = {name: digest for name, digest in bound_hashes.items()
              if name in _SEED_SECRET_FILES}
    if "migration-manifest.json" not in hashes or "preseed.cfg" not in hashes:
        raise RuntimeError("OEMDRV identity omits manifest or preseed")
    if (seed_mount / "igloo-boot/initrd").is_file() and "igloo-boot/initrd" not in hashes:
        raise RuntimeError("OEMDRV identity omits the injected installer initrd")
    retry_manifest = private_root / "manifest.retry.json"
    if not retry_manifest.exists():
        if _sha256_file(manifest_path) != hashes["migration-manifest.json"]:
            raise RuntimeError("Installed manifest differs from bound OEMDRV manifest")
        _atomic_copy(manifest_path, retry_manifest, private=True)
    if (stat.S_IMODE(retry_manifest.stat().st_mode) & 0o077 or
            _sha256_file(retry_manifest) != hashes["migration-manifest.json"]):
        raise RuntimeError("Protected original first-boot manifest differs from OEMDRV binding")
    password_hash = manifest.get("user", {}).get("linuxPasswordCrypted")
    if not isinstance(password_hash, str) or not password_hash.startswith("$6$"):
        raise RuntimeError("Original Linux password hash is unavailable for bound redaction")
    secret = password_hash.encode("utf-8")
    retry_dir = private_root / "seed-retry"
    journal = retry_dir / "journal.json"
    identity_digest = hashlib.sha256(protected_bytes).hexdigest()
    if journal.exists():
        state = json.loads(journal.read_text(encoding="utf-8"))
        if state.get("identitySha256") != identity_digest or set(state.get("redactedHashes", {})) != set(hashes):
            raise RuntimeError("OEMDRV redaction retry journal differs from current binding")
        redacted_hashes = state["redactedHashes"]
    else:
        retry_dir.mkdir(mode=0o700, parents=False, exist_ok=True)
        retry_dir.chmod(0o700)
        for name, digest in hashes.items():
            source = seed_mount / name
            if not source.is_file() or source.is_symlink() or _sha256_file(source) != digest:
                raise RuntimeError(f"Bound OEMDRV file changed before redaction: {name}")
            _atomic_copy(source, retry_dir / (name.replace("/", "__") + ".original"), private=True)
        redacted_hashes = {}
        for name in hashes:
            original = retry_dir / (name.replace("/", "__") + ".original")
            redacted = retry_dir / (name.replace("/", "__") + ".redacted")
            if name == "migration-manifest.json":
                _atomic_bytes(_redacted_manifest_bytes(original.read_bytes()), redacted, private=True)
            elif name == "preseed.cfg":
                _atomic_bytes(_redacted_preseed_bytes(original.read_bytes(), secret), redacted, private=True)
            else:
                _redacted_initrd_overlay(original, redacted, secret)
                redacted.chmod(0o600)
            redacted_hashes[name] = _sha256_file(redacted)
        _atomic_bytes(json.dumps({"identitySha256": identity_digest,
                                  "redactedHashes": redacted_hashes}, sort_keys=True).encode("utf-8"),
                      journal, private=True)
    for name, original_hash in hashes.items():
        original = retry_dir / (name.replace("/", "__") + ".original")
        redacted = retry_dir / (name.replace("/", "__") + ".redacted")
        if _sha256_file(original) != original_hash or _sha256_file(redacted) != redacted_hashes[name]:
            raise RuntimeError("OEMDRV redaction backup changed")
        current = seed_mount / name
        if not current.is_file() or _sha256_file(current) not in (original_hash, redacted_hashes[name]):
            raise RuntimeError(f"Bound OEMDRV file changed during redaction: {name}")
    run_cmd(["mount", "-o", "remount,rw", str(seed_mount)])
    try:
        _verify_seed_block_identity(seed_mount, disk_guid, partition_guid)
        for name, original_hash in hashes.items():
            current = seed_mount / name
            _discard_bound_seed_temporary(current, identity_digest, name)
            if _sha256_file(current) == original_hash:
                _replace_bound_seed_file(
                    retry_dir / (name.replace("/", "__") + ".redacted"),
                    current, redacted_hashes[name], identity_digest, name)
            if _sha256_file(current) != redacted_hashes[name]:
                raise RuntimeError(f"OEMDRV redaction readback failed: {name}")
    finally:
        # Leave any completed replacements in place after a write failure.
        # The protected journal accepts only the original and exact redacted
        # hashes on retry. Restoring originals here would copy secrets through
        # temporary files on the FAT seed, including across a power loss.
        run_cmd(["mount", "-o", "remount,ro", str(seed_mount)])
    logger.info("Redacted %d exact identity-bound OEMDRV file(s)", len(hashes))


def redact_manifest(manifest: dict[str, Any], manifest_path: Path = Path("/var/lib/igloo/manifest.json")) -> None:
    if not manifest_path.is_file():
        raise FileNotFoundError("First-boot manifest disappeared before redaction")
    retry = manifest_path.parent / "manifest.retry.json"
    if not retry.exists():
        _atomic_copy(manifest_path, retry, private=True)
    if stat.S_IMODE(retry.stat().st_mode) & 0o077:
        raise RuntimeError("Original first-boot manifest retry copy is not private")
    _atomic_bytes(_redacted_manifest_bytes(retry.read_bytes()), manifest_path, private=True)
    if _redacted_manifest_bytes(retry.read_bytes()) != manifest_path.read_bytes():
        raise RuntimeError("Installed manifest redaction readback failed")
    logger.info("Redacted installed first-boot manifest; retry copy retained until durable success")


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


def _ensure_public_autostart_dir() -> Path:
    """XDG's system autostart path must be traversable under umask 077."""
    xdg = Path("/etc/xdg")
    autostart = xdg / "autostart"
    for directory in (xdg, autostart):
        directory.mkdir(exist_ok=True)
        if not stat.S_ISDIR(directory.lstat().st_mode):
            raise RuntimeError(f"XDG autostart directory is redirected: {directory}")
        directory.chmod(0o755)
    return autostart


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

        autostart = _ensure_public_autostart_dir() / "igloo-display-layout.desktop"
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
        autostart.chmod(0o644)
        logger.info("Staged the login display-layout hook (Cinnamon xrandr / GNOME D-Bus)")
    except OSError:
        logger.exception("Could not stage the selected display-layout login hook")
        raise


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
    if not re.fullmatch(r"igloo-wallpaper\.[a-z0-9]{2,8}", fname, re.IGNORECASE):
        raise RuntimeError("Selected wallpaper name is unsafe")
    src = Path("/opt/igloo") / fname
    if not src.is_file():
        raise RuntimeError("Selected wallpaper is missing from installed staging")

    username = (manifest.get("user", {}).get("preferredLinuxUsername") or "").strip()
    home = Path("/home") / username if username else None
    if home is None or not home.is_dir():
        raise RuntimeError("Selected wallpaper target home is unavailable")

    pictures = home / "Pictures"
    pictures.mkdir(parents=True, exist_ok=True)
    dst = pictures / f"wallpaper{src.suffix}"
    dst.write_bytes(src.read_bytes())
    run_cmd(["chown", "-R", f"{username}:{username}", str(pictures)])
    logger.info("Wallpaper installed at %s", dst)

    uri = f"file://{dst}"
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
    _ensure_dconf_local_db(required=True)
    run_cmd(["dconf", "update"], timeout=60)
    logger.info("Seeded the desktop wallpaper via a dconf default (GNOME + Cinnamon)")


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
    if fname != "igloo-avatar.jpg":
        raise RuntimeError("Selected account picture name is unsafe")

    src = Path("/opt/igloo") / fname
    if not src.is_file():
        raise RuntimeError("Selected account picture is missing from installed staging")

    username = (manifest.get("user", {}).get("preferredLinuxUsername") or "").strip()
    home = Path("/home") / username if username else None
    if home is None or not home.is_dir():
        raise RuntimeError("Selected account picture target home is unavailable")

    data = src.read_bytes()

    icons = Path("/var/lib/AccountsService/icons")
    icons.mkdir(parents=True, exist_ok=True)
    icon_path = icons / username
    icon_path.write_bytes(data)
    icon_path.chmod(0o644)

    users_dir = Path("/var/lib/AccountsService/users")
    users_dir.mkdir(parents=True, exist_ok=True)
    _set_accountsservice_icon(users_dir / username, icon_path)
    logger.info("Account picture installed at %s", icon_path)

    face = home / ".face"
    face.write_bytes(data)
    face.chmod(0o644)
    run_cmd(["chown", f"{username}:{username}", str(face)])
    logger.info("Account picture also written to %s", face)


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
    # A fresh Debian GNOME installation does not necessarily include
    # notify-send. Publishing the login hook without its executable reports
    # first-boot success while the notification can never run.
    if shutil.which("notify-send") is None:
        apt_install(["libnotify-bin"], timeout=300)
    if shutil.which("notify-send") is None:
        raise RuntimeError("The selected welcome notification requires notify-send")
    autostart_dir = _ensure_public_autostart_dir()
    script = Path("/opt/igloo/igloo-welcome.sh")
    script.write_text(
        "#!/usr/bin/env bash\n"
        'DONE="$HOME/.igloo-welcome-done"\n'
        '[ -f "$DONE" ] && exit 0\n'
        'systemctl is-active --quiet igloo-bootstrap.service || exit 0\n'
        # GNOME can start XDG autostart applications before its notification
        # service accepts messages. Keep the hook alive briefly so a normal
        # session startup can deliver the notice without another login.
        'for attempt in 1 2 3 4 5 6 7 8 9 10; do\n'
        '    if notify-send --urgency=normal --expire-time=15000 '
        '"Welcome to Linux!" '
        '"Your files have been migrated from Windows. Logs are in /var/log/igloo/."; '
        'then\n'
        '        touch "$DONE"\n'
        '        exit $?\n'
        '    fi\n'
        '    [ "$attempt" = 10 ] || sleep 2\n'
        'done\n'
        'echo "iGloo welcome notification was not delivered" >&2\n'
        'exit 1\n',
        encoding="utf-8")
    script.chmod(0o755)
    desktop = autostart_dir / "igloo-welcome.desktop"
    desktop.write_text(
        "[Desktop Entry]\nName=iGloo Welcome\nComment=Migration complete notification\n"
        "Exec=/opt/igloo/igloo-welcome.sh\nIcon=distributor-logo\nTerminal=false\n"
        "Type=Application\nCategories=Utility;\nX-GNOME-Autostart-enabled=true\n"
        "OnlyShowIn=GNOME;KDE;X-Cinnamon;Cinnamon;XFCE;\n",
        encoding="utf-8")
    desktop.chmod(0o644)
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


def cleanup_installer_partitions(manifest: dict[str, Any]) -> None:
    """Release the read-only seed mount; defer destructive cleanup.

    A filesystem label and shared parent disk do not prove iGloo ownership of
    a partition or an EFI entry. The manifest carries no exact partition GUID
    or entry identity, so deleting either here would risk user data.
    """
    seed_mount = Path("/run/igloo-seed")
    if seed_mount.is_mount():
        run_cmd(["umount", str(seed_mount)])
    logger.info("Installer partition and EFI cleanup deferred: no exact ownership binding")


def _validate_seed_retry_state(private_root: Path) -> bool:
    """Identify a protected scrub retry before any migration step can replay.

    The redaction step performs the final GPT and OEMDRV readback. This check
    binds the local retry inputs and journal to the copied seed identity so a
    malformed or foreign journal cannot select a full migration replay.
    """
    retry_manifest = private_root / "manifest.retry.json"
    retry_dir = private_root / "seed-retry"
    journal = retry_dir / "journal.json"
    if not any(path.exists() or path.is_symlink()
               for path in (retry_manifest, retry_dir, journal)):
        return False

    identity_path = private_root / "seed-identity.json"
    for path in (identity_path, retry_manifest):
        if path.is_symlink() or not path.is_file() or stat.S_IMODE(path.stat().st_mode) & 0o077:
            raise RuntimeError(f"Protected seed retry input is missing or unsafe: {path.name}")
    if retry_dir.exists() or retry_dir.is_symlink():
        if retry_dir.is_symlink() or not retry_dir.is_dir() or \
                stat.S_IMODE(retry_dir.stat().st_mode) & 0o077:
            raise RuntimeError("Protected seed retry directory is unsafe")

    identity_bytes = identity_path.read_bytes()
    identity = json.loads(identity_bytes)
    if not isinstance(identity, dict) or identity.get("version") != 1:
        raise RuntimeError("Protected seed retry identity is invalid")
    records = identity.get("files")
    if not isinstance(records, list):
        raise RuntimeError("Protected seed retry identity has no bound files")
    original_hashes: dict[str, str] = {}
    for record in records:
        if not isinstance(record, dict):
            raise RuntimeError("Protected seed retry identity has an invalid file record")
        name, digest = record.get("path"), record.get("sha256")
        if name in _SEED_SECRET_FILES:
            if name in original_hashes or not isinstance(digest, str) or \
                    not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
                raise RuntimeError("Protected seed retry identity has an invalid secret binding")
            original_hashes[name] = digest.lower()
    if not {"migration-manifest.json", "preseed.cfg"}.issubset(original_hashes):
        raise RuntimeError("Protected seed retry identity omits required secret bindings")
    if _sha256_file(retry_manifest) != original_hashes["migration-manifest.json"]:
        raise RuntimeError("Protected seed retry manifest differs from its identity binding")

    if journal.exists() or journal.is_symlink():
        if journal.is_symlink() or not journal.is_file() or \
                stat.S_IMODE(journal.stat().st_mode) & 0o077:
            raise RuntimeError("Protected seed redaction journal is unsafe")
        state = json.loads(journal.read_text(encoding="utf-8"))
        expected_identity_hash = hashlib.sha256(identity_bytes).hexdigest()
        redacted_hashes = state.get("redactedHashes") if isinstance(state, dict) else None
        if not isinstance(state, dict) or state.get("identitySha256") != expected_identity_hash or \
                not isinstance(redacted_hashes, dict) or set(redacted_hashes) != set(original_hashes) or \
                any(not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest)
                    for digest in redacted_hashes.values()):
            raise RuntimeError("Protected seed redaction journal differs from its identity binding")
    logger.info("Protected seed scrub retry detected; migration steps will not replay")
    return True


def main() -> int:
    p = argparse.ArgumentParser(description="Igloo first-boot agent (Debian family)")
    # Not required=True: --import-credentials is a separate, unprivileged mode
    # that reads neither of them.
    p.add_argument("--manifest", type=Path)
    p.add_argument("--log-dir", type=Path)
    p.add_argument("--only", default=None,
                    help="Run the post-driver display-layout second pass.")
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

    if args.import_credentials or args.fix_boot_order:
        # These standalone modes intentionally take no manifest. Still refuse
        # to import user secrets or change UEFI state when this shared
        # Debian-family payload is executed on an unknown installed OS.
        standalone_os_id = os_release().get("ID", "").lower()
        if standalone_os_id not in ("debian", "linuxmint"):
            logger.error("Standalone first-boot mode is unavailable on installed OS %r",
                         standalone_os_id)
            return 2
    if args.import_credentials:
        return _run_user_mode()
    if args.fix_boot_order:
        return _run_boot_order_mode()
    if args.manifest is None or args.log_dir is None:
        p.error("--manifest and --log-dir are required for the first-boot pass")

    configure_logging(args.log_dir)

    logger.info("=== Igloo first-boot agent starting (distro: %s) ===", distro_id())
    try:
        retry_manifest = args.manifest.parent / "manifest.retry.json"
        source_manifest = retry_manifest if retry_manifest.is_file() else args.manifest
        if source_manifest == retry_manifest and stat.S_IMODE(retry_manifest.stat().st_mode) & 0o077:
            raise RuntimeError("Original first-boot manifest retry copy is not private")
        manifest: dict[str, Any] = json.loads(source_manifest.read_text(encoding="utf-8"))
    except Exception:
        logger.exception("Failed to load manifest  cannot continue")
        return 1

    if manifest.get("schemaVersion") != 1:
        logger.error("Unsupported manifest schemaVersion: %r", manifest.get("schemaVersion"))
        return 2

    installed_os_id = os_release().get("ID", "").lower()
    expected_manifest_id = {"debian": "debian", "linuxmint": "linuxmint-cinnamon"}.get(installed_os_id)
    if expected_manifest_id is None or manifest.get("distroId") != expected_manifest_id:
        logger.error("First-boot distribution identity mismatch: installed OS %r, manifest %r",
                     installed_os_id, manifest.get("distroId"))
        return 2

    try:
        resume_seed_scrub = args.only is None and _validate_seed_retry_state(args.manifest.parent)
    except Exception:
        logger.exception("Protected seed scrub retry is invalid; migration cannot replay")
        return 1


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
        ("redact-seed",      lambda: redact_seed(manifest, args.manifest)),
        ("redact-manifest",  lambda: redact_manifest(manifest, args.manifest)),
        ("cleanup-seed",     lambda: cleanup_installer_partitions(manifest)),
    ]

    if args.only is not None:
        # The only supported partial invocation is the post-driver display
        # pass. An arbitrary selection could otherwise run cleanup-seed without
        # completing migration, or report success without running any step.
        if args.only != "display-layout":
            p.error("--only supports display-layout only")
        global _SECOND_PASS
        _SECOND_PASS = True
        steps = [(n, s) for n, s in steps if n == "display-layout"]
        logger.info("Running only step: display-layout")
    elif resume_seed_scrub:
        steps = [(n, s) for n, s in steps
                 if n in ("redact-seed", "redact-manifest", "cleanup-seed")]
        logger.info("Resuming only identity-bound seed and installed-manifest redaction")

    failures: list[str] = []
    for name, step in steps:
        if failures and name in ("redact-seed", "redact-manifest", "cleanup-seed"):
            # Retain the source and the original manifest for diagnosis and an
            # explicit retry. A failed pass cannot consume its recovery inputs.
            logger.warning("Skipping %s after failed migration step(s)", name)
            continue
        logger.info("--- step: %s ---", name)
        try:
            step()
            logger.info("step %s: OK", name)
        except Exception:
            logger.exception("step %s FAILED", name)
            failures.append(name)

    logger.info("=== Igloo first-boot agent done (%d failure(s)) ===", len(failures))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
