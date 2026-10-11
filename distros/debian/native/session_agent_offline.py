"""Closed canonical materialization of the stable worker and restricted unit.

The qualified file publisher receives only the current connected root FD.
The separately launched, hash-pinned observer checks the full successor tree.
This action neither creates receipt-bound configuration nor enables a service.
"""
import base64
import os
import subprocess

import agent_offline
import agent_offline_successor as successor
import configured_root as root
import session_import
from isolation_policy import canonical, digest, require
from target_files import beneath
from userdata_contract import decode, validate


SCOPE = 'LabCanonicalOfflineAgentStageV1'
STEPS = ('Baseline', 'Publication', 'Verify')
OBSERVER_CODES = frozenset(('AgentUserDataPredecessorChanged', 'AgentUserDataPredecessorDeltaChanged',
    'AgentUserDataPredecessorPackagesChanged', 'AgentCanonicalBeforeStateChanged',
    'AgentCanonicalPackageStateChanged', 'AgentPrematureActivation', 'AgentInstalledBytesChanged',
    'FilesystemSetMismatch', 'FilesystemMetadataMismatch', 'FilesystemTypeMismatch',
    'FileOrHardlinkMismatch', 'SymlinkMismatch', 'XattrMismatch', 'UnexpectedChildMount',
    'ReadbackMountChanged', 'UnsupportedPathEncoding'))


class ObserverFailure(ValueError):
    def __init__(self, evidence):
        super().__init__('AgentStageIndependentObserverFailed')
        self.evidence = evidence


def profile(declaration):
    plan = declaration['AgentStagePlan']; previous = declaration['UserDataPredecessor']
    stage, worker, unit = agent_offline.source(declaration['AgentStagePacket'], scope=SCOPE)
    require(type(plan['SchemaVersion']) is int and plan['SchemaVersion'] == 1 and
            plan['Provenance'] == declaration['LabAgentStage'] and
            plan['Provenance']['Provider'] == 'IsolatedFileBackedLab' and
            plan['Provenance']['Version'] == 6 and plan['Provenance']['Scope'] == 'OfflineAgentStage' and
            plan['ExecutionSha256'] == declaration['ExecutionSha256'] and
            plan['OperationId'] == stage['OperationId'] and plan['Stage'] == stage and
            plan['UserDataSha256'] == previous['ResultSha256'] and
            plan['UserDataCloseSha256'] == previous['CloseSha256'] and
            stage['GenerationId'] == declaration['GenerationId'], 'AgentStageSuccessorChanged')
    prior = previous['Plan']; transfer = base64.b64decode(prior['Transfer'], validate=True)
    selected = validate(transfer)
    require(prior['Provenance']['Provider'] == 'IsolatedFileBackedLab' and
            prior['Provenance']['Version'] == 5 and prior['Provenance']['Scope'] == 'SelectedDocumentTrees' and
            selected['GenerationId'] == stage['GenerationId'] and
            selected['OperationId'] == prior['OperationId'] and
            previous['Initramfs']['Configured']['Imported']['BuildId'] is not None,
            'AgentStageUserDataLineageChanged')
    return plan, previous, transfer, stage, worker, unit


def _inputs(view, request, previous, transfer):
    raw = session_import.read_bound(request['ManifestFd'], root.MAX_MANIFEST)
    imported = previous['Initramfs']['Configured']['Imported']
    require(digest(raw) == imported['ManifestSha256'], 'AgentStageManifestChanged')
    original = root.validate_manifest(raw)
    configured = previous['Initramfs']['Configured']['Observation']
    initramfs = previous['Initramfs']['Observation']
    bundle = base64.b64decode(previous['AdmissionBundle'], validate=True)
    receipt = base64.b64decode(previous['AdmissionReceipt'], validate=True)
    authority = base64.b64decode(previous['AdmissionAuthority'], validate=True)
    return original, configured, initramfs, transfer, bundle, receipt, authority, previous['Observation']


def observe(declaration):
    plan, previous, transfer, stage, worker, unit = profile(declaration)
    request = declaration['AgentStageObservation']; step = request['Step']
    require(step in STEPS and request['RootIdentity'] == request['ExpectedRootIdentity'],
            'AgentStageObserverContextChanged')
    view = root.RootView(request['RootFd'], *request['RootIdentity'])
    try:
        args = _inputs(view, request, previous, transfer)
        if step == 'Baseline':
            expected = successor.predecessor(view, *args)
            before = agent_offline.before(view.fd)
            result = {'FilesystemSha256': digest(canonical(expected['Entries'])),
                      'PackageStateSha256': previous['Observation']['PackageStateSha256'],
                      'Before': before}
        else:
            baseline = successor.compose_predecessor(view, *args)
            result = successor.verify_delta(view, baseline, declaration['AgentStagePacket'],
                                            args[0], transfer, previous['Observation']['PackageStateSha256'])
            result['StageReadback'] = {'GenerationId': stage['GenerationId'],
                'OperationId': stage['OperationId'], 'TargetRootId': stage['TargetRootId'],
                'Files': stage['Files'], 'ConfigurationAbsent': True, 'EnableLinkAbsent': True,
                'SourceAndUnrelatedContentUnchanged': True}
        print(canonical({**result, 'OperationId': plan['OperationId'],
            'SessionId': declaration['SessionId'], 'PlanSha256': declaration['PlanSha256'],
            'Step': step, 'RootIdentity': request['RootIdentity']}).decode(), flush=True)
        return 0
    finally:
        view.close()


def observe_entry(declaration):
    try:
        return observe(declaration)
    except (OSError, ValueError, KeyError, TypeError, ImportError, AttributeError) as error:
        step = declaration.get('AgentStageObservation', {}).get('Step')
        step = step if step in STEPS else 'Unknown'
        print(canonical({'AgentStageObserverFailure': 1, 'Step': step,
            'Code': str(error) if str(error) in OBSERVER_CODES else 'ObserverUnavailable',
            'ExecutionSha256': declaration['ExecutionSha256'],
            'ObserverSha256': declaration['ToolHashes'][__file__]}).decode(), flush=True)
        return 1


def _observer_response(response, declaration, step):
    require(len(response.stdout) <= 1024 * 1024 and len(response.stderr) <= 65536,
            'AgentStageObserverOutputBound')
    if response.returncode != 0:
        evidence = {'Step': step, 'Code': 'ObserverUnavailable',
            'ExecutionSha256': declaration['ExecutionSha256'],
            'ObserverSha256': declaration['ToolHashes'][__file__]}
        if 0 < len(response.stdout) <= 1024:
            try:
                value = decode(response.stdout)
                if (set(value) == {'AgentStageObserverFailure', *evidence} and
                    value['AgentStageObserverFailure'] == 1 and
                    all(value[key] == evidence[key] for key in ('Step', 'ExecutionSha256', 'ObserverSha256')) and
                    value['Code'] in OBSERVER_CODES | {'ObserverUnavailable'}):
                    evidence['Code'] = value['Code']
            except (ValueError, KeyError, TypeError): pass
        raise ObserverFailure(evidence)
    require(0 < len(response.stdout) < 1024 * 1024, 'AgentStageObserverOutputBound')
    return decode(response.stdout)


def perform(session):
    from block_session import MountSession
    require(type(session) is MountSession and session.channel.action == 'StageOfflineAgent' and
            session.acquired and not session.teardown and not session.failed and
            not session.agent_stage_attempted, 'AgentCanonicalSessionRequired')
    session.agent_stage_attempted = True
    plan, previous, transfer, stage, worker, unit = profile(session.declaration)
    connected = session_import.ConnectedImportView(session)
    descriptors = []; active = None
    try:
        view, payload = connected.views['Root'], connected.views['Payload']
        imported = previous['Initramfs']['Configured']['Imported']
        folder = 'configured-root/' + imported['BuildId'] + '/' + imported['DerivationId']
        descriptor = beneath(payload.fd, folder + '/descriptor.json', os.O_RDONLY); descriptors.append(descriptor)
        raw = session_import.read_bound(descriptor, 4 * 1024 * 1024)
        require(digest(raw) == imported['DescriptorSha256'], 'AgentStageDescriptorChanged')
        session.channel.ask('AuthenticateConfigurationSource', Descriptor=base64.b64encode(raw).decode())
        manifest = beneath(payload.fd, folder + '/root.manifest.json', os.O_RDONLY); descriptors.append(manifest)

        def checkpoint(step, outcome, **evidence):
            nonlocal active
            response = session.channel.ask('AgentStageCheckpoint', Record={'Step': step, 'Outcome': outcome, **evidence})
            active = step if outcome == 'IntentDurable' else None
            return response

        def independent(step):
            connected.verify()
            request = {'Step': step, 'RootFd': view.fd, 'RootIdentity': list(view.expected),
                       'ExpectedRootIdentity': list(view.expected), 'PayloadFd': payload.fd, 'ManifestFd': manifest}
            child = {**session.declaration, 'AgentStageObservation': request}
            response = subprocess.run([session.runtime.python, '-I', '-B', session.declaration['EntryPath']],
                input=canonical(child) + b'\n', pass_fds=[view.fd, payload.fd, manifest],
                capture_output=True, timeout=7200, check=False)
            value = _observer_response(response, session.declaration, step)
            require(value['OperationId'] == plan['OperationId'] and
                    value['SessionId'] == session.declaration['SessionId'] and
                    value['PlanSha256'] == session.declaration['PlanSha256'] and
                    value['Step'] == step and value['RootIdentity'] == list(view.expected),
                    'AgentStageObserverBindingChanged')
            connected.verify()
            return {'ObserverEvidence': value, 'ObserverEvidenceSha256': digest(canonical(value))}

        checkpoint('Baseline', 'IntentDurable')
        checkpoint('Baseline', 'AppliedAndVerified', **independent('Baseline'))
        intent = agent_offline.declaration(view.fd, stage, worker, unit, scope=SCOPE)
        connected.verify(); session.channel.checkpoint({'State': 'IntentDurable', 'Operation': 'StageOfflineAgent'})
        checkpoint('Publication', 'IntentDurable', PublicationIntent=intent)
        agent_offline.publish(view.fd, stage, worker, unit, intent, connected.verify, scope=SCOPE)
        checkpoint('Publication', 'AppliedAndVerified', **independent('Publication'))
        checkpoint('Verify', 'IntentDurable')
        result = checkpoint('Verify', 'AppliedAndVerified', **independent('Verify'))
        connected.verify(); session.channel.checkpoint({'State': 'AppliedAndVerified',
            'AgentStageResultReference': result['Reference']})
        return {'Reference': result['Reference'], 'Sha256': result['Sha256'], 'OperationId': plan['OperationId'],
                'Lifecycle': 'StagedInputsPending'}
    except (OSError, ValueError, KeyError, TypeError, ImportError, AttributeError, subprocess.TimeoutExpired) as error:
        try:
            if active is not None:
                details = {'ObserverFailure': error.evidence} if isinstance(error, ObserverFailure) else {}
                checkpoint(active, 'OutcomeUnknown', Code='AgentStageEffectOrObservationUnavailable', **details)
            else: session.channel.ask('AgentStageStopped')
        except (OSError, ValueError, KeyError, TypeError): pass
        raise
    finally:
        for descriptor in descriptors: os.close(descriptor)
        connected.close()
