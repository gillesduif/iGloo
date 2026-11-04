"""Bounded disposable Debian v6 first-boot launcher for the WSL lab."""

from pathlib import Path
import json
import subprocess
import sys
import time


lab = Path('/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6')
seed_free = sys.argv[1:] == ['--seed-free']
assert not sys.argv[1:] or seed_free
phase = 'secondboot' if seed_free else 'firstboot'
for name in ('selected.raw', 'decoy.raw', 'ovmf-vars-installer-v6.fd'):
    assert (lab / name).is_file(), name
if not seed_free:
    assert (lab / 'seed.raw').is_file()
assert not subprocess.check_output(['losetup', '-a']).strip()
assert subprocess.run(
    ['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True
).returncode == 1

args = [
    'qemu-system-x86_64', '-machine', 'q35,accel=kvm', '-cpu', 'host',
    '-m', '6144', '-smp', '4',
    '-drive', 'if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd',
    '-drive', f'if=pflash,format=raw,file={lab / "ovmf-vars-installer-v6.fd"}',
    '-drive', f'if=none,id=selected,format=raw,file={lab / "selected.raw"},cache=none',
    '-device', 'virtio-blk-pci,drive=selected,bootindex=1',
    '-drive', f'if=none,id=decoy,format=raw,file={lab / "decoy.raw"},cache=none',
    '-device', 'virtio-blk-pci,drive=decoy',
]
if not seed_free:
    args += [
        '-drive', f'if=none,id=seed,format=raw,file={lab / "seed.raw"},cache=none',
        '-device', 'virtio-blk-pci,drive=seed',
    ]
args += [
    '-nic', 'none', '-display', 'none', '-vnc', '127.0.0.1:5',
    '-monitor', f'unix:{lab / f"qemu-{phase}-v6.monitor"},server,nowait',
    '-serial', f'file:{lab / f"qemu-{phase}-v6.serial.log"}',
    '-no-reboot', '-pidfile', str(lab / f'qemu-{phase}-v6.pid'), '-daemonize',
]
(lab / f'qemu-{phase}-v6-command.json').write_text(json.dumps(args, indent=2) + '\n')
subprocess.run(args, check=True)
time.sleep(1)
pid = int((lab / f'qemu-{phase}-v6.pid').read_text())
subprocess.run(['kill', '-0', str(pid)], check=True)
print('QEMU installed', phase, 'pid:', pid)
