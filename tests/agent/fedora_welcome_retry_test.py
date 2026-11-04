"""Fedora's welcome hook must reflect durable first-boot success."""

import importlib.util
import os
from pathlib import Path
import stat
import subprocess
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


REPOSITORY = Path(__file__).resolve().parents[2]
SOURCE = REPOSITORY / "distros/fedora-kde/agent/agent.py"
KICKSTART = REPOSITORY / "distros/fedora-kde/kickstart/ks.cfg.template"
SPEC = importlib.util.spec_from_file_location("igloo_fedora_welcome_retry", SOURCE)
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)


@unittest.skipIf(os.name == "nt", "POSIX XDG hook test")
class FedoraWelcomeRetryTests(unittest.TestCase):
    def test_welcome_waits_for_successful_service_and_notification(self):
        with TemporaryDirectory(prefix="igloo-fedora-welcome-") as temporary:
            root = Path(temporary)
            (root / "opt/igloo").mkdir(parents=True)

            def lab_path(value):
                name = str(value)
                for prefix in ("/etc/xdg", "/opt/igloo"):
                    if name == prefix or name.startswith(prefix + "/"):
                        return root / name.lstrip("/")
                raise AssertionError(f"unexpected path: {name}")

            old_umask = os.umask(0o077)
            try:
                with patch.object(agent, "Path", side_effect=lab_path):
                    agent.install_welcome_app({})
            finally:
                os.umask(old_umask)

            script = root / "opt/igloo/igloo-welcome.sh"
            desktop = root / "etc/xdg/autostart/igloo-welcome.desktop"
            self.assertEqual(0o755, stat.S_IMODE(script.stat().st_mode))
            self.assertEqual(0o644, stat.S_IMODE(desktop.stat().st_mode))

            fake_bin = root / "bin"
            fake_bin.mkdir()
            systemctl = fake_bin / "systemctl"
            notify = fake_bin / "notify-send"
            home = root / "home"
            home.mkdir()
            marker = home / ".igloo-welcome-done"
            notification_log = root / "notifications"
            environment = dict(os.environ, HOME=str(home),
                               PATH=str(fake_bin) + os.pathsep + os.environ["PATH"],
                               NOTIFY_LOG=str(notification_log))

            def run_hook():
                return subprocess.run(["bash", str(script)], env=environment,
                                      capture_output=True, text=True, check=False)

            systemctl.write_text("#!/bin/sh\nexit 1\n", encoding="utf-8")
            systemctl.chmod(0o755)
            notify.write_text('#!/bin/sh\nprintf called >> "$NOTIFY_LOG"\nexit 0\n',
                              encoding="utf-8")
            notify.chmod(0o755)
            self.assertEqual(0, run_hook().returncode)
            self.assertFalse(notification_log.exists())
            self.assertFalse(marker.exists())

            systemctl.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            notify.write_text("#!/bin/sh\nexit 17\n", encoding="utf-8")
            self.assertEqual(17, run_hook().returncode)
            self.assertFalse(marker.exists())

            notify.write_text('#!/bin/sh\nprintf called >> "$NOTIFY_LOG"\nexit 0\n',
                              encoding="utf-8")
            self.assertEqual(0, run_hook().returncode)
            self.assertEqual("called", notification_log.read_text(encoding="utf-8"))
            self.assertTrue(marker.is_file())
            self.assertEqual(0, run_hook().returncode)
            self.assertEqual("called", notification_log.read_text(encoding="utf-8"))

    def test_unit_remains_active_only_after_launcher_success(self):
        unit = KICKSTART.read_text(encoding="utf-8")
        self.assertIn("Before=display-manager.service", unit)
        self.assertIn("Type=oneshot", unit)
        self.assertIn("ExecStart=/opt/igloo/first-boot.sh", unit)
        self.assertIn("RemainAfterExit=yes", unit)
        self.assertIn("ConditionPathExists=/var/lib/igloo/manifest.json", unit)
        self.assertNotIn("ConditionPathExists=!/var/lib/igloo/.done", unit)


if __name__ == "__main__":
    unittest.main()
