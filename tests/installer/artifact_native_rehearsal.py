"""Explicit root-only file-capability fixture. No blocks, mounts or target execution."""
import base64
import os
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_configured_root import ConfiguredRootTests, entry
import configured_root as root
import configured_root_metadata as profile


class CapabilityFixture(unittest.TestCase):
    def test_reviewed_journal_acl_pair_and_no_inherited_child_acl(self):
        self.assertEqual(0, os.geteuid(), "Native metadata fixture requires actual root.")
        fixture = ConfiguredRootTests(); fixture.setUp()
        try:
            fixture.manifest["SchemaVersion"] = 2
            fixture.add(entry("/var/log"))
            journal = entry("/var/log/journal", mode=0o2755)
            journal.update(Uid=0, Gid=997, Xattrs={n: base64.b64encode(profile.JOURNAL_ACL).decode() for n in profile.ACL_NAMES})
            fixture.add(journal)
            fixture.add(entry("/etc/group", "File", b"adm:x:4:\nsystemd-journal:x:997:\n"), b"adm:x:4:\nsystemd-journal:x:997:\n")
            child = b""; fixture.add(entry("/var/log/journal/fixture", "File", child), child)
            status = fixture.contents["/var/lib/dpkg/status"] + b"Package: systemd\nStatus: install ok installed\nArchitecture: amd64\nVersion: 257.13-1~deb13u1\nMaintainer: Fixture\nDescription: metadata test only\n\n"
            fixture.contents["/var/lib/dpkg/status"] = status
            for item in fixture.manifest["Entries"]:
                if item["Path"] == "/var/lib/dpkg/status": item.update(Length=len(status), Sha256=root.digest(status))
            fixture.manifest["Packages"].append({"Name": "systemd", "Version": "257.13-1~deb13u1", "Architecture": "amd64", "DpkgStatus": "install ok installed"})
            fixture.run_import()
            for name in profile.ACL_NAMES:
                self.assertEqual(profile.JOURNAL_ACL, os.getxattr(fixture.target / "var/log/journal", name))
            self.assertEqual([], os.listxattr(fixture.target / "var/log/journal/fixture"))
            root.verify_tree(fixture.view, fixture.manifest)
            changed = bytearray(profile.JOURNAL_ACL); changed[24] = 5
            os.setxattr(fixture.target / "var/log/journal", "system.posix_acl_default", changed)
            with self.assertRaises((root.Rejected, OSError)):
                root.verify_tree(fixture.view, fixture.manifest)
        finally:
            fixture.tearDown()

    def test_declared_capability_restored_and_independently_observed(self):
        self.assertEqual(0, os.geteuid(), "Run this explicit fixture as root; do not fake capability support.")
        fixture = ConfiguredRootTests()
        fixture.setUp()
        try:
            content = b"fixture data only; never executed\n"
            ping = entry("/usr/bin/ping", "File", content, mode=0o755)
            capability = struct.pack("<IIIII", 0x02000001, 1 << 13, 0, 0, 0)
            ping["Xattrs"] = {"security.capability": base64.b64encode(capability).decode()}
            fixture.add(ping, content)
            result = fixture.run_import()
            self.assertEqual("AppliedAndVerified", result["Outcome"])
            self.assertEqual(capability, os.getxattr(fixture.target / "usr/bin/ping", "security.capability"))
            root.verify_tree(fixture.view, fixture.manifest)
            # A capability present on disk but absent from the authenticated manifest fails.
            ping["Xattrs"] = {}
            with self.assertRaises(root.Rejected):
                root.verify_tree(fixture.view, fixture.manifest)
        finally:
            fixture.tearDown()


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(CapabilityFixture)
    sys.exit(0 if unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful() else 1)
