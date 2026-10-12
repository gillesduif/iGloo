"""Closed, lab-only configuration/initramfs host continuation.

This controller does not grant a stage operation or its successor authority.
Those remain the separate protected one-use authorization records consumed by
the existing derivation and completion publisher. No public CLI capability uses
this file.
"""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import selectors
import stat
import subprocess
import sys
import time
import uuid

if __name__ == '__main__':
    # Isolated Python never falls back to CWD or a caller's PYTHONPATH. This
    # host-only source stage is root-custodied before its local imports.
    here = Path(__file__).absolute()
    for item in (here, here.parent, *here.parents):
        state = item.lstat()
        if (stat.S_ISLNK(state.st_mode) or state.st_uid != 0 or
                state.st_mode & 0o022 or
                (item == here and (not stat.S_ISREG(state.st_mode) or
                                   state.st_nlink != 1))):
            raise ValueError('StageCallerToolStageUnprotected')
    sys.path.insert(0, str(here.parent))

import canonical_stage_acquisition as acquisition
import canonical_stage_completion as completion
import canonical_storage_lab as storage
from canonical_lab_runtime import checkpoint
from canonical_stage_files import read_file


REQUEST_FIELDS = {'Version', 'Scope', 'Stage', 'Previous', 'Repo', 'Publish',
    'SourceAuthorization', 'Pin', 'ReaderRuntime', 'ReaderRuntimeSha256',
    'NextAuthorization', 'ConfigurationCheckpoint', 'FailedConfiguration',
    'Acceptance', 'AcceptanceSha256', 'ConfiguredCompletion',
    'ConfiguredPredecessor'}
SOURCE_FILES = {'canonical_stage_lifecycle.py', 'canonical_stage_acquisition.py',
    'canonical_stage_completion.py', 'canonical_stage_files.py',
    'canonical_storage_lab.py', 'canonical_configuration_derivation.py',
    'canonical_initramfs_lab.py', 'canonical_userdata_lab.py',
    'canonical_import_checkpoint_acceptance.py',
    'canonical_import_checkpoint_inspection.py', 'canonical_import_readback.py',
    'checkpoint_readonly_inspection.py', 'factory_artifact_verifier.py'}
HEX = re.compile(r'[0-9A-F]{64}\Z')
# Independently recorded for the retained, authenticated Linux .NET 8.0.20
# development runtime. This pins the executable, not publisher provenance or
# every adjacent runtime library; those remain a native tool-stage obligation.
READER_DOTNET_SHA256 = '37674A9F73C1F531B7DCB26F569692F2C85419C0BA4FB7622D5BFC65BB0F5810'


def require(value, code):
    if not value:
        raise ValueError(code)


def _unique(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, 'StageCallerDuplicateField')
        value[key] = item
    return value


def _path(value):
    require(type(value) is str and value.startswith('/') and
            '..' not in Path(value).parts and str(Path(value)) == value,
            'StageCallerPathChanged')
    return Path(value)


def _request(path):
    completion.protected(path)
    raw = read_file(path, limit=16 * 1024)
    value = json.loads(raw, object_pairs_hook=_unique)
    require(type(value) is dict and set(value) == REQUEST_FIELDS and
            type(value['Version']) is int and value['Version'] == 1 and
            value['Scope'] == 'InternalLabStageLifecycleV1' and
            type(value['Stage']) is str and value['Stage'] in completion.STAGES and
            type(value['ReaderRuntimeSha256']) is str and
            HEX.fullmatch(value['ReaderRuntimeSha256']) and
            ((value['Stage'] == 'configuration' and
              type(value['AcceptanceSha256']) is str and
              HEX.fullmatch(value['AcceptanceSha256'])) or
             (value['Stage'] == 'initramfs' and
              value['AcceptanceSha256'] is None)),
            'StageCallerRequestChanged')
    for name in REQUEST_FIELDS - {'Version', 'Scope', 'Stage',
                                  'ReaderRuntimeSha256', 'AcceptanceSha256'}:
        if value[name] is not None:
            _path(value[name])
    stage = value['Stage']
    require((stage == 'configuration' and
             all(value[name] is not None for name in
                 ('ConfigurationCheckpoint', 'FailedConfiguration', 'Acceptance')) and
             value['ConfiguredCompletion'] is None and
             value['ConfiguredPredecessor'] is None) or
            (stage == 'initramfs' and value['ConfiguredCompletion'] is not None and
             value['ConfiguredPredecessor'] is not None and
             all(value[name] is None for name in
                 ('ConfigurationCheckpoint', 'FailedConfiguration', 'Acceptance'))),
            'StageCallerPredecessorKindChanged')
    return value, hashlib.sha256(raw).hexdigest().upper()


def _pin(path):
    completion.protected(path)
    value = json.loads(read_file(path, limit=16 * 1024), object_pairs_hook=_unique)
    require(value.get('Use') == 'DevelopmentImportOnly' and
            value.get('ProductionAuthentication') == 'Unsupported' and
            all(type(value.get(name)) is str and HEX.fullmatch(value[name])
                for name in ('DescriptorSha256', 'PolicySha256',
                             'ManifestSha256', 'ContentSha256')),
            'StageCallerDevelopmentPinChanged')
    try:
        lower = dt.datetime.fromisoformat(value['NotBeforeUtc'].replace('Z', '+00:00'))
        upper = dt.datetime.fromisoformat(value['NotAfterUtc'].replace('Z', '+00:00'))
        now = dt.datetime.now(dt.timezone.utc)
    except (KeyError, TypeError, ValueError, AttributeError) as error:
        raise ValueError('StageCallerDevelopmentPinInvalid') from error
    require(lower.tzinfo is not None and upper.tzinfo is not None and
            lower <= now < upper, 'StageCallerDevelopmentPinExpired')


def _preflight(value):
    """Reject wrong scope and input authority before run creation/reservation."""
    stage = value['Stage']
    auth = _path(value['SourceAuthorization'])
    require(auth.parent.parent == acquisition.AUTH_PARENT[stage] and
            auth.name == 'authorization.json', 'StageCallerAuthorizationPathChanged')
    completion.protected(auth)
    source = json.loads(read_file(auth), object_pairs_hook=_unique)
    require(source.get('Version') == 1 and
            source.get('Scope') == completion.STAGES[stage]['source_scope'] and
            auth.parent.name == source.get('AttemptId') and
            str(uuid.UUID(source['AttemptId'])) == source['AttemptId'] and
            str(uuid.UUID(source['OperationId'])) == source['OperationId'] and
            str(uuid.UUID(source['GenerationId'])) == source['GenerationId'],
            'StageCallerAuthorizationChanged')
    run = acquisition.RUN_PARENT[stage]/uuid.UUID(source['AttemptId']).hex
    require(not run.exists() and
            not (auth.parent/'reservation.json').exists() and
            not (auth.parent/'completion-process.json').exists() and
            not (auth.parent/'completion-controller-success.json').exists(),
            'StageCallerOperationAlreadyAllocated')
    next_auth = _path(value['NextAuthorization'])
    require(next_auth.parent.parent == completion.NEXT_AUTH_ROOTS[stage] and
            next_auth.name == 'authorization.json',
            'StageCallerNextAuthorizationPathChanged')
    _pin(_path(value['Pin']))
    previous = _path(value['Previous'])
    require(previous.parent == acquisition.RUN_PARENT[stage],
            'StageCallerRuntimeSourceChanged')
    if stage == 'configuration':
        native = _path(value['Repo'])/'distros/debian/native'
        completion.protected(native, file=False)
        # The established checkpoint reader imports the native neutral-root
        # observer. This path is part of the frozen, root-custodied source set.
        sys.path.insert(0, str(native))
        try:
            from canonical_import_checkpoint_acceptance import verify_acceptance
            accepted = verify_acceptance(_path(value['Acceptance']),
                                         value['AcceptanceSha256'])
        finally:
            sys.path.remove(str(native))
        checkpoint_dir = _path(value['ConfigurationCheckpoint'])
        require(checkpoint_dir == Path(accepted['Candidate'].rstrip('/')) and
                source.get('CheckpointSha256') == accepted['CheckpointResultSha256'] and
                source.get('ImportSha256') == accepted['ImportResultSha256'] and
                source.get('CloseSha256') == accepted['CloseResultSha256'],
                'StageCallerImportAcceptanceChanged')
    else:
        verified = completion.verify(_path(value['ConfiguredCompletion']), auth,
                                     'configuration')
        require(_path(value['ConfiguredPredecessor']).name ==
                verified['SourceRunId'].replace('-', ''),
                'StageCallerConfiguredPredecessorChanged')
    return run, auth, source


def _source_files(value):
    """Only root-custodied selected sources and the complete published DLL tree."""
    repo, publish, runtime = (_path(value[name]) for name in
                              ('Repo', 'Publish', 'ReaderRuntime'))
    completion.protected(repo, file=False)
    completion.protected(publish, file=False)
    completion.protected(runtime)
    names = (SOURCE_FILES | acquisition.HOST_CLOSURE |
             storage.STAGE_GUEST_FILES['configuration'] |
             storage.STAGE_GUEST_FILES['initramfs'])
    require(Path(__file__).resolve() ==
            (repo/'tests/installer/canonical_stage_lifecycle.py').resolve(),
            'StageCallerSourceLocationChanged')
    installer = repo/'tests/installer'
    native_names = {'userdata_contract.py', 'debian_first_boot.py',
                    'isolation_policy.py'}
    completion.protected(installer, file=False)
    require({path.name for path in installer.iterdir()} == names-native_names and
            all(path.is_file() and not path.is_symlink()
                for path in installer.iterdir()),
            'StageCallerSourceClosureChanged')
    files = {installer/name for name in names-native_names}
    files |= {repo/'distros/debian/native'/name for name in names & native_names}
    files |= {repo/'distros/_shared/installer/collect_inventory.py',
              repo/'distros/debian/native/command_gate.c'}
    native = repo/'distros/debian/native'
    completion.protected(native, file=False)
    for root in (native, publish):
        for path in root.rglob('*'):
            require(not path.is_symlink() and path.name != '__pycache__',
                    'StageCallerToolEntryChanged')
            completion.protected(path, file=not path.is_dir())
            if path.is_file():
                files.add(path)
            else:
                require(path.is_dir(), 'StageCallerToolEntryChanged')
    require(len(files) <= 2048 and files and
            (publish/'CanonicalImportQualification.dll') in files,
            'StageCallerToolInventoryChanged')
    hashes = {}; total = 0
    for path in sorted(files):
        completion.protected(path)
        state = path.lstat()
        require(stat.S_ISREG(state.st_mode) and state.st_nlink == 1 and
                0 < state.st_size <= 256 * 1024 * 1024,
                'StageCallerToolObjectChanged')
        total += state.st_size
        require(total <= 512 * 1024 * 1024, 'StageCallerToolInventoryOversized')
        hashes[str(path)] = storage.sha(path)
    require(value['ReaderRuntimeSha256'] == READER_DOTNET_SHA256 and
            storage.sha(runtime) == READER_DOTNET_SHA256,
            'StageCallerReaderRuntimeChanged')
    return hashes


def _freeze_source(value, authorization, request_sha):
    hashes = _source_files(value)
    checkpoint(authorization.parent, 'completion-caller-source.json', {
        'Version': 1, 'Scope': 'FrozenLabStageCallerSourceV1',
        'Stage': value['Stage'], 'RequestSha256': request_sha,
        'Files': hashes, 'ReaderRuntimeSha256': value['ReaderRuntimeSha256']})
    require(json.loads(read_file(authorization.parent/'completion-caller-source.json'))
            ['Files'] == hashes, 'StageCallerToolManifestReopenChanged')
    return hashes


def _verify_staged_closure(value, authorization, run, hashes):
    """Join frozen source bytes to the exact staged guest, host and .NET files."""
    stage = value['Stage']
    repo, publish = _path(value['Repo']), _path(value['Publish'])
    run = Path(run)
    require(hashes == _source_files(value), 'StageCallerSourceChangedDuringProvisioning')
    guest_path = authorization.parent/'guest-import-closure.json'
    host_path = authorization.parent/'completion-tools.json'
    launch_path = authorization.parent/'completion-launch.json'
    for path in (guest_path, host_path, launch_path):
        completion.protected(path)
    guest = json.loads(read_file(guest_path), object_pairs_hook=_unique)
    host = json.loads(read_file(host_path), object_pairs_hook=_unique)
    launch = json.loads(read_file(launch_path), object_pairs_hook=_unique)
    guest_files = storage.STAGE_GUEST_FILES[stage]
    require(set(guest) == {'Version', 'Stage', 'Entry', 'Files'} and
            guest['Version'] == 1 and guest['Stage'] == stage and
            guest['Entry'] == f'canonical_{stage}_guest.py' and
            set(guest['Files']) == guest_files and
            set(host) == {'Version', 'Scope', 'Stage', 'Files'} and
            host['Version'] == 1 and
            host['Scope'] == 'ProtectedStageAcquisitionToolsV1' and
            host['Stage'] == stage and
            set(host['Files']) == acquisition.HOST_CLOSURE and
            launch['GuestImportClosureSha256'] == storage.sha(guest_path) and
            launch['ReceiverManifestSha256'] == storage.sha(host_path) and
            launch['LaunchSha256'] == storage.sha(run/'launch-intent.json') and
            launch['ToolsSha256'] == storage.sha(run/'tools.iso'),
            'StageCallerStagedManifestChanged')
    input_root = run/'input'
    identities = json.loads(read_file(run/'input-identities.json'),
                            object_pairs_hook=_unique)
    require(read_file(input_root/'lab/guest-import-closure.json') ==
            read_file(guest_path), 'StageCallerGuestManifestCopyChanged')
    for name in guest_files:
        source = repo/'tests/installer'/name
        staged = input_root/'lab'/name
        completion.protected(staged)
        require(guest['Files'][name] == identities['lab/'+name] ==
                hashes[str(source)] == storage.sha(staged),
                'StageCallerGuestSourceChanged')
    for name in acquisition.HOST_CLOSURE:
        source = repo/('distros/debian/native' if name in
            ('userdata_contract.py', 'debian_first_boot.py', 'isolation_policy.py')
            else 'tests/installer')/name
        staged = authorization.parent/'completion-tools'/name
        completion.protected(staged)
        require(host['Files'][name] == hashes[str(source)] ==
                storage.sha(staged), 'StageCallerHostSourceChanged')
    prefix = str(publish) + '/'
    expected = {name[len(prefix):]: expected_hash for name, expected_hash
                in hashes.items() if name.startswith(prefix)}
    observed = {path.relative_to(input_root/'harness').as_posix(): path
                for path in (input_root/'harness').rglob('*') if path.is_file()}
    require(expected and set(expected) == set(observed),
            'StageCallerHarnessInventoryChanged')
    for name, path in observed.items():
        completion.protected(path)
        require(expected[name] == identities['harness/'+name] ==
                storage.sha(path), 'StageCallerHarnessSourceChanged')


def _bounded_reader(command, *, seconds=120):
    """Bound the read-only Linux reader's output, exit and inherited pipes."""
    require(type(seconds) in (int, float) and 0 < seconds <= 120,
            'StageCallerLinuxReaderDeadlineChanged')
    process = subprocess.Popen(command, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               env={'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
                                    'DOTNET_NOLOGO': '1'})
    output = {process.stdout: bytearray(), process.stderr: bytearray()}
    deadline = time.monotonic() + seconds
    try:
        with selectors.DefaultSelector() as selected:
            for stream in output:
                selected.register(stream, selectors.EVENT_READ)
            while selected.get_map():
                remaining = deadline - time.monotonic()
                require(remaining > 0, 'StageCallerLinuxReaderTimeout')
                for key, _ in selected.select(min(remaining, 1)):
                    data = os.read(key.fileobj.fileno(), 4096)
                    if not data:
                        selected.unregister(key.fileobj)
                        continue
                    output[key.fileobj].extend(data)
                    require(len(output[key.fileobj]) <= 8192,
                            'StageCallerLinuxReaderOutputBound')
        remaining = deadline - time.monotonic()
        require(remaining > 0, 'StageCallerLinuxReaderTimeout')
        process.wait(timeout=remaining)
        require(process.returncode == 0, 'StageCallerLinuxReaderFailed')
        return bytes(output[process.stdout])
    except BaseException:
        if process.poll() is None:
            process.terminate()  # Only the independent read-only reader.
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired as error:
                raise ValueError('StageCallerLinuxReaderUnstopped') from error
        raise
    finally:
        for stream in output:
            stream.close()


def _reader(value, pending, authorization):
    """Read the captured stage semantics with the actual Linux .NET successor reader."""
    stage = value['Stage']
    raw = pending/'raw'
    observed = completion.STAGES[stage]['readback'](
        raw/'guest-evidence/execution', raw/'guest-evidence/journals')
    require(observed[completion.STAGES[stage]['status']] ==
            observed['Teardown'] == 'AppliedAndVerified',
            'StageCallerIndependentReadbackChanged')
    source = authorization.parent/'completion-caller-source.json'
    frozen = json.loads(read_file(source), object_pairs_hook=_unique)
    require(frozen['Files'] == _source_files(value),
            'StageCallerToolSourceChanged')
    reader_input = authorization.parent/'completion-reader-input'
    if stage == 'configuration':
        from canonical_initramfs_lab import stage_predecessor_raw
        command = '--reopen-configured-predecessor'
    else:
        from canonical_userdata_lab import stage_predecessor_raw
        command = '--reopen-initramfs-predecessor'
    stage_predecessor_raw(raw, reader_input)
    runtime = _path(value['ReaderRuntime'])
    harness = _path(value['Publish'])/'CanonicalImportQualification.dll'
    decoded = json.loads(_bounded_reader([str(runtime), str(harness), command,
                                          str(reader_input)]), object_pairs_hook=_unique)
    anchor = json.loads(read_file(pending/'anchor.json'), object_pairs_hook=_unique)
    require(decoded.get('Scope') == ('ReadOnlyConfiguredPredecessor' if
            stage == 'configuration' else 'ReadOnlyInitramfsPredecessor') and
            decoded.get('EffectAuthorization') is False and
            decoded.get('ResultSha256') == anchor['ResultSha256'] and
            decoded.get('CloseSha256') == anchor['CloseSha256'] and
            decoded.get('PlanSha256') == anchor['PlanSha256'],
            'StageCallerLinuxReaderResultChanged')
    checkpoint(authorization.parent, 'completion-reader-result.json', {
        'Version': 1, 'Scope': 'LabStageReadOnlyLinuxReaderV1',
        'Stage': stage, 'AnchorSha256': storage.sha(pending/'anchor.json'),
        'ControllerSuccessSha256': storage.sha(
            authorization.parent/'completion-controller-success.json'),
        'SourceManifestSha256': storage.sha(source),
        'RuntimeSha256': storage.sha(runtime),
        'HarnessSha256': storage.sha(harness),
        'ReaderInput': str(reader_input),
        'ReaderResult': decoded, 'EffectAuthorization': False})
    return _reader_reopen(value, pending, authorization)


def _reader_reopen(value, pending, authorization):
    path = authorization.parent/'completion-reader-result.json'
    record = json.loads(read_file(path), object_pairs_hook=_unique)
    anchor_raw = read_file(pending/'anchor.json')
    anchor = json.loads(anchor_raw, object_pairs_hook=_unique)
    success_raw = read_file(
        authorization.parent/'completion-controller-success.json')
    completion._require_caller_reader(authorization.parent, value['Stage'],
                                      anchor, anchor_raw, success_raw)
    frozen = json.loads(read_file(
        authorization.parent/'completion-caller-source.json'),
        object_pairs_hook=_unique)
    require(record['Scope'] == 'LabStageReadOnlyLinuxReaderV1' and
            record['Stage'] == value['Stage'] and
            record['EffectAuthorization'] is False and
            record['AnchorSha256'] == storage.sha(pending/'anchor.json') and
            record['ControllerSuccessSha256'] == storage.sha(
                authorization.parent/'completion-controller-success.json') and
            record['SourceManifestSha256'] == storage.sha(
                authorization.parent/'completion-caller-source.json') and
            record['RuntimeSha256'] == storage.sha(_path(value['ReaderRuntime'])) and
            record['HarnessSha256'] == storage.sha(
                _path(value['Publish'])/'CanonicalImportQualification.dll') and
            record['ReaderResult']['ResultSha256'] == anchor['ResultSha256'] and
            record['ReaderResult']['CloseSha256'] == anchor['CloseSha256'] and
            record['ReaderResult']['PlanSha256'] == anchor['PlanSha256'] and
            frozen['Files'] == _source_files(value),
            'StageCallerReaderReopenChanged')
    return record


def _publish(value, run, authorization, pending):
    _reader_reopen(value, pending, authorization)
    stage = value['Stage']
    next_auth = _path(value['NextAuthorization'])
    require(next_auth.is_file(), 'StageCallerPublicationAuthorizationPending')
    completion.protected(next_auth)
    destination = pending.with_name(pending.name+'-published')
    if destination.exists():
        return completion.verify(destination, next_auth, stage)
    result = completion.publish(pending, next_auth, destination, stage)
    require(result == completion.verify(destination, next_auth, stage),
            'StageCallerPublishedReopenChanged')
    return result


def _publication_pending(value):
    return {'Stage': value['Stage'], 'State': 'PendingPublicationAuthorization',
            'EffectAuthorization': False}


def _failure_code(error):
    if type(error) is KeyboardInterrupt:
        return 'StageCallerCancelled'
    code = str(error) if type(error) is ValueError else type(error).__name__
    return code if re.fullmatch(r'[A-Za-z][A-Za-z0-9_.-]{0,79}', code) else 'StageCallerFailure'


def run(request_path, *, publication_only=False):
    value, request_sha = _request(Path(request_path))
    stage = value['Stage']; authorization = _path(value['SourceAuthorization'])
    pending = (acquisition.COMPLETION_PARENT/stage/
               uuid.UUID(authorization.parent.name).hex)
    run_path = acquisition.RUN_PARENT[stage]/pending.name
    destination = pending.with_name(pending.name+'-published')
    if publication_only:
        completion.protected(authorization)
        if destination.exists():
            return completion.verify(destination,
                _path(value['NextAuthorization']), stage)
        require(pending.is_dir() and
                (authorization.parent/'completion-controller-success.json').is_file(),
                'StageCallerDurableCompletionUnavailable')
        require(not (authorization.parent/'completion-caller-failure.json').exists(),
                'StageCallerPriorFailureRequiresReview')
        if not _path(value['NextAuthorization']).is_file():
            return _publication_pending(value)
        return _publish(value, run_path, authorization, pending)
    if destination.exists():
        return completion.verify(destination,
            _path(value['NextAuthorization']), stage)
    # A lost response after durable completion is read-only here. It must not
    # allocate a new run or repeat the fixed guest command.
    if (authorization.parent/'completion-controller-success.json').exists():
        _reader_reopen(value, pending, authorization)
        raise ValueError('StageCallerCompletionAlreadyRecorded')
    boundary = 'Preflight'; launched = {}; capture_started = False
    handle = None; launch = None; process = None
    try:
        _source_files(value)
        run_path, authorization, _ = _preflight(value)
        hashes = _freeze_source(value, authorization, request_sha)
        boundary = 'Provisioning'

        def acquired(process, path):
            launched['Pid'] = process.pid
            launched['Run'] = Path(path)

        def staged(path):
            _verify_staged_closure(value, authorization, path, hashes)

        common = {'configuration_pin': _path(value['Pin']),
                  'on_launch': acquired, 'before_launch': staged}
        if stage == 'configuration':
            common.update(configuration_checkpoint=_path(value['ConfigurationCheckpoint']),
                derived_authorization=authorization,
                failed_configuration=_path(value['FailedConfiguration']))
        else:
            common.update(initramfs_authorization=authorization,
                configured_predecessor=_path(value['ConfiguredPredecessor']),
                configured_completion=_path(value['ConfiguredCompletion']))
        actual_run = storage.create(_path(value['Previous']), _path(value['Repo']),
                                    _path(value['Publish']), **common)
        require(actual_run == run_path and launched.get('Run') == run_path,
                'StageCallerLaunchOwnershipChanged')
        boundary = 'LaunchObservation'
        handle, launch, process = acquisition.pin_writer(stage, run_path,
                                                          authorization)
        binding = storage.observe(run_path,
            protected_binding=authorization.parent/'completion-host-binding.json')
        require(storage.sha(binding) == storage.sha(
            authorization.parent/'completion-host-binding.json') and
            hashes == _source_files(value), 'StageCallerSourceOrBindingChanged')
        boundary = 'GuestPreparation'
        acquisition.prepare_live(stage, run_path, authorization,
            authorization.parent/'completion-host-binding.json')
        boundary = 'LiveCapture'
        capture_started = True
        seal = acquisition.complete_live(stage, run_path, authorization, pending)
        os.close(handle); handle = None
        boundary = 'ControllerSuccess'
        acquisition.record_controller_success(stage, run_path, authorization,
                                              pending, seal)
        boundary = 'LinuxReader'
        _reader(value, pending, authorization)
        boundary = 'Publication'
        if not _path(value['NextAuthorization']).is_file():
            return _publication_pending(value)
        return _publish(value, run_path, authorization, pending)
    except BaseException as error:
        excluded = None
        if (handle is not None and
                not (authorization.parent/'completion-failure-disposition.json').exists() and
                not (authorization.parent/'completion-writer-disposition.json').exists()):
            try:
                excluded = acquisition.failure_disposition(stage, run_path,
                    authorization, pending, handle, launch, process, error)
            except (OSError, ValueError):
                excluded = False
        elif launched and not capture_started and handle is None:
            try:
                reopened, launch, process = acquisition.pin_writer(stage,
                    run_path, authorization)
                try:
                    excluded = acquisition.failure_disposition(stage, run_path,
                        authorization, pending, reopened, launch, process, error)
                finally:
                    os.close(reopened)
            except (OSError, ValueError):
                excluded = False
        elif capture_started:
            disposition = authorization.parent/'completion-failure-disposition.json'
            if disposition.is_file():
                excluded = json.loads(read_file(disposition)).get('WriterExcluded')
            elif (authorization.parent/'completion-writer-disposition.json').is_file():
                excluded = json.loads(read_file(
                    authorization.parent/'completion-writer-disposition.json')).get('WriterExcluded')
        try:
            if not (authorization.parent/'completion-caller-failure.json').exists():
                checkpoint(authorization.parent, 'completion-caller-failure.json', {
                    'Version': 1, 'Scope': 'InternalLabStageCallerFailureV1',
                    'Stage': stage, 'RunId': str(uuid.UUID(run_path.name)),
                    'Boundary': boundary, 'Code': _failure_code(error),
                    'LaunchObserved': bool(launched), 'WriterExcluded': excluded,
                    'EffectAuthorization': False})
        except (OSError, ValueError):
            pass
        raise
    finally:
        if handle is not None:
            os.close(handle)


def main(argv):
    require(len(argv) == 2 and argv[0] in ('run', 'publish'),
            'StageCallerCommandUnsupported')
    try:
        result = run(argv[1], publication_only=argv[0] == 'publish')
        print(json.dumps({'State': result.get('State', 'ProtectedStageCompletionPublished'),
                          'Stage': result['Stage'], 'EffectAuthorization': False},
                         sort_keys=True))
        return 0
    except (Exception, KeyboardInterrupt) as error:
        print(json.dumps({'State': 'Stopped', 'Code': _failure_code(error),
                          'EffectAuthorization': False}, sort_keys=True))
        return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
