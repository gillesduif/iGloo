"""Correct only the synthetic v7 lab login fixture after first-boot readback."""

from pathlib import Path
import crypt
import hashlib
import json
import os
import subprocess
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
DISK = LAB / 'selected.raw'
ROOT = LAB / 'mnt-installed-root-rw'
IDS = json.loads((LAB / 'base-identities.json').read_text())
PASSWORD = 'igloolab123'  # Synthetic disposable guest account and envelope key.
assert os.geteuid() == 0 and DISK.is_file() and DISK.stat().st_size == 30 << 30
assert (LAB / 'firstboot-readback-v7.json').is_file()
assert not (LAB / 'lab-login-correction-v7.json').exists()
assert not subprocess.check_output(['losetup', '-a']).strip()
assert subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode == 1


def output(*args):
    return subprocess.check_output(args, text=True).strip()


loop = output('losetup', '--find', '--show', '--partscan', str(DISK))
try:
    assert Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() == DISK.resolve()
    for _ in range(30):
        if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == IDS['selectedDiskGuid'] and
                output('lsblk', '-dnro', 'PARTUUID', loop + 'p3').lower() == IDS['linuxEspGuid']):
            break
        time.sleep(.1)
    else:
        raise RuntimeError('Selected disposable GPT identity is unavailable')
    ROOT.mkdir(exist_ok=True)
    subprocess.run(['mount', '-o', 'rw,nodev,nosuid', loop + 'p4', str(ROOT)], check=True)
    try:
        shadow = ROOT / 'etc/shadow'
        lines = shadow.read_text().splitlines()
        rows = [line for line in lines if line.startswith('igloolab:')]
        assert len(rows) == 1 and (ROOT / 'home/igloolab').stat().st_uid == 1000
        original = rows[0].split(':')[1]
        assert original.startswith('$6$') and crypt.crypt(PASSWORD, original) != original
        subprocess.run(['chpasswd', '-R', str(ROOT)], input=f'igloolab:{PASSWORD}\n',
                       text=True, check=True)
        updated = [line for line in shadow.read_text().splitlines()
                   if line.startswith('igloolab:')][0].split(':')[1]
        assert updated.startswith('$') and crypt.crypt(PASSWORD, updated) == updated
        subprocess.run(['sync'], check=True)
        record = {
            'targetDiskGuid': IDS['selectedDiskGuid'],
            'targetRootPartition': output('lsblk', '-dnro', 'PARTUUID', loop + 'p4').lower(),
            'firstbootReadbackSha256': hashlib.sha256((LAB / 'firstboot-readback-v7.json').read_bytes()).hexdigest(),
            'originalShadowEntrySha256': hashlib.sha256(original.encode()).hexdigest(),
            'correctedShadowEntrySha256': hashlib.sha256(updated.encode()).hexdigest(),
            'correction': 'Lab-only synthetic login aligned to already-bound credential envelope; installed first boot was verified before this write.',
        }
        (LAB / 'lab-login-correction-v7.json').write_text(json.dumps(record, indent=2) + '\n')
        print(json.dumps(record, indent=2))
    finally:
        subprocess.run(['umount', str(ROOT)], check=True)
finally:
    subprocess.run(['losetup', '--detach', loop], check=True)
