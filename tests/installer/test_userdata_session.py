"""Actual .NET session/context JSON to real adapter; native effects are doubles."""
import base64
from contextlib import ExitStack
import copy
import json
import os
import stat
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'distros/debian/native'))
import session_userdata as adapter
import userdata_contract as contract
import canonical_userdata_readback as handoff
from test_userdata_admission import synthetic_records

WIRE = os.environ.get('IGLOO_USERDATA_SESSION_WIRE')


@unittest.skipUnless(WIRE, 'Current .NET session and context serializer required')
class UserDataSessionTests(unittest.TestCase):
    def setUp(self):
        directory = Path(WIRE)
        self.request = json.loads((directory/'request.json').read_bytes())
        self.authority = (directory/'context.json').read_bytes()
        self.transfer = (directory/'plan.json').read_bytes()

    def test_real_plan_context_and_numeric_roles(self):
        plan, previous, transfer = adapter.profile(self.request)
        self.assertEqual(self.transfer, transfer)
        context = contract.declaration(self.authority, transfer)
        self.assertEqual(plan['InitramfsSha256'], previous['ResultSha256'])
        self.assertEqual(self.request['SessionId'], context['SessionId'])
        self.assertEqual([0, 1, 2], [b['Role'] for b in context['Bindings']])
        self.assertIsNone(self.request['InitramfsPlan']); self.assertIsNone(self.request['ConfigurationPlan'])
        self.assertIsNone(self.request['ImportPlan'])

    def test_actual_session_context_constructor_and_cloned_store_mount(self):
        # Exercise the real exported session type, context parser and constructor.
        # Only filesystem acquisition/observations are synthetic boundaries.
        from block_session import MountSession
        session = object.__new__(MountSession)
        session.declaration = copy.deepcopy(self.request)
        session.channel = SimpleNamespace(action='TransferUserData')
        session.failed = session.teardown = False; session.acquired = True
        d = contract.declaration(self.authority, self.transfer)
        session.blocks = SimpleNamespace(bindings=d['Bindings'])
        witness = session.declaration['UserDataStore']
        current = [witness['Device'], witness['Inode'], witness['MountId']+100]
        observed = self.store_observation(witness, current)
        session.supervisor = SimpleNamespace(namespace=9999, _observe=Mock(return_value=observed),
            receipts=[{'Role': 'Root', 'Path': '/synthetic/root'}, {'Role': 'Payload', 'Path': '/synthetic/payload'}])
        identities = {1: [10, 11, 12], 2: [20, 21, 22], 3: [20, 31, 22], 4: current}
        p = contract.validate(self.transfer)
        connected = SimpleNamespace(views={'Root': SimpleNamespace(fd=2), 'Payload': SimpleNamespace(fd=1)}, verify=Mock())
        acquired = {'SourceIdentity': identities[1], 'TargetIdentity': identities[2], 'HomeIdentity': identities[3],
            'Accounts': {}, 'SourceBefore': {}, 'DestinationBefore': {}}
        leases = []
        def lease(path):
            value = SimpleNamespace(path=path, fd=len(leases)+1, verify=Mock(), close=Mock())
            leases.append(value); return value
        def metadata(fd):
            return SimpleNamespace(st_dev=identities[fd][0], st_uid=p['Uid'] if fd == 3 else 0,
                st_gid=p['Gid'] if fd == 3 else 0, st_mode=stat.S_IFDIR | 0o700)
        with ExitStack() as stack:
            stack.enter_context(patch.object(adapter.source, 'observe', return_value=acquired))
            stack.enter_context(patch.object(adapter, 'DirectoryLease', side_effect=lease))
            stack.enter_context(patch.object(adapter.producer, 'identity', side_effect=lambda fd: identities[fd]))
            stack.enter_context(patch.object(adapter.producer, 'account', return_value={}))
            stack.enter_context(patch.object(adapter.os, 'fstat', side_effect=metadata))
            stack.enter_context(patch.object(adapter.os, 'fstatvfs', return_value=SimpleNamespace(
                f_flag=os.ST_RDONLY, f_bavail=100000, f_frsize=4096, f_favail=100000)))
            context = adapter.CanonicalContext(session, connected, self.authority)
            self.assertEqual(p, context.plan); self.assertFalse(context.used)
            self.assertNotEqual(witness['MountId'], current[2])
            context.verify(); context.close()
        self.assertTrue(all(x.close.called for x in leases))
        with self.assertRaisesRegex(ValueError, 'CanonicalSessionRequired'):
            adapter.CanonicalContext(SimpleNamespace(), connected, self.authority)

    @staticmethod
    def store_observation(witness, current):
        device = f"{witness['Major']}:{witness['Minor']}"
        return {'Namespace': 9999, 'Paths': [{'Path': witness['Path'], 'Identity': current[:2],
            'MountId': current[2], 'Device': device}], 'Mounts': [{'Id': current[2], 'Path': '/lab-journal',
            'Device': device, 'Root': '/', 'FileSystem': 'ext4', 'Options': ['rw', 'nodev', 'nosuid', 'noexec'], 'Propagation': []}]}

    def test_cloned_journal_requires_fresh_namespace_path_and_exact_mount(self):
        witness = self.request['UserDataStore']; current = [witness['Device'], witness['Inode'], witness['MountId']+100]
        observed = self.store_observation(witness, current)
        adapter.verify_store_mount(observed, 9999, witness, current)
        changes = [lambda o: o.update(Namespace=1), lambda o: o['Paths'][0].update(Identity=[0, 0]),
            lambda o: o['Paths'][0].update(MountId=witness['MountId']),
            lambda o: o['Mounts'][0].update(Device='0:0'), lambda o: o['Mounts'][0].update(Root='/alias'),
            lambda o: o['Mounts'][0].update(Options=['rw', 'nodev', 'nosuid']),
            lambda o: o['Mounts'][0].update(Propagation=['shared:1']),
            lambda o: o['Mounts'].append({**o['Mounts'][0], 'Id': 123456, 'Path': '/alias'}),
            lambda o: o['Mounts'].append({**o['Mounts'][0], 'Id': 123456, 'Device': '1:1', 'Path': '/lab-journal/child'})]
        for change in changes:
            value = copy.deepcopy(observed); change(value)
            with self.assertRaises(ValueError): adapter.verify_store_mount(value, 9999, witness, current)

    def test_wrong_predecessor_scope_generation_and_account_rejected(self):
        changes = [lambda r: r['UserDataPlan'].update(InitramfsSha256='0'*64),
            lambda r: r['LabUserData'].update(Version=4), lambda r: r.update(GenerationId='00000000-0000-0000-0000-000000000001'),
            lambda r: r['InitramfsPredecessor']['Configured']['Plan'].update(UserId=1001)]
        for mutate in changes:
            r = copy.deepcopy(self.request); mutate(r)
            with self.assertRaises(ValueError): adapter.profile(r)

    def exercise(self, fault=None, real_context=False):
        request = copy.deepcopy(self.request); p = contract.validate(self.transfer)
        # Synthetic protected-stage identity; the wire export has no real native
        # tool placement. The native rehearsal hashes and loads the actual files.
        request['ToolHashes'][adapter.__file__] = contract.digest(Path(adapter.__file__).read_bytes())
        descriptor = b'explicit synthetic authenticated-descriptor boundary'
        request['InitramfsPredecessor']['Configured']['Imported']['DescriptorSha256'] = contract.digest(descriptor)
        records, receipt, bundle = synthetic_records(self.transfer, self.authority)
        names = [f'{i:08d}-{contract.digest(r)}.json' for i, r in enumerate(records)]
        lookup = dict(zip(names, records)); lookup['userdata-receipt.json'] = receipt
        events = []; publications = []; transfers = []
        def ask(kind, **fields):
            events.append((kind, copy.deepcopy(fields)))
            if kind == 'UserDataContext': return {'Declaration': base64.b64encode(self.authority).decode()}
            if kind == 'UserDataCheckpoint' and fields['Record']['Step'] == fault and fields['Record']['Outcome'] == 'IntentDurable':
                raise ValueError('injected-intent-reopen-failure')
            return {'Reference': 'fixture-only-reference.json', 'Sha256': 'A'*64}
        channel = SimpleNamespace(ask=ask, checkpoint=lambda r: events.append(('Session', r)))
        views = {k: SimpleNamespace(fd=n, expected=(n, n+10, n+20)) for k, n in (('Root', 101), ('Payload', 102))}
        connected = SimpleNamespace(views=views, verify=Mock(), close=Mock())
        context = SimpleNamespace(plan=p, source=SimpleNamespace(fd=103), root=SimpleNamespace(fd=104), home=SimpleNamespace(fd=105),
            store=SimpleNamespace(fd=106), accounts={}, source_before={}, before={}, verify=Mock(), close=Mock())
        session = SimpleNamespace(declaration=request, userdata_attempted=False, runtime=SimpleNamespace(python='/fixture/python'), channel=channel)
        leases = []
        if real_context:
            from block_session import MountSession
            actual = object.__new__(MountSession); actual.__dict__.update(session.__dict__); session = actual
            channel.action = 'TransferUserData'; session.failed = session.teardown = False; session.acquired = True
            d = contract.declaration(self.authority, self.transfer)
            session.blocks = SimpleNamespace(bindings=d['Bindings'])
            witness = request['UserDataStore']; current = [witness['Device'], witness['Inode'], witness['MountId']+100]
            session.supervisor = SimpleNamespace(namespace=9999,
                _observe=Mock(return_value=self.store_observation(witness, current)),
                receipts=[{'Role': 'Root', 'Path': '/synthetic/root'}, {'Role': 'Payload', 'Path': '/synthetic/payload'}])
            identities = {103: [10, 11, 12], 104: [20, 21, 22], 105: [20, 31, 22], 106: current}
            def lease(path):
                value = SimpleNamespace(path=path, fd=103+len(leases), verify=Mock(), close=Mock())
                leases.append(value); return value
            def metadata(fd):
                return SimpleNamespace(st_dev=identities[fd][0], st_uid=p['Uid'] if fd == 105 else 0,
                    st_gid=p['Gid'] if fd == 105 else 0, st_mode=stat.S_IFDIR | 0o700)
        def generated(c):
            transfers.append(1)
            if fault == 'Producer': raise ValueError('injected-producer-failure')
            return {'ResultSha256': contract.digest(records[-1])}
        def child(argv, **kwargs):
            r = json.loads(kwargs['input'])['UserDataObservation']; step = r['Step']
            if fault == 'Observer' and step == 'Verify': return SimpleNamespace(returncode=1, stdout=b'')
            if fault == 'TransferObserver' and step == 'Transfer':
                return SimpleNamespace(returncode=1, stdout=contract.canonical({'UserDataObserverFailure': 1,
                    'Step': step, 'Code': 'UnsupportedPathEncoding', 'ExecutionSha256': request['ExecutionSha256'],
                    'ObserverSha256': request['ToolHashes'][adapter.__file__]}))
            o = {'OperationId': p['OperationId'], 'SessionId': request['SessionId'], 'PlanSha256': request['PlanSha256'],
                'Step': step, 'RootIdentity': list(views['Root'].expected), 'AuthoritySha256': contract.digest(self.authority),
                'FilesystemSha256': 'C'*64, 'PackageStateSha256': request['InitramfsPredecessor']['Observation']['PackageStateSha256']}
            if step in ('Admission', 'Verify'): o['Admission'] = contract.validate_admitted_bundle(bundle, receipt, self.authority)
            return SimpleNamespace(returncode=0, stdout=contract.canonical(o))
        def publish(*args):
            publications.append(1)
            if fault == 'Publication': raise OSError('injected-publication-failure')
            return contract.validate_admitted_bundle(bundle, receipt, self.authority)
        with ExitStack() as stack:
            stack.enter_context(patch.object(adapter.session_import, 'ConnectedImportView', return_value=connected))
            if real_context:
                stack.enter_context(patch.object(adapter.source, 'observe', return_value={
                    'SourceIdentity': identities[103], 'TargetIdentity': identities[104], 'HomeIdentity': identities[105],
                    'Accounts': {}, 'SourceBefore': {}, 'DestinationBefore': {}}))
                stack.enter_context(patch.object(adapter, 'DirectoryLease', side_effect=lease))
                stack.enter_context(patch.object(adapter.producer, 'account', return_value={}))
                stack.enter_context(patch.object(adapter.os, 'fstat', side_effect=metadata))
                stack.enter_context(patch.object(adapter.os, 'fstatvfs', return_value=SimpleNamespace(
                    f_flag=os.ST_RDONLY, f_bavail=100000, f_frsize=4096, f_favail=100000)))
            else:
                error = {'ContextImport': ImportError, 'ContextAttribute': AttributeError}.get(fault)
                stack.enter_context(patch.object(adapter, 'CanonicalContext', return_value=context,
                    side_effect=error('synthetic missing adapter dependency') if error else None))
            stack.enter_context(patch.object(adapter, 'beneath', return_value=110))
            stack.enter_context(patch.object(adapter.os, 'close'))
            stack.enter_context(patch.object(adapter.os, 'listdir', return_value=names+['plan.sha256']))
            stack.enter_context(patch.object(adapter.producer, 'identity', side_effect=
                (lambda fd: identities[fd]) if real_context else (lambda fd: [fd, fd+1, fd+2])))
            stack.enter_context(patch.object(adapter.session_import, 'read_bound', return_value=descriptor))
            stack.enter_context(patch.object(adapter.producer, '_transfer', side_effect=generated))
            stack.enter_context(patch.object(adapter.journal, 'read', side_effect=lambda fd, name: lookup[name]))
            stack.enter_context(patch.object(adapter.subprocess, 'run', side_effect=child))
            # Exact admission record construction/terminal validation still run.
            stack.enter_context(patch.object(adapter.admission, 'before', return_value={'SyntheticNativeBoundary': True}))
            stack.enter_context(patch.object(adapter.admission, 'publish', side_effect=publish))
            if fault:
                with self.assertRaises((OSError, ValueError, ImportError, AttributeError)): adapter.perform(session)
            else:
                result = adapter.perform(session); self.assertEqual('fixture-only-reference.json', result['Reference'])
            with self.assertRaisesRegex(ValueError, 'SingleUse'): adapter.perform(session)
        self.assertTrue(connected.close.called)
        if fault in ('Transfer', 'ContextImport', 'ContextAttribute'): self.assertFalse(transfers); self.assertFalse(publications)
        if fault in ('ContextImport', 'ContextAttribute'):
            self.assertEqual(('UserDataStopped', {}), events[-1])
            self.assertEqual(['Baseline'], [f['Record']['Step'] for k, f in events
                if k == 'UserDataCheckpoint' and f['Record']['Outcome'] == 'AppliedAndVerified'])
            self.assertFalse(any(k == 'UserDataCheckpoint' and f['Record']['Step'] == 'Transfer' for k, f in events))
        if fault == 'Admission': self.assertEqual([1], transfers); self.assertFalse(publications)
        if fault == 'TransferObserver':
            self.assertEqual([1], transfers); self.assertFalse(publications)
            record = events[-1][1]['Record']
            self.assertEqual(('Transfer', 'OutcomeUnknown'), (record['Step'], record['Outcome']))
            self.assertEqual('UnsupportedPathEncoding', record['ObserverFailure']['Code'])
            self.assertEqual(request['ExecutionSha256'], record['ObserverFailure']['ExecutionSha256'])
            self.assertFalse(any(k == 'UserDataCheckpoint' and f['Record']['Step'] == 'Admission' for k, f in events))
        if not fault:
            completed = [f['Record']['Step'] for k, f in events if k == 'UserDataCheckpoint' and f['Record']['Outcome'] == 'AppliedAndVerified']
            self.assertEqual(['Baseline', 'Transfer', 'Admission', 'Verify'], completed)
            self.assertEqual('AppliedAndVerified', events[-1][1]['State'])
        else:
            self.assertFalse(any(k == 'Session' and f.get('State') == 'AppliedAndVerified' for k, f in events))
            self.assertIn(events[-1][0], ('UserDataStopped', 'UserDataCheckpoint'))

    def test_complete_wire_dispatch_to_producer_admission_verify_handoff(self): self.exercise()
    def test_intent_reopen_prevents_transfer(self): self.exercise('Transfer')
    def test_admission_intent_reopen_prevents_publication(self): self.exercise('Admission')
    def test_producer_failure_cannot_admit(self): self.exercise('Producer')
    def test_publication_failure_cannot_complete(self): self.exercise('Publication')
    def test_final_observation_failure_cannot_complete(self): self.exercise('Observer')
    def test_post_transfer_observer_failure_blocks_admission_and_records_identity(self): self.exercise('TransferObserver')
    def test_context_import_failure_records_stopped_without_transfer(self): self.exercise('ContextImport')
    def test_context_attribute_failure_records_stopped_without_transfer(self): self.exercise('ContextAttribute')
    def test_baseline_to_real_constructor_then_transfer(self): self.exercise(real_context=True)

    def test_journal_rejects_stacked_mount_of_another_device(self):
        witness = self.request['UserDataStore']; current = [witness['Device'], witness['Inode'], witness['MountId']]
        observed = self.store_observation(witness, current)
        # Equality with the parent ID is legal too; only the fresh join matters.
        adapter.verify_store_mount(observed, 9999, witness, current)
        observed['Mounts'].append({**observed['Mounts'][0], 'Id': current[2]+1, 'Device': '1:1'})
        with self.assertRaisesRegex(ValueError, 'UserDataJournalMountChanged'):
            adapter.verify_store_mount(observed, 9999, witness, current)

    def test_reopened_handoff_requires_terminal_admission_and_exact_teardown(self):
        request = self.request; plan = request['UserDataPlan']; generation = request['GenerationId']
        records, receipt, bundle = synthetic_records(self.transfer, self.authority)
        admission = contract.validate_admitted_bundle(bundle, receipt, self.authority)
        context = contract.declaration(self.authority, self.transfer)
        for fault in (None, 'producer', 'teardown', 'admission', 'missing', 'close'):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                d = Path(directory); evidence = d/'execution'; evidence.mkdir(); journals = d/'journals'
                (evidence/'userdata.stdout').write_text(json.dumps({'Phase': 'UserDataPreflight', 'Plan': plan,
                    'PlanSha256': request['PlanSha256'], 'SessionId': request['SessionId']})+'\n')
                for store in ('session', 'effects'):
                    (journals/store/generation).mkdir(parents=True)
                    (journals/store/generation/'plan.sha256').write_bytes(request['PlanSha256'].encode())
                producer = journals/'effects'/plan['OperationId']; producer.mkdir()
                (producer/'plan.sha256').write_bytes(contract.digest(self.transfer).encode())
                (journals/'effects/userdata-receipt.json').write_bytes(receipt)
                for i, raw in enumerate(records):
                    (producer/f'{i:08d}-{contract.digest(raw)}.json').write_bytes(raw if fault != 'producer' else raw+b' ')
                previous = {'session': None, 'effects': None}; counts = {'session': 0, 'effects': 0}
                def append(store, fields):
                    index = counts[store]
                    raw = contract.canonical({'SchemaVersion': 5, 'GenerationId': generation,
                        'SessionId': request['SessionId'], 'OperationId': plan['OperationId'], 'PlanSha256': request['PlanSha256'],
                        'Provenance': plan['Provenance'], 'InitramfsSha256': plan['InitramfsSha256'],
                        'InitramfsCloseSha256': plan['InitramfsCloseSha256'], 'Sequence': index, 'PreviousSha256': previous[store], **fields})
                    name = f'{index:08d}-{contract.digest(raw)}.json'; (journals/store/generation/name).write_bytes(raw)
                    previous[store] = contract.digest(raw); counts[store] += 1
                    return name
                final = None
                for step in ('Baseline', 'Transfer', 'Admission', 'Verify'):
                    if fault == 'missing' and step == 'Admission': continue
                    o = {'OperationId': plan['OperationId'], 'SessionId': request['SessionId'], 'PlanSha256': request['PlanSha256'],
                        'Step': step, 'AuthoritySha256': contract.digest(self.authority), 'FilesystemSha256': 'C'*64,
                        'PackageStateSha256': 'D'*64, 'ConfiguredAndInitramfsPreserved': True, 'MachineIdentity': 'FirstBootPending'}
                    if step in ('Admission', 'Verify'):
                        o['Admission'] = copy.deepcopy(admission)
                        if fault == 'admission': o['Admission']['EvidenceSha256'] = '0'*64
                    e = {'ObserverEvidence': o, 'ObserverEvidenceSha256': contract.digest(contract.canonical(o))}
                    if step == 'Transfer': e.update(Bundle=base64.b64encode(bundle).decode(), Receipt=base64.b64encode(receipt).decode())
                    for outcome in ('IntentDurable', 'AppliedAndVerified'):
                        final = append('effects', {'Step': step, 'Outcome': outcome, 'AuthoritySha256': contract.digest(self.authority),
                            'Bindings': context['Bindings'], 'ProtectedStateSha256': 'F'*64, 'Evidence': e})
                root, payload = {'Id': 1, 'Path': '/fixture-root'}, {'Id': 2, 'Path': '/fixture-payload'}
                for name, path, before, after in (('UnmountPayload', payload['Path'], [root, payload], [root]),
                    ('UnmountRoot', root['Path'], [root], [])):
                    append('session', {'Action': name, 'State': 'OutcomeUnknown' if fault == 'teardown' else 'AppliedAndVerified',
                        'Evidence': {'Path': path, 'Before': {'Mounts': before}, 'After': {'Mounts': after}}})
                append('session', {'Action': 'Close', 'State': 'AppliedAndVerified', 'UserDataResultReference': 'wrong' if fault == 'close' else final})
                if fault:
                    with self.assertRaises((ValueError, KeyError)): handoff.verify(evidence, journals)
                else:
                    result = handoff.verify(evidence, journals)
                    self.assertEqual('AppliedAndVerified', result['Teardown']); self.assertFalse(result['FirstBootSucceeded'])


if __name__ == '__main__': unittest.main()
