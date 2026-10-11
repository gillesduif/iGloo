"""Protected, exact-byte UserData completion handoff for a later lab successor.

The anchor is an independent, protected input from the trusted terminal
verifier. This module never derives an anchor from the QEMU-owned export.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import stat

from canonical_configuration_derivation import require
from canonical_lab_runtime import checkpoint
from canonical_userdata_readback import verify as verify_terminal


FILES = (
    'userdata-retention.json', 'userdata-derivation.json',
    'userdata-independent-host-readback.json',
    'input/userdata-authorization.json',
    'guest-evidence/execution/host-binding.json',
    'guest-evidence/execution/inventory.json',
    'guest-evidence/execution/userdata.stdout',
)
LIVE_FILES = tuple('guest-evidence/execution/' + name for name in (
    'userdata-exit.json', 'independent-handoff.json', 'preserved-before.json',
    'preserved-after.json', 'provisioning-journal-unmount.json'))
MAX_FILES = 4096
MAX_BYTES = 128 * 1024 * 1024
HEX = re.compile(r'[0-9A-F]{64}\Z')


def digest(raw):
    return hashlib.sha256(raw).hexdigest().upper()


def protected(path, *, file=True):
    """Check the actual no-follow path, not a resolved replacement."""
    path = Path(path).absolute()
    for part in (path, *path.parents):
        s = part.lstat()
        require(not stat.S_ISLNK(s.st_mode) and s.st_uid == 0 and not s.st_mode & 0o022,
                'CompletionPathUnprotected')
        if part == path:
            require(stat.S_ISREG(s.st_mode) if file else stat.S_ISDIR(s.st_mode),
                    'CompletionPathTypeChanged')
            if file:
                require(s.st_nlink == 1, 'CompletionFileAliased')


def _read_json(path):
    raw = Path(path).read_bytes()
    require(len(raw) <= 4 * 1024 * 1024, 'CompletionDeclarationOversized')
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, 'CompletionDuplicateField')
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=unique), raw


def _files(root):
    root = Path(root)
    selected = list(FILES)
    if (root / LIVE_FILES[0]).exists():
        selected.extend(LIVE_FILES)
    for branch in ('input/initramfs-predecessor', 'guest-evidence/journals'):
        base = root / branch
        require(base.is_dir() and not base.is_symlink(), 'CompletionRecordsMissing')
        for parent, dirs, names in os.walk(base, followlinks=False):
            for name in dirs + names:
                item = Path(parent) / name
                require(not item.is_symlink(), 'CompletionRecordLink')
                if name in names:
                    selected.append(item.relative_to(root).as_posix())
    all_files, all_dirs = [], []
    for parent, dirs, names in os.walk(root, followlinks=False):
        for name in dirs + names:
            item = Path(parent) / name
            require(not item.is_symlink() and item.stat().st_dev == root.stat().st_dev,
                    'CompletionRecordLinkOrMount')
            if name in names:
                all_files.append(item.relative_to(root).as_posix())
            else:
                all_dirs.append(item.relative_to(root).as_posix())
    expected_dirs = {parent.as_posix() for name in selected
                     for parent in Path(name).parents if parent != Path('.')}
    require(len(selected) <= MAX_FILES and set(selected) == set(all_files),
            'CompletionInventoryUnboundedOrUnexpected')
    require(set(all_dirs) == expected_dirs, 'CompletionDirectoryUnexpected')
    return sorted(selected)


def verify(handoff, external_authorization, *, require_live=False):
    """Read-only join of separately protected anchor, raw bytes and chains."""
    handoff, external_authorization = Path(handoff), Path(external_authorization)
    protected(handoff, file=False)
    protected(external_authorization)
    protected(handoff / 'anchor.json')
    protected(handoff / 'result.json')
    anchor, anchor_raw = _read_json(handoff / 'anchor.json')
    result, _ = _read_json(handoff / 'result.json')
    auth, auth_raw = _read_json(external_authorization)
    require(set(auth) == {'Version', 'Scope', 'AttemptId', 'OperationId', 'GenerationId',
                          'UserDataSha256', 'CloseSha256', 'RetentionSha256',
                          'TargetSha256', 'JournalSha256'}, 'CompletionAuthorizationSchema')
    live = anchor.get('Version') == 2
    require(set(anchor) == {'Version', 'Scope', 'SourceRunId', 'GenerationId', 'OperationId',
                            'SessionId', 'ResultSha256', 'CloseSha256', 'RetentionSha256',
                            'TargetSha256', 'JournalSha256', 'Files'} | ({'CaptureSha256'} if live else set()),
            'CompletionAnchorSchema')
    require((anchor['Version'], anchor['Scope']) in ((1, 'TrustedUserDataTerminalV1'),
            (2, 'TrustedLiveUserDataTerminalV2')) and (not require_live or live) and
            auth['Version'] == 1 and auth['Scope'] == 'OneUserDataCheckpointOfflineAgentStage' and
            anchor['GenerationId'] == auth['GenerationId'] and
            anchor['ResultSha256'] == auth['UserDataSha256'] and
            anchor['CloseSha256'] == auth['CloseSha256'] and
            anchor['RetentionSha256'] == auth['RetentionSha256'] and
            anchor['TargetSha256'] == auth['TargetSha256'] and
            anchor['JournalSha256'] == auth['JournalSha256'] and
            anchor['SourceRunId'] != auth['AttemptId'] and anchor['OperationId'] != auth['OperationId'] and
            all(isinstance(anchor[k], str) and HEX.fullmatch(anchor[k]) for k in
                ('ResultSha256', 'CloseSha256', 'RetentionSha256', 'TargetSha256', 'JournalSha256')),
            'CompletionExternalAnchorChanged')
    if live:
        protected(handoff / 'capture.json')
        protected(handoff / 'packet.bin')
        require((handoff / 'packet.bin').stat().st_size <= 24*1024*1024,
                'CompletionCaptureOversized')
        capture, capture_raw = _read_json(handoff / 'capture.json')
        require(digest(capture_raw) == anchor['CaptureSha256'] and
                digest((handoff / 'packet.bin').read_bytes()) == capture['PacketSha256'] and
                capture['Scope'] == 'PendingControlledUserDataCaptureV1' and
                capture['Status'] == 'PendingBackingReadback' and
                capture['RunId'] == anchor['SourceRunId'] and
                capture['GenerationId'] == anchor['GenerationId'] and
                capture['OperationId'] == anchor['OperationId'] and
                capture['SessionId'] == anchor['SessionId'] and
                capture['ResultSha256'] == anchor['ResultSha256'] and
                capture['CloseSha256'] == anchor['CloseSha256'] and
                all((isinstance(capture[k], str) and HEX.fullmatch(capture[k])) for k in
                    ('LaunchWitnessSha256', 'ProcessWitnessSha256', 'PacketSha256')),
                'CompletionCaptureChanged')
    require(set(result) == {'Version', 'Scope', 'AnchorSha256', 'AuthorizationSha256', 'Files', 'Outcome'} and
            result['Version'] == 1 and result['Scope'] == 'ProtectedUserDataCompletionV1' and
            result['Outcome'] == 'AppliedAndVerified' and result['AnchorSha256'] == digest(anchor_raw) and
            result['AuthorizationSha256'] == digest(auth_raw), 'CompletionPublicationChanged')
    raw = handoff / 'raw'
    protected(raw, file=False)
    paths = _files(raw)
    require(set(anchor['Files']) == set(result['Files']) == set(paths), 'CompletionInventoryChanged')
    if live:
        require(set(capture['Files']).issubset(paths) and
                all(anchor['Files'][name] == value for name, value in capture['Files'].items()) and
                set(LIVE_FILES).issubset(capture['Files']), 'CompletionCaptureInventoryChanged')
    total = 0
    for name in paths:
        path = raw / name
        protected(path)
        data = path.read_bytes(); total += len(data)
        require(total <= MAX_BYTES and anchor['Files'][name] == result['Files'][name] == digest(data),
                'CompletionRawBytesChanged')
    retained, _ = _read_json(raw / 'userdata-retention.json')
    observed = verify_terminal(raw / 'guest-evidence/execution', raw / 'guest-evidence/journals')
    from userdata_contract import decode
    preflight = decode((raw / 'guest-evidence/execution/userdata.stdout').read_bytes().splitlines()[0])
    require(observed['UserData'] == observed['Teardown'] == 'AppliedAndVerified' and
            observed['EffectRecords'][-1]['Sha256'] == anchor['ResultSha256'] and
            observed['SessionRecords'][-1]['Sha256'] == anchor['CloseSha256'] and
            observed['SessionId'] == anchor['SessionId'] and
            observed['OperationId'] == anchor['OperationId'] and
            retained['RunId'] == anchor['SourceRunId'] and
            preflight['Plan']['Provenance']['GenerationId'] == anchor['GenerationId'] and
            digest((raw / 'userdata-retention.json').read_bytes()) == anchor['RetentionSha256'] and
            retained['Backings']['target.raw']['Sha256'] == anchor['TargetSha256'] and
            retained['Backings']['journal.raw']['Sha256'] == anchor['JournalSha256'],
            'CompletionTerminalChanged')
    return {'Raw': raw, 'AnchorSha256': digest(anchor_raw), 'AuthorizationSha256': digest(auth_raw),
            'ResultSha256': anchor['ResultSha256'], 'CloseSha256': anchor['CloseSha256'],
            'TargetSha256': anchor['TargetSha256'], 'JournalSha256': anchor['JournalSha256'],
            'SourceRunId': anchor['SourceRunId'], 'GenerationId': anchor['GenerationId']}


def publish_from_protected_verifier(source, independent_anchor, authorization, destination):
    """Future trusted-verifier call site; rejected exports cannot be sources.

    An anchor must already be independently retained. This function cannot
    mint one from a candidate export or assert historical custody by copying.
    """
    source, independent_anchor, authorization, destination = map(Path,
        (source, independent_anchor, authorization, destination))
    protected(source, file=False); protected(independent_anchor); protected(authorization)
    require(destination != source and source not in destination.parents and
            destination not in source.parents and
            source not in independent_anchor.parents and source not in authorization.parents and
            independent_anchor != authorization and
            destination != independent_anchor and destination != authorization,
            'CompletionSourceDestinationAlias')
    anchor, anchor_raw = _read_json(independent_anchor)
    paths = _files(source)
    require(set(paths) == set(anchor['Files']), 'CompletionInventoryChanged')
    total = 0
    for name in paths:
        item = source / name; protected(item)
        data = item.read_bytes(); total += len(data)
        require(total <= MAX_BYTES and anchor['Files'][name] == digest(data), 'CompletionRawBytesChanged')
    # Verify the terminal BEFORE any publication effect.
    observed = verify_terminal(source / 'guest-evidence/execution', source / 'guest-evidence/journals')
    auth, _ = _read_json(authorization)
    retained, _ = _read_json(source / 'userdata-retention.json')
    from userdata_contract import decode
    preflight = decode((source / 'guest-evidence/execution/userdata.stdout').read_bytes().splitlines()[0])
    live = anchor.get('Version') == 2
    require(auth['Version'] == 1 and auth['Scope'] == 'OneUserDataCheckpointOfflineAgentStage' and
            (anchor['Version'], anchor['Scope']) in ((1, 'TrustedUserDataTerminalV1'),
                (2, 'TrustedLiveUserDataTerminalV2')) and
            auth['UserDataSha256'] == anchor['ResultSha256'] and auth['CloseSha256'] == anchor['CloseSha256'] and
            auth['RetentionSha256'] == anchor['RetentionSha256'] and
            auth['TargetSha256'] == anchor['TargetSha256'] and auth['JournalSha256'] == anchor['JournalSha256'] and
            auth['GenerationId'] == anchor['GenerationId'] and
            retained['RunId'] == anchor['SourceRunId'] and
            preflight['Plan']['Provenance']['GenerationId'] == anchor['GenerationId'] and
            observed['SessionId'] == anchor['SessionId'] and observed['OperationId'] == anchor['OperationId'] and
            digest((source / 'userdata-retention.json').read_bytes()) == anchor['RetentionSha256'] and
            retained['Backings']['target.raw']['Sha256'] == anchor['TargetSha256'] and
            retained['Backings']['journal.raw']['Sha256'] == anchor['JournalSha256'],
            'CompletionExternalAnchorChanged')
    if live:
        protected(source.parent / 'capture.json')
        protected(source.parent / 'packet.bin')
        require((source.parent / 'packet.bin').stat().st_size <= 24*1024*1024,
                'CompletionCaptureOversized')
        capture, capture_raw = _read_json(source.parent / 'capture.json')
        require(digest(capture_raw) == anchor['CaptureSha256'] and
                digest((source.parent / 'packet.bin').read_bytes()) == capture['PacketSha256'],
                'CompletionCaptureChanged')
    require(observed['UserData'] == observed['Teardown'] == 'AppliedAndVerified' and
            observed['EffectRecords'][-1]['Sha256'] == anchor['ResultSha256'] and
            observed['SessionRecords'][-1]['Sha256'] == anchor['CloseSha256'], 'CompletionTerminalChanged')
    require(not destination.exists() and destination.parent.is_dir(), 'CompletionDestinationExists')
    protected(destination.parent, file=False)
    destination.mkdir(mode=0o700)
    raw = destination / 'raw'; raw.mkdir(mode=0o700)
    for name in paths:
        target = raw / name; target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        data = (source / name).read_bytes()
        fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'wb') as out:
            out.write(data); out.flush(); os.fsync(out.fileno())
    anchor_target = destination / 'anchor.json'
    fd = os.open(anchor_target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'wb') as out:
        out.write(anchor_raw); out.flush(); os.fsync(out.fileno())
    if live:
        for name in ('capture.json', 'packet.bin'):
            fd = os.open(destination / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, 'wb') as out:
                out.write((source.parent / name).read_bytes()); out.flush(); os.fsync(out.fileno())
    for directory in sorted((p for p in destination.rglob('*') if p.is_dir()),
                            key=lambda p: len(p.parts), reverse=True):
        fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    for directory in (destination, destination.parent):
        fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    checkpoint(destination, 'result.json', {'Version': 1, 'Scope': 'ProtectedUserDataCompletionV1',
        'AnchorSha256': digest(anchor_raw), 'AuthorizationSha256': digest(authorization.read_bytes()),
        'Files': anchor['Files'], 'Outcome': 'AppliedAndVerified'})
    return verify(destination, authorization)
