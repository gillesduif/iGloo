"""Read-only first-login credential readback on the exact disposable v7 target."""

from contextlib import closing
from pathlib import Path
import hashlib
import json
import os
import sqlite3
import stat
import subprocess
import time

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
DISK = LAB / 'selected.raw'
ROOT = LAB / 'mnt-installed-root-ro'
IDS = json.loads((LAB / 'base-identities.json').read_text())


def output(*args):
    return subprocess.check_output(args, text=True).strip()


if os.geteuid() != 0 or DISK.stat().st_size != 30 << 30:
    raise RuntimeError('Exact root or disposable target unavailable')
if subprocess.check_output(['losetup', '-a']).strip():
    raise RuntimeError('Another loop device is in use')
if subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode != 1:
    raise RuntimeError('QEMU is still running')
if not (LAB / 'lab-login-correction-v7.json').is_file():
    raise RuntimeError('The first-login lab fixture correction is missing')

loop = output('losetup', '--find', '--show', '--partscan', '--read-only', str(DISK))
try:
    if Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() != DISK.resolve():
        raise RuntimeError('Loop backing file changed')
    for _ in range(30):
        if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == IDS['selectedDiskGuid']
                and output('lsblk', '-dnro', 'PARTUUID', loop + 'p3').lower() == IDS['linuxEspGuid']
                and output('blkid', '-s', 'TYPE', '-o', 'value', loop + 'p4') == 'ext4'):
            break
        time.sleep(.1)
    else:
        raise RuntimeError('Selected GPT/root identity is unavailable')
    ROOT.mkdir(exist_ok=True)
    subprocess.run(['mount', '-o', 'ro,noload,nodev,nosuid', loop + 'p4', str(ROOT)], check=True)
    try:
        home = ROOT / 'home/igloolab'
        store = home / '.local/share/igloo/credentials.json'
        log = home / '.local/state/igloo-credentials.log'
        database = home / '.var/app/com.google.Chrome/config/google-chrome/Default/Login Data'
        marker = home / '.var/app/com.google.Chrome/config/google-chrome/Default/Bookmarks'
        if not database.is_file() or not marker.is_file():
            raise RuntimeError('Selected Flatpak Chrome profile is absent')
        with closing(sqlite3.connect(f'file:{database}?mode=ro', uri=True)) as connection:
            rows = connection.execute(
                'SELECT origin_url, username_value, password_value FROM logins '
                'WHERE origin_url=? AND username_value=?',
                ('https://example.invalid/igloo-v7-login', 'synthetic-igloo-user-v7'),
            ).fetchall()
        entries = []
        for url, username, password in rows:
            encrypted = bytes(password)
            if not encrypted.startswith(b'v10') or len(encrypted) < 19:
                raise RuntimeError('Selected Chrome login is not a v10 encrypted value')
            key = hashlib.pbkdf2_hmac('sha1', b'peanuts', b'saltysalt', 1, 16)
            decryptor = Cipher(algorithms.AES(key), modes.CBC(b' ' * 16)).decryptor()
            padded = decryptor.update(encrypted[3:]) + decryptor.finalize()
            padding = padded[-1]
            if not 1 <= padding <= 16 or padded[-padding:] != bytes([padding]) * padding:
                raise RuntimeError('Selected Chrome login has invalid v10 padding')
            if padded[:-padding] != b'synthetic-migrated-v7-secret':
                raise RuntimeError('Selected Chrome login plaintext differs from fixture')
            entries.append({'origin': url, 'username': username,
                            'ciphertextSha256': hashlib.sha256(encrypted).hexdigest(),
                            'v10Verified': True, 'syntheticValueMatches': True})
        record = {
            'targetDiskGuid': IDS['selectedDiskGuid'],
            'rootPartitionGuid': output('lsblk', '-dnro', 'PARTUUID', loop + 'p4').lower(),
            'seedDeviceAbsentFromSecondBootCommand':
                all('seed.raw' not in arg for arg in json.loads(
                    (LAB / 'qemu-secondboot-v7-command.json').read_text())),
            'doneDurable': (ROOT / 'var/lib/igloo/.done').is_file(),
            'credentialStoreAbsent': not store.exists(),
            'credentialLogTail': log.read_text(errors='replace')[-1000:] if log.is_file() else '',
            'selectedLoginRows': entries,
            'bookmarksStillPresent': marker.is_file(),
            'loginDataMode': oct(stat.S_IMODE(database.stat().st_mode)),
            'loginDataUid': database.stat().st_uid,
        }
        (LAB / 'secondboot-readback-v7.json').write_text(json.dumps(record, indent=2) + '\n')
        print(json.dumps(record, indent=2))
        assert record['seedDeviceAbsentFromSecondBootCommand'] and record['doneDurable']
        assert record['credentialStoreAbsent'] and len(entries) == 1
        assert record['bookmarksStillPresent']
    finally:
        subprocess.run(['umount', str(ROOT)], check=True)
finally:
    subprocess.run(['losetup', '--detach', loop], check=True)
