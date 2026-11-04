"""Exact-bound OEMDRV redaction and retry checks on disposable files."""

import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import Mock, patch


AGENT_PATH = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/agent.py"
spec = importlib.util.spec_from_file_location("debian_seed_agent", AGENT_PATH)
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)

HASH = "$6$igloo-lab$0123456789abcdef0123456789abcdef"


def cpio_entry(name: str, body: bytes) -> bytes:
    fields = [0, 0o100644, 0, 0, 1, 0, len(body), 0, 0, 0, 0, len(name) + 1, 0]
    header = b"070701" + b"".join(f"{value:08X}".encode() for value in fields)
    out = header + name.encode() + b"\0"
    out += b"\0" * ((-len(out)) % 4)
    out += body
    out += b"\0" * ((-len(out)) % 4)
    return out


class SeedRedactionTests(unittest.TestCase):
    def fixture(self, root: Path, *, with_initrd=True, selected_media=False,
                selected_data=False):
        seed = root / "seed"
        installed = root / "installed"
        seed.mkdir()
        installed.mkdir()
        manifest = {"schemaVersion": 1, "distroId": "debian",
                    "user": {"linuxPasswordCrypted": HASH, "preferredLinuxUsername": "lab"},
                    "wifiNetworks": [{"psk": "lab-wifi"}],
                    "browsers": [{"credentialsBlob": "lab-browser"}]}
        manifest_bytes = json.dumps(manifest).encode()
        (seed / "migration-manifest.json").write_bytes(manifest_bytes)
        installed_manifest = installed / "manifest.json"
        installed_manifest.write_bytes(manifest_bytes)
        installed_manifest.chmod(0o600)
        preseed = b"d-i passwd/user-password-crypted password " + HASH.encode() + b"\n"
        (seed / "preseed.cfg").write_bytes(preseed)
        files = ["migration-manifest.json", "preseed.cfg"]
        media_files = []
        if selected_media:
            media_files = ["igloo-wallpaper.PNG", "igloo-avatar.jpg"]
            manifest["wallpaper"] = {"fileName": media_files[0]}
            manifest["accountPicture"] = {"fileName": media_files[1]}
            manifest_bytes = json.dumps(manifest).encode()
            (seed / "migration-manifest.json").write_bytes(manifest_bytes)
            installed_manifest.write_bytes(manifest_bytes)
            for name in media_files:
                (seed / name).write_bytes(("selected " + name).encode())
        user_files = []
        if selected_data:
            manifest["files"] = {"stagingPath": "selected",
                                 "folders": [{"name": "Documents"}]}
            manifest["browsers"] = [{
                "sourceRelativePath": "AppData/Local/Google/Chrome/User Data",
                "profileStagingPath": "browser-profiles/AppData/Local/Google/Chrome/User Data",
            }]
            user_files = [
                "files/Documents/empty.txt",
                "browser-profiles/AppData/Local/Google/Chrome/User Data/Default/Bookmarks",
            ]
            for name, payload in zip(user_files, (b"", b"selected bookmarks")):
                selected = seed / name
                selected.parent.mkdir(parents=True, exist_ok=True)
                selected.write_bytes(payload)
            manifest_bytes = json.dumps(manifest).encode()
            (seed / "migration-manifest.json").write_bytes(manifest_bytes)
            installed_manifest.write_bytes(manifest_bytes)
        if with_initrd:
            boot = seed / "igloo-boot"
            boot.mkdir()
            overlay = cpio_entry("preseed.cfg", preseed) + cpio_entry("TRAILER!!!", b"")
            (boot / "initrd").write_bytes(b"lab-initrd-prefix" + gzip.compress(overlay))
            files.append("igloo-boot/initrd")
        agent_dir = seed / "igloo-agent"
        agent_dir.mkdir()
        for name in sorted(agent._COMMON_SEED_AGENT_FILES):
            (agent_dir / name).write_bytes((name + " provider payload\n").encode())
        bound_files = files + media_files + user_files + ["igloo-agent/" + name
                                              for name in sorted(agent._COMMON_SEED_AGENT_FILES)]
        identity = {"version": 1,
                    "diskGuid": "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE",
                    "partitionGuid": "11111111-2222-3333-4444-555555555555",
                    "files": [{"path": name,
                               "sha256": hashlib.sha256((seed / name).read_bytes()).hexdigest().upper()}
                              for name in bound_files]}
        identity_bytes = json.dumps(identity).encode()
        (seed / "igloo-seed-identity.json").write_bytes(identity_bytes)
        (installed / "seed-identity.json").write_bytes(identity_bytes)
        (installed / "seed-identity.json").chmod(0o600)
        return seed, installed_manifest, manifest, files

    def test_bound_files_are_redacted_and_initrd_stays_decompressible(self):
        with TemporaryDirectory() as temporary:
            seed, installed, manifest, files = self.fixture(Path(temporary))
            with patch.object(agent, "_verify_seed_block_identity") as verify, \
                    patch.object(agent, "run_cmd", return_value=Mock(returncode=0)) as command:
                agent.redact_seed(manifest, installed, seed)
                agent.redact_manifest(manifest, installed)
                agent.redact_seed(manifest, installed, seed)  # completed-file retry is idempotent
            self.assertEqual(4, verify.call_count)
            self.assertEqual(4, command.call_count)
            for name in files:
                content = (seed / name).read_bytes()
                self.assertNotIn(HASH.encode(), content)
            initrd = (seed / "igloo-boot/initrd").read_bytes()
            self.assertTrue(initrd.startswith(b"lab-initrd-prefix\x1f\x8b"))
            self.assertNotIn(HASH.encode(), gzip.decompress(initrd[len(b"lab-initrd-prefix"):]))
            self.assertNotIn(HASH, installed.read_text())
            self.assertEqual(0o600, installed.stat().st_mode & 0o777)
            self.assertIn(HASH, (installed.parent / "manifest.retry.json").read_text())
            journal = installed.parent / "seed-retry/journal.json"
            self.assertTrue(journal.is_file())
            self.assertEqual(set(files), set(json.loads(journal.read_text())["redactedHashes"]))
            for name in agent._COMMON_SEED_AGENT_FILES:
                self.assertEqual((name + " provider payload\n").encode(),
                                 (seed / "igloo-agent" / name).read_bytes())

    def test_changed_bound_agent_refuses_seed_scrub(self):
        with TemporaryDirectory() as temporary:
            seed, installed, manifest, _ = self.fixture(Path(temporary))
            (seed / "igloo-agent/agent.py").write_text("changed executable\n")
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd") as command, \
                    self.assertRaisesRegex(RuntimeError, "agent file changed"):
                agent.redact_seed(manifest, installed, seed)
            command.assert_not_called()
            self.assertIn(HASH, (seed / "migration-manifest.json").read_text())

    def test_selected_media_is_bound_but_not_part_of_secret_scrub(self):
        with TemporaryDirectory() as temporary:
            seed, installed, manifest, secrets = self.fixture(
                Path(temporary), selected_media=True)
            selected = {name: (seed / name).read_bytes()
                        for name in ("igloo-wallpaper.PNG", "igloo-avatar.jpg")}
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd", return_value=Mock(returncode=0)):
                agent.redact_seed(manifest, installed, seed)
            self.assertEqual(selected, {name: (seed / name).read_bytes()
                                        for name in selected})
            journal = json.loads((installed.parent / "seed-retry/journal.json").read_text())
            self.assertEqual(set(secrets), set(journal["redactedHashes"]))
            self.assertNotIn(HASH, (seed / "migration-manifest.json").read_text())

    def test_extra_or_changed_selected_media_refuses_scrub(self):
        with TemporaryDirectory() as temporary:
            seed, installed, manifest, _ = self.fixture(
                Path(temporary), selected_media=True)
            (seed / "igloo-wallpaper.PNG").write_bytes(b"changed selected media")
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd") as command, \
                    self.assertRaisesRegex(RuntimeError, "selected media changed"):
                agent.redact_seed(manifest, installed, seed)
            command.assert_not_called()
            (seed / "igloo-wallpaper.PNG").write_bytes(b"selected igloo-wallpaper.PNG")
            identity = json.loads((seed / "igloo-seed-identity.json").read_text())
            identity["files"].append({"path": "unexpected.txt", "sha256": "A" * 64})
            encoded = json.dumps(identity).encode()
            (seed / "igloo-seed-identity.json").write_bytes(encoded)
            (installed.parent / "seed-identity.json").write_bytes(encoded)
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd") as command, \
                    self.assertRaisesRegex(RuntimeError, "unexpected file"):
                agent.redact_seed(manifest, installed, seed)
            command.assert_not_called()
            self.assertIn(HASH, (seed / "migration-manifest.json").read_text())

    def test_selected_user_data_is_bound_but_excluded_from_secret_scrub(self):
        with TemporaryDirectory() as temporary:
            seed, installed, manifest, secrets = self.fixture(
                Path(temporary), selected_data=True)
            selected = {
                name: (seed / name).read_bytes() for name in (
                    "files/Documents/empty.txt",
                    "browser-profiles/AppData/Local/Google/Chrome/User Data/Default/Bookmarks",
                )
            }
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd", return_value=Mock(returncode=0)):
                agent.redact_seed(manifest, installed, seed)
                agent.redact_seed(manifest, installed, seed)  # identity-bound retry
            self.assertEqual(selected, {name: (seed / name).read_bytes()
                                        for name in selected})
            journal = json.loads((installed.parent / "seed-retry/journal.json").read_text())
            self.assertEqual(set(secrets), set(journal["redactedHashes"]))

    def test_changed_or_extra_selected_user_data_refuses_scrub(self):
        with TemporaryDirectory() as temporary:
            seed, installed, manifest, _ = self.fixture(
                Path(temporary), selected_data=True)
            bookmark = seed / (
                "browser-profiles/AppData/Local/Google/Chrome/User Data/Default/Bookmarks")
            bookmark.write_bytes(b"changed bookmarks")
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd") as command, \
                    self.assertRaisesRegex(RuntimeError, "selected user data changed"):
                agent.redact_seed(manifest, installed, seed)
            command.assert_not_called()
            bookmark.write_bytes(b"selected bookmarks")
            extra = bookmark.parent / "unselected.json"
            extra.write_bytes(b"unselected")
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd") as command, \
                    self.assertRaisesRegex(RuntimeError, "omits selected user-data bindings"):
                agent.redact_seed(manifest, installed, seed)
            command.assert_not_called()

    def test_identity_mismatch_and_unproven_block_refuse_seed_writes(self):
        with TemporaryDirectory() as temporary:
            seed, installed, manifest, _ = self.fixture(Path(temporary), with_initrd=False)
            (installed.parent / "seed-identity.json").write_text("{}")
            with patch.object(agent, "run_cmd") as command, \
                    self.assertRaisesRegex(RuntimeError, "identity copies differ"):
                agent.redact_seed(manifest, installed, seed)
            command.assert_not_called()
            (installed.parent / "seed-identity.json").write_bytes(
                (seed / "igloo-seed-identity.json").read_bytes())
            with patch.object(agent, "_verify_seed_block_identity",
                              side_effect=RuntimeError("wrong PARTUUID")), \
                    patch.object(agent, "run_cmd") as command, \
                    self.assertRaisesRegex(RuntimeError, "wrong PARTUUID"):
                agent.redact_seed(manifest, installed, seed)
            command.assert_not_called()
            self.assertIn(HASH, (seed / "migration-manifest.json").read_text())

    def test_present_injected_initrd_must_be_bound(self):
        with TemporaryDirectory() as temporary:
            seed, installed, manifest, _ = self.fixture(Path(temporary))
            identity = json.loads((seed / "igloo-seed-identity.json").read_text())
            identity["files"] = [record for record in identity["files"]
                                 if record["path"] != "igloo-boot/initrd"]
            encoded = json.dumps(identity).encode()
            (seed / "igloo-seed-identity.json").write_bytes(encoded)
            (installed.parent / "seed-identity.json").write_bytes(encoded)
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd") as command, \
                    self.assertRaisesRegex(RuntimeError, "omits the injected installer initrd"):
                agent.redact_seed(manifest, installed, seed)
            command.assert_not_called()

    def test_failed_seed_write_keeps_mixed_bound_state_and_retries_without_secret_temporaries(self):
        with TemporaryDirectory() as temporary:
            seed, installed, manifest, _ = self.fixture(Path(temporary), with_initrd=False)
            real_replace = agent._replace_bound_seed_file
            failed = False

            def fail_once(source, destination, expected_hash, identity_digest, relative_name):
                nonlocal failed
                if destination == seed / "preseed.cfg" and not failed:
                    failed = True
                    raise OSError("lab write failure")
                return real_replace(source, destination, expected_hash,
                                    identity_digest, relative_name)

            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd", return_value=Mock(returncode=0)), \
                    patch.object(agent, "_replace_bound_seed_file", side_effect=fail_once), \
                    self.assertRaisesRegex(OSError, "lab write failure"):
                agent.redact_seed(manifest, installed, seed)
            self.assertNotIn(HASH, (seed / "migration-manifest.json").read_text())
            self.assertIn(HASH, (seed / "preseed.cfg").read_text())
            self.assertIn(HASH, (installed.parent / "manifest.retry.json").read_text())
            self.assertTrue((installed.parent / "seed-retry/journal.json").is_file())
            self.assertEqual([], list(seed.rglob(".igloo-*")))
            self.assertFalse((installed.parent / ".done").exists())
            identity_digest = hashlib.sha256(
                (seed / "igloo-seed-identity.json").read_bytes()).hexdigest()
            suffix = hashlib.sha256((identity_digest + ":preseed.cfg").encode()).hexdigest()[:32]
            abandoned = seed / (".igloo-redact-" + suffix)
            abandoned.write_bytes(b"partial planned redacted bytes")
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd", return_value=Mock(returncode=0)):
                agent.redact_seed(manifest, installed, seed)
            self.assertNotIn(HASH, (seed / "preseed.cfg").read_text())
            self.assertEqual([], list(seed.rglob(".igloo-*")))
            manifest_suffix = hashlib.sha256(
                (identity_digest + ":migration-manifest.json").encode()).hexdigest()[:32]
            completed_temp = seed / (".igloo-redact-" + manifest_suffix)
            completed_temp.write_bytes(b"partial planned redacted bytes")
            with patch.object(agent, "_verify_seed_block_identity"), \
                    patch.object(agent, "run_cmd", return_value=Mock(returncode=0)):
                agent.redact_seed(manifest, installed, seed)
            self.assertEqual([], list(seed.rglob(".igloo-*")))

    def test_redaction_refuses_redirected_journal_derived_temporary(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "redacted"
            destination = root / "preseed.cfg"
            outsider = root / "outside"
            source.write_bytes(b"! redacted\n")
            destination.write_bytes(HASH.encode())
            outsider.write_bytes(b"unrelated")
            identity_digest = "a" * 64
            suffix = hashlib.sha256((identity_digest + ":preseed.cfg").encode()).hexdigest()[:32]
            (root / (".igloo-redact-" + suffix)).symlink_to(outsider)
            with self.assertRaisesRegex(RuntimeError, "temporary is redirected"):
                agent._replace_bound_seed_file(source, destination,
                    hashlib.sha256(source.read_bytes()).hexdigest(),
                    identity_digest, "preseed.cfg")
            self.assertEqual(HASH.encode(), destination.read_bytes())
            self.assertEqual(b"unrelated", outsider.read_bytes())


if __name__ == "__main__":
    unittest.main()
