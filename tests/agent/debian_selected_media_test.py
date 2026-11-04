"""Selected Debian-family media must reach installed settings or retain retry state."""

import importlib.util
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest import mock


SOURCE = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/agent.py"
SPEC = importlib.util.spec_from_file_location("debian_selected_media_agent", SOURCE)
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)


def sandbox_path(root: Path):
    def resolve(path):
        value = str(path)
        for prefix, destination in (
            ("/opt/igloo", "opt/igloo"),
            ("/home", "home"),
            ("/etc/dconf", "etc/dconf"),
            ("/var/lib/AccountsService", "accounts"),
        ):
            if value == prefix or value.startswith(prefix + "/"):
                return root / destination / value[len(prefix):].lstrip("/")
        return Path(value)
    return resolve


class DebianSelectedMediaTests(unittest.TestCase):
    def test_missing_selected_asset_fails_for_first_boot_retry(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "home/lab").mkdir(parents=True)
            with mock.patch.object(AGENT, "Path", side_effect=sandbox_path(root)):
                with self.assertRaisesRegex(RuntimeError, "Selected wallpaper is missing"):
                    AGENT.migrate_wallpaper({
                        "wallpaper": {"fileName": "igloo-wallpaper.PNG"},
                        "user": {"preferredLinuxUsername": "lab"},
                    })
                with self.assertRaisesRegex(RuntimeError, "Selected account picture is missing"):
                    AGENT.migrate_account_picture({
                        "accountPicture": {"fileName": "igloo-avatar.jpg"},
                        "user": {"preferredLinuxUsername": "lab"},
                    })

    def test_selected_media_is_copied_and_applied_with_exact_case(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            staged = root / "opt/igloo"
            staged.mkdir(parents=True)
            (root / "home/lab").mkdir(parents=True)
            (staged / "igloo-wallpaper.PNG").write_bytes(b"wallpaper marker")
            (staged / "igloo-avatar.jpg").write_bytes(b"avatar marker")
            with mock.patch.object(AGENT, "Path", side_effect=sandbox_path(root)), \
                 mock.patch.object(AGENT, "run_cmd", return_value=mock.Mock(returncode=0)):
                AGENT.migrate_wallpaper({
                    "wallpaper": {"fileName": "igloo-wallpaper.PNG"},
                    "user": {"preferredLinuxUsername": "lab"},
                })
                AGENT.migrate_account_picture({
                    "accountPicture": {"fileName": "igloo-avatar.jpg"},
                    "user": {"preferredLinuxUsername": "lab"},
                })
            self.assertEqual(b"wallpaper marker", (root / "home/lab/Pictures/wallpaper.PNG").read_bytes())
            self.assertIn("picture-uri=", (root / "etc/dconf/db/local.d/00-igloo-wallpaper").read_text())
            self.assertEqual(b"avatar marker", (root / "accounts/icons/lab").read_bytes())
            self.assertEqual(b"avatar marker", (root / "home/lab/.face").read_bytes())

    def test_selected_media_write_or_dconf_failure_propagates(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            staged = root / "opt/igloo"
            staged.mkdir(parents=True)
            (root / "home/lab").mkdir(parents=True)
            (staged / "igloo-wallpaper.jpg").write_bytes(b"wallpaper marker")
            (staged / "igloo-avatar.jpg").write_bytes(b"avatar marker")
            def fail_chown(argv, **_kwargs):
                if argv[0] == "chown":
                    raise RuntimeError("ownership transfer failed")
                return mock.Mock(returncode=0)
            with mock.patch.object(AGENT, "Path", side_effect=sandbox_path(root)), \
                 mock.patch.object(AGENT, "run_cmd", side_effect=fail_chown):
                with self.assertRaisesRegex(RuntimeError, "ownership transfer failed"):
                    AGENT.migrate_wallpaper({
                        "wallpaper": {"fileName": "igloo-wallpaper.jpg"},
                        "user": {"preferredLinuxUsername": "lab"},
                    })
                with self.assertRaisesRegex(RuntimeError, "ownership transfer failed"):
                    AGENT.migrate_account_picture({
                        "accountPicture": {"fileName": "igloo-avatar.jpg"},
                        "user": {"preferredLinuxUsername": "lab"},
                    })
            def fail_dconf(argv, **_kwargs):
                if argv[0] == "dconf":
                    raise RuntimeError("dconf update failed")
                return mock.Mock(returncode=0)
            with mock.patch.object(AGENT, "Path", side_effect=sandbox_path(root)), \
                 mock.patch.object(AGENT, "run_cmd", side_effect=fail_dconf):
                with self.assertRaisesRegex(RuntimeError, "dconf update failed"):
                    AGENT.migrate_wallpaper({
                        "wallpaper": {"fileName": "igloo-wallpaper.jpg"},
                        "user": {"preferredLinuxUsername": "lab"},
                    })


if __name__ == "__main__":
    unittest.main()
