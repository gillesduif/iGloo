"""Actual .NET wire to Python control flow; native effects/observers are doubles.

Run after the Migration serializer test with IGLOO_INITRAMFS_WIRE_FIXTURE set.
Ordinary files exercise the real create-new publisher. This is not native proof.
"""
import copy
from contextlib import ExitStack
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'distros/debian/native'))
import session_initramfs as adapter
from isolation_policy import canonical, digest

WIRE = os.environ.get('IGLOO_INITRAMFS_WIRE_FIXTURE')

@unittest.skipUnless(WIRE and sys.platform == 'linux' and os.geteuid() == 0,
                     'requires current .NET serializer export and Linux ordinary-file fixture')
class WireControlFlowTests(unittest.TestCase):
    def setUp(self):
        self.request = json.loads((Path(WIRE)/'request.json').read_bytes())
        self.bindings = json.loads((Path(WIRE)/'lease-response.json').read_bytes())['Leases']['Bindings']

    def test_wire_rejection_without_coercion(self):
        self.assertEqual(self.request['InitramfsPlan']['Candidate']['RootUuid'], adapter.bound_root_uuid(self.bindings))
        changes = [lambda b: b.pop(), lambda b: b.reverse(), lambda b: b[0].update(Role='Root'),
            lambda b: b[0].update(Role=False), lambda b: b[0].update(Access=True),
            lambda b: b[1].update(Role=0), lambda b: b[2].update(Access=1),
            lambda b: b[0].update(FileSystem='FAT32'), lambda b: b[1].update(FileSystem='EXT4'),
            lambda b: b[0].update(FileSystemUuid='not-a-uuid'), lambda b: b[1].update(FileSystemUuid=b[2]['FileSystemUuid']),
            lambda b: b.__setitem__(0, None)]
        for mutate in changes:
            value = copy.deepcopy(self.bindings); mutate(value)
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, 'CanonicalRootBinding'):
                adapter.bound_root_uuid(value)

    def exercise(self, failure=None):
        with tempfile.TemporaryDirectory() as directory, tempfile.TemporaryDirectory(dir='/dev/shm') as store_path, ExitStack() as stack:
            base = Path(directory); target = base/'root'; payload = base/'payload'; workspace = base/'workspace'
            (target/'boot').mkdir(parents=True); (target/'boot').chmod(0o755); payload.mkdir(); workspace.mkdir(mode=0o700)
            request = copy.deepcopy(self.request); prior = request['ConfiguredPredecessor']; plan = request['InitramfsPlan']
            folder = payload/'configured-root'/prior['Imported']['BuildId']/prior['Imported']['DerivationId']; folder.mkdir(parents=True)
            raw = b'fixture descriptor'; (folder/'descriptor.json').write_bytes(raw); prior['Imported']['DescriptorSha256'] = digest(raw)
            raw = canonical({'Entries': []}); (folder/'root.manifest.json').write_bytes(raw); prior['Imported']['ManifestSha256'] = digest(raw)
            views = {}
            for role, path in [('Root',target),('Payload',payload)]:
                fd = os.open(path, os.O_DIRECTORY); stack.callback(os.close, fd)
                views[role] = SimpleNamespace(fd=fd, expected=(os.fstat(fd).st_dev, os.fstat(fd).st_ino, 1))
            connected = SimpleNamespace(views=views, verify=Mock(), close=Mock())
            events = []; launches = []
            def ask(kind, **fields):
                events.append((kind, copy.deepcopy(fields)))
                if failure == 'intent' and kind == 'InitramfsCheckpoint' and fields['Record']['Step'] == 'Publication' and fields['Record']['Outcome'] == 'IntentDurable':
                    raise ValueError('fixture-intent-reopen-failure')
                return {'Reference': 'fixture-result.json', 'Sha256': 'A'*64}
            channel = SimpleNamespace(ask=ask, checkpoint=lambda value: events.append(('Session',value)))
            session = SimpleNamespace(initramfs_attempted=False, declaration=request, blocks=SimpleNamespace(bindings=self.bindings),
                supervisor=SimpleNamespace(receipts=[{'Role':'Root','Path':str(target)}, {'Role':'Payload','Path':str(payload)}]),
                runtime=SimpleNamespace(python='/fixture/python'), channel=channel)
            from package_broker import DirectoryLease
            def lease(path):
                return DirectoryLease(store_path if path == plan['Workspace'] else path)
            def execute(launch, resources, intent):
                launches.append(launch); intent('B'*64,'C'*64)
                (workspace/'candidate.img').write_bytes(b'explicit synthetic candidate, not a kernel image')
                (workspace/'candidate.img').chmod(0o600)
                return {'State':'Exited','ExitCode':0}
            def observed(argv, **kwargs):
                observation = json.loads(kwargs['input'])['InitramfsObservation']; step = observation['Step']
                if failure == 'observer' and step == 'Verify': return SimpleNamespace(returncode=1,stdout=b'')
                return SimpleNamespace(returncode=0, stdout=canonical({'OperationId':plan['OperationId'],
                    'PlanSha256':request['PlanSha256'], 'Step':step, 'RootIdentity':list(views['Root'].expected)}))
            stack.enter_context(patch.object(adapter.session_import,'ConnectedImportView',return_value=connected))
            stack.enter_context(patch.object(adapter.root,'validate_manifest',return_value={'Entries':[]}))
            stack.enter_context(patch.object(adapter.configured_successor,'configured_manifest',return_value={'Entries':[]}))
            stack.enter_context(patch.object(adapter.candidate,'verify_execution_inputs',side_effect=ValueError('private-details-must-not-be-recorded') if failure == 'between' else None))
            stack.enter_context(patch.object(adapter.candidate,'prepare_workspace',return_value=workspace))
            stack.enter_context(patch.object(adapter,'DirectoryLease',side_effect=lease))
            stack.enter_context(patch.object(adapter.os,'fstatvfs',return_value=SimpleNamespace(f_flag=0,f_bavail=16*1024**3,f_frsize=1,f_favail=100000)))
            stack.enter_context(patch.object(adapter,'PackageBroker',return_value=SimpleNamespace(execute=execute)))
            stack.enter_context(patch.object(adapter.subprocess,'run',side_effect=observed))
            if failure:
                with self.assertRaises(ValueError): adapter.perform(session)
            else:
                result = adapter.perform(session); self.assertEqual('fixture-result.json',result['Reference'])
            if failure in ('between', 'uuid'):
                self.assertEqual([],launches)
                self.assertEqual(('InitramfsStopped',{'Location':'GenerationInputs'}),events[-1])
                self.assertNotIn('private-details',json.dumps(events))
                completed = [f['Record']['Step'] for k,f in events if k=='InitramfsCheckpoint' and f['Record']['Outcome']=='AppliedAndVerified']
                self.assertEqual(['Baseline'],completed)
            else:
                self.assertEqual(1,len(launches))
                self.assertIn('UUID='+adapter.bound_root_uuid(self.bindings),launches[0].arguments)
                dest = target/'boot'/adapter.publication.LEAF
                self.assertEqual(failure != 'intent',dest.exists())
                outcomes = [(f['Record']['Step'],f['Record']['Outcome']) for k,f in events if k=='InitramfsCheckpoint']
                if not failure:
                    self.assertEqual([(s,o) for s in ('Baseline','Generation','Candidate','Publication','Verify') for o in ('IntentDurable','AppliedAndVerified')],outcomes)
                    self.assertEqual('AppliedAndVerified',events[-1][1]['State'])
                else:
                    self.assertNotIn(('Verify','AppliedAndVerified'),outcomes)
            with self.assertRaisesRegex(ValueError,'SingleUse'): adapter.perform(session)

    def test_actual_wire_baseline_generation_publication_verify_handoff(self): self.exercise()
    def test_between_step_exception_is_bounded_and_retains_last_completed(self): self.exercise('between')
    def test_valid_but_contradictory_root_uuid_never_reaches_generation(self):
        self.bindings[0]['FileSystemUuid'] = '00000000-0000-0000-0000-000000000001'
        self.exercise('uuid')

    def test_publication_intent_reopen_failure_prevents_write(self): self.exercise('intent')
    def test_final_observer_failure_cannot_complete(self): self.exercise('observer')

if __name__ == '__main__': unittest.main()
