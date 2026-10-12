"""Live, controlled-console UserData completion capture; lab authority only.

The QEMU-owned run directory is never an evidence source for this receiver.
The protected launch witness and the live peer/FD observation establish the
channel; the complete guest chains establish the result. A pending capture is
not a successor checkpoint until writer exclusion and retention finalization.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import shlex
import socket
import stat
import struct
import sys
import time

HOST_CLOSURE = frozenset(('canonical_userdata_acquisition.py',
    'canonical_userdata_completion.py', 'canonical_userdata_readback.py',
    'canonical_configuration_readback.py', 'canonical_configuration_derivation.py',
    'canonical_lab_runtime.py', 'canonical_storage_lab.py', 'canonical_lab_capacity.py',
    'userdata_contract.py', 'debian_first_boot.py', 'isolation_policy.py'))


# This code is carried by the peer-bound, fixed host command. The guest must
# not execute even the first line of a writable-stage entry script until its
# exact bytes join the protected prelaunch tools manifest. Keep the verified
# bytes for execution and imports; a path substitution after hashing is not an
# execution source. The host source file is itself in the protected receiver
# closure, while its manifest expectation comes from completion-launch.json.
STAGE_ENTRY_BOOTSTRAP = '''\
import base64, hashlib, importlib.abc, importlib.util, json, os, pathlib, re, stat, subprocess, sys
script = pathlib.Path(sys.argv[1])
stage, challenge, expected, *operation = sys.argv[2:]
preparing = len(operation) == 3 and operation[0] == 'prepare'
if (not sys.flags.isolated or not sys.dont_write_bytecode or
    stage not in ('configuration', 'initramfs') or
    script.name != 'canonical_'+stage+'_guest.py' or
    script.parent.name != 'lab' or not script.is_absolute() or
    re.fullmatch('[0-9A-F]{64}', expected) is None or
    re.fullmatch('[0-9A-F]{64}', challenge) is None or
    (operation and not preparing) or
    (preparing and (re.fullmatch('[0-9a-f]{32}', operation[1]) is None or
                    len(operation[2]) > 3072))):
    raise ValueError('StageGuestBootstrapArgumentsChanged')
if preparing:
    mountpoint = pathlib.Path('/opt/igloo-lab')
    if not mountpoint.exists():
        mountpoint.mkdir(mode=0o700)
    info = mountpoint.lstat()
    if (not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or
        info.st_mode & 0o022 or list(mountpoint.iterdir())):
        raise ValueError('StageGuestBootstrapMountpointChanged')
    subprocess.run(['mount', '-t', 'iso9660', '-o', 'ro,nodev,nosuid,noexec',
                    '/dev/sr0', str(mountpoint)], check=True, timeout=30,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    mounts = [line.split(' - ') for line in pathlib.Path('/proc/self/mountinfo').read_text().splitlines()]
    owned = [(left.split(), right.split()) for left, right in mounts
             if left.split()[4] == str(mountpoint)]
    if (len(owned) != 1 or owned[0][0][3:5] != ['/', str(mountpoint)] or
        owned[0][1][:2] != ['iso9660', '/dev/sr0'] or
        not {'ro', 'nodev', 'nosuid', 'noexec'}.issubset(
            set(owned[0][0][5].split(','))) or
        any(left.split()[4].startswith(str(mountpoint)+'/') for left, _ in mounts)):
        raise ValueError('StageGuestBootstrapToolsMountChanged')
flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
directory = os.open('/', flags)
try:
    for part in script.parent.parts[1:]:
        child = os.open(part, flags, dir_fd=directory)
        os.close(directory); directory = child
        info = os.fstat(directory)
        if info.st_uid != 0 or info.st_mode & 0o022:
            raise ValueError('StageGuestBootstrapParentUnprotected')
    def exact(name, limit):
        fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC,
                     dir_fd=directory)
        try:
            before = os.fstat(fd)
            if (not stat.S_ISREG(before.st_mode) or before.st_uid != 0 or
                before.st_nlink != 1 or before.st_mode & 0o022 or
                not 0 < before.st_size <= limit):
                raise ValueError('StageGuestBootstrapFileUnprotected')
            pieces = []
            left = before.st_size
            while left:
                part = os.read(fd, left)
                if not part:
                    raise ValueError('StageGuestBootstrapFileChanged')
                pieces.append(part); left -= len(part)
            after = os.fstat(fd)
            identity = lambda value: (value.st_dev, value.st_ino, value.st_mode,
                value.st_uid, value.st_gid, value.st_nlink, value.st_size,
                value.st_mtime_ns, value.st_ctime_ns)
            if identity(before) != identity(after):
                raise ValueError('StageGuestBootstrapFileChanged')
            return b''.join(pieces)
        finally:
            os.close(fd)
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('StageGuestBootstrapManifestDuplicate')
            result[key] = value
        return result
    manifest_raw = exact('guest-import-closure.json', 16 * 1024)
    if hashlib.sha256(manifest_raw).hexdigest().upper() != expected:
        raise ValueError('StageGuestBootstrapExpectationChanged')
    manifest = json.loads(manifest_raw, object_pairs_hook=unique)
    names = {'canonical_storage_guest.py', 'canonical_import_guest.py',
        'canonical_configuration_guest.py', 'canonical_stage_guest.py',
        'canonical_stage_files.py', 'canonical_configuration_readback.py',
        'canonical_initramfs_readback.py'}
    if stage == 'initramfs':
        names.add('canonical_initramfs_guest.py')
    if (type(manifest) is not dict or
        set(manifest) != {'Version', 'Stage', 'Entry', 'Files'} or
        type(manifest['Version']) is not int or manifest['Version'] != 1 or
        manifest['Stage'] != stage or manifest['Entry'] != script.name or
        type(manifest['Files']) is not dict or
        set(manifest['Files']) != names or
        set(os.listdir(directory)) != names | {'guest-import-closure.json'}):
        raise ValueError('StageGuestBootstrapManifestChanged')
    files = {name: exact(name, 2 * 1024 * 1024) for name in names}
    if any(type(manifest['Files'][name]) is not str or
           hashlib.sha256(files[name]).hexdigest().upper() != manifest['Files'][name]
           for name in names):
        raise ValueError('StageGuestBootstrapFileHashChanged')
finally:
    os.close(directory)
class FrozenStage(importlib.abc.MetaPathFinder, importlib.abc.Loader):
    def find_spec(self, fullname, path=None, target=None):
        if fullname+'.py' in files:
            return importlib.util.spec_from_loader(fullname, self,
                origin=str(script.parent/(fullname+'.py')))
    def create_module(self, spec):
        return None
    def exec_module(self, module):
        name = module.__name__+'.py'
        module.__file__ = str(script.parent/name)
        exec(compile(files[name], module.__file__, 'exec'), module.__dict__)
sys.meta_path.insert(0, FrozenStage())
if preparing:
    encoded = operation[2]
    data = base64.b64decode(encoded, validate=True)
    if (not 0 < len(data) <= 2304 or
        base64.b64encode(data).decode('ascii') != encoded or
        json.loads(data, object_pairs_hook=unique).get('RunId', '').replace('-', '') != operation[1]):
        raise ValueError('StageGuestBootstrapHostBindingChanged')
    host_path = pathlib.Path('/root/igloo-stage-host-binding-'+operation[1]+'.json')
    output = os.open(host_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(output, 'wb') as stream:
        stream.write(data); stream.flush(); os.fsync(stream.fileno())
    directory = os.open('/root', flags)
    try: os.fsync(directory)
    finally: os.close(directory)
    sys.argv = [str(script), '--prepare', challenge, expected, str(host_path)]
else:
    sys.argv = [str(script), '--execute-and-capture', challenge, expected]
exec(compile(files[script.name], str(script), 'exec'),
     {'__name__': '__main__', '__file__': str(script), '__package__': None})
'''


def stage_entry_command(stage, challenge, expected):
    """Closed host command; no guest-selected path, code or expected digest."""
    require(stage in ('configuration', 'initramfs') and
            type(challenge) is str and HEX.fullmatch(challenge) and
            type(expected) is str and HEX.fullmatch(expected),
            'CaptureGuestImportExpectationInvalid')
    entry = '/opt/igloo-lab/lab/canonical_'+stage+'_guest.py'
    # The interactive serial TTY is in canonical mode. A single Base64 line
    # longer than its input limit would be truncated before Python starts.
    # The quoted multi-line source is one -c argument, with each physical
    # input line independently below that limit.
    command = ('python3 -I -B -c '+shlex.quote(STAGE_ENTRY_BOOTSTRAP)+' '+
               entry+' '+stage+' '+challenge+' '+expected+
               '; echo IGLOO_CAPTURE_EXIT:$?\n')
    require(all(len(line.encode('ascii')) <= 4000 for line in command.splitlines()),
            'CaptureGuestCommandLineBound')
    return command


def stage_preparation_command(stage, challenge, expected, run_id, host_binding):
    """Fixed, single-use guest preparation through the same frozen import closure."""
    require(stage in ('configuration', 'initramfs') and
            type(challenge) is str and HEX.fullmatch(challenge) and
            type(expected) is str and HEX.fullmatch(expected) and
            type(run_id) is str and re.fullmatch(r'[0-9a-f]{32}', run_id) and
            type(host_binding) is bytes and 0 < len(host_binding) <= 2304,
            'StagePreparationInputChanged')
    encoded = base64.b64encode(host_binding).decode('ascii')
    require(len(encoded) <= 3072, 'StagePreparationInputOversized')
    entry = '/opt/igloo-lab/lab/canonical_'+stage+'_guest.py'
    command = ('python3 -I -B -c '+shlex.quote(STAGE_ENTRY_BOOTSTRAP)+' '+
               entry+' '+stage+' '+challenge+' '+expected+' prepare '+
               run_id+' '+encoded+'; echo IGLOO_PREPARE_EXIT:$?\n')
    require(all(len(line.encode('ascii')) <= 4000 for line in command.splitlines()),
            'StagePreparationCommandLineBound')
    return command

if __name__ == '__main__':
    # -I excludes caller-controlled Python paths. Verify and add only this
    # protected, per-operation closure before importing local modules.
    here = Path(__file__).absolute(); stage = here.parent
    if (here.name != 'canonical_userdata_acquisition.py' or stage.name != 'completion-tools' or
            stage.parent.parent != Path('/var/lib/igloo/userdata-authorizations')):
        raise ValueError('CaptureToolStageUnnominated')
    for path in (here, stage, *stage.parents):
        state = path.lstat()
        if stat.S_ISLNK(state.st_mode) or state.st_uid != 0 or state.st_mode & 0o022 or (
                path == here and (not stat.S_ISREG(state.st_mode) or state.st_nlink != 1)):
            raise ValueError('CaptureToolStageUnprotected')
    manifest_path = stage.parent/'completion-tools.json'; state = manifest_path.lstat()
    if not stat.S_ISREG(state.st_mode) or state.st_uid != 0 or state.st_nlink != 1 or state.st_mode & 0o022:
        raise ValueError('CaptureToolManifestUnprotected')
    manifest_raw = manifest_path.read_bytes()
    if len(manifest_raw) > 4096: raise ValueError('CaptureToolManifestOversized')
    def _unique_bootstrap(pairs):
        result = {}
        for key, value in pairs:
            if key in result: raise ValueError('CaptureToolManifestDuplicate')
            result[key] = value
        return result
    manifest = json.loads(manifest_raw, object_pairs_hook=_unique_bootstrap)
    if (set(manifest) != {'Version', 'Scope', 'Files'} or manifest['Version'] != 1 or
            manifest['Scope'] != 'ProtectedUserDataAcquisitionToolsV1' or
            set(manifest['Files']) != HOST_CLOSURE or
            {p.name for p in stage.iterdir()} != HOST_CLOSURE):
        raise ValueError('CaptureToolManifestChanged')
    for name, expected in manifest['Files'].items():
        path = stage/name; state = path.lstat()
        if (not stat.S_ISREG(state.st_mode) or state.st_uid != 0 or state.st_nlink != 1 or
                state.st_mode & 0o022 or
                hashlib.sha256(path.read_bytes()).hexdigest().upper() != expected):
            raise ValueError('CaptureToolBytesChanged')
    sys.path.insert(0, str(stage))

from canonical_configuration_derivation import closed, identity, require
from canonical_lab_runtime import checkpoint
from canonical_storage_lab import sha
from canonical_userdata_completion import digest, protected
from canonical_userdata_readback import verify as verify_terminal

CAPTURE_EXEC = frozenset(('host-binding.json', 'inventory.json', 'userdata.stdout',
    'userdata-exit.json', 'independent-handoff.json', 'preserved-before.json',
    'preserved-after.json', 'provisioning-journal-unmount.json'))
CAPTURE_FIXED = frozenset(('input/userdata-authorization.json', 'userdata-derivation.json'))
BRANCHES = ('input/initramfs-predecessor/', 'guest-evidence/journals/')
MAX_PACKET = 24 * 1024 * 1024
MAX_WIRE = 34 * 1024 * 1024
HEX = re.compile(r'[0-9A-F]{64}\Z')


def unique(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, 'CaptureDuplicateField')
        value[key] = item
    return value


def process_start_time(pid):
    raw = Path(f'/proc/{pid}/stat').read_text()
    return int(raw[raw.rfind(')') + 2:].split()[19])


def inspect_controlled_process(run, authorization):
    """Reopen root-custodied witnesses and independently inspect the live QEMU."""
    run, authorization = Path(run), Path(authorization)
    protected(authorization)
    parent = authorization.parent
    protected(parent, file=False)
    launch_path, process_path = parent/'completion-launch.json', parent/'completion-process.json'
    protected(launch_path); protected(process_path)
    protected(parent/'completion-tools.json')
    launch = json.loads(launch_path.read_bytes(), object_pairs_hook=unique)
    process = json.loads(process_path.read_bytes(), object_pairs_hook=unique)
    a = json.loads(authorization.read_bytes(), object_pairs_hook=unique)
    require(a['Version'] == 1 and a['Scope'] == 'OneInitramfsCheckpointSelectedDocuments' and
            set(launch) == {'Version', 'Scope', 'AuthorizationSha256', 'RunId', 'RunPath',
            'LaunchSha256', 'Arguments', 'Disks', 'ToolsSha256', 'ReceiverManifestSha256'} and
            launch['Version'] == 1 and launch['Scope'] == 'ControlledUserDataLaunchV1' and
            launch['AuthorizationSha256'] == sha(authorization) and
            sha(parent/'completion-tools.json') == launch['ReceiverManifestSha256'] and
            launch['RunId'] == a['AttemptId'] and launch['RunPath'] == str(run) and
            run.name == a['AttemptId'].replace('-', '') and
            set(process) == {'Version', 'Scope', 'Pid', 'StartTime', 'LaunchWitnessSha256'} and
            process['Version'] == 1 and process['Scope'] == 'ControlledUserDataProcessV1' and
            process['LaunchWitnessSha256'] == sha(launch_path), 'CaptureLaunchWitnessChanged')
    pid = process['Pid']
    require(type(pid) is int and pid > 1 and type(process['StartTime']) is int and
            process_start_time(pid) == process['StartTime'], 'CaptureProcessChanged')
    actual_args = Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')[:-1]
    require(actual_args == [s.encode() for s in launch['Arguments']] and
            launch['Arguments'][0] == '/usr/bin/qemu-system-x86_64' and
            '-runas' in launch['Arguments'] and
            launch['Arguments'][launch['Arguments'].index('-runas')+1] == 'gillesduif' and
            '-nic' in launch['Arguments'] and launch['Arguments'][launch['Arguments'].index('-nic')+1] == 'none' and
            f'unix:{run}/console.sock,server=on,wait=off' in launch['Arguments'],
            'CaptureLaunchChanged')
    expected = {str(run/name): 2 for name in launch['Disks']}
    expected[str(run/'tools.iso')] = 0
    found = {}
    for fd in Path(f'/proc/{pid}/fd').iterdir():
        state = fd.stat(); path = os.readlink(fd)
        require(not stat.S_ISBLK(state.st_mode), 'CapturePhysicalBlockFd')
        if not stat.S_ISREG(state.st_mode) or path == str(run/'qemu.stderr'):
            continue
        flags = next(s.split()[1] for s in Path(f'/proc/{pid}/fdinfo/{fd.name}').read_text().splitlines()
                     if s.startswith('flags:'))
        mode = int(flags, 8) & 3
        require(path in expected and mode == expected[path] and path not in found,
                'CaptureBackingFdChanged')
        found[path] = {'Device': state.st_dev, 'Inode': state.st_ino, 'Length': state.st_size}
    require(set(found) == set(expected) and
            all(found[str(run/name)] == binding for name, binding in launch['Disks'].items()) and
            sha(run/'tools.iso') == launch['ToolsSha256'], 'CaptureBackingIdentityChanged')
    return launch, process, found


class CaptureFrame:
    """One bounded line parser shared by saved transcripts and live reads."""
    def __init__(self, challenge):
        require(isinstance(challenge, str) and HEX.fullmatch(challenge) is not None,
                'CaptureWireBound')
        self.challenge = challenge
        self.pending = bytearray()
        self.size = 0
        self.begin = None
        self.body = []
        self.body_size = 0
        self.ended = False
        self.exited = False
        self.lines = 0
        self.last_line_offset = 0
        self.last_line_raw = b''
        self.rejected_line = False

    def feed(self, part):
        self.rejected_line = False
        self.size += len(part)
        if self.size > MAX_WIRE:
            self.last_line_raw = bytes(self.pending[:65536]) or part[:65536]
            self.rejected_line = True
            raise ValueError('CaptureWireBound')
        self.pending.extend(part)
        # A serial peer may deliver one byte per recv. No earlier complete
        # line remains in pending, so scanning/copying it on each byte is
        # unnecessary and made a full fixture millions of partial scans.
        if b'\n' not in part:
            self.last_line_offset = self.size - len(self.pending)
            if len(self.pending) > 65536:
                self.last_line_raw = bytes(self.pending[:65536])
                self.rejected_line = True
                raise ValueError('CaptureLineBound')
            return
        while True:
            end = self.pending.find(b'\n')
            if end < 0:
                self.last_line_offset = self.size - len(self.pending)
                if len(self.pending) > 65536:
                    self.last_line_raw = bytes(self.pending[:65536])
                    self.rejected_line = True
                    raise ValueError('CaptureLineBound')
                return
            self.last_line_offset = self.size - len(self.pending)
            self.last_line_raw = bytes(self.pending[:min(end+1, 65536)])
            self.rejected_line = True
            require(end <= 65536, 'CaptureLineBound')
            line = bytes(self.pending[:end]).replace(b'\r', b'')
            del self.pending[:end+1]
            self.lines += 1
            self._line(line)
            self.rejected_line = False

    def _line(self, line):
        if line.startswith(b'IGLOO_USERDATA_CAPTURE_BEGIN '):
            require(self.begin is None and not self.ended and not self.exited,
                    'CaptureReplayOrDuplicate')
            try: parts = line.decode('ascii').split()
            except UnicodeDecodeError: raise ValueError('CaptureHeaderChanged') from None
            require(len(parts) == 4 and parts[1] == self.challenge and parts[2].isdigit() and
                    0 < int(parts[2]) <= MAX_PACKET and HEX.fullmatch(parts[3]) is not None,
                    'CaptureHeaderChanged')
            self.begin = (int(parts[2]), parts[3]); return
        if line.startswith(b'IGLOO_DATA '):
            require(self.begin is not None, 'CaptureDataBeforeBegin')
            require(not self.ended, 'CaptureDataAfterEnd')
            require(11 < len(line) <= 4107, 'CaptureDataLineBound')
            self.body_size += len(line)-11
            require(self.body_size <= 4*((MAX_PACKET+2)//3), 'CaptureWireBound')
            self.body.append(line[11:]); return
        if line.startswith(b'IGLOO_USERDATA_CAPTURE_END '):
            require(self.begin is not None and not self.ended and line ==
                    ('IGLOO_USERDATA_CAPTURE_END '+self.challenge).encode(),
                    'CaptureEndChanged')
            self.ended = True; return
        # The serial shell may wrap its echoed fixed command so that the
        # literal, unexpanded argument appears at the start of a new line.
        # It is never a terminal status and cannot advance the frame state.
        if self.begin is None and line == b'IGLOO_CAPTURE_EXIT:$?':
            return
        if line.startswith(b'IGLOO_CAPTURE_EXIT:'):
            require(self.begin is not None and self.ended and not self.exited,
                    'CaptureExitOutOfOrder')
            require(re.fullmatch(rb'IGLOO_CAPTURE_EXIT:[0-9]+', line) is not None,
                    'CaptureExitChanged')
            require(line == b'IGLOO_CAPTURE_EXIT:0', 'CaptureGuestExitNonzero')
            self.exited = True; return
        if line.startswith(b'IGLOO_'):
            raise ValueError('CaptureUnknownRecord:begin=%d:end=%d:data=%d:line=%d' %
                             (self.begin is not None, self.ended, len(self.body), len(line)))
        if self.begin is not None and not self.ended and line:
            raise ValueError('CaptureUnknownRecord')

    def packet(self):
        require(self.begin is not None and self.ended and self.exited,
                'CaptureIncomplete')
        encoded_body = b''.join(self.body)
        try: packet = base64.b64decode(encoded_body, validate=True)
        except (ValueError, base64.binascii.Error):
            raise ValueError('CaptureEncodingChanged') from None
        require(base64.b64encode(packet) == encoded_body, 'CaptureEncodingChanged')
        require(len(packet) == self.begin[0] and digest(packet) == self.begin[1],
                'CapturePacketChanged')
        return packet


def parse_frame(transcript, challenge):
    """Parse one bounded serial frame, not a saved export or arbitrary JSON."""
    frame = CaptureFrame(challenge)
    frame.feed(transcript)
    return frame.packet()


class ReceiveDiagnostics:
    """Protected, bounded raw observation; never a completion or authority."""
    def __init__(self, parent, identities):
        parent = Path(parent)
        protected(parent, file=False)
        self.path = parent/'receive-diagnostics'
        require(not self.path.exists(), 'CaptureDiagnosticsReplay')
        self.path.mkdir(mode=0o700)
        self.fd = os.open(self.path/'incoming.raw',
                          os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        self.started = time.monotonic()
        self.total = 0
        self.count = 0
        self.buffer = bytearray()
        self.written = 0
        self.durable = 0
        self.bytes_since_sync = 0
        self.sync_count = 0
        self.write_count = 0
        self.io_nanoseconds = 0
        self.last_sync = self.started
        self.chunk_sizes = {'1': 0, '2-15': 0, '16-255': 0,
                            '256-4095': 0, '4096+': 0}
        self.first = []
        self.last = []
        self.identities = identities
        self.finished = False
        self.begin_milliseconds = None
        self.end_milliseconds = None
        self.exit_milliseconds = None
        self.last_progress_milliseconds = None
        self.max_progress_gap_milliseconds = 0
        self.cpu_started = time.process_time_ns()

    def _flush(self, sync=False):
        started = time.perf_counter_ns()
        if self.buffer:
            view = memoryview(self.buffer)
            offset = 0
            try:
                while offset < len(view):
                    count = os.write(self.fd, view[offset:])
                    require(count > 0, 'CaptureDiagnosticsShortWrite')
                    offset += count
                    self.write_count += 1
            finally:
                view.release()
            self.written += offset
            self.bytes_since_sync += offset
            self.buffer.clear()
        if sync:
            os.fsync(self.fd)
            self.durable = self.written
            self.bytes_since_sync = 0
            self.sync_count += 1
            self.last_sync = time.monotonic()
        self.io_nanoseconds += time.perf_counter_ns() - started

    def record(self, part):
        available = max(0, MAX_WIRE-self.total)
        raw = part[:available]
        if raw:
            self.buffer.extend(raw)
            if len(self.buffer) >= 65536:
                self._flush(sync=self.bytes_since_sync + len(self.buffer) >= 1024*1024)
        self.total += len(raw)
        self.count += 1
        size = len(part)
        bucket = '1' if size == 1 else ('2-15' if size < 16 else
                 ('16-255' if size < 256 else ('256-4095' if size < 4096 else '4096+')))
        self.chunk_sizes[bucket] += 1
        # Sample at most one in 4096 fragments after the first 64. Counts
        # remain exact while timing metadata stays bounded on tiny reads.
        if len(self.first) < 64 or self.count % 4096 == 0:
            now = time.monotonic()
            item = {'Index': self.count, 'Length': size,
                    'ElapsedMilliseconds': int((now-self.started)*1000)}
            if len(self.first) < 64: self.first.append(item)
            else:
                self.last.append(item)
                if len(self.last) > 64: self.last.pop(0)
            if self.buffer and now-self.last_sync >= 1:
                self._flush(sync=True)

    def finish(self, frame, outcome, error=None):
        require(not self.finished, 'CaptureDiagnosticsReplay')
        self.finished = True
        self._flush(sync=True)
        os.close(self.fd)
        if error is not None:
            raw = (frame.last_line_raw[:65536] if frame.rejected_line else
                   bytes(frame.pending[:65536]) if frame.pending else
                   frame.last_line_raw[:65536])
            fd = os.open(self.path/'offending-line.raw',
                         os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            try:
                require(os.write(fd, raw) == len(raw), 'CaptureDiagnosticsShortWrite')
                os.fsync(fd)
            finally: os.close(fd)
        checkpoint(self.path, 'observation.json', {
            'Version': 1, 'Scope': 'UntrustedControlledReceiveDiagnosticV1',
            'Outcome': outcome,
            'Code': str(error).split(':', 1)[0] if error is not None else None,
            'ReceiverSha256': sha(Path(__file__)),
            'ExecutedIdentities': self.identities,
            'RawBytes': self.total, 'RawSha256': sha(self.path/'incoming.raw'),
            'DurablePrefixBytes': self.durable, 'WriteCalls': self.write_count,
            'FsyncCalls': self.sync_count,
            'DiagnosticIoMilliseconds': self.io_nanoseconds // 1000000,
            'ReceiverCpuMilliseconds': (time.process_time_ns()-self.cpu_started) // 1000000,
            'ElapsedMilliseconds': int((time.monotonic()-self.started)*1000),
            'ChunkCount': self.count, 'ChunkSizes': self.chunk_sizes,
            'FirstChunks': self.first, 'LastChunks': self.last,
            'BeginSeen': frame.begin is not None, 'EndSeen': frame.ended,
            'ZeroExitSeen': frame.exited, 'DataLines': len(frame.body),
            'ExpectedBase64Bytes': 4*((frame.begin[0]+2)//3) if frame.begin else None,
            'AcceptedBase64Bytes': frame.body_size,
            'BeginMilliseconds': self.begin_milliseconds,
            'EndMilliseconds': self.end_milliseconds,
            'ExitMilliseconds': self.exit_milliseconds,
            'LastValidProgressMilliseconds': self.last_progress_milliseconds,
            'MaxValidProgressGapMilliseconds': self.max_progress_gap_milliseconds,
            'ParserLines': frame.lines, 'ParserBytes': frame.size,
            'OffendingOffset': frame.last_line_offset if error is not None else None,
            'OffendingBytes': len(frame.last_line_raw) if error is not None else None,
            'OffendingSha256': sha(self.path/'offending-line.raw') if error is not None else None,
            'CompletionAuthority': False})
        protected(self.path, file=False)


def _allowed(name):
    if name in CAPTURE_FIXED or name.startswith('guest-evidence/execution/') and \
            name.removeprefix('guest-evidence/execution/') in CAPTURE_EXEC:
        return True
    return any(name.startswith(prefix) and len(name) > len(prefix) for prefix in BRANCHES)


def accept_packet(packet, challenge, run, authorization, pending, launch, process, found):
    """Commit only a bounded raw capture into protected pending custody."""
    run, authorization, pending = Path(run), Path(authorization), Path(pending)
    require(len(packet) <= MAX_PACKET and not pending.exists(), 'CapturePendingExists')
    protected(pending.parent, file=False)
    value = json.loads(packet, object_pairs_hook=unique)
    require(set(value) == {'Version', 'Scope', 'Challenge', 'RunId', 'GenerationId',
            'OperationId', 'SessionId', 'PlanSha256', 'ResultSha256', 'CloseSha256',
            'HostBindingSha256', 'Files'} and
            value['Version'] == 1 and value['Scope'] == 'ControlledGuestUserDataCaptureV1' and
            value['Challenge'] == challenge and value['RunId'] == launch['RunId'] and
            all(isinstance(value[k], str) and HEX.fullmatch(value[k]) for k in
                ('PlanSha256', 'ResultSha256', 'CloseSha256', 'HostBindingSha256')) and
            type(value['Files']) is list and 5 <= len(value['Files']) <= 4096,
            'CaptureSchemaChanged')
    auth = json.loads(authorization.read_bytes(), object_pairs_hook=unique)
    require(value['GenerationId'] == auth['GenerationId'] and
            value['OperationId'] == auth['OperationId'], 'CaptureOperationChanged')
    files = {}; total = 0
    for item in value['Files']:
        require(set(item) == {'Path', 'Length', 'Sha256', 'Bytes'} and
                isinstance(item['Path'], str) and _allowed(item['Path']) and
                all(re.fullmatch(r'[A-Za-z0-9_.-]+', part) for part in item['Path'].split('/')) and
                not any(part in ('', '.', '..') for part in item['Path'].split('/')) and
                item['Path'] not in files and type(item['Length']) is int and
                0 <= item['Length'] <= 16*1024*1024 and HEX.fullmatch(item['Sha256']) is not None,
                'CaptureInventoryChanged')
        raw = base64.b64decode(item['Bytes'], validate=True); total += len(raw)
        require(base64.b64encode(raw).decode('ascii') == item['Bytes'], 'CaptureEncodingChanged')
        require(total <= 16*1024*1024 and len(raw) == item['Length'] and
                digest(raw) == item['Sha256'], 'CaptureRawBytesChanged')
        files[item['Path']] = raw
    require(CAPTURE_FIXED.issubset(files) and
            {'guest-evidence/execution/'+name for name in CAPTURE_EXEC}.issubset(files) and
            any(name.startswith(BRANCHES[0]) for name in files) and
            any(name.startswith(BRANCHES[1]) for name in files) and
            files['input/userdata-authorization.json'] == authorization.read_bytes() and
            digest(files['guest-evidence/execution/host-binding.json']) == value['HostBindingSha256'],
            'CaptureRequiredRecordChanged')
    host = json.loads(files['guest-evidence/execution/host-binding.json'], object_pairs_hook=unique)
    require(host['RunId'] == value['RunId'] and
            all(any(b['Serial'] == serial and b['HostDevice'] == found[str(run/name)]['Device'] and
                    b['HostInode'] == found[str(run/name)]['Inode'] and
                    b['Length'] == found[str(run/name)]['Length'] and
                    b['LaunchSha256'] == launch['LaunchSha256']
                    for b in host['ReopenedBackings'])
                for name, serial in (('runtime.raw', 'IGLOO-GPT-RUNTIME'),
                                     ('target.raw', 'IGLOO-LAB-TARGET'),
                                     ('journal.raw', 'IGLOO-LAB-JOURNAL'))),
            'CaptureHostBindingChanged')
    # The only write destination is host-selected and protected. A guest path
    # in the packet can never select a host file.
    pending.mkdir(mode=0o700)
    parent_fd = os.open(pending.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try: os.fsync(parent_fd)
    finally: os.close(parent_fd)
    packet_fd = os.open(pending/'packet.bin', os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(packet_fd, 'wb') as out:
        out.write(packet); out.flush(); os.fsync(out.fileno())
    raw_root = pending/'raw'; raw_root.mkdir(mode=0o700)
    for name, data in sorted(files.items()):
        path = raw_root/name; path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'wb') as out:
            out.write(data); out.flush(); os.fsync(out.fileno())
    for path in sorted((p for p in pending.rglob('*') if p.is_dir()), key=lambda p: len(p.parts), reverse=True):
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    observed = verify_terminal(raw_root/'guest-evidence/execution', raw_root/'guest-evidence/journals')
    require(observed['UserData'] == observed['Teardown'] == 'AppliedAndVerified' and
            observed['OperationId'] == value['OperationId'] and
            observed['SessionId'] == value['SessionId'] and
            observed['PlanSha256'] == value['PlanSha256'] and
            observed['EffectRecords'][-1]['Sha256'] == value['ResultSha256'] and
            observed['SessionRecords'][-1]['Sha256'] == value['CloseSha256'] and
            json.loads(files['guest-evidence/execution/userdata-exit.json'])['ExitCode'] == 0,
            'CaptureTerminalChanged')
    checkpoint(pending, 'capture.json', {'Version': 1, 'Scope': 'PendingControlledUserDataCaptureV1',
        'RunId': value['RunId'], 'GenerationId': value['GenerationId'],
        'OperationId': value['OperationId'], 'SessionId': value['SessionId'],
        'ResultSha256': value['ResultSha256'], 'CloseSha256': value['CloseSha256'],
        'PacketSha256': digest(packet), 'LaunchWitnessSha256': sha(authorization.parent/'completion-launch.json'),
        'ProcessWitnessSha256': sha(authorization.parent/'completion-process.json'),
        'Files': {name: digest(raw) for name, raw in files.items()},
        'Status': 'PendingBackingReadback'})
    protected(pending, file=False)
    require(json.loads((pending/'capture.json').read_bytes())['Files'] ==
            {name: digest((raw_root/name).read_bytes()) for name in files}, 'CaptureReopenChanged')
    return pending


def read_controlled_channel(channel, challenge, process, deadline_seconds, *, fixture=False,
                            stage='userdata',
                            guest_import_closure_sha256=None,
                            root_created_unprivileged_uid=None,
                            diagnostic_parent=None, executed_identities=None,
                            begin_timeout_seconds=None, inactivity_timeout_seconds=None,
                            terminal_timeout_seconds=None, clock=None,
                            inactivity_after_begin_only=False):
    """The same peer-bound serial exchange is used by process fixtures."""
    pid, uid, _ = struct.unpack('3i', channel.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
    current_uid = os.stat(f'/proc/{pid}').st_uid
    # QEMU creates the serial listener before -runas drops from root. Linux
    # SO_PEERCRED reports the listener's creation credentials, while /proc
    # reports the current unprivileged UID. PID and start time still join the
    # peer to the inspected, exact-argv QEMU process.
    if root_created_unprivileged_uid is None:
        peer_uid_valid = uid == current_uid
    else:
        peer_uid_valid = (type(root_created_unprivileged_uid) is int and
                          root_created_unprivileged_uid != 0 and
                          current_uid == root_created_unprivileged_uid and
                          uid in (0, root_created_unprivileged_uid))
    require(pid == process['Pid'] and peer_uid_valid and
            process_start_time(pid) == process['StartTime'], 'CapturePeerChanged')
    require(stage in ('userdata', 'configuration', 'initramfs') and
            (not fixture or stage == 'userdata'), 'CaptureStageUnsupported')
    require((stage == 'userdata' and guest_import_closure_sha256 is None) or
            (stage in ('configuration', 'initramfs') and
             type(guest_import_closure_sha256) is str and
             re.fullmatch(r'[0-9A-F]{64}', guest_import_closure_sha256)),
            'CaptureGuestImportExpectationInvalid')
    if fixture:
        command = ('mkdir -p /mnt/igloo-live-fixture && '
                   'mount -t iso9660 -o ro,nosuid,nodev,noexec /dev/sr0 /mnt/igloo-live-fixture && '
                   'python3 -I -B /mnt/igloo-live-fixture/canonical_userdata_guest.py '
                   '--emit-fixture-capture '+challenge+'; echo IGLOO_CAPTURE_EXIT:$?\n')
    elif stage == 'userdata':
        command = ('python3 -I -B /opt/igloo-userdata-canonical/lab/canonical_userdata_guest.py '
                   '--execute-and-capture '+challenge+'; echo IGLOO_CAPTURE_EXIT:$?\n')
    else:
        # The digest comes from the protected launch witness, created before
        # QEMU takes the working directory. It is never read from the guest.
        command = stage_entry_command(stage, challenge,
                                      guest_import_closure_sha256)
    frame = CaptureFrame(challenge)
    diagnostics = (ReceiveDiagnostics(diagnostic_parent, executed_identities or {})
                   if diagnostic_parent is not None else None)
    clock = clock or time.monotonic
    require(deadline_seconds > 0 and type(inactivity_after_begin_only) is bool and
            all(value is None or value > 0 for value in
                (begin_timeout_seconds, inactivity_timeout_seconds,
                 terminal_timeout_seconds)), 'CaptureDeadlineInvalid')
    try:
        channel.sendall(command.encode('ascii'))
        started = clock()
        end = started + deadline_seconds
        begin_end = started + begin_timeout_seconds if begin_timeout_seconds else None
        last_progress = started
        terminal_end = None
        chunks_since_check = 0
        while True:
            if chunks_since_check == 0:
                now = clock()
                if now >= end: raise ValueError('CaptureTimeout')
                if begin_end is not None and frame.begin is None and now >= begin_end:
                    raise ValueError('CaptureBeginTimeout')
                if terminal_end is not None and now >= terminal_end:
                    raise ValueError('CaptureTerminalTimeout')
                progress_bound_active = (inactivity_timeout_seconds is not None and
                    (not inactivity_after_begin_only or frame.begin is not None))
                if progress_bound_active and now-last_progress >= inactivity_timeout_seconds:
                    raise ValueError('CaptureInactivityTimeout')
                limits = [end]
                if frame.begin is None and begin_end is not None: limits.append(begin_end)
                if terminal_end is not None: limits.append(terminal_end)
                if progress_bound_active:
                    limits.append(last_progress+inactivity_timeout_seconds)
                channel.settimeout(min(2, max(.01, min(limits)-now)))
            try: part = channel.recv(65536)
            except socket.timeout:
                chunks_since_check = 0
                continue
            require(part, 'CaptureChannelClosed')
            if diagnostics is not None: diagnostics.record(part)
            completed_line = b'\n' in part
            if completed_line:
                before = (frame.begin is not None, len(frame.body), frame.ended)
            frame.feed(part)
            if completed_line:
                after = (frame.begin is not None, len(frame.body), frame.ended)
            if completed_line and after != before:
                now = clock()
                if diagnostics is not None:
                    diagnostics.max_progress_gap_milliseconds = max(
                        diagnostics.max_progress_gap_milliseconds,
                        int((now-last_progress)*1000))
                    diagnostics.last_progress_milliseconds = int((now-started)*1000)
                    if not before[0] and after[0]:
                        diagnostics.begin_milliseconds = int((now-started)*1000)
                    if not before[2] and after[2]:
                        diagnostics.end_milliseconds = int((now-started)*1000)
                last_progress = now
                if not before[2] and after[2] and terminal_timeout_seconds is not None:
                    terminal_end = last_progress + terminal_timeout_seconds
            if frame.exited:
                packet = frame.packet()
                if diagnostics is not None:
                    diagnostics.exit_milliseconds = int((clock()-started)*1000)
                    diagnostics.finish(frame, 'CompleteFrame')
                return packet
            chunks_since_check = (chunks_since_check+1) % 256
    except Exception as error:
        if diagnostics is not None and not diagnostics.finished:
            diagnostics.finish(frame, 'Failed', error)
        raise


def receive_live(run, authorization, pending, *, deadline_seconds=5*3600+600):
    """Dispatch once and capture over the nominated live serial channel."""
    run = Path(run)
    require(not Path(pending).exists(), 'CapturePendingExists')
    launch, process, found = inspect_controlled_process(run, authorization)
    challenge = os.urandom(32).hex().upper()
    console = run/'console.sock'
    require(stat.S_ISSOCK(console.lstat().st_mode) and not console.is_symlink(),
            'CaptureConsoleChanged')
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
        channel.settimeout(5); channel.connect(str(console))
        packet = read_controlled_channel(channel, challenge, process, deadline_seconds,
            root_created_unprivileged_uid=pwd.getpwnam('gillesduif').pw_uid,
            diagnostic_parent=Path(authorization).parent,
            executed_identities={'LaunchWitnessSha256': sha(Path(authorization).parent/
                                                           'completion-launch.json')})
    # Recheck the same process and descriptors after the complete transfer.
    require(inspect_controlled_process(run, authorization)[2] == found,
            'CaptureBackingChangedDuringTransfer')
    return accept_packet(packet, challenge, run, authorization, pending, launch, process, found)


def finalize_retention(run, authorization, pending):
    """Compute anchor OUTPUTS only after writer exclusion and stable backing readback."""
    run, authorization, pending = map(Path, (run, authorization, pending))
    protected(authorization); protected(pending, file=False)
    capture_path = pending/'capture.json'; protected(capture_path)
    protected(pending/'packet.bin')
    capture = json.loads(capture_path.read_bytes(), object_pairs_hook=unique)
    require(capture['Status'] == 'PendingBackingReadback' and
            capture['RunId'] == json.loads(authorization.read_bytes())['AttemptId'] and
            sha(pending/'packet.bin') == capture['PacketSha256'] and
            not (pending/'anchor.json').exists(), 'CaptureFinalizationChanged')
    launch_path = authorization.parent/'completion-launch.json'
    process_path = authorization.parent/'completion-process.json'
    protected(launch_path); protected(process_path)
    require(sha(launch_path) == capture['LaunchWitnessSha256'] and
            sha(process_path) == capture['ProcessWitnessSha256'], 'CaptureWitnessChanged')
    launch = json.loads(launch_path.read_bytes())
    pid = json.loads(process_path.read_bytes())['Pid']
    require(not Path(f'/proc/{pid}/cmdline').exists() or
            not Path(f'/proc/{pid}/cmdline').read_bytes(), 'CaptureWriterStillRunning')
    names = ('runtime.raw', 'target.raw', 'journal.raw')
    closed([run/name for name in names])
    before = {name: identity(run/name) for name in names}
    before_meta = {name: ((run/name).stat().st_mtime_ns, (run/name).stat().st_ctime_ns)
                   for name in names}
    require(all(before[name] == (binding['Device'], binding['Inode'], binding['Length']) and
                (run/name).stat().st_nlink == 1 for name, binding in launch['Disks'].items()),
            'CaptureRetainedBackingChanged')
    hashes = {name: sha(run/name) for name in names}
    closed([run/name for name in names])
    require({name: identity(run/name) for name in names} == before and
            {name: ((run/name).stat().st_mtime_ns, (run/name).stat().st_ctime_ns)
             for name in names} == before_meta,
            'CaptureBackingChangedDuringHash')
    raw = pending/'raw'; observed = verify_terminal(raw/'guest-evidence/execution', raw/'guest-evidence/journals')
    require(observed['EffectRecords'][-1]['Sha256'] == capture['ResultSha256'] and
            observed['SessionRecords'][-1]['Sha256'] == capture['CloseSha256'], 'CaptureTerminalChanged')
    from canonical_userdata_completion import LIVE_FILES
    expected_capture = {**capture['Files']}
    require(set(expected_capture) == set(p.relative_to(raw).as_posix() for p in raw.rglob('*') if p.is_file()) and
            all(sha(raw/name) == expected for name, expected in expected_capture.items()) and
            set(LIVE_FILES).issubset(expected_capture), 'CaptureRawReopenChanged')
    retained = {'RunId': capture['RunId'], 'OperationId': capture['OperationId'],
        'Scope': 'OneInitramfsCheckpointSelectedDocuments', 'SchemaVersion': 1,
        'CanonicalUserData': 'AppliedAndVerified', 'CanonicalTeardown': 'AppliedAndVerified',
        'CloseReference': observed['SessionRecords'][-1],
        'IndependentHandoffSha256': digest((raw/'guest-evidence/execution/independent-handoff.json').read_bytes()),
        'Backings': {name: {'Device': before[name][0], 'Inode': before[name][1],
            'Length': before[name][2], 'Links': 1, 'Sha256': hashes[name]} for name in names}}
    checkpoint(raw, 'userdata-retention.json', retained)
    checkpoint(raw, 'userdata-independent-host-readback.json', {'Verified': observed,
        'CaptureSha256': sha(capture_path), 'WriterExcluded': True})
    from canonical_userdata_completion import _files
    inventory = {name: sha(raw/name) for name in _files(raw)}
    anchor = {'Version': 2, 'Scope': 'TrustedLiveUserDataTerminalV2',
        'SourceRunId': capture['RunId'], 'GenerationId': capture['GenerationId'],
        'OperationId': capture['OperationId'], 'SessionId': capture['SessionId'],
        'ResultSha256': capture['ResultSha256'], 'CloseSha256': capture['CloseSha256'],
        'RetentionSha256': sha(raw/'userdata-retention.json'),
        'TargetSha256': hashes['target.raw'], 'JournalSha256': hashes['journal.raw'],
        'CaptureSha256': sha(capture_path), 'Files': inventory}
    checkpoint(pending, 'anchor.json', anchor)
    protected(pending/'anchor.json')
    require(json.loads((pending/'anchor.json').read_bytes(), object_pairs_hook=unique) == anchor,
            'CaptureAnchorReopenChanged')
    return pending/'anchor.json'


def main(argv):
    """Closed host continuation after the existing guest UserData execute step.

    Receive is invoked while the controlled guest still runs; finalize is a
    separate post-shutdown operation. Neither command issues later agent-stage
    authorization or publishes a successor-ready result on its own.
    """
    require(len(argv) == 3 and argv[0] in ('receive', 'finalize'), 'CaptureCommandUnsupported')
    action, run_arg, authorization_arg = argv
    run, authorization = Path(run_arg), Path(authorization_arg)
    require(run.parent == Path('/var/lib/igloo/initramfs-helper-labs') and
            authorization.parent.parent == Path('/var/lib/igloo/userdata-authorizations') and
            authorization.name == 'authorization.json' and
            Path(__file__).absolute().parent.parent == authorization.parent and
            run.name == authorization.parent.name.replace('-', ''),
            'CaptureHostPathUnnominated')
    parent = Path('/var/lib/igloo/userdata-completions')
    if not parent.exists():
        protected(parent.parent, file=False)
        parent.mkdir(mode=0o700)
        fd = os.open(parent.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    protected(parent, file=False)
    pending = parent/run.name
    if action == 'receive':
        receive_live(run, authorization, pending)
        print(json.dumps({'Status': 'PendingBackingReadback', 'RunId': authorization.parent.name},
                         sort_keys=True))
    else:
        anchor = finalize_retention(run, authorization, pending)
        print(json.dumps({'Status': 'ProtectedAnchorProduced', 'AnchorSha256': sha(anchor)},
                         sort_keys=True))


if __name__ == '__main__':
    import sys
    main(sys.argv[1:])
