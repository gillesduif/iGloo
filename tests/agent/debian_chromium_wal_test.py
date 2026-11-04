"""A selected Chromium SQLite WAL must survive the Debian-family agent copy."""

import importlib.util
import os
import sqlite3
import stat
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, main, mock


AGENT_PATH = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/agent.py"
SPEC = importlib.util.spec_from_file_location("debian_chromium_wal_agent", AGENT_PATH)
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)


class DebianChromiumWalTest(TestCase):
    def test_existing_public_browser_file_is_replaced_after_private_copy(self):
        with TemporaryDirectory(prefix="igloo-debian-browser-") as temporary:
            root = Path(temporary)
            source = root / "windows/Default"
            source.mkdir(parents=True)
            (source / "Bookmarks").write_bytes(b"new selected bookmarks")
            home = root / "home/lab"
            target = home / ".config/google-chrome/Default/Bookmarks"
            target.parent.mkdir(parents=True)
            target.write_bytes(b"old public bookmarks")
            target.chmod(0o777)
            observations = []
            real_copy = AGENT.shutil.copyfileobj

            def observe_copy(reader, writer, *args, **kwargs):
                observations.append((target.read_bytes(),
                                     stat.S_IMODE(os.fstat(writer.fileno()).st_mode)))
                return real_copy(reader, writer, *args, **kwargs)

            with mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                    mock.patch.object(AGENT.shutil, "copyfileobj", side_effect=observe_copy):
                AGENT._place_chromium_files("Google Chrome", source, home)
            self.assertEqual(target.read_bytes(), b"new selected bookmarks")
            if os.name == "posix":
                self.assertEqual(observations, [(b"old public bookmarks", 0o600)])
                self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o600)

    def test_committed_cookie_wal_survives_profile_copy_privately(self):
        with TemporaryDirectory(prefix="igloo-debian-cookie-wal-") as temporary:
            root = Path(temporary)
            source = root / "windows/Default"
            cookie_db = source / "Network/Cookies"
            cookie_db.parent.mkdir(parents=True)
            connection = sqlite3.connect(cookie_db)
            try:
                connection.execute("PRAGMA journal_mode=WAL")
                connection.execute("PRAGMA wal_autocheckpoint=0")
                connection.execute("CREATE TABLE cookies (name TEXT)")
                connection.execute("INSERT INTO cookies VALUES ('checkpointed')")
                connection.commit()
                connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
                connection.execute("INSERT INTO cookies VALUES ('still-in-wal')")
                connection.commit()
                wal = Path(str(cookie_db) + "-wal")
                self.assertGreater(wal.stat().st_size, 0)

                home = root / "home/lab"
                home.mkdir(parents=True)
                config = home / ".config"
                with mock.patch.object(AGENT, "_chromium_config_homes",
                                       return_value=[config]):
                    AGENT._place_chromium_files("Google Chrome", source, home)
                installed = config / "google-chrome/Default/Network/Cookies"
                with sqlite3.connect(installed) as readback:
                    names = [row[0] for row in readback.execute(
                        "SELECT name FROM cookies ORDER BY name")]
                self.assertEqual(names, ["checkpointed", "still-in-wal"])
                self.assertTrue((installed.parent / "Cookies-wal").is_file())
                if os.name == "posix":
                    self.assertEqual(0o600, stat.S_IMODE(
                        (installed.parent / "Cookies-wal").stat().st_mode))
            finally:
                connection.close()

    def test_orphan_cookie_wal_does_not_copy(self):
        with TemporaryDirectory(prefix="igloo-debian-cookie-wal-") as temporary:
            root = Path(temporary)
            source = root / "windows/Default"
            network = source / "Network"
            network.mkdir(parents=True)
            (network / "Cookies-wal").write_bytes(b"orphaned WAL")
            home = root / "home/lab"
            home.mkdir(parents=True)
            config = home / ".config"
            with mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[config]):
                with self.assertRaisesRegex(RuntimeError, "WAL has no database"):
                    AGENT._place_chromium_files("Google Chrome", source, home)
            self.assertFalse((config / "google-chrome/Default/Network/Cookies-wal").exists())


if __name__ == "__main__":
    main()
