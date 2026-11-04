"""Selected Debian-family Chromium credentials remain retryable after writes fail."""

import builtins
import importlib.util
import json
import os
import runpy
import sqlite3
import subprocess
import sys
from types import SimpleNamespace
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, main, mock


AGENT_PATH = (Path(__file__).resolve().parents[2] /
              "distros/_debian-family/agent/agent.py")
SPEC = importlib.util.spec_from_file_location("debian_credential_retry_agent", AGENT_PATH)
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)
CHROME_LOGIN_SCHEMA = (AGENT_PATH.parents[3] / "tools/chromium-crypto"
                       / "chrome155_login_data_schema.sql")


def create_browser_login_db(db: Path) -> None:
    db.parent.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(db) as connection:
        connection.executescript(CHROME_LOGIN_SCHEMA.read_text(encoding="utf-8"))


class DebianCredentialRetryTest(TestCase):
    def test_credential_prompt_uses_a_labeled_hidden_field(self):
        with mock.patch.object(AGENT.shutil, "which", return_value="/usr/bin/zenity"), \
             mock.patch.object(AGENT.subprocess, "run", return_value=SimpleNamespace(
                 returncode=0, stdout="synthetic-password\n")) as run:
            self.assertEqual(AGENT._ask_password(), (True, "synthetic-password"))
        argv = run.call_args.args[0]
        self.assertIn("--forms", argv)
        self.assertIn("--add-password=Linux account password", argv)
        self.assertIn("--title=Import saved browser passwords", argv)
        self.assertIn("--text=Enter your Linux account password to import saved browser passwords.", argv)

    def test_first_boot_retry_preserves_partially_imported_credential_store(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home" / "igloolab"
            home.mkdir(parents=True)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            chrome = {"name": "Google Chrome", "blob": "encrypted-chrome"}
            brave = {"name": "Brave", "blob": "encrypted-brave"}
            retained = {"attempts": 1, "browsers": [brave]}
            store.write_text(json.dumps(retained), encoding="utf-8")
            (root / "opt/igloo").mkdir(parents=True)
            (root / "etc/xdg/autostart").mkdir(parents=True)

            def mapped_path(value):
                return root / value.lstrip("/") if value.startswith("/") else Path(value)

            def persist(path, data):
                path.write_text(json.dumps(data), encoding="utf-8")

            account = SimpleNamespace(pw_dir=str(home), pw_uid=home.stat().st_uid,
                                      pw_gid=home.stat().st_gid)
            with mock.patch.object(AGENT, "Path", side_effect=mapped_path), \
                 mock.patch.dict(sys.modules, {"pwd": SimpleNamespace(getpwnam=lambda _: account)}), \
                 mock.patch.object(AGENT, "_safe_credential_store_path",
                                   return_value=([store.parent], store)), \
                 mock.patch.object(AGENT, "_persist_credential_retry", side_effect=persist), \
                 mock.patch.object(AGENT.os, "chown", create=True), \
                 mock.patch.object(AGENT.stat, "S_IMODE", return_value=0o600):
                AGENT.stage_credential_import({
                    "user": {"preferredLinuxUsername": "igloolab"},
                    "browsers": [
                        {"name": chrome["name"], "credentialsBlob": chrome["blob"]},
                        {"name": brave["name"], "credentialsBlob": brave["blob"]},
                    ],
                })
            self.assertEqual(retained, json.loads(store.read_text(encoding="utf-8")))
            hook = (root / "opt/igloo/credential-import.sh").read_text(
                encoding="utf-8")
            self.assertIn("[g]nome-initial-setup --existing-user", hook)
            self.assertIn("[/]usr/lib/linuxmint/mintwelcome/mintwelcome[.]py", hook)
            self.assertIn("org.gnome.Shell OverviewActive", hook)
            self.assertIn("org.freedesktop.DBus.Properties.Set", hook)
            self.assertIn("[ \"$attempt\" -lt 15 ] || exit 0", hook)
            if os.name == "posix":
                subprocess.run(["sh", "-n", str(root / "opt/igloo/credential-import.sh")],
                               check=True)

    def test_existing_credential_retry_rejects_foreign_or_malformed_state(self):
        with TemporaryDirectory() as temporary:
            store = Path(temporary) / "credentials.json"
            selected = [{"name": "Google Chrome", "blob": "encrypted-chrome"}]
            invalid = (
                '{ malformed',
                json.dumps({"attempts": -1, "browsers": selected}),
                json.dumps({"attempts": 1, "browsers": [
                    {"name": "Google Chrome", "blob": "different-envelope"}]}),
                json.dumps({"attempts": 1, "browsers": selected + selected}),
            )
            with mock.patch.object(AGENT.stat, "S_IMODE", return_value=0o600):
                for payload in invalid:
                    with self.subTest(payload=payload):
                        store.write_text(payload, encoding="utf-8")
                        with self.assertRaisesRegex(RuntimeError,
                                                    "Existing credential retry store"):
                            AGENT._validate_existing_credential_retry(store, selected)
                        self.assertEqual(payload, store.read_text(encoding="utf-8"))

    def test_completed_receipt_prevents_restage_before_and_after_store_unlink(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home" / "igloolab"
            home.mkdir(parents=True)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            selected = [{"name": "Google Chrome", "blob": "encrypted-chrome"}]
            selection_hash = AGENT._credential_selection_sha256(selected)
            store.write_text(json.dumps({"attempts": 1, "browsers": selected,
                                         "selectionSha256": selection_hash}),
                             encoding="utf-8")
            receipt = store.with_name(AGENT._CRED_DONE_NAME)
            receipt.write_text(json.dumps({"version": 1, "selectionSha256": selection_hash,
                                           "status": "consumed"}), encoding="utf-8")
            (root / "opt/igloo").mkdir(parents=True)
            (root / "etc/xdg/autostart").mkdir(parents=True)

            def mapped_path(value):
                return root / value.lstrip("/") if value.startswith("/") else Path(value)

            account = SimpleNamespace(pw_dir=str(home), pw_uid=home.stat().st_uid,
                                      pw_gid=home.stat().st_gid)
            manifest = {"user": {"preferredLinuxUsername": "igloolab"},
                        "browsers": [{"name": selected[0]["name"],
                                      "credentialsBlob": selected[0]["blob"]}]}
            with mock.patch.object(AGENT, "Path", side_effect=mapped_path), \
                 mock.patch.dict(sys.modules, {"pwd": SimpleNamespace(getpwnam=lambda _: account)}), \
                 mock.patch.object(AGENT, "_safe_credential_store_path",
                                   return_value=([store.parent], store)), \
                 mock.patch.object(AGENT, "_persist_credential_retry") as persist, \
                 mock.patch.object(AGENT, "_fsync_directory"), \
                 mock.patch.object(AGENT.os, "chown", create=True), \
                 mock.patch.object(AGENT.stat, "S_IMODE", return_value=0o600):
                AGENT.stage_credential_import(manifest)
                self.assertFalse(store.exists())
                persist.assert_not_called()
                AGENT.stage_credential_import(manifest)
                self.assertFalse(store.exists())
                persist.assert_not_called()
                receipt.write_text(json.dumps({"version": 1,
                                               "selectionSha256": "0" * 64,
                                               "status": "consumed"}), encoding="utf-8")
                with self.assertRaisesRegex(RuntimeError, "receipt differs"):
                    AGENT.stage_credential_import(manifest)
                self.assertFalse(store.exists())
                persist.assert_not_called()

    def test_completion_receipt_is_written_before_credential_store_removal(self):
        with TemporaryDirectory() as temporary:
            store = Path(temporary) / "credentials.json"
            store.write_text("encrypted input", encoding="utf-8")
            selected = [{"name": "Google Chrome", "blob": "encrypted-chrome"}]
            data = {"attempts": 0, "browsers": selected,
                    "selectionSha256": AGENT._credential_selection_sha256(selected)}
            with mock.patch.object(AGENT, "_persist_credential_retry",
                                   side_effect=OSError("receipt write failed")):
                with self.assertRaisesRegex(OSError, "receipt write failed"):
                    AGENT._finish_credential_import(store, data, "consumed")
            self.assertTrue(store.is_file())

            def persist(path, content):
                path.write_text(json.dumps(content), encoding="utf-8")

            with mock.patch.object(AGENT, "_persist_credential_retry", side_effect=persist), \
                 mock.patch.object(AGENT, "_fsync_directory") as fsync:
                AGENT._finish_credential_import(store, data, "consumed")
            self.assertFalse(store.exists())
            self.assertEqual("consumed", json.loads(
                (store.parent / AGENT._CRED_DONE_NAME).read_text(encoding="utf-8"))["status"])
            fsync.assert_called_once_with(store.parent)

    def test_successful_login_retires_store_with_bound_completion_receipt(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            selected = [{"name": "Google Chrome", "blob": "encrypted-chrome"}]
            digest = AGENT._credential_selection_sha256(selected)
            store.write_text(json.dumps({"attempts": 0, "browsers": selected,
                                         "selectionSha256": digest}), encoding="utf-8")
            store.chmod(0o600)

            def persist(path, content):
                path.write_text(json.dumps(content), encoding="utf-8")

            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, "lab")), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_decrypt_envelope", return_value={
                     "logins": [{"url": "https://example.invalid/login",
                                 "username": "fixture", "password": "synthetic"}],
                     "cookies": []}), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_chromium_credential_profile_ready",
                                   return_value=True), \
                 mock.patch.object(AGENT, "_import_into_login_data", return_value=1), \
                 mock.patch.object(AGENT, "_persist_credential_retry", side_effect=persist), \
                 mock.patch.object(AGENT, "_fsync_directory"), \
                 mock.patch.object(AGENT.stat, "S_IMODE", return_value=0o600):
                self.assertEqual(AGENT.run_user_credential_import(), 0)
                self.assertEqual(AGENT.run_user_credential_import(), 0)
            self.assertFalse(store.exists())
            self.assertEqual({"version": 1, "selectionSha256": digest,
                              "status": "consumed"}, json.loads(
                (store.parent / AGENT._CRED_DONE_NAME).read_text(encoding="utf-8")))

    def test_malformed_user_store_cannot_publish_completion_receipt(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            entry = {"name": "Google Chrome", "blob": "encrypted-chrome"}
            malformed = (
                {"attempts": 0, "browsers": [], "selectionSha256": "1" * 64},
                {"attempts": True, "browsers": [entry]},
                {"attempts": -1, "browsers": [entry]},
                {"attempts": 0, "browsers": [{"name": "Google Chrome"}]},
                {"attempts": 0, "browsers": [entry], "selectionSha256": "foreign"},
            )
            receipt = store.with_name(AGENT._CRED_DONE_NAME)
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT.stat, "S_IMODE", return_value=0o600), \
                 mock.patch.object(AGENT, "_ask_password") as ask_password, \
                 mock.patch.object(AGENT, "_persist_credential_retry") as persist:
                for data in malformed:
                    with self.subTest(data=data):
                        payload = json.dumps(data)
                        store.write_text(payload, encoding="utf-8")
                        store.chmod(0o600)
                        self.assertEqual(AGENT.run_user_credential_import(), 0)
                        self.assertEqual(payload, store.read_text(encoding="utf-8"))
                        self.assertFalse(receipt.exists())
                ask_password.assert_not_called()
                persist.assert_not_called()

    def test_valid_retry_store_exhausts_only_after_attempt_limit(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            selected = [{"name": "Google Chrome", "blob": "encrypted-chrome"}]
            digest = AGENT._credential_selection_sha256(selected)
            store.write_text(json.dumps({"attempts": AGENT._CRED_MAX_ATTEMPTS,
                                         "browsers": selected,
                                         "selectionSha256": digest}), encoding="utf-8")
            store.chmod(0o600)

            def persist(path, content):
                path.write_text(json.dumps(content), encoding="utf-8")

            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT.stat, "S_IMODE", return_value=0o600), \
                 mock.patch.object(AGENT, "_ask_password") as ask_password, \
                 mock.patch.object(AGENT, "_persist_credential_retry", side_effect=persist), \
                 mock.patch.object(AGENT, "_fsync_directory"):
                self.assertEqual(AGENT.run_user_credential_import(), 0)
            ask_password.assert_not_called()
            self.assertFalse(store.exists())
            self.assertEqual({"version": 1, "selectionSha256": digest,
                              "status": "exhausted"}, json.loads(
                (store.parent / AGENT._CRED_DONE_NAME).read_text(encoding="utf-8")))

    def test_valid_legacy_store_without_selection_hash_reaches_password_prompt(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            payload = json.dumps({"attempts": 1, "browsers": [
                {"name": "Google Chrome", "blob": "encrypted-chrome"}]})
            store.write_text(payload, encoding="utf-8")
            store.chmod(0o600)
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT.stat, "S_IMODE", return_value=0o600), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_chromium_credential_profile_ready",
                                   return_value=True), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, None)) as ask:
                self.assertEqual(AGENT.run_user_credential_import(), 0)
            ask.assert_called_once_with()
            self.assertEqual(payload, store.read_text(encoding="utf-8"))
            self.assertFalse(store.with_name(AGENT._CRED_DONE_NAME).exists())

    def test_foreign_completion_receipt_keeps_valid_retry_store(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            selected = [{"name": "Google Chrome", "blob": "encrypted-chrome"}]
            digest = AGENT._credential_selection_sha256(selected)
            payload = json.dumps({"attempts": 1, "browsers": selected,
                                  "selectionSha256": digest})
            store.write_text(payload, encoding="utf-8")
            store.chmod(0o600)
            receipt = store.with_name(AGENT._CRED_DONE_NAME)
            receipt.write_text(json.dumps({"version": 1,
                                           "selectionSha256": "0" * 64,
                                           "status": "consumed"}), encoding="utf-8")
            receipt.chmod(0o600)
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT.stat, "S_IMODE", return_value=0o600), \
                 mock.patch.object(AGENT, "_ask_password") as ask_password:
                with self.assertRaisesRegex(RuntimeError, "receipt differs"):
                    AGENT.run_user_credential_import()
            ask_password.assert_not_called()
            self.assertEqual(payload, store.read_text(encoding="utf-8"))

    def test_unmapped_or_uninstalled_browser_keeps_envelope_for_retry(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)

            def persist(path, content):
                path.write_text(json.dumps(content), encoding="utf-8")

            for name in ("Unknown Browser", "Google Chrome"):
                with self.subTest(name=name):
                    entry = {"name": name, "blob": "encrypted-fixture"}
                    store.write_text(json.dumps({"attempts": 0, "browsers": [entry]}),
                                     encoding="utf-8")
                    store.chmod(0o600)
                    with mock.patch.object(AGENT.Path, "home", return_value=home), \
                         mock.patch.object(AGENT, "_ask_password") as ask_password, \
                         mock.patch.object(AGENT, "_aes_self_test"), \
                         mock.patch.object(AGENT, "_decrypt_envelope") as decrypt, \
                         mock.patch.object(AGENT, "_chromium_config_homes", return_value=[]), \
                         mock.patch.object(AGENT, "_persist_credential_retry", side_effect=persist):
                        self.assertEqual(AGENT.run_user_credential_import(), 0)
                    ask_password.assert_not_called()
                    decrypt.assert_not_called()
                    self.assertEqual([entry], json.loads(
                        store.read_text(encoding="utf-8"))["browsers"])
                    self.assertFalse((store.parent / AGENT._CRED_DONE_NAME).exists())

    def test_installed_browser_waits_for_native_login_data_without_prompt(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            original = json.dumps({"attempts": 0, "browsers": [
                {"name": "Google Chrome", "blob": "encrypted-chrome"}]})
            store.write_text(original, encoding="utf-8")
            store.chmod(0o600)
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_ask_password") as ask_password:
                self.assertEqual(0, AGENT.run_user_credential_import())
            ask_password.assert_not_called()
            self.assertEqual(original, store.read_text(encoding="utf-8"))
            self.assertFalse(store.with_name(AGENT._CRED_DONE_NAME).exists())
            self.assertFalse((home / ".config/google-chrome/Default/Login Data").exists())

    def test_copied_cookie_jar_can_import_before_login_database_exists(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            store.write_text(json.dumps({"attempts": 0, "browsers": [
                {"name": "Google Chrome", "blob": "encrypted-chrome"}]}),
                encoding="utf-8")
            store.chmod(0o600)
            profile = home / ".config/google-chrome/Default"
            (profile / "Network").mkdir(parents=True)
            (profile / "Network/Cookies").write_bytes(b"copied cookie database")
            payload = {"logins": [], "cookies": [{"host": ".example.invalid",
                                                 "name": "fixture", "path": "/",
                                                 "value": "synthetic"}]}
            def persist(path, content):
                path.write_text(json.dumps(content), encoding="utf-8")

            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, "lab")) \
                         as ask_password, \
                 mock.patch.object(AGENT, "_decrypt_envelope", return_value=payload), \
                 mock.patch.object(AGENT, "_reencrypt_cookies", return_value=1) \
                         as import_cookies, \
                 mock.patch.object(AGENT, "_persist_credential_retry",
                                   side_effect=persist), \
                 mock.patch.object(AGENT, "_fsync_directory"), \
                 mock.patch.object(AGENT, "_import_into_login_data") as import_login:
                self.assertEqual(0, AGENT.run_user_credential_import())
            ask_password.assert_called_once()
            import_cookies.assert_called_once_with(profile, payload["cookies"])
            import_login.assert_not_called()
            self.assertFalse(store.exists())
            self.assertFalse((profile / "Login Data").exists())

    def test_absent_browser_stages_portable_profile_and_keeps_encrypted_envelope(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home"
            home.mkdir()
            source = root / "staged" / "Default"
            source.mkdir(parents=True)
            (source / "Bookmarks").write_text("{}", encoding="utf-8")
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            browser = {"name": "Google Chrome", "blob": "encrypted-chrome"}
            store.write_text(json.dumps({"attempts": 0, "browsers": [browser]}),
                             encoding="utf-8")
            store.chmod(0o600)

            def persist(path, content):
                path.write_text(json.dumps(content), encoding="utf-8")

            with mock.patch.object(AGENT.shutil, "which", return_value=None), \
                 mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_ask_password") as ask_password, \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_decrypt_envelope") as decrypt, \
                 mock.patch.object(AGENT, "_persist_credential_retry", side_effect=persist):
                self.assertEqual(AGENT._chromium_config_homes(
                    "com.google.Chrome", ("google-chrome",), home), [])
                AGENT._place_chromium_files("Google Chrome", source, home)
                self.assertEqual(AGENT.run_user_credential_import(), 0)
            ask_password.assert_not_called()
            decrypt.assert_not_called()

            self.assertTrue((source / "Bookmarks").is_file())
            copied = home / ".config/google-chrome/Default/Bookmarks"
            self.assertEqual(copied.read_text(encoding="utf-8"), "{}")
            if os.name == "posix":
                self.assertEqual(copied.stat().st_mode & 0o777, 0o600)
            self.assertEqual(json.loads(store.read_text(encoding="utf-8")),
                             {"attempts": 0, "browsers": [browser]})
            self.assertFalse((store.parent / AGENT._CRED_DONE_NAME).exists())



    def test_user_import_survives_unreadable_optional_boot_helper(self):
        original_import = builtins.__import__

        def denied_boot_helper(name, *args, **kwargs):
            if name == "igloo_boot":
                raise PermissionError("root-only optional boot helper")
            return original_import(name, *args, **kwargs)

        with mock.patch("builtins.__import__", side_effect=denied_boot_helper):
            namespace = runpy.run_path(str(AGENT_PATH), run_name="igloo_import_probe")
        self.assertIsNone(namespace["igloo_boot"])

    def test_system_autostart_remains_readable_under_private_umask(self):
        with TemporaryDirectory() as temporary:
            hook = Path(temporary) / "igloo-credential-import.desktop"
            previous = os.umask(0o077)
            try:
                AGENT._write_credential_autostart(hook)
            finally:
                os.umask(previous)
            self.assertEqual(hook.stat().st_mode & 0o777, 0o644)
            self.assertIn("Exec=/opt/igloo/credential-import.sh",
                          hook.read_text(encoding="utf-8"))

    def test_closed_password_prompt_preserves_encrypted_store_and_attempts(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            original = json.dumps({"attempts": 1, "browsers": [
                {"name": "Google Chrome", "blob": "encrypted-chrome"}]})
            store.write_text(original, encoding="utf-8")
            store.chmod(0o600)
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_chromium_credential_profile_ready",
                                   return_value=True), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, None)):
                self.assertEqual(AGENT.run_user_credential_import(), 0)
            self.assertEqual(store.read_text(encoding="utf-8"), original)
            self.assertEqual(store.stat().st_mode & 0o777, 0o600)

    def test_staging_rejects_unsafe_username_before_account_lookup(self):
        for username in ("..", "a/b", "Auser", "a" * 33, "user space"):
            with self.subTest(username=username), \
                 mock.patch("pwd.getpwnam") as account_lookup:
                with self.assertRaisesRegex(RuntimeError, "unsafe target user"):
                    AGENT.stage_credential_import({
                        "user": {"preferredLinuxUsername": username},
                        "browsers": [{"name": "Google Chrome", "credentialsBlob": "ciphertext"}],
                    })
                account_lookup.assert_not_called()

    def test_staging_rejects_redirected_store_parents_and_existing_store(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary) / "home"
            elsewhere = Path(temporary) / "elsewhere"
            home.mkdir()
            elsewhere.mkdir()
            (home / ".local").symlink_to(elsewhere, target_is_directory=True)
            with self.assertRaisesRegex(RuntimeError, "redirected"):
                AGENT._safe_credential_store_path(home)
            (home / ".local").unlink()

            directories, store = AGENT._safe_credential_store_path(home)
            self.assertEqual(len(directories), 3)
            self.assertTrue(all(path.is_dir() and not path.is_symlink()
                                for path in directories))
            store.symlink_to(elsewhere / "other-credentials.json")
            with self.assertRaisesRegex(RuntimeError, "redirected"):
                AGENT._safe_credential_store_path(home)
            self.assertFalse((elsewhere / "other-credentials.json").exists())
            store.unlink()
            directories[0].chmod(0o777)
            with self.assertRaisesRegex(RuntimeError, "redirected"):
                AGENT._safe_credential_store_path(home)

    def test_missing_or_partial_login_schema_is_not_fabricated(self):
        with TemporaryDirectory() as temporary:
            db = Path(temporary) / "Login Data"
            with self.assertRaisesRegex(RuntimeError, "Browser-created Login Data"):
                AGENT._import_into_login_data(db, [{
                    "url": "https://example.invalid/login",
                    "username": "fixture", "password": "synthetic-password",
                }])
            self.assertFalse(db.exists())
            with sqlite3.connect(db) as connection:
                connection.execute(
                    "CREATE TABLE logins (id INTEGER PRIMARY KEY, "
                    "origin_url TEXT, username_value TEXT, "
                    "unavailable_field TEXT NOT NULL)")
            original = db.read_bytes()
            with self.assertRaisesRegex(RuntimeError, "Browser-created Login Data"):
                AGENT._import_into_login_data(db, [{
                    "url": "https://example.invalid/login",
                    "username": "fixture", "password": "synthetic-password",
                }])
            self.assertEqual(original, db.read_bytes())

    def test_complete_browser_schema_still_checks_new_required_columns(self):
        with TemporaryDirectory() as temporary:
            db = Path(temporary) / "Login Data"
            create_browser_login_db(db)
            with sqlite3.connect(db) as connection:
                connection.execute(
                    "ALTER TABLE logins ADD COLUMN unavailable_field TEXT NOT NULL")
            with self.assertRaisesRegex(RuntimeError, "unsupported required columns"):
                AGENT._import_into_login_data(db, [{
                    "url": "https://example.invalid/login",
                    "username": "fixture", "password": "synthetic-password",
                }])

    def test_failed_browser_retains_only_its_envelope_until_retry(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            chrome = {"name": "Google Chrome", "blob": "encrypted-chrome"}
            brave = {"name": "Brave", "blob": "encrypted-brave"}
            store.write_text(json.dumps({"attempts": 0,
                                         "browsers": [chrome, brave]}),
                             encoding="utf-8")
            store.chmod(0o600)
            payload = {"logins": [{"url": "https://example.invalid/login",
                                   "username": "fixture",
                                   "password": "synthetic-password"}],
                       "cookies": []}
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, "lab")), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_decrypt_envelope", return_value=payload), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_chromium_credential_profile_ready",
                                   return_value=True), \
                 mock.patch.object(AGENT, "_import_into_login_data",
                                   side_effect=[1, OSError("database failure"), 1]) \
                         as import_login:
                self.assertEqual(AGENT.run_user_credential_import(), 0)
                retained = json.loads(store.read_text(encoding="utf-8"))
                self.assertEqual(retained["browsers"], [brave])
                self.assertEqual(retained["attempts"], 0)
                self.assertEqual(store.stat().st_mode & 0o777, 0o600)
                self.assertEqual(AGENT.run_user_credential_import(), 0)
                self.assertFalse(store.exists())
                self.assertEqual(import_login.call_count, 3)

    def test_missing_selected_cookie_remains_encrypted_for_retry(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            browser = {"name": "Google Chrome", "blob": "encrypted-chrome"}
            store.write_text(json.dumps({"attempts": 0, "browsers": [browser]}),
                             encoding="utf-8")
            store.chmod(0o600)
            payload = {"logins": [], "cookies": [{"name": "session"}]}
            with mock.patch.object(AGENT.Path, "home", return_value=home), \
                 mock.patch.object(AGENT, "_ask_password", return_value=(True, "lab")), \
                 mock.patch.object(AGENT, "_aes_self_test"), \
                 mock.patch.object(AGENT, "_decrypt_envelope", return_value=payload), \
                 mock.patch.object(AGENT, "_chromium_config_homes",
                                   return_value=[home / ".config"]), \
                 mock.patch.object(AGENT, "_chromium_credential_profile_ready",
                                   return_value=True), \
                 mock.patch.object(AGENT, "_reencrypt_cookies", side_effect=[0, 1]):
                self.assertEqual(AGENT.run_user_credential_import(), 0)
                retained = json.loads(store.read_text(encoding="utf-8"))
                self.assertEqual(retained["browsers"], [browser])
                self.assertEqual(retained["attempts"], 0)
                self.assertEqual(AGENT.run_user_credential_import(), 0)
                self.assertFalse(store.exists())

    def test_unreadable_store_is_retained(self):
        with TemporaryDirectory() as temporary:
            home = Path(temporary)
            store = home / AGENT._CRED_STORE_REL
            store.parent.mkdir(parents=True)
            store.write_text("not-json", encoding="utf-8")
            store.chmod(0o600)
            with mock.patch.object(AGENT.Path, "home", return_value=home):
                self.assertEqual(AGENT.run_user_credential_import(), 0)
            self.assertTrue(store.is_file())

    def test_posix_real_credential_retry_and_completion_receipt(self):
        """Exercise encryption, SQLite, atomic files, and fsync in one process."""
        if os.name != "posix":
            self.skipTest("This fixture requires POSIX ownership and file modes")

        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = root / "home" / "igloolab"
            home.mkdir(parents=True, mode=0o700)
            (root / "opt" / "igloo").mkdir(parents=True)
            (root / "etc" / "xdg" / "autostart").mkdir(parents=True)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            zenity = fake_bin / "zenity"
            zenity.write_text("#!/bin/sh\nprintf '%s\\n' 'synthetic-fixture-password'\n",
                              encoding="utf-8")
            zenity.chmod(0o700)
            for browser_binary in ("google-chrome", "brave-browser"):
                installed = fake_bin / browser_binary
                installed.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
                installed.chmod(0o700)

            # The only substituted interfaces are the root install locations,
            # account lookup/chown, installed browser commands, and the graphical
            # password prompt. The
            # subprocess uses the real POSIX store, crypto, SQLite and fsync.
            script = r'''
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sqlite3
import sys
from types import SimpleNamespace
from unittest import mock

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

root = Path(os.environ["IGLOO_FIXTURE_ROOT"])
home = root / "home" / "igloolab"
spec = importlib.util.spec_from_file_location(
    "debian_credential_posix_agent", os.environ["IGLOO_AGENT_PATH"])
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)
password = "synthetic-fixture-password"

def envelope(browser, url, username):
    payload = {"browser": browser, "logins": [{
        "url": url, "username": username, "password": "synthetic-migrated-value"}],
        "cookies": []}
    salt, nonce = os.urandom(16), os.urandom(12)
    key = hashlib.pbkdf2_hmac("sha256", password.encode(), salt,
                              agent._CHROMIUM_PBKDF2_ITERATIONS, 32)
    body = json.dumps(payload, separators=(",", ":")).encode()
    return base64.b64encode(agent._CHROMIUM_ENVELOPE_MAGIC + salt + nonce +
                            AESGCM(key).encrypt(nonce, body, None)).decode()

chrome_url = "https://chrome.example.invalid/login"
brave_url = "https://brave.example.invalid/login"
manifest = {"user": {"preferredLinuxUsername": "igloolab"}, "browsers": [
    {"name": "Google Chrome", "credentialsBlob": envelope(
        "Google Chrome", chrome_url, "chrome-user")},
    {"name": "Brave", "credentialsBlob": envelope(
        "Brave", brave_url, "brave-user")}]}
selected = [{"name": entry["name"], "blob": entry["credentialsBlob"]}
            for entry in manifest["browsers"]]
selection_hash = agent._credential_selection_sha256(selected)
store = home / agent._CRED_STORE_REL
receipt = store.with_name(agent._CRED_DONE_NAME)
original_path = Path
account = SimpleNamespace(pw_dir=str(home), pw_uid=os.getuid(),
                          pw_gid=os.getgid())

def mapped_path(value):
    if isinstance(value, str) and value.startswith("/"):
        return root / value.lstrip("/")
    return original_path(value)

def stage():
    with mock.patch.object(agent, "Path", side_effect=mapped_path), \
         mock.patch.dict(sys.modules, {"pwd": SimpleNamespace(
             getpwnam=lambda name: account)}), \
         mock.patch.object(agent.os, "chown"):
        agent.stage_credential_import(manifest)

def login_row(browser_dir, url, username):
    db = home / ".config" / browser_dir / "Default" / "Login Data"
    with sqlite3.connect(db) as connection:
        rows = connection.execute(
            "SELECT origin_url, username_value, password_value FROM logins"
        ).fetchall()
    assert len(rows) == 1 and rows[0][:2] == (url, username), rows
    assert rows[0][2].startswith(b"v10"), rows

def browser_db(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(path) as connection:
        connection.executescript(Path(os.environ["IGLOO_BROWSER_SCHEMA_PATH"])
                                 .read_text(encoding="utf-8"))

stage()
assert store.stat().st_mode & 0o777 == 0o600
assert not receipt.exists()
assert agent.run_user_credential_import() == 0
assert agent._read_private_credential_json(store, "Credential store") == {
    "attempts": 0, "browsers": selected,
    "selectionSha256": selection_hash}
assert not (home / ".config/google-chrome/Default/Login Data").exists()
browser_db(home / ".config/google-chrome/Default/Login Data")
brave_db = home / ".config/BraveSoftware/Brave-Browser/Default/Login Data"
brave_db.mkdir(parents=True)  # A real SQLite write failure, not a stub.
assert agent.run_user_credential_import() == 0
login_row("google-chrome", chrome_url, "chrome-user")
retained = agent._read_private_credential_json(store, "Credential store")
assert retained == {"attempts": 0, "browsers": [selected[1]],
                    "selectionSha256": selection_hash}, retained
partial_bytes = store.read_bytes()
stage()  # First boot replay must not recreate the consumed Chrome envelope.
assert store.read_bytes() == partial_bytes
assert not receipt.exists()

brave_db.rmdir()
browser_db(brave_db)
assert agent.run_user_credential_import() == 0
login_row("BraveSoftware/Brave-Browser", brave_url, "brave-user")
assert not store.exists()
assert receipt.stat().st_mode & 0o777 == 0o600
assert agent._read_private_credential_json(receipt, "Completion receipt") == {
    "version": 1, "selectionSha256": selection_hash, "status": "consumed"}
receipt_bytes = receipt.read_bytes()

# Crash after the durable receipt but before unlinking the old input.
agent._persist_credential_retry(store, {"attempts": 0, "browsers": selected,
                                        "selectionSha256": selection_hash})
stage()
assert not store.exists() and receipt.read_bytes() == receipt_bytes
stage()  # A later pass still must not restage completed envelopes.
assert not store.exists() and receipt.read_bytes() == receipt_bytes

# A receipt bound to another selection cannot retire even a valid store.
agent._persist_credential_retry(store, {"attempts": 0, "browsers": selected,
                                        "selectionSha256": selection_hash})
agent._persist_credential_retry(receipt, {
    "version": 1, "selectionSha256": "f" * 64, "status": "consumed"})
before = store.read_bytes()
try:
    stage()
except RuntimeError as error:
    assert "receipt differs" in str(error), str(error)
else:
    raise AssertionError("A foreign completion receipt was accepted")
assert store.read_bytes() == before and store.stat().st_mode & 0o777 == 0o600
'''
            environment = os.environ.copy()
            environment.update({
                "HOME": str(home),
                "XDG_CONFIG_HOME": str(home / ".config"),
                "PATH": str(fake_bin),
                "IGLOO_AGENT_PATH": str(AGENT_PATH),
                "IGLOO_BROWSER_SCHEMA_PATH": str(CHROME_LOGIN_SCHEMA),
                "IGLOO_FIXTURE_ROOT": str(root),
            })
            result = subprocess.run([sys.executable, "-B", "-c", script],
                                    env=environment, text=True,
                                    capture_output=True, timeout=60, check=False)
            self.assertEqual(0, result.returncode,
                             f"POSIX credential fixture failed:\n{result.stderr}")


if __name__ == "__main__":
    main()
