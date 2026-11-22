"""The iGloo menu replaces Fedora's stock text menu and leaves a serial console alone."""

import importlib.util
import sys
from contextlib import ExitStack
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, main
from unittest.mock import patch


SOURCE = Path(__file__).resolve().parents[2] / "distros/_shared/agent/igloo_boot.py"
SPEC = importlib.util.spec_from_file_location("igloo_boot_terminal_test", SOURCE)
BOOT = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = BOOT
SPEC.loader.exec_module(BOOT)

# /etc/default/grub of the Fedora 44 installation of the r31 run, as its evidence shows it.
FEDORA_DEFAULT = ('GRUB_TIMEOUT=5\n'
                  'GRUB_DISTRIBUTOR="$(sed \'s, release .*$,,g\' /etc/system-release)"\n'
                  'GRUB_DEFAULT=saved\n'
                  'GRUB_DISABLE_SUBMENU=true\n'
                  'GRUB_TERMINAL_OUTPUT="console"\n'
                  'GRUB_CMDLINE_LINUX="rhgb quiet"\n'
                  'GRUB_DISABLE_RECOVERY="true"\n'
                  'GRUB_ENABLE_BLSCFG=false\n'
                  'GRUB_DISABLE_OS_PROBER=false\n')


class RecordingLogger:
    def __init__(self):
        self.errors = []
        self.info_messages = []
        self.warnings = []

    def error(self, message, *args):
        self.errors.append(message % args if args else message)

    def info(self, message, *args):
        self.info_messages.append(message % args if args else message)

    def warning(self, message, *args):
        self.warnings.append(message % args if args else message)

    def exception(self, message, *args):
        self.errors.append(message % args if args else message)


def last_value(text, key):
    """What a shell that sources the file ends up with for one variable."""
    values = [line.split("=", 1)[1].strip('"') for line in text.splitlines() if line.startswith(key + "=")]
    return values[-1] if values else None


class GrubThemeTerminalTest(TestCase):
    def configure(self, root, default_text, serial=False):
        """Runs the menu configuration against a grub2-mkconfig that reads only /etc/default/grub, as Fedora's does."""
        default = root / "default-grub"
        default.write_text(default_text, encoding="utf-8")
        cfg = root / "grub.cfg"
        logger = RecordingLogger()

        def regenerate(_command, **_kwargs):
            text = default.read_text(encoding="utf-8")
            output = "serial console" if serial else last_value(text, "GRUB_TERMINAL_OUTPUT") or "gfxterm"
            theme = last_value(text, "GRUB_THEME")
            cfg.write_text(f"terminal_output {output}\n" + (f"set theme={theme}\n" if theme and not serial else "")
                           + "menuentry 'Fedora Linux' {\n linux /vmlinuz root=UUID=abc\n}\n", encoding="utf-8")

        boot = BOOT.Boot(regenerate, logger, cfg, root / "themes" / "stylish", ["grub2-mkconfig"], root / "hook")
        with ExitStack() as stack:
            stack.enter_context(patch.object(BOOT, "_DROPIN", root / "grub.d" / "99-igloo-menu.cfg"))
            stack.enter_context(patch.object(BOOT, "_GRUB_DEFAULT", default))
            stack.enter_context(patch.object(BOOT, "_install_theme", return_value=True))
            stack.enter_context(patch.object(BOOT, "_patch_linux_submenu", return_value=True))
            for name in ("_patch_os_prober_labels", "_apply_grub_fixups", "put_self_first_in_boot_order",
                         "install_boot_order_unit"):
                stack.enter_context(patch.object(BOOT, name))
            stack.enter_context(patch.object(BOOT.shutil, "which", return_value=None))
            BOOT.configure_boot_menu({}, boot)
        return default.read_text(encoding="utf-8"), cfg.read_text(encoding="utf-8"), logger

    def test_stock_fedora_gets_the_theme_through_its_own_settings_file(self):
        with TemporaryDirectory() as temporary:
            default, cfg, logger = self.configure(Path(temporary), FEDORA_DEFAULT)

        # Fedora's grub2-mkconfig reads no drop-in folder, so the block goes at the end of the file it does read.
        self.assertTrue(default.startswith(FEDORA_DEFAULT))
        self.assertEqual(last_value(default, "GRUB_TERMINAL_OUTPUT"), "gfxterm")
        self.assertTrue(last_value(default, "GRUB_THEME").endswith("/stylish/theme.txt"))
        self.assertEqual(last_value(default, "GRUB_DEFAULT"), "0")
        self.assertEqual(last_value(default, "GRUB_TIMEOUT"), "10")
        # Fedora ships true, which skips the branch where the patched 10_linux prints its one entry.
        self.assertEqual(last_value(default, "GRUB_DISABLE_SUBMENU"), "false")
        self.assertIn("stylish/theme.txt", cfg)
        self.assertIn("terminal_output gfxterm", cfg)
        self.assertTrue(any("verified: the iGloo theme is referenced" in item for item in logger.info_messages))
        self.assertEqual(logger.errors, [])

    def test_a_second_pass_does_not_append_the_block_again(self):
        with TemporaryDirectory() as temporary:
            first, _, _ = self.configure(Path(temporary), FEDORA_DEFAULT)
            second, _, _ = self.configure(Path(temporary), first)
        self.assertEqual(first, second)

    def test_a_serial_console_keeps_its_terminal_and_is_no_error(self):
        with TemporaryDirectory() as temporary:
            default, cfg, logger = self.configure(Path(temporary), FEDORA_DEFAULT, serial=True)

        self.assertEqual(default, FEDORA_DEFAULT)
        self.assertIn("terminal_output serial console", cfg)
        self.assertTrue(any("serial console" in item for item in logger.info_messages))
        self.assertEqual(logger.errors, [])

    def test_only_a_serial_line_counts_as_a_terminal_to_keep(self):
        self.assertTrue(BOOT._uses_serial_terminal("terminal_output serial console\n"))
        self.assertTrue(BOOT._uses_serial_terminal("terminal_output serial\n"))
        self.assertFalse(BOOT._uses_serial_terminal("terminal_output console\n"))
        self.assertFalse(BOOT._uses_serial_terminal("terminal_output gfxterm\n"))
        self.assertFalse(BOOT._uses_serial_terminal("terminal_output gfxterm serial\n"))

    def test_the_drop_in_names_the_submenu_setting_the_patched_script_needs(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            boot = BOOT.Boot(lambda *_a, **_k: None, RecordingLogger(), root / "grub.cfg", root / "themes", [], root / "hook")
            with patch.object(BOOT, "_DROPIN", root / "99-igloo-menu.cfg"):
                BOOT._write_dropin(boot, "4k", themed=True, single_entry=True, collapse=True)
                patched = (root / "99-igloo-menu.cfg").read_text(encoding="utf-8")
                BOOT._write_dropin(boot, "4k", themed=True, single_entry=False, collapse=True)
                unpatched = (root / "99-igloo-menu.cfg").read_text(encoding="utf-8")
                BOOT._write_dropin(boot, "4k", themed=True, single_entry=False, collapse=False)
                kept = (root / "99-igloo-menu.cfg").read_text(encoding="utf-8")
        self.assertEqual(last_value(patched, "GRUB_DISABLE_SUBMENU"), "false")
        self.assertEqual(last_value(unpatched, "GRUB_DISABLE_SUBMENU"), "y")
        self.assertIsNone(last_value(kept, "GRUB_DISABLE_SUBMENU"))


if __name__ == "__main__":
    main()
