"""Protocol fixtures only; actual GPT storage smoke is separately retained evidence."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'distros/debian/native'))
import block_session
import canonical_storage_guest


class StorageSmokeTests(unittest.TestCase):
    def test_provisioning_roles_cannot_follow_an_arbitrary_matching_backing(self):
        target = {'Serial': 'IGLOO-LAB-TARGET', 'Length': 40 * 1024**3}
        journal = {'Serial': 'IGLOO-LAB-JOURNAL', 'Length': 8 * 1024**3}
        host = {'CreatedBackings': [target, journal], 'ReopenedBackings': [target, journal]}
        self.assertEqual(target, canonical_storage_guest.new_disk_binding(host, 'vdb', target['Serial'], target['Length']))
        for device, serial, length in [('vdb', journal['Serial'], journal['Length']),
                                      ('vdc', target['Serial'], target['Length']), ('vda', target['Serial'], target['Length'])]:
            with self.subTest(device=device), self.assertRaisesRegex(ValueError, 'NewDiskRoleChanged'):
                canonical_storage_guest.new_disk_binding(host, device, serial, length)

    def declaration(self):
        return {'GenerationId': 'generation', 'ImportPlan': None, 'StorageSmoke': {
            'Provider': 'IsolatedFileBackedLab', 'Version': 1, 'Scope': 'StorageSmoke', 'GenerationId': 'generation'}}

    def test_closed_root_only_topology(self):
        self.assertTrue(block_session.root_only(self.declaration()))
        for key, value in [('Provider', 'Windows'), ('Version', 0), ('Scope', 'ImportConfiguredRoot'), ('GenerationId', 'other')]:
            declaration = self.declaration(); declaration['StorageSmoke'][key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'LabProvenanceInvalid|LabImportScopeChanged'):
                block_session.root_only(declaration)
        declaration = self.declaration(); declaration['ImportPlan'] = {'anything': True}
        with self.assertRaisesRegex(ValueError, 'LabProvenanceInvalid|LabImportScopeChanged'):
            block_session.root_only(declaration)

    def test_smoke_cannot_dispatch_import_even_after_acquisition(self):
        session = SimpleNamespace(failed=False, closed=False, acquired=True, teardown=False,
            channel=Mock(), declaration=self.declaration())
        with self.assertRaisesRegex(ValueError, 'ImportSessionNotActive'):
            block_session.MountSession.perform(session, 'ImportConfiguredRoot')
        self.assertTrue(session.failed)

    def test_import_scope_requires_matching_plan_and_cannot_mix_smoke(self):
        provenance = {'Provider': 'IsolatedFileBackedLab', 'Version': 2, 'Scope': 'ConfiguredRootImport', 'GenerationId': 'fresh'}
        declaration = {'GenerationId': 'fresh', 'LabImport': provenance,
                       'ImportPlan': {'SchemaVersion': 1, 'Provenance': provenance}}
        self.assertTrue(block_session.root_only(declaration))
        for mutation in ('plan', 'scope', 'mixed', 'generation'):
            changed = __import__('copy').deepcopy(declaration)
            if mutation == 'plan': changed['ImportPlan'] = None
            elif mutation == 'scope': changed['LabImport']['Scope'] = 'StorageSmoke'
            elif mutation == 'mixed': changed['StorageSmoke'] = self.declaration()['StorageSmoke']
            else: changed['GenerationId'] = 'old'
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                block_session.root_only(changed)

    def test_legacy_topology_is_not_reinterpreted_as_smoke(self):
        self.assertFalse(block_session.root_only({'ImportPlan': None}))
        self.assertTrue(block_session.root_only({'ImportPlan': {'Generation': 'fixture'}}))


if __name__ == '__main__': unittest.main()
