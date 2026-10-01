"""Actual .NET wire, pure consumer/delta tests and explicitly synthetic effects.

No storage authority, native acceptance pass or first-boot success is produced.
Ordinary-file publisher tests require the separately enabled isolated fixture.
"""
import base64
import copy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'distros/debian/native'))
import userdata_contract as contract
import userdata_admission as admission
import userdata_source as source
import userdata_successor as successor
import debian_first_boot as worker

WIRE = os.environ.get('IGLOO_USERDATA_ADMISSION_WIRE')


def synthetic_records(transfer, authority):
    """Doubles only for effects; real terminal/receipt encoding and validation."""
    p = contract.validate(transfer)
    base = {'SchemaVersion': 2, 'GenerationId': p['GenerationId'], 'OperationId': p['OperationId'],
        'PlanSha256': contract.digest(transfer), 'Scope': contract.SCOPE, 'AuthoritySha256': contract.digest(authority)}
    context = {'SourceIdentity': [11, 12, 13], 'TargetIdentity': [14, 15, 16], 'HomeIdentity': [14, 17, 16],
        'Accounts': {'passwd': 'A'*64, 'group': 'B'*64}, 'SourceBeforeSha256': 'C'*64,
        'DestinationBeforeSha256': 'D'*64, 'AuthoritySha256': contract.digest(authority)}
    intent = contract.canonical({**base, 'State': 'IntentDurable', **context})
    link = {'IntentReference': '00000000-'+contract.digest(intent)+'.json', 'IntentSha256': contract.digest(intent)}
    observation = {'GenerationId': p['GenerationId'], 'OperationId': p['OperationId'], 'PlanSha256': contract.digest(transfer),
        'BindingSha256': contract.digest(contract.canonical(context)), 'Files': sum(e['Kind']=='File' for e in p['Entries']),
        'Bytes': p['Selection']['totalBytes'], 'SourceUnchanged': True, 'UnrelatedDestinationUnchanged': True,
        'ContentAndOwnershipVerified': True, 'DestinationSha256': 'E'*64}
    observed = contract.canonical({**base, 'State': 'IndependentObservation', 'Observation': observation, **link})
    result = contract.canonical({**base, 'State': 'AppliedAndVerified', 'Files': observation['Files'], 'Bytes': observation['Bytes'],
        'ObservationReference': '00000001-'+contract.digest(observed)+'.json', 'ObservationSha256': contract.digest(observed),
        'BindingSha256': observation['BindingSha256'], **link})
    receipt = contract.canonical({'SchemaVersion': 1, 'GenerationId': p['GenerationId'], 'Kind': 'UserData',
        'State': 'AppliedAndVerified', 'EvidenceSha256': contract.digest(result)})
    records = [intent, observed, result]
    bundle = {'SchemaVersion': 1, 'Scope': contract.SCOPE, 'Authority': base64.b64encode(authority).decode(),
        'Transfer': base64.b64encode(transfer).decode(), 'Records': [base64.b64encode(r).decode() for r in records]}
    return records, receipt, contract.canonical(bundle)


@unittest.skipUnless(WIRE, 'Current .NET admission serializer export required')
class WireTests(unittest.TestCase):
    def setUp(self):
        self.path = Path(WIRE); self.transfer = (self.path/'plan.json').read_bytes()
        self.authority = (self.path/'declaration.json').read_bytes()
        self.plan = contract.validate(self.transfer)
        self.records, self.receipt, self.bundle = synthetic_records(self.transfer, self.authority)

    def test_actual_wire_mapping_and_terminal_return(self):
        d = contract.declaration(self.authority, self.transfer)
        self.assertEqual([0, 1, 2], [b['Role'] for b in d['Bindings']])
        self.assertTrue(any(e['Length'] > 65536 for e in self.plan['Entries']))
        result = contract.validate_admitted_bundle(self.bundle, self.receipt, self.authority)
        self.assertFalse(result['ProductionQualified']); self.assertFalse(result['FirstBootSucceeded'])
        self.assertEqual(3, result['ReopenedRecords'])
        self.assertEqual(4, contract.decode(self.records[-1])['Files'])
        if os.environ.get('IGLOO_USERDATA_ADMISSION_RETURN'):
            # This explicit export is synthetic protocol evidence, not admission.
            (self.path/'bundle.json').write_bytes(self.bundle); (self.path/'receipt.json').write_bytes(self.receipt)

    def test_exact_wire_roles_scopes_and_bindings(self):
        changes = [lambda d: d.update(Scope='FixtureOnlySelectedDocuments'),
            lambda d: d['Provenance'].update(Version=4), lambda d: d['Provenance'].update(Provider='Windows'),
            lambda d: d['Provenance'].update(Version=True), lambda d: d.pop('Provenance'),
            lambda d: d.update(SourcePrefix='runtime/documents'), lambda d: d['Account'].update(Home='/root'),
            lambda d: d['Provenance'].update(GenerationId='00000000-0000-0000-0000-000000000001'),
            lambda d: d['Bindings'][0].update(Role='Root'), lambda d: d['Bindings'][0].update(Role=False),
            lambda d: d['Bindings'][0].update(Access=0), lambda d: d['Bindings'][0].update(FileSystem='FAT32'),
            lambda d: d['Bindings'][0].update(FileSystemUuid='NOT-A-UUID'), lambda d: d['Bindings'].reverse(),
            lambda d: d['Bindings'].pop(), lambda d: d['Bindings'][2].update(Role=0),
            lambda d: d['Bindings'][1].update(FileSystemUuid=d['Bindings'][2]['FileSystemUuid']),
            lambda d: d['Bindings'][0]['StoragePartition'].update(OffsetBytes=True),
            lambda d: d['Bindings'][0]['StoragePartition'].update(OffsetBytes=1),
            lambda d: d['Bindings'][0]['StoragePartition'].update(PartitionGuid=d['Bindings'][1]['StoragePartition']['PartitionGuid']),
            lambda d: d['Bindings'][0]['StoragePartition']['Disk'].update(LogicalSectorSize=1),
            lambda d: d['Account'].update(Uid=True)]
        for change in changes:
            d = contract.decode(self.authority); change(d)
            with self.subTest(change=changes.index(change)), self.assertRaises((ValueError, KeyError, TypeError)):
                contract.declaration(contract.canonical(d), self.transfer)

    def test_fixture_and_rehashed_context_cannot_be_admitted(self):
        for change in ('fixture', 'authority', 'intent', 'incomplete', 'failed', 'observation', 'replay'):
            bundle = contract.decode(self.bundle); records = [contract.decode(r) for r in self.records]
            if change == 'fixture':
                for r in records: r['Scope'] = 'FixtureOnlySelectedDocuments'
            if change == 'authority': records[-1]['AuthoritySha256'] = '0'*64
            if change == 'intent': records[0]['HomeIdentity'][1] += 1
            if change == 'incomplete': records.pop(1)
            if change == 'failed': records[-1]['State'] = 'OutcomeUnknown'
            if change == 'observation': records[1]['Observation']['ContentAndOwnershipVerified'] = False
            if change == 'replay': records.append(records[-1])
            bundle['Records'] = [base64.b64encode(contract.canonical(r)).decode() for r in records]
            raw = contract.canonical(bundle)
            with self.subTest(change=change), self.assertRaises((ValueError, KeyError)):
                contract.validate_admitted_bundle(raw, self.receipt, self.authority)
            if os.environ.get('IGLOO_USERDATA_ADMISSION_RETURN'):
                (self.path/('rejected-'+change+'.json')).write_bytes(raw)

    def test_missing_mandatory_producers_still_block_first_boot(self):
        with self.assertRaises(ValueError):
            worker.validate_configuration({'SchemaVersion': 1, 'GenerationId': self.plan['GenerationId'],
                'Profile': worker.PROFILE, 'WorkerSha256': 'A'*64,
                'RequiredReceipts': [{'Kind': 'UserData', 'FileName': 'userdata.json', 'Sha256': contract.digest(self.receipt)}]})

    def test_exact_delta_leaves_os_and_home_complement_outside_small_tree_limits(self):
        home = self.plan['Home']
        baseline = {'Entries': [successor.directory_entry('/var'), successor.directory_entry('/var/lib'),
            successor.directory_entry(home, 1000, 1000, 0o700),
            successor.file_entry(successor.IMAGE_PATH, 123, 'A'*64, mode=0o600),
            {'Path': home+'/.face.icon', 'Type': 'SymbolicLink', 'Uid': 1000, 'Gid': 1000,
             'Mode': 0o777, 'Length': None, 'Sha256': None, 'Target': '.face', 'Xattrs': {}}] +
            [successor.file_entry('/usr/bin/fixture-'+str(i), 5, 'B'*64) for i in range(3000)]}
        before = copy.deepcopy(baseline)
        expected, added = successor.expected_delta(baseline, self.transfer, (self.bundle, self.receipt, self.authority))
        self.assertEqual(before, baseline)
        self.assertTrue(all(e in expected['Entries'] for e in baseline['Entries']))
        self.assertEqual(len(self.plan['Entries'])+4, len(added))
        with patch.object(successor.root, 'verify_tree', side_effect=ValueError('independent-delta-failure')):
            with self.assertRaises(ValueError): successor.verify_delta(None, baseline, self.transfer, 'A'*64)
        conflict = copy.deepcopy(baseline); conflict['Entries'].append(successor.directory_entry(home+'/Documents', 1000, 1000, 0o700))
        with self.assertRaises(ValueError): successor.expected_delta(conflict, self.transfer)

    def test_complete_selected_inventory_including_unselected_sentinel(self):
        inventory = {e['Source']: {k: e[k] for k in ('Kind', 'Length', 'Sha256')} for e in self.plan['Entries']}
        inventory['unselected.txt'] = {'Kind': 'File', 'Length': 7, 'Sha256': 'D'*64}
        source.selected_content(self.plan, inventory)
        inventory['OneDrive/Documents/unlisted.txt'] = inventory['unselected.txt']
        with self.assertRaises(ValueError): source.selected_content(self.plan, inventory)


@unittest.skipUnless(WIRE and os.environ.get('IGLOO_USERDATA_ADMISSION_NATIVE_UNIT') == '1',
                     'Explicit isolated ordinary-file fixture required')
class PublicationTests(WireTests):
    def setUp(self):
        super().setUp(); self.temp = tempfile.TemporaryDirectory(); self.root = Path(self.temp.name)/'root'
        (self.root/'var/lib').mkdir(parents=True); self.fd = os.open(self.root, os.O_RDONLY | os.O_DIRECTORY)
        self.intent = admission.declaration(self.fd, self.bundle, self.receipt, self.authority)
    def tearDown(self): os.close(self.fd); self.temp.cleanup()
    def publish(self, revalidate=lambda: None):
        return admission.publish(self.fd, self.bundle, self.receipt, self.authority, self.intent, revalidate)
    def test_create_new_exact_path_reopen_and_duplicate_rejection(self):
        self.publish(); observed = admission.observe(self.fd, self.authority)
        self.assertEqual(contract.digest(self.bundle), observed['EvidenceSha256'])
        with self.assertRaises(ValueError): self.publish()
    def test_parent_link_rejected(self):
        (self.root/'var/lib/igloo').symlink_to('/tmp')
        with self.assertRaises((ValueError, OSError)): self.publish()
    def test_immediate_revalidation_failure_prevents_effect(self):
        def fail(): raise ValueError('intent-or-fresh-witness-rejected')
        with self.assertRaises(ValueError): self.publish(fail)
        self.assertFalse((self.root/'var/lib/igloo').exists())
    def test_write_failure_retains_partial_state_without_receipt(self):
        with patch.object(admission.os, 'write', side_effect=OSError('injected')), self.assertRaises(OSError): self.publish()
        self.assertFalse((self.root/'var/lib/igloo/first-boot-input/userdata.json').exists())
        with self.assertRaises((ValueError, OSError)): self.publish()


if __name__ == '__main__': unittest.main()
