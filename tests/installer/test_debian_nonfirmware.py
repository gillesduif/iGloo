"""Native Linux primitives on disposable ordinary directories, never block devices."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "distros/debian/native"))
import target_files as files
import target_observer as observer
import runtime_profile as runtime
import debian_first_boot as worker
import offline_bundle as bundle


class TargetFileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="igloo-target-files-")
        self.root = Path(self.temporary.name)
        (self.root / "etc").mkdir()
        (self.root / "usr").mkdir()
        self.fd = os.open(self.root, os.O_RDONLY | os.O_DIRECTORY)
        self.writer = files.TargetFiles(self.fd, os.fstat(self.fd).st_dev, files.mount_id(self.fd), os.getuid(), os.getgid())

    def tearDown(self):
        self.writer.close()
        os.close(self.fd)
        self.temporary.cleanup()

    def test_atomic_write_and_independent_readback(self):
        receipt = self.writer.apply("/etc/hostname", "Utf8File", "workstation\n", 0o644, {"Kind": "Absent"})
        self.assertEqual(receipt, self.writer.observe("/etc/hostname"))
        self.assertEqual(receipt["Sha256"], worker.digest(b"workstation\n"))
        self.assertEqual(receipt["Owner"], os.getuid())

    def test_wrong_device(self):
        with self.assertRaises(files.Rejected):
            files.TargetFiles(self.fd, os.fstat(self.fd).st_dev + 1, files.mount_id(self.fd))

    def test_wrong_mount(self):
        with self.assertRaises(files.Rejected):
            files.TargetFiles(self.fd, os.fstat(self.fd).st_dev, files.mount_id(self.fd) + 1)

    def test_parent_symlink_escape(self):
        (self.root / "etc/escape").symlink_to("../usr")
        with self.assertRaises(OSError):
            self.writer.apply("/etc/escape/file", "Utf8File", "bad", 0o644, {"Kind": "Absent"})
        self.assertFalse((self.root / "usr/file").exists())

    def test_final_symlink_substitution(self):
        (self.root / "etc/hostname").symlink_to("../usr/file")
        with self.assertRaises(files.Rejected):
            self.writer.apply("/etc/hostname", "Utf8File", "bad", 0o644, self.writer.observe("/etc/hostname"))

    def test_hardlink_rejected(self):
        (self.root / "etc/a").write_text("unchanged")
        os.link(self.root / "etc/a", self.root / "etc/b")
        with self.assertRaises(files.Rejected):
            self.writer.observe("/etc/a")

    def test_stale_before_state(self):
        (self.root / "etc/hostname").write_text("changed")
        with self.assertRaises(files.Rejected):
            self.writer.apply("/etc/hostname", "Utf8File", "new", 0o644, {"Kind": "Absent"})
        self.assertEqual((self.root / "etc/hostname").read_text(), "changed")

    def test_writable_configuration_mode_rejected(self):
        with self.assertRaises(files.Rejected):
            self.writer.apply("/etc/hostname", "Utf8File", "bad", 0o777, {"Kind": "Absent"})

    def test_no_esp_or_payload_or_path_escape(self):
        for path in ("/boot/efi/file", "/run/igloo-source/file", "/etc/../escape", "etc/file", "/etc//file", "/dev/sda"):
            with self.subTest(path=path), self.assertRaises(files.Rejected):
                self.writer.observe(path)

    def test_fsync_failure_not_success(self):
        with patch.object(files.os, "fsync", side_effect=OSError("fixture")), self.assertRaises(OSError):
            self.writer.apply("/etc/hostname", "Utf8File", "new", 0o644, {"Kind": "Absent"})
        self.assertFalse((self.root / "etc/hostname").exists())
        self.assertEqual(len(list((self.root / "etc").glob(".igloo-*"))), 1)

    def test_exact_link_and_cleanup(self):
        self.writer.apply("/etc/localtime", "SymbolicLink", "/usr/share/zoneinfo/Europe/Brussels", 0o777, {"Kind": "Absent"})
        before = self.writer.observe("/etc/localtime")
        self.assertEqual(before["Kind"], "SymbolicLink")
        self.writer.apply("/etc/localtime", "MustBeAbsent", None, 0, before)
        self.assertEqual(self.writer.observe("/etc/localtime"), {"Kind": "Absent"})

    def test_no_unsafe_symlink_target(self):
        with self.assertRaises(files.Rejected):
            self.writer.apply("/etc/file", "SymbolicLink", "/sys/firmware", 0o777, {"Kind": "Absent"})

    def test_native_independent_package_database(self):
        (self.root / "etc/status").write_text("Package: fixture\nStatus: install ok installed\nPriority: optional\nSection: misc\nInstalled-Size: 1\nMaintainer: Fixture <test@example.invalid>\nArchitecture: amd64\nVersion: 1.0\nDescription: fixture\n\n")
        directory = os.open(self.root / "etc", os.O_RDONLY | os.O_DIRECTORY)
        try:
            packages = observer.package_state(directory)
            self.assertEqual(packages[0]["DpkgStatus"], "install ok installed")
            self.assertEqual(packages[0]["Version"], "1.0")
        finally:
            os.close(directory)

    def test_independent_boot_artifact_survives_failed_command(self):
        (self.root / "etc/initrd.img-fixture").write_bytes(b"partial but present")
        directory = os.open(self.root / "etc", os.O_RDONLY | os.O_DIRECTORY)
        try:
            self.assertEqual(observer.artifact(directory, "initrd.img-fixture")["Length"], 19)
            with self.assertRaises(FileNotFoundError):
                observer.artifact(directory, "missing")
        finally:
            os.close(directory)


class FirstBootTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="igloo-firstboot-")
        self.root = Path(self.temp.name)
        for name in ("inputs", "state"):
            (self.root / name).mkdir(mode=0o700)
        self.inputs = os.open(self.root / "inputs", os.O_RDONLY | os.O_DIRECTORY)
        self.state = os.open(self.root / "state", os.O_RDONLY | os.O_DIRECTORY)
        self.payload = (ROOT / "distros/debian/native/debian_first_boot.py").read_bytes()
        self.generation = str(uuid.uuid4())
        self.content = {"SchemaVersion": 1, "GenerationId": self.generation, "Kind": "DeploymentContent",
                        "State": "AppliedAndVerified", "EvidenceSha256": "A" * 64}
        self.write_receipt(self.content)

    def write_receipt(self, value):
        data = json.dumps(value).encode()
        (self.root / "inputs/content.json").write_bytes(data)
        self.config = {"SchemaVersion": 1, "GenerationId": self.generation, "Profile": worker.PROFILE,
                       "WorkerSha256": worker.digest(self.payload), "RequiredReceipts": [
                           {"Kind": "DeploymentContent", "FileName": "content.json", "Sha256": worker.digest(data)}]}

    def tearDown(self):
        os.close(self.inputs); os.close(self.state)
        self.temp.cleanup()

    def run_worker(self):
        return worker.execute(json.dumps(self.config).encode(), self.payload, self.inputs, self.state, self.generation, input_owner=os.getuid())

    def outcome(self):
        return json.loads((self.root / "state/outcome.json").read_bytes())

    def test_success_is_bound_to_exact_receipts(self):
        self.assertEqual(self.run_worker(), 0)
        self.assertEqual(self.outcome()["State"], "FirstBootSucceeded")
        self.assertEqual(self.outcome()["VerifiedReceipts"], self.config["RequiredReceipts"])

    def test_required_user_data_cannot_disappear(self):
        self.config["RequiredReceipts"].append({"Kind": "UserData", "FileName": "user-data.json", "Sha256": "B" * 64})
        self.assertEqual(self.run_worker(), 1)
        self.assertEqual(self.outcome()["State"], "FirstBootFailed")

    def test_changed_receipt(self):
        (self.root / "inputs/content.json").write_text("substitution")
        self.assertEqual(self.run_worker(), 1)
        self.assertEqual(self.outcome()["State"], "FirstBootFailed")

    def test_generation_mismatch(self):
        self.write_receipt({**self.content, "GenerationId": str(uuid.uuid4())})
        self.assertEqual(self.run_worker(), 1)

    def test_partial_receipt_rejected(self):
        self.write_receipt({**self.content, "State": "OutcomeUnknown"})
        self.assertEqual(self.run_worker(), 1)

    def test_worker_hash_mismatch(self):
        self.config["WorkerSha256"] = "B" * 64
        with self.assertRaises(worker.Rejected):
            self.run_worker()
        self.assertFalse((self.root / "state/intent.json").exists())

    def test_symlink_input(self):
        (self.root / "inputs/actual.json").write_bytes((self.root / "inputs/content.json").read_bytes())
        (self.root / "inputs/content.json").unlink()
        (self.root / "inputs/content.json").symlink_to("actual.json")
        self.assertEqual(self.run_worker(), 1)

    def test_world_writable_input(self):
        os.chmod(self.root / "inputs/content.json", 0o666)
        self.assertEqual(self.run_worker(), 1)

    def test_no_blind_retry_or_overwrite(self):
        self.assertEqual(self.run_worker(), 0)
        original = (self.root / "state/outcome.json").read_bytes()
        with self.assertRaises(FileExistsError):
            self.run_worker()
        self.assertEqual((self.root / "state/outcome.json").read_bytes(), original)

    def test_intent_fsync_failure_prevents_work(self):
        with patch.object(worker.os, "fsync", side_effect=OSError("fixture")), self.assertRaises(OSError):
            self.run_worker()
        self.assertFalse((self.root / "state/outcome.json").exists())

    def test_no_privileged_or_external_commands(self):
        import ast
        tree = ast.parse(self.payload)
        imports = {n.names[0].name for n in ast.walk(tree) if isinstance(n, ast.Import)}
        self.assertFalse(imports & {"subprocess", "socket", "shutil", "ctypes"})
        self.assertNotIn("os.walk", self.payload.decode())


class RuntimeProfileTests(unittest.TestCase):
    def test_helper_tree_changes_detected(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name); (root / "functions").write_text("one")
            before = runtime.tree_identity(root)
            (root / "functions").write_text("two")
            self.assertNotEqual(runtime.tree_identity(root), before)

    def test_helper_symlink_escape_rejected(self):
        with tempfile.TemporaryDirectory() as name:
            (Path(name) / "functions").symlink_to("/etc/passwd")
            with self.assertRaises(ValueError):
                runtime.tree_identity(name)

    def test_runtime_hash_changed_rejected(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name) / "tool"; path.write_text("one")
            profile = runtime.capture([str(path)], ["dpkg"])
            self.assertEqual(len(runtime.verify(profile)), 64)
            path.write_text("two")
            with self.assertRaises(ValueError):
                runtime.verify(profile)

    def test_bootstrap_download_uses_captured_file_transport(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            data = b"deb fixture"
            path = "pool/main/i/igloo-base.deb"
            (root / "debian/pool/main/i").mkdir(parents=True)
            (root / "debian" / path).write_bytes(data)
            request = {"Repositories": [{"Id": "debian", "Suite": "trixie", "OriginUri": "https://example.invalid/debian"}],
                       "BootstrapPackages": ["igloo-base"], "DebootstrapPath": "/fixture/debootstrap"}
            metadata = [("debian", {"Package": "igloo-base", "Version": "1.0", "Architecture": "amd64",
                                    "Filename": path, "Size": str(len(data)), "SHA256": bundle.digest(data)})]
            calls = []
            def invoke(args, **_):
                calls.append(args)
                if args[0] == "/fixture/debootstrap":
                    self.assertEqual(args[-1], (root / "debian").as_uri())
                    archives = Path(args[-2]) / "var/cache/apt/archives"
                    archives.mkdir(parents=True)
                    (archives / "igloo-base.deb").write_bytes(data)
                    return b""
                return b"Package: igloo-base\nVersion: 1.0\nArchitecture: amd64\n"
            with patch.object(bundle, "run", side_effect=invoke), patch.object(bundle, "fetch", side_effect=AssertionError("No refetch")):
                result = bundle.bootstrap_archives(root, request, root / "keyring", metadata, True)
            self.assertEqual(len(result), 1)
            self.assertEqual(len(calls), 2)

    def test_runtime_observer_substitution_is_rejected(self):
        with self.assertRaisesRegex(bundle.Rejected, "RuntimeObserverToolChanged"):
            bundle.runtime_tool({"RuntimeProfileToolSha256": "0" * 64})

    def test_native_credential_seal_requirement_supported_by_kernel(self):
        import fcntl
        fd = os.memfd_create("igloo-credential-fixture", os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING)
        try:
            os.write(fd, b"synthetic fixture only")
            fcntl.fcntl(fd, fcntl.F_ADD_SEALS, fcntl.F_SEAL_SEAL | fcntl.F_SEAL_SHRINK | fcntl.F_SEAL_GROW | fcntl.F_SEAL_WRITE)
            self.assertEqual(fcntl.fcntl(fd, fcntl.F_GET_SEALS) & 15, 15)
            with self.assertRaises(PermissionError):
                os.write(fd, b"substitution")
        finally:
            os.close(fd)


if __name__ == "__main__":
    unittest.main()
