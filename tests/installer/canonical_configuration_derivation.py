"""One explicitly authorized lab copy experiment. No recovery, format or retry."""
import json
import os
from pathlib import Path
import stat
import subprocess

from canonical_lab_runtime import checkpoint
from canonical_storage_lab import sha
from canonical_lab_capacity import require_backing_capacity


def require(value, code):
    if not value: raise ValueError(code)


def identity(path):
    path = Path(path); info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and path.resolve() == path, 'BackingNotRegular')
    return info.st_dev, info.st_ino, info.st_size


def closed(paths):
    identities = {identity(p)[:2] for p in paths}
    for process in Path('/proc').iterdir():
        if not process.name.isdigit(): continue
        try:
            for fd in (process/'fd').iterdir():
                info = fd.stat()
                require((info.st_dev, info.st_ino) not in identities, 'BackingStillOpen')
        except (FileNotFoundError, ProcessLookupError): pass


def copy_verified(source, destination, expected):
    source, destination = Path(source), Path(destination)
    before = identity(source)
    require_backing_capacity(before[2] + 2 * 1024**3)
    require(not destination.exists() and sha(source) == expected, 'CheckpointBytesChanged')
    subprocess.run(['cp', '--sparse=always', '--reflink=auto', '--no-clobber', str(source), str(destination)], check=True)
    after = identity(destination)
    require(before[:2] != after[:2] and before[2] == after[2] and destination.stat().st_nlink == 1, 'CopyAliasesSource')
    destination.chmod(0o600)
    with destination.open('rb') as stream: os.fsync(stream.fileno())
    require(identity(source) == before and sha(destination) == expected and sha(source) == expected, 'CopyReadbackChanged')
    return before, after


def derive(authorization_path, source, failed, run):
    authorization_path, source, failed, run = map(Path, (authorization_path, source, failed, run))
    require(os.geteuid() == 0, 'ProtectedHostRequired')
    for p in (authorization_path, *authorization_path.parents):
        require(not p.is_symlink() and p.stat().st_uid == 0 and not p.stat().st_mode & 0o022, 'AuthorizationUnprotected')
    authorization = json.loads(authorization_path.read_bytes())
    require(authorization['Version'] == 1 and authorization['Scope'] == 'OneCheckpointDerivedCoreConfiguration' and
            run.name == authorization['AttemptId'].replace('-', ''), 'DerivedScopeMismatch')
    require(sha(source/'result.json') == authorization['CheckpointSha256'], 'CheckpointResultChanged')
    result = json.loads((source/'result.json').read_bytes())
    require(result['Outcome'] == 'AppliedAndVerified' and result['IntentSha256'] == sha(source/'intent.json') and
        result['ImportResult']['Sha256'] == authorization['ImportSha256'] and
        result['CloseResult']['Sha256'] == authorization['CloseSha256'], 'CheckpointLineageChanged')
    paths = [p/n for p in (source, failed) for n in ('target.raw', 'journal.raw')]
    closed(paths)
    space = os.statvfs(run)
    required = sum((source/n).stat().st_size for n in ('target.raw', 'journal.raw')) + 2*1024**3
    require(space.f_bavail * space.f_frsize >= required, 'IndependentCopyCapacityInsufficient')
    # Protected host reservation survives a copied journal. Never delete or resume it.
    checkpoint(authorization_path.parent, 'reservation.json', {'AuthorizationSha256': sha(authorization_path),
        'AttemptId': authorization['AttemptId'], 'OperationId': authorization['OperationId'],
        'FailedOperationId': authorization['FailedOperationId'], 'CheckpointSha256': authorization['CheckpointSha256']})
    checkpoint(run, 'copy-intent.json', {'AuthorizationSha256': sha(authorization_path),
        'AvailableBytes': space.f_bavail * space.f_frsize, 'RequiredBytesIncludingReserve': required,
        'Sources': {n: identity(source/n) for n in ('target.raw','journal.raw')},
        'FailedBackings': {n: identity(failed/n) for n in ('target.raw','journal.raw')}})
    copies = []
    forbidden = {identity(p)[:2] for p in paths}
    for name, serial, key in [('target.raw','IGLOO-LAB-TARGET','TargetSha256'), ('journal.raw','IGLOO-LAB-JOURNAL','JournalSha256')]:
        require((source/name).stat().st_mode & 0o777 == 0o400 and result['Backings'][name]['Sha256'] == authorization[key], 'CheckpointProtectionChanged')
        before, after = copy_verified(source/name, run/name, authorization[key])
        require(after[:2] not in forbidden, 'CopyAliasesRetainedBacking'); forbidden.add(after[:2])
        copies.append({'Serial': serial, 'SourceDevice': before[0], 'SourceInode': before[1],
            'DestinationDevice': after[0], 'DestinationInode': after[1], 'Length': after[2],
            'SourceSha256': authorization[key], 'ReopenedSha256': sha(run/name)})
    value = {'Authorization': authorization, 'AuthorizationSha256': sha(authorization_path),
        'CheckpointSha256': sha(source/'result.json'), 'Copies': copies}
    checkpoint(run, 'configuration-derivation.json', value)
    return value
