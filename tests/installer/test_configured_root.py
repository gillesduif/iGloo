"""Artifact mechanics in newly allocated temporary directories, never target disks."""
import base64
import copy
import datetime as dt
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "distros/debian/native"))
import configured_root as root
import deployment_journal


def entry(path, kind="Directory", data=b"", target=None, mode=None):
    return {"Path": path, "Type": kind, "Uid": os.getuid(), "Gid": os.getgid(),
            "Mode": mode if mode is not None else 0o777 if kind == "SymbolicLink" else 0o755 if kind == "Directory" else 0o644,
            "Length": len(data) if kind == "File" else 0,
            "Sha256": root.digest(data) if kind == "File" else None, "Target": target, "Xattrs": {}}


def fixture():
    directories = ["/", "/boot", "/boot/efi", "/dev", "/etc", "/etc/NetworkManager", "/home", "/proc",
                   "/root", "/run", "/sys", "/tmp", "/usr", "/usr/bin", "/usr/sbin", "/usr/lib", "/usr/lib64",
                   "/usr/share", "/var", "/var/lib", "/var/lib/dbus", "/var/lib/dpkg", "/var/lib/dpkg/updates"]
    contents = {"/etc/machine-id": b"", "/etc/fstab": b"", "/etc/shadow": b"root:!:20000:0:99999:7:::\n",
                "/usr/share/demo": b"deliberately incomplete synthetic Debian fixture\n",
                "/var/lib/dpkg/status": b"Package: fixture-base\nStatus: install ok installed\nPriority: required\nArchitecture: all\nVersion: 1.0\nMaintainer: Fixture <fixture@example.invalid>\nDescription: not a Debian installation\n\n"}
    entries = [entry(p) for p in directories] + [entry(p, "File", data) for p, data in contents.items()]
    entries += [entry(p, "SymbolicLink", target=t) for p, t in {
        "/bin": "usr/bin", "/sbin": "usr/sbin", "/lib": "usr/lib", "/lib64": "usr/lib64",
        "/etc/resolv.conf": "/run/NetworkManager/resolv.conf", "/var/lib/dbus/machine-id": "/etc/machine-id"}.items()]
    entries.append(entry("/usr/share/demo-link", "HardLink", target="/usr/share/demo"))
    return {"SchemaVersion": 1, "BuildId": str(uuid.uuid4()), "Format": root.FORMAT,
            "MachineNeutralPolicy": root.NEUTRAL, "Entries": sorted(entries, key=lambda e: e["Path"]),
            "Packages": [{"Name": "fixture-base", "Version": "1.0", "Architecture": "all", "DpkgStatus": "install ok installed"}]}, contents


class ConfiguredRootTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="igloo-configured-root-")
        self.base = Path(self.temporary.name)
        self.target = self.base / "owned-fixture"
        self.target.mkdir(mode=0o700)
        self.outside = self.base / "preserved-windows-fixture"
        self.outside.mkdir()
        (self.outside / "sentinel").write_bytes(b"unchanged")
        self.fd = os.open(self.target, os.O_RDONLY | os.O_DIRECTORY)
        self.view = root.RootView(self.fd, os.fstat(self.fd).st_dev, os.fstat(self.fd).st_ino, root.mount_id(self.fd))
        self.manifest, self.contents = fixture()
        self.store = self.base / "journal"
        self.store.mkdir(mode=0o700)
        self.generation = str(uuid.uuid4())
        self.plan = "A" * 64
        self.journal = root.ImportJournal(str(self.store), self.generation, self.plan, self.manifest["BuildId"])
        self.now = dt.datetime(2026, 9, 28, tzinfo=dt.timezone.utc)

    def tearDown(self):
        self.view.close()
        os.close(self.fd)
        self.assertEqual(b"unchanged", (self.outside / "sentinel").read_bytes())
        self.temporary.cleanup()

    def source(self):
        data = root.canonical(self.manifest)
        content = root.MAGIC + b"".join(self.contents[e["Path"]] for e in self.manifest["Entries"] if e["Type"] == "File")
        (self.base / "root.content").write_bytes(content)
        pin = root.DevelopmentPin(self.manifest["BuildId"], root.digest(data), root.digest(content), self.now + dt.timedelta(days=1))
        return data, pin

    def run_import(self, data=None, pin=None):
        if data is None:
            data, pin = self.source()
        descriptor = os.open(self.base / "root.content", os.O_RDONLY | os.O_NOFOLLOW)
        try:
            return root.import_development_fixture(self.view, data, descriptor, pin, self.journal,
                                                   self.generation, self.plan, self.now)
        finally:
            os.close(descriptor)

    def add(self, item, content=None):
        self.manifest["Entries"].append(item)
        self.manifest["Entries"].sort(key=lambda e: e["Path"])
        if content is not None:
            self.contents[item["Path"]] = content

    def record_outcome(self):
        names = sorted((self.store / self.generation).glob("*.json"))
        return json.loads(names[-1].read_bytes())["Outcome"]

    def test_full_mechanics_and_independent_dpkg_readback(self):
        result = self.run_import()
        self.assertEqual("AppliedAndVerified", result["Outcome"])
        self.assertEqual("MechanicsFixtureOnly", result["Qualification"])
        self.assertEqual("AppliedAndVerified", self.record_outcome())
        self.assertEqual(root.digest(root.canonical(self.manifest["Packages"])), root.verify_dpkg(self.view, self.manifest))
        self.assertEqual("usr/bin", os.readlink(self.target / "bin"))
        self.assertEqual(2, (self.target / "usr/share/demo").stat().st_nlink)
        self.assertEqual((self.target / "usr/share/demo").stat().st_ino, (self.target / "usr/share/demo-link").stat().st_ino)
        self.assertFalse(list((self.target / "boot/efi").iterdir()))

    def test_manifest_deterministic(self):
        self.assertEqual(root.canonical(self.manifest), root.canonical(copy.deepcopy(self.manifest)))

    def test_boolean_version_is_not_integer_version(self):
        self.manifest["SchemaVersion"] = True
        with self.assertRaises(root.Rejected):
            root.validate_manifest(root.canonical(self.manifest))

    def test_unbounded_directory_depth(self):
        with self.assertRaises(root.Rejected):
            root.path_parts("/" + "/".join(["a"] * 129))

    def test_semantic_pack_reopen_matches_source_stream(self):
        data, pin = self.source()
        self.run_import(data, pin)
        path = self.base / "repacked.content"
        descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        try:
            result = root.pack_verified_fixture(self.view, data, descriptor)
        finally:
            os.close(descriptor)
        self.assertEqual(pin.content_sha256, result["Sha256"])
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
        try:
            self.assertEqual(self.manifest, root.verify_source(data, descriptor, pin, self.now))
        finally:
            os.close(descriptor)

    def test_repack_rejects_changed_package_state(self):
        self.run_import()
        path = self.target / "var/lib/dpkg/status"
        path.write_bytes(path.read_bytes().replace(b"Version: 1.0", b"Version: 2.0"))
        descriptor = os.open(self.base / "new.content", os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        try:
            with self.assertRaises(root.Rejected):
                root.pack_verified_fixture(self.view, root.canonical(self.manifest), descriptor)
            self.assertEqual(0, os.fstat(descriptor).st_size)
        finally:
            os.close(descriptor)

    def test_post_import_observer_fresh_process(self):
        import subprocess
        data, _ = self.source()
        self.run_import()
        (self.base / "manifest.json").write_bytes(data)
        # A fresh trusted Python process independently reopens ordinary fixture FDs.
        # No command is executed inside the imported root or with target credentials.
        source = """import os,sys,pathlib
sys.path.insert(0,sys.argv[1])
import configured_root as r
m=r.validate_manifest(pathlib.Path(sys.argv[3]).read_bytes())
fd=os.open(sys.argv[2],os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
v=r.RootView(fd,os.fstat(fd).st_dev,os.fstat(fd).st_ino,r.mount_id(fd))
try: print(r.verify_tree(v,m),r.verify_dpkg(v,m),r.verify_neutral(v,m))
finally: v.close();os.close(fd)
"""
        observed = subprocess.run([sys.executable, "-I", "-B", "-c", source,
                                   str(Path(root.__file__).parent), str(self.target), str(self.base / "manifest.json")],
                                  stdin=subprocess.DEVNULL, capture_output=True, check=False, timeout=30)
        self.assertEqual(0, observed.returncode, observed.stderr.decode())
        self.assertEqual(3, len(observed.stdout.decode().split()))

    def test_modes_and_user_xattrs(self):
        item = next(e for e in self.manifest["Entries"] if e["Path"] == "/usr/share/demo")
        item["Xattrs"] = {"user.igloo-fixture": base64.b64encode(b"declared").decode()}
        # This profile deliberately does not permit hardlinks on xattr-bearing files.
        self.manifest["Entries"] = [e for e in self.manifest["Entries"] if e["Type"] != "HardLink"]
        self.run_import()
        self.assertEqual(b"declared", os.getxattr(self.target / "usr/share/demo", "user.igloo-fixture"))

    def test_exact_declared_capability_only(self):
        item = entry("/usr/bin/ping", "File", b"not executed", mode=0o755)
        item["Uid"], item["Gid"] = 0, 0
        value = struct.pack("<IIIII", 0x02000001, 1 << 13, 0, 0, 0)
        item["Xattrs"] = {"security.capability": base64.b64encode(value).decode()}
        self.assertEqual(value, root.attributes(item)["security.capability"])
        item["Path"] = "/usr/bin/other"
        with self.assertRaises(root.Rejected):
            root.attributes(item)

    def test_no_secret_or_machine_generation_in_generic_manifest(self):
        self.assertNotIn(self.generation.encode(), root.canonical(self.manifest))
        self.assertNotIn(b"Password", root.canonical(self.manifest))

    def test_intent_reopened_before_first_write(self):
        original = root.import_files
        def checked(*args):
            self.assertEqual("IntentDurable", self.record_outcome())
            self.assertEqual([], os.listdir(self.target))
            return original(*args)
        with patch.object(root, "import_files", side_effect=checked):
            self.run_import()

    def test_no_blind_reimport(self):
        self.run_import()
        with self.assertRaises(root.Rejected):
            self.run_import()

    def test_existing_root_entry_blocks_before_intent(self):
        (self.target / "unknown").write_text("preserve")
        with self.assertRaises(root.Rejected):
            self.run_import()
        self.assertFalse((self.store / self.generation).exists())
        self.assertEqual("preserve", (self.target / "unknown").read_text())

    def test_root_replacement(self):
        self.target.rename(self.base / "old-root")
        self.target.mkdir()
        with self.assertRaises(root.Rejected):
            self.run_import()
        self.assertEqual([], list(self.target.iterdir()))

    def test_mount_witness_changed(self):
        with patch.object(root, "mount_id", return_value=self.view.expected[2] + 1):
            with self.assertRaises(root.Rejected):
                self.run_import()

    def test_child_mount_substitution(self):
        import io
        original = open
        def changed(path, *args, **kwargs):
            if path == "/proc/self/mountinfo":
                return io.StringIO("2 1 1:1 / " + str(self.target / "boot/efi") + " rw - ext4 /dev/fixture rw\n")
            return original(path, *args, **kwargs)
        with patch("builtins.open", side_effect=changed):
            with self.assertRaises(root.Rejected):
                self.view.check()

    def test_generation_mismatch(self):
        self.generation = str(uuid.uuid4())
        with self.assertRaises(root.Rejected):
            self.run_import()

    def test_truncated_stream(self):
        data, pin = self.source()
        (self.base / "root.content").write_bytes((self.base / "root.content").read_bytes()[:-1])
        with self.assertRaises(root.Rejected):
            self.run_import(data, pin)
        self.assertEqual([], os.listdir(self.target))

    def test_modified_blob(self):
        data, pin = self.source()
        value = bytearray((self.base / "root.content").read_bytes())
        value[-3] ^= 1
        (self.base / "root.content").write_bytes(value)
        with self.assertRaises(root.Rejected):
            self.run_import(data, pin)

    def test_wrong_content_pin(self):
        data, pin = self.source()
        pin = root.DevelopmentPin(pin.build_id, pin.manifest_sha256, "B" * 64, pin.expires_at)
        with self.assertRaises(root.Rejected):
            self.run_import(data, pin)

    def test_wrong_manifest_pin(self):
        data, pin = self.source()
        with self.assertRaises(root.Rejected):
            self.run_import(data + b" ", pin)

    def test_wrong_build_pin(self):
        data, pin = self.source()
        pin = root.DevelopmentPin(str(uuid.uuid4()), pin.manifest_sha256, pin.content_sha256, pin.expires_at)
        with self.assertRaises(root.Rejected):
            self.run_import(data, pin)

    def test_expired_pin(self):
        data, pin = self.source()
        pin = root.DevelopmentPin(pin.build_id, pin.manifest_sha256, pin.content_sha256, self.now)
        with self.assertRaises(root.Rejected):
            self.run_import(data, pin)

    def test_duplicate_json_property(self):
        data = root.canonical(self.manifest).replace(b'"SchemaVersion":1', b'"SchemaVersion":1,"SchemaVersion":1')
        with self.assertRaises(root.Rejected):
            root.validate_manifest(data)

    def test_reordered_entries(self):
        self.manifest["Entries"].reverse()
        with self.assertRaises(root.Rejected):
            root.validate_manifest(root.canonical(self.manifest))

    def test_unexpected_post_import_file(self):
        self.run_import()
        (self.target / "unexpected").write_bytes(b"not in manifest")
        with self.assertRaises(root.Rejected):
            root.verify_tree(self.view, self.manifest)

    def test_undeclared_post_import_xattr(self):
        self.run_import()
        os.setxattr(self.target / "usr/share/demo", "user.undeclared", b"no")
        with self.assertRaises(root.Rejected):
            root.verify_tree(self.view, self.manifest)

    def test_extra_external_hardlink(self):
        self.run_import()
        os.link(self.target / "usr/share/demo", self.base / "outside-link")
        with self.assertRaises(root.Rejected):
            root.verify_tree(self.view, self.manifest)

    def test_partial_write_is_failed_not_notstarted(self):
        original = os.write
        def interrupted(fd, data):
            original(fd, data[:1])
            raise OSError("injected mid-file")
        with patch.object(root.os, "write", side_effect=interrupted):
            with self.assertRaises(OSError):
                self.run_import()
        self.assertEqual("Failed", self.record_outcome())
        self.assertTrue(list(self.target.rglob("*")))

    def test_interrupted_between_files(self):
        original = root._metadata
        count = 0
        def interrupted(*args):
            nonlocal count
            count += 1
            if count == 3:
                raise OSError("injected between files")
            return original(*args)
        with patch.object(root, "_metadata", side_effect=interrupted):
            with self.assertRaises(OSError):
                self.run_import()
        self.assertEqual("Failed", self.record_outcome())

    def test_observer_unavailable_is_unknown(self):
        with patch.object(root, "inspect_tree", side_effect=OSError("observer unavailable")):
            with self.assertRaises(OSError):
                self.run_import()
        self.assertEqual("OutcomeUnknown", self.record_outcome())

    def test_fsync_failure_before_intent_never_imports(self):
        with patch.object(deployment_journal.os, "fsync", side_effect=OSError("injected fsync")):
            with self.assertRaises(OSError):
                self.run_import()
        self.assertEqual([], os.listdir(self.target))

    def test_import_file_fsync_failure_preserves_partial_state(self):
        original = os.fsync
        def fail_target(fd):
            if os.readlink(f"/proc/self/fd/{fd}") == str(self.target / "etc/fstab"):
                raise OSError("injected file fsync")
            return original(fd)
        with patch.object(root.os, "fsync", side_effect=fail_target):
            with self.assertRaises(OSError):
                self.run_import()
        self.assertEqual("Failed", self.record_outcome())

    def test_unconfigured_package_manifest_rejected(self):
        self.manifest["Packages"][0]["DpkgStatus"] = "install ok unpacked"
        with self.assertRaises(root.Rejected):
            root.validate_manifest(root.canonical(self.manifest))

    def test_credential_residue_rejected(self):
        path = "/etc/shadow"
        self.contents[path] = b"root:!$6$not-a-real-credential:20000:0:99999:7:::\n"
        item = next(e for e in self.manifest["Entries"] if e["Path"] == path)
        item.update(Length=len(self.contents[path]), Sha256=root.digest(self.contents[path]))
        with self.assertRaises(root.Rejected):
            self.run_import()
        self.assertEqual("Failed", self.record_outcome())

    def test_result_journal_failure_preserves_intent(self):
        original = self.journal.record
        def fail_result(outcome, **evidence):
            if outcome != "IntentDurable":
                raise OSError("result storage unavailable")
            original(outcome, **evidence)
        with patch.object(self.journal, "record", side_effect=fail_result):
            with self.assertRaises(OSError):
                self.run_import()
        self.assertEqual("IntentDurable", self.record_outcome())

    def test_dpkg_substitution(self):
        self.run_import()
        path = self.target / "var/lib/dpkg/status"
        path.write_bytes(path.read_bytes().replace(b"Version: 1.0", b"Version: 2.0"))
        with self.assertRaises(root.Rejected):
            root.verify_dpkg(self.view, self.manifest)

    def test_pending_triggers(self):
        self.contents["/var/lib/dpkg/status"] = self.contents["/var/lib/dpkg/status"].replace(b"Status: install ok installed", b"Status: install ok installed\nTriggers-Pending: unsafe")
        item = next(e for e in self.manifest["Entries"] if e["Path"] == "/var/lib/dpkg/status")
        item.update(Length=len(self.contents[item["Path"]]), Sha256=root.digest(self.contents[item["Path"]]))
        with self.assertRaises(root.Rejected):
            self.run_import()
        self.assertEqual("Failed", self.record_outcome())

    def test_machine_identity_residue(self):
        self.add(entry("/etc/hostname", "File", b"factory-host\n"), b"factory-host\n")
        with self.assertRaises(root.Rejected):
            self.run_import()
        self.assertEqual("Failed", self.record_outcome())


def invalid_manifest_case(kind):
    def test(self):
        bad = entry("/usr/attack", "File", b"bad")
        if kind == "dotdot": bad["Path"] = "/usr/../escape"
        elif kind == "relative": bad["Path"] = "usr/escape"
        elif kind == "absolute-archive-path": bad["Path"] = "//outside"
        elif kind == "symlink-child": bad["Path"] = "/bin/child"
        elif kind == "symlink-escape": bad = entry("/usr/attack", "SymbolicLink", target="../../outside")
        elif kind == "magic-link": bad = entry("/usr/attack", "SymbolicLink", target="/proc/self/fd/1")
        elif kind == "symlink-cycle": bad = entry("/usr/attack", "SymbolicLink", target="attack")
        elif kind == "hardlink-outside": bad = entry("/usr/attack", "HardLink", target="../../outside")
        elif kind == "hardlink-preexisting": bad = entry("/usr/attack", "HardLink", target="/lost+found/old")
        elif kind == "duplicate-type": bad = entry("/usr", "SymbolicLink", target="/etc")
        elif kind in ("BlockDevice", "CharacterDevice", "Fifo", "Socket"): bad["Type"] = kind
        elif kind == "unsafe-xattr": bad["Xattrs"] = {"trusted.overlay.redirect": "Lw=="}
        elif kind == "undeclared-capability": bad["Xattrs"] = {"security.capability": base64.b64encode(struct.pack("<IIIII", 0x02000001, 1 << 21, 0, 0, 0)).decode()}
        elif kind == "esp-content": bad["Path"] = "/boot/efi/shimx64.efi"
        elif kind == "runtime-content": bad["Path"] = "/dev/sda"
        self.add(bad, b"bad")
        with self.assertRaises((root.Rejected, ValueError)):
            root.validate_manifest(root.canonical(self.manifest))
        self.assertEqual([], os.listdir(self.target))
    return test


for _case in ("dotdot", "relative", "absolute-archive-path", "symlink-child", "symlink-escape", "magic-link", "symlink-cycle",
              "hardlink-outside", "hardlink-preexisting", "duplicate-type", "BlockDevice", "CharacterDevice", "Fifo", "Socket",
              "unsafe-xattr", "undeclared-capability", "esp-content", "runtime-content"):
    setattr(ConfiguredRootTests, "test_reject_" + _case.replace("-", "_"), invalid_manifest_case(_case))


if __name__ == "__main__":
    unittest.main()
