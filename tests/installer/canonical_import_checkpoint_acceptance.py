"""Narrow present-day lab-input decision for one read-only import checkpoint.

It is neither a new import/Close nor authority for configuration effects.
"""

import datetime as dt
import hashlib
import json
from pathlib import Path

from canonical_lab_runtime import checkpoint
from canonical_import_checkpoint_inspection import BASE, CHECKPOINT, CLOSE, EXPECTED, EXPECTED_SEMANTICS, IMPORT, PIN, identity, no_writer, protected

SCOPE = 'PresentDayReadOnlyImportCheckpointLabInput'
RUN = 'daa72a8f-914a-4574-92ef-4d39ffb0c1e1'
GENERATION = 'ff104c9d-505c-4d88-8620-19b1153bd91e'
SESSION = '84b83ea8-606f-4ed8-a470-817e384d9d72'
APPROVAL = 'ConditionalUserApprovalForExactVerifiedCurrentStateOnly'
STORE = '/var/lib/igloo/import-checkpoint-inspection-20261004-v2'
FILES = ('inspection-budget.json', 'preflight.json', 'journal-mount-intent.json',
         'journal-mount-observation.json', 'direct-journal-readback.json',
         'target-mount-intent.json', 'target-mount-observation.json',
         'complete-root-readback.json', 'inspection-teardown.json', 'result.json')
MOUNT_PREDICATES = {'Namespace', 'OneExactMount', 'NoChildOrStackedMount', 'PrivatePropagation',
    'Ext4MountRoot', 'ReadOnlyMount', 'NoJournalReplay', 'MountDeviceAndDescriptor',
    'NoDeviceAlias', 'LoopReadOnly', 'LoopSourceAndExtent', 'ImageIdentity'}


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def require(value, code):
    if not value:
        raise ValueError(code)


def decode(records):
    require(set(records) == set(FILES), 'InspectionRecordSetChanged')
    budget = records['inspection-budget.json']
    require(budget['Scope'] == 'OneReadOnlyNominatedCheckpointInspection' and
            budget['AuthorizedSessions'] == budget['InvokedSessions'] == 1 and
            budget['Candidate'] == str(BASE) + '/' and budget['NoCanonicalOperation'] is True,
            'InspectionBudgetOrScopeChanged')
    preflight = records['preflight.json']
    require(preflight['PinSha256'] == PIN and preflight['ManifestSha256'] ==
            '5BE0814960F7D87B3FB3379688D60873028921D4D8F0BF504DD4C0EB6AE3B9B2' and
            set(preflight['Images']) == {'target', 'journal'}, 'PreflightBindingChanged')
    for role, expected in EXPECTED.items():
        image = preflight['Images'][role]
        require(image[:3] == [expected['Device'], expected['Inode'], expected['Length']] and
                image[3] // 1_000_000_000 == expected['MtimeSeconds'], 'BackingIdentityChanged')
        mount = records[role+'-mount-observation.json']
        require(mount['FailedPredicate'] is None and set(mount['Predicates']) == MOUNT_PREDICATES and
                all(v is True for v in mount['Predicates'].values()) and
                len(mount['SelectedMounts']) == 1 and
                mount['SelectedMounts'][0]['FileSystem'] == 'ext4' and
                'norecovery' in mount['SelectedMounts'][0]['SuperOptions'] and
                mount['Loop']['ReadOnly'] is True and mount['ImageIdentity'] == image,
                'InspectionMountEvidenceChanged')
        intent = records[role+'-mount-intent.json']
        require(intent['ImageIdentity'] == image and intent['NoReplayRequested'] is True and
                intent['Namespace'] == mount['Namespace'], 'InspectionMountIntentChanged')
    journal = records['direct-journal-readback.json']
    require(journal['ImportRecords'] == 1823 and journal['SessionRecords'] == 18 and
            journal['GenerationId'] == GENERATION and journal['SessionId'] == SESSION and
            journal['ImportResult']['Sha256'] == IMPORT and journal['CloseResult']['Sha256'] == CLOSE and
            journal['ExactTeardownVerified'] is True and journal['ExpectedSemanticHashes'] == EXPECTED_SEMANTICS,
            'DirectJournalResultChanged')
    root = records['complete-root-readback.json']
    require(root['ManifestEntries'] == 144911 and root['ManifestPackages'] == 1594 and
            {k: root[k] for k in EXPECTED_SEMANTICS} == EXPECTED_SEMANTICS, 'CompleteRootResultChanged')
    teardown = records['inspection-teardown.json']
    require(teardown == {'OrdinaryUnmountAndWriterExclusion': True, 'FailureCode': None,
                         'MountedRoles': ['journal', 'target']}, 'InspectionTeardownChanged')
    result = records['result.json']
    require(result['Outcome'] == 'CurrentCheckpointReadOnlyVerified' and result['EffectAuthority'] is False,
            'InspectionOutcomeChanged')
    return preflight


def read_inspection(store):
    store = Path(store); require(str(store) == STORE, 'InspectionStoreChanged'); protected(store)
    require({p.name for p in store.iterdir()} == set(FILES), 'InspectionRecordSetChanged')
    raw = {}
    for name in FILES:
        path = store/name; protected(path, file=True)
        raw[name] = path.read_bytes()
    records = {name: json.loads(data) for name, data in raw.items()}
    preflight = decode(records)
    for name, field in (('preflight.json', 'PreflightSha256'),
                        ('direct-journal-readback.json', 'DirectJournalSha256'),
                        ('complete-root-readback.json', 'CompleteRootSha256'),
                        ('inspection-teardown.json', 'TeardownSha256')):
        require(records['result.json'][field] == digest(raw[name]), 'InspectionResultReferenceChanged')
    require(all(list(identity(BASE/(role+'.raw'))) == preflight['Images'][role]
                for role in ('journal', 'target')), 'CheckpointChangedAfterObservation')
    no_writer([BASE/'journal.raw', BASE/'target.raw'])
    return records, raw


def declaration(store, raw, now):
    preflight = json.loads(raw['preflight.json'])
    expiry = dt.datetime.fromisoformat(preflight['PinNotAfterUtc'].replace('Z', '+00:00'))
    require(now < expiry, 'DevelopmentPinExpiredBeforeDecision')
    return {'Version': 1, 'Scope': SCOPE, 'AcceptedAtUtc': now.isoformat(),
        'Approval': APPROVAL, 'Candidate': str(BASE) + '/', 'HistoricalImportRunId': RUN,
        'HistoricalGenerationId': GENERATION, 'HistoricalSessionId': SESSION,
        'CheckpointResultSha256': CHECKPOINT, 'ImportResultSha256': IMPORT, 'CloseResultSha256': CLOSE,
        'PinSha256': PIN, 'PinNotAfterUtc': preflight['PinNotAfterUtc'],
        'DescriptorSha256': preflight['DescriptorSha256'], 'ManifestSha256': preflight['ManifestSha256'],
        'Backings': {role+'.raw': {'Identity': preflight['Images'][role],
            'Sha256': EXPECTED[role]['Sha256']} for role in ('target', 'journal')},
        'SemanticHashes': EXPECTED_SEMANTICS,
        'InspectionRecords': {name: {'Reference': name, 'Sha256': digest(data)} for name, data in raw.items()},
        'InspectionStore': str(store), 'HistoricalCustodyOfLaterStagesProven': False,
        'NewImportOrCanonicalClose': False, 'ProductionAuthentication': 'Unsupported',
        'EffectAuthorization': False, 'RequiresSeparateOneUseAuthorizationAndFreshAcquisition': True}


def publish(store, destination):
    records, raw = read_inspection(store)
    del records
    destination = Path(destination)
    require(not destination.exists(), 'AcceptanceAlreadyExists')
    protected(destination.parent)
    value = declaration(store, raw, dt.datetime.now(dt.timezone.utc))
    destination.mkdir(mode=0o700)
    checkpoint(destination, 'acceptance.json', value)
    return digest((destination/'acceptance.json').read_bytes())


def verify_acceptance(destination, expected_sha256):
    destination = Path(destination); protected(destination)
    path = destination/'acceptance.json'; protected(path, file=True)
    data = path.read_bytes()
    require(digest(data) == expected_sha256, 'AcceptanceBytesChanged')
    value = json.loads(data)
    require(set(value) == {'Version', 'Scope', 'AcceptedAtUtc', 'Approval', 'Candidate',
        'HistoricalImportRunId', 'HistoricalGenerationId', 'HistoricalSessionId',
        'CheckpointResultSha256', 'ImportResultSha256', 'CloseResultSha256', 'PinSha256',
        'PinNotAfterUtc', 'DescriptorSha256', 'ManifestSha256', 'Backings', 'SemanticHashes',
        'InspectionRecords', 'InspectionStore', 'HistoricalCustodyOfLaterStagesProven',
        'NewImportOrCanonicalClose', 'ProductionAuthentication', 'EffectAuthorization',
        'RequiresSeparateOneUseAuthorizationAndFreshAcquisition'} and
        value['Version'] == 1 and value['Scope'] == SCOPE and value['Approval'] == APPROVAL and
        value['Candidate'] == str(BASE) + '/' and value['CheckpointResultSha256'] == CHECKPOINT and
        value['ImportResultSha256'] == IMPORT and value['CloseResultSha256'] == CLOSE and
        value['HistoricalCustodyOfLaterStagesProven'] is False and
        value['NewImportOrCanonicalClose'] is False and value['EffectAuthorization'] is False and
        value['ProductionAuthentication'] == 'Unsupported' and
        value['RequiresSeparateOneUseAuthorizationAndFreshAcquisition'] is True,
        'AcceptanceScopeChanged')
    records, raw = read_inspection(value['InspectionStore'])
    require(set(value['InspectionRecords']) == set(raw) and all(
        value['InspectionRecords'][name] == {'Reference': name, 'Sha256': digest(data)}
        for name, data in raw.items()), 'AcceptanceInspectionChanged')
    require(value == declaration(value['InspectionStore'], raw,
            dt.datetime.fromisoformat(value['AcceptedAtUtc'])), 'AcceptanceBackingOrSemanticChanged')
    del records
    return value
