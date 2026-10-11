import copy
import unittest

from canonical_import_checkpoint_acceptance import CLOSE, EXPECTED, EXPECTED_SEMANTICS, FILES, GENERATION, IMPORT, MOUNT_PREDICATES, SESSION, decode
from canonical_import_checkpoint_inspection import BASE, PIN


def fixture():
    records = {name: {} for name in FILES}
    records['inspection-budget.json'] = {'Scope': 'OneReadOnlyNominatedCheckpointInspection',
        'AuthorizedSessions': 1, 'InvokedSessions': 1, 'Candidate': str(BASE) + '/',
        'NoCanonicalOperation': True}
    records['preflight.json'] = {'PinSha256': PIN,
        'ManifestSha256': '5BE0814960F7D87B3FB3379688D60873028921D4D8F0BF504DD4C0EB6AE3B9B2',
        'Images': {role: [v['Device'], v['Inode'], v['Length'], v['MtimeSeconds'] * 1_000_000_000]
                   for role, v in EXPECTED.items()}}
    for role in EXPECTED:
        records[role+'-mount-observation.json'] = {'FailedPredicate': None,
            'Predicates': {name: True for name in MOUNT_PREDICATES},
            'SelectedMounts': [{'FileSystem': 'ext4', 'SuperOptions': ['ro', 'norecovery']}],
            'Loop': {'ReadOnly': True}, 'ImageIdentity': records['preflight.json']['Images'][role],
            'Namespace': 12}
        records[role+'-mount-intent.json'] = {'ImageIdentity': records['preflight.json']['Images'][role],
            'NoReplayRequested': True, 'Namespace': 12}
    records['direct-journal-readback.json'] = {'ImportRecords': 1823, 'SessionRecords': 18,
        'GenerationId': GENERATION, 'SessionId': SESSION,
        'ImportResult': {'Sha256': IMPORT}, 'CloseResult': {'Sha256': CLOSE},
        'ExactTeardownVerified': True, 'ExpectedSemanticHashes': EXPECTED_SEMANTICS}
    records['complete-root-readback.json'] = {'ManifestEntries': 144911, 'ManifestPackages': 1594,
        **EXPECTED_SEMANTICS}
    records['inspection-teardown.json'] = {'OrdinaryUnmountAndWriterExclusion': True,
        'FailureCode': None, 'MountedRoles': ['journal', 'target']}
    records['result.json'] = {'Outcome': 'CurrentCheckpointReadOnlyVerified', 'EffectAuthority': False}
    return records


class CurrentCheckpointAcceptanceTests(unittest.TestCase):
    def test_narrow_complete_result_decodes(self):
        self.assertEqual(PIN, decode(fixture())['PinSha256'])

    def test_missing_or_changed_required_evidence_rejected(self):
        changes = [
            lambda r: r.pop('direct-journal-readback.json'),
            lambda r: r['inspection-budget.json'].update(InvokedSessions=2),
            lambda r: r['preflight.json']['Images']['target'].__setitem__(1, 42),
            lambda r: r['journal-mount-observation.json']['Predicates'].update(NoJournalReplay=False),
            lambda r: r['journal-mount-observation.json']['Predicates'].pop('NoJournalReplay'),
            lambda r: r['journal-mount-observation.json']['SelectedMounts'][0].update(SuperOptions=['ro']),
            lambda r: r['target-mount-observation.json']['Loop'].update(ReadOnly=False),
            lambda r: r['direct-journal-readback.json']['CloseResult'].update(Sha256='F'*64),
            lambda r: r['direct-journal-readback.json'].update(ExactTeardownVerified=False),
            lambda r: r['complete-root-readback.json'].update(PackageStateSha256='F'*64),
            lambda r: r['inspection-teardown.json'].update(OrdinaryUnmountAndWriterExclusion=False),
            lambda r: r['result.json'].update(EffectAuthority=True),
        ]
        for change in changes:
            with self.subTest(change=change):
                records = copy.deepcopy(fixture()); change(records)
                with self.assertRaises(ValueError): decode(records)


if __name__ == '__main__':
    unittest.main()
