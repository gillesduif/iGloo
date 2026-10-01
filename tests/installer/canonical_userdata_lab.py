"""Bounded UserData derivative, using the existing independent-copy authority.

Copy equality precedes payload staging. The later delivery record binds the
changed working backing; it never changes the meaning of the copy receipt.
"""
import json
import os
from pathlib import Path
import shutil

from canonical_configuration_derivation import closed, copy_verified, identity, require
from canonical_initramfs_readback import verify
from canonical_lab_capacity import require_backing_capacity
from canonical_lab_runtime import checkpoint
from canonical_storage_lab import sha

RESERVE = 180 * 1024**3


def derive(authorization_path, predecessor, run):
    authorization_path, predecessor, run = map(Path, (authorization_path, predecessor, run))
    for p in (authorization_path, *authorization_path.parents):
        s = p.lstat(); require(not p.is_symlink() and s.st_uid == 0 and not s.st_mode & 0o022, 'UserDataAuthorizationProtection')
    a = json.loads(authorization_path.read_bytes())
    require(a['Version'] == 1 and a['Scope'] == 'OneInitramfsCheckpointSelectedDocuments' and
            run.name == a['AttemptId'].replace('-', ''), 'UserDataDerivationScope')
    retention = predecessor/'initramfs-retention.json'; require(sha(retention) == a['RetentionSha256'], 'UserDataRetentionChanged')
    kept = json.loads(retention.read_bytes()); previous = verify(predecessor/'guest-evidence/execution', predecessor/'guest-evidence/journals')
    require(previous['Initramfs'] == previous['Teardown'] == kept['Outcome'] == kept['CanonicalTeardown'] == 'AppliedAndVerified' and
        previous['EffectRecords'][-1] == kept['InitramfsResult'] and previous['SessionRecords'][-1] == kept['CloseResult'] and
        a['InitramfsSha256'] == kept['InitramfsResult']['Sha256'] and a['CloseSha256'] == kept['CloseResult']['Sha256'] and
        a['GenerationId'] == kept['GenerationId'] and a['AttemptId'] != kept['RunId'] and a['OperationId'] != kept['OperationId'],
        'UserDataPredecessorChanged')
    paths = [predecessor/n for n in kept['Backings']]; closed(paths)
    require_backing_capacity(RESERVE)
    space = os.statvfs(run); require(space.f_bavail*space.f_frsize >= RESERVE, 'UserDataGuestCapacity')
    for name, binding in kept['Backings'].items():
        p = predecessor/name
        require(identity(p) == (binding['Device'], binding['Inode'], binding['Length']) and
            p.stat().st_nlink == binding['Links'] == 1 and sha(p) == binding['Sha256'], 'UserDataOriginalBackingChanged')
    checkpoint(authorization_path.parent, 'reservation.json', {'Scope': a['Scope'], 'AuthorizationSha256': sha(authorization_path),
        'OperationId': a['OperationId'], 'AttemptId': a['AttemptId'], 'InitramfsSha256': a['InitramfsSha256']})
    checkpoint(run, 'userdata-copy-intent.json', {'AuthorizationSha256': sha(authorization_path),
        'Sources': kept['Backings'], 'RetentionSha256': sha(retention), 'RequiredBytesIncludingReserve': RESERVE})
    copies = []
    for name, serial, key in (('target.raw', 'IGLOO-LAB-TARGET', 'TargetSha256'), ('journal.raw', 'IGLOO-LAB-JOURNAL', 'JournalSha256')):
        require(kept['Backings'][name]['Sha256'] == a[key], 'UserDataCopyAncestry')
        before, after = copy_verified(predecessor/name, run/name, a[key])
        copies.append({'Serial': serial, 'SourceDevice': before[0], 'SourceInode': before[1], 'DestinationDevice': after[0],
            'DestinationInode': after[1], 'Length': after[2], 'SourceSha256': a[key], 'ReopenedSha256': sha(run/name)})
    checkpoint(run, 'userdata-initial-copy.json', {'Authorization': a, 'AuthorizationSha256': sha(authorization_path), 'Copies': copies})


def finalize_staging(run, guest_delivery):
    run, guest_delivery = Path(run), Path(guest_delivery)
    copied = json.loads((run/'userdata-initial-copy.json').read_bytes()); a = copied['Authorization']
    closed([run/'target.raw', run/'journal.raw'])  # staging guest has shut down; no concurrent writer
    for copy, name in zip(copied['Copies'], ('target.raw', 'journal.raw')):
        p = run/name
        require(identity(p) == (copy['DestinationDevice'], copy['DestinationInode'], copy['Length']) and p.stat().st_nlink == 1,
                'UserDataWorkingBackingSubstituted')
    require(sha(run/'journal.raw') == a['JournalSha256'], 'UserDataStagingChangedJournal')
    delivered = json.loads(guest_delivery.read_bytes())
    require(delivered['GenerationId'] == a['GenerationId'] and delivered['OperationId'] == a['OperationId'] and
        delivered['TransferSha256'] == a['TransferSha256'] and
        delivered['PayloadBeforeSha256'] == delivered['UnrelatedPayloadAfterSha256'], 'UserDataDeliveryMismatch')
    staged = sha(run/'target.raw'); require(staged != a['TargetSha256'], 'UserDataStagingNotObserved')
    checkpoint(run, 'userdata-delivery.json', {**delivered, 'TargetCopySha256': a['TargetSha256'], 'StagedTargetSha256': staged})
    checkpoint(run, 'userdata-derivation.json', {**copied, 'DeliverySha256': sha(run/'userdata-delivery.json'), 'StagedTargetSha256': staged})
    checkpoint(run, 'userdata-staging-host-readback.json', {'GuestDeliverySha256': sha(guest_delivery),
        'DeliverySha256': sha(run/'userdata-delivery.json'), 'WorkingTargetSha256': staged, 'NoActiveWriterObserved': True})


def stage_predecessor(predecessor, destination):
    predecessor, destination = Path(predecessor), Path(destination); destination.mkdir()
    shutil.copytree(predecessor/'input/configured-predecessor', destination/'configured')
    for name in ('session', 'effects'): shutil.copytree(predecessor/'guest-evidence/journals'/name, destination/name)
    for name in ('declaration.json', 'inventory.json'): shutil.copyfile(predecessor/'guest-evidence/execution'/name, destination/name)
    shutil.copyfile(predecessor/'input/initramfs-authorization.json', destination/'initramfs-authorization.json')
    preflight = json.loads((predecessor/'guest-evidence/execution/initramfs.stdout').read_text().splitlines()[0])
    kept = json.loads((predecessor/'initramfs-retention.json').read_bytes())
    checkpoint(destination, 'initramfs-plan.json', preflight['Plan'])
    checkpoint(destination, 'binding.json', {'InitramfsSha256': kept['InitramfsResult']['Sha256'], 'CloseSha256': kept['CloseResult']['Sha256']})
