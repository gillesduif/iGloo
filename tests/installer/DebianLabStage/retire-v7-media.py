"""Retire two exact task-created v7 raw media after complete readback."""

from pathlib import Path
import json
import os
import stat
import subprocess
import uuid


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
IDS = json.loads((LAB / 'base-identities.json').read_text())
MEDIA = ((LAB / 'selected.raw', 30 << 30, IDS['selectedDiskGuid']),
         (LAB / 'seed.raw', 5 << 30, IDS['seedDiskGuid']))
REQUIRED = ('firstboot-readback-v7.json', 'secondboot-readback-v7.json',
            'retirement-readback-v7.json', 'lab-login-correction-v7.json',
            'preboot-hashes-v7.json', 'qemu-install-v7-command.json',
            'qemu-firstboot-v7-command.json', 'qemu-secondboot-v7-command.json')


def gpt_guid(path):
    with path.open('rb') as stream:
        stream.seek(512)
        header = stream.read(92)
    if header[:8] != b'EFI PART':
        raise RuntimeError('Exact media lacks GPT header')
    return str(uuid.UUID(bytes_le=header[56:72]))


if os.geteuid() != 0 or LAB.resolve() != LAB:
    raise RuntimeError('Run as root against the exact v7 lab')
if subprocess.check_output(['losetup', '-a']).strip():
    raise RuntimeError('A loop device is still attached')
if subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode != 1:
    raise RuntimeError('QEMU is still running')
if subprocess.run(['pgrep', '-af', '[q]emu-nbd'], capture_output=True).returncode != 1:
    raise RuntimeError('NBD helper is still running')
if any(not (LAB / name).is_file() for name in REQUIRED):
    raise RuntimeError('Bounded native readbacks are incomplete')
if not json.loads((LAB / 'retirement-readback-v7.json').read_text())['doneDurable']:
    raise RuntimeError('Last installed readback did not prove durable firstboot')
for path, size, guid in MEDIA:
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size != size:
        raise RuntimeError('A task-created media path is no longer a unique regular file')
    if gpt_guid(path) != guid:
        raise RuntimeError('A task-created media GPT identity changed')

# A child overlay makes deletion unsafe even when the raw media itself is idle.
for root, directories, files in os.walk('/home/gillesduif'):
    directories[:] = [name for name in directories if not name.startswith('.')]
    for name in files:
        if not name.endswith('.qcow2'):
            continue
        image = Path(root) / name
        info = json.loads(subprocess.check_output(
            ['qemu-img', 'info', '--output=json', str(image)], text=True))
        backing = info.get('backing-filename')
        if backing:
            backing_path = Path(backing)
            if not backing_path.is_absolute():
                backing_path = image.parent / backing_path
            if backing_path.resolve() in {path.resolve() for path, _, _ in MEDIA}:
                raise RuntimeError(f'Active backing descendant prevents retirement: {image}')

record = {'retired': [], 'preservedDecoy': str(LAB / 'decoy.raw')}
for path, size, guid in MEDIA:
    info = path.stat()
    record['retired'].append({'path': str(path), 'logicalBytes': size,
                              'allocatedBytesBefore': info.st_blocks * 512,
                              'diskGuid': guid})
    path.unlink()
    if path.exists():
        raise RuntimeError(f'Exact disposable media persisted after unlink: {path}')
os.sync()
(LAB / 'media-retirement-v7.json').write_text(json.dumps(record, indent=2) + '\n')
print(json.dumps(record, indent=2))
