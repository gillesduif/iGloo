"""Read back selected Debian v8 first-boot results from exact disposable GPTs.

Run only after the VM has shut down. This mounts the target and OEMDRV read-only;
it does not mutate either image or assert physical erasure of FAT32 old blocks.
"""

from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import stat
import subprocess
import time
import uuid


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
TARGET = LAB / 'selected.raw'
SEED = LAB / 'seed.raw'
ROOT_MOUNT = LAB / 'mnt-installed-root-ro'
SEED_MOUNT = LAB / 'mnt-seed-ro'


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def digest(path):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b''):
            result.update(block)
    return result.hexdigest()


def exact_loop(image, disk_guid, partition_guid, number):
    loop = output('losetup', '--find', '--show', '--partscan', '--read-only', str(image))
    try:
        if Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() != image.resolve():
            raise RuntimeError('Loop backing path differs from the disposable image')
        for _ in range(30):
            if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == disk_guid and
                    output('lsblk', '-dnro', 'PARTUUID', loop + f'p{number}').lower() == partition_guid):
                return loop
            time.sleep(.1)
        raise RuntimeError('Exact disposable GPT identity unavailable')
    except BaseException:
        subprocess.run(['losetup', '--detach', loop], check=True)
        raise


def installed_packages(status_path):
    installed = set()
    for stanza in status_path.read_text().split('\n\n'):
        fields = dict(line.split(': ', 1) for line in stanza.splitlines()
                      if line.startswith(('Package: ', 'Status: ')))
        if fields.get('Status') == 'install ok installed':
            installed.add(fields.get('Package'))
    return installed


def selected_wifi_profile(root, ssid):
    prefix = re.sub(r'[^A-Za-z0-9._-]', '_', ssid)[:48].strip('_') or 'wifi'
    name = 'igloo-' + prefix + '-' + hashlib.sha256(ssid.encode()).hexdigest() + '.nmconnection'
    path = root / 'etc/NetworkManager/system-connections' / name
    if path.is_symlink() or stat.S_IMODE(path.stat().st_mode) != 0o600:
        raise RuntimeError('Selected Wi-Fi profile is redirected or not owner-only: ' + name)
    return path, path.read_text()


def main():
    if os.geteuid() != 0 or LAB.resolve() != LAB:
        raise RuntimeError('Run as root against the exact disposable v8 lab')
    if output('losetup', '-a') or subprocess.run(
            ['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode != 1:
        raise RuntimeError('QEMU or loop resource is still active')
    identities = json.loads((LAB / 'base-identities.json').read_text())
    expected = json.loads((LAB / 'payload-summary-v8.json').read_text())
    manifest = json.loads((LAB / 'manifest.json').read_text())
    if (TARGET.stat().st_size != 30 << 30 or SEED.stat().st_size != 5 << 30 or
            manifest['hardware']['targetDiskGuid'] != identities['selectedDiskGuid'] or
            manifest['hardware']['needsNonFreeCodecs'] is not True):
        raise RuntimeError('Disposable v8 target, seed, or selection changed')
    target_loop = exact_loop(TARGET, identities['selectedDiskGuid'], identities['linuxEspGuid'], 3)
    seed_loop = None
    try:
        seed_loop = exact_loop(SEED, identities['seedDiskGuid'], identities['seedPartitionGuid'], 1)
        ROOT_MOUNT.mkdir(exist_ok=True)
        SEED_MOUNT.mkdir(exist_ok=True)
        subprocess.run(['mount', '-o', 'ro,noload,nodev,nosuid', target_loop + 'p4', str(ROOT_MOUNT)], check=True)
        try:
            subprocess.run(['mount', '-t', 'vfat', '-o', 'ro,nodev,nosuid', seed_loop + 'p1', str(SEED_MOUNT)], check=True)
            try:
                root = ROOT_MOUNT
                seed = SEED_MOUNT
                username = manifest['user']['preferredLinuxUsername']
                home = root / 'home' / username
                files = {
                    'documents': home / 'Documents/igloo-debian-v8.txt',
                    'firefox': home / '.mozilla/firefox/Profiles/igloolab.default/igloo-profile-marker.txt',
                    'chromeBookmarks': home / '.var/app/com.google.Chrome/config/google-chrome/Default/Bookmarks',
                    'chromeSession': home / '.var/app/com.google.Chrome/config/google-chrome/Default/Sessions/Session_123',
                }
                pins = {'documents': 'documentsSha256', 'firefox': 'firefoxMarkerSha256',
                        'chromeBookmarks': 'chromeBookmarksSha256', 'chromeSession': 'chromeSessionSha256'}
                migrated = {key: {'sha256': digest(path), 'uid': path.stat().st_uid,
                                  'mode': oct(stat.S_IMODE(path.stat().st_mode))}
                            for key, path in files.items()}
                for key, item in migrated.items():
                    if item['sha256'] != expected[pins[key]] or item['uid'] != 1000:
                        raise RuntimeError('Selected migration differs: ' + key)

                media = {
                    'wallpaperStaged': root / 'opt/igloo/igloo-wallpaper.PNG',
                    'wallpaperUser': home / 'Pictures/wallpaper.PNG',
                    'avatarStaged': root / 'opt/igloo/igloo-avatar.jpg',
                    'avatarGreeter': root / 'var/lib/AccountsService/icons' / username,
                    'avatarUser': home / '.face',
                }
                media_hashes = {key: digest(path) for key, path in media.items()}
                for key in ('wallpaperStaged', 'wallpaperUser'):
                    if media_hashes[key] != expected['selectedMediaSha256']['igloo-wallpaper.PNG']:
                        raise RuntimeError('Selected wallpaper differs: ' + key)
                for key in ('avatarStaged', 'avatarGreeter', 'avatarUser'):
                    if media_hashes[key] != expected['selectedMediaSha256']['igloo-avatar.jpg']:
                        raise RuntimeError('Selected account picture differs: ' + key)
                icon_config = (root / 'var/lib/AccountsService/users' / username).read_text()
                if f'Icon=/var/lib/AccountsService/icons/{username}' not in icon_config:
                    raise RuntimeError('Selected avatar is not bound in AccountsService')
                wallpaper_config = (root / 'etc/dconf/db/local.d/00-igloo-wallpaper').read_text()
                if f'file:///home/{username}/Pictures/wallpaper.PNG' not in wallpaper_config:
                    raise RuntimeError('Selected wallpaper is not bound in dconf defaults')

                selected = manifest['wifiNetworks']
                if len(selected) != 2 or selected[0]['psk'] is None or selected[1]['psk'] is not None:
                    raise RuntimeError('Selected v8 Wi-Fi fixture changed')
                keyed_path, keyed = selected_wifi_profile(root, selected[0]['ssid'])
                prompt_path, promptable = selected_wifi_profile(root, selected[1]['ssid'])
                wifi_uuids = [str(uuid.uuid5(uuid.NAMESPACE_URL, 'igloo:wifi:' + net['ssid']))
                              for net in selected]
                if (keyed_path == prompt_path or
                        f"ssid={selected[0]['ssid']}" not in keyed or
                        f'uuid={wifi_uuids[0]}' not in keyed or
                        f"psk={selected[0]['psk']}" not in keyed or
                        'key-mgmt=wpa-psk' not in keyed or
                        'hidden=true' not in keyed or
                        'autoconnect=true' not in keyed or
                        f"ssid={selected[1]['ssid']}" not in promptable or
                        f'uuid={wifi_uuids[1]}' not in promptable or
                        'key-mgmt=wpa-psk' not in promptable or
                        'psk-flags=1' not in promptable or
                        'autoconnect=false' not in promptable or
                        re.search(r'^psk=', promptable, re.MULTILINE)):
                    raise RuntimeError('Selected Wi-Fi profile content differs')

                installed = installed_packages(root / 'var/lib/dpkg/status')
                codecs = {'libavcodec-extra', 'gstreamer1.0-libav',
                          'gstreamer1.0-plugins-ugly', 'gstreamer1.0-plugins-bad'}
                if not codecs <= installed or 'tree' not in installed:
                    raise RuntimeError('Selected codecs or tree package was not installed')
                if not (root / 'var/lib/flatpak/app/com.google.Chrome').exists():
                    raise RuntimeError('Selected Chrome Flatpak was not installed')

                log = (root / 'var/log/igloo/agent.log').read_text()
                required_steps = ['wifi', 'codecs', 'suggested-pkgs', 'user-files', 'wallpaper',
                                  'account-picture', 'stage-credentials', 'redact-seed',
                                  'redact-manifest', 'cleanup-seed']
                positions = [log.index('--- step: ' + name + ' ---') for name in required_steps]
                if positions != sorted(positions) or not all(
                        'step ' + name + ': OK' in log for name in required_steps):
                    raise RuntimeError('Selected first-boot step order or outcome differs')
                if 'first-boot agent done (0 failure(s))' not in log:
                    raise RuntimeError('First-boot agent did not finish successfully')

                state = root / 'var/lib/igloo'
                installed_manifest = state / 'manifest.json'
                if (not (state / '.done').is_file() or (state / 'manifest.retry.json').exists()
                        or (state / 'seed-retry').exists() or
                        stat.S_IMODE(installed_manifest.stat().st_mode) != 0o600):
                    raise RuntimeError('Durable first-boot completion or private cleanup missing')
                credential_store = home / '.local/share/igloo/credentials.json'
                credential_retired = not credential_store.exists()
                if credential_retired:
                    login_data = home / '.var/app/com.google.Chrome/config/google-chrome/Default/Login Data'
                    if not login_data.is_file() or login_data.is_symlink():
                        raise RuntimeError('Retired credential envelope has no Chrome Login Data')
                    with closing(sqlite3.connect(f'file:{login_data}?mode=ro', uri=True)) as connection:
                        rows = connection.execute(
                            'SELECT password_value FROM logins WHERE origin_url=? AND username_value=?',
                            ('https://example.invalid/igloo-v8-login', 'synthetic-igloo-user-v8'),
                        ).fetchall()
                    if len(rows) != 1 or not bytes(rows[0][0]).startswith(b'v10'):
                        raise RuntimeError('Retired credential envelope lacks the exact selected login')
                elif (not credential_store.is_file() or credential_store.stat().st_uid != 1000 or
                      stat.S_IMODE(credential_store.stat().st_mode) != 0o600):
                    raise RuntimeError('Selected login envelope is not staged privately')
                welcome_marker = home / '.igloo-welcome-done'
                welcome_marker_valid = (welcome_marker.is_file() and
                                        welcome_marker.stat().st_uid == 1000)
                credential_log = home / '.local/state/igloo-credentials.log'
                public_hooks = {
                    'opt': root / 'opt',
                    'optIgloo': root / 'opt/igloo',
                    'iglooBoot': root / 'opt/igloo/igloo_boot.py',
                    'xdg': root / 'etc/xdg',
                    'xdgAutostart': root / 'etc/xdg/autostart',
                    'welcomeAutostart': root / 'etc/xdg/autostart/igloo-welcome.desktop',
                    'welcomeScript': root / 'opt/igloo/igloo-welcome.sh',
                    'credentialAutostart': root / 'etc/xdg/autostart/igloo-credential-import.desktop',
                }
                required_modes = {'opt': 0o755, 'optIgloo': 0o755,
                                  'iglooBoot': 0o644, 'xdg': 0o755,
                                  'xdgAutostart': 0o755, 'welcomeAutostart': 0o644,
                                  'welcomeScript': 0o755, 'credentialAutostart': 0o644}
                for name, path in public_hooks.items():
                    if stat.S_IMODE(path.stat().st_mode) != required_modes[name]:
                        raise RuntimeError('Selected first-login hook is not user-readable: ' + name)
                secrets = [manifest['user']['linuxPasswordCrypted'].encode(),
                           selected[0]['psk'].encode(),
                           next(browser['credentialsBlob'].encode() for browser in manifest['browsers']
                                if browser['name'] == 'Google Chrome')]
                secret_files = {
                    'seedManifest': seed / 'migration-manifest.json',
                    'seedPreseed': seed / 'preseed.cfg',
                    'seedInitrd': seed / 'igloo-boot/initrd',
                    'installedManifest': installed_manifest,
                }
                if any(value in path.read_bytes() for path in secret_files.values() for value in secrets):
                    raise RuntimeError('A selected secret remains in an addressable manifest or boot file')
                result = {
                    'selectedDiskGuid': identities['selectedDiskGuid'],
                    'seedDiskGuid': identities['seedDiskGuid'],
                    'linuxEspGuid': identities['linuxEspGuid'],
                    'done': True,
                    'requiredSteps': required_steps,
                    'selectedFileReadback': migrated,
                    'selectedMediaSha256': media_hashes,
                    'privateWifiProfiles': [keyed_path.name, prompt_path.name],
                    'selectedWifiUuidsForLiveNmcliCheck': wifi_uuids,
                    'promptableWifiHasNoPsk': True,
                    'installedCodecPackages': sorted(codecs),
                    'treeInstalled': True,
                    'chromeFlatpakInstalled': True,
                    'credentialEnvelopeRetiredAfterFirstLogin': credential_retired,
                    'credentialLogTail': credential_log.read_text(errors='replace')[-1000:]
                    if credential_log.is_file() else '',
                    'welcomeMarkerValid': welcome_marker_valid,
                    'firstLoginHookModes': {name: oct(mode) for name, mode in required_modes.items()},
                    'addressableSecretFilesRedacted': list(secret_files),
                }
                (LAB / 'firstboot-readback-v8.json').write_text(json.dumps(result, indent=2) + '\n')
                print(json.dumps(result, indent=2))
            finally:
                subprocess.run(['umount', str(SEED_MOUNT)], check=True)
        finally:
            subprocess.run(['umount', str(ROOT_MOUNT)], check=True)
    finally:
        if seed_loop:
            subprocess.run(['losetup', '--detach', seed_loop], check=True)
        subprocess.run(['losetup', '--detach', target_loop], check=True)


if __name__ == '__main__':
    main()
