"""Exercise the pre-copy Debian OEMDRV binding with independent disk observations."""

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SOURCE = (Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/igloo-verify-seed.py")
spec = importlib.util.spec_from_file_location("igloo_debian_seed_verifier", SOURCE)
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)
PART = "6303f619-17ad-4017-acc1-05d3a5c51c38"
DISK = "4e76d226-9262-45e1-8571-c74a53c0ebbf"


class DebianSeedVerifierTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="igloo-debian-seed-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.seed = self.root / "seed"
        self.state = self.root / "state"
        self.seed.mkdir()
        self.state.mkdir()
        self.part_file = self.state / "selected-seed-partuuid"
        self.disk_file = self.state / "selected-seed-diskguid"
        self.part_file.write_text(PART + "\n")
        self.disk_file.write_text(DISK + "\n")
        records = []
        self.agent_dir = self.seed / "igloo-agent"
        self.agent_dir.mkdir()
        for name in ("migration-manifest.json", "preseed.cfg"):
            payload = (json.dumps({"schemaVersion": 1, "distroId": "debian"}).encode()
                       if name == "migration-manifest.json" else b"preseed original\n")
            (self.seed / name).write_bytes(payload)
            records.append({"path": name, "sha256": hashlib.sha256(payload).hexdigest()})
        for name in sorted(verifier.COMMON_AGENT_FILES):
            payload = (name + " provider payload\n").encode()
            (self.agent_dir / name).write_bytes(payload)
            records.append({"path": "igloo-agent/" + name,
                            "sha256": hashlib.sha256(payload).hexdigest()})
        self.identity = {"version": 1, "diskGuid": DISK,
                         "partitionGuid": PART, "files": records}
        self.identity_file = self.seed / "igloo-seed-identity.json"
        self.identity_file.write_text(json.dumps(self.identity))
        self.disk_result = DISK
        self.inventory = PART + "\n"

    def _command(self, *args: str) -> str:
        if args[:3] == ("lsblk", "-rno", "PARTUUID"):
            return self.inventory
        if args[0] == "findmnt":
            return "/dev/vdb1"
        if args[:3] == ("lsblk", "-dnro", "PARTUUID"):
            return PART
        if args[:3] == ("lsblk", "-dnro", "PKNAME"):
            return "vdb"
        if args[:3] == ("lsblk", "-dnro", "PTUUID"):
            return self.disk_result
        raise AssertionError(args)

    def verify(self) -> list[str]:
        number = self.seed.stat().st_dev
        with patch.object(verifier, "_command", side_effect=self._command), \
             patch.object(verifier, "_block_number", return_value=number), \
             patch.object(Path, "is_mount", return_value=True):
            return verifier.verify_seed(self.part_file, self.disk_file, self.seed)

    def select_media(self, wallpaper: str | None = None,
                     avatar: str | None = None) -> None:
        manifest = {"schemaVersion": 1, "distroId": "debian"}
        if wallpaper:
            manifest["wallpaper"] = {"fileName": wallpaper}
            (self.seed / wallpaper).write_bytes(b"selected wallpaper bytes")
            self.identity["files"].append({
                "path": wallpaper,
                "sha256": hashlib.sha256((self.seed / wallpaper).read_bytes()).hexdigest(),
            })
        if avatar:
            manifest["accountPicture"] = {"fileName": avatar}
            (self.seed / avatar).write_bytes(b"selected avatar bytes")
            self.identity["files"].append({
                "path": avatar,
                "sha256": hashlib.sha256((self.seed / avatar).read_bytes()).hexdigest(),
            })
        payload = json.dumps(manifest).encode()
        (self.seed / "migration-manifest.json").write_bytes(payload)
        self.identity["files"][0]["sha256"] = hashlib.sha256(payload).hexdigest()
        self.identity_file.write_text(json.dumps(self.identity))

    def select_user_data(self) -> tuple[Path, Path]:
        manifest = {
            "schemaVersion": 1, "distroId": "debian",
            "files": {"stagingPath": "selected", "folders": [{"name": "Documents"}]},
            "browsers": [{
                "sourceRelativePath": "AppData/Local/Google/Chrome/User Data",
                "profileStagingPath": "browser-profiles/AppData/Local/Google/Chrome/User Data",
            }],
        }
        folder = self.seed / "files/Documents"
        browser = (self.seed /
                   "browser-profiles/AppData/Local/Google/Chrome/User Data/Default")
        folder.mkdir(parents=True)
        browser.mkdir(parents=True)
        empty = folder / "empty.txt"
        bookmarks = browser / "Bookmarks"
        empty.write_bytes(b"")
        bookmarks.write_bytes(b"selected bookmarks")
        payload = json.dumps(manifest).encode()
        (self.seed / "migration-manifest.json").write_bytes(payload)
        self.identity["files"][0]["sha256"] = hashlib.sha256(payload).hexdigest()
        for selected in (empty, bookmarks):
            self.identity["files"].append({
                "path": selected.relative_to(self.seed).as_posix(),
                "sha256": hashlib.sha256(selected.read_bytes()).hexdigest(),
            })
        self.identity_file.write_text(json.dumps(self.identity))
        return empty, bookmarks

    def test_exact_guid_pair_and_file_hashes_pass(self) -> None:
        self.verify()

    def test_legacy_unstaged_selection_needs_no_user_data_identity_records(self) -> None:
        manifest = {
            "schemaVersion": 1, "distroId": "debian",
            "files": {"stagingPath": "", "includedFolders": ["Documents"]},
            "browsers": [{"sourceRelativePath": "AppData/Roaming/Mozilla/Firefox",
                          "profileStagingPath": ""}],
        }
        payload = json.dumps(manifest).encode()
        (self.seed / "migration-manifest.json").write_bytes(payload)
        self.identity["files"][0]["sha256"] = hashlib.sha256(payload).hexdigest()
        self.identity_file.write_text(json.dumps(self.identity))
        self.assertEqual([], self.verify())

    def test_empty_selected_folder_root_is_required_without_file_records(self) -> None:
        manifest = {"schemaVersion": 1, "distroId": "debian",
                    "files": {"stagingPath": "selected",
                              "folders": [{"name": "Documents"}]}}
        selected = self.seed / "files/Documents"
        selected.mkdir(parents=True)
        payload = json.dumps(manifest).encode()
        (self.seed / "migration-manifest.json").write_bytes(payload)
        self.identity["files"][0]["sha256"] = hashlib.sha256(payload).hexdigest()
        self.identity_file.write_text(json.dumps(self.identity))
        self.assertEqual([], self.verify())
        selected.rmdir()
        with self.assertRaisesRegex(ValueError, "Selected user-data directory is missing"):
            self.verify()

    def test_wrong_or_duplicate_partition_and_wrong_disk_fail(self) -> None:
        self.inventory = PART + "\n" + PART + "\n"
        with self.assertRaisesRegex(ValueError, "ambiguous"):
            self.verify()
        self.inventory = PART + "\n"
        self.disk_result = "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
        with self.assertRaisesRegex(ValueError, "installer selection"):
            self.verify()

    def test_existing_mount_from_another_partition_fails_before_copy(self) -> None:
        selected_number = self.seed.stat().st_dev

        def observed_block_number(path: Path) -> int:
            return (selected_number if str(path).startswith('/dev/disk/by-partuuid/')
                    else selected_number + 1)

        with patch.object(verifier, "_command", side_effect=self._command), \
             patch.object(verifier, "_block_number", side_effect=observed_block_number), \
             patch.object(Path, "is_mount", return_value=True):
            with self.assertRaisesRegex(ValueError, "Mounted seed differs"):
                verifier.verify_seed(self.part_file, self.disk_file, self.seed)

    def test_changed_seed_payload_fails_before_copy(self) -> None:
        (self.seed / "preseed.cfg").write_text("changed\n")
        with self.assertRaisesRegex(ValueError, "changed"):
            self.verify()

    def test_agent_tamper_or_unbound_extra_fails_before_copy(self) -> None:
        (self.agent_dir / "agent.py").write_text("changed executable\n")
        with self.assertRaisesRegex(ValueError, "changed"):
            self.verify()
        (self.agent_dir / "agent.py").write_text("agent.py provider payload\n")
        (self.agent_dir / "extra.py").write_text("unreviewed executable\n")
        with self.assertRaisesRegex(ValueError, "inventory differs"):
            self.verify()

    def test_missing_agent_binding_fails_before_copy(self) -> None:
        self.identity["files"] = [record for record in self.identity["files"]
                                  if record["path"] != "igloo-agent/agent.py"]
        self.identity_file.write_text(json.dumps(self.identity))
        with self.assertRaisesRegex(ValueError, "agent file bindings"):
            self.verify()

    def test_mint_requires_its_three_additional_agent_files(self) -> None:
        manifest = self.seed / "migration-manifest.json"
        manifest.write_text(json.dumps({"schemaVersion": 1, "distroId": "linuxmint-cinnamon"}))
        self.identity["files"][0]["sha256"] = hashlib.sha256(manifest.read_bytes()).hexdigest()
        self.identity_file.write_text(json.dumps(self.identity))
        with self.assertRaisesRegex(ValueError, "inventory differs"):
            self.verify()
        for name in sorted(verifier.MINT_AGENT_FILES - verifier.COMMON_AGENT_FILES):
            payload = (name + " provider payload\n").encode()
            (self.agent_dir / name).write_bytes(payload)
            self.identity["files"].append({"path": "igloo-agent/" + name,
                                           "sha256": hashlib.sha256(payload).hexdigest()})
        self.identity_file.write_text(json.dumps(self.identity))
        self.verify()

    def test_only_identity_bound_redacted_retry_is_accepted(self) -> None:
        redacted = b"preseed.cfg redacted\n"
        (self.seed / "preseed.cfg").write_bytes(redacted)
        with self.assertRaisesRegex(ValueError, "changed"):
            self.verify()
        retry = self.state / "seed-retry"
        retry.mkdir(mode=0o700)
        journal = retry / "journal.json"
        journal.write_text(json.dumps({
            "identitySha256": hashlib.sha256(self.identity_file.read_bytes()).hexdigest(),
            "redactedHashes": {
                "migration-manifest.json": self.identity["files"][0]["sha256"],
                "preseed.cfg": hashlib.sha256(redacted).hexdigest(),
            },
        }))
        journal.chmod(0o600)
        self.verify()
        # The first scrub replacement is the manifest. A power loss at this
        # point leaves a redacted manifest and original installer config.
        original_preseed = b"preseed original\n"
        (self.seed / "preseed.cfg").write_bytes(original_preseed)
        redacted_manifest = json.dumps({"schemaVersion": 1, "distroId": "debian",
                                        "user": {"linuxPasswordCrypted": ""}}).encode()
        (self.seed / "migration-manifest.json").write_bytes(redacted_manifest)
        state = json.loads(journal.read_text())
        state["redactedHashes"]["migration-manifest.json"] = hashlib.sha256(
            redacted_manifest).hexdigest()
        journal.write_text(json.dumps(state))
        self.verify()
        journal.write_text(journal.read_text().replace("a", "b", 1))
        with self.assertRaisesRegex(ValueError, "differs"):
            self.verify()

    def test_selected_media_requires_exact_case_and_hash_before_copy(self) -> None:
        self.select_media("igloo-wallpaper.PNG", "igloo-avatar.jpg")
        self.assertEqual(["igloo-avatar.jpg", "igloo-wallpaper.PNG"], self.verify())
        (self.seed / "igloo-wallpaper.PNG").write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "changed"):
            self.verify()
        (self.seed / "igloo-wallpaper.PNG").write_bytes(b"selected wallpaper bytes")
        self.identity["files"] = [record for record in self.identity["files"]
                                  if record["path"] != "igloo-avatar.jpg"]
        self.identity_file.write_text(json.dumps(self.identity))
        with self.assertRaisesRegex(ValueError, "selected media bindings"):
            self.verify()

    def test_unselected_or_unsafe_media_fails_before_copy(self) -> None:
        (self.seed / "igloo-wallpaper.png").write_bytes(b"unselected")
        with self.assertRaisesRegex(ValueError, "unselected migration media"):
            self.verify()
        (self.seed / "igloo-wallpaper.png").unlink()
        self.select_media("igloo-wallpaper.PNG")
        manifest = json.loads((self.seed / "migration-manifest.json").read_text())
        manifest["wallpaper"]["fileName"] = "../igloo-wallpaper.PNG"
        payload = json.dumps(manifest).encode()
        (self.seed / "migration-manifest.json").write_bytes(payload)
        self.identity["files"][0]["sha256"] = hashlib.sha256(payload).hexdigest()
        self.identity_file.write_text(json.dumps(self.identity))
        with self.assertRaisesRegex(ValueError, "name is unsafe"):
            self.verify()

    def test_selected_user_data_requires_exact_nested_and_empty_file_hashes(self) -> None:
        empty, bookmarks = self.select_user_data()
        self.assertEqual([], self.verify())
        bookmarks.write_bytes(b"changed bookmarks")
        with self.assertRaisesRegex(ValueError, "changed"):
            self.verify()
        bookmarks.write_bytes(b"selected bookmarks")
        empty.write_bytes(b"no longer empty")
        with self.assertRaisesRegex(ValueError, "changed"):
            self.verify()

    def test_unselected_or_unbound_user_data_refuses_bootstrap(self) -> None:
        _, bookmarks = self.select_user_data()
        extra = bookmarks.parent / "extra.json"
        extra.write_bytes(b"unselected")
        with self.assertRaisesRegex(ValueError, "selected user-data bindings"):
            self.verify()
        extra.unlink()
        unselected = self.seed / "files/Pictures/extra.jpg"
        unselected.parent.mkdir()
        unselected.write_bytes(b"unselected")
        with self.assertRaisesRegex(ValueError, "unselected user-data directory"):
            self.verify()
        unselected.unlink()
        unselected.parent.rmdir()
        empty_extra = bookmarks.parent / "unbound-empty"
        empty_extra.mkdir()
        with self.assertRaisesRegex(ValueError, "unbound empty directory"):
            self.verify()
        empty_extra.rmdir()
        self.identity["files"] = [record for record in self.identity["files"]
                                  if record["path"] !=
                                  "browser-profiles/AppData/Local/Google/Chrome/User Data/Default/Bookmarks"]
        self.identity_file.write_text(json.dumps(self.identity))
        with self.assertRaisesRegex(ValueError, "selected user-data bindings"):
            self.verify()

    def test_changed_selected_browser_path_or_link_refuses_bootstrap(self) -> None:
        _, bookmarks = self.select_user_data()
        manifest = json.loads((self.seed / "migration-manifest.json").read_text())
        manifest["browsers"][0]["profileStagingPath"] += "."
        payload = json.dumps(manifest).encode()
        (self.seed / "migration-manifest.json").write_bytes(payload)
        self.identity["files"][0]["sha256"] = hashlib.sha256(payload).hexdigest()
        self.identity_file.write_text(json.dumps(self.identity))
        with self.assertRaisesRegex(ValueError, "differs from its selected source"):
            self.verify()
        manifest["browsers"][0]["profileStagingPath"] = (
            "browser-profiles/AppData/Local/Google/Chrome/User Data")
        payload = json.dumps(manifest).encode()
        (self.seed / "migration-manifest.json").write_bytes(payload)
        self.identity["files"][0]["sha256"] = hashlib.sha256(payload).hexdigest()
        self.identity_file.write_text(json.dumps(self.identity))
        bookmarks.unlink()
        bookmarks.symlink_to(self.seed / "preseed.cfg")
        with self.assertRaisesRegex(ValueError, "redirected entry"):
            self.verify()

    @unittest.skipIf(os.name == "nt", "Case-distinct files need a POSIX filesystem")
    def test_case_alias_inside_selected_profile_refuses_bootstrap(self) -> None:
        _, bookmarks = self.select_user_data()
        (bookmarks.parent / "bookmarks").write_bytes(b"case alias")
        with self.assertRaisesRegex(ValueError, "ambiguous or excessive"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
