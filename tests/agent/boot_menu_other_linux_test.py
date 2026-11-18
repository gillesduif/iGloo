"""The boot menu check counts this system's own submenu, not the one os-prober lists for another Linux."""

import importlib.util
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
from types import SimpleNamespace
import unittest
from unittest import mock


SOURCE = Path(__file__).resolve().parents[2] / "distros/_shared/agent/igloo_boot.py"
SPEC = importlib.util.spec_from_file_location("boot_menu_other_linux_fixture", SOURCE)
BOOT = importlib.util.module_from_spec(SPEC)
# The module declares a dataclass, which looks its own module up by name.
sys.modules[SPEC.name] = BOOT
SPEC.loader.exec_module(BOOT)

# The top-level entries of the r25 Mint run beside Debian, as the agent logged them.
OWN = "menuentry 'Ubuntu' --class ubuntu --class os $menuentry_id_option 'gnulinux-simple-93bf2081-92c5-4492-9a4d-43d036999321' {\n}\n"
WINDOWS = "menuentry 'Windows 11 ' --class windows --class os $menuentry_id_option 'osprober-efi-12AF-8C61' {\n}\n"
OTHER = ("menuentry 'Debian GNU/Linux 13 (trixie) ' --class debian --class os $menuentry_id_option "
         "'osprober-gnulinux-simple-4f973ac5-bac7-4ff8-a428-6e060f61050b' {\n}\n"
         "submenu 'Advanced options for Debian GNU/Linux 13 (trixie) ' $menuentry_id_option "
         "'osprober-gnulinux-advanced-4f973ac5-bac7-4ff8-a428-6e060f61050b' {\n}\n")
OWN_SUBMENU = ("submenu 'Advanced options for Ubuntu' $menuentry_id_option "
               "'gnulinux-advanced-93bf2081-92c5-4492-9a4d-43d036999321' {\n}\n")
HEAD = "set theme=/boot/grub/themes/stylish/theme.txt\nsearch --no-floppy --fs-uuid --set=root --hint='hd0,gpt9' 93bf2081\n"


class BootMenuOtherLinuxTests(unittest.TestCase):
    def verify(self, cfg):
        with TemporaryDirectory() as temporary:
            path = Path(temporary) / "grub.cfg"
            path.write_text(cfg, encoding="utf-8")
            boot = SimpleNamespace(grub_cfg=path, logger=mock.Mock())
            BOOT._verify(boot, True)
            return boot.logger

    def test_the_submenu_of_another_linux_is_not_a_failure(self):
        log = self.verify(HEAD + OWN + WINDOWS + OTHER)
        log.error.assert_not_called()
        said = [call.args[0] for call in log.info.call_args_list]
        self.assertIn("verified: no Advanced options submenu of this system in grub.cfg", said)
        self.assertIn("another Linux on this machine is listed with its own Advanced options submenu", said)

    def test_the_own_submenu_is_still_a_failure(self):
        for cfg in (HEAD + OWN + OWN_SUBMENU + WINDOWS, HEAD + OWN + OWN_SUBMENU + WINDOWS + OTHER):
            log = self.verify(cfg)
            log.error.assert_called_once()
            self.assertIn("Advanced options submenu is still", log.error.call_args.args[0])

    def test_without_another_linux_nothing_is_said_about_one(self):
        log = self.verify(HEAD + OWN + WINDOWS)
        log.error.assert_not_called()
        self.assertNotIn("another Linux on this machine is listed with its own Advanced options submenu",
                         [call.args[0] for call in log.info.call_args_list])


if __name__ == "__main__":
    unittest.main()
