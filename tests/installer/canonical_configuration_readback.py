"""Read-only successor evidence inspection. Never authorizes replay or mutation."""
import hashlib
import json
from pathlib import Path
import re


def digest(data): return hashlib.sha256(data).hexdigest().upper()
def require(value, code):
    if not value: raise ValueError(code)


def chain(directory, preflight, *, captured=None, prefix=None):
    reservation = ((directory/'plan.sha256').read_bytes() if captured is None else
                   captured[prefix+'/plan.sha256'])
    require(reservation == preflight['PlanSha256'].encode(), 'ConfigurationReservationChanged')
    records, refs, previous = [], [], None
    entries = ([(p.name, p.read_bytes()) for p in sorted(directory.iterdir())
                if p.name != 'plan.sha256'] if captured is None else
               [(name[len(prefix)+1:], captured[name]) for name in sorted(captured)
                if name.startswith(prefix+'/') and name != prefix+'/plan.sha256'])
    for sequence, (name, data) in enumerate(entries):
        require(re.fullmatch(r'[0-9]{8}-[0-9A-F]{64}\.json', name), 'ConfigurationRecordName')
        sha = digest(data); record = json.loads(data)
        require(name == f'{sequence:08d}-{sha}.json' and record['Sequence'] == sequence and
            record['PreviousSha256'] == previous and record['SchemaVersion'] == 3 and
            record['SessionId'] == preflight['SessionId'] and record['PlanSha256'] == preflight['PlanSha256'] and
            record['OperationId'] == preflight['Plan']['OperationId'] and record['Provenance'] == preflight['Plan']['Provenance'] and
            record['GenerationId'] == preflight['Plan']['Provenance']['GenerationId'] and record['Predecessor'] == preflight['Predecessor'],
            'ConfigurationRecordBinding')
        previous = sha; refs.append({'Reference': name, 'Sha256': sha}); records.append(record)
    require(records, 'ConfigurationRecordsMissing')
    return records, refs


def verify(evidence, journals, *, captured=None):
    evidence, journals = Path(evidence), Path(journals)
    stdout = ((evidence/'configuration.stdout').read_bytes() if captured is None else
              captured['guest-evidence/execution/configuration.stdout'])
    lines = [json.loads(line) for line in stdout.splitlines()]
    preflight = lines[0]; require(preflight['Phase'] == 'ConfigurationPreflight', 'ConfigurationPreflightMissing')
    generation = preflight['Plan']['Provenance']['GenerationId']
    session, session_refs = chain(journals/'session'/generation, preflight,
        captured=captured, prefix=f'guest-evidence/journals/session/{generation}')
    effects, effect_refs = chain(journals/'effects'/generation, preflight,
        captured=captured, prefix=f'guest-evidence/journals/effects/{generation}')
    steps = ('Baseline', 'Files', 'Debconf', 'Exim', 'Tls', 'Locale', 'User', 'Credential', 'Sudo', 'Verify')
    completed = []; pending = False; terminated = False
    bindings = effects[0]['Bindings']; preserved = effects[0]['ProtectedStateSha256']
    for record in effects:
        require(not terminated and len(completed) < len(steps) and record['Step'] == steps[len(completed)] and record['Bindings'] == bindings and
            record['ProtectedStateSha256'] == preserved, 'ConfigurationEffectOrder')
        if record['Outcome'] == 'IntentDurable':
            require(not pending, 'RepeatedConfigurationIntent'); pending = True
        else:
            require(pending and record['Outcome'] in ('AppliedAndVerified', 'OutcomeUnknown'), 'ConfigurationResultWithoutIntent')
            if record['Outcome'] == 'OutcomeUnknown': terminated = True; continue
            observed = record['Evidence']['ObserverEvidence']
            encoded = json.dumps(observed, sort_keys=True, separators=(',', ':')).encode()
            require(digest(encoded) == record['Evidence']['ObserverEvidenceSha256'] and
                observed['OperationId'] == preflight['Plan']['OperationId'] and observed['PlanSha256'] == preflight['PlanSha256'],
                'ConfigurationObserverBinding')
            completed.append(record['Step']); pending = False
    unmounts = [r for r in session if r['Action'].startswith('Unmount') and r['State'] == 'AppliedAndVerified']
    teardown = [r['Action'] for r in unmounts] == ['UnmountPayload', 'UnmountRoot']
    if teardown:
        for record in unmounts:
            e = record['Evidence']; before = e['Before']['Mounts']; after = e['After']['Mounts']
            removed = [m for m in before if m['Path'] == e['Path']]
            require(len(removed) == 1 and after == [m for m in before if m['Id'] != removed[0]['Id']], 'ConfigurationUnmountDelta')
        teardown = session[-1]['Action'] == 'Close' and session[-1]['State'] == 'AppliedAndVerified'
    complete = completed == list(steps) and not pending and not terminated
    if complete:
        require(session[-1]['ConfigurationResultReference'] == effect_refs[-1]['Reference'], 'ConfigurationHandoffMismatch')
    return {'OperationId': preflight['Plan']['OperationId'], 'SessionId': preflight['SessionId'],
        'GenerationId': generation, 'PlanSha256': preflight['PlanSha256'], 'Predecessor': preflight['Predecessor'],
        'CompletedSteps': completed, 'Configuration': 'AppliedAndVerified' if complete else 'OutcomeUnknown',
        'Teardown': 'AppliedAndVerified' if teardown else 'NotVerified', 'SessionRecords': session_refs, 'EffectRecords': effect_refs,
        'ProtectedStateSha256': preserved, 'LastIndependentObservation': next(r['Evidence']['ObserverEvidence'] for r in reversed(effects)
            if r['Outcome'] == 'AppliedAndVerified'), 'ReplayAuthorized': False}
