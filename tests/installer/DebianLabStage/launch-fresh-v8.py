"""Boot only the exact new disposable Debian v8 file-backed disks under OVMF."""

import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import time

from fresh_v8_capacity import prelaunch_capacity, watch


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
if LAB.resolve() != LAB or not LAB.is_dir():
    raise RuntimeError('Exact disposable Debian v8 lab directory is unavailable')
capacity = prelaunch_capacity()
IDS = json.loads((LAB / 'base-identities.json').read_text())


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


for name, size in (('selected.raw', 30 << 30), ('decoy.raw', 30 << 30), ('seed.raw', 5 << 30)):
    path = LAB / name
    if path.is_symlink() or not path.is_file() or path.stat().st_size != size:
        raise RuntimeError('Disposable Debian v8 image missing or redirected: ' + name)
for name in ('seed-identity-v8.json', 'grub.cfg', 'initrd-v8.gz', 'payload-summary-v8.json'):
    if not (LAB / name).is_file():
        raise RuntimeError('Fresh Debian v8 boot input is missing: ' + name)
if subprocess.check_output(['losetup', '-a']).strip() or subprocess.run(
        ['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode != 1:
    raise RuntimeError('A loop device or QEMU is still active')
if IDS['isoSha256'] != 'e94859a83b305cae5125dc9c6080ced040c59d099ea7381e7f300fd1dba87a17':
    raise RuntimeError('Signed Debian ISO identity differs')
if sha(LAB / 'initrd-v8.gz') != json.loads(
        (LAB / 'payload-summary-v8.json').read_text())['injectedInitrdSha256']:
    raise RuntimeError('Injected Debian v8 initrd changed')
identity = json.loads((LAB / 'seed-identity-v8.json').read_text())
if (identity['diskGuid'] != IDS['seedDiskGuid'] or
        identity['partitionGuid'] != IDS['seedPartitionGuid'] or
        len(identity['files']) != 13):
    raise RuntimeError('Exact Debian v8 seed binding differs')
grub = (LAB / 'grub.cfg').read_text()
for token in ('igloo.seedpartuuid=' + IDS['seedPartitionGuid'],
              'igloo.seeddiskguid=' + IDS['seedDiskGuid'],
              'igloo.linuxesppartuuid=' + IDS['linuxEspGuid']):
    if grub.lower().count(token.lower()) != 1:
        raise RuntimeError('Debian v8 OVMF GRUB config lacks exact GPT binding')

vars_source = Path('/usr/share/OVMF/OVMF_VARS_4M.fd')
vars_target = LAB / 'ovmf-vars-installer-v8.fd'
if not vars_source.is_file() or vars_target.exists():
    raise RuntimeError('Fresh OVMF variables are unavailable')
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
    '-monitor', f'unix:{LAB / "qemu-install-v8.monitor"},server,nowait',
    '-serial', f'file:{LAB / "qemu-install-v8.serial.log"}',
    '-no-reboot', '-pidfile', str(LAB / 'qemu-install-v8.pid'), '-daemonize',
]
(LAB / 'qemu-install-v8-command.json').write_text(json.dumps(args, indent=2) + '\n')
subprocess.run(args, check=True)
time.sleep(1)
pid = int((LAB / 'qemu-install-v8.pid').read_text())
subprocess.run(['kill', '-0', str(pid)], check=True)
print(json.dumps({'qemuPid': pid, 'phase': 'installer', **capacity}), flush=True)
watch(pid, LAB / 'qemu-install-v8.monitor', 'installer')
