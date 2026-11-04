"""Bind a small current-source OEMDRV to the disposable v6 installed child."""

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
import uuid


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/current-firstboot-v6-child')
STAGE_SOURCE = Path('/mnt/c/Users/Public/igloo-debian-firstboot-v6-child-stage-20261018/output-rebuilt')
STAGE = LAB / 'provider-stage'
SEED = LAB / 'seed.raw'
ROOT = LAB / 'mounted-root'
CHILD = LAB / 'selected-child.qcow2'
MEDIA = Path('/home/gillesduif/igloo-mint-22.3-lab/mint-selected-media-v14')
FIREFOX = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7/provider-stage-v7/browser-profiles/AppData/Roaming/Mozilla/Firefox')
REPO = Path(__file__).resolve().parents[3]
EXPECTED_AGENT = '254ee58ff3b7fb59430b35a6af097f0496b30e48389fce87908b915fc2cd68af'
EXPECTED_MEDIA = {
    'igloo-wallpaper.PNG': 'f3d180c7610e4a3fce2d54ed6681d12c8d202c4b949ab5a576c601e188efcac6',
    'igloo-avatar.jpg': 'ad1e7511713f030a8a1d73f36f26d70eda212a2b43e9ea8d2ad7b4e0271c4c4a',
}
AGENT_FILES = {
    'agent.py', 'first-boot.sh', 'display-apply.py', 'display-apply-gnome.py',
    'igloo_boot.py', 'igloo-first-boot.service',
    'grub-theme-stylish-1080p.tar.gz', 'grub-theme-stylish-4k.tar.gz',
}
GIB = 1024 ** 3


def run(*args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def guid(text, kind):
    pattern = ('Disk identifier \\(GUID\\): ([0-9A-Fa-f-]{36})' if kind == 'disk'
               else 'Partition unique GUID: ([0-9A-Fa-f-]{36})')
    match = re.search(pattern, text)
    if match is None:
        raise RuntimeError('No exact observed seed GPT ' + kind + ' GUID')
    return str(uuid.UUID(match.group(1)))


def require_idle():
    if output('losetup', '-a'):
        raise RuntimeError('Another loop is active')
    if subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'],
                      capture_output=True).returncode != 1:
        raise RuntimeError('Another QEMU guest is active')
    if output('blockdev', '--getsize64', '/dev/nbd0') != '0':
        raise RuntimeError('NBD0 is not idle')


def main():
    if (os.geteuid() != 0 or LAB.resolve() != LAB or
            not CHILD.is_file() or CHILD.is_symlink() or
            not STAGE.is_dir() or STAGE.is_symlink() or list(STAGE.iterdir()) or
            SEED.exists() or (LAB / 'seed-identity.json').exists() or
            shutil.disk_usage('/mnt/c').free < 22 * GIB):
        raise RuntimeError('The exact child, fresh stage, or 22 GiB C: margin is missing')
    require_idle()
    prep = json.loads((LAB / 'preparation.json').read_text())
    manifest = json.loads((LAB / 'manifest.json').read_text())
    if (prep['childDiskGuid'] != manifest['hardware']['targetDiskGuid'] or
            manifest['hardware']['needsNonFreeCodecs'] is not True or
            {manifest['wallpaper']['fileName'], manifest['accountPicture']['fileName']}
            != set(EXPECTED_MEDIA) or
            not STAGE_SOURCE.is_dir() or STAGE_SOURCE.is_symlink()):
        raise RuntimeError('The disposable manifest or current provider stage changed')
    if any(path.is_symlink() for path in STAGE_SOURCE.rglob('*')):
        raise RuntimeError('Provider staging contains a redirected file')
    if (sha(STAGE_SOURCE / 'migration-manifest.json') != sha(LAB / 'manifest.json') or
            sha(STAGE_SOURCE / 'igloo-agent/agent.py') != EXPECTED_AGENT or
            'mountpoint -q /run/igloo-seed' not in
            (STAGE_SOURCE / 'preseed.cfg').read_text()):
        raise RuntimeError('Current provider bytes do not match the reviewed inputs')
    observed_agent = {item.name for item in (STAGE_SOURCE / 'igloo-agent').iterdir()
                      if item.is_file()}
    if observed_agent != AGENT_FILES:
        raise RuntimeError('The Debian provider agent inventory changed')
    shutil.copytree(STAGE_SOURCE, STAGE, dirs_exist_ok=True)
    for name, expected in EXPECTED_MEDIA.items():
        source = MEDIA / name
        if source.is_symlink() or not source.is_file() or sha(source) != expected:
            raise RuntimeError('The selected synthetic media changed: ' + name)
        shutil.copyfile(source, STAGE / name)

    documents = STAGE / 'files/Documents'
    documents.mkdir(parents=True)
    (documents / 'igloo-current-v6-child.txt').write_text(
        'Selected Documents marker: Debian current-agent v6 child\n')
    firefox = STAGE / 'browser-profiles/AppData/Roaming/Mozilla/Firefox'
    if FIREFOX.is_symlink() or not FIREFOX.is_dir():
        raise RuntimeError('The selected Firefox fixture is missing')
    shutil.copytree(FIREFOX, firefox)
    (firefox / 'Profiles/igloolab.default/igloo-current-child.txt').write_text(
        'Selected Firefox marker: Debian current-agent v6 child\n')
    chrome = STAGE / 'browser-profiles/AppData/Local/Google/Chrome/User Data/Default'
    (chrome / 'Sessions').mkdir(parents=True)
    (chrome / 'Bookmarks').write_text(json.dumps({
        'version': 1,
        'roots': {'bookmark_bar': {'type': 'folder', 'name': 'Bookmarks bar',
                  'children': [{'type': 'url', 'name': 'iGloo current child',
                                'url': 'https://example.invalid/igloo-current-v6-child'}]}},
    }, sort_keys=True) + '\n')
    (chrome / 'Sessions/Session_456').write_text(
        'Selected Chromium session marker: Debian current-agent v6 child\n')
    if sha(STAGE / 'migration-manifest.json') != sha(LAB / 'manifest.json'):
        raise RuntimeError('Selected user staging changed the provider manifest')

    run('truncate', '-s', str(256 * 1024 * 1024), str(SEED))
    run('sgdisk', '-o', '-n', '1:2048:0', '-t', '1:EF00', '-c', '1:OEMDRV', str(SEED))
    loop = output('losetup', '--find', '--show', '--partscan', str(SEED))
    mounted = False
    mount = LAB / 'mounted-seed'
    mount.mkdir(mode=0o700)
    try:
        if Path(output('losetup', '-n', '-O', 'BACK-FILE', loop)).resolve() != SEED.resolve():
            raise RuntimeError('The FAT loop backing path changed')
        part = loop + 'p1'
        for _ in range(30):
            if Path(part).exists():
                break
            time.sleep(.1)
        else:
            raise RuntimeError('The seed partition node is unavailable')
        seed_disk_guid = guid(output('sgdisk', '-p', loop), 'disk')
        seed_part_guid = guid(output('sgdisk', '-i', '1', loop), 'partition')
        run('mkfs.fat', '-F', '32', '-n', 'OEMDRV', part)
        run('mount', '-t', 'vfat', '-o', 'rw', part, str(mount))
        mounted = True
        for source in STAGE.rglob('*'):
            if source.is_file():
                target = mount / source.relative_to(STAGE)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
        bound = ['migration-manifest.json', 'preseed.cfg']
        bound.extend('igloo-agent/' + name for name in sorted(AGENT_FILES))
        bound.extend(sorted(EXPECTED_MEDIA))
        identity = {
            'version': 1, 'diskGuid': seed_disk_guid,
            'partitionGuid': seed_part_guid,
            'files': [{'path': path, 'sha256': sha(mount / path).upper()}
                      for path in bound],
        }
        (mount / 'igloo-seed-identity.json').write_text(json.dumps(identity, indent=2) + '\n')
        run('sync')
        (LAB / 'seed-identity.json').write_text(json.dumps(identity, indent=2) + '\n')
        (LAB / 'selected-seed-partuuid').write_text(seed_part_guid + '\n')
        (LAB / 'selected-seed-diskguid').write_text(seed_disk_guid + '\n')
        (LAB / 'selected-seed-partuuid').chmod(0o600)
        (LAB / 'selected-seed-diskguid').chmod(0o600)
        run('python3', str(REPO / 'distros/_debian-family/agent/igloo-verify-seed.py'),
            str(LAB / 'selected-seed-partuuid'), str(LAB / 'selected-seed-diskguid'), str(mount))
    finally:
        if mounted:
            run('umount', str(mount))
        run('fsck.fat', '-n', loop + 'p1')
        run('losetup', '-d', loop)

    # The v6 installed root is changed only through its independently verified
    # qcow2 child. Its seed selection must match the fresh FAT GPT, not a label.
    run('qemu-nbd', '--connect=/dev/nbd0', '--format=qcow2', str(CHILD))
    mounted = False
    try:
        if (output('blockdev', '--getsize64', '/dev/nbd0') != str(30 * GIB) or
                prep['childDiskGuid'] not in output('sgdisk', '-p', '/dev/nbd0').lower()):
            raise RuntimeError('The selected child changed before seed binding')
        run('mount', '-o', 'rw,nodev,nosuid', '/dev/nbd0p4', str(ROOT))
        mounted = True
        state = ROOT / 'var/lib/igloo'
        bootstrap = ROOT / 'opt/igloo/igloo-bootstrap.sh'
        old_line = ('mount -t vfat -o ro /dev/disk/by-partuuid/$seed_partuuid '
                    '/run/igloo-seed\n')
        new_line = ('if mountpoint -q /run/igloo-seed ; then\n'
                    "    findmnt -rn -o OPTIONS --mountpoint /run/igloo-seed | grep -Eq '(^|,)ro(,|$)' || exit 1\n"
                    'else mount -t vfat -o ro /dev/disk/by-partuuid/$seed_partuuid '
                    '/run/igloo-seed ; fi\n')
        text = bootstrap.read_text()
        if text.count(old_line) != 1 or new_line in text:
            raise RuntimeError('The child bootstrap mount statement changed')
        bootstrap.write_text(text.replace(old_line, new_line, 1))
        bootstrap.chmod(0o755)
        for name, value in (('selected-seed-partuuid', seed_part_guid),
                            ('selected-seed-diskguid', seed_disk_guid)):
            target = state / name
            if target.read_text() != 'PENDING\n':
                raise RuntimeError('The child seed selector was already bound')
            target.write_text(value + '\n')
            target.chmod(0o600)
        if (state / '.done').exists() or (state / 'manifest.retry.json').exists() or \
                (state / 'seed-retry').exists():
            raise RuntimeError('The child retains a completed or partial first-boot state')
        run('sync')
    finally:
        if mounted:
            run('umount', str(ROOT))
        run('qemu-nbd', '--disconnect', '/dev/nbd0')

    summary = {
        'targetChildDiskGuid': prep['childDiskGuid'],
        'seedDiskGuid': seed_disk_guid,
        'seedPartitionGuid': seed_part_guid,
        'seedBytes': SEED.stat().st_size,
        'seedAllocatedBytes': SEED.stat().st_blocks * 512,
        'seedIdentitySha256': sha(LAB / 'seed-identity.json'),
        'providerPreseedSha256': sha(STAGE / 'preseed.cfg'),
        'currentAgentSha256': sha(STAGE / 'igloo-agent/agent.py'),
        'selectedDocumentSha256': sha(documents / 'igloo-current-v6-child.txt'),
        'selectedFirefoxSha256': sha(firefox / 'Profiles/igloolab.default/igloo-current-child.txt'),
        'selectedChromeBookmarksSha256': sha(chrome / 'Bookmarks'),
        'cFreeBytes': shutil.disk_usage('/mnt/c').free,
    }
    (LAB / 'seed-stage.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    main()
