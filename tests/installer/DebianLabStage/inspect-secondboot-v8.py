"""Read back actual first-login migration on the exact disposable v8 target."""

from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import stat
import subprocess
import time

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
DISK = LAB / 'selected.raw'
ROOT = LAB / 'mnt-installed-root-ro'


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def main():
    if (os.geteuid() != 0 or LAB.resolve() != LAB or DISK.is_symlink() or
            DISK.stat().st_size != 30 << 30 or
            subprocess.check_output(['losetup', '-a']).strip() or
            subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'],
                           capture_output=True).returncode != 1):
        raise RuntimeError('Exact offline disposable v8 target is unavailable')
    ids = json.loads((LAB / 'base-identities.json').read_text())
    manifest = json.loads((LAB / 'manifest.json').read_text())
    boot = json.loads((LAB / 'qemu-secondboot-v8-command.json').read_text())
    if any('seed.raw' in arg for arg in boot):
        raise RuntimeError('The second boot unexpectedly carried OEMDRV')

    loop = output('losetup', '--find', '--show', '--partscan', '--read-only', str(DISK))
    try:
        if Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() != DISK.resolve():
            raise RuntimeError('Selected loop backing differs from v8 target')
        for _ in range(30):
            if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == ids['selectedDiskGuid'] and
                    output('lsblk', '-dnro', 'PARTUUID', loop + 'p3').lower() == ids['linuxEspGuid'] and
                    output('blkid', '-s', 'TYPE', '-o', 'value', loop + 'p4') == 'ext4'):
                break
            time.sleep(.1)
        else:
            raise RuntimeError('Selected v8 GPT/root identity is unavailable')
        ROOT.mkdir(exist_ok=True)
        subprocess.run(['mount', '-o', 'ro,noload,nodev,nosuid', loop + 'p4', str(ROOT)], check=True)
        try:
            username = manifest['user']['preferredLinuxUsername']
            home = ROOT / 'home' / username
            store = home / '.local/share/igloo/credentials.json'
            log = home / '.local/state/igloo-credentials.log'
            marker = home / '.igloo-welcome-done'
            database = home / '.var/app/com.google.Chrome/config/google-chrome/Default/Login Data'
            if not database.is_file() or database.is_symlink() or not marker.is_file():
                raise RuntimeError('Selected Chrome login or welcome execution marker is missing')
            if marker.stat().st_uid != 1000 or store.exists():
                raise RuntimeError('Welcome marker ownership or retired credential store differs')
            if stat.S_IMODE((ROOT / 'etc/xdg/autostart/igloo-welcome.desktop').stat().st_mode) != 0o644:
                raise RuntimeError('Welcome autostart entry is not user-readable')
            with closing(sqlite3.connect(f'file:{database}?mode=ro', uri=True)) as connection:
                rows = connection.execute(
                    'SELECT origin_url, username_value, password_value FROM logins '
                    'WHERE origin_url=? AND username_value=?',
                    ('https://example.invalid/igloo-v8-login', 'synthetic-igloo-user-v8'),
                ).fetchall()
            if len(rows) != 1:
                raise RuntimeError('Exactly one selected Chrome saved login was not imported')
            origin, login_user, value = rows[0]
            encrypted = bytes(value)
            if not encrypted.startswith(b'v10') or len(encrypted) < 19:
                raise RuntimeError('Selected Chrome password is not a v10 encrypted value')
            key = hashlib.pbkdf2_hmac('sha1', b'peanuts', b'saltysalt', 1, 16)
            decryptor = Cipher(algorithms.AES(key), modes.CBC(b' ' * 16)).decryptor()
            padded = decryptor.update(encrypted[3:]) + decryptor.finalize()
            padding = padded[-1]
            if not 1 <= padding <= 16 or padded[-padding:] != bytes([padding]) * padding:
                raise RuntimeError('Selected Chrome v10 password padding differs')
            if padded[:-padding] != b'synthetic-migrated-v8-secret':
                raise RuntimeError('Selected Chrome saved password differs from the synthetic fixture')
            record = {
                'selectedDiskGuid': ids['selectedDiskGuid'],
                'rootPartitionGuid': output('lsblk', '-dnro', 'PARTUUID', loop + 'p4').lower(),
                'seedAbsentFromSecondBoot': True,
                'doneDurable': (ROOT / 'var/lib/igloo/.done').is_file(),
                'welcomeMarkerPresent': True,
                'welcomeMarkerUid': marker.stat().st_uid,
                'welcomeAutostartMode': '0o644',
                'credentialStoreRetired': True,
                'credentialLogTail': log.read_text(errors='replace')[-1000:] if log.is_file() else '',
                'chromeLoginOrigin': origin,
                'chromeLoginUsername': login_user,
                'chromeCiphertextSha256': hashlib.sha256(encrypted).hexdigest(),
                'chromeV10SyntheticValueVerified': True,
            }
            if not record['doneDurable']:
                raise RuntimeError('First-boot completion is not durable after seed-free login')
            (LAB / 'secondboot-readback-v8.json').write_text(json.dumps(record, indent=2) + '\n')
            print(json.dumps(record, indent=2))
        finally:
            subprocess.run(['umount', str(ROOT)], check=True)
    finally:
        subprocess.run(['losetup', '--detach', loop], check=True)


if __name__ == '__main__':
    main()
