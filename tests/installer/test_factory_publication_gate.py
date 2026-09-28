"""Read-only rejection of real-factory residue; these are not factory builds."""
import copy
import unittest

import test_configured_root as mechanics
from factory_root_observer import inventory
import factory_publication_gate as gate


class FactoryPublicationTests(unittest.TestCase):
    def setUp(self):
        self.fixture = mechanics.ConfiguredRootTests(); self.fixture.setUp(); self.fixture.run_import()

    def tearDown(self):
        self.fixture.tearDown()

    def audit(self):
        return {"BuildId": self.fixture.manifest["BuildId"], "PackageStateQualified": True,
                "Packages": self.fixture.manifest["Packages"], "Filesystem": inventory(self.fixture.target)}

    def test_synthetic_readback_is_not_artifact_authentication(self):
        result = gate.evaluate(self.fixture.target, self.audit())
        self.assertTrue(result["PublicationChecksPassed"])
        self.assertEqual("Unsupported", result["ProductionAuthentication"])
        self.assertFalse(result["StreamProduced"])

    def test_private_key_blocks_even_without_factory_hostname(self):
        directory = self.fixture.target / "etc/ssl/private"; directory.mkdir(parents=True)
        key = directory / "ssl-cert-snakeoil.key"; key.write_bytes(b"NOT A REAL SECRET: synthetic fixture")
        before = key.read_bytes()
        result = gate.evaluate(self.fixture.target, self.audit())
        self.assertFalse(result["PublicationChecksPassed"])
        self.assertIn("NeutralState:BuildConfigurationResidue", result["Blockers"])
        self.assertEqual(before, key.read_bytes())

    def test_hidden_factory_exim_state_blocks(self):
        directory = self.fixture.target / "etc/exim4"; directory.mkdir()
        (directory / "update-exim4.conf.conf").write_text("dc_other_hostnames='igloo-factory'\n")
        result = gate.evaluate(self.fixture.target, self.audit())
        self.assertIn("NeutralState:FactoryHostnameInPackageState", result["Blockers"])

    def test_observed_device_entry_is_not_filtered(self):
        audit = self.audit()
        device = {"Path": "/dev/null", "Type": "CharacterDevice", "Uid": 0, "Gid": 0, "Mode": 0o666,
                  "Length": 0, "Sha256": None, "Target": None, "Xattrs": {}}
        audit["Filesystem"]["Entries"].append(device)
        candidate = gate.candidate_manifest(self.fixture.target, audit)
        self.assertIn(device, candidate["Entries"])
        self.assertFalse(gate.evaluate(self.fixture.target, audit)["PublicationChecksPassed"])

    def test_unconfigured_package_report_blocks(self):
        audit = self.audit(); audit["PackageStateQualified"] = False
        result = gate.evaluate(self.fixture.target, audit)
        self.assertIn("PackageStateNotQualified", result["Blockers"])

    def test_real_hardlink_topology_retained(self):
        audit = self.audit(); before = copy.deepcopy(audit)
        result = gate.candidate_manifest(self.fixture.target, audit)
        links = [e for e in result["Entries"] if e["Type"] == "HardLink"]
        self.assertEqual(1, len(links))
        self.assertEqual("/usr/share/demo", links[0]["Target"])
        self.assertEqual(audit, before)

    def test_mail_identity_remains_blocking(self):
        (self.fixture.target / "etc/mailname").write_text("factory.invalid\n")
        self.assertFalse(gate.evaluate(self.fixture.target, self.audit())["PublicationChecksPassed"])


if __name__ == "__main__":
    unittest.main()
