#!/usr/bin/env python3
"""Ground truth for keeping this system first in the UEFI boot order.

Reported on desktop-living, twice: the machine boots straight into Windows and
the GRUB menu never appears, so it looks like Linux was never installed.

The order is set correctly at install time - that part was never broken. What
was broken is that it only ever happened once: put_self_first_in_boot_order runs
from the first-boot agent, whose unit carries
ConditionPathExists=!/var/lib/igloo/.done. Windows Boot Manager puts itself back
at the front on updates and on some ordinary boots, and after that nothing on
the machine ever ran the fix again. A one-shot fix for a recurring condition.

install_boot_order_unit() closes that: a unit that re-asserts the order on every
boot. These tests cover the reordering itself and the unit that carries it.
"""
import importlib.util
import sys
import types
from pathlib import Path

MOD_PATH = Path(__file__).resolve().parents[2] / "distros/_shared/agent/igloo_boot.py"
spec = importlib.util.spec_from_file_location("igloo_boot", MOD_PATH)
ib = importlib.util.module_from_spec(spec)
# @dataclass resolves its annotations through sys.modules, so register first.
sys.modules[spec.name] = ib
spec.loader.exec_module(ib)

# Verbatim shape of `efibootmgr` on the reporter's machine, with Windows having
# taken the front slot back and debian sitting third.
EFIBOOTMGR_WINDOWS_FIRST = """BootCurrent: 0003
Timeout: 1 seconds
BootOrder: 0000,0002,0003,0001
Boot0000* Windows Boot Manager
Boot0001* UEFI: Samsung SSD 990 PRO
Boot0002* iGloo distribution installer
Boot0003* debian
"""

EFIBOOTMGR_ALREADY_FIRST = """BootCurrent: 0003
BootOrder: 0003,0000,0001
Boot0000* Windows Boot Manager
Boot0001* UEFI: Samsung SSD 990 PRO
Boot0003* debian
"""

failures: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        print(f"  PASS  {name}")
    else:
        failures.append(name)
        print(f"  FAIL  {name}  {detail}")


class FakeBoot:
    """Records every command instead of running it."""

    def __init__(self, efibootmgr_output: str = "", rc: int = 0) -> None:
        self.output, self.rc, self.commands = efibootmgr_output, rc, []
        self.logger = types.SimpleNamespace(
            info=lambda *a, **k: None, warning=lambda *a, **k: None,
            error=lambda *a, **k: None, exception=lambda *a, **k: None)

    def run_cmd(self, cmd, **kw):
        self.commands.append(list(cmd))
        out = self.output if cmd[:1] == ["efibootmgr"] and len(cmd) == 1 else ""
        return types.SimpleNamespace(returncode=self.rc, stdout=out, stderr="")

    def order_set_to(self):
        for c in self.commands:
            if c[:2] == ["efibootmgr", "-o"]:
                return c[2]
        return None


def test_windows_stole_the_front_slot() -> None:
    print("Windows first, debian third")
    b = FakeBoot(EFIBOOTMGR_WINDOWS_FIRST)
    ib.put_self_first_in_boot_order(b)
    check("the booted entry is moved to the front",
          b.order_set_to() == "0003,0000,0002,0001", str(b.order_set_to()))


def test_already_first_is_left_alone() -> None:
    print("the order already starts with us")
    b = FakeBoot(EFIBOOTMGR_ALREADY_FIRST)
    ib.put_self_first_in_boot_order(b)
    check("efibootmgr is not asked to write anything", b.order_set_to() is None,
          str(b.commands))


def test_no_efi_variables() -> None:
    print("efibootmgr says nothing useful (BIOS boot, or no efivarfs)")
    b = FakeBoot("EFI variables are not supported on this system.")
    ib.put_self_first_in_boot_order(b)
    check("nothing written", b.order_set_to() is None, str(b.commands))


def test_unit_is_installed_and_enabled(tmp: Path) -> None:
    print("the unit that makes this happen on every boot")
    saved = ib._BOOT_ORDER_UNIT
    ib._BOOT_ORDER_UNIT = tmp / "etc" / "systemd" / "system" / "igloo-boot-order.service"
    b = FakeBoot()
    try:
        ib.install_boot_order_unit(b)
        text = ib._BOOT_ORDER_UNIT.read_text(encoding="utf-8")
    finally:
        ib._BOOT_ORDER_UNIT = saved

    check("it calls the agent's boot-order mode",
          "--fix-boot-order" in text, text)
    check("it runs on every boot, not once",
          "WantedBy=multi-user.target" in text
          and "/var/lib/igloo/.done" not in text, text)
    check("it stays out of the way on a BIOS machine",
          "ConditionPathIsDirectory=/sys/firmware/efi" in text, text)
    check("it waits for efivarfs", "After=local-fs.target" in text, text)
    check("systemctl enable was called",
          ["systemctl", "enable", "igloo-boot-order.service"] in b.commands,
          str(b.commands))


def test_configure_boot_menu_installs_it(tmp: Path) -> None:
    print("the install-time pass wires the unit up")
    # Every module-level path is redirected: on a dev box these are absolute
    # system paths that would otherwise be created on the drive running the test.
    saved = (ib._BOOT_ORDER_UNIT, ib._FIXUP_UNIT, ib._DROPIN)
    ib._BOOT_ORDER_UNIT = tmp / "igloo-boot-order.service"
    ib._FIXUP_UNIT = tmp / "igloo-grub-fixups.service"
    ib._DROPIN = tmp / "99-igloo-menu.cfg"
    b = FakeBoot(EFIBOOTMGR_WINDOWS_FIRST)
    b.grub_cfg = tmp / "grub.cfg"
    b.themes_dir = tmp / "themes"
    b.regenerate_cmd = ["true"]
    b.grub_hook = tmp / "hook"
    try:
        ib.configure_boot_menu({}, b)
    except Exception as exc:          # theming needs a real system; the order does not
        print(f"    (configure_boot_menu stopped early: {type(exc).__name__})")
    finally:
        installed = ib._BOOT_ORDER_UNIT.is_file()
        ib._BOOT_ORDER_UNIT, ib._FIXUP_UNIT, ib._DROPIN = saved
    check("the order was re-asserted", b.order_set_to() == "0003,0000,0002,0001",
          str(b.order_set_to()))
    check("and the unit was written", installed)


def test_default_entry_is_this_system(tmp: Path) -> None:
    print("choosing Windows once must not make Windows the default forever")
    saved = ib._DROPIN
    ib._DROPIN = tmp / "99-igloo-menu.cfg"
    b = FakeBoot()
    b.themes_dir = tmp / "themes"
    try:
        ib._write_dropin(b, "4k", themed=True, single_entry=True, collapse=True)
        text = ib._DROPIN.read_text(encoding="utf-8")
    finally:
        ib._DROPIN = saved

    # GRUB_SAVEDEFAULT writes the entry you just picked into grubenv and
    # GRUB_DEFAULT=saved boots it next time, so one visit to Windows pins
    # Windows permanently. That is what made the machine stop offering Linux.
    check("the default is entry 0, this system", "GRUB_DEFAULT=0" in text, text)
    check("the last choice is not remembered",
          "GRUB_SAVEDEFAULT" not in text, text)
    check("GRUB_DEFAULT=saved is gone", "GRUB_DEFAULT=saved" not in text, text)
    check("the menu is still shown for 10 s",
          "GRUB_TIMEOUT=10" in text and "GRUB_TIMEOUT_STYLE=menu" in text, text)


if __name__ == "__main__":
    import tempfile
    test_windows_stole_the_front_slot()
    test_already_first_is_left_alone()
    test_no_efi_variables()
    for test in (test_unit_is_installed_and_enabled, test_configure_boot_menu_installs_it,
                 test_default_entry_is_this_system):
        with tempfile.TemporaryDirectory(prefix="boot-order-test-") as d:
            test(Path(d))
    print()
    if failures:
        print(f"{len(failures)} FAILED: {', '.join(failures)}")
        sys.exit(1)
    print("all checks passed")
