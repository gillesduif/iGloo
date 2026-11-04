"""Read-only content and secret readback of the disposable Debian v7 boot."""

from pathlib import Path
import hashlib
import json
import os
import stat
import subprocess
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
IDS = json.loads((LAB / 'base-identities.json').read_text())
EXPECTED = json.loads((LAB / 'payload-summary-v7.json').read_text())
MANIFEST = json.loads((LAB / 'manifest.json').read_text())
TARGET = LAB / 'selected.raw'
SEED = LAB / 'seed.raw'
ROOT_MOUNT = LAB / 'mnt-installed-root-ro'
SEED_MOUNT = LAB / 'mnt-seed-ro'
assert not subprocess.check_output(['losetup', '-a']).strip()
assert subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode == 1
assert TARGET.stat().st_size == 30 << 30 and SEED.stat().st_size == 5 << 30


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def exact_loop(image, disk_guid, part_guid, number):
    loop = output('losetup', '--find', '--show', '--partscan', '--read-only', str(image))
    assert Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() == image.resolve()
    for _ in range(30):
        if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == disk_guid and
                output('lsblk', '-dnro', 'PARTUUID', loop + f'p{number}').lower() == part_guid):
            return loop
        time.sleep(.1)
    raise RuntimeError('Exact disposable GPT identity unavailable')


target_loop = exact_loop(TARGET, IDS['selectedDiskGuid'], IDS['linuxEspGuid'], 3)
seed_loop = None
try:
    seed_loop = exact_loop(SEED, IDS['seedDiskGuid'], IDS['seedPartitionGuid'], 1)
    ROOT_MOUNT.mkdir(exist_ok=True)
    SEED_MOUNT.mkdir(exist_ok=True)
    subprocess.run(['mount', '-o', 'ro,noload,nodev,nosuid', target_loop + 'p4', str(ROOT_MOUNT)], check=True)
    try:
        subprocess.run(['mount', '-t', 'vfat', '-o', 'ro,nodev,nosuid', seed_loop + 'p1', str(SEED_MOUNT)], check=True)
        try:
            home = ROOT_MOUNT / 'home/igloolab'
            files = {
                'documents': home / 'Documents/igloo-debian-v7.txt',
                'firefox': home / '.mozilla/firefox/Profiles/igloolab.default/igloo-profile-marker.txt',
                'chromeBookmarks': home / '.var/app/com.google.Chrome/config/google-chrome/Default/Bookmarks',
                'chromeSession': home / '.var/app/com.google.Chrome/config/google-chrome/Default/Sessions/Session_123',
            }
            pins = {'documents': 'documentsSha256', 'firefox': 'firefoxMarkerSha256',
                    'chromeBookmarks': 'chromeBookmarksSha256', 'chromeSession': 'chromeSessionSha256'}
            migrated = {key: {'sha256': digest(path), 'expected': EXPECTED[pins[key]],
                              'uid': path.stat().st_uid, 'mode': oct(stat.S_IMODE(path.stat().st_mode))}
                        for key, path in files.items()}
            assert all(item['sha256'] == item['expected'] and item['uid'] == 1000
                       for item in migrated.values())
            state = ROOT_MOUNT / 'var/lib/igloo'
            installed_manifest = state / 'manifest.json'
            credential_store = home / '.local/share/igloo/credentials.json'
            secret_values = [MANIFEST['user']['linuxPasswordCrypted'].encode(),
                             MANIFEST['wifiNetworks'][0]['psk'].encode(),
                             next(b['credentialsBlob'].encode() for b in MANIFEST['browsers']
                                  if b['name'] == 'Google Chrome')]
            secret_files = [
                SEED_MOUNT / 'migration-manifest.json',
                SEED_MOUNT / 'preseed.cfg',
                SEED_MOUNT / 'igloo-boot/initrd',
                installed_manifest,
                ROOT_MOUNT / 'var/log/igloo/bootstrap.log',
                ROOT_MOUNT / 'var/log/igloo/agent.log',
            ]
            secret_absent = {str(path.relative_to(SEED_MOUNT if path.is_relative_to(SEED_MOUNT)
                                                 else ROOT_MOUNT)): all(value not in path.read_bytes()
                                                                           for value in secret_values)
                             for path in secret_files}
            assert all(secret_absent.values()), secret_absent
            result = {
                'selectedDiskGuid': IDS['selectedDiskGuid'],
                'seedDiskGuid': IDS['seedDiskGuid'],
                'linuxEspGuid': IDS['linuxEspGuid'],
                'done': (state / '.done').is_file(),
                'retryAbsent': not (state / 'manifest.retry.json').exists()
                               and not (state / 'seed-retry').exists(),
                'installedManifestMode': oct(stat.S_IMODE(installed_manifest.stat().st_mode)),
                'credentialStoreMode': oct(stat.S_IMODE(credential_store.stat().st_mode)),
                'credentialStoreUid': credential_store.stat().st_uid,
                'credentialAutostartMode': oct(stat.S_IMODE((ROOT_MOUNT /
                    'etc/xdg/autostart/igloo-credential-import.desktop').stat().st_mode)),
                'optIglooMode': oct(stat.S_IMODE((ROOT_MOUNT / 'opt/igloo').stat().st_mode)),
                'iglooBootMode': oct(stat.S_IMODE((ROOT_MOUNT / 'opt/igloo/igloo_boot.py').stat().st_mode)),
                'nativeTreeInstalled': (ROOT_MOUNT / 'usr/bin/tree').is_file(),
                'chromeFlatpakInstalled': (ROOT_MOUNT / 'var/lib/flatpak/app/com.google.Chrome').exists(),
                'migrated': migrated,
                'originalSecretsAbsentFromAddressableFiles': secret_absent,
            }
            assert result['done'] and result['retryAbsent']
            assert result['installedManifestMode'] == '0o600'
            assert result['credentialStoreMode'] == '0o600' and result['credentialStoreUid'] == 1000
            assert result['credentialAutostartMode'] == '0o644'
            assert result['optIglooMode'] == '0o755' and result['iglooBootMode'] == '0o644'
            assert result['nativeTreeInstalled'] and result['chromeFlatpakInstalled']
            (LAB / 'firstboot-readback-v7.json').write_text(json.dumps(result, indent=2) + '\n')
            print(json.dumps(result, indent=2))
        finally:
            subprocess.run(['umount', str(SEED_MOUNT)], check=True)
    finally:
        subprocess.run(['umount', str(ROOT_MOUNT)], check=True)
finally:
    if seed_loop:
        subprocess.run(['losetup', '--detach', seed_loop], check=True)
    subprocess.run(['losetup', '--detach', target_loop], check=True)
