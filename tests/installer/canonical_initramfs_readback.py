"""Independent raw chain/handoff reopen. No effects or replay authorization."""
import json
from pathlib import Path
import re

from canonical_configuration_readback import digest, require


def verify(evidence, journals):
    evidence, journals = Path(evidence), Path(journals)
    preflight = json.loads((evidence/'initramfs.stdout').read_text().splitlines()[0])
    require(preflight['Phase'] == 'InitramfsPreflight', 'InitramfsPreflightMissing')
    plan = preflight['Plan']; generation = plan['Provenance']['GenerationId']
    def chain(store):
        directory = journals/store/generation
        require((directory/'plan.sha256').read_bytes() == preflight['PlanSha256'].encode(), 'InitramfsReservationChanged')
        result, references, previous = [], [], None
        for index, path in enumerate(sorted(p for p in directory.iterdir() if p.name != 'plan.sha256')):
            require(re.fullmatch(r'[0-9]{8}-[0-9A-F]{64}\.json', path.name), 'InitramfsRecordName')
            raw = path.read_bytes(); value = json.loads(raw); sha = digest(raw)
            require(path.name == f'{index:08d}-{sha}.json' and value['Sequence'] == index and
                value['PreviousSha256'] == previous and value['SchemaVersion'] == 4 and
                value['GenerationId'] == generation and value['SessionId'] == preflight['SessionId'] and
                value['OperationId'] == plan['OperationId'] and value['PlanSha256'] == preflight['PlanSha256'] and
                value['Provenance'] == plan['Provenance'] and value['ConfigurationSha256'] == plan['ConfigurationSha256'] and
                value['ConfigurationCloseSha256'] == plan['ConfigurationCloseSha256'], 'InitramfsRecordBinding')
            previous = sha; result.append(value); references.append({'Reference': path.name, 'Sha256': sha})
        return result, references
    effects, effect_refs = chain('effects'); sessions, session_refs = chain('session')
    steps = ('Baseline', 'Generation', 'Candidate', 'Publication', 'Verify')
    require(len(effects) == len(steps)*2, 'InitramfsEffectChainIncomplete')
    image = None; package = None
    for index, step in enumerate(steps):
        intent, result = effects[index*2:index*2+2]
        require(intent['Step'] == result['Step'] == step and intent['Outcome'] == 'IntentDurable' and
                result['Outcome'] == 'AppliedAndVerified', 'InitramfsEffectOrder')
        require(all(r['Bindings'] == effects[0]['Bindings'] and r['ProtectedStateSha256'] == effects[0]['ProtectedStateSha256']
                    for r in (intent, result)), 'InitramfsLeaseSetChanged')
        observation = result['Evidence']['ObserverEvidence']
        require(digest(json.dumps(observation, sort_keys=True, separators=(',', ':')).encode()) ==
                result['Evidence']['ObserverEvidenceSha256'] and observation['OperationId'] == plan['OperationId'] and
                observation['PlanSha256'] == preflight['PlanSha256'] and observation['Step'] == step and
                observation['ConfiguredEntriesSha256'] == plan['ConfiguredEntriesSha256'], 'InitramfsObserverBinding')
        require(type(observation.get('PackageStateSha256')) is str and
                re.fullmatch('[0-9A-F]{64}', observation['PackageStateSha256']) and
                (package is None or package == observation['PackageStateSha256']), 'InitramfsPackageObservationChanged')
        package = observation['PackageStateSha256']
        if step == 'Generation':
            helper = result['Evidence']['HelperEvidence']
            require(helper['State'] == 'Exited' and type(helper['ExitCode']) is int and helper['ExitCode'] == 0,
                    'InitramfsGenerationIncomplete')
        if step != 'Baseline':
            observed = {k: observation['Image'][k] for k in ('Length', 'Sha256')}
            require(type(observed['Length']) is int and 0 < observed['Length'] <= 536870912 and
                    re.fullmatch('[0-9A-F]{64}', observed['Sha256']) and (image is None or observed == image),
                    'InitramfsPublishedImageChanged')
            image = observed
        if step == 'Publication':
            publication = intent['Evidence']['Publication']
            require(publication['Destination'] == plan['Candidate']['Output'] and
                    publication['Policy'] == 'CreateNewInitramfsImageV1' and
                    (publication['Uid'], publication['Gid'], publication['Mode']) == (0, 0, 384) and
                    {k: publication['Candidate'][k] for k in ('Length', 'Sha256')} == image and
                    {k: result['Evidence']['Publication'][k] for k in ('Length', 'Sha256')} == image,
                    'InitramfsPublicationBinding')
        if step in ('Candidate', 'Verify'):
            require(observation['CompleteImageQualification'] is True and observation['Image']['Sha256'] == observation['ImageSemantics']['ImageSha256'] and
                observation['Image']['Length'] == observation['ImageSemantics']['ImageLength'], 'InitramfsImageQualificationMissing')
            observed = {k: observation['Image'][k] for k in ('Length', 'Sha256')}
            require(image is None or observed == image, 'InitramfsPublishedImageChanged'); image = observed
    require(sessions[-1]['Action'] == 'Close' and sessions[-1]['State'] == 'AppliedAndVerified' and
        sessions[-1]['InitramfsResultReference'] == effect_refs[-1]['Reference'], 'InitramfsCloseMissing')
    indices = []
    for action in ('UnmountPayload', 'UnmountRoot'):
        matches = [(i,r) for i,r in enumerate(sessions) if r['Action'] == action and r['State'] == 'AppliedAndVerified']
        require(len(matches) == 1, 'InitramfsTeardownReceiptMissing')
        index, value = matches[0]; indices.append(index); observed = value['Evidence']; path = observed['Path']
        before, after = observed['Before']['Mounts'], observed['After']['Mounts']
        removed = [m for m in before if m['Path'] == path]
        require(len(removed) == 1 and not any(m['Path'] == path for m in after) and
                [m for m in before if m['Path'] != path] == after, 'InitramfsExactTeardownDelta')
    require(indices == sorted(indices) and indices[-1] < len(sessions)-1, 'InitramfsTeardownOrder')
    return {'Initramfs': 'AppliedAndVerified', 'Teardown': 'AppliedAndVerified', 'Image': image, 'Plan': plan,
        'PlanSha256': preflight['PlanSha256'], 'SessionId': preflight['SessionId'], 'EffectRecords': effect_refs,
        'SessionRecords': session_refs, 'LastIndependentObservation': effects[-1]['Evidence']['ObserverEvidence'],
        'ReplayAuthorized': False, 'TargetBooted': False}
