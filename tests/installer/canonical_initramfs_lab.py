"""Bounded configured-checkpoint derivative. Uses existing copy/launch authority."""
import json
import os
from pathlib import Path
import shutil

from canonical_configuration_derivation import closed, copy_verified, identity, require
from canonical_configuration_readback import verify
from canonical_lab_runtime import checkpoint, checkpoint_exact_json_member
from canonical_lab_capacity import require_backing_capacity
from canonical_storage_lab import sha
from canonical_stage_completion import verify as verify_completion


def derive(authorization_path, predecessor, completion, run):
    authorization_path, predecessor, run = map(Path, (authorization_path, predecessor, run))
    for p in (authorization_path, *authorization_path.parents):
        require(not p.is_symlink() and p.stat().st_uid == 0 and not p.stat().st_mode & 0o022, 'InitramfsAuthorizationUnprotected')
    authorization = json.loads(authorization_path.read_bytes())
    require(authorization['Version'] == 1 and authorization['Scope'] == 'OneConfiguredCheckpointInitramfs' and
            run.name == authorization['AttemptId'].replace('-', ''), 'InitramfsAuthorizationScope')
    verified = verify_completion(completion, authorization_path, 'configuration')
    require(predecessor.name == verified['SourceRunId'].replace('-', ''),
            'ConfiguredSourceRunChanged')
    protected_raw = verified['Raw']
    retention = protected_raw/'configured-retention.json'
    require(sha(retention) == authorization['RetentionSha256'], 'ConfiguredRetentionChanged')
    kept = json.loads(retention.read_bytes())
    prior = verify(protected_raw/'guest-evidence/execution', protected_raw/'guest-evidence/journals')
    require(prior['Configuration'] == prior['Teardown'] == 'AppliedAndVerified' and
            prior['EffectRecords'][-1] == kept['ConfigurationResult'] and prior['SessionRecords'][-1] == kept['CloseResult'] and
            authorization['ConfigurationSha256'] == kept['ConfigurationResult']['Sha256'] and
            authorization['CloseSha256'] == kept['CloseResult']['Sha256'] and
            authorization['GenerationId'] == kept['GenerationId'], 'ConfiguredPredecessorChanged')
    paths = [predecessor/n for n in kept['Backings']]; closed(paths)
    require_backing_capacity(180*1024**3)
    space = os.statvfs(run)
    require(space.f_bavail*space.f_frsize >= 180*1024**3, 'ConfiguredCopyGuestCapacity')
    for name, binding in kept['Backings'].items():
        p = predecessor/name
        require(identity(p) == (binding['Device'], binding['Inode'], binding['Length']) and
                p.stat().st_nlink == binding['Links'] and sha(p) == binding['Sha256'], 'ConfiguredBackingChanged')
    checkpoint(authorization_path.parent, 'reservation.json', {'Scope': authorization['Scope'],
        'AuthorizationSha256': sha(authorization_path), 'OperationId': authorization['OperationId'],
        'AttemptId': authorization['AttemptId'], 'ConfigurationSha256': authorization['ConfigurationSha256']})
    checkpoint(run, 'initramfs-copy-intent.json', {'AuthorizationSha256': sha(authorization_path),
        'Sources': kept['Backings'], 'RetentionSha256': sha(retention), 'RequiredBytesIncludingReserve': 180*1024**3})
    copies = []
    for name, serial, key in (('target.raw', 'IGLOO-LAB-TARGET', 'TargetSha256'), ('journal.raw', 'IGLOO-LAB-JOURNAL', 'JournalSha256')):
        require(kept['Backings'][name]['Sha256'] == authorization[key], 'InitramfsCopyAncestry')
        before, after = copy_verified(predecessor/name, run/name, authorization[key])
        copies.append({'Serial': serial, 'SourceDevice': before[0], 'SourceInode': before[1], 'DestinationDevice': after[0],
            'DestinationInode': after[1], 'Length': after[2], 'SourceSha256': authorization[key], 'ReopenedSha256': sha(run/name)})
    checkpoint(run, 'initramfs-derivation.json', {'Authorization': authorization,
        'AuthorizationSha256': sha(authorization_path), 'Copies': copies})
    verify_completion(completion, authorization_path, 'configuration')


def stage_predecessor(completion, authorization, destination):
    predecessor = verify_completion(completion, authorization, 'configuration')['Raw']
    stage_predecessor_raw(predecessor, destination)


def stage_predecessor_raw(predecessor, destination):
    """Shape already verified captured bytes for the read-only .NET reader."""
    predecessor = Path(predecessor)
    destination = Path(destination); destination.mkdir()
    shutil.copytree(predecessor/'input/predecessor', destination/'import')
    for name in ('session', 'effects'): shutil.copytree(predecessor/'guest-evidence/journals'/name, destination/name)
    for name in ('continuation.json', 'continuation-inventory.json'):
        shutil.copyfile(predecessor/'guest-evidence/execution'/name, destination/name)
    shutil.copyfile(predecessor/'input/source-authorization.json', destination/'derived-authorization.json')
    first = (predecessor/'guest-evidence/execution/configuration.stdout').read_bytes().splitlines()[0]
    checkpoint_exact_json_member(destination, 'configuration-plan.json', first, 'Plan')
    retained = json.loads((predecessor/'configured-retention.json').read_bytes())
    checkpoint(destination, 'binding.json', {'ConfigurationSha256': retained['ConfigurationResult']['Sha256'],
        'CloseSha256': retained['CloseResult']['Sha256']})
