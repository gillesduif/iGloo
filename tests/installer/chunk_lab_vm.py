"""Opt-in new file-backed VM derivative. Does not provision/import any target.

Run in the explicitly authorized Linux lab host as root, never on host block devices.
The original evidence chain is opened by QEMU read-only. No shares, network,
physical passthrough, firmware interface or target boot is configured.
"""
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import uuid


def create():
    base = Path('/var/lib/igloo/factory-evidence/99695826-82bc-4077-b4cf-a22cebc706da')
    backing = base / 'neutralization-9f5101de-4b25-44d5-8ade-a18c4e371408/derived.qcow2'
    assert os.getuid() == 0 and stat.S_ISREG(backing.stat().st_mode)
    identifier = uuid.uuid4().hex
    directory = Path('/var/lib/igloo/chunk-labs') / identifier
    directory.mkdir(parents=True, exist_ok=False)
    os.chown(directory, 1000, 1000)
    overlay = directory / 'runtime.qcow2'
    subprocess.run(['qemu-img', 'create', '-f', 'qcow2', '-F', 'qcow2', '-b', str(backing), str(overlay)], check=True)
    for name, size in [('target.raw', 40 * 1024**3), ('journal.raw', 8 * 1024**3)]:
        fd = os.open(directory / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        try:
            os.ftruncate(fd, size); os.fsync(fd)
        finally:
            os.close(fd)
    for name in ('runtime.qcow2', 'target.raw', 'journal.raw'):
        os.chown(directory / name, 1000, 1000)
    socket = '/tmp/igloo-chunks-' + identifier[:12] + '.sock'
    args = ['/usr/bin/qemu-system-x86_64', '-no-user-config', '-nodefaults', '-machine', 'q35,accel=kvm',
        '-cpu', 'host', '-smp', '4', '-m', '8192', '-display', 'none', '-nic', 'none', '-monitor', 'none',
        '-no-reboot', '-sandbox', 'on,obsolete=deny,elevateprivileges=allow,spawn=deny,resourcecontrol=deny',
        '-runas', 'gillesduif', '-serial', 'unix:' + socket + ',server=on,wait=off']
    for name, fmt, device, serial in [('runtime.qcow2', 'qcow2', 'runtime', 'IGLOO-FACTORY'),
        ('target.raw', 'raw', 'target', 'IGLOO-LAB-TARGET'), ('journal.raw', 'raw', 'journal', 'IGLOO-LAB-JOURNAL')]:
        args += ['-drive', f'file={directory / name},if=none,id={device},format={fmt},cache=none',
                 '-device', f'virtio-blk-pci,drive={device},serial={serial}']
    args += ['-boot', 'c']
    intent = {'LabId': identifier, 'Directory': str(directory), 'Socket': socket, 'Arguments': args,
              'BackingStat': list(backing.stat()), 'Provisioning': 'NotStarted'}
    with (directory / 'intent.json').open('x') as output:
        json.dump(intent, output, indent=2); output.flush(); os.fsync(output.fileno())
    with (directory / 'console.log').open('xb') as log:
        process = subprocess.Popen(args, stdout=log, stderr=log, start_new_session=True)
    (directory / 'pid').write_text(str(process.pid))
    print(json.dumps({**intent, 'Pid': process.pid}))


if __name__ == '__main__':
    create()
