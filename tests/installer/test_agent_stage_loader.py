"""Source-level protected-loader join; no installer or canonical effect authority."""
import hashlib
import base64
from contextlib import redirect_stdout
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch


PACKET = os.environ.get('IGLOO_AGENT_CANONICAL_TEST_PACKET')
TRANSFER = os.environ.get('IGLOO_AGENT_USERDATA_TRANSFER')
SOURCE = Path(__file__).resolve().parents[2] / 'distros/debian/native'


@unittest.skipUnless(PACKET, 'current .NET canonical test packet required')
class AgentStageLoaderTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp(prefix='agent-stage-source-', dir='/var/lib/igloo/agent-stage-source-tests'))
        for original in SOURCE.glob('*.py'):
            shutil.copyfile(original, self.directory / original.name)
        self.hashes = {str(path): hashlib.sha256(path.read_bytes()).hexdigest().upper()
                       for path in self.directory.glob('*.py')}
        spec = importlib.util.spec_from_file_location('agent_stage_test_entry', self.directory / 'session_entry.py')
        self.entry = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.entry)
        self.packet = json.loads(Path(PACKET).read_bytes())
        self.stage = json.loads(base64.b64decode(self.packet['Declaration'], validate=True))
        self.provenance = {'Provider': 'IsolatedFileBackedLab', 'Version': 6,
                           'Scope': 'OfflineAgentStage', 'RunId': '11111111-1111-1111-1111-111111111111',
                           'GenerationId': self.stage['GenerationId'], 'EvidenceSha256': 'A'*64}
        self.declaration = {'ToolHashes': self.hashes, 'LabAgentStage': self.provenance,
                            'AgentStagePlan': {'Provenance': self.provenance}, 'AgentStagePacket': self.packet,
                            'GenerationId': self.provenance['GenerationId'],
                            'PlanSha256': 'B'*64, 'ParentMountNamespace': 12345}

    def tearDown(self):
        shutil.rmtree(self.directory)

    def test_actual_loader_packet_scope_and_session_constructor(self):
        with patch.dict(sys.modules):
            self.entry.load_verified_modules(self.declaration)
            agent = sys.modules['session_agent_offline']
            blocks = sys.modules['block_session']
            stage, worker, unit = sys.modules['agent_offline'].source(
                self.packet, scope='LabCanonicalOfflineAgentStageV1')
            self.assertEqual('LabCanonicalOfflineAgentStageV1', stage['Scope'])
            self.assertTrue(worker and unit)
            self.assertIs(agent.successor, sys.modules['agent_offline_successor'])
            self.assertIs(agent.session_import, sys.modules['session_import'])
            self.assertEqual(self.provenance, blocks.lab_provenance(self.declaration))
            # The real constructor and exact class check run; only the native
            # namespace supervisor is replaced with an inert effect boundary.
            channel = SimpleNamespace(action='StageOfflineAgent', checkpoint=lambda _: None)
            with patch.object(blocks, 'ExactMountSupervisor', return_value=SimpleNamespace(receipts=[])):
                session = blocks.MountSession(self.declaration, SimpleNamespace(), channel)
            self.assertIs(type(session), blocks.MountSession)
            self.assertFalse(session.agent_stage_attempted)
            self.assertEqual('AgentCanonicalSessionRequired', self._rejected(agent, session))

    @staticmethod
    def _rejected(agent, session):
        try:
            agent.perform(session)
        except ValueError as error:
            return str(error)
        raise AssertionError('inactive session was accepted')

    def test_incomplete_stale_or_mixed_manifest_rejected_before_dispatch(self):
        for change in ('missing', 'changed', 'mixed'):
            with self.subTest(change=change), patch.dict(sys.modules):
                value = dict(self.declaration); value['ToolHashes'] = dict(self.hashes)
                if change == 'missing': value['ToolHashes'].pop(str(self.directory/'session_agent_offline.py'))
                elif change == 'changed': value['ToolHashes'][str(self.directory/'session_agent_offline.py')] = '0'*64
                else: value['LabUserData'] = {'Version': 5}
                if change == 'mixed':
                    self.entry.load_verified_modules(value)
                    with self.assertRaisesRegex(ValueError, 'AgentStageProvenanceInvalid'):
                        sys.modules['block_session'].lab_provenance(value)
                else:
                    with self.assertRaisesValueError():
                        self.entry.load_verified_modules(value)

    @unittest.skipUnless(TRANSFER, 'real serialized .NET UserData selection required')
    def test_baseline_stage_observe_result_and_failure_gate(self):
        with patch.dict(sys.modules):
            self.entry.load_verified_modules(self.declaration)
            agent = sys.modules['session_agent_offline']
            blocks = sys.modules['block_session']
            raw_transfer = Path(TRANSFER).read_bytes()
            selected = sys.modules['userdata_contract'].validate(raw_transfer)
            self.assertEqual(self.stage['GenerationId'], selected['GenerationId'])
            descriptor = b'synthetic descriptor boundary, not authenticated evidence'
            previous = {'ResultSha256': 'C'*64, 'CloseSha256': 'D'*64,
                        'Plan': {'Transfer': base64.b64encode(raw_transfer).decode(),
                                 'OperationId': selected['OperationId'],
                                 'Provenance': {'Provider': 'IsolatedFileBackedLab', 'Version': 5,
                                                'Scope': 'SelectedDocumentTrees'}},
                        'Initramfs': {'Configured': {'Imported': {'BuildId': self.stage['GenerationId'],
                            'DerivationId': '66666666-6666-6666-6666-666666666666',
                            'DescriptorSha256': hashlib.sha256(descriptor).hexdigest().upper()}}}}
            request = {**self.declaration, 'UserDataPredecessor': previous,
                       'AgentStagePlan': {**self.declaration['AgentStagePlan'],
                           'SchemaVersion': 1, 'OperationId': self.stage['OperationId'],
                           'UserDataSha256': previous['ResultSha256'],
                           'UserDataCloseSha256': previous['CloseSha256'],
                           'Stage': self.stage, 'ExecutionSha256': 'E'*64},
                       'ExecutionSha256': 'E'*64, 'SessionId': '55555555-5555-5555-5555-555555555555',
                       'EntryPath': str(self.directory/'session_entry.py')}
            self.assertEqual(self.stage, agent.profile(request)[3])
            for fail in (None, 'PublicationObserver'):
                with self.subTest(fail=fail):
                    events = []; writes = []
                    def ask(kind, **fields):
                        events.append((kind, fields))
                        return {'Reference': 'synthetic-effect-reference', 'Sha256': 'A'*64}
                    channel = SimpleNamespace(action='StageOfflineAgent', ask=ask,
                        checkpoint=lambda value: events.append(('Session', value)))
                    with patch.object(blocks, 'ExactMountSupervisor', return_value=SimpleNamespace(receipts=[])):
                        session = blocks.MountSession(request, SimpleNamespace(python='/usr/bin/python3'), channel)
                    session.acquired = True
                    views = {'Root': SimpleNamespace(fd=101, expected=(1, 2, 3)),
                             'Payload': SimpleNamespace(fd=102, expected=(4, 5, 6))}
                    connected = SimpleNamespace(views=views, verify=Mock(), close=Mock())
                    def observer(_, **kwargs):
                        step = json.loads(kwargs['input'])['AgentStageObservation']['Step']
                        if fail == 'PublicationObserver' and step == 'Publication':
                            return SimpleNamespace(returncode=1, stdout=b'', stderr=b'')
                        value = {'OperationId': self.stage['OperationId'], 'SessionId': request['SessionId'],
                                 'PlanSha256': request['PlanSha256'], 'Step': step,
                                 'RootIdentity': list(views['Root'].expected),
                                 'FilesystemSha256': ('B' if step == 'Baseline' else 'F')*64,
                                 'PackageStateSha256': 'D'*64}
                        return SimpleNamespace(returncode=0,
                            stdout=sys.modules['isolation_policy'].canonical(value), stderr=b'')
                    with patch.object(agent.session_import, 'ConnectedImportView', return_value=connected), \
                         patch.object(agent, 'beneath', return_value=103), \
                         patch.object(agent.session_import, 'read_bound', return_value=descriptor), \
                         patch.object(agent.os, 'close'), \
                         patch.object(agent.agent_offline, 'declaration', return_value={'SyntheticEffectBoundary': True}), \
                         patch.object(agent.agent_offline, 'publish', side_effect=lambda *a, **k: writes.append(1)), \
                         patch.object(agent.subprocess, 'run', side_effect=observer):
                        if fail:
                            with self.assertRaises(agent.ObserverFailure): agent.perform(session)
                        else:
                            self.assertEqual('StagedInputsPending', agent.perform(session)['Lifecycle'])
                        with self.assertRaisesRegex(ValueError, 'AgentCanonicalSessionRequired'):
                            agent.perform(session)
                    self.assertEqual([1], writes)
                    self.assertTrue(connected.close.called)
                    completed = [data['Record']['Step'] for kind, data in events
                                 if kind == 'AgentStageCheckpoint' and data['Record']['Outcome'] == 'AppliedAndVerified']
                    self.assertEqual(['Baseline'] if fail else ['Baseline', 'Publication', 'Verify'], completed)
                    self.assertEqual('OutcomeUnknown' if fail else 'AppliedAndVerified',
                                     events[-1][1]['Record']['Outcome'] if fail else events[-1][1]['State'])

            output = io.StringIO()
            with patch.object(agent, 'observe', side_effect=ValueError('UnsupportedPathEncoding')), \
                 redirect_stdout(output):
                self.assertEqual(1, agent.observe_entry({**request,
                    'AgentStageObservation': {'Step': 'Publication'}}))
            failure = json.loads(output.getvalue())
            self.assertEqual({'AgentStageObserverFailure', 'Step', 'Code', 'ExecutionSha256', 'ObserverSha256'}, set(failure))
            self.assertEqual('UnsupportedPathEncoding', failure['Code'])

    @staticmethod
    def assertRaisesValueError():
        return unittest.TestCase().assertRaises(ValueError)


if __name__ == '__main__':
    unittest.main()
