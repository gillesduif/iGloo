"""Fedora's serial installer console must remain available after GRUB theming."""

import importlib.util
import sys
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, main


SOURCE = Path(__file__).resolve().parents[2] / "distros/_shared/agent/igloo_boot.py"
SPEC = importlib.util.spec_from_file_location("igloo_boot_terminal_test", SOURCE)
BOOT = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = BOOT
SPEC.loader.exec_module(BOOT)


class RecordingLogger:
    def __init__(self):
        self.errors = []
        self.info_messages = []

    def error(self, message, *args):
        self.errors.append(message % args if args else message)

    def info(self, message, *args):
        self.info_messages.append(message % args if args else message)

    def warning(self, message, *args):
        pass


class GrubThemeTerminalTest(TestCase):
    def test_serial_console_suppresses_theme_fallback_error(self):
        with TemporaryDirectory() as temporary:
            cfg = Path(temporary) / "grub.cfg"
            # The output directive is from the installed Fedora 44 VM's
            # generated GRUB config after booting its installer on ttyS0.
            cfg.write_text("terminal_output serial console\n"
                           "menuentry 'Fedora Linux' {\n linux /vmlinuz root=UUID=abc\n}\n",
                           encoding="utf-8")
            logger = RecordingLogger()
            boot = BOOT.Boot(lambda *_args, **_kwargs: None, logger, cfg,
                             Path(temporary) / "themes", [], Path(temporary) / "hook")
            self.assertTrue(BOOT._uses_text_terminal(cfg.read_text()))
            BOOT._verify(boot, themed=True)
            self.assertFalse(any("theme installed" in error for error in logger.errors))
            self.assertTrue(any("text/serial" in item for item in logger.info_messages))

    def test_graphical_output_still_requires_theme_reference(self):
        self.assertFalse(BOOT._uses_text_terminal("terminal_output gfxterm\n"))


if __name__ == "__main__":
    main()
