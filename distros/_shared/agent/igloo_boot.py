"""Boot menu (M15), shared by every distro family.

The agents differ in run_cmd and logging, and the distros differ in where GRUB
keeps its files, so both are passed in through Boot rather than imported.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import unicodedata
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, NamedTuple

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


# The theme folder and archives keep their first name: the installer seed lists the archives by that name.
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
    b.logger.info("Installed the iGloo GRUB theme (%s) in %s", variant, b.themes_dir)
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


def require_windows_boot_entry(path: Path) -> None:
    """Require an actual EFI chainloader in a Windows menu entry.

    This checks generated configuration, not Windows bootability or ESP ownership.
    A Windows title, diagnostic message or commented-out command is not enough.
    """
    try:
        cfg = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        raise RuntimeError("The generated GRUB menu is unreadable; dual-boot setup is incomplete") from error
    entries = re.finditer(
        r"^[ \t]*menuentry[ \t]+([^\n]+)\n(.*?)^[ \t]*}[ \t]*(?:#.*)?$",
        cfg, re.MULTILINE | re.DOTALL)
    for entry in entries:
        header, body = entry.groups()
        if not re.search(r"\bwindows\b", header, re.IGNORECASE):
            continue
        if re.search(
                r"^[ \t]*chainloader[ \t]+(?:\([^\r\n)]+\))?"
                r"/EFI/Microsoft/Boot/bootmgfw\.efi[ \t]*(?:#.*)?$",
                body, re.MULTILINE | re.IGNORECASE):
            return
    raise RuntimeError("The generated GRUB menu has no Windows EFI boot entry; dual-boot setup is incomplete")


def _regenerate(b: Boot, *, require_windows: bool = False) -> None:
    b.run_cmd(b.regenerate_cmd, check=require_windows, timeout=300)
    if require_windows:
        require_windows_boot_entry(b.grub_cfg)


def _uses_text_terminal(cfg: str) -> bool:
    """Recognize an explicit GRUB text/serial terminal in generated output.

    Fedora preserves the serial console selected for unattended VM installs.
    GRUB cannot render a graphical theme there, and replacing that terminal
    would remove the recovery console. Multiple output directives are left
    ambiguous so the normal theme verification still runs.
    """
    outputs = [line.strip().split()[1:] for line in cfg.splitlines()
               if line.strip().startswith("terminal_output ")]
    return len(outputs) == 1 and bool(outputs[0]) and "gfxterm" not in outputs[0]


def _verify(b: Boot, themed: bool) -> None:
    """BR-07: prove from the generated grub.cfg that the menu actually changed."""
    try:
        cfg = b.grub_cfg.read_text(encoding="utf-8", errors="replace")
    except OSError:
        b.logger.error("VERIFICATION FAILED: %s unreadable after regeneration", b.grub_cfg)
        return

    # Two different failures look identical in grub.cfg, so split them here.
    if "stylish/theme.txt" in cfg:
        b.logger.info("verified: the iGloo theme is referenced in grub.cfg")
    elif themed and _uses_text_terminal(cfg):
        b.logger.info("iGloo theme installed; GRUB uses a text/serial terminal "
                      "and keeps that recovery console")
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

    # os-prober lists another Linux with its own submenu ('osprober-gnulinux-advanced-…'); that one is not ours to remove.
    if "'gnulinux-advanced-" in cfg:
        b.logger.error("VERIFICATION FAILED: the Advanced options submenu is still "
                       "in grub.cfg")
    else:
        b.logger.info("verified: no Advanced options submenu of this system in grub.cfg")
    if "'osprober-gnulinux-advanced-" in cfg:
        b.logger.info("another Linux on this machine is listed with its own Advanced options submenu")

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


_WIZARD_FIRST, _WIZARD_LAST = 0x0080, 0x00FE


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
        listed = {n.upper() for n in re.findall(r"^Boot([0-9A-Fa-f]{4})[* ] ", out, re.M)}
        # The wizard numbers its own entry 0080-00FE; firmware can drop the entry and keep the number.
        gone = [e for e in entries
                if me in listed and e not in listed and _WIZARD_FIRST <= int(e, 16) <= _WIZARD_LAST]
        new_order = [me] + [e for e in entries if e != me and e not in gone]
        if new_order == entries:
            b.logger.info("UEFI boot order already starts with Boot%s", me)
            return

        if b.run_cmd(["efibootmgr", "-o", ",".join(new_order)], check=False).returncode == 0:
            if entries[:1] != [me]:
                b.logger.info("Moved Boot%s to the front of the UEFI boot order (was %s)",
                              me, ",".join(entries))
            for number in gone:
                b.logger.info("Dropped Boot%s from the UEFI boot order: no such entry exists", number)
        else:
            b.logger.warning("Could not set the UEFI boot order - Windows may boot directly")
    except Exception:
        b.logger.exception("Boot-order fix failed (non-fatal)")


_BASIC_DATA_GPT_TYPE = uuid.UUID("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7")
_SEED_INVENTORY_MOUNT = Path("/run/igloo-seed-inventory")
_SEED_MOUNTPOINTS = ("/run/igloo-seed", "/run/igloo-seed-verify", "/run/igloo-seed-export",
                     "/run/igloo-seed-redaction", str(_SEED_INVENTORY_MOUNT))
_SEED_TOOLS = ("sfdisk", "partx", "efibootmgr")
_SEED_RECEIPT = "igloo-seed-reuse.json"
# The installer menu marks its first start here; GRUB changes the file, so no receipt or identity lists it.
_SEED_START_MARK = "igloo-boot/grubenv"
# Written next to iGloo's own files by the Fedora log export and by Windows.
_SEED_OTHER_TREES = ("igloo-logs/", "system volume information/")
_GUID = r"[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}"


class _TableRow(NamedTuple):
    node: str
    number: str
    type: uuid.UUID
    guid: uuid.UUID


class _Seed(NamedTuple):
    partition: str
    disk: str
    number: str
    table: list[uuid.UUID]


def _same_guid(text: Any, expected: uuid.UUID) -> bool:
    try:
        return uuid.UUID(str(text).strip()) == expected
    except ValueError:
        return False


def _seed_identity(identity_path: Path) -> dict[str, Any]:
    identity = json.loads(identity_path.read_bytes())
    if not isinstance(identity, dict) or identity.get("version") != 1:
        raise RuntimeError("Installer seed identity has an unknown version")
    return identity


def seed_identity_guids(identity_path: Path) -> tuple[uuid.UUID, uuid.UUID]:
    """GPT disk and partition GUID of the seed this installation started from."""
    identity = _seed_identity(identity_path)
    try:
        disk = uuid.UUID(identity["diskGuid"])
        partition = uuid.UUID(identity["partitionGuid"])
    except (KeyError, ValueError, TypeError, AttributeError) as error:
        raise RuntimeError("Installer seed identity has no valid GPT identity") from error
    if disk.int == 0 or partition.int == 0:
        raise RuntimeError("Installer seed identity has an empty GPT identity")
    return disk, partition


def seed_carries_user_data(identity_path: Path) -> bool:
    """True when the seed identity binds a file from the user's folders or browser profiles."""
    entries = _seed_identity(identity_path).get("files")
    if not isinstance(entries, list):
        raise RuntimeError("Installer seed identity has no file list")
    return any(isinstance(entry, dict) and isinstance(entry.get("path"), str)
               and entry["path"].lower().startswith(("files/", "browser-profiles/"))
               for entry in entries)


def _table_rows(dump: str) -> list[_TableRow]:
    """The partition rows of an sfdisk dump of a GPT disk."""
    if not re.search(r"^label:\s*gpt\s*$", dump, re.M):
        raise RuntimeError("The installer partition is not on a GPT disk")
    rows = []
    for line in dump.splitlines():
        row = re.match(r"^(\S+?(\d+))\s*:\s*start=(.*)$", line)
        if not row:
            continue
        kind = re.search(rf"\btype=({_GUID})\b", row.group(3))
        guid = re.search(rf"\buuid=({_GUID})\b", row.group(3))
        if not kind or not guid:
            raise RuntimeError("The partition table on disk is not understood")
        rows.append(_TableRow(row.group(1), row.group(2),
                              uuid.UUID(kind.group(1)), uuid.UUID(guid.group(1))))
    return rows


def find_seed_partition(b: Boot, disk_guid: uuid.UUID, partition_guid: uuid.UUID) -> _Seed | None:
    """The one partition with the seed's GPT identity; None when it is gone, an error on any doubt."""
    listing = b.run_cmd(["lsblk", "-rno", "PATH,PARTUUID"]).stdout or ""
    matches = [fields[0] for fields in (line.split() for line in listing.splitlines())
               if len(fields) == 2 and _same_guid(fields[1], partition_guid)]
    if not matches:
        return None
    if len(matches) != 1:
        raise RuntimeError("More than one partition carries the installer seed GUID")
    partition = matches[0]
    parents = (b.run_cmd(["lsblk", "-rno", "PKNAME", partition]).stdout or "").split()
    if len(parents) != 1 or not re.fullmatch(r"[A-Za-z0-9._-]+", parents[0]):
        raise RuntimeError("The installer partition has no single parent disk")
    disk = "/dev/" + parents[0]
    if not _same_guid(b.run_cmd(["blkid", "-s", "PTUUID", "-o", "value", disk]).stdout, disk_guid):
        raise RuntimeError("The installer partition sits on a disk its identity does not name")
    # A seed on a USB stick is the user's installer medium, not a leftover on the Windows disk.
    transport = (b.run_cmd(["lsblk", "-dnro", "RM,TRAN", disk]).stdout or "").split()
    if not transport or transport[0] != "0" or "usb" in transport[1:]:
        raise RuntimeError("The installer partition is on a removable or USB disk")
    file_system = (b.run_cmd(["blkid", "-p", "-s", "TYPE", "-o", "value", partition],
                             check=False).stdout or "").strip().lower()
    if file_system != "vfat":
        raise RuntimeError("The partition with the installer seed GUID is not FAT32")

    # The table on disk decides the number sfdisk is given, not a device name.
    table = _table_rows(b.run_cmd(["sfdisk", "--dump", disk]).stdout or "")
    rows = [row for row in table if row.guid == partition_guid]
    if len(rows) != 1 or os.path.realpath(rows[0].node) != os.path.realpath(partition):
        raise RuntimeError("The partition table on disk does not match the running kernel")
    if rows[0].type != _BASIC_DATA_GPT_TYPE:
        raise RuntimeError("The partition with the installer seed GUID is not a data partition")
    return _Seed(partition, disk, rows[0].number, [row.guid for row in table])


def _seed_path_key(path: str) -> str:
    return unicodedata.normalize("NFC", path.replace("\\", "/")).casefold()


def _listed_seed_paths(document: Any) -> set[str]:
    files = document.get("files") if isinstance(document, dict) else None
    if not isinstance(files, list) or any(
            not isinstance(entry, dict) or not isinstance(entry.get("path"), str) for entry in files):
        raise RuntimeError("An installer seed file list is not understood")
    return {_seed_path_key(entry["path"]) for entry in files}


def foreign_seed_files(b: Boot, partition: str, identity_path: Path) -> list[str]:
    """Files on the seed volume that neither its identity nor its reuse receipt lists."""
    mountpoint = _SEED_INVENTORY_MOUNT
    mountpoint.mkdir(parents=True, exist_ok=True)
    b.run_cmd(["mount", "-t", "vfat", "-o", "ro,nosuid,nodev,noexec,utf8", partition, str(mountpoint)])
    try:
        known = (_listed_seed_paths(_seed_identity(identity_path))
                 | _listed_seed_paths(json.loads((mountpoint / _SEED_RECEIPT).read_bytes()))
                 | {_SEED_RECEIPT, "igloo-seed-identity.json", _SEED_START_MARK})

        def stop(error: OSError) -> None:
            raise error

        found = []
        for directory, _, names in os.walk(mountpoint, onerror=stop):
            for name in names:
                relative = (Path(directory) / name).relative_to(mountpoint).as_posix()
                key = _seed_path_key(relative)
                if key not in known and not key.startswith(_SEED_OTHER_TREES):
                    found.append(relative)
        return sorted(found)
    finally:
        b.run_cmd(["umount", str(mountpoint)])


def boot_entries_for_partition(listing: str, partition_guid: uuid.UUID) -> list[str]:
    """Boot#### numbers whose device path names the partition, from an efibootmgr listing."""
    numbers = []
    for line in listing.splitlines():
        entry = re.match(r"^Boot([0-9A-Fa-f]{4})\*?\s", line)
        target = re.search(rf"HD\(\d+,GPT,({_GUID}),", line)
        if entry and target and _same_guid(target.group(1), partition_guid):
            numbers.append(entry.group(1).upper())
    return numbers


def publish_completion(marker: Path) -> None:
    """Record durably that the first-boot pass is complete, as its launcher does after the pass."""
    if not marker.exists():
        temporary = marker.with_name(marker.name + ".igloo-writing")
        with temporary.open("wb") as output:
            output.write(b"complete\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, marker)
    directory = os.open(marker.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def remove_installer_seed(b: Boot, identity_path: Path, completion_marker: Path) -> None:
    """Delete the partition the seed identity names and the firmware entries whose path points at it."""
    disk_guid, partition_guid = seed_identity_guids(identity_path)
    missing = [tool for tool in _SEED_TOOLS if shutil.which(tool) is None]
    if missing:
        raise RuntimeError("Not installed: " + ", ".join(missing))
    seed = find_seed_partition(b, disk_guid, partition_guid)
    if seed is None:
        b.logger.info("No partition carries installer seed GUID %s any more", partition_guid)
    else:
        mounted = (b.run_cmd(["findmnt", "-rno", "TARGET", "-S", seed.partition],
                             check=False).stdout or "").split()
        foreign = [target for target in mounted if target not in _SEED_MOUNTPOINTS]
        if foreign:
            raise RuntimeError(f"The installer partition is mounted at {foreign[0]}")
        for target in mounted:
            b.run_cmd(["umount", target])
        # The volume has a drive letter in Windows; a file the user put there must not go with it.
        strangers = foreign_seed_files(b, seed.partition, identity_path)
        if strangers:
            raise RuntimeError(f"The installer partition holds {len(strangers)} file(s) iGloo did not "
                               f"write, such as {strangers[0]}")
        # A pass whose seed is gone cannot run again, so its completion is durable first.
        publish_completion(completion_marker)
        # The re-read ioctl fails on the disk the system runs from; partx tells the kernel below.
        b.run_cmd(["sfdisk", "--no-tell-kernel", "--delete", seed.disk, seed.number], check=False)
        left = [row.guid for row in _table_rows(b.run_cmd(["sfdisk", "--dump", seed.disk]).stdout or "")]
        if partition_guid in left:
            raise RuntimeError("The installer partition is still in the partition table")
        if sorted(left, key=str) != sorted((guid for guid in seed.table if guid != partition_guid), key=str):
            raise RuntimeError(f"The partition table of {seed.disk} changed beyond the installer partition")
        if b.run_cmd(["partx", "--delete", "--nr", seed.number, seed.disk], check=False).returncode != 0:
            b.logger.info("The kernel keeps %s until the next boot", seed.partition)
        b.logger.info("Deleted installer partition %s (number %s on %s, GPT %s)",
                      seed.partition, seed.number, seed.disk, partition_guid)

    listing = b.run_cmd(["efibootmgr", "-v"], check=False).stdout or ""
    for number in boot_entries_for_partition(listing, partition_guid):
        if b.run_cmd(["efibootmgr", "-b", number, "-B"], check=False).returncode == 0:
            b.logger.info("Removed UEFI entry Boot%s, which pointed at the installer partition", number)
        else:
            b.logger.warning("Could not remove UEFI entry Boot%s", number)


def configure_boot_menu(manifest: dict[str, Any], b: Boot,
                        collapse: bool = True, *, require_windows: bool = False) -> None:
    """Theme the menu, boot this system by default, rename the Windows entry.

    Cosmetic failures retain the stock menu. require_windows makes regeneration
    and the Windows EFI entry mandatory. collapse=False keeps the per-kernel
    submenu, for the case where the newest kernel is not the one that boots.
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
    _regenerate(b, require_windows=require_windows)
    if not require_windows:
        put_self_first_in_boot_order(b)
        install_boot_order_unit(b)

    try:
        cfg = b.grub_cfg.read_text(encoding="utf-8", errors="replace")
    except OSError:
        cfg = ""
    if themed and "stylish/theme.txt" not in cfg and not _uses_text_terminal(cfg):
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
        _regenerate(b, require_windows=require_windows)

    # After the last regeneration: every one of them drops the hint again.
    _apply_grub_fixups(b)
    _verify(b, themed)
    if require_windows:
        require_windows_boot_entry(b.grub_cfg)
        # The theme fallback and fixups can change the first generated menu.
        # Do not activate this boot path until its final content is accepted.
        put_self_first_in_boot_order(b)
        install_boot_order_unit(b)
