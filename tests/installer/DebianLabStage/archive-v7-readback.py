"""Preserve bounded readbacks before retiring only the fresh v7 lab media."""

from pathlib import Path
import hashlib
import json
import os
import shutil
import stat
import subprocess
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
IDS = json.loads((LAB / 'base-identities.json').read_text())
OUT = LAB / 'retirement-readback'
TARGET = LAB / 'selected.raw'
SEED = LAB / 'seed.raw'


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def exact_loop(image, disk_guid, part_guid, number):
    loop = output('losetup', '--find', '--show', '--partscan', '--read-only', str(image))
    if Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() != image.resolve():
        raise RuntimeError('Loop backing path changed')
    for _ in range(30):
        if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == disk_guid
                and output('lsblk', '-dnro', 'PARTUUID', loop + f'p{number}').lower() == part_guid):
            return loop
        time.sleep(.1)
    raise RuntimeError('Exact GPT identity unavailable')


if os.geteuid() != 0 or not LAB.is_dir() or OUT.exists():
    raise RuntimeError('Fresh v7 readback must be run once by root')
if any(not (LAB / name).is_file() for name in (
        'firstboot-readback-v7.json', 'secondboot-readback-v7.json',
        'preboot-hashes-v7.json', 'lab-login-correction-v7.json')):
    raise RuntimeError('Previous native readbacks are incomplete')
if subprocess.check_output(['losetup', '-a']).strip():
    raise RuntimeError('Other loop device in use')
if subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode != 1:
    raise RuntimeError('QEMU still active')
if TARGET.stat().st_size != 30 << 30 or SEED.stat().st_size != 5 << 30:
    raise RuntimeError('Fresh v7 media size changed')

OUT.mkdir(mode=0o700)
record = {'selectedDiskGuid': IDS['selectedDiskGuid'],
          'seedDiskGuid': IDS['seedDiskGuid'],
          'linuxEspGuid': IDS['linuxEspGuid'], 'files': {}}


def preserve(path, name):
    if not path.is_file() or path.is_symlink() or path.stat().st_size > 4 * 1024 * 1024:
        raise RuntimeError(f'Bounded readback artifact unavailable: {name}')
    target = OUT / name
    shutil.copyfile(path, target)
    target.chmod(0o600)
    record['files'][name] = {'sha256': digest(target), 'bytes': target.stat().st_size}


target_loop = exact_loop(TARGET, IDS['selectedDiskGuid'], IDS['linuxEspGuid'], 3)
seed_loop = None
try:
    root_mount = LAB / 'mnt-installed-root-ro'
    esp_mount = LAB / 'mnt-linux-esp-ro'
    seed_mount = LAB / 'mnt-seed-ro'
    for mount in (root_mount, esp_mount, seed_mount):
        mount.mkdir(exist_ok=True)
    record['installedGpt'] = json.loads(output('sfdisk', '--json', target_loop))
    subprocess.run(['mount', '-o', 'ro,noload,nodev,nosuid', target_loop + 'p4', str(root_mount)], check=True)
    try:
        for source, name in (
                ('var/log/igloo/agent.log', 'agent.log'),
                ('var/log/igloo/bootstrap.log', 'bootstrap.log'),
                ('etc/fstab', 'fstab'),
                ('var/lib/igloo/manifest.json', 'installed-manifest.json'),
                ('home/igloolab/.local/state/igloo-credentials.log', 'credential-import.log')):
            preserve(root_mount / source, name)
        record['doneDurable'] = (root_mount / 'var/lib/igloo/.done').is_file()
    finally:
        subprocess.run(['umount', str(root_mount)], check=True)
    subprocess.run(['mount', '-t', 'vfat', '-o', 'ro,nodev,nosuid', target_loop + 'p3', str(esp_mount)], check=True)
    try:
        record['linuxEspFiles'] = {
            str(path.relative_to(esp_mount)): digest(path)
            for path in esp_mount.rglob('*') if path.is_file()
        }
        if not any(name.lower().endswith('shimx64.efi') for name in record['linuxEspFiles']):
            raise RuntimeError('Installed Linux ESP has no shim')
    finally:
        subprocess.run(['umount', str(esp_mount)], check=True)
    seed_loop = exact_loop(SEED, IDS['seedDiskGuid'], IDS['seedPartitionGuid'], 1)
    subprocess.run(['mount', '-t', 'vfat', '-o', 'ro,nodev,nosuid', seed_loop + 'p1', str(seed_mount)], check=True)
    try:
        for source, name in (
                ('migration-manifest.json', 'redacted-seed-manifest.json'),
                ('preseed.cfg', 'redacted-seed-preseed.cfg'),
                ('igloo-seed-identity.json', 'seed-identity-after-firstboot.json')):
            preserve(seed_mount / source, name)
        record['redactedInitrdSha256'] = digest(seed_mount / 'igloo-boot/initrd')
    finally:
        subprocess.run(['umount', str(seed_mount)], check=True)
finally:
    if seed_loop:
        subprocess.run(['losetup', '--detach', seed_loop], check=True)
    subprocess.run(['losetup', '--detach', target_loop], check=True)

if not record['doneDurable']:
    raise RuntimeError('Durable firstboot marker missing')
(LAB / 'retirement-readback-v7.json').write_text(json.dumps(record, indent=2) + '\n')
print(json.dumps({'doneDurable': record['doneDurable'],
                  'preservedFiles': record['files'],
                  'linuxEspFileCount': len(record['linuxEspFiles']),
                  'redactedInitrdSha256': record['redactedInitrdSha256']}, indent=2))
