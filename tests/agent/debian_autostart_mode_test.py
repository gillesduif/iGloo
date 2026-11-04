"""XDG first-login hooks must be readable after the root pass's umask 077."""

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
SPEC = importlib.util.spec_from_file_location('igloo_debian_autostart_mode', SOURCE)
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)


@contextmanager
def private_first_boot(root):
    (root / 'etc').mkdir()
    (root / 'opt/igloo').mkdir(parents=True)

    def lab_path(value):
        text = str(value)
        for original, relative in (('/etc/xdg', 'etc/xdg'), ('/opt/igloo', 'opt/igloo')):
            if text == original or text.startswith(original + '/'):
                return root / relative / text[len(original):].lstrip('/')
        raise AssertionError('Unexpected real filesystem path: ' + text)

    old_umask = os.umask(0o077)
    try:
        with (patch.object(agent, 'Path', side_effect=lab_path),
              patch.object(agent, 'apt_install') as apt_install,
              patch.object(agent.shutil, 'which', return_value='/usr/bin/notify-send')):
            yield root
            apt_install.assert_not_called()
    finally:
        os.umask(old_umask)


@unittest.skipIf(os.name == 'nt', 'POSIX XDG mode test')
class DebianAutostartModeTests(unittest.TestCase):
    def test_display_and_welcome_hooks_are_public_but_private_state_is_untouched(self):
        with TemporaryDirectory(prefix='igloo-autostart-') as temporary:
            root = Path(temporary)
            private_state = root / 'var/lib/igloo'
            private_state.mkdir(parents=True)
            private_state.chmod(0o700)
            with private_first_boot(root):
                agent._stage_display_login_hook([])
                agent.install_welcome_app({})
            modes = {
                'xdg': 0o755,
                'xdg/autostart': 0o755,
                'xdg/autostart/igloo-display-layout.desktop': 0o644,
                'xdg/autostart/igloo-welcome.desktop': 0o644,
            }
            for suffix, expected in modes.items():
                with self.subTest(path=suffix):
                    path = root / 'etc' / suffix
                    self.assertEqual(expected, stat.S_IMODE(path.stat().st_mode))
            welcome = (root / 'etc/xdg/autostart/igloo-welcome.desktop').read_text()
            self.assertIn('OnlyShowIn=GNOME;KDE;X-Cinnamon;Cinnamon;XFCE;', welcome)
            for suffix, expected in {
                    'display-layout.json': 0o644,
                    'display-apply.sh': 0o755,
                    'igloo-welcome.sh': 0o755,
            }.items():
                with self.subTest(path=suffix):
                    self.assertEqual(expected, stat.S_IMODE(
                        (root / 'opt/igloo' / suffix).stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE(private_state.stat().st_mode))

    def test_redirected_autostart_directory_is_refused(self):
        with TemporaryDirectory(prefix='igloo-autostart-') as temporary:
            root = Path(temporary)
            outside = root / 'outside'
            outside.mkdir()
            outside.chmod(0o700)
            with private_first_boot(root):
                (root / 'etc/xdg').symlink_to(outside, target_is_directory=True)
                with self.assertRaisesRegex(RuntimeError, 'redirected'):
                    agent.install_welcome_app({})
            self.assertEqual(0o700, stat.S_IMODE(outside.stat().st_mode))
            self.assertFalse((outside / 'autostart').exists())

    def test_missing_notifier_does_not_publish_a_false_success_hook(self):
        with TemporaryDirectory(prefix='igloo-autostart-') as temporary:
            root = Path(temporary)
            with private_first_boot(root):
                with patch.object(agent.shutil, 'which', return_value=None):
                    with patch.object(agent, 'apt_install') as apt_install:
                        with self.assertRaisesRegex(RuntimeError, 'requires notify-send'):
                            agent.install_welcome_app({})
                        apt_install.assert_called_once_with(['libnotify-bin'], timeout=300)
            self.assertFalse((root / 'opt/igloo/igloo-welcome.sh').exists())
            self.assertFalse((root / 'etc/xdg/autostart/igloo-welcome.desktop').exists())

    def test_missing_notifier_is_installed_before_publishing_hook(self):
        with TemporaryDirectory(prefix='igloo-autostart-') as temporary:
            root = Path(temporary)
            with private_first_boot(root):
                with (patch.object(agent.shutil, 'which', side_effect=[None, '/usr/bin/notify-send']),
                      patch.object(agent, 'apt_install') as apt_install):
                    agent.install_welcome_app({})
                    apt_install.assert_called_once_with(['libnotify-bin'], timeout=300)
            self.assertTrue((root / 'opt/igloo/igloo-welcome.sh').is_file())

    def test_welcome_marker_is_written_only_after_notification_succeeds(self):
        with TemporaryDirectory(prefix='igloo-autostart-') as temporary:
            root = Path(temporary)
            with private_first_boot(root):
                agent.install_welcome_app({})
            fake_bin = root / 'bin'
            fake_bin.mkdir()
            notify = fake_bin / 'notify-send'
            systemctl = fake_bin / 'systemctl'
            sleep = fake_bin / 'sleep'
            home = root / 'home'
            home.mkdir()
            marker = home / '.igloo-welcome-done'
            notification_log = root / 'notify-called'
            first_attempt = root / 'notify-first-attempt'
            script = root / 'opt/igloo/igloo-welcome.sh'
            environment = dict(os.environ, HOME=str(home),
                               PATH=str(fake_bin) + os.pathsep + os.environ['PATH'],
                               NOTIFY_LOG=str(notification_log),
                               NOTIFY_FIRST=str(first_attempt))

            sleep.write_text('#!/bin/sh\nexit 0\n')
            sleep.chmod(0o755)

            systemctl.write_text('#!/bin/sh\nexit 1\n')
            systemctl.chmod(0o755)
            notify.write_text('#!/bin/sh\nprintf called >> "$NOTIFY_LOG"\nexit 0\n')
            notify.chmod(0o755)
            incomplete = subprocess.run(['bash', str(script)], env=environment,
                                        capture_output=True, text=True, check=False)
            self.assertEqual(0, incomplete.returncode)
            self.assertFalse(notification_log.exists())
            self.assertFalse(marker.exists())

            systemctl.write_text('#!/bin/sh\nexit 0\n')
            notify.write_text('#!/bin/sh\nexit 1\n')
            failed = subprocess.run(['bash', str(script)], env=environment,
                                    capture_output=True, text=True, check=False)
            self.assertNotEqual(0, failed.returncode)
            self.assertFalse(marker.exists())

            notify.write_text(
                '#!/bin/sh\n'
                'if [ ! -f "$NOTIFY_FIRST" ]; then\n'
                '    touch "$NOTIFY_FIRST"\n'
                '    exit 1\n'
                'fi\n'
                'printf called >> "$NOTIFY_LOG"\n')
            succeeded = subprocess.run(['bash', str(script)], env=environment,
                                       capture_output=True, text=True, check=False)
            self.assertEqual(0, succeeded.returncode)
            self.assertTrue(marker.is_file())
            self.assertTrue(first_attempt.is_file())
            self.assertEqual('called', notification_log.read_text())


if __name__ == '__main__':
    unittest.main()
