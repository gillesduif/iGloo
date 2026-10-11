"""Controlled live configuration/initramfs capture for future lab successors.

This host receiver is stage-bound. It never accepts a QEMU-owned export as a
capture source or derives a historical anchor from an old record directory.
"""
import base64
import fcntl
import json
import os
from pathlib import Path
import pwd
import re
import select
import socket
import stat
import struct
import subprocess
import sys

HOST_CLOSURE = frozenset((
    'canonical_stage_acquisition.py', 'canonical_stage_completion.py',
    'canonical_stage_files.py',
    'canonical_configuration_readback.py', 'canonical_initramfs_readback.py',
    'canonical_userdata_acquisition.py', 'canonical_userdata_completion.py',
    'canonical_userdata_readback.py', 'canonical_configuration_derivation.py',
    'canonical_lab_runtime.py', 'canonical_storage_lab.py',
    'canonical_lab_capacity.py', 'userdata_contract.py',
    'debian_first_boot.py', 'isolation_policy.py'))

if __name__ == '__main__':
    # Only the root-held per-run closure may become an executable receiver.
    here = Path(__file__).absolute()
    directory = here.parent
    stage_parent = directory.parent.parent
    nominated = {
        'configuration-authorizations': 'configuration',
        'initramfs-authorizations': 'initramfs',
    }
    if (here.name != 'canonical_stage_acquisition.py' or
            directory.name != 'completion-tools' or
            stage_parent.parent != Path('/var/lib/igloo') or
            stage_parent.name not in nominated):
        raise ValueError('StageCaptureToolStageUnnominated')
    for path in (here, directory, *directory.parents):
        state = path.lstat()
        if (stat.S_ISLNK(state.st_mode) or state.st_uid != 0 or
                state.st_mode & 0o022 or
                path == here and (not stat.S_ISREG(state.st_mode) or state.st_nlink != 1)):
            raise ValueError('StageCaptureToolStageUnprotected')
    manifest_path = directory.parent/'completion-tools.json'
    state = manifest_path.lstat()
    if (not stat.S_ISREG(state.st_mode) or state.st_uid != 0 or
            state.st_nlink != 1 or state.st_mode & 0o022):
        raise ValueError('StageCaptureToolManifestUnprotected')
    manifest_raw = manifest_path.read_bytes()
    if len(manifest_raw) > 4096:
        raise ValueError('StageCaptureToolManifestOversized')
    def _unique_bootstrap(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise ValueError('StageCaptureToolManifestDuplicate')
            value[key] = item
        return value
    manifest = json.loads(manifest_raw, object_pairs_hook=_unique_bootstrap)
    if (set(manifest) != {'Version', 'Scope', 'Stage', 'Files'} or
            manifest['Version'] != 1 or
            manifest['Scope'] != 'ProtectedStageAcquisitionToolsV1' or
            manifest['Stage'] != nominated[stage_parent.name] or
            type(manifest['Files']) is not dict or
            set(manifest['Files']) != HOST_CLOSURE or
            {p.name for p in directory.iterdir()} != HOST_CLOSURE):
        raise ValueError('StageCaptureToolManifestChanged')
    import hashlib
    for name, expected in manifest['Files'].items():
        path = directory/name
        state = path.lstat()
        if (not stat.S_ISREG(state.st_mode) or state.st_uid != 0 or
                state.st_nlink != 1 or state.st_mode & 0o022 or
                hashlib.sha256(path.read_bytes()).hexdigest().upper() != expected):
            raise ValueError('StageCaptureToolBytesChanged')
    sys.path.insert(0, str(directory))

from canonical_configuration_derivation import closed, identity, require
from canonical_lab_runtime import checkpoint
from canonical_stage_completion import (STAGES, digest, protected, verify as verify_completion)
from canonical_storage_lab import sha
from canonical_stage_files import read_file as exact_file, snapshot_tree
from canonical_userdata_acquisition import (process_start_time,
                                            read_controlled_channel, unique)


RUN_PARENT = {
    'configuration': Path('/var/lib/igloo/canonical-labs'),
    'initramfs': Path('/var/lib/igloo/initramfs-helper-labs'),
}
AUTH_PARENT = {
    'configuration': Path('/var/lib/igloo/configuration-authorizations'),
    'initramfs': Path('/var/lib/igloo/initramfs-authorizations'),
}
COMPLETION_PARENT = Path('/var/lib/igloo/stage-completions')
HEX = re.compile(r'[0-9A-F]{64}\Z')
MAX_PACKET = 24 * 1024 * 1024


def _load(path):
    protected(path)
    return json.loads(Path(path).read_bytes(), object_pairs_hook=unique)


def _scope(stage):
    require(stage in STAGES, 'StageCaptureKindUnsupported')
    return STAGES[stage]['source_scope']


def _mount_field(raw):
    return re.sub(rb'\\([0-7]{3})',
                  lambda match: bytes((int(match.group(1), 8),)), raw)


def _mount_unattached(paths, proc_root=Path('/proc')):
    """Inspect every observable process mount namespace by backing identity.

    A namespace or mountpoint that cannot be observed blocks retention. In
    particular, a bind-mounted alias is not cleared by searching for the
    nominated path string in the receiver's own mountinfo.
    """
    bindings = {(path.stat().st_dev, path.stat().st_ino) for path in paths}
    devices = {f'{os.major(dev)}:{os.minor(dev)}'.encode()
               for dev, _ in bindings}
    processes = sorted((entry for entry in proc_root.iterdir()
                        if entry.name.isdecimal()), key=lambda entry: int(entry.name))
    require(len(processes) <= 4096, 'StageBackingMountObservationUnbounded')
    namespaces = set()
    observed_bytes = 0
    for process in processes:
        try:
            namespace = (process/'ns/mnt').stat().st_ino
            if namespace in namespaces:
                continue
            raw = (process/'mountinfo').read_bytes()
        except OSError as error:
            raise ValueError('StageBackingMountNamespaceUnresolved') from error
        observed_bytes += len(raw)
        require(len(raw) <= 8 * 1024 * 1024 and
                observed_bytes <= 32 * 1024 * 1024,
                'StageBackingMountObservationUnbounded')
        namespaces.add(namespace)
        for line in raw.splitlines():
            left, separator, right = line.partition(b' - ')
            fields, super_fields = left.split(), right.split()
            require(separator and len(fields) >= 6 and len(super_fields) >= 3,
                    'StageBackingMountObservationMalformed')
            require(super_fields[0] != b'nsfs',
                    'StageBackingDetachedMountNamespaceUnresolved')
            require(not any(os.fsencode(path) in line for path in paths),
                    'StageBackingHostMountReference')
            if fields[2] not in devices:
                continue
            mountpoint = _mount_field(fields[4])
            require(mountpoint.startswith(b'/') and
                    b'..' not in mountpoint.split(b'/'),
                    'StageBackingMountObservationMalformed')
            try:
                mounted = ((process/'root') /
                           os.fsdecode(mountpoint).lstrip('/')).stat()
            except OSError as error:
                raise ValueError('StageBackingMountIdentityUnresolved') from error
            require((mounted.st_dev, mounted.st_ino) not in bindings,
                    'StageBackingHostMountIdentityAttached')
    require(namespaces, 'StageBackingMountNamespaceUnresolved')
    # A namespace held only through an fd need not have a process whose
    # mountinfo was just read. Its topology cannot be cleared here.
    inspected = 0
    for process in processes:
        try:
            descriptors = tuple((process/'fd').iterdir())
            inspected += len(descriptors)
            require(inspected <= 131072,
                    'StageBackingMountObservationUnbounded')
            for descriptor in descriptors:
                try:
                    link = os.readlink(descriptor)
                except FileNotFoundError:
                    continue  # The fd closed during observation.
                if link.startswith('mnt:['):
                    require(int(link[5:-1]) in namespaces,
                            'StageBackingDetachedMountNamespaceUnresolved')
        except OSError as error:
            raise ValueError('StageBackingMountNamespaceUnresolved') from error


def _loop_unattached(paths, sys_root=Path('/sys/block'), dev_root=Path('/dev')):
    """Use LOOP_GET_STATUS64 device/inode, including aliased backing names."""
    bindings = {(path.stat().st_dev, path.stat().st_ino) for path in paths}
    devices = tuple(sys_root.glob('loop[0-9]*'))
    require(len(devices) <= 4096, 'StageBackingLoopObservationUnbounded')
    for device in devices:
        if not (device/'loop/backing_file').exists():
            continue
        try:
            fd = os.open(dev_root/device.name,
                         os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
            try:
                status = bytearray(232)  # Linux loop_info64.
                fcntl.ioctl(fd, 0x4C05, status, True)  # LOOP_GET_STATUS64.
            finally:
                os.close(fd)
        except OSError as error:
            raise ValueError('StageBackingLoopIdentityUnresolved') from error
        device_id, inode = struct.unpack_from('=QQ', status)
        require(device_id != 0 and inode != 0,
                'StageBackingLoopIdentityUnresolved')
        require((device_id, inode) not in bindings,
                'StageBackingLoopAttached')


def unattached(paths):
    """Require FD, loop/NBD and observable mount-namespace absence."""
    paths = tuple(map(Path, paths))
    closed(paths)
    for path in paths:
        require(not subprocess.check_output(
            ['/usr/sbin/losetup', '-j', str(path)], timeout=10).strip(),
            'StageBackingLoopAttached')
    _loop_unattached(paths)
    for device in Path('/sys/block').glob('nbd[0-9]*'):
        marker = device/'pid'
        if marker.exists():
            require(marker.read_text().strip() in ('', '0'),
                    'StageBackingNbdAttachmentUnresolved')
    _mount_unattached(paths)
    return {'FdWritersAbsent': True, 'LoopAttachmentsAbsent': True,
            'NbdAttachmentsAbsent': True, 'HostMountReferencesAbsent': True}


def inspect_controlled_process(stage, run, authorization):
    """Bind the live serial peer to root-held launch and exact QEMU backing FDs."""
    scope = _scope(stage)
    run, authorization = Path(run), Path(authorization)
    require(run.parent == RUN_PARENT[stage] and
            authorization.parent.parent == AUTH_PARENT[stage] and
            authorization.name == 'authorization.json' and
            run.name == authorization.parent.name.replace('-', ''),
            'StageCapturePathUnnominated')
    protected(authorization)
    parent = authorization.parent
    launch_path = parent/'completion-launch.json'
    process_path = parent/'completion-process.json'
    launch, process, auth = _load(launch_path), _load(process_path), _load(authorization)
    require(auth['Version'] == 1 and auth['Scope'] == scope and
            auth['AttemptId'].replace('-', '') == run.name and
            set(launch) == {'Version', 'Scope', 'Stage', 'AuthorizationSha256',
                            'RunId', 'RunPath', 'LaunchSha256', 'Arguments',
                            'Disks', 'ToolsSha256', 'ReceiverManifestSha256',
                            'GuestImportClosureSha256'} and
            launch['Version'] == 1 and launch['Scope'] == 'ControlledStageLaunchV1' and
            launch['Stage'] == stage and launch['AuthorizationSha256'] == sha(authorization) and
            launch['RunId'] == auth['AttemptId'] and launch['RunPath'] == str(run) and
            launch['LaunchSha256'] == sha(run/'launch-intent.json') and
            launch['ReceiverManifestSha256'] == sha(parent/'completion-tools.json') and
            type(launch['GuestImportClosureSha256']) is str and
            HEX.fullmatch(launch['GuestImportClosureSha256']) and
            launch['GuestImportClosureSha256'] == digest(exact_file(
                parent/'guest-import-closure.json', limit=16 * 1024)) and
            set(process) == {'Version', 'Scope', 'Stage', 'Pid', 'StartTime',
                             'LaunchWitnessSha256'} and
            process['Version'] == 1 and process['Scope'] == 'ControlledStageProcessV1' and
            process['Stage'] == stage and
            process['LaunchWitnessSha256'] == sha(launch_path),
            'StageCaptureLaunchWitnessChanged')
    pid = process['Pid']
    require(type(pid) is int and pid > 1 and type(process['StartTime']) is int and
            process_start_time(pid) == process['StartTime'],
            'StageCaptureProcessChanged')
    actual = Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')[:-1]
    args = launch['Arguments']
    require(actual == [arg.encode() for arg in args] and
            args[0] == '/usr/bin/qemu-system-x86_64' and
            args[args.index('-runas')+1] == 'gillesduif' and
            args[args.index('-nic')+1] == 'none' and
            f'unix:{run}/console.sock,server=on,wait=off' in args,
            'StageCaptureLaunchChanged')
    expected = {str(run/name): 2 for name in launch['Disks']}
    expected[str(run/'tools.iso')] = 0
    found = {}
    for fd in Path(f'/proc/{pid}/fd').iterdir():
        state = fd.stat()
        path = os.readlink(fd)
        require(not stat.S_ISBLK(state.st_mode), 'StageCapturePhysicalBlockFd')
        if not stat.S_ISREG(state.st_mode) or path == str(run/'qemu.stderr'):
            continue
        flags = next(line.split()[1] for line in
                     Path(f'/proc/{pid}/fdinfo/{fd.name}').read_text().splitlines()
                     if line.startswith('flags:'))
        mode = int(flags, 8) & 3
        require(path in expected and mode == expected[path] and path not in found,
                'StageCaptureBackingFdChanged')
        found[path] = {'Device': state.st_dev, 'Inode': state.st_ino,
                       'Length': state.st_size}
    require(set(found) == set(expected) and
            all(found[str(run/name)] == binding
                for name, binding in launch['Disks'].items()) and
            sha(run/'tools.iso') == launch['ToolsSha256'],
            'StageCaptureBackingIdentityChanged')
    return launch, process, found


def _allowed(stage, name):
    if not isinstance(name, str) or not re.fullmatch(
            r'[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*', name) or any(
                part in ('.', '..') for part in name.split('/')):
        return False
    fixed = {'input/source-authorization.json'} | {
        'guest-evidence/execution/'+part for part in (
            'host-binding.json', 'inventory.json', f'{stage}.stdout',
            'capture-journal-mount.json',
            f'{stage}-exit.json', 'independent-handoff.json',
            'preserved-before.json', 'preserved-after.json',
            'provisioning-journal-unmount.json',
            'continuation.json' if stage == 'configuration' else 'declaration.json',
            'continuation-inventory.json' if stage == 'configuration' else
            'candidate-plan.json')}
    return (name in fixed or name.startswith(STAGES[stage]['predecessor']) or
            name.startswith('guest-evidence/journals/'))


def accept_packet(stage, packet, challenge, run, authorization, pending,
                  launch, process, found):
    """Write an incomplete protected capture, then reopen the actual chains."""
    _scope(stage)
    run, authorization, pending = map(Path, (run, authorization, pending))
    require(0 < len(packet) <= MAX_PACKET and not pending.exists(),
            'StageCapturePendingExists')
    protected(pending.parent, file=False)
    value = json.loads(packet, object_pairs_hook=unique)
    title = stage.title()
    require(set(value) == {'Version', 'Scope', 'Stage', 'Challenge', 'RunId',
                           'GenerationId', 'OperationId', 'SessionId', 'PlanSha256',
                           'ResultSha256', 'CloseSha256', 'HostBindingSha256', 'Files'} and
            value['Version'] == 1 and value['Scope'] == f'ControlledGuest{title}CaptureV1' and
            value['Stage'] == stage and value['Challenge'] == challenge and
            value['RunId'] == launch['RunId'] and
            all(type(value[k]) is str and HEX.fullmatch(value[k]) for k in
                ('PlanSha256', 'ResultSha256', 'CloseSha256', 'HostBindingSha256')) and
            type(value['Files']) is list and 5 <= len(value['Files']) <= 4096,
            'StageCaptureSchemaChanged')
    auth = _load(authorization)
    require(value['GenerationId'] == auth['GenerationId'] and
            value['OperationId'] == auth['OperationId'],
            'StageCaptureOperationChanged')
    files = {}
    total = 0
    for item in value['Files']:
        require(type(item) is dict and set(item) ==
                {'Path', 'Length', 'Sha256', 'Bytes'} and
                _allowed(stage, item['Path']) and item['Path'] not in files and
                type(item['Length']) is int and 0 <= item['Length'] <= 16*1024*1024 and
                type(item['Sha256']) is str and HEX.fullmatch(item['Sha256']) and
                type(item['Bytes']) is str,
                'StageCaptureInventoryChanged')
        raw = base64.b64decode(item['Bytes'], validate=True)
        total += len(raw)
        require(total <= 16*1024*1024 and len(raw) == item['Length'] and
                base64.b64encode(raw).decode('ascii') == item['Bytes'] and
                digest(raw) == item['Sha256'], 'StageCaptureRawBytesChanged')
        files[item['Path']] = raw
    require(files['input/source-authorization.json'] ==
            exact_file(authorization, limit=4 * 1024 * 1024) and
            digest(files['guest-evidence/execution/host-binding.json']) ==
                value['HostBindingSha256'] and
            any(name.startswith(STAGES[stage]['predecessor']) for name in files) and
            any(name.startswith('guest-evidence/journals/session/') for name in files) and
            any(name.startswith('guest-evidence/journals/effects/') for name in files),
            'StageCaptureRequiredRecordChanged')
    host = json.loads(files['guest-evidence/execution/host-binding.json'],
                      object_pairs_hook=unique)
    require(host['RunId'] == value['RunId'] and
            all(any(binding['Serial'] == serial and
                    (binding['HostDevice'], binding['HostInode'], binding['Length']) ==
                    (found[str(run/name)]['Device'], found[str(run/name)]['Inode'],
                     found[str(run/name)]['Length']) and
                    binding['LaunchSha256'] == launch['LaunchSha256']
                    for binding in host['ReopenedBackings'])
                for name, serial in (('runtime.raw', 'IGLOO-GPT-RUNTIME'),
                                     ('target.raw', 'IGLOO-LAB-TARGET'),
                                     ('journal.raw', 'IGLOO-LAB-JOURNAL'))),
            'StageCaptureHostBindingChanged')
    from canonical_stage_completion import validate_journal_mount
    require('guest-evidence/execution/capture-journal-mount.json' in files,
            'StageCaptureJournalMountMissing')
    validate_journal_mount(files['guest-evidence/execution/capture-journal-mount.json'])
    pending.mkdir(mode=0o700)
    packet_fd = os.open(pending/'packet.bin', os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(packet_fd, 'wb') as out:
        out.write(packet); out.flush(); os.fsync(out.fileno())
    parent_fd = os.open(pending, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try: os.fsync(parent_fd)
    finally: os.close(parent_fd)
    raw_root = pending/'raw'
    raw_root.mkdir(mode=0o700)
    for name, data in sorted(files.items()):
        target = raw_root/name
        target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'wb') as out:
            out.write(data); out.flush(); os.fsync(out.fileno())
    for directory in sorted((p for p in pending.rglob('*') if p.is_dir()),
                            key=lambda path: len(path.parts), reverse=True):
        fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    spec = STAGES[stage]
    observed = spec['readback'](raw_root/'guest-evidence/execution',
                                raw_root/'guest-evidence/journals', captured=files)
    operation = observed['OperationId'] if stage == 'configuration' else observed['Plan']['OperationId']
    require(observed[title] == observed['Teardown'] == 'AppliedAndVerified' and
            operation == value['OperationId'] and observed['SessionId'] == value['SessionId'] and
            observed['PlanSha256'] == value['PlanSha256'] and
            observed['EffectRecords'][-1]['Sha256'] == value['ResultSha256'] and
            observed['SessionRecords'][-1]['Sha256'] == value['CloseSha256'] and
            json.loads(files[f'guest-evidence/execution/{stage}-exit.json'])['ExitCode'] == 0,
            'StageCaptureTerminalChanged')
    checkpoint(pending, 'capture.json', {
        'Version': 1, 'Scope': f'PendingControlled{title}CaptureV1',
        'Stage': stage, 'Challenge': challenge, 'RunId': value['RunId'],
        'GenerationId': value['GenerationId'], 'OperationId': value['OperationId'],
        'SessionId': value['SessionId'], 'PlanSha256': value['PlanSha256'],
        'ResultSha256': value['ResultSha256'], 'CloseSha256': value['CloseSha256'],
        'PacketSha256': digest(packet),
        'LaunchWitnessSha256': sha(authorization.parent/'completion-launch.json'),
        'ProcessWitnessSha256': sha(authorization.parent/'completion-process.json'),
        'SourceAuthorizationSha256': sha(authorization),
        'Files': {name: digest(raw) for name, raw in files.items()},
        'Status': 'PendingBackingReadback'})
    protected(pending, file=False)
    require(_load(pending/'capture.json')['PacketSha256'] == sha(pending/'packet.bin'),
            'StageCaptureReopenChanged')
    return pending


def receive_live(stage, run, authorization, pending, *, deadline_seconds=5*3600+600):
    run = Path(run)
    require(not Path(pending).exists(), 'StageCapturePendingExists')
    launch, process, found = inspect_controlled_process(stage, run, authorization)
    challenge = os.urandom(32).hex().upper()
    console = run/'console.sock'
    require(stat.S_ISSOCK(console.lstat().st_mode) and not console.is_symlink(),
            'StageCaptureConsoleChanged')
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
        channel.settimeout(5)
        channel.connect(str(console))
        packet = read_controlled_channel(channel, challenge, process, deadline_seconds,
            stage=stage,
            guest_import_closure_sha256=launch['GuestImportClosureSha256'],
            root_created_unprivileged_uid=pwd.getpwnam('gillesduif').pw_uid,
            begin_timeout_seconds=5*3600+120,
            inactivity_timeout_seconds=120,
            inactivity_after_begin_only=True,
            terminal_timeout_seconds=120,
            diagnostic_parent=Path(authorization).parent,
            executed_identities={'LaunchWitnessSha256':
                sha(Path(authorization).parent/'completion-launch.json')})
    require(inspect_controlled_process(stage, run, authorization)[2] == found,
            'StageCaptureBackingChangedDuringTransfer')
    return accept_packet(stage, packet, challenge, run, authorization,
                         pending, launch, process, found)


def finish_writer(stage, run, authorization, pending, *, wait_seconds=60):
    """Request only normal poweroff, then prove the nominated writer exited.

    A timeout leaves the pending capture intact and never authorizes a retry,
    finalization, force termination or a canonical Close claim.
    """
    _scope(stage)
    run, authorization, pending = map(Path, (run, authorization, pending))
    require(type(wait_seconds) in (int, float) and 0 < wait_seconds <= 120 and
            pending.parent == COMPLETION_PARENT/stage and
            pending.name == run.name and
            not (authorization.parent/'completion-writer-disposition.json').exists(),
            'StageWriterDispositionReplayOrPathChanged')
    launch, process, found = inspect_controlled_process(stage, run, authorization)
    capture = _load(pending/'capture.json')
    require(capture['Status'] == 'PendingBackingReadback' and
            capture['Stage'] == stage and capture['RunId'] == launch['RunId'] and
            capture['LaunchWitnessSha256'] == sha(authorization.parent/'completion-launch.json') and
            capture['ProcessWitnessSha256'] == sha(authorization.parent/'completion-process.json'),
            'StageWriterCaptureChanged')
    pid = process['Pid']
    require(hasattr(os, 'pidfd_open'), 'StageWriterPidfdUnavailable')
    handle = os.pidfd_open(pid)
    try:
        require(process_start_time(pid) == process['StartTime'],
                'StageWriterProcessChanged')
        console = run/'console.sock'
        require(stat.S_ISSOCK(console.lstat().st_mode) and not console.is_symlink(),
                'StageWriterConsoleChanged')
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
            channel.settimeout(5)
            channel.connect(str(console))
            peer_pid, peer_uid, _ = struct.unpack('3i', channel.getsockopt(
                socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
            unprivileged_uid = pwd.getpwnam('gillesduif').pw_uid
            require(peer_pid == pid and unprivileged_uid != 0 and
                    os.stat(f'/proc/{pid}').st_uid == unprivileged_uid and
                    peer_uid in (0, unprivileged_uid) and
                    process_start_time(pid) == process['StartTime'],
                    'StageWriterControlPeerChanged')
            channel.sendall(b'poweroff\n')
        poller = select.poll()
        poller.register(handle, select.POLLIN)
        require(any(event & select.POLLIN for _, event in
                    poller.poll(int(wait_seconds*1000))), 'StageWriterExitTimeout')
    finally:
        os.close(handle)
    # The pidfd proves exit of the exact process, even if its numeric PID is
    # recycled. The shared verifier separately excludes all backing FDs.
    names = ('runtime.raw', 'target.raw', 'journal.raw')
    exclusions = unattached([run/name for name in names])
    require(all(identity(run/name) == (binding['Device'], binding['Inode'],
                                               binding['Length']) and
                (run/name).stat().st_nlink == 1
                for name, binding in launch['Disks'].items()) and
            sha(run/'tools.iso') == launch['ToolsSha256'],
            'StageWriterBackingChanged')
    checkpoint(authorization.parent, 'completion-writer-disposition.json', {
        'Version': 1, 'Scope': 'ControlledStageWriterDispositionV1',
        'Stage': stage, 'RunId': launch['RunId'], 'Pid': pid,
        'StartTime': process['StartTime'],
        'LaunchWitnessSha256': sha(authorization.parent/'completion-launch.json'),
        'ProcessWitnessSha256': sha(authorization.parent/'completion-process.json'),
        'CaptureSha256': sha(pending/'capture.json'),
        'PoweroffRequested': True, 'ProcessExitedViaPidfd': True,
        'WriterExcluded': True, 'GuestShutdownVerified': False,
        'CanonicalCloseClaimed': False, 'AttachmentObservation': exclusions})
    reopened = _load(authorization.parent/'completion-writer-disposition.json')
    require(reopened['CaptureSha256'] == sha(pending/'capture.json') and
            reopened['WriterExcluded'] is True,
            'StageWriterDispositionReopenChanged')
    return authorization.parent/'completion-writer-disposition.json'


def pin_writer(stage, run, authorization):
    """Hold a pidfd before the capture can fail or the numeric PID can recycle."""
    launch, process, _ = inspect_controlled_process(stage, run, authorization)
    require(hasattr(os, 'pidfd_open'), 'StageWriterPidfdUnavailable')
    handle = os.pidfd_open(process['Pid'])
    try:
        require(process_start_time(process['Pid']) == process['StartTime'],
                'StageWriterProcessChanged')
        return handle, launch, process
    except BaseException:
        os.close(handle)
        raise


def pidfd_exited(handle, wait_seconds):
    poller = select.poll()
    poller.register(handle, select.POLLIN)
    return any(event & select.POLLIN for _, event in
               poller.poll(int(wait_seconds * 1000)))


def failure_disposition(stage, run, authorization, pending, handle, launch, process,
                        failure, *, wait_seconds=30, attempt_poweroff=True):
    """Preserve failure and attempt only a fixed normal shutdown of this writer.

    No host termination authority is inferred for a canonical target. A live
    writer after the bounded request remains explicitly unresolved.
    """
    _scope(stage)
    run, authorization, pending = map(Path, (run, authorization, pending))
    require(type(wait_seconds) in (int, float) and 0 < wait_seconds <= 60 and
            type(attempt_poweroff) is bool and
            pending.parent == COMPLETION_PARENT/stage and pending.name == run.name and
            not (authorization.parent/'completion-failure-intent.json').exists(),
            'StageFailureDispositionReplayOrPathChanged')
    code = str(failure) if type(failure) is ValueError else type(failure).__name__
    if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_.-]{0,79}', code):
        code = 'StageCaptureFailure'
    capture_path = pending/'capture.json'
    try:
        capture_sha = digest(exact_file(capture_path, limit=4 * 1024 * 1024))
    except (OSError, ValueError):
        capture_sha = None
    checkpoint(authorization.parent, 'completion-failure-intent.json', {
        'Version': 1, 'Scope': 'ControlledStageFailureDispositionV1',
        'Stage': stage, 'RunId': launch['RunId'], 'FailureCode': code,
        'Pid': process['Pid'], 'StartTime': process['StartTime'],
        'LaunchWitnessSha256': sha(authorization.parent/'completion-launch.json'),
        'ProcessWitnessSha256': sha(authorization.parent/'completion-process.json'),
        'CaptureSha256': capture_sha, 'WriterUnresolvedAtIntent': True})
    requested = False
    if attempt_poweroff and not pidfd_exited(handle, 0):
        try:
            current_launch, current_process, _ = inspect_controlled_process(
                stage, run, authorization)
            require(current_launch == launch and current_process == process and
                    process_start_time(process['Pid']) == process['StartTime'],
                    'StageFailureWriterChanged')
            console = run/'console.sock'
            require(stat.S_ISSOCK(console.lstat().st_mode) and
                    not console.is_symlink(), 'StageFailureConsoleChanged')
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                channel.settimeout(5)
                channel.connect(str(console))
                peer_pid, peer_uid, _ = struct.unpack('3i', channel.getsockopt(
                    socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
                uid = pwd.getpwnam('gillesduif').pw_uid
                require(peer_pid == process['Pid'] and uid != 0 and
                        os.stat(f'/proc/{process["Pid"]}').st_uid == uid and
                        peer_uid in (0, uid), 'StageFailureControlPeerChanged')
                channel.sendall(b'poweroff\n')
                requested = True
        except (OSError, ValueError):
            pass  # The writer remains unresolved; no alternate command or signal.
    exited = pidfd_exited(handle, wait_seconds) if requested else pidfd_exited(handle, 0)
    excluded = False
    observation = None
    if exited:
        try:
            observation = unattached([run/name for name in
                                      ('runtime.raw', 'target.raw', 'journal.raw')])
            excluded = True
        except (OSError, ValueError, subprocess.SubprocessError):
            pass
    checkpoint(authorization.parent, 'completion-failure-disposition.json', {
        'Version': 1, 'Scope': 'ControlledStageFailureDispositionV1',
        'Stage': stage, 'RunId': launch['RunId'], 'FailureCode': code,
        'FailureIntentSha256': sha(authorization.parent/'completion-failure-intent.json'),
        'NormalPoweroffRequested': requested, 'ProcessExitedViaPidfd': exited,
        'AttachmentObservation': observation, 'WriterExcluded': excluded,
        'GuestShutdownVerified': False, 'CanonicalCloseClaimed': False})
    require(_load(authorization.parent/'completion-failure-disposition.json')
            ['WriterExcluded'] == excluded, 'StageFailureDispositionReopenChanged')
    return excluded


def complete_live(stage, run, authorization, pending):
    """One-way receive -> writer disposition -> finalization sequence."""
    _scope(stage)
    require(Path(pending).parent == COMPLETION_PARENT/stage and
            Path(pending).name == Path(run).name and
            not Path(pending).exists(), 'StageCaptureReplayOrPathChanged')
    handle, launch, process = pin_writer(stage, run, authorization)
    try:
        step = 'receive'
        try:
            receive_live(stage, run, authorization, pending)
            step = 'finish'
            finish_writer(stage, run, authorization, pending)
            step = 'finalize'
            return finalize_retention(stage, run, authorization, pending)
        except BaseException as error:
            try:
                excluded = failure_disposition(stage, run, authorization, pending,
                    handle, launch, process, error,
                    attempt_poweroff=(step == 'receive'))
            except BaseException as disposition_error:
                raise ValueError('StageCaptureFailureDispositionUnavailable') from disposition_error
            if not excluded:
                raise ValueError('StageCaptureFailedWriterUnresolved') from error
            raise
    finally:
        os.close(handle)


def seal_finalized_anchor(stage, pending, authorization, anchor, capture):
    """Create the positive seal only after the caller's retention readbacks."""
    pending, authorization = Path(pending), Path(authorization)
    staged_name = 'completion-seal-staged.json'
    final_name = 'completion-seal.json'
    require(stage in STAGES and
            not os.path.lexists(pending/staged_name) and
            not os.path.lexists(pending/final_name),
            'StageCaptureSealReplay')
    protected(pending, file=False)
    protected(pending/'anchor.json')
    require(_load(pending/'anchor.json') == anchor,
            'StageCaptureAnchorReopenChanged')
    anchor_raw = exact_file(pending/'anchor.json', limit=4 * 1024 * 1024)
    require(json.loads(anchor_raw, object_pairs_hook=unique) == anchor,
            'StageCaptureAnchorBytesChanged')
    from canonical_stage_completion import _require_no_failure_disposition
    _require_no_failure_disposition(authorization.parent)
    seal = {'Version': 1, 'Scope': 'ProtectedStageRetentionSealV1',
        'Stage': stage, 'RunId': capture['RunId'],
        'GenerationId': capture['GenerationId'],
        'OperationId': capture['OperationId'],
        'SessionId': capture['SessionId'],
        'ResultSha256': capture['ResultSha256'],
        'CloseSha256': capture['CloseSha256'],
        'AnchorSha256': digest(anchor_raw),
        'CaptureSha256': sha(pending/'capture.json'),
        'WriterDispositionSha256': sha(
            authorization.parent/'completion-writer-disposition.json'),
        'RetentionSha256': anchor['RetentionSha256'],
        'TargetSha256': anchor['TargetSha256'],
        'JournalSha256': anchor['JournalSha256'],
        'Status': 'FinalizedAfterIndependentReopen'}
    # The final name is absent until a separately named, durable candidate has
    # been independently reopened. A failure during that write/reopen cannot
    # leave a plausible positive seal, even if failure-marker IO also fails.
    checkpoint(pending, staged_name, seal)
    staged_raw = exact_file(pending/staged_name, limit=4 * 1024 * 1024)
    require(json.loads(staged_raw, object_pairs_hook=unique) == seal,
            'StageCaptureSealStagedReopenChanged')
    parent = os.open(pending, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        # Link is create-new. While both names exist, link count 2 and the
        # staged-name guard both make this handoff inadmissible to successors.
        os.link(staged_name, final_name, src_dir_fd=parent,
                dst_dir_fd=parent, follow_symlinks=False)
        os.fsync(parent)
        os.unlink(staged_name, dir_fd=parent)
        os.fsync(parent)
    finally:
        os.close(parent)
    final_raw = exact_file(pending/final_name, limit=4 * 1024 * 1024)
    require(final_raw == staged_raw and
            _load(pending/final_name) == seal,
            'StageCaptureSealReopenChanged')
    return pending/final_name


def record_controller_success(stage, run, authorization, pending, seal):
    """Acknowledge only a completed `complete_live` return in the host controller.

    Production calls this only from main after the receiver, writer disposition,
    retention and final seal have all returned successfully. It is not a
    substitute for a separately issued successor authorization.
    """
    _scope(stage)
    run, authorization, pending, seal = map(Path,
        (run, authorization, pending, seal))
    require(run.parent == RUN_PARENT[stage] and
            authorization.parent.parent == AUTH_PARENT[stage] and
            authorization.name == 'authorization.json' and
            run.name == authorization.parent.name.replace('-', '') and
            pending == COMPLETION_PARENT/stage/run.name and
            seal == pending/'completion-seal.json' and
            not os.path.lexists(authorization.parent/
                                  'completion-controller-success.json'),
            'StageControllerSuccessReplayOrPathChanged')
    from canonical_stage_completion import (_validate_seal,
        _validate_controller_success, _require_committed_seal,
        _require_no_failure_disposition)
    protected(authorization)
    protected(pending, file=False)
    _require_committed_seal(pending)
    _require_no_failure_disposition(authorization.parent)

    def read(path):
        raw = exact_file(path, limit=4 * 1024 * 1024)
        return json.loads(raw, object_pairs_hook=unique), raw

    source, source_raw = read(authorization)
    anchor, anchor_raw = read(pending/'anchor.json')
    finalized, seal_raw = read(seal)
    capture, capture_raw = read(pending/'capture.json')
    launch, launch_raw = read(authorization.parent/'completion-launch.json')
    process, process_raw = read(authorization.parent/'completion-process.json')
    disposition, disposition_raw = read(
        authorization.parent/'completion-writer-disposition.json')
    require(source['Scope'] == STAGES[stage]['source_scope'] and
            source['AttemptId'] == anchor['SourceRunId'] == capture['RunId'] ==
            launch['RunId'] == disposition['RunId'] and
            source['OperationId'] == anchor['OperationId'] == capture['OperationId'] and
            source['GenerationId'] == anchor['GenerationId'] == capture['GenerationId'] and
            launch['Stage'] == process['Stage'] == disposition['Stage'] == stage and
            launch['AuthorizationSha256'] == digest(source_raw) and
            process['LaunchWitnessSha256'] == digest(launch_raw) and
            capture['LaunchWitnessSha256'] == digest(launch_raw) and
            capture['ProcessWitnessSha256'] == digest(process_raw) and
            disposition['CaptureSha256'] == digest(capture_raw) and
            disposition['ProcessExitedViaPidfd'] is True and
            disposition['WriterExcluded'] is True,
            'StageControllerSuccessInputsChanged')
    _validate_seal(finalized, anchor, anchor_raw, capture_raw,
                   disposition_raw, stage)
    success = {'Version': 1,
        'Scope': 'ProtectedStageControllerSuccessV1',
        'Stage': stage, 'RunId': anchor['SourceRunId'],
        'GenerationId': anchor['GenerationId'],
        'OperationId': anchor['OperationId'],
        'SessionId': anchor['SessionId'],
        'ResultSha256': anchor['ResultSha256'],
        'CloseSha256': anchor['CloseSha256'],
        'AnchorSha256': digest(anchor_raw),
        'SealSha256': digest(seal_raw),
        'CaptureSha256': digest(capture_raw),
        'SourceAuthorizationSha256': digest(source_raw),
        'LaunchWitnessSha256': digest(launch_raw),
        'ProcessWitnessSha256': digest(process_raw),
        'WriterDispositionSha256': digest(disposition_raw),
        'Status': 'CompletedAfterProtectedSealReopen'}
    _validate_controller_success(success, stage, anchor, anchor_raw,
                                 seal_raw, capture_raw, source_raw, launch_raw,
                                 process_raw, disposition_raw)
    target = authorization.parent/'completion-controller-success.json'
    checkpoint(authorization.parent, target.name, success)
    reopened, _ = read(target)
    require(reopened == success, 'StageControllerSuccessReopenChanged')
    return target


def finalize_retention(stage, run, authorization, pending):
    """Output terminal/backing digests only after canonical Close and writer exit."""
    _scope(stage)
    run, authorization, pending = map(Path, (run, authorization, pending))
    protected(pending, file=False)
    capture = _load(pending/'capture.json')
    launch_path = authorization.parent/'completion-launch.json'
    process_path = authorization.parent/'completion-process.json'
    launch, process = _load(launch_path), _load(process_path)
    disposition = _load(authorization.parent/'completion-writer-disposition.json')
    require(capture['Status'] == 'PendingBackingReadback' and
            capture['RunId'] == _load(authorization)['AttemptId'] and
            capture['LaunchWitnessSha256'] == sha(launch_path) and
            capture['ProcessWitnessSha256'] == sha(process_path) and
            set(disposition) == {'Version', 'Scope', 'Stage', 'RunId', 'Pid',
                'StartTime', 'LaunchWitnessSha256', 'ProcessWitnessSha256',
                'CaptureSha256', 'PoweroffRequested', 'ProcessExitedViaPidfd',
                'WriterExcluded', 'GuestShutdownVerified', 'CanonicalCloseClaimed',
                'AttachmentObservation'} and
            disposition['Version'] == 1 and
            disposition['Scope'] == 'ControlledStageWriterDispositionV1' and
            disposition['Stage'] == stage and disposition['RunId'] == capture['RunId'] and
            disposition['Pid'] == process['Pid'] and
            disposition['StartTime'] == process['StartTime'] and
            disposition['LaunchWitnessSha256'] == sha(launch_path) and
            disposition['ProcessWitnessSha256'] == sha(process_path) and
            disposition['CaptureSha256'] == sha(pending/'capture.json') and
            disposition['PoweroffRequested'] is True and
            disposition['ProcessExitedViaPidfd'] is True and
            disposition['WriterExcluded'] is True and
            disposition['AttachmentObservation'] == {'FdWritersAbsent': True,
                'LoopAttachmentsAbsent': True, 'NbdAttachmentsAbsent': True,
                'HostMountReferencesAbsent': True} and
            disposition['GuestShutdownVerified'] is False and
            disposition['CanonicalCloseClaimed'] is False and
            capture['PacketSha256'] == sha(pending/'packet.bin') and
            not (pending/'anchor.json').exists(),
            'StageCaptureFinalizationChanged')
    pid = process['Pid']
    require(not Path(f'/proc/{pid}/cmdline').exists() or
            not Path(f'/proc/{pid}/cmdline').read_bytes(),
            'StageCaptureWriterStillRunning')
    names = ('runtime.raw', 'target.raw', 'journal.raw')
    unattached([run/name for name in names])
    before = {name: identity(run/name) for name in names}
    metadata = {name: ((run/name).stat().st_mtime_ns, (run/name).stat().st_ctime_ns)
                for name in names}
    require(all(before[name] == (binding['Device'], binding['Inode'], binding['Length']) and
                (run/name).stat().st_nlink == 1
                for name, binding in launch['Disks'].items()),
            'StageCaptureRetainedBackingChanged')
    hashes = {name: sha(run/name) for name in names}
    unattached([run/name for name in names])
    require({name: identity(run/name) for name in names} == before and
            {name: ((run/name).stat().st_mtime_ns, (run/name).stat().st_ctime_ns)
             for name in names} == metadata,
            'StageCaptureBackingChangedDuringHash')
    raw = pending/'raw'
    captured_files, captured_dirs = snapshot_tree(raw,
        max_bytes=16 * 1024 * 1024, max_file=16 * 1024 * 1024)
    required_dirs = {'/'.join(name.split('/')[:index])
                     for name in captured_files
                     for index in range(1, len(name.split('/')))}
    require(set(capture['Files']) == set(captured_files) and
            set(captured_dirs) == required_dirs and
            all(_allowed(stage, name) and digest(data) == capture['Files'][name]
                for name, data in captured_files.items()),
            'StageCaptureRawInventoryChanged')
    from canonical_stage_completion import validate_journal_mount
    validate_journal_mount(captured_files[
        'guest-evidence/execution/capture-journal-mount.json'])
    observed = STAGES[stage]['readback'](raw/'guest-evidence/execution',
                                         raw/'guest-evidence/journals',
                                         captured=captured_files)
    require(observed[stage.title()] == observed['Teardown'] == 'AppliedAndVerified' and
            observed['EffectRecords'][-1]['Sha256'] == capture['ResultSha256'] and
            observed['SessionRecords'][-1]['Sha256'] == capture['CloseSha256'] and
            snapshot_tree(raw, max_bytes=16 * 1024 * 1024,
                          max_file=16 * 1024 * 1024) ==
            (captured_files, captured_dirs),
            'StageCaptureRawReopenChanged')
    from canonical_stage_completion import (_inventory,
        _require_no_failure_disposition)
    retained = {'RunId': capture['RunId'], 'GenerationId': capture['GenerationId'],
                'OperationId': capture['OperationId'], 'Scope': _scope(stage),
                'Outcome': 'AppliedAndVerified', 'CanonicalTeardown': 'AppliedAndVerified',
                STAGES[stage]['result']: observed['EffectRecords'][-1],
                'CloseResult': observed['SessionRecords'][-1],
                'IndependentHandoffSha256': sha(raw/'guest-evidence/execution/independent-handoff.json'),
                'Backings': {name: {'Device': before[name][0], 'Inode': before[name][1],
                    'Length': before[name][2], 'Links': 1, 'Sha256': hashes[name]}
                    for name in names}}
    checkpoint(raw, STAGES[stage]['retention'], retained)
    checkpoint(raw, f'{stage}-independent-host-readback.json', {
        'Verified': observed, 'CaptureSha256': sha(pending/'capture.json'),
        'WriterExcluded': True})
    inventory = {name: digest(data) for name, data in _inventory(raw, stage).items()}
    anchor = {'Version': 1, 'Scope': f'TrustedLive{stage.title()}TerminalV1',
        'Stage': stage, 'SourceRunId': capture['RunId'],
        'GenerationId': capture['GenerationId'], 'OperationId': capture['OperationId'],
        'SessionId': capture['SessionId'], 'PlanSha256': capture['PlanSha256'],
        'ResultSha256': capture['ResultSha256'], 'CloseSha256': capture['CloseSha256'],
        'RetentionSha256': sha(raw/STAGES[stage]['retention']),
        'TargetSha256': hashes['target.raw'], 'JournalSha256': hashes['journal.raw'],
        'CaptureSha256': sha(pending/'capture.json'), 'Files': inventory}
    checkpoint(pending, 'anchor.json', anchor)
    return seal_finalized_anchor(stage, pending, authorization, anchor, capture)


def main(argv):
    require(len(argv) in (4, 5) and argv[0] in ('complete', 'publish'),
            'StageCaptureCommandUnsupported')
    action, stage, run_arg, auth_arg, *rest = argv
    _scope(stage)
    run, authorization = Path(run_arg), Path(auth_arg)
    require(run.parent == RUN_PARENT[stage] and
            authorization.parent.parent == AUTH_PARENT[stage] and
            authorization.name == 'authorization.json' and
            run.name == authorization.parent.name.replace('-', '') and
            (not rest if action != 'publish' else len(rest) == 1),
            'StageCaptureHostPathUnnominated')
    protected(authorization)
    protected(COMPLETION_PARENT.parent, file=False)
    if not COMPLETION_PARENT.exists():
        COMPLETION_PARENT.mkdir(mode=0o700)
        fd = os.open(COMPLETION_PARENT.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    protected(COMPLETION_PARENT, file=False)
    branch = COMPLETION_PARENT/stage
    if not branch.exists():
        branch.mkdir(mode=0o700)
        fd = os.open(COMPLETION_PARENT, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    protected(branch, file=False)
    pending = branch/run.name
    if action == 'complete':
        seal = complete_live(stage, run, authorization, pending)
        success = record_controller_success(stage, run, authorization,
                                            pending, seal)
        print(json.dumps({'Status': 'ProtectedControllerCompletionRecorded',
                          'Stage': stage,
                          'AnchorSha256': digest(exact_file(pending/'anchor.json')),
                          'SealSha256': digest(exact_file(seal)),
                          'ControllerSuccessSha256': digest(exact_file(success))},
                         sort_keys=True))
    else:
        from canonical_stage_completion import publish
        next_authorization = Path(rest[0])
        destination = branch/(run.name+'-published')
        verified = publish(pending, next_authorization, destination, stage)
        print(json.dumps({'Status': 'ProtectedStageCompletionPublished',
                          'Stage': stage, 'AnchorSha256': verified['AnchorSha256']},
                         sort_keys=True))


if __name__ == '__main__':
    main(sys.argv[1:])
