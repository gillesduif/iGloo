"""Partial filesystem fixtures using the actual .NET selected-document wire.

No canonical receipts, privileged producer or deployment authority. The native
rehearsal separately runs the producer/admission on a protected isolated root.
"""
import base64
import copy
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'distros/debian/native'))
import configured_root as root
import userdata_contract as contract
import userdata_successor as successor
import session_userdata as adapter
from target_files import mount_id

WIRE = os.environ.get('IGLOO_USERDATA_ADMISSION_WIRE')


def snapshot_manifest(view):
    """Fixture baseline captured BEFORE any selected-data effects."""
    values = []
    for path, info, target, attrs, sha in root.inspect_tree(view):
        assert not attrs
        kind = 'Directory' if stat.S_ISDIR(info.st_mode) else 'SymbolicLink' if stat.S_ISLNK(info.st_mode) else 'File'
        values.append({'Path': path, 'Type': kind, 'Uid': info.st_uid, 'Gid': info.st_gid,
            'Mode': stat.S_IMODE(info.st_mode), 'Length': info.st_size if kind == 'File' else None,
            'Sha256': sha if kind == 'File' else None, 'Target': target, 'Xattrs': {}})
    return {'SchemaVersion': 2, 'Entries': sorted(values, key=lambda e: e['Path']),
        'Packages': [{'Name': 'fixture-only', 'Version': '1', 'Architecture': 'all', 'DpkgStatus': 'install ok installed'}]}


def package_fixture(directory):
    p = directory/'var/lib/dpkg'; p.mkdir(parents=True, mode=0o755)
    (p/'status').write_text('Package: fixture-only\nStatus: install ok installed\nArchitecture: all\nVersion: 1\nDescription: synthetic observer fixture\n\n')
    (p/'status').chmod(0o644)


@unittest.skipUnless(WIRE, 'Actual .NET selection required')
class PathPolicyTests(unittest.TestCase):
    def setUp(self):
        self.raw = (Path(WIRE)/'plan.json').read_bytes(); self.plan = contract.validate(self.raw)
        self.data = json.loads((Path(WIRE)/'data.json').read_bytes())
        self.temp = tempfile.TemporaryDirectory(); self.directory = Path(self.temp.name)
        self.home = self.directory/self.plan['Home'].lstrip('/'); self.home.mkdir(parents=True, mode=0o700)
        self.home.chmod(0o700)
        if os.getuid() == 0: os.chown(self.home, self.plan['Uid'], self.plan['Gid'])
        self.assertEqual(os.stat(self.home).st_uid, self.plan['Uid'])
        (self.directory/'boot').mkdir(); package_fixture(self.directory)
        (self.directory/'sentinel').write_bytes(b'unchanged fixture baseline')
        fd = os.open(self.directory, os.O_RDONLY | os.O_DIRECTORY); s = os.fstat(fd)
        self.view = root.RootView(fd, s.st_dev, s.st_ino, mount_id(fd)); os.close(fd)
        self.original = snapshot_manifest(self.view)
        self.configured = {'ProfileStep': 'Verify', 'MachineIdentity': 'FirstBootPending',
            'ChangedPaths': [self.plan['Home']], 'FilesystemDeltaSha256': contract.digest(contract.canonical(self.original['Entries'])),
            'PackageStateSha256': contract.digest(contract.canonical(self.original['Packages']))}
        self.image = b'fixture bytes only; not an initramfs qualification'
        (self.directory/successor.IMAGE_PATH.lstrip('/')).write_bytes(self.image)
        (self.directory/successor.IMAGE_PATH.lstrip('/')).chmod(0o600)
        # Test owner is explicit, matching the real image contract on root-run fixtures.
        self.init = {'Step': 'Verify', 'CompleteImageQualification': True,
            'ConfiguredEntriesSha256': self.configured['FilesystemDeltaSha256'],
            'PackageStateSha256': self.configured['PackageStateSha256'],
            'Image': {'Length': len(self.image), 'Sha256': contract.digest(self.image)},
            'ImageSemantics': {'ImageLength': len(self.image), 'ImageSha256': contract.digest(self.image)}}

    def tearDown(self):
        self.view.close(); self.temp.cleanup()

    def add_selected(self):
        for e in sorted(self.plan['Entries'], key=lambda e: (e['Destination'].count('/'), e['Destination'])):
            p = self.home/e['Destination']
            if e['Kind'] == 'Directory': p.mkdir(mode=0o700)
            else: p.write_bytes(base64.b64decode(self.data[e['Source']])); p.chmod(0o600)
            if os.getuid() == 0: os.chown(p, self.plan['Uid'], self.plan['Gid'])

    def test_real_walk_reproduces_old_rejection_and_exact_successor_acceptance(self):
        before = contract.canonical(self.original)
        self.add_selected()
        with self.assertRaisesRegex(ValueError, 'UnsupportedPathEncoding'): root.inspect_tree(self.view)
        policy = contract.SelectedDocumentPaths(self.raw)
        observed = root.inspect_tree(self.view, selected_documents=policy)
        self.assertIn(self.plan['Home']+'/Documents/résumé.txt', [e[0] for e in observed])
        baseline = successor.compose_predecessor(self.view, self.original, self.configured, self.init, transfer=self.raw)
        # Image metadata follows the canonical root-owned contract. Unprivileged
        # cheap tests still exercise the full walk and exact delta with their
        # explicitly synthetic image owner; native tests exercise UID 0.
        for e in baseline['Entries']:
            if e['Path'] == successor.IMAGE_PATH: e['Uid'] = os.getuid(); e['Gid'] = os.getgid()
        result = successor.verify_delta(self.view, baseline, self.raw, self.configured['PackageStateSha256'])
        self.assertTrue(result['ConfiguredAndInitramfsPreserved'])
        self.assertEqual(before, contract.canonical(self.original))
        with self.assertRaisesRegex(ValueError, 'UnsupportedPathEncoding'): root.inspect_tree(self.view)

    def test_unknown_unicode_and_spelling_substitution_remain_rejected(self):
        self.add_selected(); policy = contract.SelectedDocumentPaths(self.raw)
        p = self.home/'Documents/résumé.txt'; p.rename(self.home/'Documents/re\u0301sume\u0301.txt')
        with self.assertRaisesRegex(ValueError, 'UnsupportedPathEncoding'): root.inspect_tree(self.view, selected_documents=policy)

    def test_no_generic_skip_or_mutable_destination_override(self):
        with self.assertRaisesRegex(ValueError, 'InvalidSuccessorPathPolicy'): root.inspect_tree(self.view, selected_documents=True)
        with self.assertRaises((AttributeError, TypeError)): contract.SelectedDocumentPaths(self.raw).destinations = frozenset(['/anything'])
        with self.assertRaisesRegex(ValueError, 'ObservationPlanBytes'): contract.SelectedDocumentPaths(bytearray(self.raw))

    def test_normalized_collision_and_out_of_policy_paths(self):
        for name in ('../escape', 'Documents/file:stream', 'Documents/\u2603.txt', 'Documents/line\nfeed'):
            p = copy.deepcopy(self.plan); p['Entries'][-1]['Destination'] = name
            with self.assertRaises(ValueError): contract.SelectedDocumentPaths(contract.canonical(p))
        p = copy.deepcopy(self.plan); e = copy.deepcopy(next(e for e in p['Entries'] if 'résumé' in e['Destination']))
        e['Source'] = e['Source'].replace('résumé', 're\u0301sume\u0301'); e['Destination'] = e['Destination'].replace('résumé', 're\u0301sume\u0301')
        p['Entries'].append(e); p['Selection']['totalBytes'] += e['Length']
        with self.assertRaisesRegex(ValueError, 'Collision'): contract.SelectedDocumentPaths(contract.canonical(p))


if __name__ == '__main__': unittest.main()
