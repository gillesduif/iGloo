"""Boot only the freshly installed Debian v8 file-backed target."""

import json
from pathlib import Path
import subprocess
import sys
import time

from fresh_v8_capacity import prelaunch_capacity, watch


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
if LAB.resolve() != LAB or not LAB.is_dir():
    raise RuntimeError('Exact disposable Debian v8 lab directory is unavailable')
capacity = prelaunch_capacity()
phase = sys.argv[1]
if phase not in ('firstboot', 'secondboot'):
    raise RuntimeError('Expected firstboot or secondboot')
for name, size in (('selected.raw', 30 << 30), ('decoy.raw', 30 << 30), ('seed.raw', 5 << 30)):
    path = LAB / name
    if path.is_symlink() or not path.is_file() or path.stat().st_size != size:
        raise RuntimeError('Disposable Debian v8 image missing or redirected: ' + name)
if not (LAB / 'ovmf-vars-installer-v8.fd').is_file():
    raise RuntimeError('Debian v8 installed boot lacks OVMF variables')
if subprocess.check_output(['losetup', '-a']).strip() or subprocess.run(
        ['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode != 1:
    raise RuntimeError('A loop device or QEMU is still active')

args = [
    'qemu-system-x86_64', '-machine', 'q35,accel=kvm', '-cpu', 'host',
    '-m', '6144', '-smp', '4',
    '-drive', 'if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd',
    '-drive', f'if=pflash,format=raw,file={LAB / "ovmf-vars-installer-v8.fd"}',
    '-drive', f'if=none,id=selected,format=raw,file={LAB / "selected.raw"},cache=none',
    '-device', 'virtio-blk-pci,drive=selected,bootindex=1',
    '-drive', f'if=none,id=decoy,format=raw,file={LAB / "decoy.raw"},cache=none',
    '-device', 'virtio-blk-pci,drive=decoy',
    '-netdev', 'user,id=n0', '-device', 'virtio-net-pci,netdev=n0',
    '-display', 'none', '-vnc', '127.0.0.1:5',
    '-monitor', f'unix:{LAB / f"qemu-{phase}-v8.monitor"},server,nowait',
    '-serial', f'file:{LAB / f"qemu-{phase}-v8.serial.log"}',
    '-no-reboot', '-pidfile', str(LAB / f'qemu-{phase}-v8.pid'), '-daemonize',
]
if phase == 'firstboot':
    position = args.index('-netdev')
    args[position:position] = [
        '-drive', f'if=none,id=seed,format=raw,file={LAB / "seed.raw"},cache=none',
        '-device', 'virtio-blk-pci,drive=seed',
    ]
(LAB / f'qemu-{phase}-v8-command.json').write_text(json.dumps(args, indent=2) + '\n')
subprocess.run(args, check=True)
time.sleep(1)
pid = int((LAB / f'qemu-{phase}-v8.pid').read_text())
subprocess.run(['kill', '-0', str(pid)], check=True)
print(json.dumps({'qemuPid': pid, 'phase': phase, **capacity}), flush=True)
watch(pid, LAB / f'qemu-{phase}-v8.monitor', phase)
