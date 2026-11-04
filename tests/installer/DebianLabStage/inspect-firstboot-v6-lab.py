"""Read-only result inspection of the disposable Debian v6 disk images."""

from contextlib import ExitStack
from pathlib import Path
import hashlib
import json
import os
import subprocess


lab = Path('/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6')
root_mount = lab / 'mnt-firstboot-root-ro'
seed_mount = lab / 'mnt-firstboot-seed-ro'
root_mount.mkdir(exist_ok=True)
seed_mount.mkdir(exist_ok=True)
assert subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode == 1
assert not subprocess.check_output(['losetup', '-a']).strip()
source_manifest = json.loads((lab / 'manifest.json').read_text())
original_secret = source_manifest['user']['linuxPasswordCrypted'].encode()
assert original_secret and original_secret.startswith(b'$6$')
expected = json.loads((lab / 'payload-summary.json').read_text())


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def attach(path):
    return subprocess.check_output(
        ['losetup', '--find', '--show', '--partscan', '--read-only', str(path)], text=True
    ).strip()


with ExitStack() as cleanup:
    root_loop = attach(lab / 'selected.raw')
    cleanup.callback(subprocess.run, ['losetup', '-d', root_loop], check=True)
    seed_loop = attach(lab / 'seed.raw')
    cleanup.callback(subprocess.run, ['losetup', '-d', seed_loop], check=True)
    subprocess.run(['mount', '-o', 'ro,noload', root_loop + 'p4', str(root_mount)], check=True)
    cleanup.callback(subprocess.run, ['umount', str(root_mount)], check=True)
    subprocess.run(['mount', '-o', 'ro', seed_loop + 'p1', str(seed_mount)], check=True)
    cleanup.callback(subprocess.run, ['umount', str(seed_mount)], check=True)

    state = root_mount / 'var/lib/igloo'
    print('done:', (state / '.done').exists())
    print('retry manifest:', (state / 'manifest.retry.json').exists())
    print('seed retry:', (state / 'seed-retry').exists())
    print('seed scrub journal:', (state / 'seed-scrub-state.json').exists())
    print('reboot required:', (state / '.reboot-required').exists())
    for path in sorted((root_mount / 'var/log').glob('*igloo*')):
        print('log:', path.relative_to(root_mount), path.stat().st_size)
        if path.is_dir():
            for child in sorted(path.iterdir()):
                print(' log file:', child.relative_to(root_mount), child.stat().st_size)
                if child.is_file() and child.stat().st_size < 2_000_000:
                    lines = child.read_text(errors='replace').splitlines()
                    for line in lines[-35:]:
                        if original_secret.decode() not in line:
                            print('  ', line)
        if path.is_file() and path.stat().st_size < 2_000_000:
            lines = path.read_text(errors='replace').splitlines()
            for line in lines[-30:]:
                if original_secret.decode() not in line:
                    print(' ', line)

    installed_manifest = state / 'manifest.json'
    if installed_manifest.exists():
        content = installed_manifest.read_bytes()
        print('installed manifest mode:', oct(installed_manifest.stat().st_mode & 0o777))
        print('installed manifest retains secret:', original_secret in content)
        print('installed manifest credential cleared:',
              not json.loads(content)['user'].get('linuxPasswordCrypted'))
    else:
        print('installed manifest missing')

    files = {
        'document': root_mount / 'home/igloolab/Documents/igloo-debian-v6.txt',
        'chrome bookmarks': root_mount / 'home/igloolab/.config/google-chrome/Default/Bookmarks',
        'chrome session': root_mount / 'home/igloolab/.config/google-chrome/Default/Sessions/Session_123',
        'firefox marker': root_mount / 'home/igloolab/.mozilla/firefox/igloolab.default/igloo-profile-marker.txt',
    }
    for label, path in files.items():
        if path.exists():
            value = sha256(path)
            status = os.stat(path)
            print(label, 'sha256:', value, 'mode:', oct(status.st_mode & 0o777),
                  'uid:', status.st_uid, 'gid:', status.st_gid)
        else:
            print(label, 'MISSING')
    for path in (root_mount / 'home/igloolab').rglob('igloo-profile-marker.txt'):
        print('firefox actual marker:', path.relative_to(root_mount), sha256(path))
    print('expected document:', expected['documentSha256'])
    print('expected chrome bookmarks:', expected['chromeBookmarksSha256'])
    print('expected chrome session:', expected['chromeSessionSha256'])

    for name in ('migration-manifest.json', 'preseed.cfg', 'igloo-boot/initrd'):
        path = seed_mount / name
        print('seed file:', name, 'exists:', path.exists(),
              'retains exact secret:', original_secret in path.read_bytes() if path.exists() else None)

    print('fstab ESP:', [line for line in (root_mount / 'etc/fstab').read_text().splitlines()
                         if '/boot/efi' in line and not line.lstrip().startswith('#')])
