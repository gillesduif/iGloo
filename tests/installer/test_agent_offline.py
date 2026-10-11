"""Pure contract/precondition tests; never invoke the native installer."""
import base64
import copy
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest.mock import patch

from agent_offline import before, decode, source, validate
from agent_offline_observer import observe, scan_tree
from agent_offline_rehearsal import durable


class OfflineAgentContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = os.environ.get('IGLOO_AGENT_OFFLINE_PACKET')
        if not path:
            raise unittest.SkipTest('actual .NET-serialized fixture packet not supplied')
        cls.packet = decode(Path(path).read_bytes())

    def test_actual_dotnet_packet_has_pending_acyclic_state(self):
        plan, worker, unit = source(self.packet)
        self.assertTrue(plan['ConfigurationPending'] and plan['EnablePending'])
        self.assertFalse(plan['StartDuringInstallation'])
        self.assertEqual(2, len(plan['Files']))
        self.assertEqual(9342, len(worker))
        self.assertIn(b'ProtectSystem=strict', unit)

    def test_substitution_and_early_activation_rejected(self):
        for change in ('worker', 'unit', 'weakened-unit', 'enable', 'configuration', 'production', 'target', 'unknown', 'duplicate'):
            packet = copy.deepcopy(self.packet)
            plan = decode(base64.b64decode(packet['Declaration']))
            if change == 'worker': packet['Worker'] = base64.b64encode(b'legacy agent').decode()
            elif change == 'unit': packet['Unit'] = base64.b64encode(b'[Unit]\n[Service]\nPrivateNetwork=no\n').decode()
            elif change == 'weakened-unit':
                text = base64.b64decode(packet['Unit']).decode().replace('PrivateNetwork=yes\n',
                                                                         'PrivateNetwork=yes\nPrivateNetwork=no\n')
                packet['Unit'] = base64.b64encode(text.encode()).decode()
                plan['Files'][1]['Length'] = len(text.encode())
                from agent_offline import digest
                plan['Files'][1]['Sha256'] = digest(text.encode())
            elif change == 'enable': plan['EnablePending'] = False
            elif change == 'configuration': plan['ConfigurationPending'] = False
            elif change == 'production': plan['Scope'] = 'Production'
            elif change == 'target': plan['TargetRootId'] = '00000000-0000-0000-0000-000000000000'
            elif change == 'unknown': plan['Unexpected'] = True
            elif change == 'duplicate':
                raw = base64.b64decode(packet['Declaration'])
                packet['Declaration'] = base64.b64encode(raw[:-1] + b',"Scope":"Production"}').decode()
            if change not in ('worker', 'unit', 'duplicate'):
                packet['Declaration'] = base64.b64encode(json.dumps(plan, separators=(',', ':')).encode()).decode()
            with self.subTest(change=change), self.assertRaises((ValueError, UnicodeError)):
                source(packet)

    def test_changed_parent_and_destination_rejected_before_effects(self):
        with tempfile.TemporaryDirectory(prefix='igloo-agent-precondition-') as name:
            root = Path(name)
            for path in ('usr', 'usr/lib', 'etc', 'etc/systemd', 'etc/systemd/system',
                         'etc/systemd/system/multi-user.target.wants'):
                (root/path).mkdir(mode=0o755)
            fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
            try:
                self.assertTrue(before(fd)['WorkerAbsent'])
                self.assertIn('/etc/systemd/system', scan_tree(fd))
                unit = root/'etc/systemd/system/igloo-deployment.service'
                unit.write_bytes(b'wrong existing unit')
                with self.assertRaises(ValueError): before(fd)
                unit.unlink()
                (root/'usr/lib/igloo').symlink_to('/tmp')
                with self.assertRaises(ValueError): before(fd)
            finally: os.close(fd)

    def test_journal_flush_failure_cannot_be_a_successful_publication(self):
        with tempfile.TemporaryDirectory(prefix='igloo-agent-journal-') as name:
            fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY)
            try:
                with patch('agent_offline_rehearsal.os.fsync', side_effect=OSError('synthetic fsync failure')):
                    with self.assertRaises(OSError): durable(fd, 'intent.json', b'{"synthetic":true}')
                self.assertTrue((Path(name)/'intent.json').exists())
                with self.assertRaises(FileExistsError): durable(fd, 'intent.json', b'{"synthetic":false}')
            finally: os.close(fd)


class OfflineAgentObservedFixtureTests(unittest.TestCase):
    def test_real_installed_output_rejects_substitution_and_extra_delta(self):
        path = os.environ.get('IGLOO_AGENT_OFFLINE_RESULT')
        if not path:
            self.skipTest('accepted native fixture not supplied')
        original = Path(path)
        packet = decode((original/'packet.json').read_bytes())
        plan, worker, unit = source(packet)
        before_tree = decode((original/'before-tree.json').read_bytes())
        for change in ('unit', 'worker-mode', 'extra', 'unrelated', 'configuration', 'enable-link'):
            with self.subTest(change=change), tempfile.TemporaryDirectory(prefix='igloo-agent-observer-') as temp:
                copied = Path(temp)/'root'
                shutil.copytree(original/'root', copied, symlinks=True)
                for owned in ('home/iglootest', 'home/iglootest/Documents',
                              'home/iglootest/Documents/résumé.txt'):
                    os.chown(copied/owned, 1000, 1000)
                fd = os.open(copied, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    self.assertTrue(observe(fd, plan, worker, unit, before_tree)
                                    ['SourceAndUnrelatedContentUnchanged'])
                finally: os.close(fd)
                if change == 'unit': (copied/'etc/systemd/system/igloo-deployment.service').write_bytes(b'bad unit')
                elif change == 'worker-mode': (copied/'usr/lib/igloo/debian-agent.py').chmod(0o666)
                elif change == 'extra': (copied/'etc/extra.txt').write_bytes(b'extra')
                elif change == 'unrelated': (copied/'home/iglootest/Documents/résumé.txt').write_bytes(b'changed')
                elif change == 'configuration': (copied/'etc/igloo/deployment.json').write_bytes(b'{}')
                else: (copied/'etc/systemd/system/multi-user.target.wants/igloo-deployment.service').symlink_to('../igloo-deployment.service')
                fd = os.open(copied, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    with self.assertRaises(ValueError): observe(fd, plan, worker, unit, before_tree)
                finally: os.close(fd)


if __name__ == '__main__':
    unittest.main()
