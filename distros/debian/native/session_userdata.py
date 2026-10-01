"""Closed canonical selected-document transfer and protected admission.

Only the existing canonical session can construct the context. The evidence
worker does not import this executor, acquire leases or modify user homes.
"""
import base64
import json
import os
from pathlib import Path
import subprocess

import configured_root as root
import deployment_journal as journal
import session_import
import userdata_producer as producer
import userdata_contract as contract
import userdata_source as source
import userdata_admission as admission
import userdata_successor as successor
from isolation_policy import canonical, digest, require
from package_broker import DirectoryLease
from target_files import beneath


def profile(declaration):
    plan = declaration['UserDataPlan']; previous = declaration['InitramfsPredecessor']
    require(type(plan['SchemaVersion']) is int and plan['SchemaVersion'] == 1 and
        plan['Provenance'] == declaration['LabUserData'] and plan['Provenance']['Provider'] == 'IsolatedFileBackedLab' and
        plan['Provenance']['Version'] == 5 and plan['Provenance']['Scope'] == 'SelectedDocumentTrees' and
        plan['InitramfsSha256'] == previous['ResultSha256'] and plan['InitramfsCloseSha256'] == previous['CloseSha256'] and
        plan['ExecutionSha256'] == declaration['ExecutionSha256'], 'UserDataSuccessorChanged')
    transfer = base64.b64decode(plan['Transfer'], validate=True); p = contract.validate(transfer)
    require(p['OperationId'] == plan['OperationId'] and p['GenerationId'] == plan['Provenance']['GenerationId'] == declaration['GenerationId'] and
        p['Username'] == previous['Configured']['Plan']['Username'] and p['Uid'] == p['Gid'] == previous['Configured']['Plan']['UserId'],
        'UserDataConfiguredAccountLineage')
    return plan, previous, transfer


class CanonicalContext:
    def __init__(self, session, connected, authority):
        # No caller-supplied scope switch or fixture conversion.
        from block_session import MountSession
        require(type(session) is MountSession and session.channel.action == 'TransferUserData' and not session.failed and
                session.acquired and not session.teardown, 'UserDataCanonicalSessionRequired')
        self.session = session; self.connected = connected; self.authority = authority
        plan, _, self.raw = profile(session.declaration)
        d = contract.declaration(authority, self.raw)
        require(d['Bindings'] == session.blocks.bindings and d['SessionId'] == session.declaration['SessionId'] and
            d['PlanSha256'] == session.declaration['PlanSha256'], 'UserDataFreshContextChanged')
        self.plan = contract.validate(self.raw); self.plan_hash = digest(self.raw); self.used = False; self.leases = []
        payload = connected.views['Payload']; target = connected.views['Root']
        delivery = base64.b64decode(plan['Delivery'], validate=True)
        acquired = source.observe(payload.fd, target.fd, self.raw, authority, delivery)
        paths = {r['Role']: r['Path'] for r in session.supervisor.receipts}
        store = session.declaration['UserDataStore']
        require(store['Path'] == '/lab-journal/userdata/'+plan['Provenance']['RunId']+'/effects' and store['FileSystem'] == 'EXT4',
                'UserDataProducerJournalPlacement')
        try:
            for path in (paths['Payload']+'/'+d['SourcePrefix'], paths['Root'], paths['Root']+self.plan['Home'], store['Path']):
                self.leases.append(DirectoryLease(path))
            self.source, self.root, self.home, self.store = self.leases
            # The verified placement witness belongs to the parent namespace.
            # Pin its inode/device, then independently observe the cloned mount
            # in this supervisor's namespace; mount IDs are namespace-local.
            require(producer.identity(self.store.fd)[:2] == [store['Device'], store['Inode']] and
                os.fstat(self.store.fd).st_dev not in (os.fstat(self.root.fd).st_dev, os.fstat(self.source.fd).st_dev),
                'UserDataProducerJournalSubstituted')
            require([producer.identity(l.fd) for l in (self.source, self.root, self.home)] ==
                [acquired[k] for k in ('SourceIdentity', 'TargetIdentity', 'HomeIdentity')], 'UserDataAcquisitionChanged')
            self.accounts = acquired['Accounts']; self.source_before = acquired['SourceBefore']; self.before = acquired['DestinationBefore']
            space = os.fstatvfs(self.home.fd)
            require(space.f_bavail*space.f_frsize >= self.plan['Selection']['totalBytes']+1024*1024 and
                space.f_favail >= len(self.plan['Entries'])+4, 'UserDataDestinationCapacity')
            self.verify()
        except BaseException: self.close(); raise

    def verify(self):
        self.connected.verify()
        producer.verify_context(self)
        require(os.fstatvfs(self.source.fd).f_flag & os.ST_RDONLY, 'UserDataSourceBecameWritable')
        verify_store_mount(self.session.supervisor._observe(self.store.path),
            self.session.supervisor.namespace, self.session.declaration['UserDataStore'],
            producer.identity(self.store.fd))

    def run_observer(self, request, fds, action):
        self.verify()
        child = {**self.session.declaration, 'UserDataProducerObservation': {'Action': action, 'Request': request}}
        result = subprocess.run([self.session.runtime.python, '-I', '-B', self.session.declaration['EntryPath']],
            input=canonical(child)+b'\n', pass_fds=fds, capture_output=True, timeout=120, check=False)
        require(result.returncode == 0 and 0 < len(result.stdout) < 65536, 'UserDataIndependentProducerObserverFailed')
        self.verify()
        return contract.decode(result.stdout)

    def close(self):
        for lease in self.leases: lease.close()


def verify_store_mount(observed, namespace, witness, current):
    """Join the parent placement witness to fresh independent child readback."""
    require(observed['Namespace'] == namespace and current[:2] == [witness['Device'], witness['Inode']] and
        witness['FileSystem'] == 'EXT4' and observed['Paths'] == [{'Path': witness['Path'],
            'Identity': current[:2], 'MountId': current[2],
            'Device': f"{witness['Major']}:{witness['Minor']}"}], 'UserDataJournalPathChanged')
    mounts = observed['Mounts']; selected = [m for m in mounts if m['Id'] == current[2]]
    require(len(selected) == 1, 'UserDataJournalMountMissing')
    mount = selected[0]
    require(mount['Path'] == '/lab-journal' and mount['Root'] == '/' and mount['FileSystem'] == 'ext4' and
        mount['Device'] == f"{witness['Major']}:{witness['Minor']}" and
        {'rw', 'nodev', 'nosuid', 'noexec'} <= set(mount['Options']) and
        [m for m in mounts if m['Device'] == mount['Device']] == selected and
        not any(m['Propagation'] or m['Path'].startswith('/lab-journal/') for m in mounts),
        'UserDataJournalMountChanged')


def observe(declaration):
    plan, previous, transfer = profile(declaration)
    if 'UserDataProducerObservation' in declaration:
        request = declaration['UserDataProducerObservation']; action = request['Action']
        require(action in ('--observe', '--reopen') and request['Request'].get('Authority') is not None,
                'UserDataProducerObservationScope')
        result = (producer.observe if action == '--observe' else producer.reopen)(request['Request'])
        print(canonical(result).decode(), flush=True); return 0
    request = declaration['UserDataObservation']; authority = base64.b64decode(request['Authority'], validate=True)
    d = contract.declaration(authority, transfer)
    require(d['SessionId'] == declaration['SessionId'] and d['PlanSha256'] == declaration['PlanSha256'], 'UserDataObserverSessionChanged')
    view = root.RootView(request['RootFd'], *request['RootIdentity'])
    try:
        raw = session_import.read_bound(request['ManifestFd'], root.MAX_MANIFEST)
        configured = previous['Configured']; imported = configured['Imported']
        require(digest(raw) == imported['ManifestSha256'], 'UserDataOriginalManifestChanged')
        original = root.validate_manifest(raw); step = request['Step']
        require(step in ('Baseline', 'Transfer', 'Admission', 'Verify'), 'UserDataObserverStep')
        if step == 'Baseline':
            expected = successor.predecessor_manifest(view, original, configured['Observation'], previous['Observation'])
            acquired = source.observe(request['PayloadFd'], view.fd, transfer, authority, base64.b64decode(plan['Delivery'], validate=True))
            outcome = {'FilesystemSha256': digest(canonical(expected['Entries'])), 'SourceBindingSha256': digest(canonical(acquired))}
        else:
            expected = successor.compose_predecessor(view, original, configured['Observation'], previous['Observation'])
            producer.observe(request['Producer'])  # fresh selected-source, account, home/complement verification
            admitted = None
            if step in ('Admission', 'Verify'):
                directory = beneath(view.fd, str(admission.INPUT_ROOT).lstrip('/'), os.O_RDONLY | os.O_DIRECTORY)
                try: admitted = (admission.read(directory, contract.INPUT_EVIDENCE), admission.read(directory, contract.INPUT_RECEIPT), authority)
                finally: os.close(directory)
            outcome = successor.verify_delta(view, expected, transfer, configured['Observation']['PackageStateSha256'], admitted)
            if admitted is not None: outcome['Admission'] = admission.observe(view.fd, authority)
        result = {**outcome, 'OperationId': plan['OperationId'], 'SessionId': declaration['SessionId'], 'PlanSha256': declaration['PlanSha256'],
            'Step': step, 'RootIdentity': request['RootIdentity'], 'AuthoritySha256': digest(authority),
            'PackageStateSha256': configured['Observation']['PackageStateSha256']}
        print(canonical(result).decode(), flush=True); return 0
    finally: view.close()


def perform(session):
    require(not session.userdata_attempted, 'UserDataSessionSingleUse'); session.userdata_attempted = True
    plan, previous, transfer = profile(session.declaration)
    connected = session_import.ConnectedImportView(session); descriptors = []; context = None; active = None
    try:
        view, payload = connected.views['Root'], connected.views['Payload']; imported = previous['Configured']['Imported']
        folder = 'configured-root/'+imported['BuildId']+'/'+imported['DerivationId']
        descriptor = beneath(payload.fd, folder+'/descriptor.json', os.O_RDONLY); descriptors.append(descriptor)
        raw = session_import.read_bound(descriptor, 4*1024*1024)
        require(digest(raw) == imported['DescriptorSha256'], 'UserDataDescriptorChanged')
        session.channel.ask('AuthenticateConfigurationSource', Descriptor=base64.b64encode(raw).decode())
        authority = base64.b64decode(session.channel.ask('UserDataContext')['Declaration'], validate=True)
        manifest = beneath(payload.fd, folder+'/root.manifest.json', os.O_RDONLY); descriptors.append(manifest)
        def checkpoint(step, outcome, **evidence):
            nonlocal active
            result = session.channel.ask('UserDataCheckpoint', Record={'Step': step, 'Outcome': outcome, **evidence})
            active = step if outcome == 'IntentDurable' else None
            return result
        def independent(step):
            connected.verify()
            request = {'Step': step, 'Authority': base64.b64encode(authority).decode(), 'RootFd': view.fd,
                'RootIdentity': list(view.expected), 'PayloadFd': payload.fd, 'ManifestFd': manifest}
            fds = [view.fd, payload.fd, manifest]
            if context is not None:
                context.verify(); inherited = [context.source.fd, context.root.fd, context.home.fd]; fds += inherited
                request['Producer'] = {'Plan': base64.b64encode(transfer).decode(), 'Authority': request['Authority'],
                    'Fds': inherited, 'Identities': [producer.identity(f) for f in inherited], 'Accounts': context.accounts,
                    'SourceBefore': context.source_before, 'DestinationBefore': context.before}
            response = subprocess.run([session.runtime.python, '-I', '-B', session.declaration['EntryPath']],
                input=canonical({**session.declaration, 'UserDataObservation': request})+b'\n', pass_fds=fds,
                capture_output=True, timeout=7200, check=False)
            require(response.returncode == 0 and 0 < len(response.stdout) < 1024*1024, 'UserDataIndependentRootObserverFailed')
            value = contract.decode(response.stdout)
            require(value['OperationId'] == plan['OperationId'] and value['SessionId'] == session.declaration['SessionId'] and
                value['PlanSha256'] == session.declaration['PlanSha256'] and value['Step'] == step and
                value['RootIdentity'] == list(view.expected), 'UserDataIndependentRootObserverChanged')
            connected.verify()
            return {'ObserverEvidence': value, 'ObserverEvidenceSha256': digest(canonical(value))}
        checkpoint('Baseline', 'IntentDurable')
        checkpoint('Baseline', 'AppliedAndVerified', **independent('Baseline'))
        context = CanonicalContext(session, connected, authority)
        connected.verify(); session.channel.checkpoint({'State': 'IntentDurable', 'Operation': 'TransferUserData'})
        checkpoint('Transfer', 'IntentDurable', AuthoritySha256=digest(authority))
        produced = producer._transfer(context)
        directory = beneath(context.store.fd, context.plan['OperationId'], os.O_RDONLY | os.O_DIRECTORY)
        try: records = [journal.read(directory, n) for n in sorted(os.listdir(directory)) if n != 'plan.sha256']
        finally: os.close(directory)
        receipt = journal.read(context.store.fd, 'userdata-receipt.json')
        checked = contract.terminal(transfer, records, receipt, authority)
        require(checked['ResultSha256'] == produced['ResultSha256'], 'UserDataTerminalChanged')
        bundle = canonical({'SchemaVersion': 1, 'Scope': contract.SCOPE, 'Authority': base64.b64encode(authority).decode(),
            'Transfer': base64.b64encode(transfer).decode(), 'Records': [base64.b64encode(r).decode() for r in records]})
        contract.validate_admitted_bundle(bundle, receipt, authority)
        checkpoint('Transfer', 'AppliedAndVerified', Receipt=base64.b64encode(receipt).decode(), Bundle=base64.b64encode(bundle).decode(),
            **independent('Transfer'))
        intent = admission.declaration(view.fd, bundle, receipt, authority)
        checkpoint('Admission', 'IntentDurable', Admission=intent)
        admission.publish(view.fd, bundle, receipt, authority, intent, context.verify)
        checkpoint('Admission', 'AppliedAndVerified', **independent('Admission'))
        checkpoint('Verify', 'IntentDurable')
        result = checkpoint('Verify', 'AppliedAndVerified', **independent('Verify'))
        connected.verify(); session.channel.checkpoint({'State': 'AppliedAndVerified', 'UserDataResultReference': result['Reference']})
        return {'Reference': result['Reference'], 'Sha256': result['Sha256'], 'OperationId': plan['OperationId']}
    except (OSError, ValueError, KeyError, TypeError, ImportError, AttributeError, subprocess.TimeoutExpired):
        try:
            if active is not None: checkpoint(active, 'OutcomeUnknown', Code='UserDataEffectOrObservationUnavailable')
            else: session.channel.ask('UserDataStopped')
        except (OSError, ValueError, KeyError): pass
        raise
    finally:
        if context is not None: context.close()
        for fd in descriptors: os.close(fd)
        connected.close()
