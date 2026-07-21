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


def install_gpu_drivers(manifest: dict[str, Any]) -> None:
    """Install the proprietary NVIDIA driver if the GPU is NVIDIA."""
    gpu = manifest.get("hardware", {}).get("gpuVendor", "").lower()
    if gpu != "nvidia":
        logger.info("GPU driver: vendor=%r, skipping NVIDIA step", gpu)
        return

    if is_ubuntu_like():
        # Ubuntu/Mint ship the ubuntu-drivers tool which picks the right driver.
        logger.info("Installing NVIDIA driver via ubuntu-drivers")
        apt(["install", "ubuntu-drivers-common"], timeout=300, check=False)
        run_cmd(["ubuntu-drivers", "autoinstall"], timeout=1200, check=False, env=APT_ENV)
    else:
        # Debian: the driver lives in the non-free component (enabled by the preseed).
        logger.info("Installing NVIDIA driver from Debian non-free")
        apt(["install", "nvidia-driver", "firmware-misc-nonfree"], timeout=1200, check=False)

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
        # Mint ships its own codec metapackage — exactly what its first-run
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


# ── User-file migration from the Windows NTFS partition ──────────────────────

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
        logger.info("Missing user names in manifest — skipping file migration")
        return

    user_home = Path("/home") / linux_user
    if not user_home.is_dir():
        logger.warning("User home %s does not exist — skipping file migration", user_home)
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


# ── Wi-Fi (NetworkManager keyfiles) — distro-agnostic, reused from Fedora ────

def set_user_password(manifest: dict[str, Any]) -> None:
    """Guarantee the user's password is set, via chpasswd, from the manifest.

    The preseed/autoinstall already sets it, but Debian-family *plaintext* password
    preseeding (passwd/user-password) is unreliable across releases — the account is
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
    file is /etc/default/keyboard — the greeter and every new X/Wayland session
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

    Once this agent has run, the staging partition(s) — OEMDRV/CIDATA with the
    installer config + agent payload (and, for iso-scan/casper distros, the
    multi-gigabyte ISO), plus the dedicated IGLOOISO partition when present —
    serve no further purpose. Leaving them wastes gigabytes and confuses users
    ("what is this OEMDRV drive?"), so the FINAL agent step deletes them, along
    with Igloo's now-dangling one-shot UEFI boot entry.

    Safety rules (BR-01/BR-03, docs/business/business-rules.md):
      * delete ONLY by exact filesystem-label match on Igloo's staging labels —
        never by partition number, position, or size;
      * the partition must sit on the same physical disk as the Linux root;
      * every action is best-effort and logged — any doubt leaves the partition
        in place, never a broken disk.
    The freed space is intentionally left unallocated: it borders the Windows
    partition, so Windows' own Disk Management can extend C: into it.
    """
    src = run_cmd(["findmnt", "-rno", "SOURCE", "/"], check=False)
    root_dev = (src.stdout or "").strip()
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
        logger.exception("Failed to load manifest — cannot continue")
        return 1

    if manifest.get("schemaVersion") != 1:
        logger.error("Unsupported manifest schemaVersion: %r", manifest.get("schemaVersion"))
        return 2

    # Order: refresh apt → drivers/codecs/firmware → flatpak → files → wifi → redact.
    steps: list[tuple[str, Any]] = [
        ("set-password",    lambda: set_user_password(manifest)),
        ("set-keyboard",    lambda: set_keyboard(manifest)),
        ("apt-update",      lambda: apt_update(manifest)),
        ("gpu-drivers",     lambda: install_gpu_drivers(manifest)),
        ("codecs",          lambda: install_codecs(manifest)),
        ("firmware",        lambda: ensure_firmware(manifest)),
        ("os-prober",       lambda: enable_os_prober(manifest)),
        ("flathub",         lambda: setup_flathub(manifest)),
        ("suggested-pkgs",  lambda: install_suggested_packages(manifest)),
        ("user-files",      lambda: migrate_user_files(manifest)),
        ("wifi",            lambda: migrate_wifi(manifest)),
        ("welcome-app",     lambda: install_welcome_app(manifest)),
        ("redact-manifest", lambda: redact_manifest(manifest)),
        ("cleanup-seed",    lambda: cleanup_installer_partitions(manifest)),
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
