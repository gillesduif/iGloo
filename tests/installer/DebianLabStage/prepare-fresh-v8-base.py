"""Create only the exact new sparse Debian v8 selected, decoy and OEMDRV images."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

from fresh_v8_capacity import GIB, STOP_FREE, c_free_bytes, prelaunch_capacity


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
PARENT = LAB.parent
ISO = Path('/home/gillesduif/igloo-lab/debian-20261012/debian-live-13.7.0-amd64-gnome.iso')
ISO_HASH = 'e94859a83b305cae5125dc9c6080ced040c59d099ea7381e7f300fd1dba87a17'


def run(*args):
    subprocess.run(args, check=True)


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def require_capacity():
    if c_free_bytes() < STOP_FREE:
        raise RuntimeError('Debian v8 source staging crossed the 21 GiB C: stop floor')


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def fresh_sparse(path, size):
    fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        os.ftruncate(fd, size)
        os.fsync(fd)
    finally:
        os.close(fd)


def attach(path):
    loop = output('losetup', '--find', '--show', '--partscan', str(path))
    if Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() != path.resolve():
        run('losetup', '--detach', loop)
        raise RuntimeError('Loop backing differs from exact Debian v8 image')
    return loop


def copy_iso(source, destination):
    digest = hashlib.sha256()
    with source.open('rb') as origin, destination.open('xb') as target:
        for block in iter(lambda: origin.read(4 * 1024 * 1024), b''):
            require_capacity()
            target.write(block)
            digest.update(block)
        target.flush()
        os.fsync(target.fileno())
    if digest.hexdigest() != ISO_HASH:
        raise RuntimeError('Current Debian ISO source differs from the signed pin')
    require_capacity()
    if sha(destination) != ISO_HASH:
        raise RuntimeError('Disposable OEMDRV ISO copy differs from the signed source')


def main():
    if (os.geteuid() != 0 or PARENT.resolve() != PARENT or not PARENT.is_dir() or
            LAB.exists() or LAB.is_symlink() or ISO.is_symlink() or
            not ISO.is_file() or ISO.stat().st_size != 3_804_708_864 or
            output('losetup', '-a') or
            subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'],
                           capture_output=True).returncode != 1):
        raise RuntimeError('Exact fresh Debian v8 inputs/resources are unavailable')
    capacity = prelaunch_capacity()
    LAB.mkdir(mode=0o700)
    selected = LAB / 'selected.raw'
    decoy = LAB / 'decoy.raw'
    seed = LAB / 'seed.raw'
    for name, path in (('selected', selected), ('decoy', decoy)):
        require_capacity()
        fresh_sparse(path, 30 * GIB)
        run('sgdisk', '--clear', '--new=1:2048:+512M', '--typecode=1:EF00',
            '--change-name=1:WindowsESP', '--new=2:0:+4G', '--typecode=2:0700',
            '--change-name=2:WindowsSource', '--new=3:0:+1G', '--typecode=3:EF00',
            '--change-name=3:IglooLinuxESP', str(path))
        loop = attach(path)
        try:
            run('mkfs.fat', '-F', '32', '-n', 'WINESP', loop + 'p1')
            require_capacity()
            run('mkfs.ntfs', '-Q', '-L', 'WinSource', loop + 'p2')
            require_capacity()
            run('mkfs.fat', '-F', '32', '-n', 'IGLOOESP', loop + 'p3')
            mount = LAB / ('mnt-' + name + '-p1')
            mount.mkdir()
            run('mount', '-t', 'vfat', '-o', 'rw', loop + 'p1', str(mount))
            try:
                (mount / 'igloo-windows-esp-sentinel.txt').write_text(
                    'Disposable Windows ESP sentinel: ' + name + '\n')
                run('sync')
            finally:
                run('umount', str(mount))
            run('fsck.fat', '-n', loop + 'p1')
            run('fsck.fat', '-n', loop + 'p3')
        finally:
            run('losetup', '--detach', loop)

    require_capacity()
    fresh_sparse(seed, 5 * GIB)
    run('sgdisk', '--clear', '--new=1:2048:0', '--typecode=1:0700',
        '--change-name=1:OEMDRV', str(seed))
    loop = attach(seed)
    try:
        run('mkfs.fat', '-F', '32', '-n', 'OEMDRV', loop + 'p1')
        mount = LAB / 'mnt-seed'
        mount.mkdir()
        run('mount', '-t', 'vfat', '-o', 'rw', loop + 'p1', str(mount))
        try:
            if list(mount.iterdir()):
                raise RuntimeError('Fresh v8 OEMDRV is not empty before ISO copy')
            copy_iso(ISO, mount / 'debian.iso')
            run('sync')
        finally:
            run('umount', str(mount))
        run('fsck.fat', '-n', loop + 'p1')
    finally:
        run('losetup', '--detach', loop)
    require_capacity()

    def disk_guid(path):
        return output('sgdisk', '-p', str(path)).split('Disk identifier (GUID): ')[1].splitlines()[0].lower()

    def part_guid(path, number):
        return output('sgdisk', '-i', str(number), str(path)).split('Partition unique GUID: ')[1].splitlines()[0].lower()

    first = int(output('sgdisk', '-F', str(selected)))
    last = int(output('sgdisk', '-E', str(selected)))
    identities = {
        'isoSha256': ISO_HASH,
        'selectedDiskGuid': disk_guid(selected),
        'decoyDiskGuid': disk_guid(decoy),
        'seedDiskGuid': disk_guid(seed),
        'windowsEspGuid': part_guid(selected, 1),
        'linuxEspGuid': part_guid(selected, 3),
        'seedPartitionGuid': part_guid(seed, 1),
        'freeOffsetBytes': first * 512,
        'freeSizeBytes': (last - first + 1) * 512,
    }
    if len({identities['selectedDiskGuid'], identities['decoyDiskGuid'],
            identities['seedDiskGuid']}) != 3:
        raise RuntimeError('Fresh v8 GPT disk IDs are not unique')
    (LAB / 'base-identities.json').write_text(json.dumps(identities, indent=2) + '\n')
    print(json.dumps({'identities': identities, 'startCapacity': capacity,
                      'afterStageCFreeBytes': c_free_bytes(),
                      'wslFreeBytes': shutil.disk_usage('/').free}, indent=2))


if __name__ == '__main__':
    main()
