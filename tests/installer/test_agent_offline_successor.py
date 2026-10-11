"""Small filesystem successor-observer regressions, without installation effects.

The baseline below is an explicitly synthetic subset. The canonical observer
still requires independently reopened complete UserData predecessor inputs.
"""
import base64
import os
from pathlib import Path
import shutil
import stat
import tempfile
import unittest

import agent_offline_successor as successor
import configured_root as root
from agent_offline import decode, digest, identity, source
from userdata_contract import SelectedDocumentPaths


def fixture_entry(path, info, target, attrs, content):
    kind = 'Directory' if stat.S_ISDIR(info.st_mode) else 'SymbolicLink' if stat.S_ISLNK(info.st_mode) else 'File'
    if attrs:
        raise AssertionError('Synthetic fixture must have no extended attributes')
    return {'Path': path, 'Type': kind, 'Uid': info.st_uid, 'Gid': info.st_gid,
            'Mode': stat.S_IMODE(info.st_mode), 'Length': info.st_size if kind == 'File' else None,
            'Sha256': content if kind == 'File' else None,
            'Target': target if kind == 'SymbolicLink' else None, 'Xattrs': {}}


class OfflineAgentSuccessorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        packet = os.environ.get('IGLOO_AGENT_CANONICAL_TEST_PACKET')
        fixture = os.environ.get('IGLOO_AGENT_OFFLINE_RESULT')
        transfer = os.environ.get('IGLOO_AGENT_USERDATA_TRANSFER')
        if not packet or not fixture or not transfer:
            raise unittest.SkipTest('real .NET packet, accepted fixture and selected transfer required')
        cls.packet = decode(Path(packet).read_bytes())
        cls.plan, cls.worker, cls.unit = source(cls.packet, scope='LabCanonicalOfflineAgentStageV1')
        cls.fixture = Path(fixture)/'root'
        cls.transfer = Path(transfer).read_bytes()
        cls.policy = SelectedDocumentPaths(cls.transfer)

    def prepared(self, directory):
        copied = Path(directory)/'root'
        shutil.copytree(self.fixture, copied, symlinks=True)
        for owned in ('home/iglootest', 'home/iglootest/Documents',
                      'home/iglootest/Documents/résumé.txt'):
            os.chown(copied/owned, 1000, 1000)
        (copied/'usr/lib/igloo/debian-agent.py').unlink()
        (copied/'usr/lib/igloo').rmdir()
        (copied/'etc/systemd/system/igloo-deployment.service').unlink()
        fd = os.open(copied, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            view = root.RootView(fd, *identity(fd)[:3])
            try:
                baseline = {'SchemaVersion': 2,
                    'Entries': [fixture_entry(*entry) for entry in root.inspect_tree(view, selected_documents=self.policy)]}
            finally: view.close()
        finally: os.close(fd)
        (copied/'usr/lib/igloo').mkdir(mode=0o755)
        for path, raw in (('usr/lib/igloo/debian-agent.py', self.worker),
                          ('etc/systemd/system/igloo-deployment.service', self.unit)):
            item = copied/path
            item.write_bytes(raw); item.chmod(0o644)
        return copied, baseline

    def verified(self, copied, baseline):
        expected, plan, worker, unit = successor.expected_delta(baseline, self.packet)
        self.assertEqual(self.plan, plan)
        self.assertEqual(self.worker, worker)
        self.assertEqual(self.unit, unit)
        fd = os.open(copied, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            view = root.RootView(fd, *identity(fd)[:3])
            try: root.verify_tree(view, expected, selected_documents=self.policy)
            finally: view.close()
        finally: os.close(fd)
        return expected

    def test_accented_selected_path_survives_full_walk_and_exact_delta(self):
        with tempfile.TemporaryDirectory(prefix='igloo-agent-successor-') as folder:
            copied, baseline = self.prepared(folder)
            with self.assertRaises(ValueError): root.path_parts('/home/iglootest/Documents/résumé.txt')
            expected = self.verified(copied, baseline)
            self.assertIn('/home/iglootest/Documents/résumé.txt', {e['Path'] for e in expected['Entries']})
            self.assertEqual(digest(self.worker), next(e['Sha256'] for e in expected['Entries']
                                                       if e['Path'] == '/usr/lib/igloo/debian-agent.py'))

    def test_unrelated_change_and_premature_activation_rejected(self):
        for change in ('worker', 'unit-mode', 'extra', 'selected', 'configuration', 'enable'):
            with self.subTest(change=change), tempfile.TemporaryDirectory(prefix='igloo-agent-rejection-') as folder:
                copied, baseline = self.prepared(folder)
                self.verified(copied, baseline)
                if change == 'worker': (copied/'usr/lib/igloo/debian-agent.py').write_bytes(b'changed')
                elif change == 'unit-mode': (copied/'etc/systemd/system/igloo-deployment.service').chmod(0o666)
                elif change == 'extra': (copied/'etc/extra').write_bytes(b'undeclared')
                elif change == 'selected': (copied/'home/iglootest/Documents/résumé.txt').write_bytes(b'changed')
                elif change == 'configuration': (copied/'etc/igloo/deployment.json').write_bytes(b'{}')
                else: (copied/'etc/systemd/system/multi-user.target.wants/igloo-deployment.service').symlink_to('../igloo-deployment.service')
                with self.assertRaises(ValueError): self.verified(copied, baseline)

    def test_fixture_scope_cannot_enter_canonical_successor(self):
        changed = dict(self.packet)
        declaration = decode(base64.b64decode(changed['Declaration'], validate=True))
        declaration['Scope'] = 'FixtureOnlyOfflineAgentStageV1'
        import json
        changed['Declaration'] = base64.b64encode(json.dumps(declaration, separators=(',', ':')).encode()).decode()
        with tempfile.TemporaryDirectory(prefix='igloo-agent-scope-') as folder:
            _, baseline = self.prepared(folder)
            with self.assertRaises(ValueError): successor.expected_delta(baseline, changed)


if __name__ == '__main__':
    unittest.main()
