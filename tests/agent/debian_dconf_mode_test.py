"""What a desktop session reads must be readable after the root pass's umask 077."""

from contextlib import contextmanager
import importlib.util
import os
from pathlib import Path
import stat
import subprocess
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


SOURCE = (Path(__file__).resolve().parents[2]
          / 'distros' / '_debian-family' / 'agent' / 'agent.py')
SPEC = importlib.util.spec_from_file_location('igloo_debian_dconf_mode', SOURCE)
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)

MANIFEST = {'user': {'preferredLinuxUsername': 'ada', 'keymap': 'nl'},
            'wallpaper': {'fileName': 'igloo-wallpaper.jpg'}}


def mode(path):
    return stat.S_IMODE(path.stat().st_mode)


@contextmanager
def private_first_boot(root, dconf_installed=True):
    for relative in ('etc/default', 'opt/igloo', 'home/ada'):
        (root / relative).mkdir(parents=True)
    (root / 'opt/igloo/igloo-wallpaper.jpg').write_bytes(b'image')
    commands = []

    def lab_path(value):
        text = str(value)
        for original in ('/etc/dconf', '/etc/default', '/opt/igloo', '/home'):
            if text == original or text.startswith(original + '/'):
                return root / text.lstrip('/')
        raise AssertionError('Unexpected real filesystem path: ' + text)

    def run_cmd(cmd, **_):
        commands.append(cmd)
        if cmd == ['dconf', 'update']:
            if not dconf_installed:
                return subprocess.CompletedProcess(cmd, 127)
            # The real tool writes the compiled database under the caller's umask.
            (root / 'etc/dconf/db/local').write_bytes(b'gvdb')
        return subprocess.CompletedProcess(cmd, 0)

    old_umask = os.umask(0o077)
    try:
        with (patch.object(agent, 'Path', side_effect=lab_path),
              patch.object(agent, 'run_cmd', side_effect=run_cmd)):
            yield commands
    finally:
        os.umask(old_umask)


@unittest.skipIf(os.name == 'nt', 'POSIX mode test')
class DebianDconfModeTests(unittest.TestCase):
    def test_the_wallpaper_default_is_readable_by_the_session(self):
        with TemporaryDirectory(prefix='igloo-dconf-') as temporary:
            root = Path(temporary)
            with private_first_boot(root) as commands:
                agent.migrate_wallpaper(MANIFEST)
            self.assertIn(['dconf', 'update'], commands)
            for suffix, expected in {
                    '': 0o755,
                    'profile': 0o755,
                    'profile/user': 0o644,
                    'db': 0o755,
                    'db/local': 0o644,
                    'db/local.d': 0o755,
                    'db/local.d/00-igloo-wallpaper': 0o644,
            }.items():
                with self.subTest(path=suffix):
                    self.assertEqual(expected, mode(root / 'etc/dconf' / suffix))
            self.assertEqual('user-db:user\nsystem-db:local\n',
                             (root / 'etc/dconf/profile/user').read_text())
            self.assertIn("picture-uri='file://" + str(root / 'home/ada/Pictures/wallpaper.jpg'),
                          (root / 'etc/dconf/db/local.d/00-igloo-wallpaper').read_text())

    def test_the_keyboard_default_is_readable_by_the_session(self):
        with TemporaryDirectory(prefix='igloo-dconf-') as temporary:
            root = Path(temporary)
            with private_first_boot(root):
                agent.set_keyboard(MANIFEST)
            for suffix, expected in {
                    '': 0o755,
                    'profile/user': 0o644,
                    'db/local': 0o644,
                    'db/local.d/00-igloo-keyboard': 0o644,
            }.items():
                with self.subTest(path=suffix):
                    self.assertEqual(expected, mode(root / 'etc/dconf' / suffix))

    def test_a_missing_dconf_tool_still_leaves_the_profile_readable(self):
        with TemporaryDirectory(prefix='igloo-dconf-') as temporary:
            root = Path(temporary)
            with private_first_boot(root, dconf_installed=False):
                agent.set_keyboard(MANIFEST)
            self.assertEqual(0o644, mode(root / 'etc/dconf/profile/user'))
            self.assertFalse((root / 'etc/dconf/db/local').exists())

    def test_modes_the_distribution_chose_are_only_widened(self):
        with TemporaryDirectory(prefix='igloo-dconf-') as temporary:
            root = Path(temporary)
            shipped = root / 'etc/dconf/db'
            shipped.mkdir(parents=True)
            shipped.chmod(0o2775)
            outside = root / 'outside'
            outside.mkdir()
            outside.chmod(0o700)
            with private_first_boot(root):
                (root / 'etc/dconf/profile').symlink_to(outside, target_is_directory=True)
                agent.migrate_wallpaper(MANIFEST)
            self.assertEqual(0o2775, mode(shipped))
            self.assertEqual(0o700, mode(outside))

    def test_the_firefox_override_is_readable_by_the_menu(self):
        with TemporaryDirectory(prefix='igloo-dconf-') as temporary:
            root = Path(temporary)
            system = root / 'usr/share/applications'
            system.mkdir(parents=True)
            (system / 'firefox-esr.desktop').write_text('[Desktop Entry]\nName=Firefox ESR\n')
            staff = root / 'usr/local/share'
            staff.mkdir(parents=True)
            staff.chmod(0o2775)
            target = staff / 'applications'
            old_umask = os.umask(0o077)
            try:
                agent._hide_packaged_firefox(system, target)
            finally:
                os.umask(old_umask)
            self.assertEqual(0o755, mode(target) & 0o777)
            self.assertEqual(0o644, mode(target / 'firefox-esr.desktop'))
            self.assertIn('NoDisplay=true', (target / 'firefox-esr.desktop').read_text())
            self.assertEqual(0o2775, mode(staff))


if __name__ == '__main__':
    unittest.main()
