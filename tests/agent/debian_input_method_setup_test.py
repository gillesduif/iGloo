"""Debian's Japanese input methods add themselves at first login; that is kept away from other languages."""

from contextlib import contextmanager
import importlib.util
import os
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


SOURCE = (Path(__file__).resolve().parents[2]
          / 'distros' / '_debian-family' / 'agent' / 'agent.py')
SPEC = importlib.util.spec_from_file_location('igloo_debian_input_method_setup', SOURCE)
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)

# The entry Debian 13 ships in ibus-mozc 2.29.5160.102+dfsg-1.4; ibus-anthy 1.5.17-1 differs in the name only.
ENTRY = ('[Desktop Entry]\n'
         'Name={package} GNOME Initial Setup\n'
         'Type=Application\n'
         'Exec=/usr/share/{package}/{package}-gnome-initial-setup.sh\n'
         'OnlyShowIn=GNOME;\n'
         'NoDisplay=true\n'
         'AutostartCondition=unless-exists {package}-gnome-initial-setup-done\n')


def manifest(locale='nl_BE.UTF-8'):
    return {'user': {'preferredLinuxUsername': 'ada', 'locale': locale}}


@contextmanager
def first_boot(root, packages=('ibus-anthy', 'ibus-mozc'), entry=ENTRY):
    for relative in ('etc/xdg/autostart', 'etc/default', 'home/ada'):
        (root / relative).mkdir(parents=True)
    for package in packages:
        (root / 'etc/xdg/autostart' / f'{package}-gnome-initial-setup.desktop').write_text(
            entry.format(package=package), encoding='utf-8')
    commands = []

    def lab_path(value):
        text = str(value)
        for original in ('/etc/xdg', '/etc/default', '/etc/locale.conf', '/home'):
            if text == original or text.startswith(original + '/'):
                return root / text.lstrip('/')
        raise AssertionError('Unexpected real filesystem path: ' + text)

    def run_cmd(cmd, **_):
        commands.append(cmd)
        return subprocess.CompletedProcess(cmd, 0)

    old_umask = os.umask(0o077)
    try:
        with (patch.object(agent, 'Path', side_effect=lab_path),
              patch.object(agent, 'run_cmd', side_effect=run_cmd)):
            yield commands
    finally:
        os.umask(old_umask)


class ForeignInputMethodSetupTest(unittest.TestCase):
    def test_both_japanese_setups_are_marked_done_for_a_dutch_user(self):
        with TemporaryDirectory() as temp:
            root = Path(temp)
            with first_boot(root) as commands:
                agent._skip_foreign_input_method_setup(manifest())
            config = root / 'home/ada/.config'
            self.assertTrue((config / 'ibus-anthy-gnome-initial-setup-done').is_file())
            self.assertTrue((config / 'ibus-mozc-gnome-initial-setup-done').is_file())
            # Root made the files; the session needs them to be the user's.
            self.assertEqual(
                [['chown', '-h', 'ada:ada', str(config), str(config / f'{package}-gnome-initial-setup-done')]
                 for package in ('ibus-anthy', 'ibus-mozc')], commands)

    def test_a_japanese_user_keeps_the_setup(self):
        for locale in ('ja_JP.UTF-8', 'ja'):
            with self.subTest(locale=locale), TemporaryDirectory() as temp:
                root = Path(temp)
                with first_boot(root) as commands:
                    agent._skip_foreign_input_method_setup(manifest(locale))
                self.assertFalse((root / 'home/ada/.config').exists())
                self.assertEqual([], commands)

    def test_the_system_language_decides_when_the_manifest_has_none(self):
        with TemporaryDirectory() as temp:
            root = Path(temp)
            with first_boot(root):
                (root / 'etc/default/locale').write_text('LANG="ja_JP.UTF-8"\n', encoding='utf-8')
                agent._skip_foreign_input_method_setup(manifest(locale=None))
            self.assertFalse((root / 'home/ada/.config').exists())

    def test_nothing_is_written_when_the_packages_are_not_installed(self):
        with TemporaryDirectory() as temp:
            root = Path(temp)
            with first_boot(root, packages=()) as commands:
                agent._skip_foreign_input_method_setup(manifest())
            self.assertFalse((root / 'home/ada/.config').exists())
            self.assertEqual([], commands)

    def test_an_entry_without_a_usable_condition_is_left_alone(self):
        for condition in ('', 'AutostartCondition=if-exists done\n',
                          'AutostartCondition=unless-exists ../../.bashrc\n',
                          'AutostartCondition=unless-exists ..\n',
                          'AutostartCondition=unless-exists\n'):
            with self.subTest(condition=condition), TemporaryDirectory() as temp:
                root = Path(temp)
                with (first_boot(root, packages=('ibus-mozc',), entry='[Desktop Entry]\n' + condition) as commands,
                      self.assertLogs(agent.logger, level='WARNING')):
                    agent._skip_foreign_input_method_setup(manifest())
                self.assertFalse((root / 'home/ada/.config').exists())
                self.assertEqual([], commands)

    def test_a_missing_home_changes_nothing(self):
        with TemporaryDirectory() as temp:
            root = Path(temp)
            with first_boot(root) as commands:
                agent._skip_foreign_input_method_setup(
                    {'user': {'preferredLinuxUsername': 'nobody', 'locale': 'nl_BE.UTF-8'}})
            self.assertEqual([], commands)
            self.assertFalse((root / 'home/nobody').exists())


if __name__ == '__main__':
    unittest.main()
