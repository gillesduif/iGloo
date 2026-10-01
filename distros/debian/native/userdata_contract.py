"""Selected-document successor wire and terminal-record validation.

Parsing is not acquisition, admission or execution authority. In particular, a
rehashed fixture record cannot be presented as a canonical producer record.
"""
import base64
import re
import uuid
import unicodedata

from debian_first_boot import decode, validate_completion_receipt
from isolation_policy import canonical, digest, require

MAX_BYTES = 32 * 1024 * 1024
FORBIDDEN = {'.ssh', '.gnupg', '.pki', 'passwd', 'shadow', 'group', 'gshadow', 'sudoers'}
FOLDERS = {'Documents', 'Downloads', 'Pictures', 'Desktop', 'Music', 'Videos'}

SCOPE = 'CanonicalLabSelectedDocumentsV1'
PROVENANCE_SCOPE = 'SelectedDocumentTrees'
INPUT_RECEIPT = 'userdata.json'
INPUT_EVIDENCE = 'userdata-evidence.json'
FIELDS = ('SourceIdentity', 'TargetIdentity', 'HomeIdentity', 'Accounts',
          'SourceBeforeSha256', 'DestinationBeforeSha256')


def key(path):
    return ''.join(c.upper() if len(c.upper()) == 1 else c for c in unicodedata.normalize('NFC', path))


def relative(path):
    require(type(path) is str and 0 < len(path) <= 512 and len(path.split('/')) <= 16 and
            all(ord(c) < 256 for c in unicodedata.normalize('NFC', path)) and
            all(p not in ('', '.', '..') and ':' not in p and '\\' not in p and
                not p.endswith((' ', '.')) and not any(unicodedata.category(c)=='Cc' for c in p)
                for p in path.split('/')), 'UserDataPathRejected')
    require(not any(p.lower() in FORBIDDEN for p in path.split('/')), 'UserDataSensitivePathRejected')
    return path


def validate(raw):
    require(0 < len(raw) < 1024*1024, 'UserDataPlanSize')
    p = decode(raw)
    require(set(p) == {'SchemaVersion','GenerationId','OperationId','Scope','ManifestSha256','Username','Uid','Gid','Home','Selection','CollisionPolicy','Entries'} and
            type(p['SchemaVersion']) is int and p['SchemaVersion'] == 1 and p['Scope'] == 'SelectedDocumentTreesV1' and
            p['CollisionPolicy'] == 'CreateNewSelectedTrees', 'UserDataScopeRejected')
    for field in ('GenerationId','OperationId'):
        require(str(uuid.UUID(p[field])) == p[field] and uuid.UUID(p[field]).int != 0, 'UserDataIdentity')
    require(re.fullmatch('[0-9A-F]{64}',p['ManifestSha256']) and re.fullmatch('[a-z][a-z0-9_-]{0,30}',p['Username']) and
            type(p['Uid']) is int and type(p['Gid']) is int and 1000 <= p['Uid'] <= 60000 and 1000 <= p['Gid'] <= 60000 and
            p['Home'] == '/home/'+p['Username'], 'UserDataAccountDeclaration')
    selection = p['Selection']; folders = selection['folders']
    require(set(selection) == {'stagingPath','totalBytes','includedFolders','folders'} and selection['stagingPath'] == '' and
            type(selection['totalBytes']) is int and type(folders) is list and 1 <= len(folders) <= 6 and
            all(set(f) == {'name','sourceRelativePath'} and f['name'] in FOLDERS for f in folders) and
            sorted(selection['includedFolders']) == sorted(f['name'] for f in folders) and
            len({f['name'] for f in folders}) == len(folders), 'UserDataSelection')
    for f in folders: relative(f['sourceRelativePath'])
    entries = p['Entries']; require(type(entries) is list and 0 < len(entries) <= 1024, 'UserDataEntryBound')
    for e in entries:
        require(set(e) == {'Source','Destination','Kind','Length','Sha256'}, 'UserDataEntryShape')
        relative(e['Source']); relative(e['Destination'])
        matches = [f for f in folders if e['Source'] == f['sourceRelativePath'] or e['Source'].startswith(f['sourceRelativePath']+'/')]
        require(len(matches) == 1 and e['Destination'] == matches[0]['name']+e['Source'][len(matches[0]['sourceRelativePath']):], 'UserDataMapping')
        require(e['Kind'] in ('File','Directory') and type(e['Length']) is int and 0 <= e['Length'] <= MAX_BYTES and
                (e['Sha256'] is None and e['Length'] == 0 if e['Kind']=='Directory' else
                 type(e['Sha256']) is str and re.fullmatch('[0-9A-F]{64}', e['Sha256'])), 'UserDataMetadataPolicy')
    require(len({key(e['Destination']) for e in entries}) == len(entries) and len({key(e['Source']) for e in entries}) == len(entries) and
            0 < sum(e['Length'] for e in entries) == selection['totalBytes'] <= MAX_BYTES, 'UserDataCollisionOrBytes')
    dirs = {e['Destination'] for e in entries if e['Kind']=='Directory'}
    require(all(f['name'] in dirs for f in folders) and all('/' not in e['Destination'] or e['Destination'].rsplit('/',1)[0] in dirs for e in entries), 'UserDataMissingDirectory')
    return p


def sha(value):
    return type(value) is str and re.fullmatch('[0-9A-F]{64}', value) is not None


def identifier(value):
    return type(value) is str and str(uuid.UUID(value)) == value and uuid.UUID(value).int != 0


def declaration(raw, transfer):
    """Consume actual .NET JSON, including numeric canonical lease roles.

    The connected supervisor must additionally validate these declarations
    against its owned leases, fresh observations and private-pipe authority.
    """
    require(0 < len(raw) <= 1024 * 1024, 'UserDataDeclarationBound')
    d = decode(raw)
    require(set(d) == {'SchemaVersion', 'Scope', 'Provenance', 'SessionId', 'PlanSha256',
        'TransferSha256', 'PredecessorSha256', 'PredecessorCloseSha256', 'DeliverySha256',
        'SourcePrefix', 'Account', 'Bindings'} and type(d['SchemaVersion']) is int and
        d['SchemaVersion'] == 1 and d['Scope'] == SCOPE, 'UserDataCanonicalDeclaration')
    p = d['Provenance']
    require(set(p) == {'Provider', 'Version', 'Scope', 'RunId', 'GenerationId', 'EvidenceSha256'} and
        p['Provider'] == 'IsolatedFileBackedLab' and type(p['Version']) is int and p['Version'] == 5 and
        p['Scope'] == PROVENANCE_SCOPE and identifier(p['RunId']) and identifier(p['GenerationId']) and
        sha(p['EvidenceSha256']) and identifier(d['SessionId']), 'UserDataProviderRejected')
    require(all(sha(d[k]) for k in ('PlanSha256', 'TransferSha256', 'PredecessorSha256',
        'PredecessorCloseSha256', 'DeliverySha256')) and d['TransferSha256'] == digest(transfer),
        'UserDataTransferBinding')
    # Imported lazily to keep the pure record validator usable by a read-only
    # consumer without importing the file copier or its subprocess boundary.
    plan = validate(transfer)
    require(p['GenerationId'] == plan['GenerationId'] and identifier(plan['OperationId']) and
        d['SourcePrefix'] == 'userdata/' + plan['OperationId'] + '/source' and
        type(d['Account']) is dict and type(d['Account'].get('Uid')) is int and type(d['Account'].get('Gid')) is int and
        d['Account'] == {k: plan[k] for k in ('Username', 'Uid', 'Gid', 'Home')},
        'UserDataSourceAccountGenerationBinding')
    bindings = d['Bindings']
    require(type(bindings) is list and len(bindings) == 3 and all(type(b) is dict for b in bindings) and
        all(type(b.get('Role')) is int and type(b.get('Access')) is int for b in bindings) and
        [b['Role'] for b in bindings] == [0, 1, 2] and [b['Access'] for b in bindings] == [1, 0, 0] and
        [b.get('FileSystem') for b in bindings] == ['EXT4', 'FAT32', 'FAT32'] and
        all(b.get('Partition') is None and type(b.get('StoragePartition')) is dict for b in bindings),
        'UserDataCanonicalLeaseRoles')
    require(type(bindings[0].get('FileSystemUuid')) is str and
        re.fullmatch('[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}', bindings[0]['FileSystemUuid']) and
        all(type(b.get('FileSystemUuid')) is str and re.fullmatch('[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}', b['FileSystemUuid'])
            for b in bindings[1:]) and len({b['FileSystemUuid'].upper() for b in bindings}) == 3,
        'UserDataCanonicalFileSystems')
    partitions = []
    for b in bindings:
        s = b['StoragePartition']; disk = s.get('Disk')
        require(set(s) == {'Disk', 'PartitionGuid', 'PartitionType', 'OffsetBytes', 'SizeBytes'} and
            type(disk) is dict and set(disk) == {'GptDiskGuid', 'SizeBytes', 'LogicalSectorSize'} and
            identifier(s['PartitionGuid']) and identifier(s['PartitionType']) and identifier(disk['GptDiskGuid']) and
            all(type(n) is int and n > 0 for n in (s['OffsetBytes'], s['SizeBytes'], disk['SizeBytes'], disk['LogicalSectorSize'])) and
            disk['LogicalSectorSize'] in (512, 4096) and s['OffsetBytes'] % disk['LogicalSectorSize'] == 0 and
            s['SizeBytes'] % disk['LogicalSectorSize'] == 0 and s['OffsetBytes']+s['SizeBytes'] <= disk['SizeBytes'],
            'UserDataCanonicalGeometry')
        partitions.append(s)
    require(len({s['PartitionGuid'] for s in partitions}) == 3 and all(s['Disk'] == partitions[0]['Disk'] for s in partitions),
            'UserDataCanonicalPartitionAlias')
    by_offset = sorted(partitions, key=lambda p: p['OffsetBytes'])
    require(all(a['OffsetBytes']+a['SizeBytes'] <= b['OffsetBytes'] for a, b in zip(by_offset, by_offset[1:])),
            'UserDataCanonicalPartitionOverlap')
    return d


def terminal(transfer, records, receipt, authority=None):
    """Verify exact raw v2 terminal chain; never trust just the v1 envelope."""
    plan = validate(transfer)
    require(type(records) is list and len(records) == 3 and all(type(r) is bytes and 0 < len(r) <= 1024*1024 for r in records),
            'UserDataIncompleteOrFailedChain')
    values = [decode(r) for r in records]
    scope = 'FixtureOnlySelectedDocuments' if authority is None else SCOPE
    if authority is not None:
        declaration(authority, transfer)
    for v in values:
        require(type(v.get('SchemaVersion')) is int and v['SchemaVersion'] == 2 and
            v['Scope'] == scope and v['GenerationId'] == plan['GenerationId'] and
            v['OperationId'] == plan['OperationId'] and v['PlanSha256'] == digest(transfer), 'UserDataRecordBinding')
        require(('AuthoritySha256' not in v if authority is None else v.get('AuthoritySha256') == digest(authority)),
                'UserDataRecordAuthority')
    intent, observed, result = values
    require([v['State'] for v in values] == ['IntentDurable', 'IndependentObservation', 'AppliedAndVerified'],
            'UserDataRecordOrdering')
    names = [f'{i:08d}-{digest(r)}.json' for i, r in enumerate(records)]
    context = {k: intent[k] for k in FIELDS}
    if authority is not None:
        context['AuthoritySha256'] = digest(authority)
    binding = digest(canonical(context))
    observation = observed['Observation']
    require(all(type(v.get('Files')) is int and type(v.get('Bytes')) is int for v in (result, observation)),
            'UserDataResultNumericTypes')
    require(all(v['IntentReference'] == names[0] and v['IntentSha256'] == digest(records[0]) for v in values[1:]) and
        result['BindingSha256'] == observation['BindingSha256'] == binding, 'UserDataIntentContextBinding')
    require(result['ObservationReference'] == names[1] and result['ObservationSha256'] == digest(records[1]) and
        result['Files'] == observation['Files'] == sum(e['Kind'] == 'File' for e in plan['Entries']) and
        result['Bytes'] == observation['Bytes'] == plan['Selection']['totalBytes'] and
        observation['PlanSha256'] == digest(transfer) and observation['GenerationId'] == plan['GenerationId'] and
        observation['OperationId'] == plan['OperationId'] and
        all(observation[k] is True for k in ('SourceUnchanged', 'UnrelatedDestinationUnchanged', 'ContentAndOwnershipVerified')),
        'UserDataResultObservationBinding')
    envelope = decode(receipt)
    validate_completion_receipt(envelope, plan['GenerationId'], 'UserData')
    require(envelope['EvidenceSha256'] == digest(records[-1]), 'UserDataReceiptEvidenceBinding')
    return {'ResultReference': names[-1], 'ResultSha256': digest(records[-1]),
        'ReceiptSha256': digest(receipt), 'ReopenedRecords': 3, 'Scope': scope}


def validate_admitted_bundle(raw, receipt, expected_authority):
    """Lab-only consumer. Does not grant production or first-boot completion."""
    require(0 < len(raw) <= 1024*1024, 'UserDataAdmissionBound')
    bundle = decode(raw)
    require(set(bundle) == {'SchemaVersion', 'Scope', 'Authority', 'Transfer', 'Records'} and
        type(bundle['SchemaVersion']) is int and bundle['SchemaVersion'] == 1 and bundle['Scope'] == SCOPE and
        type(bundle['Records']) is list and len(bundle['Records']) == 3, 'UserDataAdmissionShape')
    authority = base64.b64decode(bundle['Authority'], validate=True)
    require(authority == expected_authority, 'UserDataAdmissionAuthorityChanged')
    transfer = base64.b64decode(bundle['Transfer'], validate=True)
    records = [base64.b64decode(r, validate=True) for r in bundle['Records']]
    checked = terminal(transfer, records, receipt, authority)
    return {**checked, 'EvidenceSha256': digest(raw), 'AuthoritySha256': digest(authority),
            'ProductionQualified': False, 'FirstBootSucceeded': False}
