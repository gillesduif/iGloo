"""Read-only gate for the disposable v6 current-agent first-boot child."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/current-firstboot-v6-child')
BASE = Path('/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6/selected.raw')
SEED = LAB / 'seed.raw'
CHILD = LAB / 'selected-child.qcow2'
ROOT = LAB / 'mounted-root'
EXPECTED_AGENT = '254ee58ff3b7fb59430b35a6af097f0496b30e48389fce87908b915fc2cd68af'


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True)


def output(*args):
    return run(*args).stdout.strip()


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def main():
    if (os.geteuid() != 0 or LAB.resolve() != LAB or
            BASE.is_symlink() or BASE.stat().st_nlink != 1 or
            SEED.is_symlink() or SEED.stat().st_nlink != 1 or
            CHILD.is_symlink() or CHILD.stat().st_nlink != 1 or
            SEED.stat().st_size != 256 * 1024 * 1024 or
            output('losetup', '-a') or
            output('blockdev', '--getsize64', '/dev/nbd0') != '0' or
            subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'],
                           capture_output=True).returncode != 1):
        raise RuntimeError('The exclusive read-only lab preflight is unavailable')
    if shutil.disk_usage('/mnt/c').free < 22 * 1024**3:
        raise RuntimeError('C: does not have 22 GiB free before guest boot')
    prep = json.loads((LAB / 'preparation.json').read_text())
    staged = json.loads((LAB / 'seed-stage.json').read_text())
    identity = json.loads((LAB / 'seed-identity.json').read_text())
    manifest = json.loads((LAB / 'manifest.json').read_text())
    child_info = json.loads(output('qemu-img', 'info', '--output=json', str(CHILD)))
    if (child_info.get('format') != 'qcow2' or
            child_info.get('full-backing-filename') != str(BASE) or
            child_info.get('backing-filename-format') != 'raw' or
            manifest['hardware']['targetDiskGuid'] != prep['childDiskGuid'] or
            staged['targetChildDiskGuid'] != prep['childDiskGuid'] or
            identity['diskGuid'] != staged['seedDiskGuid'] or
            identity['partitionGuid'] != staged['seedPartitionGuid'] or
            sha(LAB / 'manifest.json') != prep['manifestSha256'] or
            sha(LAB / 'provider-stage/igloo-agent/agent.py') != EXPECTED_AGENT):
        raise RuntimeError('The child backing, manifest, or seed binding changed')

    loop = output('losetup', '--find', '--show', '--partscan', '--read-only', str(SEED))
    seed_mount = LAB / 'mounted-seed'
    mounted = False
    try:
        if Path(output('losetup', '-n', '-O', 'BACK-FILE', loop)).resolve() != SEED.resolve():
            raise RuntimeError('The seed loop has a different backing file')
        part = loop + 'p1'
        for _ in range(30):
            if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == staged['seedDiskGuid'] and
                    output('lsblk', '-dnro', 'PARTUUID', part).lower() == staged['seedPartitionGuid']):
                break
            time.sleep(.1)
        else:
            raise RuntimeError('The exact staged OEMDRV GPT is not visible')
        start_sector = int((Path('/sys/class/block') / Path(part).name / 'start').read_text())
        with SEED.open('rb') as stream:
            stream.seek(start_sector * 512)
            primary = stream.read(512)
            if primary[82:90] != b'FAT32   ':
                raise RuntimeError('The small OEMDRV is not FAT32')
            sector_size = int.from_bytes(primary[11:13], 'little')
            reserved = int.from_bytes(primary[14:16], 'little')
            backup_sector = int.from_bytes(primary[50:52], 'little')
            if sector_size != 512 or not 1 <= backup_sector < reserved:
                raise RuntimeError('Unexpected seed FAT geometry')
            stream.seek(start_sector * 512 + backup_sector * sector_size)
            backup = stream.read(sector_size)
            stream.seek(start_sector * 512 + reserved * sector_size + 4)
            fat_entry_1 = int.from_bytes(stream.read(4), 'little')
        fat_check = subprocess.run(['fsck.fat', '-n', part], capture_output=True,
                                   text=True, check=False, timeout=120)
        if (fat_check.returncode != 0 or primary[65] != backup[65] or
                not fat_entry_1 & 0x08000000 or not fat_entry_1 & 0x04000000):
            raise RuntimeError('The staged OEMDRV is already dirty or inconsistent')
        run('mount', '-t', 'vfat', '-o', 'ro', part, str(seed_mount))
        mounted = True
        verifier = LAB / 'provider-stage/igloo-initrd/igloo-verify-seed.py'
        media = output('python3', str(verifier), str(LAB / 'selected-seed-partuuid'),
                       str(LAB / 'selected-seed-diskguid'), str(seed_mount)).splitlines()
        if set(media) != {'igloo-wallpaper.PNG', 'igloo-avatar.jpg'}:
            raise RuntimeError('Selected media verifier output changed')
        for record in identity['files']:
            if sha(seed_mount / record['path']).lower() != record['sha256'].lower():
                raise RuntimeError('A staged seed identity member changed')
        if sha(seed_mount / 'migration-manifest.json') != sha(LAB / 'manifest.json'):
            raise RuntimeError('The staged manifest differs from the child manifest')
    finally:
        if mounted:
            run('umount', str(seed_mount))
        run('losetup', '-d', loop)

    run('qemu-nbd', '--connect=/dev/nbd0', '--format=qcow2', '--read-only', str(CHILD))
    mounted = False
    try:
        if (output('blockdev', '--getsize64', '/dev/nbd0') != str(30 * 1024**3) or
                prep['childDiskGuid'] not in output('sgdisk', '-p', '/dev/nbd0').lower() or
                prep['linuxEspPartitionGuid'] not in output('sgdisk', '-i', '3', '/dev/nbd0').lower()):
            raise RuntimeError('The staged child GPT does not match its identity')
        run('mount', '-o', 'ro,noload,nodev,nosuid', '/dev/nbd0p4', str(ROOT))
        mounted = True
        state = ROOT / 'var/lib/igloo'
        bootstrap = ROOT / 'opt/igloo/igloo-bootstrap.sh'
        if (any((state / name).exists() for name in
                ('.done', 'manifest.retry.json', 'seed-retry')) or
                (state / 'selected-seed-partuuid').read_text().strip() != staged['seedPartitionGuid'] or
                (state / 'selected-seed-diskguid').read_text().strip() != staged['seedDiskGuid'] or
                'mountpoint -q /run/igloo-seed' not in bootstrap.read_text() or
                sha(ROOT / 'opt/igloo/igloo-verify-seed.py') !=
                sha(LAB / 'provider-stage/igloo-initrd/igloo-verify-seed.py')):
            raise RuntimeError('The child first-boot selector/retry state changed')
    finally:
        if mounted:
            run('umount', str(ROOT))
        run('qemu-nbd', '--disconnect', '/dev/nbd0')

    record = {
        'childDiskGuid': prep['childDiskGuid'],
        'seedDiskGuid': staged['seedDiskGuid'],
        'seedPartitionGuid': staged['seedPartitionGuid'],
        'identitySha256': sha(LAB / 'seed-identity.json'),
        'baseSha256Before': (LAB / 'base-sha256-before.txt').read_text().split()[0],
        'primaryBootByte65': primary[65],
        'backupBootByte65': backup[65],
        'fatEntry1Hex': f'{fat_entry_1:08x}',
        'fatCleanBit': True,
        'fsckFatReadOnlyExitCode': fat_check.returncode,
        'currentAgentSha256': EXPECTED_AGENT,
        'cFreeBytes': shutil.disk_usage('/mnt/c').free,
    }
    (LAB / 'preboot-check.json').write_text(json.dumps(record, indent=2) + '\n')
    print(json.dumps(record, indent=2))


if __name__ == '__main__':
    main()
