"""Protected lab completion input for configuration and initramfs successors.

These stage-specific v1 records are prospective. An old QEMU-owned export or
an independently recopied chain cannot be turned into one by this reader.
Only a controlled live capture may publish the pending/anchor/result chain.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import uuid

from canonical_configuration_readback import verify as verify_configuration
from canonical_initramfs_readback import verify as verify_initramfs
from canonical_stage_files import read_file as exact_file, snapshot_tree


STAGES = {
    'configuration': {
        'source_scope': 'OneCheckpointDerivedCoreConfiguration',
        'next_scope': 'OneConfiguredCheckpointInitramfs',
        'next_result': 'ConfigurationSha256',
        'stdout': 'configuration.stdout',
        'retention': 'configured-retention.json',
        'predecessor': 'input/predecessor/',
        'result': 'ConfigurationResult',
        'status': 'Configuration',
        'readback': verify_configuration,
    },
    'initramfs': {
        'source_scope': 'OneConfiguredCheckpointInitramfs',
        'next_scope': 'OneInitramfsCheckpointSelectedDocuments',
        'next_result': 'InitramfsSha256',
        'stdout': 'initramfs.stdout',
        'retention': 'initramfs-retention.json',
        'predecessor': 'input/configured-predecessor/',
        'result': 'InitramfsResult',
        'status': 'Initramfs',
        'readback': verify_initramfs,
    },
}
HEX = re.compile(r'[0-9A-F]{64}\Z')
PATH = re.compile(r'[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\Z')
MAX_FILES = 4096
MAX_BYTES = 128 * 1024 * 1024
COMPLETION_ROOT = Path('/var/lib/igloo/stage-completions')
NEXT_AUTH_ROOTS = {
    'configuration': Path('/var/lib/igloo/initramfs-authorizations'),
    'initramfs': Path('/var/lib/igloo/userdata-authorizations'),
}
SOURCE_AUTH_ROOTS = {
    'configuration': Path('/var/lib/igloo/configuration-authorizations'),
    'initramfs': Path('/var/lib/igloo/initramfs-authorizations'),
}
GUEST_IMPORT_FILES = {
    'configuration': {'canonical_storage_guest.py', 'canonical_import_guest.py',
        'canonical_configuration_guest.py', 'canonical_stage_guest.py',
        'canonical_stage_files.py', 'canonical_configuration_readback.py',
        'canonical_initramfs_readback.py'},
    'initramfs': {'canonical_storage_guest.py', 'canonical_import_guest.py',
        'canonical_configuration_guest.py', 'canonical_initramfs_guest.py',
        'canonical_stage_guest.py', 'canonical_stage_files.py',
        'canonical_configuration_readback.py', 'canonical_initramfs_readback.py'},
}


def protected(path, *, file=True):
    """The same no-follow, root-custody rule as the UserData completion store."""
    path = Path(path).absolute()
    for part in (path, *path.parents):
        state = part.lstat()
        require(not stat.S_ISLNK(state.st_mode) and state.st_uid == 0 and
                not state.st_mode & 0o022, 'StageCompletionPathUnprotected')
        if part == path:
            require(stat.S_ISREG(state.st_mode) if file else stat.S_ISDIR(state.st_mode),
                    'StageCompletionPathTypeChanged')
            if file:
                require(state.st_nlink == 1, 'StageCompletionFileAliased')


def require(value, code):
    if not value:
        raise ValueError(code)


def _uuid(value):
    try:
        return type(value) is str and str(uuid.UUID(value)) == value
    except (ValueError, AttributeError, TypeError):
        return False


def digest(raw):
    return hashlib.sha256(raw).hexdigest().upper()


def _unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'StageCompletionDuplicateField')
        result[key] = value
    return result


def _read(path, limit=4 * 1024 * 1024):
    try:
        raw = exact_file(path, limit=limit)
    except (OSError, ValueError) as error:
        raise ValueError('StageCompletionRequiredRecordUnavailable') from error
    require(len(raw) <= limit, 'StageCompletionRecordOversized')
    return json.loads(raw, object_pairs_hook=_unique), raw


def _guest_import_witness(source_store, launch, stage):
    observed, raw = _read(source_store/'guest-import-closure.json', limit=16 * 1024)
    require(set(observed) == {'Version', 'Stage', 'Entry', 'Files'} and
            type(observed['Version']) is int and observed['Version'] == 1 and
            observed['Stage'] == stage and
            observed['Entry'] == f'canonical_{stage}_guest.py' and
            type(observed['Files']) is dict and
            set(observed['Files']) == GUEST_IMPORT_FILES[stage] and
            all(type(value) is str and HEX.fullmatch(value)
                for value in observed['Files'].values()) and
            launch.get('GuestImportClosureSha256') == digest(raw),
            'StageCompletionGuestImportWitnessChanged')
    return raw


def _captured_record(paths, name, limit=4 * 1024 * 1024):
    raw = paths[name]
    require(len(raw) <= limit, 'StageCompletionRecordOversized')
    return json.loads(raw, object_pairs_hook=_unique), raw


def _require_no_failure_disposition(source_store):
    # A finalizer may have written anchor.json before its independent reopen
    # failed. Neither that anchor nor a previously published result can be
    # admitted once the controlled runner durably records such a failure.
    require(not any(os.path.lexists(source_store/name) for name in (
        'completion-failure-intent.json',
        'completion-failure-disposition.json')),
        'StageCompletionFailureDispositionPresent')


def _require_committed_seal(directory):
    # A failed candidate write/reopen or interrupted create-new link leaves
    # this staged name behind. Never infer success from a plausible final file
    # while its two-phase publication is still indeterminate.
    require(not os.path.lexists(Path(directory)/'completion-seal-staged.json'),
            'StageCompletionSealUncommitted')


def _require_caller_reader(source_store, stage, anchor, anchor_raw,
                           success_raw, *, publishing=False):
    """Close the stage caller's direct-publisher bypass when it owns this run.

    Earlier records without a caller source marker retain their v1 meaning.
    A new caller-owned run cannot publish or reopen a successor without its
    independently reopened Linux reader result.
    """
    source_store = Path(source_store)
    source_path = source_store/'completion-caller-source.json'
    if not os.path.lexists(source_path):
        return
    protected(source_path)
    frozen, frozen_raw = _read(source_path)
    reader_path = source_store/'completion-reader-result.json'
    require(os.path.lexists(reader_path), 'StageCompletionCallerReaderMissing')
    protected(reader_path)
    reader, _ = _read(reader_path)
    require(set(frozen) == {'Version', 'Scope', 'Stage', 'RequestSha256',
                'Files', 'ReaderRuntimeSha256'} and
            frozen['Version'] == 1 and
            frozen['Scope'] == 'FrozenLabStageCallerSourceV1' and
            frozen['Stage'] == stage and
            type(frozen['RequestSha256']) is str and
            HEX.fullmatch(frozen['RequestSha256']) and
            type(frozen['ReaderRuntimeSha256']) is str and
            HEX.fullmatch(frozen['ReaderRuntimeSha256']) and
            type(frozen['Files']) is dict and
            all(type(path) is str and type(value) is str and HEX.fullmatch(value)
                for path, value in frozen['Files'].items()),
            'StageCompletionCallerSourceChanged')
    harness = [value for path, value in frozen['Files'].items()
               if path.endswith('/CanonicalImportQualification.dll')]
    require(len(harness) == 1 and
            set(reader) == {'Version', 'Scope', 'Stage', 'AnchorSha256',
                'ControllerSuccessSha256', 'SourceManifestSha256',
                'RuntimeSha256', 'HarnessSha256', 'ReaderInput',
                'ReaderResult', 'EffectAuthorization'} and
            reader['Version'] == 1 and
            reader['Scope'] == 'LabStageReadOnlyLinuxReaderV1' and
            reader['Stage'] == stage and
            reader['AnchorSha256'] == digest(anchor_raw) and
            reader['ControllerSuccessSha256'] == digest(success_raw) and
            reader['SourceManifestSha256'] == digest(frozen_raw) and
            reader['RuntimeSha256'] == frozen['ReaderRuntimeSha256'] and
            reader['HarnessSha256'] == harness[0] and
            reader['ReaderInput'] == str(source_store/'completion-reader-input') and
            reader['EffectAuthorization'] is False and
            type(reader['ReaderResult']) is dict and
            set(reader['ReaderResult']) == {'Scope', 'ResultSha256',
                'CloseSha256', 'PlanSha256', 'EffectAuthorization'} and
            reader['ReaderResult']['Scope'] == (
                'ReadOnlyConfiguredPredecessor' if stage == 'configuration'
                else 'ReadOnlyInitramfsPredecessor') and
            reader['ReaderResult']['ResultSha256'] == anchor['ResultSha256'] and
            reader['ReaderResult']['CloseSha256'] == anchor['CloseSha256'] and
            reader['ReaderResult']['PlanSha256'] == anchor['PlanSha256'] and
            reader['ReaderResult']['EffectAuthorization'] is False,
            'StageCompletionCallerReaderChanged')
    failure_path = source_store/'completion-caller-failure.json'
    if os.path.lexists(failure_path):
        protected(failure_path)
        failure, _ = _read(failure_path)
        require(not publishing and failure.get('Boundary') == 'Publication',
                'StageCompletionCallerFailurePresent')


def _validate_seal(seal, anchor, anchor_raw, capture_raw,
                   disposition_raw, stage):
    require(set(seal) == {'Version', 'Scope', 'Stage', 'RunId', 'GenerationId',
                'OperationId', 'SessionId', 'ResultSha256', 'CloseSha256',
                'AnchorSha256', 'CaptureSha256', 'WriterDispositionSha256',
                'RetentionSha256', 'TargetSha256', 'JournalSha256', 'Status'} and
            seal['Version'] == 1 and
            seal['Scope'] == 'ProtectedStageRetentionSealV1' and
            seal['Status'] == 'FinalizedAfterIndependentReopen' and
            seal['Stage'] == stage and
            seal['RunId'] == anchor['SourceRunId'] and
            all(seal[key] == anchor[key] for key in
                ('GenerationId', 'OperationId', 'SessionId', 'ResultSha256',
                 'CloseSha256', 'RetentionSha256', 'TargetSha256',
                 'JournalSha256')) and
            seal['AnchorSha256'] == digest(anchor_raw) and
            seal['CaptureSha256'] == digest(capture_raw) and
            seal['WriterDispositionSha256'] == digest(disposition_raw),
            'StageCompletionSealChanged')


def _validate_controller_success(success, stage, anchor, anchor_raw, seal_raw,
                                 capture_raw, source_raw, launch_raw,
                                 process_raw, disposition_raw):
    require(set(success) == {'Version', 'Scope', 'Stage', 'RunId',
                'GenerationId', 'OperationId', 'SessionId', 'ResultSha256',
                'CloseSha256', 'AnchorSha256', 'SealSha256', 'CaptureSha256',
                'SourceAuthorizationSha256', 'LaunchWitnessSha256',
                'ProcessWitnessSha256', 'WriterDispositionSha256', 'Status'} and
            success['Version'] == 1 and
            success['Scope'] == 'ProtectedStageControllerSuccessV1' and
            success['Status'] == 'CompletedAfterProtectedSealReopen' and
            success['Stage'] == stage and
            success['RunId'] == anchor['SourceRunId'] and
            all(success[key] == anchor[key] for key in
                ('GenerationId', 'OperationId', 'SessionId', 'ResultSha256',
                 'CloseSha256')) and
            success['AnchorSha256'] == digest(anchor_raw) and
            success['SealSha256'] == digest(seal_raw) and
            success['CaptureSha256'] == digest(capture_raw) and
            success['SourceAuthorizationSha256'] == digest(source_raw) and
            success['LaunchWitnessSha256'] == digest(launch_raw) and
            success['ProcessWitnessSha256'] == digest(process_raw) and
            success['WriterDispositionSha256'] == digest(disposition_raw),
            'StageCompletionControllerSuccessChanged')


def _read_controller_success(source_store):
    try:
        return _read(Path(source_store)/'completion-controller-success.json')
    except (OSError, ValueError) as error:
        raise ValueError('StageCompletionControllerSuccessUnavailable') from error


def _inventory(raw, stage):
    """Acquire bounded exact bytes before any result or terminal readback."""
    files, directory_names = snapshot_tree(raw, max_bytes=MAX_BYTES,
                                            max_file=16 * 1024 * 1024)
    dirs = set(directory_names)
    require(all(PATH.fullmatch(name) is not None and
                all(part not in ('.', '..') for part in name.split('/'))
                for name in files), 'StageCompletionPathChanged')
    required = {
        'input/source-authorization.json',
        f'guest-evidence/execution/{stage}.stdout',
        f'guest-evidence/execution/{stage}-exit.json',
        'guest-evidence/execution/host-binding.json',
        'guest-evidence/execution/capture-journal-mount.json',
        'guest-evidence/execution/independent-handoff.json',
        'guest-evidence/execution/preserved-before.json',
        'guest-evidence/execution/preserved-after.json',
        'guest-evidence/execution/provisioning-journal-unmount.json',
        STAGES[stage]['retention'],
        f'{stage}-independent-host-readback.json',
    }
    require(required.issubset(files) and
            any(n.startswith(STAGES[stage]['predecessor']) for n in files) and
            any(n.startswith('guest-evidence/journals/session/') for n in files) and
            any(n.startswith('guest-evidence/journals/effects/') for n in files),
            'StageCompletionRequiredRecordMissing')
    fixed = required | {
        'guest-evidence/execution/inventory.json',
        'guest-evidence/execution/continuation.json' if stage == 'configuration' else
        'guest-evidence/execution/declaration.json',
        'guest-evidence/execution/continuation-inventory.json' if stage == 'configuration' else
        'guest-evidence/execution/candidate-plan.json',
    }
    branches = (STAGES[stage]['predecessor'], 'guest-evidence/journals/')
    require(all(name in fixed or any(name.startswith(branch) for branch in branches)
                for name in files), 'StageCompletionUnexpectedRecord')
    expected_dirs = {('/'.join(name.split('/')[:index])) for name in files
                     for index in range(1, len(name.split('/')))}
    require(dirs == expected_dirs and len(files) <= MAX_FILES,
            'StageCompletionDirectoryChanged')
    return files


def validate_journal_mount(raw):
    record = json.loads(raw, object_pairs_hook=_unique)
    require(set(record) == {'MountId', 'MajorMinor', 'Root', 'MountPoint',
                'FileSystem', 'Source', 'MountOptions', 'PrivatePropagation',
                'ExactNoChildOrStackedMount'} and
            type(record['MountId']) is int and record['MountId'] > 0 and
            type(record['MajorMinor']) is str and
            re.fullmatch(r'[0-9]+:[0-9]+', record['MajorMinor']) and
            record['Root'] == '/' and record['MountPoint'] == '/lab-journal' and
            record['FileSystem'] == 'ext4' and record['Source'] == '/dev/vdc1' and
            type(record['MountOptions']) is list and
            len(record['MountOptions']) == len(set(record['MountOptions'])) and
            {'rw', 'nodev', 'nosuid', 'noexec'}.issubset(record['MountOptions']) and
            'ro' not in record['MountOptions'] and
            record['PrivatePropagation'] is True and
            record['ExactNoChildOrStackedMount'] is True,
            'StageCompletionJournalMountChanged')
    return record


def verify(handoff, next_authorization, stage):
    """Read-only, strict terminal-to-successor join; never grants effects alone."""
    require(stage in STAGES, 'StageCompletionKindUnsupported')
    spec = STAGES[stage]
    handoff, next_authorization = Path(handoff), Path(next_authorization)
    require(handoff.parent == COMPLETION_ROOT/stage and
            handoff.name.endswith('-published') and
            next_authorization.name == 'authorization.json' and
            next_authorization.parent.parent == NEXT_AUTH_ROOTS[stage],
            'StageCompletionPathUnnominated')
    protected(handoff, file=False)
    _require_committed_seal(handoff)
    next_auth, next_auth_raw = _read(next_authorization)
    anchor, anchor_raw = _read(handoff/'anchor.json')
    seal, seal_raw = _read(handoff/'completion-seal.json')
    result, _ = _read(handoff/'result.json')
    capture, capture_raw = _read(handoff/'capture.json')
    packet_raw = exact_file(handoff/'packet.bin', limit=24 * 1024 * 1024)
    require(0 < len(packet_raw) <= 24 * 1024 * 1024,
            'StageCompletionPacketBound')
    title = stage.title()
    require(set(anchor) == {'Version', 'Scope', 'Stage', 'SourceRunId', 'GenerationId',
            'OperationId', 'SessionId', 'PlanSha256', 'ResultSha256', 'CloseSha256',
            'RetentionSha256', 'TargetSha256', 'JournalSha256', 'CaptureSha256', 'Files'} and
            anchor['Version'] == 1 and anchor['Scope'] == f'TrustedLive{title}TerminalV1' and
            anchor['Stage'] == stage and type(anchor['Files']) is dict and
            all(type(anchor[k]) is str and HEX.fullmatch(anchor[k]) for k in
                ('PlanSha256', 'ResultSha256', 'CloseSha256', 'RetentionSha256',
                 'TargetSha256', 'JournalSha256', 'CaptureSha256')),
            'StageCompletionAnchorChanged')
    require(set(next_auth) == ({'Version', 'Scope', 'AttemptId', 'OperationId',
            'GenerationId', spec['next_result'], 'CloseSha256', 'RetentionSha256',
            'TargetSha256', 'JournalSha256'} |
            ({'TransferSha256'} if stage == 'initramfs' else set())) and
            next_auth['Version'] == 1 and next_auth['Scope'] == spec['next_scope'] and
            all(_uuid(next_auth[key]) for key in
                ('AttemptId', 'OperationId', 'GenerationId')) and
            next_auth['AttemptId'] != anchor['SourceRunId'] and
            next_auth['OperationId'] != anchor['OperationId'] and
            next_auth['GenerationId'] == anchor['GenerationId'] and
            next_auth[spec['next_result']] == anchor['ResultSha256'] and
            all(next_auth[k] == anchor[k] for k in
                ('CloseSha256', 'RetentionSha256', 'TargetSha256', 'JournalSha256')) and
            (stage != 'initramfs' or
             type(next_auth['TransferSha256']) is str and
             HEX.fullmatch(next_auth['TransferSha256'])),
            'StageCompletionSuccessorBindingChanged')
    require(handoff.name == anchor['SourceRunId'].replace('-', '')+'-published' and
            next_authorization.parent.name == next_auth['AttemptId'],
            'StageCompletionNamedIdentityChanged')
    require(set(capture) == {'Version', 'Scope', 'Stage', 'Challenge', 'RunId', 'GenerationId',
            'OperationId', 'SessionId', 'PlanSha256', 'ResultSha256', 'CloseSha256',
            'PacketSha256', 'LaunchWitnessSha256', 'ProcessWitnessSha256',
            'SourceAuthorizationSha256', 'Files', 'Status'} and
            capture['Version'] == 1 and capture['Scope'] == f'PendingControlled{title}CaptureV1' and
            capture['Stage'] == stage and capture['Status'] == 'PendingBackingReadback' and
            all(capture[k] == anchor[k] for k in
                ('GenerationId', 'OperationId', 'SessionId', 'PlanSha256',
                 'ResultSha256', 'CloseSha256')) and
            capture['RunId'] == anchor['SourceRunId'] and
            type(capture['Challenge']) is str and HEX.fullmatch(capture['Challenge']) and
            capture['PacketSha256'] == digest(packet_raw) and
            digest(capture_raw) == anchor['CaptureSha256'] and
            all(type(capture[k]) is str and HEX.fullmatch(capture[k]) for k in
                ('LaunchWitnessSha256', 'ProcessWitnessSha256',
                 'SourceAuthorizationSha256')),
            'StageCompletionCaptureChanged')
    require(set(result) == {'Version', 'Scope', 'Stage', 'AnchorSha256',
            'SealSha256', 'ControllerSuccessSha256',
            'AuthorizationSha256', 'Files', 'Outcome'} and
            result['Version'] == 1 and result['Scope'] == f'Protected{title}CompletionV1' and
            result['Stage'] == stage and result['Outcome'] == 'AppliedAndVerified' and
            result['AnchorSha256'] == digest(anchor_raw) and
            result['SealSha256'] == digest(seal_raw) and
            result['AuthorizationSha256'] == digest(next_auth_raw),
            'StageCompletionPublicationChanged')
    raw = handoff/'raw'
    paths = _inventory(raw, stage)
    validate_journal_mount(paths['guest-evidence/execution/capture-journal-mount.json'])
    require(set(paths) == set(anchor['Files']) == set(result['Files']) and
            set(capture['Files']).issubset(paths), 'StageCompletionInventoryChanged')
    total = 0
    for name, data in paths.items():
        total += len(data)
        require(total <= MAX_BYTES and
                digest(data) == anchor['Files'][name] == result['Files'][name] and
                (name not in capture['Files'] or digest(data) == capture['Files'][name]),
                'StageCompletionRawBytesChanged')
    host_added = {spec['retention'], f'{stage}-independent-host-readback.json'}
    require(set(capture['Files']) == set(paths) - host_added,
            'StageCompletionCaptureInventoryChanged')
    packet = json.loads(packet_raw, object_pairs_hook=_unique)
    require(set(packet) == {'Version', 'Scope', 'Stage', 'Challenge', 'RunId',
            'GenerationId', 'OperationId', 'SessionId', 'PlanSha256',
            'ResultSha256', 'CloseSha256', 'HostBindingSha256', 'Files'} and
            packet['Version'] == 1 and packet['Scope'] == f'ControlledGuest{title}CaptureV1' and
            packet['Stage'] == stage and packet['Challenge'] == capture['Challenge'] and
            packet['RunId'] == anchor['SourceRunId'] and
            all(packet[k] == anchor[k] for k in
                ('GenerationId', 'OperationId', 'SessionId', 'PlanSha256',
                 'ResultSha256', 'CloseSha256')) and
            type(packet['Files']) is list and len(packet['Files']) == len(capture['Files']) and
            packet['HostBindingSha256'] ==
                digest(paths['guest-evidence/execution/host-binding.json']),
            'StageCompletionPacketChanged')
    packet_files = {}
    packet_total = 0
    for item in packet['Files']:
        require(type(item) is dict and set(item) == {'Path', 'Length', 'Sha256', 'Bytes'} and
                type(item['Path']) is str and item['Path'] in capture['Files'] and
                item['Path'] not in packet_files and type(item['Length']) is int and
                0 <= item['Length'] <= 16 * 1024 * 1024 and
                type(item['Sha256']) is str and HEX.fullmatch(item['Sha256']) and
                type(item['Bytes']) is str, 'StageCompletionPacketInventoryChanged')
        data = base64.b64decode(item['Bytes'], validate=True)
        packet_total += len(data)
        require(packet_total <= 16 * 1024 * 1024 and
                base64.b64encode(data).decode('ascii') == item['Bytes'] and
                len(data) == item['Length'] and digest(data) == item['Sha256'] ==
                capture['Files'][item['Path']] and data == paths[item['Path']],
                'StageCompletionPacketBytesChanged')
        packet_files[item['Path']] = data
    require(set(packet_files) == set(capture['Files']),
            'StageCompletionPacketInventoryChanged')
    source_auth, source_auth_raw = _captured_record(paths,
        'input/source-authorization.json')
    require(source_auth['Version'] == 1 and source_auth['Scope'] == spec['source_scope'] and
            source_auth['AttemptId'] == anchor['SourceRunId'] and
            source_auth['OperationId'] == anchor['OperationId'] and
            source_auth['GenerationId'] == anchor['GenerationId'] and
            digest(source_auth_raw) == capture['SourceAuthorizationSha256'],
            'StageCompletionSourceAuthorizationChanged')
    source_store = SOURCE_AUTH_ROOTS[stage]/anchor['SourceRunId']
    _require_no_failure_disposition(source_store)
    source_record, source_record_raw = _read(source_store/'authorization.json')
    launch, launch_raw = _read(source_store/'completion-launch.json')
    _guest_import_witness(source_store, launch, stage)
    process, process_raw = _read(source_store/'completion-process.json')
    manifest, manifest_raw = _read(source_store/'completion-tools.json')
    disposition, disposition_raw = _read(
        source_store/'completion-writer-disposition.json')
    success, success_raw = _read_controller_success(source_store)
    _validate_seal(seal, anchor, anchor_raw, capture_raw,
                   disposition_raw, stage)
    _validate_controller_success(success, stage, anchor, anchor_raw,
                                 seal_raw, capture_raw, source_auth_raw,
                                 launch_raw, process_raw, disposition_raw)
    _require_caller_reader(source_store, stage, anchor, anchor_raw, success_raw)
    require(result['ControllerSuccessSha256'] == digest(success_raw),
            'StageCompletionControllerSuccessChanged')
    pending = COMPLETION_ROOT/stage/anchor['SourceRunId'].replace('-', '')
    try:
        protected(pending, file=False)
    except (OSError, ValueError) as error:
        raise ValueError('StageCompletionPendingCaptureUnavailable') from error
    _require_committed_seal(pending)
    try:
        pending_bytes = {name: exact_file(pending/name,
                         limit=24 * 1024 * 1024 if name == 'packet.bin' else 4 * 1024 * 1024)
                         for name in ('anchor.json', 'capture.json',
                                      'completion-seal.json', 'packet.bin')}
    except (OSError, ValueError) as error:
        raise ValueError('StageCompletionPendingCaptureUnavailable') from error
    require(source_record == source_auth and source_record_raw == source_auth_raw and
            launch['Scope'] == 'ControlledStageLaunchV1' and
            launch['Stage'] == stage and launch['RunId'] == anchor['SourceRunId'] and
            launch['AuthorizationSha256'] == digest(source_auth_raw) and
            launch['ReceiverManifestSha256'] == digest(manifest_raw) and
            manifest['Scope'] == 'ProtectedStageAcquisitionToolsV1' and
            manifest['Stage'] == stage and
            process['Scope'] == 'ControlledStageProcessV1' and
            process['Stage'] == stage and
            process['LaunchWitnessSha256'] == digest(launch_raw) and
            digest(launch_raw) == capture['LaunchWitnessSha256'] and
            digest(process_raw) == capture['ProcessWitnessSha256'] and
            set(disposition) == {'Version', 'Scope', 'Stage', 'RunId', 'Pid',
                'StartTime', 'LaunchWitnessSha256', 'ProcessWitnessSha256',
                'CaptureSha256', 'PoweroffRequested', 'ProcessExitedViaPidfd',
                'WriterExcluded', 'GuestShutdownVerified', 'CanonicalCloseClaimed',
                'AttachmentObservation'} and
            disposition['Version'] == 1 and
            disposition['Scope'] == 'ControlledStageWriterDispositionV1' and
            disposition['Stage'] == stage and
            disposition['RunId'] == anchor['SourceRunId'] and
            disposition['Pid'] == process['Pid'] and
            disposition['StartTime'] == process['StartTime'] and
            disposition['LaunchWitnessSha256'] == digest(launch_raw) and
            disposition['ProcessWitnessSha256'] == digest(process_raw) and
            disposition['CaptureSha256'] == digest(capture_raw) and
            disposition['PoweroffRequested'] is True and
            disposition['ProcessExitedViaPidfd'] is True and
            disposition['WriterExcluded'] is True and
            disposition['AttachmentObservation'] == {'FdWritersAbsent': True,
                'LoopAttachmentsAbsent': True, 'NbdAttachmentsAbsent': True,
                'HostMountReferencesAbsent': True} and
            disposition['GuestShutdownVerified'] is False and
            disposition['CanonicalCloseClaimed'] is False and
            pending_bytes['anchor.json'] == anchor_raw and
            pending_bytes['capture.json'] == capture_raw and
            pending_bytes['completion-seal.json'] == seal_raw and
            pending_bytes['packet.bin'] == packet_raw,
            'StageCompletionLiveAcquisitionChanged')
    retained, retained_raw = _captured_record(paths, spec['retention'])
    require(retained['RunId'] == anchor['SourceRunId'] and
            retained['OperationId'] == anchor['OperationId'] and
            retained['GenerationId'] == anchor['GenerationId'] and
            retained['Outcome'] == retained['CanonicalTeardown'] == 'AppliedAndVerified' and
            digest(retained_raw) == anchor['RetentionSha256'] and
            all(retained['Backings'][name]['Sha256'] == anchor[key] for name, key in
                (('target.raw', 'TargetSha256'), ('journal.raw', 'JournalSha256'))),
            'StageCompletionRetentionChanged')
    host, _ = _captured_record(paths,
        'guest-evidence/execution/host-binding.json')
    require(host['RunId'] == anchor['SourceRunId'] and
            all(any(binding['Serial'] == serial and
                    (binding['HostDevice'], binding['HostInode'], binding['Length']) ==
                    (retained['Backings'][name]['Device'],
                     retained['Backings'][name]['Inode'],
                     retained['Backings'][name]['Length'])
                    for binding in host['ReopenedBackings'])
                for name, serial in (('target.raw', 'IGLOO-LAB-TARGET'),
                                     ('journal.raw', 'IGLOO-LAB-JOURNAL'))),
            'StageCompletionBackingIdentityChanged')
    observed = spec['readback'](raw/'guest-evidence/execution',
                                raw/'guest-evidence/journals', captured=paths)
    stdout_raw = paths[f'guest-evidence/execution/{spec["stdout"]}']
    first = stdout_raw.splitlines()[0]
    require(len(first) <= 4 * 1024 * 1024, 'StageCompletionPreflightOversized')
    preflight = json.loads(first, object_pairs_hook=_unique)
    require(preflight['SessionId'] == anchor['SessionId'] and
            preflight['PlanSha256'] == anchor['PlanSha256'] and
            preflight['Plan']['OperationId'] == anchor['OperationId'] and
            preflight['Plan']['Provenance']['GenerationId'] == anchor['GenerationId'],
            'StageCompletionPreflightChanged')
    exit_record, _ = _captured_record(paths,
        f'guest-evidence/execution/{stage}-exit.json')
    require(exit_record['ExitCode'] == 0 and
            exit_record['StdoutSha256'] == digest(stdout_raw),
            'StageCompletionExitChanged')
    require(observed[spec['status']] == observed['Teardown'] == 'AppliedAndVerified' and
            observed['EffectRecords'][-1] == retained[spec['result']] and
            observed['SessionRecords'][-1] == retained['CloseResult'] and
            observed['EffectRecords'][-1]['Sha256'] == anchor['ResultSha256'] and
            observed['SessionRecords'][-1]['Sha256'] == anchor['CloseSha256'] and
            observed['PlanSha256'] == anchor['PlanSha256'] and
            observed['SessionId'] == anchor['SessionId'] and
            digest(paths['guest-evidence/execution/independent-handoff.json']) ==
                retained['IndependentHandoffSha256'],
            'StageCompletionTerminalChanged')
    handoff_result, _ = _captured_record(paths,
        'guest-evidence/execution/independent-handoff.json')
    independent, _ = _captured_record(paths,
        f'{stage}-independent-host-readback.json')
    require(handoff_result == observed and independent['Verified'] == observed and
            independent['CaptureSha256'] == digest(capture_raw) and
            independent['WriterExcluded'] is True and
            _inventory(raw, stage) == paths,
            'StageCompletionIndependentReadbackChanged')
    return {'Raw': raw, 'Stage': stage, 'SourceRunId': anchor['SourceRunId'],
            'GenerationId': anchor['GenerationId'], 'OperationId': anchor['OperationId'],
            'ResultSha256': anchor['ResultSha256'], 'CloseSha256': anchor['CloseSha256'],
            'TargetSha256': anchor['TargetSha256'], 'JournalSha256': anchor['JournalSha256'],
            'AnchorSha256': digest(anchor_raw), 'EffectAuthorization': False}


def publish(pending, next_authorization, destination, stage):
    """Create the successor's protected handoff from a finalized live capture.

    The expected terminal hashes are outputs of the capture. The separate
    next-stage authorization may only bind those outputs; it cannot mint them.
    """
    require(stage in STAGES, 'StageCompletionKindUnsupported')
    pending, next_authorization, destination = map(Path,
        (pending, next_authorization, destination))
    require(pending.parent == COMPLETION_ROOT/stage and
            destination == pending.with_name(pending.name+'-published') and
            next_authorization.name == 'authorization.json' and
            next_authorization.parent.parent == NEXT_AUTH_ROOTS[stage] and
            not destination.exists(), 'StageCompletionPublicationPathChanged')
    protected(pending, file=False)
    _require_committed_seal(pending)
    protected(destination.parent, file=False)
    next_auth, next_raw = _read(next_authorization)
    anchor, anchor_raw = _read(pending/'anchor.json')
    seal, seal_raw = _read(pending/'completion-seal.json')
    capture, capture_raw = _read(pending/'capture.json')
    source_auth, source_raw = _read(pending/'raw/input/source-authorization.json')
    title = stage.title()
    require(anchor['Scope'] == f'TrustedLive{title}TerminalV1' and
            anchor['Stage'] == capture['Stage'] == stage and
            pending.name == anchor['SourceRunId'].replace('-', '') and
            capture['RunId'] == anchor['SourceRunId'] and
            digest(capture_raw) == anchor['CaptureSha256'] and
            digest(source_raw) == capture['SourceAuthorizationSha256'] and
            source_auth['Scope'] == STAGES[stage]['source_scope'] and
            next_auth['Scope'] == STAGES[stage]['next_scope'] and
            next_auth['GenerationId'] == anchor['GenerationId'] and
            next_auth[STAGES[stage]['next_result']] == anchor['ResultSha256'] and
            all(next_auth[key] == anchor[key] for key in
                ('CloseSha256', 'RetentionSha256', 'TargetSha256', 'JournalSha256')),
            'StageCompletionPublicationBindingChanged')
    require(set(next_auth) == ({'Version', 'Scope', 'AttemptId', 'OperationId',
                'GenerationId', STAGES[stage]['next_result'], 'CloseSha256',
                'RetentionSha256', 'TargetSha256', 'JournalSha256'} |
                ({'TransferSha256'} if stage == 'initramfs' else set())) and
            next_auth['Version'] == 1 and
            all(_uuid(next_auth[key]) for key in
                ('AttemptId', 'OperationId', 'GenerationId')) and
            next_authorization.parent.name == next_auth['AttemptId'] and
            next_auth['AttemptId'] != anchor['SourceRunId'] and
            next_auth['OperationId'] != anchor['OperationId'] and
            (stage != 'initramfs' or
             type(next_auth['TransferSha256']) is str and
             HEX.fullmatch(next_auth['TransferSha256'])),
            'StageCompletionNextAuthorizationChanged')
    # The capture's root-held launch/process witnesses live outside the
    # QEMU-owned export. A copied chain with no controlled acquisition fails.
    source_store = SOURCE_AUTH_ROOTS[stage]/anchor['SourceRunId']
    _require_no_failure_disposition(source_store)
    launch, launch_raw = _read(source_store/'completion-launch.json')
    _guest_import_witness(source_store, launch, stage)
    process, process_raw = _read(source_store/'completion-process.json')
    _, manifest_raw = _read(source_store/'completion-tools.json')
    disposition, disposition_raw = _read(
        source_store/'completion-writer-disposition.json')
    source_record, source_record_raw = _read(source_store/'authorization.json')
    success, success_raw = _read_controller_success(source_store)
    _validate_seal(seal, anchor, anchor_raw, capture_raw,
                   disposition_raw, stage)
    _validate_controller_success(success, stage, anchor, anchor_raw,
                                 seal_raw, capture_raw, source_raw,
                                 launch_raw, process_raw, disposition_raw)
    _require_caller_reader(source_store, stage, anchor, anchor_raw,
                           success_raw, publishing=True)
    require(digest(launch_raw) == capture['LaunchWitnessSha256'] and
            source_record == source_auth and source_record_raw == source_raw and
            digest(process_raw) == capture['ProcessWitnessSha256'] and
            launch['AuthorizationSha256'] == digest(source_raw) and
            launch['RunId'] == anchor['SourceRunId'] and
            process['LaunchWitnessSha256'] == digest(launch_raw) and
            launch['ReceiverManifestSha256'] == digest(manifest_raw) and
            disposition['Scope'] == 'ControlledStageWriterDispositionV1' and
            disposition['Stage'] == stage and
            disposition['RunId'] == anchor['SourceRunId'] and
            disposition['LaunchWitnessSha256'] == digest(launch_raw) and
            disposition['ProcessWitnessSha256'] == digest(process_raw) and
            disposition['CaptureSha256'] == digest(capture_raw) and
            disposition['ProcessExitedViaPidfd'] is True and
            disposition['WriterExcluded'] is True and
            disposition['AttachmentObservation'] == {'FdWritersAbsent': True,
                'LoopAttachmentsAbsent': True, 'NbdAttachmentsAbsent': True,
                'HostMountReferencesAbsent': True},
            'StageCompletionAcquisitionWitnessChanged')
    raw = pending/'raw'
    paths = _inventory(raw, stage)
    validate_journal_mount(paths['guest-evidence/execution/capture-journal-mount.json'])
    require(set(paths) == set(anchor['Files']) and
            set(capture['Files']) == set(paths)-{
                STAGES[stage]['retention'],
                f'{stage}-independent-host-readback.json'},
            'StageCompletionPublicationInventoryChanged')
    total = 0
    for name, data in paths.items():
        total += len(data)
        require(total <= MAX_BYTES and digest(data) == anchor['Files'][name] and
                (name not in capture['Files'] or digest(data) == capture['Files'][name]),
                'StageCompletionPublicationBytesChanged')
    observed = STAGES[stage]['readback'](raw/'guest-evidence/execution',
                                          raw/'guest-evidence/journals', captured=paths)
    require(observed[title] == observed['Teardown'] == 'AppliedAndVerified' and
            observed['EffectRecords'][-1]['Sha256'] == anchor['ResultSha256'] and
            observed['SessionRecords'][-1]['Sha256'] == anchor['CloseSha256'] and
            _inventory(raw, stage) == paths,
            'StageCompletionPublicationTerminalChanged')
    _require_no_failure_disposition(source_store)
    _require_committed_seal(pending)
    destination.mkdir(mode=0o700)
    target_root = destination/'raw'; target_root.mkdir(mode=0o700)
    for name, data in sorted(paths.items()):
        target = target_root/name
        target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'wb') as output:
            output.write(data); output.flush(); os.fsync(output.fileno())
    for name in ('anchor.json', 'capture.json', 'completion-seal.json',
                 'packet.bin'):
        data = exact_file(pending/name,
                          limit=24 * 1024 * 1024 if name == 'packet.bin' else 4 * 1024 * 1024)
        fd = os.open(destination/name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'wb') as output:
            output.write(data); output.flush(); os.fsync(output.fileno())
    for directory in sorted((p for p in destination.rglob('*') if p.is_dir()),
                            key=lambda item: len(item.parts), reverse=True):
        fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    for directory in (destination, destination.parent):
        fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    # Result publication is last. A partial directory is never admissible.
    _require_no_failure_disposition(source_store)
    _require_committed_seal(pending)
    require(exact_file(source_store/'completion-controller-success.json') ==
            success_raw, 'StageCompletionControllerSuccessChanged')
    from canonical_lab_runtime import checkpoint
    checkpoint(destination, 'result.json', {'Version': 1,
        'Scope': f'Protected{title}CompletionV1', 'Stage': stage,
        'AnchorSha256': digest(anchor_raw),
        'SealSha256': digest(seal_raw),
        'ControllerSuccessSha256': digest(success_raw),
        'AuthorizationSha256': digest(next_raw),
        'Files': anchor['Files'], 'Outcome': 'AppliedAndVerified'})
    return verify(destination, next_authorization, stage)
