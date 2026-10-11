"""Lab-only agent-stage derivative from an independently anchored completion."""
import json
import os
from pathlib import Path
import shutil

from canonical_configuration_derivation import closed, copy_verified, identity, require
from canonical_lab_capacity import require_backing_capacity
from canonical_lab_runtime import checkpoint
from canonical_storage_lab import sha
from canonical_userdata_completion import verify as verify_completion

RESERVE = 180 * 1024**3


def preflight(authorization, predecessor, handoff):
    authorization, predecessor, handoff = map(Path, (authorization, predecessor, handoff))
    verified = verify_completion(handoff, authorization, require_live=True)
    a = json.loads(authorization.read_bytes())
    require(predecessor.name == verified['SourceRunId'].replace('-', ''), 'AgentStageSourceRunChanged')
    retention = json.loads((verified['Raw'] / 'userdata-retention.json').read_bytes())
    require(retention['RunId'] == verified['SourceRunId'] and
            retention['Backings']['target.raw']['Sha256'] == a['TargetSha256'] and
            retention['Backings']['journal.raw']['Sha256'] == a['JournalSha256'], 'AgentStageBackingAnchorChanged')
    paths = [predecessor / name for name in ('target.raw', 'journal.raw')]
    closed(paths)
    for name in ('target.raw', 'journal.raw'):
        p = predecessor / name; binding = retention['Backings'][name]
        require(identity(p) == (binding['Device'], binding['Inode'], binding['Length']) and
                p.stat().st_nlink == binding['Links'] == 1 and sha(p) == binding['Sha256'],
                'AgentStageOriginalBackingChanged')
    return a, verified, retention


def derive(authorization, predecessor, handoff, run):
    authorization, predecessor, handoff, run = map(Path, (authorization, predecessor, handoff, run))
    a, verified, retention = preflight(authorization, predecessor, handoff)
    require(run.name == a['AttemptId'].replace('-', '') and a['AttemptId'] != verified['SourceRunId'],
            'AgentStageAttemptChanged')
    require_backing_capacity(RESERVE)
    space = os.statvfs(run)
    require(space.f_bavail * space.f_frsize >= RESERVE, 'AgentStageGuestCapacity')
    checkpoint(authorization.parent, 'reservation.json', {'Scope': a['Scope'],
        'AuthorizationSha256': sha(authorization), 'OperationId': a['OperationId'],
        'AttemptId': a['AttemptId'], 'UserDataSha256': a['UserDataSha256'],
        'CompletionSha256': sha(handoff / 'result.json')})
    checkpoint(run, 'agent-stage-copy-intent.json', {'AuthorizationSha256': sha(authorization),
        'CompletionSha256': sha(handoff / 'result.json'), 'Sources': retention['Backings'],
        'RequiredBytesIncludingReserve': RESERVE})
    copies = []
    for name, serial, key in (('target.raw', 'IGLOO-LAB-TARGET', 'TargetSha256'),
                              ('journal.raw', 'IGLOO-LAB-JOURNAL', 'JournalSha256')):
        before, after = copy_verified(predecessor / name, run / name, a[key])
        copies.append({'Serial': serial, 'SourceDevice': before[0], 'SourceInode': before[1],
            'DestinationDevice': after[0], 'DestinationInode': after[1], 'Length': after[2],
            'SourceSha256': a[key], 'ReopenedSha256': sha(run / name)})
    checkpoint(run, 'agent-stage-derivation.json', {'Authorization': a,
        'AuthorizationSha256': sha(authorization), 'Copies': copies})
    # The copy did not change the authenticated source or the protected handoff.
    preflight(authorization, predecessor, handoff)
    return verified


def stage_predecessor(handoff, authorization, destination):
    verified = verify_completion(handoff, authorization, require_live=True)
    destination = Path(destination)
    require(not destination.exists(), 'AgentStageInputExists')
    shutil.copytree(verified['Raw'], destination / 'raw')
    shutil.copyfile(Path(handoff) / 'anchor.json', destination / 'anchor.json')
    shutil.copyfile(Path(handoff) / 'capture.json', destination / 'capture.json')
    shutil.copyfile(Path(handoff) / 'packet.bin', destination / 'packet.bin')
    shutil.copyfile(Path(handoff) / 'result.json', destination / 'result.json')
    shutil.copyfile(authorization, destination / 'authorization.json')
    copied = verify_completion(destination, destination / 'authorization.json', require_live=True)
    require(copied['ResultSha256'] == verified['ResultSha256'] and
            copied['CloseSha256'] == verified['CloseSha256'] and
            copied['AnchorSha256'] == verified['AnchorSha256'], 'AgentStageStagedCompletionChanged')
    return verified
