"""Fedora credential retirement must bind to one exact GPT installer seed."""

import argparse
import ast
from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
import stat
import subprocess
import re
import sys
import unicodedata
import uuid
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest import TestCase, main, mock, skipUnless


SOURCE = Path(__file__).resolve().parents[2] / "distros/fedora-kde/agent/agent.py"
KICKSTART = Path(__file__).resolve().parents[2] / "distros/fedora-kde/kickstart/ks.cfg.template"
SPEC = importlib.util.spec_from_file_location("fedora_bound_seed_agent", SOURCE)
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)


class FedoraBoundSeedTest(TestCase):
    def test_invalid_bound_user_data_stops_before_firstboot_mutation(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest_path = root / "manifest.json"
            manifest_path.write_text(json.dumps({"schemaVersion": 1,
                                                 "distroId": "fedora-kde"}),
                                     encoding="utf-8")
            args = argparse.Namespace(import_credentials=False, fix_boot_order=False,
                                      manifest=manifest_path, log_dir=root)
            journal = root / "no-journal.json"
            with mock.patch.object(AGENT, "parse_args", return_value=args), \
                    mock.patch.object(AGENT, "configure_logging"), \
                    mock.patch.object(AGENT, "logger"), \
                    mock.patch.object(AGENT, "_installed_os_id", return_value="fedora"), \
                    mock.patch.object(AGENT, "_SCRUB_JOURNAL", journal), \
                    mock.patch.object(AGENT, "_verify_bound_seed_before_firstboot",
                                      side_effect=RuntimeError("selected user file changed")) as verify, \
                    mock.patch.object(AGENT, "enable_rpmfusion") as first_step:
                self.assertEqual(AGENT.main(), 1)
            verify.assert_called_once_with(manifest_path)
            first_step.assert_not_called()

    def test_kickstart_validator_checks_exact_selected_user_file_hashes(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            seed = root / "seed"
            seed.mkdir()
            device = root / "seed-partition"
            device.touch()
            agent_root = seed / "igloo-agent"
            agent_root.mkdir()
            names = ["migration-manifest.json", "ks.cfg"]
            for name in ("agent.py", "first-boot.sh", "display-apply.py",
                         "igloo_boot.py"):
                path = agent_root / name
                path.write_bytes(name.encode())
                names.append("igloo-agent/" + name)
            manifest = {"files": {"folders": [{"name": "Documents"}],
                                  "stagingPath": "files"}}
            (seed / "migration-manifest.json").write_text(json.dumps(manifest),
                                                            encoding="utf-8")
            (seed / "ks.cfg").write_bytes(b"# bound kickstart")
            user_file = seed / "files/Documents/notes.txt"
            user_file.parent.mkdir(parents=True)
            user_file.write_bytes(b"selected document")
            names.append("files/Documents/notes.txt")
            empty = seed / "files/Documents/empty.txt"
            empty.touch()
            names.append("files/Documents/empty.txt")
            disk_guid, part_guid = uuid.uuid4(), uuid.uuid4()
            identity = {"version": 1, "diskGuid": str(disk_guid),
                        "partitionGuid": str(part_guid),
                        "files": [{"path": name, "sha256": hashlib.sha256(
                            (seed / name).read_bytes()).hexdigest().upper()}
                            for name in names]}
            script = KICKSTART.read_text(encoding="utf-8").split(
                "<<'IGLOO_SEED_VALIDATOR'\n", 1)[1].split(
                    "\nIGLOO_SEED_VALIDATOR", 1)[0]
            compile(script, str(KICKSTART), "exec")
            original_read_text = Path.read_text

            def read_text(path, *args, **kwargs):
                if path.as_posix() == "/proc/cmdline":
                    return ""  # USB boot has no external seed hint.
                return original_read_text(path, *args, **kwargs)

            def read_id(argv, **kwargs):
                if argv[:3] == ["blkid", "-s", "PARTUUID"]:
                    return str(part_guid)
                if argv[:3] == ["blkid", "-s", "PTUUID"]:
                    return str(disk_guid)
                if argv[0] == "lsblk":
                    return "loop5\n"
                raise AssertionError(argv)

            def validate():
                (seed / "igloo-seed-identity.json").write_text(
                    json.dumps(identity), encoding="utf-8")
                output = io.StringIO()
                with mock.patch.object(sys, "argv", ["validator", str(device), str(seed)]), \
                        mock.patch.object(stat, "S_ISBLK", return_value=True), \
                        mock.patch.object(subprocess, "check_output", side_effect=read_id), \
                        mock.patch.object(Path, "read_text", read_text), \
                        redirect_stdout(output):
                    exec(compile(script, str(KICKSTART), "exec"), {})
                self.assertEqual(output.getvalue().strip(), str(disk_guid))

            validate()
            user_file.write_bytes(b"changed document")
            with self.assertRaisesRegex(RuntimeError, "differs from its bound identity"):
                validate()
            user_file.write_bytes(b"selected document")
            identity["files"] = [entry for entry in identity["files"]
                                 if entry["path"] != "files/Documents/empty.txt"]
            with self.assertRaisesRegex(RuntimeError, "user data differs from its identity"):
                validate()

    def test_selected_media_is_required_and_hash_verified_on_bound_seed(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            seed = root / "seed"
            seed.mkdir()
            agent_root = seed / "igloo-agent"
            agent_root.mkdir()
            manifest = {"wallpaper": {"fileName": "igloo-wallpaper.png"},
                        "accountPicture": {"fileName": "igloo-avatar.jpg"},
                        "files": {"folders": [{"name": "Documents"}],
                                  "stagingPath": "files"},
                        "browsers": [{"sourceRelativePath": "Google/Chrome/User Data",
                                      "profileStagingPath":
                                      "browser-profiles/Google/Chrome/User Data"}]}
            (seed / "migration-manifest.json").write_text(json.dumps(manifest),
                                                            encoding="utf-8")
            (seed / "ks.cfg").write_bytes(b"# bound kickstart")
            for name in ("agent.py", "first-boot.sh", "display-apply.py",
                         "igloo_boot.py"):
                (agent_root / name).write_bytes(name.encode())
            wallpaper = seed / "igloo-wallpaper.png"
            wallpaper.write_bytes(b"selected wallpaper")
            (seed / "igloo-avatar.jpg").write_bytes(b"selected avatar")
            document = seed / "files/Documents/notes.txt"
            document.parent.mkdir(parents=True)
            document.write_bytes(b"selected document")
            empty = seed / "files/Documents/empty.txt"
            empty.touch()
            bookmark = seed / "browser-profiles/Google/Chrome/User Data/Bookmarks"
            bookmark.parent.mkdir(parents=True)
            bookmark.write_bytes(b"selected bookmark")
            disk_guid, part_guid = uuid.uuid4(), uuid.uuid4()
            names = ("migration-manifest.json", "ks.cfg", "igloo-wallpaper.png",
                     "igloo-avatar.jpg", "files/Documents/notes.txt",
                     "files/Documents/empty.txt",
                     "browser-profiles/Google/Chrome/User Data/Bookmarks",
                     "igloo-agent/agent.py",
                     "igloo-agent/first-boot.sh", "igloo-agent/display-apply.py",
                     "igloo-agent/igloo_boot.py")
            identity = {
                "version": 1, "diskGuid": str(disk_guid),
                "partitionGuid": str(part_guid),
                "files": [{"path": name, "sha256": hashlib.sha256(
                    (seed / name).read_bytes()).hexdigest().upper()} for name in names],
            }
            protected = root / "protected-identity.json"

            def device_command(argv, **_kwargs):
                if argv[:3] == ["blkid", "-s", "PARTUUID"]:
                    return SimpleNamespace(stdout=str(part_guid))
                if argv[:3] == ["blkid", "-s", "PTUUID"]:
                    return SimpleNamespace(stdout=str(disk_guid))
                if argv[0] == "lsblk":
                    return SimpleNamespace(stdout="loop5\n")
                raise AssertionError(argv)

            def verify():
                payload = json.dumps(identity).encode()
                protected.write_bytes(payload)
                (seed / "igloo-seed-identity.json").write_bytes(payload)
                with mock.patch.object(AGENT.Path, "is_block_device", return_value=True), \
                     mock.patch.object(AGENT, "run_cmd", side_effect=device_command):
                    return AGENT._read_bound_seed_files(seed, "/dev/loop5p1", protected)

            self.assertEqual(set(verify()), {"migration-manifest.json", "ks.cfg"})
            document.write_bytes(b"tampered document")
            with self.assertRaisesRegex(RuntimeError, "Installer seed file changed"):
                verify()
            document.write_bytes(b"selected document")
            bookmark.write_bytes(b"tampered bookmark")
            with self.assertRaisesRegex(RuntimeError, "Installer seed file changed"):
                verify()
            bookmark.write_bytes(b"selected bookmark")
            identity["files"] = [entry for entry in identity["files"]
                                 if entry["path"] != "files/Documents/empty.txt"]
            with self.assertRaisesRegex(RuntimeError, "Selected installer user data differs"):
                verify()
            identity["files"].append({"path": "files/Documents/empty.txt",
                                      "sha256": hashlib.sha256(b"").hexdigest().upper()})
            extra = seed / "files/Documents/extra.txt"
            extra.write_bytes(b"unselected")
            with self.assertRaisesRegex(RuntimeError, "Selected installer user data differs"):
                verify()
            extra.unlink()
            wallpaper.write_bytes(b"tampered wallpaper")
            with self.assertRaisesRegex(RuntimeError, "Installer seed file changed"):
                verify()
            wallpaper.write_bytes(b"selected wallpaper")
            identity["files"] = [entry for entry in identity["files"]
                                 if entry["path"] != "igloo-avatar.jpg"]
            with self.assertRaisesRegex(RuntimeError, "Selected installer media differs"):
                verify()

    def test_selected_user_file_inventory_matches_kickstart_and_first_boot(self):
        with TemporaryDirectory() as temporary:
            seed = Path(temporary) / "seed"
            seed.mkdir()
            manifest = {
                "files": {"folders": [{"name": "Documents"}],
                          "stagingPath": "files"},
                "browsers": [{"sourceRelativePath": "Google/Chrome/User Data",
                              "profileStagingPath":
                              "browser-profiles/Google/Chrome/User Data"}],
            }
            document = seed / "files/Documents/readme.txt"
            document.parent.mkdir(parents=True)
            document.write_bytes(b"selected document")
            empty = seed / "files/Documents/empty.txt"
            empty.touch()
            bookmark = seed / "browser-profiles/Google/Chrome/User Data/Bookmarks"
            bookmark.parent.mkdir(parents=True)
            bookmark.write_bytes(b"selected bookmark")
            expected = {"files/Documents/readme.txt", "files/Documents/empty.txt",
                        "browser-profiles/Google/Chrome/User Data/Bookmarks"}

            script = KICKSTART.read_text(encoding="utf-8").split(
                "<<'IGLOO_SEED_VALIDATOR'\n", 1)[1].split(
                    "\nIGLOO_SEED_VALIDATOR", 1)[0]
            tree = ast.parse(script)
            names = {"safe_user_segment", "safe_user_path", "selected_user_paths"}
            functions = ast.Module(body=[node for node in tree.body
                                         if isinstance(node, ast.FunctionDef)
                                         and node.name in names], type_ignores=[])
            inline = {"seed": seed, "manifest": manifest, "re": re,
                      "unicodedata": unicodedata}
            exec(compile(functions, str(KICKSTART), "exec"), inline)

            def both():
                return (AGENT._selected_user_paths(seed, manifest),
                        inline["selected_user_paths"]())

            self.assertEqual(both(), (expected, expected))
            bookmark.write_bytes(b"")  # Empty selected files remain valid.
            self.assertEqual(both(), (expected, expected))

            selected_folders = manifest["files"]["folders"]
            manifest["files"]["folders"] = [
                {"name": f"Folder{index}"} for index in range(1025)]
            for verifier in (lambda: AGENT._selected_user_paths(seed, manifest),
                             inline["selected_user_paths"]):
                with self.assertRaisesRegex(RuntimeError, "root inventory is too large"):
                    verifier()
            manifest["files"]["folders"] = selected_folders

            extra = seed / "files/Documents/unselected.txt"
            extra.write_bytes(b"extra")
            # Inventory sees extra files. The identity equality check below
            # rejects them unless the Windows writer bound them.
            self.assertEqual(both(), (expected | {"files/Documents/unselected.txt"},) * 2)
            extra.unlink()
            unselected = seed / "files/Downloads"
            unselected.mkdir()
            for verifier in (lambda: AGENT._selected_user_paths(seed, manifest),
                             inline["selected_user_paths"]):
                with self.assertRaisesRegex(RuntimeError, "unselected user-data directory"):
                    verifier()
            unselected.rmdir()
            nested_empty = seed / "files/Documents/empty-dir"
            nested_empty.mkdir()
            for verifier in (lambda: AGENT._selected_user_paths(seed, manifest),
                             inline["selected_user_paths"]):
                with self.assertRaisesRegex(RuntimeError, "unbound empty directory"):
                    verifier()
            nested_empty.rmdir()

            # A selected but empty root is historically valid; an unstaged
            # historical NTFS-only selection has no new bound file records.
            manifest["browsers"] = []
            manifest["files"]["stagingPath"] = ""
            bookmark.unlink()
            bookmark.parent.rmdir()
            (seed / "browser-profiles/Google/Chrome").rmdir()
            (seed / "browser-profiles/Google").rmdir()
            (seed / "browser-profiles").rmdir()
            document.unlink()
            empty.unlink()
            self.assertEqual(both(), (set(), set()))
            (seed / "files/Documents").rmdir()
            (seed / "files").rmdir()
            self.assertEqual(both(), (set(), set()))

    def test_anaconda_copy_binding_rejects_changed_credential(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            kickstart = b"# bound seed\nuser --name=fixture --password=HASH_SECRET --iscrypted\n"
            (root / "original-ks.cfg").write_bytes(kickstart)
            generated = root / "anaconda-ks.cfg"
            generated.write_bytes(
                b"# Generated by Anaconda 44\n"
                b"user --name=fixture --password=DIFFERENT_HASH --iscrypted\n")
            manifest = {"user": {"preferredLinuxUsername": "fixture",
                                 "linuxPasswordCrypted": "HASH_SECRET"}}
            with self.assertRaisesRegex(RuntimeError, "wrong password hash"):
                AGENT._installed_anaconda_inputs(root, kickstart, manifest)

            generated.write_bytes(
                b"# Generated by Anaconda 44\n"
                b"user --name=fixture --password=HASH_SECRET --iscrypted\n")
            (root / "original-ks.cfg").write_bytes(kickstart + b"# modified\n")
            with self.assertRaisesRegex(RuntimeError, "differs from the bound seed"):
                AGENT._installed_anaconda_inputs(root, kickstart, manifest)

    def test_scrub_retry_does_not_rerun_completed_migration_steps(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest_path = root / "manifest.json"
            manifest = {"schemaVersion": 1, "distroId": "fedora-kde"}
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            journal = root / "seed-scrub-state.json"
            journal.write_text("hash-only", encoding="utf-8")
            args = argparse.Namespace(import_credentials=False, fix_boot_order=False,
                                      manifest=manifest_path, log_dir=root)
            with mock.patch.object(AGENT, "parse_args", return_value=args), \
                    mock.patch.object(AGENT, "configure_logging"), \
                    mock.patch.object(AGENT.platform, "freedesktop_os_release",
                                      return_value={"ID": "fedora"}), \
                    mock.patch.object(AGENT, "_SCRUB_JOURNAL", journal), \
                    mock.patch.object(AGENT, "redact_manifest") as scrub, \
                    mock.patch.object(AGENT, "enable_rpmfusion") as rpmfusion:
                self.assertEqual(AGENT.main(), 0)
            scrub.assert_called_once_with(manifest)
            rpmfusion.assert_not_called()

    @skipUnless(hasattr(os, "O_DIRECTORY"), "Linux launcher semantics are required")
    def test_launcher_keeps_retry_inputs_on_failure_and_skips_rerun_after_done(self):
        launcher_source = (SOURCE.parent / "first-boot.sh").read_text(encoding="utf-8")
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            log_dir = root / "log"
            state = root / "state"
            agent_dir = root / "agent"
            state.mkdir()
            agent_dir.mkdir()
            manifest = state / "manifest.json"
            manifest.write_text("{}", encoding="utf-8")
            journal = state / "seed-scrub-state.json"
            journal.write_text("hash-only", encoding="utf-8")
            invoked = state / "invoked"
            agent = agent_dir / "agent.py"
            launcher = root / "first-boot.sh"
            for source_path, target_path in (
                ("/var/log/igloo", log_dir),
                ("/var/lib/igloo/manifest.json", manifest),
                ("/var/lib/igloo/.done", state / ".done"),
                ("/var/lib/igloo/.reboot-required", state / ".reboot-required"),
                ("/var/lib/igloo/seed-scrub-state.json", journal),
                ("/opt/igloo", agent_dir),
            ):
                launcher_source = launcher_source.replace(source_path, str(target_path))
            launcher.write_text(launcher_source, encoding="utf-8")

            agent.write_text("raise SystemExit(7)\n", encoding="utf-8")
            failed = subprocess.run(["bash", str(launcher)], capture_output=True,
                                    text=True, check=False)
            self.assertEqual(failed.returncode, 7)
            self.assertFalse((state / ".done").exists())
            self.assertTrue(journal.exists())

            agent.write_text(f"from pathlib import Path\n"
                             f"p=Path({str(invoked)!r})\n"
                             "p.write_text(p.read_text() + 'x' if p.exists() else 'x')\n",
                             encoding="utf-8")
            succeeded = subprocess.run(["bash", str(launcher)], capture_output=True,
                                       text=True, check=False)
            self.assertEqual(succeeded.returncode, 0, succeeded.stderr)
            self.assertEqual((state / ".done").read_bytes(), b"complete\n")
            self.assertFalse(journal.exists())
            self.assertEqual(invoked.read_text(), "x")

            repeated = subprocess.run(["bash", str(launcher)], capture_output=True,
                                      text=True, check=False)
            self.assertEqual(repeated.returncode, 0, repeated.stderr)
            self.assertEqual(invoked.read_text(), "x")

    @skipUnless(hasattr(os, "O_DIRECTORY"), "Linux directory fsync is required")
    def test_interrupted_scrub_resumes_only_on_original_gpt_seed(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            seed = root / "seed"
            protected = root / "protected"
            seed.mkdir()
            protected.mkdir()
            manifest = {
                "schemaVersion": 1, "distroId": "fedora-kde",
                "user": {"preferredLinuxUsername": "fixture",
                         "linuxPasswordCrypted": "HASH_SECRET"},
                "wifiNetworks": [{"ssid": "lab", "psk": "PSK_SECRET"}],
                "browsers": [{"credentialsBlob": "BLOB_SECRET"}],
            }
            original = json.dumps(manifest).encode()
            (seed / "migration-manifest.json").write_bytes(original)
            (seed / "ks.cfg").write_bytes(b"# HASH_SECRET\n" + b"line\n" * 20)
            agent_root = seed / "igloo-agent"
            agent_root.mkdir()
            agent_names = ("agent.py", "first-boot.sh", "display-apply.py",
                           "igloo_boot.py")
            for name in agent_names:
                (agent_root / name).write_bytes(("bound " + name).encode())
            anaconda = root / "anaconda"
            anaconda.mkdir()
            (anaconda / "original-ks.cfg").write_bytes((seed / "ks.cfg").read_bytes())
            (anaconda / "anaconda-ks.cfg").write_bytes(
                b"# Generated by Anaconda 44\n"
                b"user --name=fixture --password=HASH_SECRET --iscrypted\n")
            installed = protected / "manifest.json"
            installed.write_bytes(original)
            disk_guid, part_guid = uuid.uuid4(), uuid.uuid4()
            identity = {
                "version": 1, "diskGuid": str(disk_guid),
                "partitionGuid": str(part_guid),
                "files": [{"path": name, "sha256": hashlib.sha256(
                    (seed / name).read_bytes()).hexdigest().upper()}
                    for name in ("migration-manifest.json", "ks.cfg", *
                                 ("igloo-agent/" + item for item in agent_names))],
            }
            identity_bytes = json.dumps(identity).encode()
            identity_path = protected / "seed-identity.json"
            identity_path.write_bytes(identity_bytes)
            (seed / "igloo-seed-identity.json").write_bytes(identity_bytes)
            journal = protected / "seed-scrub-state.json"

            def device_command(argv, **_kwargs):
                if argv[:2] == ["sync", "-f"]:
                    return SimpleNamespace(stdout="")
                if argv[:3] == ["blkid", "-s", "PARTUUID"]:
                    return SimpleNamespace(stdout=str(part_guid))
                if argv[:3] == ["blkid", "-s", "PTUUID"]:
                    return SimpleNamespace(stdout=str(disk_guid))
                if argv[0] == "lsblk":
                    return SimpleNamespace(stdout="loop5\n")
                raise AssertionError(argv)

            replace = AGENT._replace_bound_file_with_readback

            def interrupted(path, expected, replacement, *, private):
                if path.name == "migration-manifest.json":
                    raise OSError("injected interruption")
                replace(path, expected, replacement, private=private)

            with mock.patch.object(AGENT.Path, "is_block_device", return_value=True), \
                    mock.patch.object(AGENT, "run_cmd", side_effect=device_command), \
                    mock.patch.object(AGENT, "_replace_bound_file_with_readback",
                                      side_effect=interrupted):
                with self.assertRaisesRegex(OSError, "injected interruption"):
                    AGENT._scrub_bound_seed_inputs(installed, identity_path, seed,
                        "/dev/loop5p1", manifest, journal, anaconda)
            self.assertTrue(journal.exists())
            self.assertNotIn(b"HASH_SECRET", (seed / "ks.cfg").read_bytes())
            self.assertIn(b"HASH_SECRET", installed.read_bytes())
            self.assertNotIn(b"HASH_SECRET", journal.read_bytes())

            with mock.patch.object(AGENT.Path, "is_block_device", return_value=True), \
                    mock.patch.object(AGENT, "run_cmd", side_effect=device_command):
                AGENT._scrub_bound_seed_inputs(installed, identity_path, seed,
                    "/dev/loop5p1", manifest, journal, anaconda)
            for path in (seed / "ks.cfg", seed / "migration-manifest.json", installed,
                         anaconda / "original-ks.cfg", anaconda / "anaconda-ks.cfg"):
                payload = path.read_bytes()
                for secret in (b"HASH_SECRET", b"PSK_SECRET", b"BLOB_SECRET"):
                    self.assertNotIn(secret, payload)

            # Even after a journaled secret scrub, no root-executed agent file
            # may be replaced or added on OEMDRV before first-boot retirement.
            (agent_root / "agent.py").write_bytes(b"tampered agent")
            with mock.patch.object(AGENT.Path, "is_block_device", return_value=True), \
                    mock.patch.object(AGENT, "run_cmd", side_effect=device_command):
                with self.assertRaisesRegex(RuntimeError, "Installer seed file changed"):
                    AGENT._read_bound_seed_files(seed, "/dev/loop5p1", identity_path,
                        json.loads(journal.read_bytes())["replacements"])
            (agent_root / "agent.py").write_bytes(b"bound agent.py")
            (agent_root / "unbound.py").write_bytes(b"print('unbound')")
            with mock.patch.object(AGENT.Path, "is_block_device", return_value=True), \
                    mock.patch.object(AGENT, "run_cmd", side_effect=device_command):
                with self.assertRaisesRegex(RuntimeError, "agent inventory differs"):
                    AGENT._read_bound_seed_files(seed, "/dev/loop5p1", identity_path,
                        json.loads(journal.read_bytes())["replacements"])
            (agent_root / "unbound.py").unlink()

            # A replacement disk must be rejected even when it has the same
            # OEMDRV label and the copied identity file byte-matches.
            def wrong_disk(argv, **kwargs):
                if argv[:3] == ["blkid", "-s", "PARTUUID"]:
                    return SimpleNamespace(stdout=str(uuid.uuid4()))
                return device_command(argv, **kwargs)

            with mock.patch.object(AGENT.Path, "is_block_device", return_value=True), \
                    mock.patch.object(AGENT, "run_cmd", side_effect=wrong_disk):
                with self.assertRaisesRegex(RuntimeError, "GPT disk or partition"):
                    AGENT._read_bound_seed_files(seed, "/dev/loop5p1", identity_path,
                        json.loads(journal.read_bytes())["replacements"])


if __name__ == "__main__":
    main()
