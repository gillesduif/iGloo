"""Fedora must place staged browser files with private, regular file modes."""

import base64
import importlib.util
import os
import sqlite3
import stat
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, main, mock, skipUnless


AGENT_PATH = Path(__file__).resolve().parents[2] / "distros/fedora-kde/agent/agent.py"
KICKSTART_PATH = AGENT_PATH.parent.parent / "kickstart/ks.cfg.template"
SPEC = importlib.util.spec_from_file_location("fedora_chromium_agent", AGENT_PATH)
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)


class FedoraChromiumSessionsTest(TestCase):
    def test_committed_cookie_wal_survives_installer_and_agent_copy(self):
        with TemporaryDirectory(prefix="igloo-fedora-cookie-wal-") as temporary:
            root = Path(temporary)
            source = root / "windows/Default"
            cookie_db = source / "Network/Cookies"
            cookie_db.parent.mkdir(parents=True)
            connection = sqlite3.connect(cookie_db)
            try:
                connection.execute("PRAGMA journal_mode=WAL")
                connection.execute("PRAGMA wal_autocheckpoint=0")
                connection.execute("CREATE TABLE cookies (host_key TEXT, name TEXT, "
                                   "path TEXT, encrypted_value BLOB)")
                connection.execute("INSERT INTO cookies VALUES "
                                   "('example.invalid', 'checkpointed', '/', X'00')")
                connection.commit()
                connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
                connection.execute("INSERT INTO cookies VALUES "
                                   "('example.invalid', 'still-in-wal', '/', X'00')")
                connection.commit()
                self.assertGreater(Path(str(cookie_db) + "-wal").stat().st_size, 0)

                template = KICKSTART_PATH.read_text(encoding="utf-8")
                start = template.index("stage_chromium_plain() {")
                end = template.index("\n}\n", start) + 2
                script = ("set -eu\nlog() { :; }\n" + template[start:end]
                          + '\nstage_chromium_plain "Google Chrome" "$SRC"\n')
                environment = dict(os.environ, SYSIMAGE=str(root / "sysimage"),
                                   SRC=str(source))
                subprocess.run(["bash", "-c", script], env=environment, check=True,
                               capture_output=True, text=True)
                staged = root / "sysimage/var/lib/igloo/chromium/Google Chrome"
                self.assertTrue((staged / "Network/Cookies-wal").is_file())

                home = root / "home/lab"
                home.mkdir(parents=True)
                config = home / ".var/app/com.google.Chrome/config"
                with mock.patch.object(AGENT, "_chromium_config_homes",
                                       return_value=[config]):
                    AGENT._place_chromium_files("Google Chrome", staged, home)
                installed = config / "google-chrome/Default/Network/Cookies"
                with sqlite3.connect(installed) as readback:
                    names = [row[0] for row in readback.execute(
                        "SELECT name FROM cookies ORDER BY name")]
                self.assertEqual(names, ["checkpointed", "still-in-wal"])
                if os.name == "posix":
                    self.assertEqual(0o600, stat.S_IMODE(
                        (installed.parent / "Cookies-wal").stat().st_mode))

                rewritten = AGENT._reencrypt_cookies(installed.parent, [{
                    "host": "example.invalid", "name": "still-in-wal", "path": "/",
                    "value": base64.b64encode(b"synthetic-cookie").decode("ascii"),
                }])
                self.assertEqual(rewritten, 1)
                with sqlite3.connect(installed) as readback:
                    rows = list(readback.execute(
                        "SELECT name, encrypted_value FROM cookies"))
                self.assertEqual(len(rows), 1)
                self.assertEqual(rows[0][0], "still-in-wal")
                self.assertTrue(rows[0][1].startswith(b"v10"))
            finally:
                connection.close()

    def test_staged_session_files_do_not_keep_fat_permissions(self):
        with TemporaryDirectory(prefix="igloo-fedora-browser-") as temporary:
            root = Path(temporary)
            source = root / "stage" / "Brave"
            sessions = source / "Sessions"
            sessions.mkdir(parents=True)
            (sessions / "Session_1").write_bytes(b"saved tabs")
            (sessions / "Tabs_1").write_bytes(b"open tabs")
            # cp -a in Fedora %post can retain permissions projected by the
            # FAT OEMDRV mount. They are not suitable for a user's home.
            for file in sessions.iterdir():
                file.chmod(0o777)
            sessions.chmod(0o777)

            home = root / "home" / "lab"
            home.mkdir(parents=True)
            config = home / ".config"
            with mock.patch.object(AGENT, "_chromium_config_homes", return_value=[config]):
                AGENT._place_chromium_files("Brave", source, home)

            target = config / "BraveSoftware" / "Brave-Browser" / "Default" / "Sessions"
            self.assertEqual((target / "Session_1").read_bytes(), b"saved tabs")
            self.assertEqual((target / "Tabs_1").read_bytes(), b"open tabs")
            if stat.S_IMODE((sessions / "Tabs_1").stat().st_mode) == 0o777:
                self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o700)
                for file in target.iterdir():
                    self.assertEqual(stat.S_IMODE(file.stat().st_mode), 0o600)

    def test_existing_public_file_is_replaced_after_private_copy(self):
        with TemporaryDirectory(prefix="igloo-fedora-browser-") as temporary:
            root = Path(temporary)
            source = root / "stage" / "Brave" / "Sessions"
            source.mkdir(parents=True)
            (source / "Tabs_1").write_bytes(b"new private tabs")

            home = root / "home" / "lab"
            target_dir = (home / ".config" / "BraveSoftware" / "Brave-Browser"
                          / "Default" / "Sessions")
            target_dir.mkdir(parents=True)
            target = target_dir / "Tabs_1"
            target.write_bytes(b"old tabs")
            target.chmod(0o777)

            real_copy = AGENT.shutil.copyfileobj
            observations = []

            def observe_copy(reader, writer):
                observations.append((target.read_bytes(),
                                     stat.S_IMODE(os.fstat(writer.fileno()).st_mode)))
                return real_copy(reader, writer)

            with mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT.shutil, "copyfileobj", side_effect=observe_copy):
                AGENT._place_chromium_files("Brave", source.parent, home)

            self.assertEqual([before for before, _ in observations], [b"old tabs"])
            self.assertEqual(target.read_bytes(), b"new private tabs")
            if os.name == "posix":
                self.assertEqual([mode for _, mode in observations], [0o600])
                self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o600)

    def test_existing_public_history_and_cookies_keep_old_bytes_during_copy(self):
        with TemporaryDirectory(prefix="igloo-fedora-browser-") as temporary:
            root = Path(temporary)
            source = root / "stage" / "Brave"
            (source / "Network").mkdir(parents=True)
            (source / "History").write_bytes(b"new private history")
            (source / "Network" / "Cookies").write_bytes(b"new private cookies")

            home = root / "home" / "lab"
            default = (home / ".config" / "BraveSoftware" / "Brave-Browser"
                       / "Default")
            (default / "Network").mkdir(parents=True)
            old = {"History": b"old history", "Cookies": b"old cookies"}
            targets = {"History": default / "History",
                       "Cookies": default / "Network" / "Cookies"}
            for name, target in targets.items():
                target.write_bytes(old[name])
                target.chmod(0o777)

            real_copy = AGENT.shutil.copyfileobj
            observations = []

            def observe_copy(reader, writer):
                name = Path(reader.name).name
                observations.append((name, targets[name].read_bytes(),
                                     stat.S_IMODE(os.fstat(writer.fileno()).st_mode)))
                return real_copy(reader, writer)

            with mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT.shutil, "copyfileobj", side_effect=observe_copy):
                AGENT._place_chromium_files("Brave", source, home)

            self.assertEqual([(name, before) for name, before, _ in observations],
                             [("History", b"old history"),
                              ("Cookies", b"old cookies")])
            self.assertEqual(targets["History"].read_bytes(), b"new private history")
            self.assertEqual(targets["Cookies"].read_bytes(), b"new private cookies")
            if os.name == "posix":
                self.assertEqual([mode for _, _, mode in observations], [0o600, 0o600])
                for target in targets.values():
                    self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o600)

    @skipUnless(os.name == "posix", "symlink fixture needs POSIX")
    def test_redirected_profile_parent_is_rejected(self):
        with TemporaryDirectory(prefix="igloo-fedora-browser-") as temporary:
            root = Path(temporary)
            source = root / "stage" / "Brave"
            source.mkdir(parents=True)
            (source / "History").write_bytes(b"private history")
            home = root / "home" / "lab"
            config = home / ".config"
            config.mkdir(parents=True)
            foreign = root / "foreign"
            foreign.mkdir()
            (config / "BraveSoftware").symlink_to(foreign, target_is_directory=True)

            with mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[config]):
                with self.assertRaisesRegex(RuntimeError, "redirected"):
                    AGENT._place_chromium_files("Brave", source, home)
            self.assertEqual(list(foreign.iterdir()), [])


if __name__ == "__main__":
    main()
