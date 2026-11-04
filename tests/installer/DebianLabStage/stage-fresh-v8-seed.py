"""Bind only the disposable Debian v8 FAT seed to its freshly observed GPT IDs."""

import hashlib
import json
import os
import shutil
import subprocess
import time
from pathlib import Path


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
BOOT_SOURCE = Path('/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6')
STAGE = LAB / 'provider-stage-v8'
SEED = LAB / 'seed.raw'
IDS = json.loads((LAB / 'base-identities.json').read_text())
AGENT_FILES = {
    'agent.py', 'first-boot.sh', 'display-apply.py', 'display-apply-gnome.py',
    'igloo_boot.py', 'igloo-first-boot.service',
    'grub-theme-stylish-1080p.tar.gz', 'grub-theme-stylish-4k.tar.gz',
}
MEDIA_FILES = {'igloo-wallpaper.PNG', 'igloo-avatar.jpg'}


def run(*args):
    subprocess.run(args, check=True)


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest().upper()


def main():
    if (os.geteuid() != 0 or LAB.resolve() != LAB or SEED.is_symlink() or
            SEED.parent.resolve() != LAB or not SEED.is_file() or
            SEED.stat().st_nlink != 1 or not STAGE.is_dir() or
            STAGE.is_symlink() or (LAB / 'seed-identity-v8.json').exists()):
        raise RuntimeError('Fresh, unbound Debian v8 seed or stage is unavailable')
    if sha(BOOT_SOURCE / 'iso-bootx64.efi') != 'E103C5D02657879F141B086ED703584D28BB9E2721B423333BE085EE1B4D397B':
        raise RuntimeError('Pinned Debian ISO EFI boot file changed')
    if sha(BOOT_SOURCE / 'iso-grubx64.efi') != 'B102E12508C773796D102D58F81BB8A00357C01DAA09092AE1D2B1D1702BC310':
        raise RuntimeError('Pinned Debian ISO GRUB file changed')
    if sha(STAGE / 'migration-manifest.json') != sha(LAB / 'manifest.json'):
        raise RuntimeError('Staged Debian v8 manifest differs from reviewed input')
    payload = json.loads((LAB / 'payload-summary-v8.json').read_text())
    if (sha(STAGE / 'igloo-agent/agent.py') != payload['agentSha256'].upper() or
            payload['agentSha256'].lower() !=
            'abbe6eca30444a4d42403fd529ee95847f310e5c06305a26b15b82788bddff29'):
        raise RuntimeError('Debian v8 provider agent differs from the tested source')
    if any(path.is_symlink() for path in STAGE.rglob('*')):
        raise RuntimeError('Debian v8 provider stage contains a redirected entry')
    if {path.name for path in (STAGE / 'igloo-agent').iterdir() if path.is_file()} != AGENT_FILES:
        raise RuntimeError('Debian v8 agent inventory differs from provider payload')
    if not all((STAGE / name).is_file() for name in MEDIA_FILES):
        raise RuntimeError('Manifest-selected Debian v8 media is missing')
    grub = (LAB / 'grub.cfg').read_text()
    for token in (
            'igloo.seedpartuuid=' + IDS['seedPartitionGuid'],
            'igloo.seeddiskguid=' + IDS['seedDiskGuid'],
            'igloo.linuxesppartuuid=' + IDS['linuxEspGuid']):
        if grub.lower().count(token.lower()) != 1:
            raise RuntimeError('Debian v8 boot config lacks one exact GPT binding: ' + token.split('=')[0])

    loop = output('losetup', '--find', '--show', '--partscan', str(SEED))
    try:
        if Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() != SEED.resolve():
            raise RuntimeError('The loop is not bound to the disposable v8 seed')
        mount = LAB / 'mnt-seed'
        mount.mkdir(exist_ok=True)
        for _ in range(30):
            if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == IDS['seedDiskGuid'] and
                    output('lsblk', '-dnro', 'PARTUUID', loop + 'p1').lower() == IDS['seedPartitionGuid']):
                break
            time.sleep(.1)
        else:
            raise RuntimeError('Fresh v8 OEMDRV GPT identity was not observed')
        run('mount', '-t', 'vfat', '-o', 'rw', loop + 'p1', str(mount))
        try:
            if sha(mount / 'debian.iso').lower() != IDS['isoSha256']:
                raise RuntimeError('Seed ISO differs from the verified signed Debian image')
            if {path.name for path in mount.iterdir()} != {'debian.iso'}:
                raise RuntimeError('Fresh v8 seed contains stale provider/user content')
            for path in STAGE.rglob('*'):
                if path.is_file():
                    destination = mount / path.relative_to(STAGE)
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(path, destination)
            boot = mount / 'igloo-boot'
            boot.mkdir(exist_ok=True)
            for source, name in (
                (Path('/home/gillesduif/igloo-lab/debian-20261012/vmlinuz'), 'linux'),
                (LAB / 'initrd-v8.gz', 'initrd'),
                (BOOT_SOURCE / 'iso-bootx64.efi', 'shimx64.efi'),
                (BOOT_SOURCE / 'iso-grubx64.efi', 'grubx64.efi'),
            ):
                shutil.copyfile(source, boot / name)
            fallback = mount / 'EFI/BOOT'
            fallback.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(BOOT_SOURCE / 'iso-bootx64.efi', fallback / 'BOOTX64.EFI')
            shutil.copyfile(BOOT_SOURCE / 'iso-grubx64.efi', fallback / 'grubx64.efi')
            for directory in ('EFI/BOOT', 'EFI/debian', 'EFI/ubuntu', 'EFI/fedora', 'boot/grub'):
                destination = mount / directory
                destination.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(LAB / 'grub.cfg', destination / 'grub.cfg')

            observed = {path.name for path in (mount / 'igloo-agent').iterdir() if path.is_file()}
            if observed != AGENT_FILES:
                raise RuntimeError('Copied seed agent inventory differs from provider payload')
            manifest = json.loads((mount / 'migration-manifest.json').read_text())
            if ({manifest['wallpaper']['fileName'], manifest['accountPicture']['fileName']}
                    != MEDIA_FILES):
                raise RuntimeError('Selected media differs from exact v8 manifest')
            bound = ['migration-manifest.json', 'preseed.cfg', 'igloo-boot/initrd']
            bound += sorted(MEDIA_FILES)
            bound += ['igloo-agent/' + name for name in sorted(AGENT_FILES)]
            identity = {
                'version': 1, 'diskGuid': IDS['seedDiskGuid'],
                'partitionGuid': IDS['seedPartitionGuid'],
                'files': [{'path': name, 'sha256': sha(mount / name)} for name in bound],
            }
            (mount / 'igloo-seed-identity.json').write_text(json.dumps(identity, indent=2) + '\n')
            run('sync')
            (LAB / 'seed-identity-v8.json').write_text(json.dumps(identity, indent=2) + '\n')
            (LAB / 'selected-seed-partuuid').write_text(IDS['seedPartitionGuid'] + '\n')
            (LAB / 'selected-seed-diskguid').write_text(IDS['seedDiskGuid'] + '\n')
            run('python3', str(STAGE / 'igloo-initrd/igloo-verify-seed.py'),
                str(LAB / 'selected-seed-partuuid'), str(LAB / 'selected-seed-diskguid'),
                str(mount))
            if sha(mount / 'debian.iso').lower() != IDS['isoSha256']:
                raise RuntimeError('Signed Debian ISO changed during seed binding')
            print(json.dumps({
                'boundFileCount': len(bound),
                'seedPartitionGuid': IDS['seedPartitionGuid'],
                'isoSha256': sha(mount / 'debian.iso'),
                'bootConfigSha256': sha(mount / 'EFI/BOOT/grub.cfg'),
                'identitySha256': sha(mount / 'igloo-seed-identity.json'),
            }, indent=2))
        finally:
            run('umount', str(mount))
        run('fsck.fat', '-n', loop + 'p1')
    finally:
        run('losetup', '--detach', loop)


if __name__ == '__main__':
    main()
