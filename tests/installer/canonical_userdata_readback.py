"""Independent UserData producer/admission/session chain readback. No effects."""
import base64
import json
from pathlib import Path
import re

from canonical_configuration_readback import require, digest


def verify(evidence, journals):
    from userdata_contract import declaration, validate_admitted_bundle, decode
    evidence, journals = Path(evidence), Path(journals)
    preflight = decode((evidence/'userdata.stdout').read_text().splitlines()[0])
    require(preflight['Phase'] == 'UserDataPreflight', 'UserDataPreflightMissing')
    plan = preflight['Plan']; generation = plan['Provenance']['GenerationId']
    transfer = base64.b64decode(plan['Transfer'], validate=True)
    def chain(store):
        directory = journals/store/generation
        require((directory/'plan.sha256').read_bytes() == preflight['PlanSha256'].encode(), 'UserDataReservationChanged')
        result, references, previous = [], [], None
        for i, path in enumerate(sorted(p for p in directory.iterdir() if p.name != 'plan.sha256')):
            raw = path.read_bytes(); value = decode(raw); sha = digest(raw)
            require(re.fullmatch(r'[0-9]{8}-[0-9A-F]{64}\.json', path.name) and path.name == f'{i:08d}-{sha}.json' and
                value['Sequence'] == i and value['PreviousSha256'] == previous and value['SchemaVersion'] == 5 and
                value['GenerationId'] == generation and value['SessionId'] == preflight['SessionId'] and
                value['OperationId'] == plan['OperationId'] and value['PlanSha256'] == preflight['PlanSha256'] and
                value['Provenance'] == plan['Provenance'] and value['InitramfsSha256'] == plan['InitramfsSha256'] and
                value['InitramfsCloseSha256'] == plan['InitramfsCloseSha256'], 'UserDataChainChanged')
            result.append(value); references.append({'Reference': path.name, 'Sha256': sha}); previous = sha
        return result, references
    effects, effect_refs = chain('effects'); sessions, session_refs = chain('session')
    require(len(effects) == 8 and len(sessions) >= 3, 'UserDataIncompleteChains')
    transfer_result = effects[3]['Evidence']
    bundle_raw = base64.b64decode(transfer_result['Bundle'], validate=True)
    receipt = base64.b64decode(transfer_result['Receipt'], validate=True)
    bundle = decode(bundle_raw); authority = base64.b64decode(bundle['Authority'], validate=True)
    context = declaration(authority, transfer)
    require(context['Provenance'] == plan['Provenance'] and context['SessionId'] == preflight['SessionId'] and
        context['PlanSha256'] == preflight['PlanSha256'] and context['PredecessorSha256'] == plan['InitramfsSha256'] and
        context['PredecessorCloseSha256'] == plan['InitramfsCloseSha256'] and
        context['DeliverySha256'] == digest(base64.b64decode(plan['Delivery'], validate=True)) and
        base64.b64decode(bundle['Transfer'], validate=True) == transfer, 'UserDataAdmissionPlanChanged')
    admitted = validate_admitted_bundle(bundle_raw, receipt, authority)
    # Reopen the independently retained producer journal as well as the exact
    # target-usable copies carried by the admission bundle.
    producer = journals/'effects'/plan['OperationId']
    require((producer/'plan.sha256').read_bytes() == digest(transfer).encode() and
            (journals/'effects/userdata-receipt.json').read_bytes() == receipt, 'UserDataProducerReservationChanged')
    names = sorted(p for p in producer.iterdir() if p.name != 'plan.sha256')
    require(len(names) == 3, 'UserDataProducerTerminalChanged')
    for i, (path, encoded) in enumerate(zip(names, bundle['Records'])):
        raw = base64.b64decode(encoded, validate=True)
        require(path.name == f'{i:08d}-{digest(raw)}.json' and path.read_bytes() == raw, 'UserDataProducerAdmissionSubstituted')
    package = None; final_delta = None
    for i, step in enumerate(('Baseline', 'Transfer', 'Admission', 'Verify')):
        intent, result = effects[i*2:i*2+2]
        for r in (intent, result):
            require(r['Step'] == step and r['Bindings'] == context['Bindings'] and
                r['AuthoritySha256'] == digest(authority) and r['ProtectedStateSha256'] == effects[0]['ProtectedStateSha256'],
                'UserDataEffectBindingsChanged')
        require(intent['Outcome'] == 'IntentDurable' and result['Outcome'] == 'AppliedAndVerified', 'UserDataEffectIncomplete')
        o = result['Evidence']['ObserverEvidence']
        require(digest(json.dumps(o, sort_keys=True, separators=(',', ':')).encode()) == result['Evidence']['ObserverEvidenceSha256'] and
            o['OperationId'] == plan['OperationId'] and o['SessionId'] == preflight['SessionId'] and
            o['PlanSha256'] == preflight['PlanSha256'] and o['Step'] == step and o['AuthoritySha256'] == digest(authority) and
            re.fullmatch('[0-9A-F]{64}', o['FilesystemSha256']) and re.fullmatch('[0-9A-F]{64}', o['PackageStateSha256']) and
            (package is None or package == o['PackageStateSha256']), 'UserDataIndependentReadbackChanged')
        package = o['PackageStateSha256']
        if step != 'Baseline':
            require(o['ConfiguredAndInitramfsPreserved'] is True and o['MachineIdentity'] == 'FirstBootPending', 'UserDataPredecessorNotPreserved')
        if step in ('Admission', 'Verify'):
            require(all(o['Admission'][k] == admitted[k] for k in ('ResultSha256', 'ReceiptSha256', 'EvidenceSha256', 'AuthoritySha256')) and
                (final_delta is None or final_delta == o['FilesystemSha256']), 'UserDataAdmissionDeltaChanged')
            final_delta = o['FilesystemSha256']
    require(sessions[-1]['Action'] == 'Close' and sessions[-1]['State'] == 'AppliedAndVerified' and
        sessions[-1]['UserDataResultReference'] == effect_refs[-1]['Reference'], 'UserDataCloseMissing')
    unmounts = [s for s in sessions if s['Action'].startswith('Unmount') and s['State'] == 'AppliedAndVerified']
    require([s['Action'] for s in unmounts] == ['UnmountPayload', 'UnmountRoot'], 'UserDataTeardownIncomplete')
    for r in unmounts:
        e = r['Evidence']; before, after, path = e['Before']['Mounts'], e['After']['Mounts'], e['Path']
        require(len([m for m in before if m['Path'] == path]) == 1 and not any(m['Path'] == path for m in after) and
                [m for m in before if m['Path'] != path] == after, 'UserDataExactMountDeltaChanged')
    return {'UserData': 'AppliedAndVerified', 'Admission': admitted, 'Teardown': 'AppliedAndVerified',
        'EffectRecords': effect_refs, 'SessionRecords': session_refs, 'PlanSha256': preflight['PlanSha256'],
        'SessionId': preflight['SessionId'], 'OperationId': plan['OperationId'], 'FilesystemSha256': final_delta,
        'PackageStateSha256': package, 'ProductionQualified': False, 'FirstBootSucceeded': False, 'ReplayAuthorized': False}
