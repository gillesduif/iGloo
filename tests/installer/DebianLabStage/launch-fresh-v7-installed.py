"""Boot only the freshly installed Debian v7 file-backed target."""

from pathlib import Path
import json
import subprocess
import sys
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
phase = sys.argv[1]
assert phase in ('firstboot', 'secondboot')
assert (LAB / 'selected.raw').stat().st_size == 30 << 30
assert (LAB / 'decoy.raw').stat().st_size == 30 << 30
assert (LAB / 'seed.raw').stat().st_size == 5 << 30
assert (LAB / 'ovmf-vars-installer-v7.fd').is_file()
assert not subprocess.check_output(['losetup', '-a']).strip()
assert subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode == 1

args = [
    'qemu-system-x86_64', '-machine', 'q35,accel=kvm', '-cpu', 'host',
    '-m', '6144', '-smp', '4',
    '-drive', 'if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd',
    '-drive', f'if=pflash,format=raw,file={LAB / "ovmf-vars-installer-v7.fd"}',
    '-drive', f'if=none,id=selected,format=raw,file={LAB / "selected.raw"},cache=none',
    '-device', 'virtio-blk-pci,drive=selected,bootindex=1',
    '-drive', f'if=none,id=decoy,format=raw,file={LAB / "decoy.raw"},cache=none',
    '-device', 'virtio-blk-pci,drive=decoy',
    '-netdev', 'user,id=n0', '-device', 'virtio-net-pci,netdev=n0',
    '-display', 'none', '-vnc', '127.0.0.1:5',
    '-monitor', f'unix:{LAB / f"qemu-{phase}-v7.monitor"},server,nowait',
    '-serial', f'file:{LAB / f"qemu-{phase}-v7.serial.log"}',
    '-no-reboot', '-pidfile', str(LAB / f'qemu-{phase}-v7.pid'), '-daemonize',
]
if phase == 'firstboot':
    seed_position = args.index('-netdev')
    args[seed_position:seed_position] = [
        '-drive', f'if=none,id=seed,format=raw,file={LAB / "seed.raw"},cache=none',
        '-device', 'virtio-blk-pci,drive=seed',
    ]
(LAB / f'qemu-{phase}-v7-command.json').write_text(json.dumps(args, indent=2) + '\n')
subprocess.run(args, check=True)
time.sleep(1)
pid = int((LAB / f'qemu-{phase}-v7.pid').read_text())
subprocess.run(['kill', '-0', str(pid)], check=True)
print(f'Debian v7 disposable installed {phase} QEMU pid:', pid)
