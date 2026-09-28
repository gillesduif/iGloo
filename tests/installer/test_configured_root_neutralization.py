"""Exact neutralization/durable-failure fixtures; no factory/target disks."""
import base64
import unittest
from unittest.mock import patch

import test_configured_root as mechanics
import configured_root as root
import configured_root_neutralization as neutral
import factory_neutral_audit as audit


class NeutralizationTests(unittest.TestCase):
    def setUp(self):
        self.fixture = mechanics.ConfiguredRootTests(); self.fixture.setUp(); self.fixture.run_import()

    def tearDown(self):
        self.fixture.tearDown()

    def change(self, path, content):
        before = neutral.observe_object(self.fixture.view, path)
        return {"Path": path, "Operation": "Write", "Before": before,
                "After": neutral.file_after(before, content), "ContentBase64": base64.b64encode(content).decode()}

    def test_exact_empty_fstab_write_and_independent_readback(self):
        change = self.change("/etc/fstab", b"")
        neutral.apply_object(self.fixture.view, change)
        self.assertEqual(change["After"], neutral.observe_object(self.fixture.view, "/etc/fstab"))

    def test_before_hash_change_rejects_without_overwrite(self):
        change = self.change("/etc/fstab", b"")
        (self.fixture.target / "etc/fstab").write_bytes(b"unexpected")
        with self.assertRaisesRegex(root.Rejected, "BeforeChanged"):
            neutral.apply_object(self.fixture.view, change)
        self.assertEqual(b"unexpected", (self.fixture.target / "etc/fstab").read_bytes())

    def test_symlink_parent_cannot_escape(self):
        (self.fixture.target / "etc/link").symlink_to(self.fixture.outside, target_is_directory=True)
        with self.assertRaises(OSError): neutral.observe_object(self.fixture.view, "/etc/link/sentinel")

    def test_no_recursive_directory_removal(self):
        change = {"Path": "/etc", "Operation": "Remove", "Before": neutral.observe_object(self.fixture.view, "/etc"), "After": neutral.absent()}
        with self.assertRaises(OSError): neutral.apply_object(self.fixture.view, change)
        self.assertTrue((self.fixture.target / "etc/fstab").exists())

    def test_fsync_failure_is_not_absence_of_effect(self):
        change = self.change("/etc/fstab", b"partial fixture\n")
        with patch.object(neutral.os, "fsync", side_effect=OSError("fixture fsync failure")):
            with self.assertRaises(OSError): neutral.apply_object(self.fixture.view, change)
        self.assertEqual(b"partial fixture\n", (self.fixture.target / "etc/fstab").read_bytes())

    def test_hardlink_not_mutated_through_one_alias(self):
        with self.assertRaisesRegex(root.Rejected, "HardlinkRejected"):
            neutral.observe_object(self.fixture.view, "/usr/share/demo")

    def test_debconf_only_exact_identity_fields_change(self):
        before = b"Name: exim4/mailname\nValue: igloo-factory\nOwners: exim4-config\n\nName: unaffected\nValue: retained\n"
        after = before.replace(b"Value: igloo-factory", b"Value: ")
        self.assertEqual(root.digest(after), neutral.verify_debconf(before, after))
        with self.assertRaises(root.Rejected): neutral.verify_debconf(before, after.replace(b"retained", b"changed"))

    def test_debconf_duplicate_or_removed_question_rejected(self):
        data = b"Name: exim4/mailname\nValue: igloo-factory\n"
        with self.assertRaises(root.Rejected): neutral.debconf_records(data + b"\n" + data)
        with self.assertRaises(root.Rejected): neutral.verify_debconf(data, b"Name: other\n")

    def test_private_key_recognized_without_filename(self):
        data = b"-----BEGIN PRIVATE KEY-----\n" + base64.b64encode(bytes(range(128))) + b"\n-----END PRIVATE KEY-----"
        self.assertTrue(audit.encoded_private_key(data))
        self.assertFalse(audit.encoded_private_key(b'code says "-----BEGIN PRIVATE KEY-----"'))

    def test_factory_initramfs_is_explicitly_removed(self):
        self.assertEqual("Initramfs", neutral.REMOVALS["/boot/initrd.img-" + neutral.KERNEL])
        self.assertIn("factory image hash is forbidden", neutral.REGENERATION["Initramfs"][2])
        self.assertNotIn("/boot/vmlinuz-" + neutral.KERNEL, neutral.REMOVALS)
        image = self.fixture.target / ("boot/initrd.img-" + neutral.KERNEL); image.write_bytes(b"factory only")
        manifest = self.fixture.manifest.copy()
        manifest["Entries"] = [*manifest["Entries"], mechanics.entry("/boot/initrd.img-" + neutral.KERNEL, "File", b"factory only")]
        with self.assertRaisesRegex(root.Rejected, "FactoryInitramfsNotTargetEvidence"):
            root.verify_neutral(self.fixture.view, manifest)

    def test_tls_never_inherits_factory_private_identity(self):
        for name in ("/etc/ssl/private/ssl-cert-snakeoil.key", "/etc/ssl/certs/ssl-cert-snakeoil.pem", "/etc/ssl/certs/66d6b83e.0"):
            self.assertEqual("TlsIdentity", neutral.REMOVALS[name])
        self.assertIn("make-ssl-cert generate-default-snakeoil", neutral.REGENERATION["TlsIdentity"][1])
        self.assertIn("before dependent services", neutral.REGENERATION["TlsIdentity"][1])

    def test_empty_machine_id_does_not_change_worker_first_boot_semantics(self):
        self.assertIn("does not imply ConditionFirstBoot", neutral.REGENERATION["MachineIdentity"][1])
        self.assertEqual("/etc/machine-id", neutral.LINKS["/var/lib/dbus/machine-id"])

    def test_root_account_and_unknown_human_accounts_are_not_generic_locked_hash_allowance(self):
        self.assertIn("root", audit.SYSTEM_ACCOUNTS)
        self.assertNotIn("factory", audit.SYSTEM_ACCOUNTS)
        self.assertNotIn("igloo", audit.SYSTEM_ACCOUNTS)
        self.assertIn("root", audit.STAR_ACCOUNTS)
        self.assertNotIn("root", audit.LOCKED_STAR_ACCOUNTS)

    def test_public_test_constant_exception_is_exact_content_not_filename_only(self):
        path = "/usr/lib/x86_64-linux-gnu/libgnutls.so.30.40.3"
        self.assertEqual("4B14EDDE42E7CBEEFD8A6D88D035E096F2E6412A535388BB1D073A1C196E2822", audit.PUBLIC_PACKAGE_KEYS[path])
        self.assertNotEqual(root.digest(b"substituted private key"), audit.PUBLIC_PACKAGE_KEYS[path])
        self.assertNotIn("/etc/ssl/private/ssl-cert-snakeoil.key", audit.PUBLIC_PACKAGE_KEYS)

    def test_unknown_mutation_is_not_cleanup_authority(self):
        value = {"SchemaVersion": 1, "PlanVersion": neutral.PLAN, "Release": "trixie", "Architecture": "amd64",
                 "Changes": [{"Path": "/var/lib/dpkg/status", "Operation": "Remove", "After": neutral.absent()}]}
        with self.assertRaisesRegex(root.Rejected, "UnreviewedNeutralization"): neutral.validate_plan(value)

    def test_builder_apt_policy_is_not_installed_workstation_state(self):
        self.assertEqual("AptSources", neutral.REMOVALS["/etc/apt/apt.conf.d/99mmdebstrap"])
        directory = self.fixture.target / "etc/apt/apt.conf.d"; directory.mkdir(parents=True)
        path = "/etc/apt/apt.conf.d/99mmdebstrap"
        (self.fixture.target / path[1:]).write_bytes(b'Acquire::Languages "none";\n')
        manifest = self.fixture.manifest.copy()
        manifest["Entries"] = [*manifest["Entries"], mechanics.entry(path, "File", b'Acquire::Languages "none";\n')]
        with self.assertRaisesRegex(root.Rejected, "BuildConfigurationResidue"): root.verify_neutral(self.fixture.view, manifest)

    def test_network_user_data_enrollment_stay_explicit_target_responsibilities(self):
        for name in ("Network", "User", "Agent", "UserData", "Enrollment"):
            self.assertIn(name, neutral.REGENERATION)
        self.assertIn("Unsupported", neutral.REGENERATION["UserData"][1])


if __name__ == "__main__": unittest.main()
