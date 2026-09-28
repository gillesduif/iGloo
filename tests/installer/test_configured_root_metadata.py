"""Narrow schema-2 metadata learned from the actual authenticated GNOME root."""
import base64
import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "distros/debian/native"))
import configured_root as root
import configured_root_metadata as profile
from test_configured_root import entry, fixture


class RealTreeMetadataTests(unittest.TestCase):
    def capability(self):
        result = entry(profile.GST_PATH, "File", mode=0o755)
        result.update(Uid=0, Gid=0, Length=534992, Sha256=profile.GST_SHA256,
                      Xattrs={"security.capability": base64.b64encode(profile.GST_CAPABILITY).decode()})
        return result

    def acl(self):
        result = entry("/var/log/journal", mode=0o2755)
        result.update(Uid=0, Gid=997, Xattrs={n: base64.b64encode(profile.JOURNAL_ACL).decode() for n in profile.ACL_NAMES})
        return result

    def test_observed_gstreamer_capability(self):
        self.assertEqual(profile.GST_CAPABILITY, root.attributes(self.capability(), 2)["security.capability"])

    def test_capability_schema_one_stays_rejected(self):
        with self.assertRaisesRegex(root.Rejected, "UndeclaredCapability"):
            root.attributes(self.capability(), 1)

    def test_capability_moved_to_another_binary_rejected(self):
        value = self.capability(); value["Path"] = "/usr/bin/arbitrary"
        with self.assertRaises(root.Rejected): root.attributes(value, 2)

    def test_capability_changed_content_rejected(self):
        value = self.capability(); value["Sha256"] = "A" * 64
        with self.assertRaises(root.Rejected): root.attributes(value, 2)

    def test_capability_extra_privilege_rejected(self):
        value = self.capability()
        changed = bytearray(profile.GST_CAPABILITY); changed[4] |= 1
        value["Xattrs"]["security.capability"] = base64.b64encode(changed).decode()
        with self.assertRaises(root.Rejected): root.attributes(value, 2)

    def test_capability_other_owner_or_setuid_rejected(self):
        for key, changed in (("Uid", 1), ("Gid", 1), ("Mode", 0o4755), ("Length", 534993)):
            with self.subTest(key=key):
                value = self.capability(); value[key] = changed
                with self.assertRaises(root.Rejected): root.attributes(value, 2)

    def test_actual_journal_acl_pair(self):
        self.assertEqual(profile.ACL_NAMES, root.attributes(self.acl(), 2).keys())

    def test_acl_schema_one_stays_rejected(self):
        with self.assertRaises(root.Rejected): root.attributes(self.acl(), 1)

    def test_acl_different_path_or_group_or_mode_rejected(self):
        for key, changed in (("Path", "/etc"), ("Gid", 0), ("Mode", 0o2777), ("Type", "File")):
            with self.subTest(key=key):
                value = self.acl(); value[key] = changed
                with self.assertRaises(root.Rejected): root.attributes(value, 2)

    def test_acl_extra_write_permission_rejected(self):
        value = self.acl(); data = bytearray(profile.JOURNAL_ACL); data[-6] = 7
        value["Xattrs"]["system.posix_acl_default"] = base64.b64encode(data).decode()
        with self.assertRaises(root.Rejected): root.attributes(value, 2)

    def test_acl_only_one_half_rejected(self):
        value = self.acl(); value["Xattrs"].pop("system.posix_acl_default")
        with self.assertRaises(root.Rejected): root.attributes(value, 2)

    def test_unknown_security_and_trusted_xattr_rejected(self):
        for name in ("security.selinux", "trusted.overlay.opaque", "system.posix_acl_unknown"):
            value = entry("/usr/bin/test", "File"); value["Xattrs"] = {name: "eA=="}
            with self.subTest(name=name), self.assertRaises(root.Rejected): root.attributes(value, 2)

    def test_only_observed_unicode_certificate_paths(self):
        for path in profile.UNICODE_PATHS:
            self.assertTrue(root.path_parts(path))
        for path in ("/tmp/é", next(iter(profile.UNICODE_PATHS)) + "x", "/tmp/\u202eattack"):
            with self.subTest(path=path), self.assertRaises(root.Rejected): root.path_parts(path)

    def test_unicode_symlink_exact_destination(self):
        target = "/usr/share/ca-certificates/mozilla/" + profile.CERT_NAME + ".crt"
        self.assertEqual(target, root.link_destination("/etc/ssl/certs/" + profile.CERT_NAME + ".pem", target, 2))
        with self.assertRaises(root.Rejected): root.link_destination("/etc/ssl/test", target, 1)

    def test_exact_systemd_escaped_names_are_literal_posix_paths(self):
        for path in profile.ESCAPED_UNIT_PATHS:
            self.assertEqual(path.split("/")[1:], root.path_parts(path))
        for path in (r"/usr/lib/systemd/system/..\escape", r"/usr/lib/systemd/system/arbitrary\x2dname.slice"):
            with self.assertRaises(root.Rejected): root.path_parts(path)

    def test_schema_one_does_not_accept_new_escaped_path(self):
        value, _ = fixture()
        value["Entries"].append(entry(next(iter(profile.ESCAPED_UNIT_PATHS)), "File"))
        with self.assertRaisesRegex(root.Rejected, "UnsupportedPathEncoding"):
            root.validate_manifest(root.canonical(value))

    def test_expected_unit_masks_and_mtab(self):
        for path in profile.MASKED_UNITS:
            self.assertEqual("/dev/null", root.link_destination(path, "/dev/null", 2))
        self.assertEqual("/proc/self/mounts", root.link_destination("/etc/mtab", "../proc/self/mounts", 2))

    def test_runtime_path_relaxation_is_not_general(self):
        for path, target in (("/etc/anything", "/dev/null"), ("/etc/mtab", "/proc/self/fd/1"),
                             (next(iter(profile.MASKED_UNITS)), "/dev/sda"), ("/etc/mtab", "../sys/firmware/efi")):
            with self.subTest(path=path, target=target), self.assertRaises(root.Rejected): root.link_destination(path, target, 2)

    def test_new_metadata_requires_inspected_package_versions(self):
        expected = {("libgstreamer1.0-0", "1.26.2-2", "amd64"), ("systemd", "257.13-1~deb13u1", "amd64")}
        self.assertEqual(expected, profile.package_bindings([self.capability(), self.acl()]))

    def test_schema_two_fixture_stays_deterministic(self):
        value, _ = fixture(); value["SchemaVersion"] = 2
        self.assertEqual(value, root.validate_manifest(root.canonical(copy.deepcopy(value))))

    def test_schema_three_not_inferred(self):
        value, _ = fixture(); value["SchemaVersion"] = 3
        with self.assertRaises(root.Rejected): root.validate_manifest(root.canonical(value))


if __name__ == "__main__":
    unittest.main()
