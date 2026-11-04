"""One disposable current-agent first boot on the preserved Debian v6 install.

This lab-only probe does not reinstall Debian or qualify the Windows installer.
It never opens the retained raw target for writing.
"""

import base64
import crypt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


BASE = Path('/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6')
LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/current-firstboot-v6-child')
RAW = BASE / 'selected.raw'
CHILD = LAB / 'selected-child.qcow2'
VARS = LAB / 'ovmf-vars.fd'
ROOT = LAB / 'mounted-root'
STAGE = LAB / 'provider-stage'
MANIFEST = LAB / 'manifest.json'
REPO = Path(__file__).resolve().parents[3]
VERIFIER = REPO / 'distros/_debian-family/agent/igloo-verify-seed.py'
AGENT = REPO / 'distros/_debian-family/agent/agent.py'
EXPECTED_AGENT_SHA = '254ee58ff3b7fb59430b35a6af097f0496b30e48389fce87908b915fc2cd68af'
EXPECTED_SOURCE_DISK_GUID = 'c837e8b9-8b74-4a9a-a8c1-d6f9043c928b'
EXPECTED_LINUX_ESP_GUID = '6cfd80d0-dd42-4b42-91d9-f114559685d4'
LOGIN_PASSWORD = 'igloolab123'  # Synthetic and confined to the disposable child.
GIB = 1024 ** 3


def run(*args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require_idle():
    if subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'],
                      capture_output=True).returncode != 1:
        raise RuntimeError('A QEMU guest already owns the VM slot')
    if output('losetup', '-a'):
        raise RuntimeError('A loop device already owns the storage slot')
    for name in ('/dev/nbd0', '/dev/nbd1'):
        pid_file = Path('/sys/block') / Path(name).name / 'pid'
        if pid_file.exists() and pid_file.read_text().strip() not in ('', '0'):
            raise RuntimeError('An NBD device already owns the storage slot')


def require_capacity():
    free = shutil.disk_usage('/mnt/c').free
    if free < 22 * GIB:
        raise RuntimeError(f'C: free {free} bytes is below the 22 GiB staging gate')
    return free


def patch_once(text, old, new):
    if text.count(old) != 1 or new in text:
        raise RuntimeError('Installed bootstrap differs from the reviewed v6 input')
    return text.replace(old, new, 1)


def observed_disk_guid(device):
    text = output('sgdisk', '-p', device)
    match = re.search(r'Disk identifier \(GUID\): ([0-9A-Fa-f-]{36})', text)
    if match is None:
        raise RuntimeError('The child has no observed GPT disk GUID')
    return str(uuid.UUID(match.group(1)))


def build_manifest(target_guid):
    original = json.loads((BASE / 'manifest.json').read_text())
    if (original.get('distroId') != 'debian' or
            original['hardware']['targetDiskGuid'] != EXPECTED_SOURCE_DISK_GUID or
            original['hardware']['needsNonFreeCodecs'] is not False):
        raise RuntimeError('The preserved v6 manifest differs from the expected baseline')
    original['hardware']['targetDiskGuid'] = target_guid
    original['hardware']['needsNonFreeCodecs'] = True
    original['wifiNetworks'] = [
        {'ssid': 'iGloo-v6-child-fixture', 'security': 'wpa-psk',
         'psk': 'synthetic-v6-child-wifi-secret', 'hidden': True},
        {'ssid': 'iGloo-v6-child-promptable', 'security': 'wpa-psk',
         'psk': None, 'hidden': False},
    ]
    # A tiny native selection exercises the explicit package failure/retry path
    # without allocating a full Chrome Flatpak in the capacity-bound child.
    original['suggestedPackages'] = [
        {'windowsAppName': 'Tree Fixture', 'linuxAppName': 'tree',
         'nativePackage': 'tree', 'autoInstall': True},
    ]
    original['wallpaper'] = {'fileName': 'igloo-wallpaper.PNG'}
    original['accountPicture'] = {'fileName': 'igloo-avatar.jpg'}
    original['user']['linuxPasswordCrypted'] = crypt.crypt(
        LOGIN_PASSWORD, crypt.mksalt(crypt.METHOD_SHA512))
    if crypt.crypt(LOGIN_PASSWORD, original['user']['linuxPasswordCrypted']) != \
            original['user']['linuxPasswordCrypted']:
        raise RuntimeError('Synthetic login hash is inconsistent')
    chrome = next(browser for browser in original['browsers']
                  if browser['name'] == 'Google Chrome')
    salt, nonce = os.urandom(16), os.urandom(12)
    key = hashlib.pbkdf2_hmac('sha256', LOGIN_PASSWORD.encode(), salt, 600_000, 32)
    body = json.dumps({'browser': 'Google Chrome', 'logins': [{
        'url': 'https://example.invalid/igloo-current-v6-child',
        'username': 'synthetic-child-user',
        'password': 'synthetic-child-migrated-secret',
    }], 'cookies': []}, separators=(',', ':')).encode()
    chrome['credentialsBlob'] = base64.b64encode(
        b'IGCRD001' + salt + nonce + AESGCM(key).encrypt(nonce, body, None)).decode()
    chrome['includesPasswords'] = True
    return original


def main():
    resume = sys.argv[1:] == ['--resume-after-gpt-write']
    if sys.argv[1:] and not resume:
        raise RuntimeError('Unknown lab preparation mode')
    if os.geteuid() != 0:
        raise RuntimeError('Run this lab-only probe as WSL root')
    if (BASE.resolve() != Path('/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6')
            or LAB.exists() != resume or RAW.is_symlink() or RAW.stat().st_nlink != 1
            or RAW.stat().st_size != 30 * GIB
            or sha(AGENT) != EXPECTED_AGENT_SHA
            or not VERIFIER.is_file() or VERIFIER.is_symlink()):
        raise RuntimeError('The v6 baseline, lab destination, or current agent changed')
    require_idle()
    before_free = require_capacity()
    if output('sgdisk', '-p', str(RAW)).lower().find(EXPECTED_SOURCE_DISK_GUID) < 0:
        raise RuntimeError('The retained raw image has a different GPT disk GUID')
    if EXPECTED_LINUX_ESP_GUID not in output('sgdisk', '-i', '3', str(RAW)).lower():
        raise RuntimeError('The retained raw image has a different Linux ESP')

    if resume:
        if (not CHILD.is_file() or CHILD.is_symlink() or CHILD.stat().st_nlink != 1
                or not VARS.is_file() or VARS.is_symlink() or
                sha(VARS) != sha(BASE / 'ovmf-vars-installer-v6.fd') or
                not ROOT.is_dir() or list(ROOT.iterdir()) or
                MANIFEST.exists() or STAGE.exists() or
                (LAB / 'preparation.json').exists()):
            raise RuntimeError('The partial lab is not the exact GPT-only child')
        child_info = json.loads(output('qemu-img', 'info', '--output=json', str(CHILD)))
        if (child_info.get('format') != 'qcow2' or
                child_info.get('virtual-size') != 30 * GIB or
                child_info.get('backing-filename-format') != 'raw' or
                child_info.get('full-backing-filename') != str(RAW)):
            raise RuntimeError('The partial child backing chain changed')
    else:
        LAB.mkdir(mode=0o700)
        ROOT.mkdir(mode=0o700)
        run('qemu-img', 'create', '-f', 'qcow2', '-F', 'raw',
            '-b', str(RAW), str(CHILD))
        shutil.copyfile(BASE / 'ovmf-vars-installer-v6.fd', VARS)
    target_guid = None if resume else str(uuid.uuid4())

    run('modprobe', 'nbd', 'max_part=16')
    if not Path('/dev/nbd0').exists() or output('blockdev', '--getsize64', '/dev/nbd0') != '0':
        raise RuntimeError('NBD0 is not an idle lab device')
    run('qemu-nbd', '--connect=/dev/nbd0', '--format=qcow2', str(CHILD))
    mounted = False
    try:
        if output('blockdev', '--getsize64', '/dev/nbd0') != str(30 * GIB):
            raise RuntimeError('The child NBD has unexpected size')
        observed_guid = observed_disk_guid('/dev/nbd0')
        if resume:
            if observed_guid == EXPECTED_SOURCE_DISK_GUID:
                raise RuntimeError('The partial child has no distinct GPT disk GUID')
            target_guid = observed_guid
        elif observed_guid != EXPECTED_SOURCE_DISK_GUID:
            raise RuntimeError('The child does not initially match the v6 GPT')
        if EXPECTED_LINUX_ESP_GUID not in output('sgdisk', '-i', '3', '/dev/nbd0').lower():
            raise RuntimeError('The child Linux ESP differs from the v6 input')
        if not resume:
            run('sgdisk', '-U', target_guid, '/dev/nbd0')
        run('blockdev', '--rereadpt', '/dev/nbd0')
        if observed_disk_guid('/dev/nbd0') != target_guid:
            raise RuntimeError('The child GPT disk GUID write was not observed')
        if EXPECTED_LINUX_ESP_GUID not in output('sgdisk', '-i', '3', '/dev/nbd0').lower():
            raise RuntimeError('The installed Linux ESP changed unexpectedly')
        for _ in range(30):
            if Path('/dev/nbd0p4').exists():
                break
            time.sleep(.1)
        else:
            raise RuntimeError('The child installed-root partition is unavailable')
        if output('blkid', '-s', 'TYPE', '-o', 'value', '/dev/nbd0p4') != 'ext4':
            raise RuntimeError('The child has no expected ext4 installed root')
        run('mount', '-o', 'rw,nodev,nosuid', '/dev/nbd0p4', str(ROOT))
        mounted = True
        state = ROOT / 'var/lib/igloo'
        done = state / '.done'
        bootstrap = ROOT / 'opt/igloo/igloo-bootstrap.sh'
        if (done.read_bytes() != b'1\n' or not bootstrap.is_file() or
                bootstrap.is_symlink() or not (ROOT / 'home/igloolab').is_dir()):
            raise RuntimeError('The child is not the completed v6 installation')
        text = bootstrap.read_text()
        text = patch_once(text,
            'mkdir -p /opt/igloo /var/lib/igloo /var/log/igloo /run/igloo-seed\n',
            'mkdir -p /opt/igloo /var/lib/igloo /var/log/igloo /run/igloo-seed\n'
            'chmod 0755 /opt /opt/igloo\n')
        verifier_line = ('python3 /opt/igloo/igloo-verify-seed.py '
                         '/var/lib/igloo/selected-seed-partuuid '
                         '/var/lib/igloo/selected-seed-diskguid /run/igloo-seed\n')
        text = patch_once(text, verifier_line,
                          'selected_media=$(' + verifier_line.rstrip('\n') + ')\n')
        wildcard = ('cp /run/igloo-seed/igloo-wallpaper.* /opt/igloo/ 2>/dev/null || true\n'
                    'cp /run/igloo-seed/igloo-avatar.* /opt/igloo/ 2>/dev/null || true\n')
        text = patch_once(text, wildcard,
            'for file in $selected_media ; do cp "/run/igloo-seed/$file" '
            '"/opt/igloo/$file" ; done\n')
        module_line = ('chmod 0755 /opt/igloo/agent.py /opt/igloo/first-boot.sh '
                       '/opt/igloo/display-apply.py /opt/igloo/display-apply-gnome.py\n')
        text = patch_once(text, module_line, module_line +
                          'chmod 0644 /opt/igloo/igloo_boot.py\n')
        bootstrap.write_text(text)
        bootstrap.chmod(0o755)
        (ROOT / 'opt').chmod(0o755)
        (ROOT / 'opt/igloo').chmod(0o755)
        shutil.copyfile(VERIFIER, ROOT / 'opt/igloo/igloo-verify-seed.py')
        (ROOT / 'opt/igloo/igloo-verify-seed.py').chmod(0o700)
        done.unlink()
        (state / 'selected-seed-partuuid').write_text('PENDING\n')
        (state / 'selected-seed-diskguid').write_text('PENDING\n')
        (state / 'selected-seed-partuuid').chmod(0o600)
        (state / 'selected-seed-diskguid').chmod(0o600)
        run('sync')
    finally:
        if mounted:
            run('umount', str(ROOT))
        run('qemu-nbd', '--disconnect', '/dev/nbd0')

    manifest = build_manifest(target_guid)
    MANIFEST.write_text(json.dumps(manifest, indent=2) + '\n')
    MANIFEST.chmod(0o600)
    STAGE.mkdir(mode=0o700)
    summary = {
        'lab': str(LAB),
        'base': str(RAW),
        'baseLogicalBytes': RAW.stat().st_size,
        'baseAllocatedBytes': RAW.stat().st_blocks * 512,
        'childDiskGuid': target_guid,
        'linuxEspPartitionGuid': EXPECTED_LINUX_ESP_GUID,
        'sourceAgentSha256': EXPECTED_AGENT_SHA,
        'manifestSha256': sha(MANIFEST),
        'cFreeBeforeBytes': before_free,
        'cFreeAfterBytes': shutil.disk_usage('/mnt/c').free,
    }
    (LAB / 'preparation.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    main()
