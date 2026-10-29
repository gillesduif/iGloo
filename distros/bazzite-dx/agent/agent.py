#!/usr/bin/env python3
"""
Igloo first-boot migration agent for Bazzite DX.

Bazzite is an image-based Fedora: /usr is read-only, packages come with the
image, and updates replace the whole image. The Fedora KDE agent's package
steps (RPM Fusion, codecs, NVIDIA akmods, kernel-modules repair, dnf) have no
place here: codecs and the GPU driver are in the image, and the NVIDIA build of
the image was picked at install time. Its grub-mkconfig menu rework does not
apply either, because the image owns grub.cfg.

Everything about the user's data is the same on both, so this agent imports
the Fedora KDE agent (shipped next to it as fedora_agent.py) and runs its
migration steps unchanged: Wi-Fi, display layout, wallpaper, account picture,
browser profiles, credential import, log export and seed cleanup. Only the
steps an image-based system does differently are defined here.
"""
from __future__ import annotations

import contextlib
import json
import logging
import shutil
import sys
from pathlib import Path
from typing import Any, Iterator

import fedora_agent as base

logger = logging.getLogger("igloo.agent")

DISTRO_ID = "bazzite-dx"

# Written by the kickstart %post: the image reference this machine was installed from.
IMAGE_FILE = Path("/var/lib/igloo/image")

# The groups Bazzite DX gives wheel members (bazzite-dx-groups.service).
DX_GROUPS = ("docker", "incus-admin", "libvirt")
ETC_GROUP = Path("/etc/group")
USR_LIB_GROUP = Path("/usr/lib/group")

# The apps Bazzite's own ISO installs for the KDE images (installer/kde_flatpaks).
# They come from the live ISO's Flatpak store, which the netinstall does not have,
# so without this the system starts without a web browser.
DEFAULT_FLATPAKS = (
    "org.mozilla.firefox",
    "org.kde.gwenview",
    "org.kde.okular",
    "org.kde.kcalc",
    "org.kde.haruna",
    "io.github.DenysMb.Kontainer",
    "com.github.tchx84.Flatseal",
    "io.github.flattool.Warehouse",
    "com.vysp3r.ProtonPlus",
    "com.github.Matoking.protontricks",
    "org.gnome.Firmware",
)

GRUB_DEFAULT = Path("/etc/default/grub")
GRUB_CFG = Path("/boot/grub2/grub.cfg")


# ---------------------------------------------------------------------------
# Image
# ---------------------------------------------------------------------------

def booted_image(status: dict[str, Any]) -> dict[str, Any]:
    """The image reference the next boot follows, from ``bootc status --format=json``."""
    spec = (status.get("spec") or {}).get("image")
    if spec:
        return spec
    booted = ((status.get("status") or {}).get("booted") or {}).get("image") or {}
    return booted.get("image") or {}


def needs_signed_switch(image: dict[str, Any]) -> bool:
    """True when updates would not be verified against the container signature policy.

    The kickstart pulled the image with --no-signature-verification and then
    switched the origin to the signed reference. If that switch did not land the
    system still boots, but every update would be accepted unsigned.
    """
    return image.get("signature") != "containerPolicy"


def ensure_signed_image(manifest: dict[str, Any]) -> None:
    """Check the deployment follows the expected image, with signature checks on."""
    expected = IMAGE_FILE.read_text(encoding="utf-8").strip() if IMAGE_FILE.exists() else ""
    res = base.run_cmd(["bootc", "status", "--format=json"], check=False)
    try:
        status = json.loads(res.stdout or "{}")
    except json.JSONDecodeError:
        status = {}
    image = booted_image(status)
    current = image.get("image", "")
    logger.info("Deployment follows %s (transport=%s, signature=%s)",
                current or "<unknown>", image.get("transport"), image.get("signature"))

    if expected and current and current != expected:
        logger.warning("Expected %s but the deployment follows %s", expected, current)

    gpu = (manifest.get("hardware", {}).get("gpuVendor") or "").lower()
    ref = current or expected
    if gpu == "nvidia" and ref and "-nvidia" not in ref:
        logger.warning("NVIDIA GPU but %s is not the NVIDIA image - the desktop runs on "
                       "nouveau. Switch with: bootc switch %s", ref,
                       ref.replace("bazzite-dx", "bazzite-dx-nvidia", 1))

    target = expected or current
    if not target:
        logger.warning("No image reference known - cannot enforce signed updates")
        return
    if not needs_signed_switch(image):
        logger.info("Signed updates already enforced")
        return
    logger.info("Updates are not signature-checked yet - switching to the signed reference")
    # check=False: a failed step keeps the seed partitions for forensics, and a
    # transient bootc error is not worth that. It is logged loudly instead.
    res = base.run_cmd(["bootc", "switch", "--mutate-in-place", "--enforce-container-sigpolicy",
                        "--transport", "registry", target], check=False, timeout=300)
    if res.returncode != 0:
        logger.error("bootc switch failed (exit %d) - updates stay unverified until "
                     "'sudo bootc switch --enforce-container-sigpolicy %s' succeeds",
                     res.returncode, target)


def report_secure_boot(manifest: dict[str, Any]) -> None:
    """Log whether Secure Boot is on and the Universal Blue key is trusted.

    Informational: if the kernel booted with Secure Boot on, the key is
    enrolled by definition; the useful line is the one telling a user who
    turned Secure Boot off how to turn it back on.
    """
    if shutil.which("mokutil") is None:
        logger.info("mokutil not available - Secure Boot state unknown")
        return
    state = (base.run_cmd(["mokutil", "--sb-state"], check=False).stdout or "").strip()
    logger.info("Secure Boot: %s", state or "<unknown>")
    if "disabled" in state.lower() and manifest.get("hardware", {}).get("secureBootEnabled"):
        logger.warning("Secure Boot was on under Windows and is off now. To turn it back "
                       "on, run 'ujust enroll-secure-boot-key' first (password: universalblue)")


# ---------------------------------------------------------------------------
# Developer groups
# ---------------------------------------------------------------------------

def _groups_of(username: str) -> set[str]:
    res = base.run_cmd(["id", "-nG", username], check=False)
    return set((res.stdout or "").split()) if res.returncode == 0 else set()


def _ensure_etc_group_entry(group: str) -> bool:
    """Copy ``group`` from the image's /usr/lib/group into /etc/group, where usermod edits.

    The image defines its system groups in /usr/lib/group (nss-altfiles), and
    usermod refuses a group that /etc/group does not list. bazzite-dx-groups
    does the same copy.
    """
    try:
        etc = ETC_GROUP.read_text(encoding="utf-8")
        if any(ln.split(":", 1)[0] == group for ln in etc.splitlines()):
            return True
        line = next((ln for ln in USR_LIB_GROUP.read_text(encoding="utf-8").splitlines()
                     if ln.split(":", 1)[0] == group), None)
        if line is None:
            return False
        with ETC_GROUP.open("a", encoding="utf-8") as f:
            f.write(("" if etc.endswith("\n") or not etc else "\n") + line + "\n")
        return True
    except OSError:
        logger.exception("Could not add %s to %s", group, ETC_GROUP)
        return False


def ensure_dx_groups(manifest: dict[str, Any]) -> None:
    """Put the migrated user in docker, incus-admin and libvirt.

    Bazzite DX has a unit for this, but it runs once per image version and only
    for the first wheel member, so it cannot be relied on to have covered the
    user Anaconda just created. Missing groups are added directly.
    """
    username = (manifest.get("user", {}).get("preferredLinuxUsername") or "").strip()
    if not username:
        logger.info("No username in manifest - skipping developer groups")
        return
    missing = [g for g in DX_GROUPS if g not in _groups_of(username)]
    if not missing:
        logger.info("%s is already in %s", username, ", ".join(DX_GROUPS))
        return
    for group in missing:
        if not _ensure_etc_group_entry(group):
            logger.warning("Group %s does not exist on this image - skipped", group)
            continue
        res = base.run_cmd(["usermod", "-aG", group, username], check=False)
        if res.returncode == 0:
            logger.info("Added %s to %s", username, group)
        else:
            logger.warning("Could not add %s to %s (exit %d)", username, group, res.returncode)


# ---------------------------------------------------------------------------
# Boot menu
# ---------------------------------------------------------------------------

def set_grub_option(text: str, key: str, value: str) -> str:
    """``text`` (an /etc/default/grub) with ``key`` set to ``value``, replaced or appended."""
    lines = text.splitlines()
    out = [f"{key}={value}" if ln.split("=", 1)[0].strip() == key else ln for ln in lines]
    if f"{key}={value}" not in out:
        out.append(f"{key}={value}")
    return "\n".join(out) + "\n"


@contextlib.contextmanager
def boot_writable() -> Iterator[None]:
    """Remount /boot read-write for the duration, as bazzite-boot-remount does."""
    was_ro = False
    try:
        mounts = Path("/proc/mounts").read_text(encoding="utf-8")
        was_ro = any(f.split()[1:2] == ["/boot"] and "ro" in f.split()[3].split(",")
                     for f in mounts.splitlines() if len(f.split()) > 3)
    except OSError:
        pass
    if was_ro:
        base.run_cmd(["mount", "-o", "remount,rw", "/boot"], check=False)
    try:
        yield
    finally:
        if was_ro:
            base.run_cmd(["mount", "-o", "remount,ro", "/boot"], check=False)


def grub_output_path() -> str:
    """Where ``ujust regenerate-grub`` writes grub.cfg on this firmware."""
    link = Path("/etc/grub2-efi.cfg") if Path("/sys/firmware/efi").is_dir() else Path("/etc/grub2.cfg")
    return str(link) if link.exists() else str(GRUB_CFG)


def configure_boot_menu(manifest: dict[str, Any]) -> None:
    """Add Windows to the GRUB menu and keep this system first in the UEFI order.

    Bazzite's grub.cfg reads the image's boot entries itself, so the Fedora
    agent's menu rework (theme, renamed entries, collapsed kernels) has nothing
    to hook into. What a dual-boot user needs is the Windows entry: Fedora
    turns os-prober off, Bazzite documents ``ujust regenerate-grub`` to bring
    Windows in, and this does the same without the prompt.
    """
    dual_boot = manifest.get("hardware", {}).get("installMode") == "dual-boot"
    if not dual_boot:
        # Nothing to add on a disk Linux has to itself: leave the image's
        # grub.cfg exactly as bootupd wrote it.
        logger.info("Replace mode - GRUB menu left as installed")
    else:
        _add_windows_to_menu()

    if base.igloo_boot is None:
        logger.error("igloo_boot.py not staged - the UEFI boot order is left alone")
        return
    boot = base.igloo_boot.fedora(base.run_cmd, logger)
    base.igloo_boot.put_self_first_in_boot_order(boot)
    base.igloo_boot.install_boot_order_unit(boot)


def _add_windows_to_menu() -> None:
    """Enable os-prober, regenerate grub.cfg and keep the menu visible."""
    try:
        current = GRUB_DEFAULT.read_text(encoding="utf-8") if GRUB_DEFAULT.exists() else ""
        GRUB_DEFAULT.write_text(set_grub_option(current, "GRUB_DISABLE_OS_PROBER", "false"),
                                encoding="utf-8")
        logger.info("os-prober enabled in %s", GRUB_DEFAULT)
    except OSError:
        logger.exception("Could not enable os-prober - Windows stays out of the menu")

    with boot_writable():
        res = base.run_cmd(["grub2-mkconfig", "-o", grub_output_path()], check=False, timeout=300)
        if res.returncode != 0:
            logger.error("grub2-mkconfig failed (exit %d) - run 'ujust regenerate-grub' by hand",
                         res.returncode)
        # 1 = hide the menu after a good boot, except when another OS is present.
        # The deck-based images default to hiding it always, which leaves no way
        # back to Windows without knowing to hold Esc.
        base.run_cmd(["grub2-editenv", "-", "set", "menu_auto_hide=1"], check=False)
        logger.info("GRUB menu shown at every boot (dual-boot)")

    try:
        cfg = GRUB_CFG.read_text(encoding="utf-8", errors="replace")
    except OSError:
        cfg = ""
    if "Windows" in cfg:
        logger.info("verified: Windows entry present in %s", GRUB_CFG)
    else:
        logger.warning("no Windows entry in %s - choose Windows from the firmware boot menu, "
                       "or run 'ujust regenerate-grub'", GRUB_CFG)


# ---------------------------------------------------------------------------
# Applications
# ---------------------------------------------------------------------------

def install_default_apps(manifest: dict[str, Any]) -> None:
    """Install the apps Bazzite's own installer would have put on the system.

    Firefox first and on its own: it is the one the user cannot do without,
    and browser-profiles moves the migrated profile into its Flatpak home.
    """
    base.run_cmd(["flatpak", "install", "-y", "--noninteractive", "--system", "flathub",
                  DEFAULT_FLATPAKS[0]], timeout=1800)
    res = base.run_cmd(["flatpak", "install", "-y", "--noninteractive", "--system", "flathub",
                        *DEFAULT_FLATPAKS[1:]], check=False, timeout=3600)
    if res.returncode != 0:
        logger.warning("Some of Bazzite's default apps did not install (exit %d) - "
                       "they can be added from Bazaar", res.returncode)

def install_suggested_packages(manifest: dict[str, Any]) -> None:
    """Install the auto-install Flatpaks; native packages have no place on this system.

    Layering RPMs onto the image works, but every layered package slows each
    update and can block it, so Bazzite steers users to Flatpak, Homebrew and
    distrobox instead. Native suggestions are logged, not installed.
    """
    pkgs = [p for p in manifest.get("suggestedPackages", []) if p.get("autoInstall")]
    if not pkgs:
        logger.info("No auto-install packages in manifest")
        return

    flatpak_ids = [p["flatpakId"] for p in pkgs if p.get("flatpakId")]
    native_only = [p.get("linuxAppName") or p["nativePackage"] for p in pkgs
                   if p.get("nativePackage") and not p.get("flatpakId")]

    if flatpak_ids:
        logger.info("Installing Flatpak: %s", ", ".join(flatpak_ids))
        base.run_cmd(["flatpak", "install", "-y", "--noninteractive", "flathub"] + flatpak_ids,
                     timeout=1800)
    if native_only:
        logger.warning("Not installed (no Flatpak, and the system image is read-only): %s. "
                       "Install them with Homebrew or in a distrobox.", ", ".join(native_only))


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def steps(manifest: dict[str, Any]) -> list[tuple[str, Any]]:
    """The first-boot steps, in order. Each is best-effort."""
    return [
        ("image",            lambda: ensure_signed_image(manifest)),
        ("secure-boot",      lambda: report_secure_boot(manifest)),
        ("dx-groups",        lambda: ensure_dx_groups(manifest)),
        ("boot-menu",        lambda: configure_boot_menu(manifest)),
        ("flathub",          lambda: base.setup_flathub(manifest)),
        ("default-apps",     lambda: install_default_apps(manifest)),
        # Before browser-profiles: the Flatpak browsers must exist for the
        # profiles to be moved into their homes.
        ("suggested-pkgs",   lambda: install_suggested_packages(manifest)),
        ("wifi",             lambda: base.migrate_wifi(manifest)),
        ("display-layout",   lambda: base.migrate_display_layout(manifest)),
        ("wallpaper",        lambda: base.migrate_wallpaper(manifest)),
        ("account-picture",  lambda: base.migrate_account_picture(manifest)),
        ("browser-profiles", lambda: base.migrate_browser_profiles(manifest)),
        ("welcome-app",      lambda: base.install_welcome_app(manifest)),
        ("stage-credentials", lambda: base.stage_credential_import(manifest)),
        ("redact-manifest",  lambda: base.redact_manifest(manifest)),
        ("export-logs",      lambda: base.export_logs_to_seed(manifest)),
        ("cleanup-seed",     lambda: base.cleanup_installer_partitions(manifest)),
    ]


def main() -> int:
    args = base.parse_args()

    if args.import_credentials:
        return base._run_user_mode()
    if args.fix_boot_order:
        return base._run_boot_order_mode()

    base.configure_logging(args.log_dir)
    # "/" is a composefs overlay here; the root partition is mounted at /sysroot.
    base.ROOT_MOUNT = "/sysroot"

    logger.info("=== Igloo first-boot agent (Bazzite DX) starting ===")
    logger.info("Manifest : %s", args.manifest)
    logger.info("Log dir  : %s", args.log_dir)

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
    if distro != DISTRO_ID:
        logger.error("Manifest is for distro %r, not %s", distro, DISTRO_ID)
        return 3

    failures: list[str] = []
    for name, step in steps(manifest):
        logger.info("--- step: %s ---", name)
        try:
            step()
            logger.info("step %s: OK", name)
        except Exception:
            logger.exception("step %s FAILED", name)
            failures.append(name)
            # cleanup-seed keeps the seed partitions of a failed run for forensics.
            try:
                Path("/var/lib/igloo/.had-failures").write_text(name + "\n", encoding="utf-8")
            except OSError:
                pass

    if failures:
        logger.warning("First-boot agent finished with %d failed step(s): %s",
                       len(failures), ", ".join(failures))
    else:
        logger.info("All steps completed successfully")

    logger.info("=== Igloo first-boot agent done ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
