"""Boot menu (M15), shared by every distro family.

The agents differ in run_cmd and logging, and the distros differ in where GRUB
keeps its files, so both are passed in through Boot rather than imported.
"""
from __future__ import annotations

import os
import re
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

_LONGNAME_MARKER = "# igloo: rename the Windows Boot Manager entry"
_SUBMENU_MARKER = "# igloo: one entry per OS - no Advanced options submenu"
_OS_PROBER_SCRIPT = Path("/etc/grub.d/30_os-prober")
_LINUX_SCRIPT = Path("/etc/grub.d/10_linux")
_DROPIN = Path("/etc/default/grub.d/99-igloo-menu.cfg")
_GRUB_DEFAULT = Path("/etc/default/grub")

_GRUB_HOOK_TEMPLATE = r"""#!/bin/sh
# igloo: repairs the generated grub.cfg; re-run at boot and on kernel updates.
cfg={cfg}
[ -f "$cfg" ] || exit 0

# os-prober falls back to root=/dev/sdXN for any OS whose boot config it cannot
# read. Kernel names follow probe order, so such an entry boots whatever holds
# that name next time. See docs/reference/boot-menu.md.
grep -o -- 'root=/dev/[A-Za-z0-9/_.+-]*' "$cfg" | sort -u |
while read -r arg; do
    dev=$(printf '%s' "$arg" | cut -c6-)
    uuid=$(blkid -o value -s UUID "$dev" 2>/dev/null)
    if [ -z "$uuid" ]; then
        echo "igloo: $dev has no UUID, entry left alone" >&2
        continue
    fi
    sed -i "s|root=$dev\([[:space:]]\)|root=UUID=$uuid\1|g" "$cfg"
    sed -i "s|root=$dev\$|root=UUID=$uuid|g" "$cfg"
done

# grub-probe emits no hints on NVMe, so derive one per search line.
# hd0 is only unambiguous with a single disk.
[ "$(lsblk -dno NAME -e 7,11 | wc -l)" -eq 1 ] || exit 0
grep -o -- '--set=root [0-9A-Fa-f-]\{{4,\}}' "$cfg" | awk '{{print $2}}' | sort -u |
while read -r uuid; do
    dev=$(blkid -U "$uuid" 2>/dev/null) || continue
    [ -n "$dev" ] || continue
    partn=$(lsblk -rno PARTN "$dev" 2>/dev/null)
    disk=$(lsblk -rno PKNAME "$dev" 2>/dev/null)
    [ -n "$partn" ] && [ -n "$disk" ] || continue
    case "$(lsblk -dno PTTYPE "/dev/$disk" 2>/dev/null)" in
        gpt) label=gpt ;;
        dos) label=msdos ;;
        *) continue ;;
    esac
    sed -i "s|--set=root $uuid|--set=root --hint=hd0,$label$partn $uuid|g" "$cfg"
done
"""


@dataclass(frozen=True)
class Boot:
    """Everything that differs between agents and distro families."""
    run_cmd: Callable[..., Any]
    logger: Any
    grub_cfg: Path
    themes_dir: Path
    regenerate_cmd: list[str]
    grub_hook: Path


def debian_family(run_cmd: Callable[..., Any], logger: Any) -> Boot:
    cfg = Path("/boot/grub/grub.cfg")
    cmd = ["update-grub"] if shutil.which("update-grub") else ["grub-mkconfig", "-o", str(cfg)]
    return Boot(run_cmd, logger, cfg, Path("/boot/grub/themes/stylish"), cmd,
                Path("/etc/kernel/postinst.d/zzz-igloo-grub-fixups"))


def fedora(run_cmd: Callable[..., Any], logger: Any) -> Boot:
    cfg = Path("/boot/grub2/grub.cfg")
    return Boot(run_cmd, logger, cfg, Path("/boot/grub2/themes/stylish"),
                ["grub2-mkconfig", "-o", str(cfg)],
                Path("/etc/kernel/install.d/99-igloo-grub-fixups.install"))


def theme_variant(manifest: dict[str, Any]) -> str:
    """Panels above 2560x1440 get the 4k variant, as in upstream's install.sh."""
    for d in manifest.get("displays", []):
        w, h = int(d.get("widthPx") or 0), int(d.get("heightPx") or 0)
        if w > 2560 or h > 1440:
            return "4k"
    return "1080p"


def _install_theme(b: Boot, variant: str) -> bool:
    archive = Path(f"/opt/igloo/grub-theme-stylish-{variant}.tar.gz")
    if not archive.exists():
        b.logger.error("GRUB theme archive missing: %s - the menu stays stock", archive)
        return False
    b.themes_dir.mkdir(parents=True, exist_ok=True)
    res = b.run_cmd(["tar", "-xzf", str(archive), "-C", str(b.themes_dir)],
                    check=False, timeout=120)
    if res.returncode != 0:
        b.logger.error("Could not extract %s (rc=%d) - the menu stays stock",
                       archive, res.returncode)
        return False
    b.logger.info("Installed the Stylish GRUB theme (%s) in %s", variant, b.themes_dir)
    return True


def _write_dropin(b: Boot, variant: str, themed: bool, single_entry: bool,
                  collapse: bool) -> None:
    gfxmode = "3840x2160,auto" if variant == "4k" else "1920x1080,auto"
    lines = [
        "# iGloo boot menu (M15).",
        "# GRUB_CMDLINE_LINUX is deliberately not set here: the nouveau",
        "# blacklist lives in that variable on this family.",
        "GRUB_TIMEOUT=10",
        "GRUB_TIMEOUT_STYLE=menu",
        # Entry 0 is this system: 10_linux runs before 30_os-prober, so the
        # distro's own entry is always first. NOT GRUB_DEFAULT=saved with
        # GRUB_SAVEDEFAULT=true - that writes whatever was picked last into
        # grubenv, so one visit to Windows makes Windows the permanent default
        # and the machine never comes back on its own. See docs/reference/boot-menu.md.
        "GRUB_DEFAULT=0",
        "GRUB_TERMINAL_OUTPUT=gfxterm",
        f"GRUB_GFXMODE={gfxmode}",
        # Redundant when the 10_linux patch landed (those entries sit in the
        # part we skip), load-bearing in the fallback below.
        "GRUB_DISABLE_RECOVERY=true",
    ]
    # Only force the flat list when the patch was meant to run and failed. When
    # collapse is off on purpose, the stock submenu is what we want to keep.
    if collapse and not single_entry:
        lines += [
            "# Fallback: 10_linux could not be patched, so no clean single entry.",
            "GRUB_DISABLE_SUBMENU=y",
        ]
    if themed:
        lines.append(f"GRUB_THEME={b.themes_dir.as_posix()}/theme.txt")
    _DROPIN.parent.mkdir(parents=True, exist_ok=True)
    _DROPIN.write_text("\n".join(lines) + "\n", encoding="utf-8")


def _patch_os_prober_labels(b: Boot) -> None:
    """Rename the os-prober Windows entry to 'Windows 11' and drop the
    '(on /dev/...)' suffix. Marker-guarded; a grub package upgrade can revert it."""
    if not _OS_PROBER_SCRIPT.exists():
        b.logger.warning("30_os-prober not found - Windows entry keeps its stock label")
        return
    text = _OS_PROBER_SCRIPT.read_text(encoding="utf-8")
    if _LONGNAME_MARKER in text:
        b.logger.info("os-prober label patch already applied")
        return
    anchor = 'gettext_printf "Found %s on %s\\n" "${LONGNAME}" "${DEVICE}"'
    if anchor not in text:
        b.logger.warning("30_os-prober anchor not found (grub version drift?) - "
                         "Windows entry keeps its stock label")
        return
    patch = (f"{_LONGNAME_MARKER}\n"
             'if [ "${LONGNAME}" = "Windows Boot Manager" ]; then\n'
             '  LONGNAME="Windows 11"\n'
             "fi\n"
             "case \"${LONGNAME}\" in\n"
             '  *[Dd]ebian*) igloo_class="debian" ;;\n'
             '  *[Mm]int*)   igloo_class="linuxmint" ;;\n'
             '  *[Ff]edora*) igloo_class="fedora" ;;\n'
             '  *[Uu]buntu*) igloo_class="ubuntu" ;;\n'
             '  *)           igloo_class="" ;;\n'
             "esac\n")
    text = text.replace(anchor, patch + anchor, 1)

    text, count = re.subn(
        re.escape('onstr="$(gettext_printf "(on %s)" "${DEVICE}")"'), 'onstr=""', text)
    if count == 0:
        b.logger.warning("onstr pattern not found - entries keep their '(on ...)' suffix")

    # Fedora's 30_os-prober gives foreign distros only generic classes, so GRUB
    # falls back to the Tux icon. The :+ expansion emits nothing when unmatched.
    text, classes = re.subn(r"--class gnu-linux",
                            "${igloo_class:+--class ${igloo_class} }--class gnu-linux", text)
    if classes == 0:
        b.logger.warning("no '--class gnu-linux' in 30_os-prober - other distros keep "
                         "the generic icon")

    _OS_PROBER_SCRIPT.write_text(text, encoding="utf-8")
    b.logger.info("Patched 30_os-prober: Windows reads 'Windows 11', %d entry class(es) "
                  "now carry the distro icon", classes)


def _patch_linux_submenu(b: Boot) -> bool:
    """Cut 10_linux short after the clean entry so no 'Advanced options' submenu is
    generated. Idempotent via a marker; False means fall back to GRUB_DISABLE_SUBMENU."""
    if not _LINUX_SCRIPT.exists():
        b.logger.warning("10_linux not found - the Advanced options submenu stays")
        return False
    text = _LINUX_SCRIPT.read_text(encoding="utf-8")
    if _SUBMENU_MARKER in text:
        b.logger.info("10_linux submenu patch already applied")
        return True

    lines = text.splitlines(keepends=True)
    idx = next((i for i, ln in enumerate(lines) if "Advanced options for %s" in ln), None)
    if idx is None:
        b.logger.warning("10_linux submenu anchor not found (grub version drift?) - "
                         "falling back to GRUB_DISABLE_SUBMENU")
        return False
    # Refuse to cut before an entry was printed: that would leave a menu with no
    # Linux entry at all, which is the one way this cosmetic patch could hurt.
    if not any("linux_entry" in ln and "simple" in ln for ln in lines[:idx]):
        b.logger.warning("10_linux emits no 'simple' entry before the submenu "
                         "(grub version drift?) - falling back to GRUB_DISABLE_SUBMENU")
        return False

    indent = lines[idx][:len(lines[idx]) - len(lines[idx].lstrip())]
    block = "".join(indent + s + "\n" for s in (
        f"{_SUBMENU_MARKER} (M15).",
        "# The cleanly titled entry for the newest kernel has just been printed;",
        "# everything below this point builds the submenu, the per-kernel entries",
        "# and the recovery entries. Echoing title_correction_code first keeps",
        "# the patch behaviour-neutral whatever GRUB_DEFAULT is set to.",
        'echo "$title_correction_code"',
        "exit 0",
    ))
    lines.insert(idx, block)
    _LINUX_SCRIPT.write_text("".join(lines), encoding="utf-8")
    b.logger.info("Patched 10_linux: one entry per OS, no Advanced options submenu")
    return True


def _apply_grub_fixups(b: Boot) -> None:
    """Repair the two things grub-mkconfig leaves wrong on a multi-boot disk.

    Kernel-name root= arguments become UUIDs, and each search line gets the
    device hint grub-probe cannot produce on NVMe. Both are per-line: one shared
    value would be wrong for the os-prober entries of every other OS on the disk.
    See docs/reference/boot-menu.md.
    """
    script = _GRUB_HOOK_TEMPLATE.format(cfg=b.grub_cfg.as_posix())
    try:
        b.grub_hook.parent.mkdir(parents=True, exist_ok=True)
        b.grub_hook.write_text(script, encoding="utf-8")
        b.grub_hook.chmod(0o755)
    except OSError:
        b.logger.exception("Could not install the GRUB fixup hook (non-fatal)")
        return

    res = b.run_cmd([str(b.grub_hook)], check=False, timeout=120)
    if res.returncode != 0:
        b.logger.warning("GRUB fixup script exited %d - entries keep whatever "
                         "grub-mkconfig wrote", res.returncode)
        return
    try:
        cfg = b.grub_cfg.read_text(encoding="utf-8", errors="replace")
    except OSError:
        cfg = ""
    b.logger.info("grub.cfg now has %d device hint(s) and %d kernel-name root= "
                  "argument(s); %s re-applies this after kernel updates",
                  len(re.findall(r"--hint=", cfg)),
                  len(re.findall(r"root=/dev/", cfg)), b.grub_hook)
    _install_fixup_unit(b)


_FIXUP_UNIT = Path("/etc/systemd/system/igloo-grub-fixups.service")


def _install_fixup_unit(b: Boot) -> None:
    """Run the same hook on every boot, not only on kernel updates.

    A grub package upgrade re-runs grub-mkconfig by itself and throws both fixups
    away, and nothing in /etc/kernel fires for that. Repairing at boot means one
    failed attempt at worst instead of a machine that stays unbootable.
    """
    try:
        _FIXUP_UNIT.parent.mkdir(parents=True, exist_ok=True)
        _FIXUP_UNIT.write_text(
            "[Unit]\n"
            "Description=Igloo - repair root= and search hints in grub.cfg\n"
            f"ConditionPathIsReadWrite={b.grub_cfg.parent.as_posix()}\n"
            "After=local-fs.target\n"
            "\n"
            "[Service]\n"
            "Type=oneshot\n"
            f"ExecStart={b.grub_hook.as_posix()}\n"
            "\n"
            "[Install]\n"
            "WantedBy=multi-user.target\n",
            encoding="utf-8")
        b.run_cmd(["systemctl", "enable", "igloo-grub-fixups.service"], check=False)
        b.logger.info("Installed igloo-grub-fixups.service - grub.cfg is repaired "
                      "on every boot, not only after a kernel update")
    except OSError:
        b.logger.exception("Could not install the grub fixup unit; the kernel hook "
                           "still covers kernel updates")


def _regenerate(b: Boot) -> None:
    b.run_cmd(b.regenerate_cmd, check=False, timeout=300)


def _verify(b: Boot, themed: bool) -> None:
    """BR-07: prove from the generated grub.cfg that the menu actually changed."""
    try:
        cfg = b.grub_cfg.read_text(encoding="utf-8", errors="replace")
    except OSError:
        b.logger.error("VERIFICATION FAILED: %s unreadable after regeneration", b.grub_cfg)
        return

    # Two different failures look identical in grub.cfg, so split them here.
    if "stylish/theme.txt" in cfg:
        b.logger.info("verified: Stylish theme is referenced in grub.cfg")
    elif not themed:
        b.logger.error("no theme in grub.cfg because none was installed - the theme "
                       "archive was missing from /opt/igloo, not a drop-in problem")
    else:
        b.logger.error("VERIFICATION FAILED: the theme installed but grub.cfg does not "
                       "reference it - the drop-in was not sourced")

    if "--hint=" in cfg:
        b.logger.info("verified: search lines carry a device hint")
    else:
        b.logger.warning("no --hint= in grub.cfg - every boot scans all devices")

    # A kernel name here is a hang waiting to happen: the entry boots whatever
    # holds that name after the next probe, and systemd waits for it with no limit.
    stale = sorted(set(re.findall(r"root=/dev/\S+", cfg)))
    if stale:
        b.logger.error("VERIFICATION FAILED: %d entr(y/ies) boot a kernel-name device "
                       "path: %s", len(stale), ", ".join(stale))
    else:
        b.logger.info("verified: every entry names its root by UUID")

    if "gnulinux-advanced" in cfg:
        b.logger.error("VERIFICATION FAILED: the Advanced options submenu is still "
                       "in grub.cfg")
    else:
        b.logger.info("verified: no Advanced options submenu in grub.cfg")

    titles = [ln.strip() for ln in cfg.splitlines()
              if ln.startswith("menuentry ") or ln.startswith("submenu ")]
    b.logger.info("grub.cfg has %d top-level menu entries", len(titles))
    for line in titles[:12]:
        b.logger.info("  menu entry: %s", line)

    if any("Windows" in line for line in titles):
        b.logger.info("verified: Windows entry present in grub.cfg")
    else:
        b.logger.warning("no Windows menuentry found in grub.cfg")


_BOOT_ORDER_UNIT = Path("/etc/systemd/system/igloo-boot-order.service")


def install_boot_order_unit(b: Boot) -> None:
    """Re-assert the UEFI boot order on every boot, not just the first one.

    put_self_first_in_boot_order() below fixes the order once, but Windows puts
    itself back whenever it feels like it, and the first-boot agent is gated on
    /var/lib/igloo/.done so it never runs again. A one-shot fix for a recurring
    condition is no fix: the machine boots straight into Windows a week later and
    looks like Linux was never installed. This unit closes that.
    """
    try:
        _BOOT_ORDER_UNIT.parent.mkdir(parents=True, exist_ok=True)
        _BOOT_ORDER_UNIT.write_text(
            "[Unit]\n"
            "Description=Igloo - keep this system first in the UEFI boot order\n"
            # efivarfs must be mounted before efibootmgr can read or write.
            "After=local-fs.target\n"
            "ConditionPathExists=/opt/igloo/agent.py\n"
            "ConditionPathIsDirectory=/sys/firmware/efi\n"
            "\n"
            "[Service]\n"
            "Type=oneshot\n"
            "ExecStart=/usr/bin/python3 /opt/igloo/agent.py --fix-boot-order\n"
            "\n"
            "[Install]\n"
            "WantedBy=multi-user.target\n",
            encoding="utf-8")
        b.run_cmd(["systemctl", "enable", "igloo-boot-order.service"], check=False)
        b.logger.info("Installed igloo-boot-order.service - the boot order is now "
                      "re-asserted on every boot")
    except OSError:
        b.logger.exception("Could not install the boot-order unit; the order is "
                           "still set for this boot, but Windows can take it back")


def put_self_first_in_boot_order(b: Boot) -> None:
    """Move the entry we booted from to the front of the UEFI boot order.

    Windows Boot Manager reasserts itself at the top on updates and on some ordinary
    boots. When it does, the firmware runs bootmgfw.efi directly, shim never loads and
    the menu never appears - the machine looks like Linux was never installed. Nothing
    else puts it back, so the agent does.

    BootCurrent is used rather than matching on a distro name: this agent is running
    from that entry by definition, so it is the right one without guessing.
    """
    try:
        res = b.run_cmd(["efibootmgr"], check=False)
        out = res.stdout or ""
        current = re.search(r"^BootCurrent:\s*([0-9A-Fa-f]{4})", out, re.M)
        order = re.search(r"^BootOrder:\s*([0-9A-Fa-f,]+)", out, re.M)
        if not current or not order:
            b.logger.info("No BootCurrent/BootOrder from efibootmgr - boot order left alone")
            return

        me = current.group(1).upper()
        entries = [e.strip().upper() for e in order.group(1).split(",") if e.strip()]
        if entries and entries[0] == me:
            b.logger.info("UEFI boot order already starts with Boot%s", me)
            return

        new_order = [me] + [e for e in entries if e != me]
        if b.run_cmd(["efibootmgr", "-o", ",".join(new_order)], check=False).returncode == 0:
            b.logger.info("Moved Boot%s to the front of the UEFI boot order (was %s)",
                       me, ",".join(entries))
        else:
            b.logger.warning("Could not set the UEFI boot order - Windows may boot directly")
    except Exception:
        b.logger.exception("Boot-order fix failed (non-fatal)")


def configure_boot_menu(manifest: dict[str, Any], b: Boot,
                        collapse: bool = True) -> None:
    """Theme the menu, boot this system by default, rename the Windows entry.

    Every failure here is cosmetic: the stock menu remains and the system stays
    bootable. collapse=False keeps the per-kernel submenu, for the case where the
    newest kernel is not the one that boots correctly.
    """
    variant = theme_variant(manifest)
    themed = _install_theme(b, variant)
    _patch_os_prober_labels(b)
    # Before the drop-in: its content depends on whether this patch landed.
    single_entry = _patch_linux_submenu(b) if collapse else False
    _write_dropin(b, variant, themed, single_entry, collapse)
    # A machine installed by an earlier build may already carry saved_entry=Windows
    # in grubenv. GRUB_DEFAULT=0 ignores it, but leaving it there is a trap for
    # whoever reads grubenv next; both grub-editenv names exist across families.
    for editenv in ("grub-editenv", "grub2-editenv"):
        if shutil.which(editenv):
            b.run_cmd([editenv, "-", "unset", "saved_entry"], check=False)
            break
    _regenerate(b)
    put_self_first_in_boot_order(b)
    install_boot_order_unit(b)

    try:
        cfg = b.grub_cfg.read_text(encoding="utf-8", errors="replace")
    except OSError:
        cfg = ""
    if themed and "stylish/theme.txt" not in cfg:
        b.logger.warning("grub.d drop-in not sourced - falling back to /etc/default/grub")
        try:
            body = _DROPIN.read_text(encoding="utf-8")
            # First line of the drop-in doubles as the marker: a repeated run
            # must not append the block a second time.
            marker = body.splitlines()[0]
            current = _GRUB_DEFAULT.read_text(encoding="utf-8") if _GRUB_DEFAULT.exists() else ""
            if marker in current:
                b.logger.info("boot menu block already present in /etc/default/grub")
            else:
                with _GRUB_DEFAULT.open("a", encoding="utf-8") as f:
                    f.write("\n")
                    f.write(body)
        except OSError:
            b.logger.exception("Could not append to /etc/default/grub (non-fatal)")
        _DROPIN.unlink(missing_ok=True)
        _regenerate(b)

    # After the last regeneration: every one of them drops the hint again.
    _apply_grub_fixups(b)
    _verify(b, themed)
