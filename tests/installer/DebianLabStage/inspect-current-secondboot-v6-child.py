"""Read back only the exact disposable child's seed-free login outcome."""

import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/current-firstboot-v6-child')
ROOT = Path('/mnt/igloo-debian-v6-child-root')
if not os.path.ismount(ROOT):
    raise RuntimeError('Exact installed root is not mounted')
mount = next(line.split() for line in Path('/proc/mounts').read_text().splitlines()
             if line.split()[1] == str(ROOT))
if mount[0] != '/dev/nbd0p4' or 'ro' not in mount[3].split(','):
    raise RuntimeError('Installed root is not the exact read-only child')
home = ROOT / 'home/igloolab'
store = home / '.local/share/igloo/credentials.json'
database = home / '.config/google-chrome/Default/Login Data'
rows = []
if database.is_file():
    connection = sqlite3.connect(f'file:{database}?mode=ro&immutable=1', uri=True)
    try:
        for origin, username, cipher in connection.execute(
                'select origin_url, username_value, password_value from logins'):
            blob = bytes(cipher)
            plain = b''
            if blob.startswith(b'v10'):
                key = hashlib.pbkdf2_hmac('sha1', b'peanuts', b'saltysalt', 1, 16)
                padded = Cipher(algorithms.AES(key), modes.CBC(b' ' * 16)).decryptor().update(blob[3:])
                if padded and 1 <= padded[-1] <= 16 and padded.endswith(bytes([padded[-1]]) * padded[-1]):
                    plain = padded[:-padded[-1]]
            rows.append({
                'originMatchesFixture': origin == 'https://example.invalid/igloo-current-v6-child',
                'usernameMatchesFixture': username == 'synthetic-child-user',
                'passwordMatchesFixture': plain == b'synthetic-child-migrated-secret',
                'originSha256': hashlib.sha256(origin.encode()).hexdigest(),
                'usernameSha256': hashlib.sha256(username.encode()).hexdigest(),
                'cipherSize': len(blob), 'cipherPrefixHex': blob[:3].hex(),
            })
    finally:
        connection.close()
credential_log = (home / '.local/state/igloo-credentials.log').read_text()
logs = subprocess.run([
    'journalctl', f'--directory={ROOT / "var/log/journal"}', '--no-pager',
    '-o', 'short-iso', '-g', 'igloo-welcome', '-n', '30'],
    text=True, capture_output=True, check=False)
report = {
    'seedFreeCommand': json.loads((LAB / 'qemu-command-second.json').read_text()),
    'donePresent': (ROOT / 'var/lib/igloo/.done').is_file(),
    'retryManifestPresent': (ROOT / 'var/lib/igloo/manifest.retry.json').exists(),
    'credentialStorePresent': store.exists(),
    'credentialLog': credential_log.splitlines()[-10:],
    'chromeLoginRows': rows,
    'welcomeMarkerPresent': (home / '.igloo-welcome-done').exists(),
    'notifySendPresent': (ROOT / 'usr/bin/notify-send').is_file(),
    'welcomeJournal': logs.stdout.splitlines()[-20:],
}
(LAB / 'secondboot-offline-readback.json').write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps({key: value for key, value in report.items()
                  if key != 'seedFreeCommand'}, indent=2))
