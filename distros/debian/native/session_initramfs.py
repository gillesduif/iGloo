"""Closed configured-successor image effect on the canonical private pipe.

Generation uses the qualified read-only target broker. Only publication writes
the exact new /boot image. No update-initramfs state, aliases or bootloader work.
"""
import base64
import copy
import json
import os
from pathlib import Path
import re
import subprocess

import configured_root as root
import configured_successor
import initramfs_candidate as candidate
import initramfs_publication as publication
import initramfs_observer
import session_import
from isolation_policy import canonical, digest, require
from package_broker import DirectoryLease, PackageBroker
from target_files import beneath


def bound_root_uuid(bindings):
    # InstallerBlockLeaseRole is serialized numerically by the shared contract.
    # The native role names label open handles; they are not wire enum values.
    require(type(bindings) is list and len(bindings) == 3 and
            all(type(b) is dict and type(b.get('Role')) is int and type(b.get('Access')) is int for b in bindings) and
            [b['Role'] for b in bindings] == [0, 1, 2] and
            [b['Access'] for b in bindings] == [1, 0, 0] and
            [b.get('FileSystem') for b in bindings] == ['EXT4', 'FAT32', 'FAT32'] and
            all(type(b.get('FileSystemUuid')) is str for b in bindings) and
            re.fullmatch(r'[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}', bindings[0]['FileSystemUuid']) and
            all(re.fullmatch(r'[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}', b['FileSystemUuid']) for b in bindings[1:]) and
            len({b['FileSystemUuid'].upper() for b in bindings}) == 3, 'InitramfsCanonicalRootBinding')
    # .NET canonical leases use uppercase UUID text; the qualified candidate
    # declaration uses canonical lowercase UUID text. Identity is unchanged.
    return bindings[0]['FileSystemUuid'].lower()


def profile(declaration):
    plan = declaration['InitramfsPlan']; predecessor = declaration['ConfiguredPredecessor']
    require(plan['SchemaVersion'] == 1 and plan['Provenance'] == declaration['LabInitramfs'] and
            plan['ExecutionSha256'] == declaration['ExecutionSha256'] and
            plan['Provenance']['Scope'] == 'InitramfsImage' and plan['Provenance']['Version'] == 4 and
            plan['ConfigurationSha256'] == predecessor['ResultSha256'] and
            plan['ConfigurationCloseSha256'] == predecessor['CloseSha256'] and
            plan['ConfiguredEntriesSha256'] == predecessor['Observation']['FilesystemDeltaSha256'] and
            plan['Workspace'] == '/var/lib/igloo/initramfs-workspaces/' + plan['Provenance']['RunId'] and
            plan['Candidate']['ConfigurationResultSha256'] == plan['ConfigurationSha256'], 'InitramfsSuccessorChanged')
    return plan, predecessor


def configured_baseline(view, original, predecessor, saved=None, image=None):
    if image is None:
        return configured_successor.configured_manifest(view, original, predecessor['Observation'])
    require(saved is not None and digest(canonical(saved['Entries'])) == predecessor['Observation']['FilesystemDeltaSha256'] and
            {k: v for k, v in saved.items() if k != 'Entries'} == {k: v for k, v in original.items() if k != 'Entries'},
            'InitramfsSavedConfiguredBaselineChanged')
    expected = copy.deepcopy(saved)
    require(not any(e['Path'] == '/boot/' + publication.LEAF for e in expected['Entries']), 'InitramfsBaselineAlreadyPublished')
    expected['Entries'].append({'Path': '/boot/' + publication.LEAF, 'Type': 'File', 'Uid': 0, 'Gid': 0,
        'Mode': 0o600, 'Length': image['Length'], 'Sha256': image['Sha256'], 'Target': None, 'Xattrs': {}})
    expected['Entries'].sort(key=lambda e: e['Path'])
    root.verify_tree(view, expected)
    require(root.verify_dpkg(view, original) == predecessor['Observation']['PackageStateSha256'], 'InitramfsPackageStateChanged')
    return saved


def observe(declaration):
    plan, predecessor = profile(declaration); request = declaration['InitramfsObservation']
    view = root.RootView(request['RootFd'], *request['RootIdentity'])
    try:
        raw = session_import.read_bound(request['ManifestFd'], root.MAX_MANIFEST)
        require(digest(raw) == predecessor['Imported']['ManifestSha256'], 'InitramfsOriginalManifestChanged')
        original = root.validate_manifest(raw)
        installed = request['Step'] in ('Publication', 'Verify')
        image = publication.candidate(request['CandidateFd']) if request.get('CandidateFd') is not None else None
        if image is not None:
            require(image == request['CandidateIdentity'], 'InitramfsObserverCandidateSubstituted')
        saved = json.loads(session_import.read_bound(request['BaselineFd'], root.MAX_MANIFEST)) if installed else None
        baseline = configured_baseline(view, original, predecessor, saved, image if installed else None)
        require(candidate.declaration(baseline, plan['Candidate']['RootUuid'], predecessor['ResultSha256']) == plan['Candidate'],
                'InitramfsObserverInputsChanged')
        result = {'OperationId': plan['OperationId'], 'PlanSha256': declaration['PlanSha256'], 'Step': request['Step'],
            'RootIdentity': request['RootIdentity'], 'ConfiguredEntriesSha256': plan['ConfiguredEntriesSha256'],
            'PackageStateSha256': predecessor['Observation']['PackageStateSha256'], 'Image': image}
        if request['Step'] in ('Candidate', 'Verify'):
            data = session_import.read_bound(request['CandidateFd'], candidate.MAX_IMAGE)
            query = {'Stage': str(Path(declaration['EntryPath']).parent.parent), 'Target': request['Target'], 'Plan': plan['Candidate']}
            result['ImageSemantics'] = candidate.observe_image(data, baseline, plan['Candidate'],
                root.read_small(view, '/boot/config-' + candidate.RELEASE),
                root.read_small(view, '/usr/lib/modules/' + candidate.RELEASE + '/modules.dep'), view,
                lambda members: initramfs_observer.observe_generated(members, baseline, view, query, request['Queries']))
            require(publication.candidate(request['CandidateFd']) == image, 'InitramfsObserverImageChanged')
            configured_baseline(view, original, predecessor, saved, image if installed else None)
            result['CompleteImageQualification'] = True
        print(canonical(result).decode(), flush=True)
        return 0
    finally: view.close()


def perform(session):
    require(not session.initramfs_attempted, 'InitramfsSingleUse'); session.initramfs_attempted = True
    plan, predecessor = profile(session.declaration)
    connected = session_import.ConnectedImportView(session)
    descriptors = []; resources = {}; active = None; location = 'Source'
    try:
        view, payload = connected.views['Root'], connected.views['Payload']
        imported = predecessor['Imported']; folder = 'configured-root/' + imported['BuildId'] + '/' + imported['DerivationId']
        descriptor = beneath(payload.fd, folder + '/descriptor.json', os.O_RDONLY); descriptors.append(descriptor)
        raw = session_import.read_bound(descriptor, 4*1024*1024)
        require(digest(raw) == imported['DescriptorSha256'], 'InitramfsDescriptorChanged')
        session.channel.ask('AuthenticateConfigurationSource', Descriptor=base64.b64encode(raw).decode())
        manifest_fd = beneath(payload.fd, folder + '/root.manifest.json', os.O_RDONLY); descriptors.append(manifest_fd)
        raw = session_import.read_bound(manifest_fd, root.MAX_MANIFEST)
        require(digest(raw) == imported['ManifestSha256'], 'InitramfsManifestChanged')
        original = root.validate_manifest(raw)
        target = next(r['Path'] for r in session.supervisor.receipts if r['Role'] == 'Root')
        parent = Path('/var/lib/igloo/initramfs-workspaces')/plan['Provenance']['RunId']
        store = DirectoryLease(str(parent)); resources['store'] = store
        require(os.fstat(store.fd).st_uid == 0 and os.fstat(store.fd).st_mode & 0o777 == 0o700 and
                os.fstat(store.fd).st_dev not in (view.expected[0], payload.expected[0]), 'InitramfsWorkspacePlacement')
        space = os.fstatvfs(store.fd); target_space = os.fstatvfs(view.fd)
        require(not space.f_flag & os.ST_NOEXEC and space.f_bavail * space.f_frsize >= 4*1024**3 and space.f_favail >= 10000 and
                target_space.f_bavail * target_space.f_frsize >= candidate.MAX_IMAGE + 1024**3 and target_space.f_favail >= 1,
                'InitramfsWorkspaceOrPublicationCapacity')
        def checkpoint(step, outcome, **evidence):
            nonlocal active
            value = session.channel.ask('InitramfsCheckpoint', Record={'Step': step, 'Outcome': outcome, **evidence})
            active = step if outcome == 'IntentDurable' else None
            return value
        baseline_fd = None
        def independent(step, image_fd=None):
            connected.verify(); store.verify()
            request = {**session.declaration, 'InitramfsObservation': {'Step': step, 'RootFd': view.fd,
                'RootIdentity': list(view.expected), 'Target': target, 'ManifestFd': manifest_fd, 'BaselineFd': baseline_fd,
                'CandidateFd': image_fd, 'CandidateIdentity': publication.candidate(image_fd) if image_fd is not None else None,
                'Queries': str(parent/('observe-' + step))}}
            fds = [view.fd, manifest_fd] + ([image_fd] if image_fd is not None else []) + ([baseline_fd] if baseline_fd is not None else [])
            result = subprocess.run([session.runtime.python, '-I', '-B', session.declaration['EntryPath']],
                input=canonical(request)+b'\n', pass_fds=fds, capture_output=True, timeout=7200, check=False)
            require(result.returncode == 0 and 0 < len(result.stdout) < 65536, 'InitramfsIndependentObservationFailed:' + step)
            observation = json.loads(result.stdout)
            require(observation['OperationId'] == plan['OperationId'] and observation['PlanSha256'] == session.declaration['PlanSha256'] and
                    observation['Step'] == step and observation['RootIdentity'] == list(view.expected), 'InitramfsObserverSubstituted')
            return {'ObserverEvidence': observation, 'ObserverEvidenceSha256': digest(canonical(observation))}
        location = 'Baseline'
        checkpoint('Baseline', 'IntentDurable')
        checkpoint('Baseline', 'AppliedAndVerified', **independent('Baseline'))
        location = 'GenerationInputs'
        baseline = configured_successor.configured_manifest(view, original, predecessor['Observation'])
        candidate.verify_execution_inputs(view, baseline, plan['Candidate'],
            set(re.findall(r'^vendor_id\s*:\s*(\S+)', Path('/proc/cpuinfo').read_text(), re.M)))
        require(plan['Candidate']['RootUuid'] == bound_root_uuid(session.blocks.bindings), 'InitramfsRootUuidChanged')
        connected.verify(); session.channel.checkpoint({'State': 'IntentDurable', 'Operation': 'GenerateInitramfs'})
        # Workspace and baseline-reference writes are covered by the session intent;
        # target mutation requires the later distinct publication intent.
        baseline_fd = beneath(store.fd, 'configured-baseline.json', os.O_CREAT | os.O_EXCL | os.O_RDWR, 0o600)
        descriptors.append(baseline_fd)
        data = canonical(baseline); remaining = memoryview(data)
        while remaining:
            count = os.write(baseline_fd, remaining); require(count > 0, 'BaselineReferenceShortWrite'); remaining = remaining[count:]
        os.fsync(baseline_fd); os.fsync(store.fd)
        workspace = candidate.prepare_workspace(parent/'workspace', view, baseline)
        resources.update(root=DirectoryLease(target), workspace=DirectoryLease(str(workspace)),
            payload=DirectoryLease(next(r['Path'] for r in session.supervisor.receipts if r['Role'] == 'Payload')))
        def generation_intent(operation, isolation):
            checkpoint('Generation', 'IntentDurable', OperationSha256=operation, IsolationSha256=isolation)
            connected.verify(); store.verify()
        location = 'Generation'
        generated = PackageBroker(session.runtime).execute(candidate.launch(plan['OperationId'], session.declaration['PlanSha256'], plan['Candidate']),
            {k: resources[k] for k in ('root', 'workspace', 'payload')}, generation_intent)
        require(generated['State'] == 'Exited' and generated['ExitCode'] == 0, 'CanonicalInitramfsGenerationFailed')
        image_fd = beneath(resources['workspace'].fd, 'candidate.img', os.O_RDONLY); descriptors.append(image_fd)
        os.fsync(image_fd); os.fsync(resources['workspace'].fd)
        checkpoint('Generation', 'AppliedAndVerified', HelperEvidence=generated, **independent('Generation', image_fd))
        location = 'Candidate'
        checkpoint('Candidate', 'IntentDurable')
        checkpoint('Candidate', 'AppliedAndVerified', **independent('Candidate', image_fd))
        intent = publication.declaration(view.fd, image_fd)
        location = 'Publication'
        checkpoint('Publication', 'IntentDurable', Publication=intent)
        published = publication.publish(view.fd, image_fd, intent, connected.verify)
        installed_fd = beneath(view.fd, 'boot/' + publication.LEAF, os.O_RDONLY); descriptors.append(installed_fd)
        require(publication.candidate(installed_fd) == published, 'InstalledImageSubstituted')
        checkpoint('Publication', 'AppliedAndVerified', Publication=published, **independent('Publication', installed_fd))
        location = 'Verify'
        checkpoint('Verify', 'IntentDurable')
        result = checkpoint('Verify', 'AppliedAndVerified', **independent('Verify', installed_fd))
        location = 'Handoff'
        connected.verify(); session.channel.checkpoint({'State': 'AppliedAndVerified', 'InitramfsResultReference': result['Reference']})
        return {'Reference': result['Reference'], 'Sha256': result['Sha256'], 'OperationId': plan['OperationId']}
    except (OSError, ValueError, KeyError, TypeError, StopIteration, subprocess.TimeoutExpired):
        if active is not None:
            try: checkpoint(active, 'OutcomeUnknown', Code='InitramfsExecutionOrObservationUnavailable', Location=location)
            except (OSError, ValueError, KeyError): pass
        else:
            # A failure between effect records must not disappear solely as an
            # EOF. This records failure only; it never re-enables the session.
            try: session.channel.ask('InitramfsStopped', Location=location)
            except (OSError, ValueError, KeyError): pass
        raise
    finally:
        for fd in descriptors: os.close(fd)
        for lease in resources.values(): lease.close()
        connected.close()
