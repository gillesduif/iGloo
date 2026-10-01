"""Explicit host-side disposable runtime construction, never a migration entrypoint.

The retained factory is only a read-only backing of a NEW construction overlay.
Final qualification must boot the NEW GPT runtime alone, never this MBR builder.
No artifact is rebuilt. No block device is opened by this program.
"""
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import urllib.request
import uuid

RUNTIME_URL = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.20/dotnet-runtime-8.0.20-linux-x64.tar.gz'
RUNTIME_SHA512 = '4b801177e009ec710d5e2511500aa60bec95811add50fdc59b00a5c486840c66f00c48f272cf7a2f8129ed158ec5b887d1dd60594e612f749ba48fcf48af9cd2'
BASE = Path('/var/lib/igloo/factory-evidence/99695826-82bc-4077-b4cf-a22cebc706da')


def checkpoint(directory, name, value):
    data = json.dumps(value, sort_keys=True, indent=2).encode()
    with (directory / name).open('xb') as out:
        out.write(data); out.flush(); os.fsync(out.fileno())
    fd = os.open(directory, os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
    if (directory / name).read_bytes() != data:
        raise OSError('CheckpointReopenChanged')


def observe_construction(run):
    declaration = json.loads((run / 'construction-intent.json').read_bytes())
    pid = json.loads((run / 'construction-process.json').read_bytes())['Pid']
    actual = Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')[:-1]
    if actual != [s.encode() for s in declaration['Arguments']]:
        raise ValueError('LaunchChanged')
    allowed = {str(run / 'builder.qcow2'): 2, str(run / 'runtime.raw'): 2,
        str(run / 'tools.iso'): 0,
        str(BASE / 'neutralization-9f5101de-4b25-44d5-8ade-a18c4e371408/derived.qcow2'): 0,
        str(BASE / 'factory/factory.raw'): 0}
    found = {}
    for fd in Path(f'/proc/{pid}/fd').iterdir():
        st = fd.stat(); path = os.readlink(fd)
        if stat.S_ISBLK(st.st_mode): raise ValueError('PhysicalBlockFd')
        if not stat.S_ISREG(st.st_mode): continue
        if path == str(run / 'construction.stderr'): continue
        mode = int(next(s.split()[1] for s in Path(f'/proc/{pid}/fdinfo/{fd.name}').read_text().splitlines()
                        if s.startswith('flags:')), 8) & 3
        if path not in allowed or allowed[path] != mode: raise ValueError('UnexpectedRegularFd:' + path)
        if path in found: raise ValueError('DuplicateBackingFd')
        found[path] = {'Mode': mode, 'Device': st.st_dev, 'Inode': st.st_ino, 'Length': st.st_size}
    if set(found) != set(allowed): raise ValueError('BackingFdUnavailable')
    for name in ('runtime.raw',):
        if [found[str(run / name)]['Inode'], found[str(run / name)]['Length']] != declaration['NewDisks'][name]:
            raise ValueError('NewStorageSubstituted')
    checkpoint(run, 'construction-fd-readback.json', {'Pid': pid, 'Fds': found, 'PhysicalBlockFds': [],
        'LaunchSha256': hashlib.sha256((run / 'construction-intent.json').read_bytes()).hexdigest().upper()})


def start_gpt(run):
    pid = json.loads((run / 'construction-process.json').read_bytes())['Pid']
    if Path(f'/proc/{pid}/cmdline').exists() and Path(f'/proc/{pid}/cmdline').read_bytes():
        raise ValueError('ConstructionStillRunning')
    observed = json.loads((run / 'guest-construction-readback.json').read_bytes())
    part = observed['runtime-copy.json']['RootPartitionGuid']
    checkpoint(run, 'construction-stopped.json', {'Pid': pid, 'NormalShutdownRequested': True,
        'ProcessNoLongerRunning': True})
    # Sparse READ-ONLY copy of the new runtime's EXT4 partition for debugfs export.
    # No loop/NBD/host block device or mounting, and no original backing access.
    offset = 2048 * 512; length = 50327552 * 512
    source = os.open(run / 'runtime.raw', os.O_RDONLY | os.O_NOFOLLOW)
    target = os.open(run / 'runtime-filesystem-copy.raw', os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        os.ftruncate(target, length)
        pos = offset
        while pos < offset + length:
            try: data_start = os.lseek(source, pos, os.SEEK_DATA)
            except OSError as error:
                import errno
                if error.errno == errno.ENXIO: break
                raise
            data_end = min(os.lseek(source, data_start, os.SEEK_HOLE), offset + length)
            pos = max(data_start, offset)
            while pos < data_end:
                data = os.pread(source, min(1024 * 1024, data_end - pos), pos)
                if not data: raise OSError('RuntimeCopyShortRead')
                if os.pwrite(target, data, pos - offset) != len(data): raise OSError('RuntimeCopyShortWrite')
                pos += len(data)
            if data_start >= offset + length: break
        os.fsync(target)
    finally:
        os.close(source); os.close(target)
    version = '6.12.107+deb13-amd64'
    for src, dst in [(f'/boot/vmlinuz-{version}', 'kernel'), (f'/boot/initrd.img-{version}', 'initrd')]:
        subprocess.run(['debugfs', '-R', f'dump {src} {run / dst}', str(run / 'runtime-filesystem-copy.raw')], check=True,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if not (run / dst).is_file() or (run / dst).stat().st_size == 0: raise ValueError('BootExportMissing')
    args = ['/usr/bin/qemu-system-x86_64', '-no-user-config', '-nodefaults', '-machine', 'q35,accel=kvm',
        '-cpu', 'host', '-smp', '4', '-m', '8192', '-display', 'none', '-nic', 'none', '-monitor', 'none',
        '-no-reboot', '-sandbox', 'on,obsolete=deny,elevateprivileges=allow,spawn=deny,resourcecontrol=deny',
        '-runas', 'gillesduif', '-serial', 'unix:' + str(run / 'gpt-console.sock') + ',server=on,wait=off',
        '-kernel', str(run / 'kernel'), '-initrd', str(run / 'initrd'),
        '-append', f'root=PARTUUID={part} ro console=ttyS0,115200n8 noresume']
    for name, serial in [('runtime', 'IGLOO-GPT-RUNTIME'), ('target', 'IGLOO-LAB-TARGET'), ('journal', 'IGLOO-LAB-JOURNAL')]:
        args += ['-drive', f'file={run}/{name}.raw,if=none,id={name},format=raw,cache=none',
                 '-device', f'virtio-blk-pci,drive={name},serial={serial}']
    checkpoint(run, 'gpt-launch-intent.json', {'Arguments': args,
        'BootFiles': {name: hashlib.sha256((run / name).read_bytes()).hexdigest().upper() for name in ('kernel', 'initrd')},
        'Import': 'NotStarted'})
    with (run / 'gpt.stderr').open('xb') as log:
        process = subprocess.Popen(args, stdout=log, stderr=log, start_new_session=True)
    checkpoint(run, 'gpt-process.json', {'Pid': process.pid})


def observe_gpt(run, phase='gpt'):
    if phase not in ('gpt', 'dependency'): raise ValueError('UnknownRuntimePhase')
    pid = json.loads((run / (phase + '-process.json')).read_bytes())['Pid']
    declaration = json.loads((run / (phase + '-launch-intent.json')).read_bytes())
    if Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')[:-1] != [s.encode() for s in declaration['Arguments']]:
        raise ValueError('LaunchChanged')
    creation = json.loads((run / 'construction-intent.json').read_bytes())['NewDisks']
    found = {}
    for fd in Path(f'/proc/{pid}/fd').iterdir():
        st = fd.stat(); path = os.readlink(fd)
        if stat.S_ISBLK(st.st_mode): raise ValueError('PhysicalBlockFd')
        if not stat.S_ISREG(st.st_mode) or path == str(run / (phase + '.stderr')): continue
        name = Path(path).name
        mode = int(next(s.split()[1] for s in Path(f'/proc/{pid}/fdinfo/{fd.name}').read_text().splitlines()
                        if s.startswith('flags:')), 8) & 3
        if phase == 'dependency' and path == str(run / 'dependency.iso'):
            if mode != 0 or hashlib.sha256((run / 'dependency.iso').read_bytes()).hexdigest().upper() != declaration['IsoSha256']:
                raise ValueError('RuntimeInputChanged')
            continue
        if path != str(run / name) or name not in creation or name in found:
            raise ValueError('UnexpectedStorageFd')
        if mode != 2 or [st.st_ino, st.st_size] != creation[name]: raise ValueError('BackingIdentityChanged')
        found[name] = {'HostDevice': st.st_dev, 'HostInode': st.st_ino, 'Length': st.st_size, 'Access': 'ReadWrite'}
    if set(found) != set(creation): raise ValueError('StorageFdUnavailable')
    checkpoint(run, phase + '-host-readback.json', {'LabRunId': run.name, 'Pid': pid, 'Disks': found,
        'LaunchSha256': hashlib.sha256((run / (phase + '-launch-intent.json')).read_bytes()).hexdigest().upper(),
        'CreationSha256': hashlib.sha256((run / 'construction-intent.json').read_bytes()).hexdigest().upper(),
        'PhysicalBlockFds': [], 'SourceDisksAttached': False})


def create(repo, publish):
    if os.geteuid() != 0:
        raise PermissionError('Explicit isolated Linux lab host required')
    run = Path('/var/lib/igloo/canonical-labs') / uuid.uuid4().hex
    run.mkdir(parents=True, mode=0o700)
    inputs = run / 'inputs'; inputs.mkdir()
    # Acquisition happens before the offline VM, from a fixed official release.
    with urllib.request.urlopen(RUNTIME_URL, timeout=60) as response, (inputs / 'dotnet.tar.gz').open('xb') as out:
        while data := response.read(65536):
            out.write(data)
        out.flush(); os.fsync(out.fileno())
    with (inputs / 'dotnet.tar.gz').open('rb') as source:
        observed = hashlib.file_digest(source, 'sha512').hexdigest()
    if observed != RUNTIME_SHA512:
        raise ValueError('RuntimeReleaseHashMismatch')
    import shutil
    shutil.copytree(publish, inputs / 'harness')
    shutil.copytree(repo / 'distros/debian/native', inputs / 'native', ignore=shutil.ignore_patterns('__pycache__'))
    shutil.copy2(repo / 'distros/_shared/installer/collect_inventory.py', inputs / 'collector.py')
    bundle = BASE / 'packages/bundle'
    package_set = (bundle / 'package-set.json').read_bytes()
    if hashlib.sha256(package_set).hexdigest().upper() != 'BE86797B73250B19569BC6A229EB1C13D22A651DEAEF315CE8C10406A5AD1272':
        raise ValueError('RetainedPackageSetChanged')
    dependency = bundle / 'debian/pool/main/i/icu/libicu76_76.1-4_amd64.deb'
    if hashlib.sha256(dependency.read_bytes()).hexdigest().upper() != 'C1BF762996DE9ECBA9B9D871E4928A8090F023A3E9E7FA3240B3D90F892A01DC':
        raise ValueError('RuntimeDependencyChanged')
    shutil.copyfile(dependency, inputs / 'libicu76.deb')
    # Windows checkout modes are not executable-tool protection evidence.
    for p in inputs.rglob('*'):
        if p.is_symlink(): raise ValueError('ToolInputSymlink')
        os.chmod(p, 0o755 if p.is_dir() else 0o644)
    checkpoint(run, 'acquisition.json', {'Version': '8.0.20', 'Origin': RUNTIME_URL,
        'ExpectedSha512': RUNTIME_SHA512, 'ObservedSha512': observed,
        'MetadataOrigin': 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'})
    subprocess.run(['xorriso', '-as', 'mkisofs', '-quiet', '-R', '-o', str(run / 'tools.iso'), str(inputs)], check=True)
    backing = BASE / 'neutralization-9f5101de-4b25-44d5-8ade-a18c4e371408/derived.qcow2'
    if not stat.S_ISREG(backing.stat().st_mode):
        raise ValueError('BackingNotRegular')
    subprocess.run(['qemu-img', 'create', '-f', 'qcow2', '-F', 'qcow2', '-b', str(backing), str(run / 'builder.qcow2')], check=True)
    for name, size in [('runtime.raw', 24 * 1024**3), ('target.raw', 40 * 1024**3), ('journal.raw', 8 * 1024**3)]:
        with (run / name).open('xb') as out:
            out.truncate(size); out.flush(); os.fsync(out.fileno())
    args = ['/usr/bin/qemu-system-x86_64', '-no-user-config', '-nodefaults', '-machine', 'q35,accel=kvm',
        '-cpu', 'host', '-smp', '4', '-m', '8192', '-display', 'none', '-nic', 'none', '-monitor', 'none',
        '-no-reboot', '-sandbox', 'on,obsolete=deny,elevateprivileges=allow,spawn=deny,resourcecontrol=deny',
        '-runas', 'gillesduif', '-serial', 'unix:' + str(run / 'console.sock') + ',server=on,wait=off',
        '-drive', f'file={run}/builder.qcow2,if=none,id=builder,format=qcow2,cache=none',
        '-device', 'virtio-blk-pci,drive=builder,serial=IGLOO-CONSTRUCTION',
        '-drive', f'file={run}/runtime.raw,if=none,id=runtime,format=raw,cache=none',
        '-device', 'virtio-blk-pci,drive=runtime,serial=IGLOO-GPT-RUNTIME',
        '-drive', f'file={run}/tools.iso,if=none,id=tools,format=raw,readonly=on',
        '-device', 'virtio-blk-pci,drive=tools,serial=IGLOO-TOOLS', '-boot', 'c']
    checkpoint(run, 'construction-intent.json', {'LabId': run.name, 'Arguments': args,
        'BackingStat': [backing.stat().st_dev, backing.stat().st_ino, backing.stat().st_size, backing.stat().st_mtime_ns],
        'NewDisks': {n: [os.stat(run / n).st_ino, os.stat(run / n).st_size] for n in ('runtime.raw', 'target.raw', 'journal.raw')},
        'Qualification': 'NotStarted'})
    os.chown(run, 1000, 1000)
    for p in run.iterdir():
        if p.is_file(): os.chown(p, 1000, 1000)
    with (run / 'construction.stderr').open('xb') as log:
        process = subprocess.Popen(args, stdout=log, stderr=log, start_new_session=True)
    checkpoint(run, 'construction-process.json', {'Pid': process.pid})
    print(run)


if __name__ == '__main__':
    create(Path(sys.argv[1]), Path(sys.argv[2]))
