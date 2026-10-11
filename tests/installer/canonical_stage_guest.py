"""Closed post-Close capture producer for future configuration/initramfs labs.

The guest emits exact record bytes; it does not choose a host destination or
create successor authority. The host must independently bind the live QEMU
peer, reopen these chains and establish stopped-writer backing hashes.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import stat

from canonical_configuration_readback import verify as verify_configuration
from canonical_initramfs_readback import verify as verify_initramfs
from canonical_storage_guest import ENV, require, save
from canonical_stage_files import copy_tree, read_file, snapshot_tree


STAGES = {
    'configuration': ('OneCheckpointDerivedCoreConfiguration',
                      'derived-authorization.json', 'core-configuration-',
                      'configuration-derived', 'predecessor', verify_configuration),
    'initramfs': ('OneConfiguredCheckpointInitramfs',
                  'initramfs-authorization.json', 'initramfs-',
                  'initramfs', 'configured-predecessor', verify_initramfs),
}
HEX = re.compile(r'[0-9A-F]{64}\Z')


def digest(raw):
    return hashlib.sha256(raw).hexdigest().upper()


def encode(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode()


def operation_id(stage, observed):
    return observed['OperationId'] if stage == 'configuration' else observed['Plan']['OperationId']


def journal_mount_record(raw, expected_major_minor):
    """Fresh kernel mount identity for the nominated persistent journal."""
    selected = []
    for line in raw.splitlines():
        left, separator, right = line.partition(' - ')
        require(separator, 'StageCaptureMountinfoMalformed')
        fields, super_fields = left.split(), right.split()
        require(len(fields) >= 6 and len(super_fields) >= 3,
                'StageCaptureMountinfoMalformed')
        point = fields[4]
        require(not point.startswith('/lab-journal/'),
                'StageCaptureJournalChildMount')
        if point == '/lab-journal':
            selected.append((fields, super_fields))
    require(len(selected) == 1, 'StageCaptureJournalMountStackedOrAbsent')
    fields, super_fields = selected[0]
    options = set(fields[5].split(','))
    require(fields[2] == expected_major_minor and fields[3] == '/' and
            {'rw', 'nodev', 'nosuid', 'noexec'}.issubset(options) and
            not any(value.startswith(('shared:', 'master:', 'propagate_from:'))
                    for value in fields[6:]) and
            super_fields[:2] == ['ext4', '/dev/vdc1'],
            'StageCaptureJournalMountIdentityChanged')
    return {'MountId': int(fields[0]), 'MajorMinor': fields[2],
            'Root': fields[3], 'MountPoint': fields[4],
            'FileSystem': super_fields[0], 'Source': super_fields[1],
            'MountOptions': sorted(options), 'PrivatePropagation': True,
            'ExactNoChildOrStackedMount': True}


def packet_from_files(stage, challenge, authorization, verified, host_binding, files):
    """Pure stage-specific packet declaration over an exact, closed file map."""
    require(stage in STAGES and type(challenge) is str and HEX.fullmatch(challenge),
            'StageCaptureDeclarationInvalid')
    scope = STAGES[stage][0]
    require(authorization['Version'] == 1 and authorization['Scope'] == scope and
            verified[stage.title()] == verified['Teardown'] == 'AppliedAndVerified' and
            operation_id(stage, verified) == authorization['OperationId'] and
            (verified['GenerationId'] if stage == 'configuration' else
             verified['Plan']['Provenance']['GenerationId']) == authorization['GenerationId'] and
            host_binding['RunId'] == authorization['AttemptId'],
            'StageCaptureTerminalChanged')
    require(0 < len(files) <= 4096 and all(
            type(name) is str and re.fullmatch(
                r'[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*', name) and
            all(part not in ('.', '..') for part in name.split('/')) and
            type(raw) is bytes for name, raw in files.items()) and
            'input/source-authorization.json' in files and
            f'guest-evidence/execution/{stage}.stdout' in files and
            'guest-evidence/execution/capture-journal-mount.json' in files and
            'guest-evidence/execution/host-binding.json' in files and
            any(name.startswith('guest-evidence/journals/session/') for name in files) and
            any(name.startswith('guest-evidence/journals/effects/') for name in files),
            'StageCaptureInventoryChanged')
    require(sum(len(raw) for raw in files.values()) <= 16 * 1024 * 1024,
            'StageCaptureBytesBound')
    result = verified['EffectRecords'][-1]['Sha256']
    close = verified['SessionRecords'][-1]['Sha256']
    packet = {'Version': 1, 'Scope': f'ControlledGuest{stage.title()}CaptureV1',
              'Stage': stage, 'Challenge': challenge,
              'RunId': authorization['AttemptId'],
              'GenerationId': authorization['GenerationId'],
              'OperationId': authorization['OperationId'],
              'SessionId': verified['SessionId'],
              'PlanSha256': verified['PlanSha256'],
              'ResultSha256': result, 'CloseSha256': close,
              'HostBindingSha256': digest(files['guest-evidence/execution/host-binding.json']),
              'Files': [{'Path': name, 'Length': len(raw), 'Sha256': digest(raw),
                         'Bytes': base64.b64encode(raw).decode('ascii')}
                        for name, raw in sorted(files.items())]}
    raw = encode(packet)
    require(len(raw) <= 24 * 1024 * 1024, 'StageCapturePacketBound')
    return raw


def emit(packet, challenge):
    """Established bounded BEGIN/DATA/END framing, with stage in the packet."""
    require(type(challenge) is str and HEX.fullmatch(challenge) and
            0 < len(packet) <= 24 * 1024 * 1024, 'StageCaptureFrameBound')
    print('', flush=True)
    print('IGLOO_USERDATA_CAPTURE_BEGIN '+challenge+' '+str(len(packet))+' '+digest(packet),
          flush=True)
    body = base64.b64encode(packet).decode('ascii')
    for index in range(0, len(body), 1024):
        print('IGLOO_DATA '+body[index:index+1024], flush=True)
    print('IGLOO_USERDATA_CAPTURE_END '+challenge, flush=True)


def capture_completed(stage, challenge):
    """Fixed guest post-Close path; create-new records and ordinary journal unmount."""
    require(stage in STAGES and type(challenge) is str and HEX.fullmatch(challenge),
            'StageCaptureCommandUnsupported')
    scope, auth_name, evidence_prefix, journal_branch, predecessor_name, reader = STAGES[stage]
    tools = Path('/opt/igloo-lab')
    authorization_path = tools/auth_name
    authorization_raw = read_file(authorization_path)
    authorization = json.loads(authorization_raw)
    require(authorization['Version'] == 1 and authorization['Scope'] == scope,
            'StageCaptureScopeChanged')
    run_id = authorization['AttemptId']
    evidence = Path('/root')/(evidence_prefix+run_id)
    journals = Path('/lab-journal')/journal_branch/run_id
    block, mounted = os.stat('/dev/vdc1'), os.stat('/lab-journal')
    require(stat.S_ISBLK(block.st_mode) and block.st_rdev == mounted.st_dev,
            'StageCaptureJournalDeviceChanged')
    mount_record = journal_mount_record(Path('/proc/self/mountinfo').read_text(),
        f'{os.major(block.st_rdev)}:{os.minor(block.st_rdev)}')
    save(evidence, 'capture-journal-mount.json', mount_record)
    stdout_before = read_file(evidence/f'{stage}.stdout')
    exit_before = read_file(evidence/f'{stage}-exit.json')
    journal_before = snapshot_tree(journals)
    require(json.loads(exit_before)['ExitCode'] == 0 and journal_before[0],
            'StageCaptureExecutionIncomplete')
    semantic_bytes = {f'guest-evidence/execution/{stage}.stdout': stdout_before}
    semantic_bytes.update({'guest-evidence/journals/'+name: data
                           for name, data in journal_before[0].items()})
    observed = reader(evidence, journals, captured=semantic_bytes)
    require(observed[stage.title()] == observed['Teardown'] == 'AppliedAndVerified' and
            operation_id(stage, observed) == authorization['OperationId'] and
            read_file(evidence/f'{stage}.stdout') == stdout_before and
            read_file(evidence/f'{stage}-exit.json') == exit_before and
            snapshot_tree(journals) == journal_before,
            'StageCaptureTerminalUnavailable')
    save(evidence, 'independent-handoff.json', observed)
    before = json.loads(read_file(evidence/'preserved-before.json'))
    with open('/dev/vdb1', 'rb') as volume:
        after = hashlib.file_digest(volume, 'sha256').hexdigest().upper()
    require(after == before['Sha256'], 'StageCapturePreservedChanged')
    save(evidence, 'preserved-after.json', {'Sha256': after, 'Unchanged': True})
    retained = Path('/root')/(f'{stage}-retained-journals-'+run_id)
    require(not retained.exists(), 'StageCaptureReplay')
    copied_journals = copy_tree(journals, retained)
    require(copied_journals == journal_before, 'StageCaptureJournalChangedBeforeCopy')
    retained_semantic_bytes = {f'guest-evidence/execution/{stage}.stdout': stdout_before}
    retained_semantic_bytes.update({'guest-evidence/journals/'+name: data
        for name, data in copied_journals[0].items()})
    require(reader(evidence, retained, captured=retained_semantic_bytes) == observed,
            'StageCaptureRetainedChanged')
    require(snapshot_tree(retained) == copied_journals,
            'StageCaptureRetainedCopyChanged')
    before_mounts = Path('/proc/self/mountinfo').read_text().splitlines()
    owned = [line for line in before_mounts if line.split()[4] == '/lab-journal']
    require(len(owned) == 1, 'StageCaptureJournalMountMissing')
    subprocess.run(['umount', '/lab-journal'], check=True, env=ENV)
    after_mounts = Path('/proc/self/mountinfo').read_text().splitlines()
    require([line for line in before_mounts if line not in owned] == after_mounts and
            not any(line.split()[4] == '/mnt/igloo-lab' for line in after_mounts),
            'StageCaptureJournalUnmountChanged')
    save(evidence, 'provisioning-journal-unmount.json', {
        'OrdinaryUnmount': True, 'ExactMountDelta': True, 'MountAbsent': True,
        'BeforeSha256': digest(('\n'.join(before_mounts)).encode()),
        'AfterSha256': digest(('\n'.join(after_mounts)).encode())})
    os.sync()
    execution_names = (
        'host-binding.json', f'{stage}.stdout', f'{stage}-exit.json',
        'capture-journal-mount.json',
        'independent-handoff.json', 'preserved-before.json',
        'preserved-after.json', 'provisioning-journal-unmount.json',
        'continuation.json' if stage == 'configuration' else 'declaration.json',
        'continuation-inventory.json' if stage == 'configuration' else 'candidate-plan.json')
    if stage == 'initramfs':
        execution_names += ('inventory.json',)
    source_authorization = read_file(authorization_path)
    require(source_authorization == authorization_raw,
            'StageCaptureAuthorizationChanged')
    execution = {name: read_file(evidence/name) for name in execution_names}
    predecessor = snapshot_tree(tools/predecessor_name)
    journal_copy = snapshot_tree(retained)
    require(journal_copy == copied_journals and predecessor[0] and journal_copy[0],
            'StageCaptureInputMissing')
    raw = {'input/source-authorization.json': source_authorization}
    raw.update({'guest-evidence/execution/'+name: data
                for name, data in execution.items()})
    raw.update({f'input/{predecessor_name}/'+name: data
                for name, data in predecessor[0].items()})
    raw.update({'guest-evidence/journals/'+name: data
                for name, data in journal_copy[0].items()})
    require(read_file(authorization_path) == source_authorization and
            all(read_file(evidence/name) == data for name, data in execution.items()) and
            snapshot_tree(tools/predecessor_name) == predecessor and
            snapshot_tree(retained) == journal_copy,
            'StageCaptureSourceChangedBeforeEmission')
    host = json.loads(raw['guest-evidence/execution/host-binding.json'])
    emit(packet_from_files(stage, challenge, authorization, observed, host, raw), challenge)
