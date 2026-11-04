"""Read-only, bounded observation of the disposable v6 child after shutdown."""

import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/current-firstboot-v6-child')
ROOT = Path('/mnt/igloo-debian-v6-child-root')
SEED = Path('/mnt/igloo-debian-v6-child-seed')
if not os.path.ismount(ROOT) or not os.path.ismount(SEED):
    raise RuntimeError('Exact read-only target and seed mounts are required')
if not all('ro' in Path('/proc/mounts').read_text().splitlines()[i].split()[3].split(',')
           for i, line in enumerate(Path('/proc/mounts').read_text().splitlines())
           if line.split()[1] in (str(ROOT), str(SEED))):
    raise RuntimeError('Readback mounts are not read-only')


def item(relative, *, base=ROOT):
    p = base / relative
    if not p.is_file():
        return {'present': False}
    s = p.stat()
    return {
        'present': True, 'size': s.st_size, 'mode': oct(s.st_mode & 0o777),
        'uid': s.st_uid, 'gid': s.st_gid,
        'sha256': hashlib.sha256(p.read_bytes()).hexdigest(),
    }


status = (ROOT / 'var/lib/dpkg/status').read_text()


def package_installed(name):
    match = re.search(rf'(?m)^Package: {re.escape(name)}\n(?:(?!\n\n)[\s\S])*?^Status: ([^\n]+)$', status)
    return match is not None and match.group(1) == 'install ok installed'


agent_log = (ROOT / 'var/log/igloo/agent.log').read_text(errors='replace')
bootstrap_log = (ROOT / 'var/log/igloo/bootstrap.log').read_text(errors='replace')
cred_log_path = ROOT / 'home/igloolab/.local/state/igloo-credentials.log'
cred_log = cred_log_path.read_text(errors='replace') if cred_log_path.is_file() else ''
store_path = ROOT / 'home/igloolab/.local/share/igloo/credentials.json'
store_metadata = None
if store_path.is_file():
    store = json.loads(store_path.read_text())
    store_metadata = {'attempts': store.get('attempts'), 'browserCount': len(store.get('browsers', []))}

login_data = ROOT / 'home/igloolab/.config/google-chrome/Default/Login Data'
login_rows = []
if login_data.is_file():
    con = sqlite3.connect(f'file:{login_data}?mode=ro&immutable=1', uri=True)
    try:
        login_rows = [
            {'originSha256': hashlib.sha256(row[0].encode()).hexdigest(),
             'usernameSha256': hashlib.sha256(row[1].encode()).hexdigest(),
             'cipherSize': len(row[2]), 'cipherPrefixHex': bytes(row[2])[:3].hex()}
            for row in con.execute('select origin_url, username_value, password_value from logins')
        ]
    finally:
        con.close()

seed_manifest = (SEED / 'migration-manifest.json').read_text()
seed_preseed = (SEED / 'preseed.cfg').read_text()
root_manifest = (ROOT / 'var/lib/igloo/manifest.json').read_text()
seed_temp_paths = [str(p.relative_to(SEED)) for p in SEED.rglob('.igloo-*')]
wifi_dir = ROOT / 'etc/NetworkManager/system-connections'
wifi = []
for p in sorted(wifi_dir.glob('igloo-*.nmconnection')):
    content = p.read_text()
    wifi.append({
        'file': p.name, 'mode': oct(p.stat().st_mode & 0o777),
        'agentOwnedPrompt': 'psk-flags=1' in content,
        'autoconnectFalse': 'autoconnect=false' in content,
        'hasKey': re.search(r'(?m)^psk=', content) is not None,
    })

report = {
    'childDiskGuid': (ROOT / 'var/lib/igloo/manifest.json').read_text().count('8566bc03-e293-4f7d-a199-f92d121bd5df') > 0,
    'done': item('var/lib/igloo/.done'),
    'retryManifest': item('var/lib/igloo/manifest.retry.json'),
    'retrySeed': item('var/lib/igloo/seed-retry/seed-identity.json'),
    'agentRecentSuccess': '=== Igloo first-boot agent done (0 failure(s)) ===' in agent_log.split('2026-10-18')[-1],
    'bootstrapRecentSuccess': '=== Igloo first-boot agent done (0 failure(s)) ===' in bootstrap_log.split('2026-10-18')[-1],
    'credentialLog': cred_log.splitlines()[-8:],
    'credentialStore': item('home/igloolab/.local/share/igloo/credentials.json'),
    'credentialStoreMetadata': store_metadata,
    'loginRows': login_rows,
    'welcomeMarker': item('home/igloolab/.igloo-welcome-done'),
    'packages': {name: package_installed(name) for name in ('tree', 'libavcodec-extra', 'gstreamer1.0-libav', 'gstreamer1.0-plugins-ugly', 'gstreamer1.0-plugins-bad')},
    'migration': {name: item(path) for name, path in {
        'documents': 'home/igloolab/Documents/igloo-current-v6-child.txt',
        'firefox': 'home/igloolab/.mozilla/firefox/Profiles/igloolab.default/igloo-current-child.txt',
        'chromeBookmarks': 'home/igloolab/.config/google-chrome/Default/Bookmarks',
        'wallpaper': 'home/igloolab/Pictures/wallpaper.PNG',
        'avatar': 'home/igloolab/.face',
    }.items()},
    'wifi': wifi,
    'rootManifest': item('var/lib/igloo/manifest.json'),
    'seedManifest': item('migration-manifest.json', base=SEED),
    'seedPreseed': item('preseed.cfg', base=SEED),
    'seedSecretIndicators': {
        'rootManifestCryptHash': '$6$' in root_manifest,
        'seedManifestCryptHash': '$6$' in seed_manifest,
        'seedPreseedCryptHash': '$6$' in seed_preseed,
        'rootManifestPasswordFieldNonempty': bool(json.loads(root_manifest).get('linuxPasswordCrypted')),
        'seedManifestPasswordFieldNonempty': bool(json.loads(seed_manifest).get('linuxPasswordCrypted')),
    },
    'seedTemporaryFiles': seed_temp_paths,
}
(LAB / 'firstboot-offline-readback.json').write_text(json.dumps(report, indent=2) + '\n')
(LAB / 'agent-current.log').write_text(agent_log.split('2026-10-18', 1)[-1])
(LAB / 'bootstrap-current.log').write_text(bootstrap_log.split('2026-10-18', 1)[-1])
print(json.dumps(report, indent=2))
