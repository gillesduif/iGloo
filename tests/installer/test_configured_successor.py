"""Small observer fixtures; the retained configured tree is tested separately."""
import copy
from pathlib import Path
import stat
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'distros/debian/native'))
import configured_successor as successor
from isolation_policy import canonical, digest


class ConfiguredSuccessorTests(unittest.TestCase):
    def fixture(self):
        manifest = {'SchemaVersion': 2, 'Entries': [
            {'Path': '/', 'Type': 'Directory', 'Uid': 0, 'Gid': 0, 'Mode': 0o755, 'Length': 0, 'Sha256': None, 'Target': None, 'Xattrs': {}},
            {'Path': '/etc/hostname', 'Type': 'File', 'Uid': 0, 'Gid': 0, 'Mode': 0o644, 'Length': 17,
             'Sha256': digest(b'igloo-lab-config\n'), 'Target': None, 'Xattrs': {}}]}
        info = SimpleNamespace(st_mode=stat.S_IFREG | 0o644, st_uid=0, st_gid=0, st_size=17, st_nlink=1)
        actual = {'/etc/hostname': (info, None, {}, manifest['Entries'][1]['Sha256'])}
        observation = {'ProfileStep': 'Verify', 'MachineIdentity': 'FirstBootPending', 'ChangedPaths': ['/etc/hostname'],
            'FilesystemDeltaSha256': digest(canonical(manifest['Entries'])), 'PackageStateSha256': 'A'*64}
        return manifest, actual, observation

    def verify(self, manifest, actual, observation):
        with patch.object(successor, 'entries', return_value=actual), patch.object(successor.root, 'verify_tree') as tree, \
                patch.object(successor.root, 'verify_dpkg', return_value='A'*64):
            result = successor.configured_manifest(object(), manifest, observation)
            tree.assert_called_once()
            return result

    def test_exact_predecessor_observation_preserves_original_validator(self):
        manifest, actual, observation = self.fixture()
        self.assertEqual(manifest, self.verify(manifest, actual, observation))

    def test_changed_configured_bytes_metadata_or_observation_rejected(self):
        for mutation in ('bytes', 'mode', 'links', 'attrs', 'missing', 'incomplete', 'wrong-digest', 'package'):
            with self.subTest(mutation=mutation):
                manifest, actual, observation = self.fixture()
                if mutation == 'bytes': actual['/etc/hostname'] = (*actual['/etc/hostname'][:3], 'F'*64)
                if mutation == 'mode': actual['/etc/hostname'][0].st_mode = stat.S_IFREG | 0o666
                if mutation == 'links': actual['/etc/hostname'][0].st_nlink = 2
                if mutation == 'attrs': actual['/etc/hostname'][2]['user.unexpected'] = b'value'
                if mutation == 'missing': actual.clear()
                if mutation == 'incomplete': observation['ProfileStep'] = 'Files'
                if mutation == 'wrong-digest': observation['FilesystemDeltaSha256'] = 'E'*64
                if mutation == 'package': observation['PackageStateSha256'] = 'B'*64
                with self.assertRaises(ValueError): self.verify(manifest, actual, observation)

    def test_no_neutral_verification_or_configuration_helper_replay(self):
        manifest, actual, observation = self.fixture()
        before = copy.deepcopy(manifest)
        with patch.object(successor.root, 'verify_neutral', side_effect=AssertionError('Neutral verifier not applicable')):
            self.verify(manifest, actual, observation)
        self.assertEqual(before, manifest)


if __name__ == '__main__': unittest.main()
