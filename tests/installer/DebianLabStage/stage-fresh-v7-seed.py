"""Bind the fresh disposable v7 OEMDRV payload to its observed GPT identity."""

from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
OLD = Path('/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6')
STAGE = LAB / 'provider-stage-v7'
SEED = LAB / 'seed.raw'
IDS = json.loads((LAB / 'base-identities.json').read_text())


def run(*args):
    subprocess.run(args, check=True)


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b''):
            value.update(block)
    return value.hexdigest().upper()


assert SEED.is_file() and STAGE.is_dir()
assert digest(OLD / 'iso-bootx64.efi') == 'E103C5D02657879F141B086ED703584D28BB9E2721B423333BE085EE1B4D397B'
assert digest(OLD / 'iso-grubx64.efi') == 'B102E12508C773796D102D58F81BB8A00357C01DAA09092AE1D2B1D1702BC310'
assert digest(LAB / 'grub.cfg') == '16419CF6C1FB6DDFEE53E66E1BBB43C6ED42E2206D55FBED58CC2A8009513D53'
assert digest(STAGE / 'migration-manifest.json') == digest(LAB / 'manifest.json')
assert not (LAB / 'seed-identity-v7.json').exists()

loop = output('losetup', '--find', '--show', '--partscan', str(SEED))
assert Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() == SEED.resolve()
mount = LAB / 'mnt-seed'
mount.mkdir(exist_ok=True)
try:
    for attempt in range(30):
        if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == IDS['seedDiskGuid'] and
                output('lsblk', '-dnro', 'PARTUUID', loop + 'p1').lower() == IDS['seedPartitionGuid']):
            break
        time.sleep(.1)
    else:
        raise RuntimeError('Fresh OEMDRV GPT identity was not observed')
    run('mount', '-t', 'vfat', '-o', 'rw', loop + 'p1', str(mount))
    try:
        assert digest(mount / 'debian.iso').lower() == IDS['isoSha256']
        for path in STAGE.rglob('*'):
            if path.is_file():
                target = mount / path.relative_to(STAGE)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(path, target)
        boot = mount / 'igloo-boot'
        boot.mkdir(exist_ok=True)
        for source, name in (
            (Path('/home/gillesduif/igloo-lab/debian-20261012/vmlinuz'), 'linux'),
            (LAB / 'initrd-v7.gz', 'initrd'),
            (OLD / 'iso-bootx64.efi', 'shimx64.efi'),
            (OLD / 'iso-grubx64.efi', 'grubx64.efi'),
        ):
            shutil.copyfile(source, boot / name)
        fallback = mount / 'EFI/BOOT'
        fallback.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(OLD / 'iso-bootx64.efi', fallback / 'BOOTX64.EFI')
        shutil.copyfile(OLD / 'iso-grubx64.efi', fallback / 'grubx64.efi')
        for dirname in ('EFI/BOOT', 'EFI/debian', 'EFI/ubuntu', 'EFI/fedora', 'boot/grub'):
            target = mount / dirname
            target.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(LAB / 'grub.cfg', target / 'grub.cfg')
        expected = sorted((
            'agent.py', 'first-boot.sh', 'display-apply.py', 'display-apply-gnome.py',
            'igloo_boot.py', 'igloo-first-boot.service',
            'grub-theme-stylish-1080p.tar.gz', 'grub-theme-stylish-4k.tar.gz',
        ))
        observed = sorted(path.name for path in (mount / 'igloo-agent').iterdir() if path.is_file())
        assert observed == expected, (observed, expected)
        bound = ['migration-manifest.json', 'preseed.cfg', 'igloo-boot/initrd']
        bound += ['igloo-agent/' + name for name in expected]
        identity = {
            'version': 1, 'diskGuid': IDS['seedDiskGuid'],
            'partitionGuid': IDS['seedPartitionGuid'],
            'files': [{'path': name, 'sha256': digest(mount / name)} for name in bound],
        }
        (mount / 'igloo-seed-identity.json').write_text(json.dumps(identity, indent=2) + '\n')
        run('sync')
        (LAB / 'seed-identity-v7.json').write_text(json.dumps(identity, indent=2) + '\n')
        (LAB / 'selected-seed-partuuid').write_text(IDS['seedPartitionGuid'] + '\n')
        (LAB / 'selected-seed-diskguid').write_text(IDS['seedDiskGuid'] + '\n')
        run('python3', str(STAGE / 'igloo-initrd/igloo-verify-seed.py'),
            str(LAB / 'selected-seed-partuuid'), str(LAB / 'selected-seed-diskguid'), str(mount))
        assert digest(mount / 'debian.iso').lower() == IDS['isoSha256']
        print(json.dumps({
            'boundFileCount': len(bound),
            'stageFileCount': sum(path.is_file() for path in STAGE.rglob('*')),
            'isoSha256': digest(mount / 'debian.iso'),
            'bootConfigSha256': digest(mount / 'EFI/BOOT/grub.cfg'),
            'identitySha256': digest(mount / 'igloo-seed-identity.json'),
        }, indent=2))
    finally:
        run('umount', str(mount))
    run('fsck.fat', '-n', loop + 'p1')
finally:
    run('losetup', '--detach', loop)
