#!/usr/bin/env python3
"""Adversarial import tests using exact metadata/bytes selected from the REAL root.

The deliberately incomplete metadata derivative is NEVER a workstation artifact.
It retains the real status database, capability, ACL, symlinks and a full hardlink
group. It tests failure semantics economically; the separate full EXT4 rehearsal
tests complete filesystem/dpkg equivalence. Every failed target is retained.
"""
import copy
import datetime as dt
import json
import os
from pathlib import Path
import sys
import types
import unittest
import uuid
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import configured_root as root
import configured_root_metadata as metadata
from factory_artifact_verifier import authenticate
from factory_neutralization_rehearsal import checkpoint, environment

WORKSPACE = None


class RealMetadataFailureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        environment(WORKSPACE)
        assert json.loads((WORKSPACE / "artifact-independent-verification.json").read_bytes())["Result"] == "ArtifactValid"
        cls.base = WORKSPACE / "failure-fixtures-v2"; cls.base.mkdir(mode=0o700)
        full = json.loads((WORKSPACE / "artifact/root.manifest.json").read_bytes()); entries = {e["Path"]: e for e in full["Entries"]}
        selected = {"/etc/fstab", "/etc/machine-id", "/etc/shadow", "/etc/group", "/etc/resolv.conf", "/var/lib/dbus/machine-id",
                    "/var/lib/dpkg/status", "/var/lib/dpkg/updates", metadata.GST_PATH, "/var/log/journal",
                    "/dev", "/proc", "/sys", "/run", "/tmp", "/home", "/root", "/boot/efi", "/bin", "/sbin", "/lib", "/lib64"}
        links = [e for e in full["Entries"] if e["Type"] == "HardLink"]
        primary = min((e["Target"] for e in links), key=lambda p: entries[p]["Length"])
        selected.add(primary); selected.update(e["Path"] for e in links if e["Target"] == primary)
        # A no-ESP factory may omit /boot/efi entirely; do not fabricate a source
        # object merely to fit the fixture. The real manifest remains authoritative.
        selected.intersection_update(entries)
        for path in list(selected):
            parent = Path(path).parent
            while str(parent) != "/": selected.add(str(parent)); parent = parent.parent
        selected.add("/")
        # Include each merged-/usr target directory, without traversing the links.
        selected.update(p for p in ("/usr", "/usr/bin", "/usr/sbin", "/usr/lib", "/usr/lib64") if p in entries)
        cls.manifest = dict(full, Entries=[entries[p] for p in sorted(selected)])
        cls.data = root.canonical(cls.manifest); root.validate_manifest(cls.data)
        cls.content = root.MAGIC + b"".join((WORKSPACE / "root" / e["Path"][1:]).read_bytes() for e in cls.manifest["Entries"] if e["Type"] == "File")
        checkpoint(cls.base, "derivative", {"Qualification": "AdversarialMetadataDerivativeOnly", "WorkstationArtifact": False,
                   "SourceManifestSha256": root.digest((WORKSPACE / "artifact/root.manifest.json").read_bytes()),
                   "DerivativeManifestSha256": root.digest(cls.data), "Entries": len(selected), "ContentLength": len(cls.content),
                   "ContainsRealGstreamerCapability": True, "ContainsRealJournalAcl": True, "HardlinkPrimary": primary})

    def setUp(self):
        self.directory = self.base / self._testMethodName; self.directory.mkdir(mode=0o700)
        self.target = self.directory / "root"; self.target.mkdir(mode=0o700)
        self.fd = os.open(self.target, os.O_DIRECTORY); info = os.fstat(self.fd)
        self.view = root.RootView(self.fd, info.st_dev, info.st_ino, root.mount_id(self.fd))
        self.generation = str(uuid.uuid4()); self.plan = root.digest(self.data)
        self.store = self.directory / "journal"; self.store.mkdir(mode=0o700)
        self.journal = root.ImportJournal(str(self.store), self.generation, self.plan, self.manifest["BuildId"])
        self.now = dt.datetime.now(dt.timezone.utc)
        self.pin = root.DevelopmentPin(self.manifest["BuildId"], root.digest(self.data), root.digest(self.content), self.now + dt.timedelta(days=1))
        self.source = self.directory / "content"; self.source.write_bytes(self.content)

    def tearDown(self):
        self.view.close(); os.close(self.fd)  # retain all partial targets and journals

    def execute(self, data=None, generation=None, pin=None):
        fd = os.open(self.source, os.O_RDONLY)
        try:
            return root.import_development_fixture(self.view, self.data if data is None else data, fd,
                self.pin if pin is None else pin, self.journal, self.generation if generation is None else generation, self.plan, self.now)
        finally: os.close(fd)

    def outcome(self):
        files = sorted((self.store / self.generation).glob("*.json"))
        return json.loads(files[-1].read_bytes())["Outcome"] if files else "NotStarted"

    def rejected(self, context=None, **arguments):
        if context is None:
            with self.assertRaises((OSError, ValueError)): self.execute(**arguments)
        else:
            with context, self.assertRaises((OSError, ValueError)): self.execute(**arguments)
        self.assertNotEqual("AppliedAndVerified", self.outcome())

    def test_01_exact_real_metadata_derivative(self):
        self.assertEqual("AppliedAndVerified", self.execute()["Outcome"])
        root.verify_tree(self.view, self.manifest); root.verify_dpkg(self.view, self.manifest)

    def test_02_stream_truncation(self):
        self.source.write_bytes(self.content[:-1]); self.rejected()

    def test_03_stream_byte_corruption(self):
        value = bytearray(self.content); value[len(root.MAGIC)] ^= 1; self.source.write_bytes(value); self.rejected()

    def test_04_manifest_corruption(self): self.rejected(data=self.data[:-1])

    def test_05_descriptor_mismatch(self):
        descriptor = bytearray((WORKSPACE / "artifact/descriptor.json").read_bytes()); descriptor[-2] ^= 1
        with self.assertRaises(root.Rejected): authenticate(bytes(descriptor), json.loads((WORKSPACE / "external-development-pin.json").read_bytes()), self.now)

    def test_06_stale_build_pin(self):
        self.rejected(pin=root.DevelopmentPin(str(uuid.uuid4()), self.pin.manifest_sha256, self.pin.content_sha256, self.pin.expires_at))

    def test_07_stale_target_generation(self): self.rejected(generation=str(uuid.uuid4()))

    def test_08_insufficient_space(self):
        self.rejected(patch.object(root.os, "fstatvfs", return_value=types.SimpleNamespace(f_bavail=0, f_frsize=4096)))

    def test_09_target_fsync_failure(self):
        original = os.fsync
        def fail(fd):
            if os.readlink(f"/proc/self/fd/{fd}").startswith(str(self.target) + "/"): raise OSError("fixture fsync")
            return original(fd)
        self.rejected(patch.object(root.os, "fsync", side_effect=fail)); self.assertEqual("Failed", self.outcome())

    def test_10_observer_failure(self):
        self.rejected(patch.object(root, "inspect_tree", side_effect=OSError("fixture unavailable")))
        self.assertEqual("OutcomeUnknown", self.outcome())

    def test_11_interruption_after_first_file(self):
        original = root._metadata; calls = 0
        def fail(fd, entry, schema=1):
            nonlocal calls
            calls += 1
            if calls == 2: raise OSError("interrupted between files")
            return original(fd, entry, schema)
        self.rejected(patch.object(root, "_metadata", side_effect=fail)); self.assertEqual("Failed", self.outcome())
        self.rejected()  # partial root is not fresh; no blind re-import

    def test_12_unexpected_preexisting_file(self):
        (self.target / "unexpected").write_bytes(b"preserve"); self.rejected()
        self.assertEqual(b"preserve", (self.target / "unexpected").read_bytes())

    def test_13_target_replacement(self):
        self.target.rename(self.directory / "retained-original"); self.target.mkdir(); self.rejected()

    def test_14_mount_substitution_fixture(self):
        self.rejected(patch.object(root, "mount_id", return_value=self.view.expected[2] + 1))

    def test_15_xattr_failure(self): self.rejected(patch.object(root.os, "setxattr", side_effect=OSError("xattr unavailable")))

    def test_16_capability_failure(self):
        original = os.setxattr
        def fail(fd, name, value, **kwargs):
            if name == "security.capability": raise OSError("capability unavailable")
            return original(fd, name, value, **kwargs)
        self.rejected(patch.object(root.os, "setxattr", side_effect=fail))

    def test_17_acl_failure(self):
        original = os.setxattr
        def fail(fd, name, value, **kwargs):
            if name in metadata.ACL_NAMES: raise OSError("acl unavailable")
            return original(fd, name, value, **kwargs)
        self.rejected(patch.object(root.os, "setxattr", side_effect=fail))

    def test_18_hardlink_failure(self): self.rejected(patch.object(root.os, "link", side_effect=OSError("hardlink unavailable")))


if __name__ == "__main__":
    WORKSPACE = Path(sys.argv[1])
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(RealMetadataFailureTests))
    checkpoint(WORKSPACE, "real-artifact-failure-tests-v2", {"Tests": result.testsRun, "Failures": len(result.failures), "Errors": len(result.errors),
               "Skipped": len(result.skipped), "Passed": max(0, result.testsRun - len(result.failures) - len(result.errors) - len(result.skipped)),
               "FullArtifactImportClaim": False, "RealMetadataDerivative": True, "PartialTargetsRetained": True})
    sys.exit(0 if result.wasSuccessful() else 1)
