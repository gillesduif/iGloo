#!/usr/bin/env python3
"""
Igloo first-boot migration agent for Fedora KDE.

Reads the migration manifest written by the Windows-side app (copied to
/var/lib/igloo/manifest.json during Anaconda's %post phase) and applies
the remaining post-install configuration that cannot run inside the
installer chroot:

  - Enable RPM Fusion (free + nonfree)
  - Install multimedia codecs (via dnf groupupdate multimedia)
  - Install NVIDIA proprietary drivers (if the GPU is NVIDIA)
  - Install suggested packages (flatpak / dnf)
  - Show a welcome screen on first login

NOTE: User files (Documents, Downloads, …) are NOT handled here.
      They are copied from the OEMDRV volume to the user's home directory
      during Anaconda's %post --nochroot phase (ks.cfg.template), which
      runs with simultaneous access to both the OEMDRV and the new rootfs.
      By the time this agent runs, the files are already in place.

      The same applies to Gecko browser profiles (Firefox / Zen / Waterfox):
      their OS-portable profile roots are copied by the same %post phase, so
      saved passwords and settings are present before first login. Chromium
      browsers are not migrated (passwords are DPAPI-bound to Windows).
"""
from __future__ import annotations

import argparse
import json
import logging
import os
import re
import subprocess
import sys
import uuid
from pathlib import Path
from typing import Any

logger = logging.getLogger("igloo.agent")

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def run_cmd(
    cmd: list[str],
    *,
    check: bool = True,
    timeout: int = 600,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess:
    """Run a command, stream stderr to logger, and optionally raise on failure."""
    merged_env = {**os.environ, **(env or {})}
    logger.info("Running: %s", " ".join(cmd))
    result = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        timeout=timeout,
        env=merged_env,
    )
    if result.stdout.strip():
        for line in result.stdout.strip().splitlines():
            logger.debug("  stdout: %s", line)
    if result.stderr.strip():
        for line in result.stderr.strip().splitlines():
            logger.debug("  stderr: %s", line)
    if check and result.returncode != 0:
        raise RuntimeError(
            f"Command failed (exit {result.returncode}): {' '.join(cmd)}\n"
            f"stderr: {result.stderr.strip()}"
        )
    return result


def fedora_version() -> str:
    """Return the Fedora major version number as a string, e.g. '41'."""
    try:
        text = Path("/etc/fedora-release").read_text()
        # "Fedora release 41 (Forty One)"
        parts = text.split()
        idx = parts.index("release")
        return parts[idx + 1]
    except Exception:
        # Fall back to os-release
        try:
            import configparser
            p = configparser.ConfigParser()
            p.read_string("[root]\n" + Path("/etc/os-release").read_text())
            return p["root"].get("VERSION_ID", "41").strip('"')
        except Exception:
            return "41"


def _is_dnf5() -> bool:
    """Return True when the system uses DNF 5 (Fedora 41+).

    Fedora 41 switched from DNF 4 to DNF 5. The ``groupupdate`` subcommand
    was removed in DNF 5; group installs use ``dnf install @<group>`` instead.
    """
    try:
        r = subprocess.run(
            ["dnf", "--version"], capture_output=True, text=True, timeout=10
        )
        first_line = (r.stdout.strip().splitlines() or [""])[0]
        return first_line.startswith("5.")
    except Exception:
        return False


# ---------------------------------------------------------------------------
# Migration steps
# ---------------------------------------------------------------------------

def enable_rpmfusion(manifest: dict[str, Any]) -> None:
    """Install RPM Fusion free and nonfree release packages."""
    ver = fedora_version()
    free_url = f"https://mirrors.rpmfusion.org/free/fedora/rpmfusion-free-release-{ver}.noarch.rpm"
    nonfree_url = f"https://mirrors.rpmfusion.org/nonfree/fedora/rpmfusion-nonfree-release-{ver}.noarch.rpm"

    logger.info("Enabling RPM Fusion for Fedora %s", ver)
    run_cmd(
        ["dnf", "-y", "install", free_url, nonfree_url],
        timeout=300,
    )
    logger.info("RPM Fusion enabled")


def install_codecs(manifest: dict[str, Any]) -> None:
    """Install multimedia codecs via RPM Fusion.

    Fedora 41+ ships DNF 5, which dropped the ``groupupdate`` subcommand.
    On DNF 5 we use ``dnf install @<group>``; on DNF 4 we use ``groupupdate``
    with ``--setop=allow_vendor_change`` to let RPM Fusion replace Fedora's
    stock codec packages.
    """
    if not manifest.get("hardware", {}).get("needsNonFreeCodecs", True):
        logger.info("Codecs: needsNonFreeCodecs=false, skipping")
        return

    logger.info("Installing multimedia codecs")
    if _is_dnf5():
        # DNF 5: group installs via @group syntax; vendor change is automatic.
        run_cmd(
            [
                "dnf", "-y", "install", "@multimedia",
                "--exclude=PackageKit-gstreamer-plugin",
            ],
            timeout=600,
        )
        run_cmd(["dnf", "-y", "install", "@sound-and-video"], timeout=300)
    else:
        # DNF 4 (Fedora ≤ 40)
        run_cmd(
            [
                "dnf", "-y", "groupupdate", "multimedia",
                "--setop=allow_vendor_change",
                "--exclude=PackageKit-gstreamer-plugin",
            ],
            timeout=600,
        )
        run_cmd(["dnf", "-y", "groupupdate", "sound-and-video"], timeout=300)
    logger.info("Multimedia codecs installed")


def installed_kernel_versions() -> list[str]:
    """Return every installed kernel version, e.g. ['6.19.10-300.fc44.x86_64', ...].

    An install often ends up with TWO kernels: the one on the install media and a
    newer one pulled from updates during the same run. Anything that builds or
    repairs per-kernel content has to cover all of them, because GRUB boots the
    NEWEST one - not the one that happened to be running.
    """
    res = run_cmd(["rpm", "-q", "kernel-core"], check=False)
    return [
        line.strip()[len("kernel-core-"):]
        for line in (res.stdout or "").splitlines()
        if line.strip().startswith("kernel-core-")
    ]


def nvidia_pci_ids() -> list[str]:
    """Return the PCI device IDs of the NVIDIA display adapters, e.g. ['2c05']."""
    res = run_cmd(["lspci", "-n", "-d", "10de:"], check=False)
    ids = []
    for line in (res.stdout or "").splitlines():
        # "01:00.0 0300: 10de:2c05 (rev a1)" - class 0300/0302 is display.
        m = re.search(r"\b10de:([0-9a-fA-F]{4})\b", line)
        if m and re.search(r"\b03[0-9a-fA-F]{2}:", line):
            ids.append(m.group(1).lower())
    return ids


def gpu_requires_open_kernel_module() -> bool:
    """True when this GPU needs NVIDIA's OPEN kernel module.

    Decided from NVIDIA's own machine-readable data - the "kernelopen" feature flag
    in supported-gpus.json, the same source nvidia-driver-assistant uses - rather
    than a hardcoded model list.

    This matters because it is not a preference: on Blackwell (RTX 50 series) the
    PROPRIETARY kernel module does not support the GPU at all, so a default
    akmod-nvidia build leaves the card with no driver. The reverse is equally true
    - Pascal/Maxwell (GTX 10xx and older) are not open-capable and must keep the
    proprietary module - which is why this is decided per machine and never applied
    blanket. On any doubt we return False and keep the stock behaviour.
    """
    ids = nvidia_pci_ids()
    if not ids:
        logger.info("No NVIDIA display device found via lspci - using the default module")
        return False

    for path in ("/usr/share/nvidia/supported-gpus.json",
                 "/usr/share/nvidia/supported-gpus/supported-gpus.json"):
        p = Path(path)
        if not p.is_file():
            continue
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            logger.exception("Could not parse %s - using the default module", path)
            return False

        for chip in data.get("chips", []):
            devid = str(chip.get("devid", "")).lower().removeprefix("0x")
            if devid in ids:
                features = [str(f).lower() for f in chip.get("features", [])]
                needs_open = "kernelopen" in features
                logger.info("GPU %s (%s): kernelopen=%s",
                            devid, chip.get("name", "unknown"), needs_open)
                return needs_open
        logger.info("GPU %s not listed in %s - using the default module",
                    ", ".join(ids), path)
        return False

    logger.info("supported-gpus.json not found - using the default module")
    return False


def install_gpu_drivers(manifest: dict[str, Any]) -> None:
    """Install NVIDIA drivers if the GPU is NVIDIA."""
    gpu = manifest.get("hardware", {}).get("gpuVendor", "").lower()
    if gpu != "nvidia":
        logger.info("GPU driver: vendor=%r, skipping NVIDIA step", gpu)
        return

    logger.info("Installing NVIDIA drivers from RPM Fusion")
    run_cmd(
        ["dnf", "-y", "install", "akmod-nvidia", "xorg-x11-drv-nvidia-cuda"],
        timeout=600,
    )

    # Select the open kernel module when this GPU requires it. The check runs AFTER
    # the driver package is installed because supported-gpus.json ships with it.
    # The macro is what makes akmods build the open variant; the rebuild below then
    # replaces whatever the package install already built.
    if gpu_requires_open_kernel_module():
        try:
            Path("/etc/rpm/macros.nvidia-kmod").write_text(
                "%_with_kmod_nvidia_open 1\n", encoding="utf-8")
            logger.info("Selected the NVIDIA OPEN kernel module "
                        "(required by this GPU; the proprietary module does not support it)")
        except OSError:
            logger.exception("Could not write /etc/rpm/macros.nvidia-kmod - "
                             "the open module cannot be selected, the GPU may get no driver")

    # Build the module for EVERY installed kernel, not just the running one.
    # A bare `akmods --force` builds only for the running kernel. When the install
    # also pulled a newer kernel from updates, the post-install reboot lands on
    # that NEWER kernel (GRUB defaults to the newest) - which would then have no
    # nvidia module at all: no GPU driver, software rendering, and a desktop that
    # comes up black because the shell cannot paint. Building per-kernel is the fix.
    kvers = installed_kernel_versions()
    if kvers:
        # akmods compiles against kernel headers: without kernel-devel-<kver> the
        # build for that kernel fails (silently, from the caller's point of view)
        # and the kernel boots with no GPU driver. The running kernel usually has
        # its devel package already; a kernel pulled in during the install often
        # does not - so ensure one per kernel before building.
        for kver in kvers:
            if run_cmd(["rpm", "-q", f"kernel-devel-{kver}"], check=False).returncode != 0:
                logger.info("Installing kernel-devel-%s (needed to build the NVIDIA module)", kver)
                run_cmd(["dnf", "-y", "install", f"kernel-devel-{kver}"], timeout=600, check=False)

        logger.info("Building NVIDIA kernel module for %d kernel(s): %s - may take several minutes",
                    len(kvers), ", ".join(kvers))
        for kver in kvers:
            # --rebuild: the package install may already have built a module for this
            # kernel WITHOUT the open macro above; rebuilding is what replaces it.
            run_cmd(["akmods", "--kernels", kver, "--rebuild"], timeout=1800, check=False)
    else:
        logger.warning("Could not list installed kernels - falling back to the running kernel only")
        run_cmd(["akmods", "--force", "--rebuild"], timeout=1800, check=False)

    # Verify the module actually exists for each kernel; a silent build failure
    # here is precisely what produces a black desktop on the next boot.
    for kver in kvers:
        if Path(f"/lib/modules/{kver}/extra/nvidia").is_dir() or \
           list(Path(f"/lib/modules/{kver}").glob("extra/nvidia*")):
            logger.info("nvidia module present for kernel %s", kver)
        else:
            logger.error("nvidia module MISSING for kernel %s - that kernel will boot without "
                         "the GPU driver", kver)

    # Installing the NVIDIA driver blacklists nouveau and builds a new kernel
    # module that only loads on the next boot.  Starting the Plasma session now
    # would land on a half-initialised GPU (black screen / broken desktop).
    # Signal first-boot.sh to reboot once before the display manager starts so
    # the user's first real session comes up cleanly on the nvidia driver.
    try:
        Path("/var/lib/igloo/.reboot-required").write_text(
            "nvidia-driver-installed\n", encoding="utf-8"
        )
        logger.info("Flagged reboot-required (NVIDIA driver needs a clean boot)")
    except Exception:
        logger.exception("Could not write reboot-required marker (non-fatal)")

    logger.info("NVIDIA drivers installed")


def ensure_kernel_modules(manifest: dict[str, Any]) -> None:
    """Self-heal an incomplete kernel install.

    A netinstall - or a kernel update pulled mid-install - over a flaky Wi-Fi
    link can leave an installed kernel with ``kernel-core`` and
    ``kernel-modules-core`` but WITHOUT ``kernel-modules``: the package that
    ships most drivers (Wi-Fi such as rtw89, USB tethering, many NICs). The
    symptom is a freshly-installed system with no usable network device (only
    loopback). While the agent still has connectivity, make sure every installed
    kernel has its matching ``kernel-modules`` so the next boot has full hardware
    support.
    """
    versions = installed_kernel_versions()
    if not versions:
        logger.warning("Could not list installed kernels - skipping kernel-modules check")
        return

    missing = [
        kver for kver in versions
        if run_cmd(["rpm", "-q", f"kernel-modules-{kver}"], check=False).returncode != 0
    ]
    if not missing:
        logger.info("All %d installed kernel(s) already have kernel-modules", len(versions))
        return

    for kver in missing:
        logger.warning(
            "kernel-modules-%s is missing (incomplete install) - installing now", kver
        )
        run_cmd(["dnf", "-y", "install", f"kernel-modules-{kver}"], timeout=600)
    logger.info("kernel-modules repaired for: %s", ", ".join(missing))


def setup_flathub(manifest: dict[str, Any]) -> None:
    """Ensure the Flathub remote is registered.

    Fedora ships Flatpak but does not add the Flathub remote by default.
    Without it, any ``flatpak install flathub …`` call in the next step
    would fail with "Remote 'flathub' not found."
    """
    logger.info("Registering Flathub remote (if not already present)")
    run_cmd(
        [
            "flatpak", "remote-add", "--if-not-exists", "--system",
            "flathub",
            "https://dl.flathub.org/repo/flathub.flatpakrepo",
        ],
        timeout=120,
    )
    logger.info("Flathub remote ready")


def install_suggested_packages(manifest: dict[str, Any]) -> None:
    """Install any auto-install packages listed in the manifest."""
    pkgs = [
        p for p in manifest.get("suggestedPackages", [])
        if p.get("autoInstall")
    ]
    if not pkgs:
        logger.info("No auto-install packages in manifest")
        return

    flatpak_ids = [p["flatpakId"] for p in pkgs if p.get("flatpakId")]
    dnf_pkgs = [p["nativePackage"] for p in pkgs if p.get("nativePackage")]

    if flatpak_ids:
        labels = [p.get("linuxAppName") or p["flatpakId"] for p in pkgs if p.get("flatpakId")]
        logger.info("Installing Flatpak: %s", ", ".join(labels))
        run_cmd(
            ["flatpak", "install", "-y", "--noninteractive", "flathub"] + flatpak_ids,
            timeout=600,
        )

    if dnf_pkgs:
        labels = [p.get("linuxAppName") or p["nativePackage"] for p in pkgs if p.get("nativePackage")]
        logger.info("Installing dnf packages: %s", ", ".join(labels))
        run_cmd(["dnf", "-y", "install"] + dnf_pkgs, timeout=300)

    logger.info("Suggested packages installed")


def _nm_keyfile(ssid: str, security: str, psk: str | None, hidden: bool) -> str:
    """Render a NetworkManager keyfile (.nmconnection) for one Wi-Fi network."""
    lines = [
        "[connection]",
        f"id={ssid}",
        f"uuid={uuid.uuid4()}",
        "type=wifi",
        "autoconnect=true",
        "",
        "[wifi]",
        "mode=infrastructure",
        f"ssid={ssid}",
    ]
    if hidden:
        lines.append("hidden=true")

    if security == "wpa-psk" and psk:
        lines += [
            "",
            "[wifi-security]",
            "key-mgmt=wpa-psk",
            f"psk={psk}",
        ]

    lines += [
        "",
        "[ipv4]",
        "method=auto",
        "",
        "[ipv6]",
        "method=auto",
        "",
    ]
    return "\n".join(lines)


def _safe_filename(ssid: str) -> str:
    """Turn an SSID into a filesystem-safe .nmconnection basename."""
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", ssid).strip("_")
    return (safe or "wifi") + ".nmconnection"


def migrate_wifi(manifest: dict[str, Any]) -> None:
    """Write a NetworkManager profile for each saved Wi-Fi network.

    Networks come from the Windows-side ``netsh wlan export profile key=clear``
    scan. WPA/WPA2/WPA3-personal networks include their pre-shared key; open
    networks are written without a security section; enterprise (802.1X)
    networks are skipped (their credentials cannot be carried as a simple PSK).
    """
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
            logger.info("Skipping %r: WPA-PSK network but no key was recovered", ssid)
            continue

        content = _nm_keyfile(ssid, security, psk, bool(net.get("hidden", False)))
        path = sysconn / _safe_filename(ssid)
        try:
            path.write_text(content, encoding="utf-8")
            path.chmod(0o600)   # NetworkManager refuses to load world-readable keyfiles
            written += 1
            logger.info("Wrote NetworkManager profile for %r (%s)", ssid, security)
        except Exception:
            logger.exception("Failed to write Wi-Fi profile for %r (non-fatal)", ssid)

    if written:
        # Pick up the new keyfiles without a reboot.
        run_cmd(["nmcli", "connection", "reload"], check=False, timeout=60)
    logger.info("Wi-Fi migration complete: %d profile(s) written", written)


def redact_manifest(manifest: dict[str, Any]) -> None:
    """Remove sensitive fields from the on-disk manifest.

    ``linuxPassword`` and Wi-Fi ``psk`` values are stored in plaintext so the
    kickstart can connect/authenticate during installation and the agent can
    write NetworkManager profiles.  Once applied they serve no further purpose,
    but ``/var/lib/igloo/manifest.json`` is world-readable by default.
    Overwrite those fields with ``null`` so the plaintext is no longer present.
    """
    manifest_path = Path("/var/lib/igloo/manifest.json")
    if not manifest_path.exists():
        return

    try:
        with manifest_path.open(encoding="utf-8") as f:
            data: dict[str, Any] = json.load(f)

        changed = False

        user = data.get("user", {})
        if user.get("linuxPassword") is not None:
            user["linuxPassword"] = None
            changed = True

        for net in data.get("wifiNetworks", []):
            if net.get("psk") is not None:
                net["psk"] = None
                changed = True

        if changed:
            with manifest_path.open("w", encoding="utf-8") as f:
                json.dump(data, f, indent=2)
            manifest_path.chmod(0o640)   # root:root rw-r-----
            logger.info("Redacted plaintext secrets (linuxPassword, Wi-Fi PSKs) from manifest")
        else:
            logger.info("No plaintext secrets present in manifest")
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
    """
    Drop an XDG autostart entry that launches a simple welcome notification
    on the user's first login after migration.

    The notification uses notify-send (pre-installed with KDE) to pop up a
    Plasma notification - no custom UI required.
    """
    username = manifest.get("user", {}).get("preferredLinuxUsername", "")
    autostart_dir = Path("/etc/xdg/autostart")
    autostart_dir.mkdir(parents=True, exist_ok=True)

    script_path = Path("/opt/igloo/igloo-welcome.sh")
    script_path.write_text(
        "#!/usr/bin/env bash\n"
        "# iGloo welcome notification - runs once on first login\n"
        f'DONE_MARKER="$HOME/.igloo-welcome-done"\n'
        '[ -f "$DONE_MARKER" ] && exit 0\n'
        'notify-send \\\n'
        '  --icon=distributor-logo \\\n'
        '  --urgency=normal \\\n'
        '  --expire-time=15000 \\\n'
        '  "Welcome to Linux!" \\\n'
        f'  "Your files have been migrated from Windows. Log files are in /var/log/igloo/."\n'
        'touch "$DONE_MARKER"\n',
        encoding="utf-8",
    )
    script_path.chmod(0o755)

    desktop_entry = autostart_dir / "igloo-welcome.desktop"
    desktop_entry.write_text(
        "[Desktop Entry]\n"
        "Name=iGloo Welcome\n"
        "Comment=Migration complete notification\n"
        f"Exec=/opt/igloo/igloo-welcome.sh\n"
        "Icon=distributor-logo\n"
        "Terminal=false\n"
        "Type=Application\n"
        "Categories=Utility;\n"
        "X-GNOME-Autostart-enabled=true\n"
        "OnlyShowIn=KDE;GNOME;\n",
        encoding="utf-8",
    )
    logger.info("Welcome autostart entry written to %s", desktop_entry)


# ---------------------------------------------------------------------------
# Argument parsing / logging setup
# ---------------------------------------------------------------------------

def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Igloo first-boot agent (Fedora KDE)")
    p.add_argument("--manifest", required=True, type=Path,
                   help="Path to migration-manifest.json")
    p.add_argument("--log-dir", required=True, type=Path,
                   help="Directory for log output")
    return p.parse_args()


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


# ---------------------------------------------------------------------------
IGLOO_SEED_LABELS = ("OEMDRV", "CIDATA", "IGLOOISO")


def cleanup_installer_partitions(manifest: dict[str, Any]) -> None:
    """Remove Igloo's temporary installer artifacts from the machine.

    Once this agent has run, the staging partition(s)  OEMDRV with the
    kickstart, agent payload and Anaconda stage2  serve no further purpose.
    Leaving them wastes space and confuses users ("what is this OEMDRV
    drive?"), so the FINAL agent step deletes them, along with Igloo's
    now-dangling one-shot UEFI boot entry.

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
    # "/dev/nvme0n1p3[/root]" on Fedora's btrfs root, which is not a valid device
    # path  lsblk then fails, `disk` stays empty, and the whole cleanup silently
    # bails, leaving OEMDRV behind to break later installs. (ext4 roots on
    # Debian/Mint have no suffix, which is why this only ever bit Fedora.)
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


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main() -> int:
    args = parse_args()
    configure_logging(args.log_dir)

    logger.info("=== Igloo first-boot agent starting ===")
    logger.info("Manifest : %s", args.manifest)
    logger.info("Log dir  : %s", args.log_dir)

    # Load manifest
    try:
        with args.manifest.open(encoding="utf-8") as f:
            manifest: dict[str, Any] = json.load(f)
    except Exception:
        logger.exception("Failed to load manifest - cannot continue")
        return 1

    schema = manifest.get("schemaVersion")
    if schema != 1:
        logger.error("Unsupported manifest schemaVersion: %r (expected 1)", schema)
        return 2

    distro = manifest.get("distroId")
    if distro != "fedora-kde":
        logger.error("Manifest is for distro %r, not fedora-kde", distro)
        return 3

    # Steps - each is best-effort; a failure is logged and the rest continue.
    # Order matters: RPM Fusion must be enabled before codecs/GPU drivers;
    # Flathub must be registered before suggested-pkgs; redact runs last.
    # kernel-modules runs BEFORE gpu-drivers: it can install a second, newer
    # kernel's packages, and the nvidia module has to be built against every
    # kernel that exists - otherwise the reboot lands on a newer kernel with no
    # GPU driver (black desktop). Completing the kernels first keeps that honest.
    steps: list[tuple[str, Any]] = [
        ("rpmfusion",       lambda: enable_rpmfusion(manifest)),
        ("codecs",          lambda: install_codecs(manifest)),
        ("kernel-modules",  lambda: ensure_kernel_modules(manifest)),
        ("gpu-drivers",     lambda: install_gpu_drivers(manifest)),
        ("flathub",         lambda: setup_flathub(manifest)),
        ("suggested-pkgs",  lambda: install_suggested_packages(manifest)),
        ("wifi",            lambda: migrate_wifi(manifest)),
        ("display-layout",  lambda: migrate_display_layout(manifest)),
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

    if failures:
        logger.warning(
            "First-boot agent finished with %d failed step(s): %s",
            len(failures), ", ".join(failures),
        )
    else:
        logger.info("All steps completed successfully")

    logger.info("=== Igloo first-boot agent done ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
