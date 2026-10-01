"""Fresh, read-only verification of completed lab journal chains and teardown.

Not an authority, recovery path or importer. It never replays an operation.
"""
import hashlib
import json
from pathlib import Path
import re


def require(value, code):
    if not value:
        raise ValueError(code)


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def chain(directory, plan, provenance):
    require((directory / 'plan.sha256').read_bytes() == plan.encode('ascii'), 'JournalPlanChanged')
    names = sorted(p.name for p in directory.iterdir() if p.name != 'plan.sha256')
    records, references, previous = [], [], None
    for sequence, name in enumerate(names):
        require(re.fullmatch(r'[0-9]{8}-[0-9A-F]{64}\.json', name) and name.startswith(f'{sequence:08d}-'), 'JournalSequenceChanged')
        data = (directory / name).read_bytes()
        sha = digest(data); value = json.loads(data)
        require(sha == name[9:-5] and value['Sequence'] == sequence and value['PreviousSha256'] == previous and
                value['PlanSha256'] == plan and value['Provenance'] == provenance and
                value['GenerationId'] == provenance['GenerationId'] and value['SchemaVersion'] == 2, 'JournalBindingChanged')
        previous = sha; records.append(value); references.append({'Reference': name, 'Sha256': sha})
    return records, references


def verify(evidence, journals):
    evidence, journals = map(Path, (evidence, journals))
    outputs = [json.loads(line) for line in (evidence / 'canonical-import.stdout').read_text().splitlines()]
    preflight = outputs[0]; final = outputs[-1]
    require(final.get('Import') == 'AppliedAndVerified' and final.get('Teardown') == 'AppliedAndVerified', 'RunNotSuccessful')
    plan, provenance = preflight['PlanSha256'], preflight['Plan']['Provenance']
    generation = provenance['GenerationId']
    session, session_refs = chain(journals / 'session' / generation, plan, provenance)
    imported, import_refs = chain(journals / 'import' / generation, plan, provenance)
    require(session[-1]['Action'] == 'Close' and session[-1]['State'] == 'AppliedAndVerified' and
            session_refs[-1]['Reference'] == final['LastResultReference'] and session_refs[-1]['Sha256'] == final['LastResultSha256'], 'CloseReferenceChanged')
    require(imported[0]['Outcome'] == 'IntentDurable' and imported[-1]['Outcome'] == 'AppliedAndVerified' and
            session[-1]['ImportResultReference'] == import_refs[-1]['Reference'], 'ImportReferenceChanged')
    require(all(r['SessionId'] == final['SessionId'] for r in session + imported), 'SessionIdentityChanged')
    for key in ('BuildId', 'DerivationId', 'DescriptorSha256', 'ManifestSha256', 'ContentSha256', 'Transport', 'TransportManifestSha256'):
        require(all(r[key] == preflight['Plan'][key] for r in imported), 'SourceBindingChanged')
    bindings = imported[0]['Bindings']
    require([(b['Role'], b['Access'], b['FileSystem']) for b in bindings] ==
            [(0, 1, 'EXT4'), (1, 0, 'FAT32'), (2, 0, 'FAT32')] and
            all(b['Partition'] is None and b['StoragePartition'] is not None for b in bindings), 'LeaseRolesChanged')
    require(all(r['Bindings'] == bindings and r['ProtectedStateSha256'] == imported[0]['ProtectedStateSha256']
                for r in imported), 'LeaseOrPreservedBindingChanged')
    observation = imported[-1]['Evidence']['ObserverEvidence']
    encoded = json.dumps(observation, sort_keys=True, separators=(',', ':'), ensure_ascii=True).encode('ascii')
    require(digest(encoded) == imported[-1]['Evidence']['ObserverEvidenceSha256'], 'ObserverEvidenceChanged')
    unmounts = [r for r in session if r['Action'].startswith('Unmount') and r['State'] == 'AppliedAndVerified']
    require([r['Action'] for r in unmounts] == ['UnmountPayload', 'UnmountRoot'], 'TeardownOrderChanged')
    for record in unmounts:
        effect = record['Evidence']
        before, after = effect['Before']['Mounts'], effect['After']['Mounts']
        selected = [m for m in before if m['Path'] == effect['Path']]
        require(len(selected) == 1 and after == [m for m in before if m['Id'] != selected[0]['Id']] and
                all(m['Path'] != effect['Path'] for m in after), 'ExactUnmountAbsenceChanged')
    require(not any(r['Action'] == 'MountLinuxEsp' for r in session), 'UnexpectedEspMount')
    return {'GenerationId': generation, 'SessionId': final['SessionId'], 'PlanSha256': plan, 'Provenance': provenance,
        'SessionRecords': session_refs, 'ImportRecords': import_refs, 'IndependentObserver': observation, 'Bindings': bindings,
        'ProtectedStateSha256': imported[0]['ProtectedStateSha256'],
        'ImportResult': import_refs[-1], 'CloseResult': session_refs[-1],
        'TeardownResults': [session_refs[r['Sequence']] for r in unmounts],
        'SessionChainVerified': True, 'ImportChainVerified': True, 'ExactTeardownVerified': True}
