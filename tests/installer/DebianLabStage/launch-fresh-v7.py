"""Boot only the exact disposable Debian v7 file-backed disks under OVMF."""

from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
IDS = json.loads((LAB / 'base-identities.json').read_text())


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def sha(path):
    value = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


for name, size in (('selected.raw', 30 << 30), ('decoy.raw', 30 << 30), ('seed.raw', 5 << 30)):
    target = LAB / name
    assert target.is_file() and target.stat().st_size == size, name
for name in ('seed-identity-v7.json', 'preboot-hashes-v7.json', 'grub.cfg',
             'initrd-v7.gz', 'payload-summary-v7.json'):
    assert (LAB / name).is_file(), name
assert not output('losetup', '-a')
assert subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode == 1
assert IDS['isoSha256'] == 'e94859a83b305cae5125dc9c6080ced040c59d099ea7381e7f300fd1dba87a17'
assert sha(LAB / 'grub.cfg') == '16419cf6c1fb6ddfee53e66e1bbb43c6ed42e2206d55fbed58cc2a8009513d53'
assert sha(LAB / 'initrd-v7.gz') == json.loads((LAB / 'payload-summary-v7.json').read_text())['injectedInitrdSha256']
identity = json.loads((LAB / 'seed-identity-v7.json').read_text())
assert identity['diskGuid'] == IDS['seedDiskGuid']
assert identity['partitionGuid'] == IDS['seedPartitionGuid']

vars_source = Path('/usr/share/OVMF/OVMF_VARS_4M.fd')
vars_target = LAB / 'ovmf-vars-installer-v7.fd'
assert vars_source.is_file() and not vars_target.exists()
shutil.copyfile(vars_source, vars_target)
args = [
    'qemu-system-x86_64', '-machine', 'q35,accel=kvm', '-cpu', 'host',
    '-m', '6144', '-smp', '4',
    '-drive', 'if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd',
    '-drive', f'if=pflash,format=raw,file={vars_target}',
    '-drive', f'if=none,id=selected,format=raw,file={LAB / "selected.raw"},cache=none',
    '-device', 'virtio-blk-pci,drive=selected',
    '-drive', f'if=none,id=decoy,format=raw,file={LAB / "decoy.raw"},cache=none',
    '-device', 'virtio-blk-pci,drive=decoy',
    '-drive', f'if=none,id=seed,format=raw,file={LAB / "seed.raw"},cache=none',
    '-device', 'virtio-blk-pci,drive=seed,bootindex=1',
    '-netdev', 'user,id=n0', '-device', 'virtio-net-pci,netdev=n0',
    '-display', 'none', '-vnc', '127.0.0.1:5',
    '-monitor', f'unix:{LAB / "qemu-install-v7.monitor"},server,nowait',
    '-serial', f'file:{LAB / "qemu-install-v7.serial.log"}',
    '-no-reboot', '-pidfile', str(LAB / 'qemu-install-v7.pid'), '-daemonize',
]
(LAB / 'qemu-install-v7-command.json').write_text(json.dumps(args, indent=2) + '\n')
subprocess.run(args, check=True)
time.sleep(1)
pid = int((LAB / 'qemu-install-v7.pid').read_text())
subprocess.run(['kill', '-0', str(pid)], check=True)
print('Debian v7 disposable UEFI installer QEMU pid:', pid)
