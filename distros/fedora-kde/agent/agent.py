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


def _secure_boot_enabled() -> bool:
    """True when UEFI Secure Boot is enforcing kernel-module signatures.

    With Secure Boot on, the unsigned akmod-built nvidia module is rejected at
    load time. That state changes what is safe to put on the kernel cmdline:
    blacklisting the in-tree drivers would leave the machine with NO display
    driver at all, so the caller must keep the nouveau/nova_core fallback.
    """
    res = run_cmd(["mokutil", "--sb-state"], check=False, timeout=30)
    out = ((res.stdout or "") + "\n" + (res.stderr or "")).lower()
    return "secureboot enabled" in out or "secure boot enabled" in out


def ensure_nvidia_kernel_cmdline(module_ok_for_all: bool, kvers: list[str]) -> None:
    """Blacklist nouveau/nova_core on the kernel cmdline - only once the nvidia
    module is confirmed built for every installed kernel.

    Why the cmdline entry is REQUIRED (kernel panic on RTX 5070 bare metal,
    July 2026): RPM Fusion's /etc/modprobe.d/ blacklist does NOT reach the
    initramfs. Dracut builds the initramfs host-only, so the display driver
    that was loaded at build time (nouveau, or nova_core on Blackwell-capable
    kernels) is baked in and loads at every boot regardless of modprobe.d.
    Once the nvidia module is installed, both drivers probe the same GPU:
    nouveau/nova_core binds first from the initramfs, then nvidia finds the
    device already owned and initialised - on Blackwell that state conflict
    escalates to a kernel oops/panic instead of a clean fallback. The only
    blacklist honoured at the initramfs stage is the kernel command line
    (rd.driver.blacklist / modprobe.blacklist).

    Why it must NOT be added at install time (the June 2026 regression): the
    first boot happens BEFORE the driver exists. A cmdline blacklist written
    by the kickstart leaves that boot with no display driver at all whenever
    the driver is not ready (no internet yet, akmod build failure, Secure Boot
    rejecting the unsigned module) - a black screen. Adding the args HERE,
    after the module is verified on disk for every installed kernel, keeps
    both properties: first boot has a working display, and every subsequent
    boot hands the GPU to nvidia alone.

    The same guard covers Secure Boot: if the firmware will reject the
    unsigned module, keep the fallback drivers loadable and log the
    remediation instead of bricking the display.
    """
    if not module_ok_for_all:
        logger.error(
            "nvidia module not present for every installed kernel (%s) - "
            "leaving nouveau/nova_core loadable so the next boot still has a "
            "display driver. Re-run the agent after fixing the akmod build.",
            ", ".join(kvers) or "unknown",
        )
        return

    if _secure_boot_enabled():
        logger.error(
            "Secure Boot is ENABLED: the unsigned nvidia module will be rejected. "
            "NOT blacklisting nouveau/nova_core on the cmdline (the machine would "
            "lose its only working display driver). Fix: disable Secure Boot in "
            "firmware, or enrol the akmods MOK ('mokutil --import "
            "/etc/pki/akmods/certs/public_key.der') and re-run the agent."
        )
        return

    # Same argument set RPM Fusion's own xorg-x11-drv-nvidia scriptlet uses,
    # plus nova_core: Fedora kernels ship the in-tree Rust driver for
    # Blackwell, which binds the GPU exactly like nouveau does. The nomodeset
    # removal mirrors the scriptlet (a stray nomodeset would kill KMS for
    # every driver, nvidia included).
    args = ("rd.driver.blacklist=nouveau,nova_core "
            "modprobe.blacklist=nouveau,nova_core nvidia-drm.modeset=1")
    # NEVER replace --update-kernel=ALL with a per-kernel form
    # (--update-kernel=/boot/vmlinuz-<kver>). ALL is the mechanism RPM Fusion's
    # own scriptlet uses and the only one hardware-proven to write the
    # parameters on this path (RTX 5070 bare metal, July 2026). The per-kernel
    # path form was tried once: grubby exited nonzero for every entry, NO
    # kernel got the blacklist, and the machine kernel-panicked on the
    # nouveau/nova_core vs nvidia fight at the next boot - deterministically,
    # on every run, until this line was restored. If per-kernel granularity is
    # ever genuinely needed, validate the exact grubby invocation on the
    # installed system FIRST and keep ALL as the fallback.
    res = run_cmd(
        ["grubby", "--update-kernel=ALL", "--remove-args=nomodeset",
         f"--args={args}"],
        check=False, timeout=120,
    )
    if res.returncode == 0:
        logger.info("Kernel cmdline updated for ALL kernels: %s", args)
    else:
        logger.error(
            "grubby failed (exit %d) - the next boot may load nouveau/nova_core "
            "alongside nvidia again. Run manually: sudo grubby --update-kernel=ALL "
            "--args=\"%s\"", res.returncode, args,
        )


def _nvidia_module_present(kver: str) -> bool:
    """True when the akmods-built nvidia module exists on disk for this kernel."""
    base = Path(f"/lib/modules/{kver}")
    return (base / "extra/nvidia").is_dir() or bool(list(base.glob("extra/nvidia*")))


def install_gpu_drivers(manifest: dict[str, Any]) -> None:
    """Install NVIDIA drivers if the GPU is NVIDIA."""
    gpu = manifest.get("hardware", {}).get("gpuVendor", "").lower()
    if gpu != "nvidia":
        logger.info("GPU driver: vendor=%r, skipping NVIDIA step", gpu)
        return

    # Neutralise the kernel pull BEFORE the driver install. Verified against the
    # Fedora 44 updates repodata (3 Aug 2026): akmods-0.6.2-14.fc44 has the HARD
    # rich dependency `(kernel-devel-matched if kernel-core)` - NOT a weak dep,
    # so install_weak_deps=False would change nothing - and
    # kernel-devel-matched-7.1.5-201.fc44 hard-requires `kernel-core` +
    # `kernel-devel`. dnf resolves a name-only requirement to the NEWEST version,
    # so installing akmod-nvidia on a GA kernel dragged kernel-core 7.1.5 in.
    # But a name-based rich dep is already satisfied by ANY installed version:
    # pre-installing kernel-devel-matched + kernel-devel of the RUNNING kernel
    # (present in the frozen 'fedora' releases repo for the GA kernel) satisfies
    # the chain and no new kernel is pulled. check=False on purpose: if the
    # exact version is no longer in any repo (superseded updates kernel), the
    # install below simply pulls the newest kernel as before and the per-kernel
    # build + GRUB pin further down remain the safety net.
    running_kver = os.uname().release
    pre = run_cmd(
        ["dnf", "-y", "install",
         f"kernel-devel-matched-{running_kver}", f"kernel-devel-{running_kver}"],
        timeout=600, check=False,
    )
    if pre.returncode == 0:
        logger.info("Pre-installed kernel-devel-matched/kernel-devel for the running kernel %s - "
                    "the akmods rich dep is satisfied, no newer kernel will be pulled",
                    running_kver)
    else:
        logger.warning("Could not pre-install kernel-devel-matched-%s (version not in repos?) - "
                       "the driver install may pull a newer kernel; the per-kernel build and "
                       "GRUB pin below cover that", running_kver)

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
    good_kvers: list[str] = []
    for kver in kvers:
        if _nvidia_module_present(kver):
            logger.info("nvidia module present for kernel %s", kver)
            good_kvers.append(kver)
        else:
            logger.error("nvidia module MISSING for kernel %s - that kernel will boot without "
                         "the GPU driver", kver)

    # GRUB boots the NEWEST installed kernel by default - including one pulled
    # mid-install whose akmod build then failed (kernel 7.1.5 via
    # kernel-devel-matched, RTX 5070 bare-metal, July 2026: the reboot landed on
    # it with NO nvidia module and nouveau died on Blackwell, leaving a
    # corrupted framebuffer of repeated boot logos). Never leave the default to
    # chance: pin it to the newest kernel with a VERIFIED module. When every
    # kernel has the module this is the same kernel GRUB would pick anyway, so
    # the pin changes nothing in the good case.
    pinned_kver = good_kvers[-1] if good_kvers else None
    if pinned_kver and len(kvers) > 1:
        if run_cmd(["grubby", f"--set-default=/boot/vmlinuz-{pinned_kver}"],
                   check=False).returncode == 0:
            logger.info("Pinned the default boot entry to kernel %s (verified nvidia module)",
                        pinned_kver)
        else:
            logger.error("Could not pin the default kernel - GRUB may still boot an "
                         "unverified kernel")

    # An incomplete GPU driver is a FAILED run for forensics, even though no
    # step raised: mark it so cleanup-seed keeps the installer partitions (and
    # the exported logs) instead of deleting the evidence along with the
    # broken boot that follows.
    if len(good_kvers) < len(kvers):
        try:
            Path("/var/lib/igloo/.had-failures").write_text(
                "gpu-drivers: nvidia module missing for "
                f"{len(kvers) - len(good_kvers)} of {len(kvers)} kernel(s)\n",
                encoding="utf-8")
        except OSError:
            pass

    # Now that the module is confirmed on disk, take nouveau/nova_core off the
    # kernel cmdline path: their /etc/modprobe.d blacklist does not apply inside
    # the initramfs, so without rd.driver.blacklist they keep loading at every
    # boot and fight nvidia for the GPU (the RTX 5070 bare-metal kernel panic).
    # Applied whenever the kernel we will actually BOOT (the pinned one) has a
    # verified module - a failed build for a NEWER, non-default kernel must not
    # keep nouveau loadable on the good one, which is the known panic state.
    # Only when NO kernel has a module does the in-tree driver stay loadable.
    ensure_nvidia_kernel_cmdline(pinned_kver is not None, kvers)

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

        for br in data.get("browsers", []):
            if br.get("credentialsBlob") is not None:
                br["credentialsBlob"] = None
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
    kde_layout: list[dict[str, Any]] = []
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

        # Windows reports the ROTATED pixel dimensions (dmPelsWidth/Height in the
        # current orientation): a portrait monitor arrives as 2160x3840. Panels
        # only advertise landscape modes - portrait is a rotation transform, not
        # a mode - so the mode to check and to set is the unrotated one. The old
        # code checked 2160x3840 against the mode list, found nothing, and left
        # portrait monitors untouched (bare-metal RTX 5070, July 2026).
        rotation_deg = int(want.get("rotationDegrees") or 0)
        mode_w, mode_h = (height, width) if rotation_deg in (90, 270) else (width, height)
        if not _mode_is_supported(out["connector"], mode_w, mode_h):
            logger.warning("  %s does not advertise %dx%d - leaving this output alone",
                           out["connector"], mode_w, mode_h)
            continue

        # Windows reports whole Hz; panels advertise fractional rates (143.998).
        # Mutter tolerates a near match, so the integer value is written as-is.
        rate = int(want.get("refreshHz") or 60) or 60

        # Direction mapping validated on hardware (RTX 5070 dual-Odyssey G70D):
        # Windows dmDisplayOrientation=270 corresponds to the KDE/xrandr "right"
        # the user set by hand and confirmed correct - NOT "left" as the earlier
        # guess had it. 90 is the mirror image.
        rotation = {0: "normal", 90: "left", 180: "inverted", 270: "right"}.get(
            rotation_deg, "normal")

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
            f"        <mode><width>{mode_w}</width><height>{mode_h}</height>"
            f"<rate>{rate}</rate></mode>\n"
            "      </monitor>\n"
            "    </logicalmonitor>\n")
        matched += 1

        # KDE Plasma ignores monitors.xml entirely (kwin reads its own store),
        # and connector names are NOT stable across kernels/drivers (the same
        # port was DP-4 under 6.19.10/nouveau and DP-1 under 7.1.5/nvidia).
        # So for KDE we record the layout keyed by EDID PnP id; display-apply.py
        # re-resolves the connector at login time and calls kscreen-doctor.
        kde_layout.append({
            "pnpId": pnp,
            "width": mode_w,
            "height": mode_h,
            "rate": rate,
            "rotation": {0: "none", 90: "left", 180: "inverted", 270: "right"}.get(
                rotation_deg, "none"),
            # Windows display scaling as a factor (1.5 = 150%). display-apply.py
            # needs it twice: to convert Windows' PHYSICAL pixel positions into
            # KWin's LOGICAL ones (positions below are still physical here), and
            # to set the output scale so the desktop looks like it did on Windows.
            # 0/unknown in the manifest becomes 1.0 - no scaling.
            "scale": (int(want.get("scalePercent") or 100) or 100) / 100.0,
            "x": int(want.get("positionX") or 0),
            "y": int(want.get("positionY") or 0),
            "primary": bool(want.get("isPrimary")),
        })
        logger.info("  %s -> %dx%d@%dHz %s at (%s,%s) scale=%s%%", out["connector"],
                    mode_w, mode_h, rate, rotation, want.get("positionX"),
                    want.get("positionY"), int(want.get("scalePercent") or 100) or 100)

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

    # ── KDE Plasma path ──────────────────────────────────────────────────────
    # KWin ignores monitors.xml - on Fedora KDE everything above is inert. The
    # layout cannot be applied from this root context anyway: kscreen-doctor
    # needs a running Plasma session. So we persist the wanted layout and
    # register an autostart hook; display-apply.py runs at the user's first
    # login, re-resolves PnP ids to whatever the connectors are called THEN
    # (they change across kernels/drivers), and applies everything in one
    # atomic kscreen-doctor call.
    try:
        layout_path = Path("/opt/igloo/display-layout.json")
        layout_path.write_text(json.dumps(kde_layout, indent=2), encoding="utf-8")
        layout_path.chmod(0o644)

        apply_sh = Path("/opt/igloo/display-apply.sh")
        apply_sh.write_text(
            "#!/usr/bin/env bash\n"
            "# iGloo display layout for KDE Plasma - runs once per user at login.\n"
            "# Retries on later logins until kscreen-doctor reports success.\n"
            'DONE_MARKER="$HOME/.config/.igloo-display-done"\n'
            '[ -f "$DONE_MARKER" ] && exit 0\n'
            "python3 /opt/igloo/display-apply.py --layout /opt/igloo/display-layout.json \\\n"
            '  && touch "$DONE_MARKER"\n',
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
            # monitors.xml covers Mutter/Muffin desktops; this hook is KDE-only.
            "OnlyShowIn=KDE;\n",
            encoding="utf-8",
        )
        logger.info("KDE display layout staged: %s + autostart %s", layout_path, autostart)
    except OSError:
        logger.exception("Could not stage the KDE display layout (non-fatal)")


def migrate_wallpaper(manifest: dict[str, Any]) -> None:
    """Reproduce the Windows desktop wallpaper on KDE Plasma.

    The Windows side staged the image next to the manifest on the seed; the
    kickstart %post copied it to /opt/igloo. The file is placed in the user's
    Pictures folder (visibly theirs, not hidden in a system path). Setting it
    cannot happen from this root context: plasma-apply-wallpaperimage needs a
    running Plasma session, exactly like kscreen-doctor for the display
    layout. So we register a one-shot login hook with the same retry
    convention. Purely additive: no wallpaper in the manifest means the
    Fedora default is kept.
    """
    wp = manifest.get("wallpaper") or {}
    fname = (wp.get("fileName") or "").strip()
    if not fname:
        logger.info("No wallpaper in the manifest - keeping the Fedora default")
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

    try:
        apply_sh = Path("/opt/igloo/wallpaper-apply.sh")
        apply_sh.write_text(
            "#!/usr/bin/env bash\n"
            "# iGloo wallpaper for KDE Plasma - runs once per user at login.\n"
            "# Retries on later logins until plasma-apply-wallpaperimage succeeds.\n"
            'DONE_MARKER="$HOME/.config/.igloo-wallpaper-done"\n'
            '[ -f "$DONE_MARKER" ] && exit 0\n'
            'for wp in "$HOME/Pictures"/wallpaper.*; do\n'
            '  [ -f "$wp" ] || exit 0\n'
            "  if ! command -v plasma-apply-wallpaperimage >/dev/null 2>&1; then\n"
            '    mkdir -p "$HOME/.local/state"\n'
            '    echo "[$(date +%F\\ %T)] ERROR: plasma-apply-wallpaperimage not found" '
            '>> "$HOME/.local/state/igloo-display.log"\n'
            "    exit 1\n"
            "  fi\n"
            '  plasma-apply-wallpaperimage "$wp" && touch "$DONE_MARKER"\n'
            "  exit $?\n"
            "done\n",
            encoding="utf-8",
        )
        apply_sh.chmod(0o755)

        autostart = Path("/etc/xdg/autostart/igloo-wallpaper.desktop")
        autostart.write_text(
            "[Desktop Entry]\n"
            "Name=iGloo Wallpaper\n"
            "Comment=Set the migrated Windows desktop wallpaper\n"
            "Exec=/opt/igloo/wallpaper-apply.sh\n"
            "Icon=preferences-desktop-wallpaper\n"
            "Terminal=false\n"
            "Type=Application\n"
            "X-GNOME-Autostart-enabled=true\n"
            "OnlyShowIn=KDE;\n",
            encoding="utf-8",
        )
        logger.info("KDE wallpaper hook staged (runs at first login)")
    except OSError:
        logger.exception("Could not stage the KDE wallpaper hook (non-fatal)")


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


def export_logs_to_seed(manifest: dict[str, Any]) -> None:
    """Copy the install logs onto the FAT32 seed partition (OEMDRV/CIDATA).

    When the post-install boot fails (black screen, broken GPU driver), the
    logs on the Linux root are nearly unreachable - but the seed partition is
    FAT32, readable straight from Windows, and in USB mode it is never
    deleted. Dropping the logs there turns a blind failure into a one-plug
    diagnosis. Best-effort: any problem here just leaves the logs in place.
    """
    log_dir = Path("/var/log/igloo")
    if not log_dir.is_dir():
        return
    seed_dev = next((p for p in (Path("/dev/disk/by-label") / label
                                 for label in IGLOO_SEED_LABELS) if p.exists()), None)
    if seed_dev is None:
        logger.info("No seed partition present - logs stay in %s", log_dir)
        return

    mountpoint = Path("/run/igloo-seed-export")
    mounted = False
    try:
        mountpoint.mkdir(parents=True, exist_ok=True)
        if run_cmd(["mount", "-t", "vfat", str(seed_dev), str(mountpoint)],
                   check=False).returncode != 0:
            logger.info("Could not mount the seed partition for log export (non-fatal)")
            return
        mounted = True
        dest = mountpoint / "igloo-logs"
        dest.mkdir(exist_ok=True)
        for f in sorted(log_dir.glob("*.log")):
            dest.joinpath(f.name).write_bytes(f.read_bytes())
        # Anaconda's own log says what happened during the OS install itself -
        # the agent log only starts at first boot.
        anaconda = Path("/var/log/anaconda")
        if anaconda.is_dir():
            ana_dest = dest / "anaconda"
            ana_dest.mkdir(exist_ok=True)
            for f in sorted(anaconda.glob("*.log")):
                ana_dest.joinpath(f.name).write_bytes(f.read_bytes())
        # The akmods build logs carry the ACTUAL compiler error when the
        # nvidia module fails to build for a kernel (e.g. a driver branch that
        # predates the kernel's API). The agent log only records THAT the
        # build failed; these say WHY. *.failed.log files are per driver-
        # version-per-kernel.
        akmods_cache = Path("/var/cache/akmods")
        if akmods_cache.is_dir():
            ak_dest = dest / "akmods"
            for logf in sorted(akmods_cache.rglob("*.log")):
                rel = logf.relative_to(akmods_cache)
                target = ak_dest / rel
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(logf.read_bytes())
        logger.info("Exported install logs to %s (on %s)", dest, seed_dev)
    except OSError:
        logger.info("Log export to the seed partition failed (non-fatal)")
    finally:
        if mounted:
            run_cmd(["umount", str(mountpoint)], check=False)


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
    # A failed run keeps its seed partitions: they carry the exported logs and
    # the entire agent payload for forensics, and a rerun of the installer
    # wipes them anyway. Deleting them would destroy the evidence along with
    # the failure.
    if Path("/var/lib/igloo/.had-failures").exists():
        logger.warning("Earlier steps failed - leaving the installer partitions in place "
                       "for diagnosis")
        return

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
    # kernel-modules runs AFTER every step whose dnf transaction can pull in a
    # new kernel (gpu-drivers installs akmod-nvidia, which depends on
    # kernel-devel-matched -> the LATEST kernel; suggested-pkgs runs dnf too).
    # It previously ran BEFORE gpu-drivers and certified the kernel set one
    # minute before akmod-nvidia dragged kernel-core 7.1.5 in: the agent then
    # rebooted onto that kernel with kernel-modules-core but no kernel-modules
    # (no Wi-Fi driver - bare-metal RTX 5070 test, July 2026). The check itself
    # only installs exact-version kernel-modules packages and never pulls a new
    # kernel, so running it late is safe; running it early is blind.
    steps: list[tuple[str, Any]] = [
        ("rpmfusion",       lambda: enable_rpmfusion(manifest)),
        ("codecs",          lambda: install_codecs(manifest)),
        ("gpu-drivers",     lambda: install_gpu_drivers(manifest)),
        ("flathub",         lambda: setup_flathub(manifest)),
        ("suggested-pkgs",  lambda: install_suggested_packages(manifest)),
        ("kernel-modules",  lambda: ensure_kernel_modules(manifest)),
        ("wifi",            lambda: migrate_wifi(manifest)),
        ("display-layout",  lambda: migrate_display_layout(manifest)),
        ("wallpaper",       lambda: migrate_wallpaper(manifest)),
        ("welcome-app",     lambda: install_welcome_app(manifest)),
        # Chromium credentials: needs the plaintext linuxPassword, so it must
        # run before redact-manifest; needs nothing else, so it stays late.
        ("chromium-creds",  lambda: import_chromium_credentials(manifest)),
        ("redact-manifest", lambda: redact_manifest(manifest)),
        # export-logs must run BEFORE cleanup-seed: it copies the install logs
        # onto the seed partition, which cleanup then deletes on a successful
        # direct-install run (the USB-mode seed survives either way).
        ("export-logs",     lambda: export_logs_to_seed(manifest)),
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
            # Marker for cleanup-seed: a failed run keeps its seed partitions
            # (exported logs + agent payload) for forensics.
            try:
                Path("/var/lib/igloo/.had-failures").write_text(name + "\n", encoding="utf-8")
            except OSError:
                pass

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
