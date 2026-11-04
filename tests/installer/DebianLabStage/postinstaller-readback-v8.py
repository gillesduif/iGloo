"""Read back only the exact disposable Debian v8 target after d-i exits."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
SELECTED = LAB / 'selected.raw'
DECOY = LAB / 'decoy.raw'
ROOT = LAB / 'mnt-installed-root-ro'
ESP = LAB / 'mnt-linux-esp-ro'


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def digest(path):
    sha = hashlib.sha256()
    with open(path, 'rb', buffering=0) as source:
        for chunk in iter(lambda: source.read(8 * 1024 * 1024), b''):
            sha.update(chunk)
    return sha.hexdigest()


def main():
    if (os.geteuid() != 0 or LAB.resolve() != LAB or
            any(path.is_symlink() or path.stat().st_size != 30 << 30
                for path in (SELECTED, DECOY)) or
            output('losetup', '-a') or
            subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'],
                           capture_output=True).returncode != 1):
        raise RuntimeError('Exact offline disposable v8 target is unavailable')
    destination = LAB / 'postinstaller-readback-v8.json'
    if destination.exists():
        raise RuntimeError('Postinstaller v8 readback already exists')
    ids = json.loads((LAB / 'base-identities.json').read_text())
    before = json.loads((LAB / 'preboot-hashes-v8.json').read_text())
    loop = output('losetup', '--find', '--show', '--read-only', '--partscan', str(SELECTED))
    try:
        if Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() != SELECTED.resolve():
            raise RuntimeError('Selected loop backing changed')
        for _ in range(30):
            if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == ids['selectedDiskGuid'] and
                    output('lsblk', '-dnro', 'PARTUUID', loop + 'p1').lower() == ids['windowsEspGuid'] and
                    output('lsblk', '-dnro', 'PARTUUID', loop + 'p3').lower() == ids['linuxEspGuid'] and
                    output('blkid', '-s', 'TYPE', '-o', 'value', loop + 'p4') == 'ext4'):
                break
            time.sleep(.1)
        else:
            raise RuntimeError('Installed GPT/root differs from selected v8 disk')
        partitions = json.loads(output('sfdisk', '--json', loop))['partitiontable']['partitions']
        # d-i creates ext4 plus a swap partition inside the one reviewed free
        # extent. Both must remain after the dedicated ESP and inside that
        # exact selected-disk gap.
        free_start = ids['freeOffsetBytes'] // 512
        free_end = (ids['freeOffsetBytes'] + ids['freeSizeBytes']) // 512
        if (len(partitions) != 5 or partitions[3]['start'] != free_start or
                partitions[3]['type'].lower() != '0fc63daf-8483-4772-8e79-3d69d8477de4' or
                partitions[4]['type'].lower() != '0657fd6d-a4ab-43c4-84e5-0933c84b4f4f' or
                partitions[3]['start'] + partitions[3]['size'] > partitions[4]['start'] or
                partitions[4]['start'] + partitions[4]['size'] > free_end):
            raise RuntimeError('Installer root/swap partitions escaped the reviewed free extent')
        pinned = before['selected']['partitionSha256']
        hashes = {f'p{number}': digest(loop + f'p{number}') for number in (1, 2, 3)}
        if hashes['p1'] != pinned['p1'] or hashes['p2'] != pinned['p2'] or hashes['p3'] == pinned['p3']:
            raise RuntimeError('Windows partitions changed or dedicated Linux ESP was unused')

        ROOT.mkdir(exist_ok=True)
        ESP.mkdir(exist_ok=True)
        subprocess.run(['mount', '-o', 'ro,noload,nodev,nosuid', loop + 'p4', str(ROOT)], check=True)
        try:
            subprocess.run(['mount', '-t', 'vfat', '-o', 'ro,nodev,nosuid', loop + 'p3', str(ESP)], check=True)
            try:
                esp_uuid = output('blkid', '-s', 'UUID', '-o', 'value', loop + 'p3')
                windows_esp_uuid = output('blkid', '-s', 'UUID', '-o', 'value', loop + 'p1')
                fstab = (ROOT / 'etc/fstab').read_text()
                esp_lines = [line for line in fstab.splitlines()
                             if not line.lstrip().startswith('#') and '/boot/efi' in line]
                if (len(esp_lines) != 1 or esp_uuid not in esp_lines[0] or
                        windows_esp_uuid in esp_lines[0]):
                    raise RuntimeError('Installed /boot/efi is not bound to the dedicated Linux ESP')
                efi_files = sorted(str(path.relative_to(ESP)) for path in (ESP / 'EFI').rglob('*')
                                   if path.is_file())
                if not any(path.endswith('/grubx64.efi') for path in efi_files):
                    raise RuntimeError('Dedicated Linux ESP has no installed GRUB EFI binary')
                logs = ROOT / 'var/log/installer'
                if (logs / 'syslog').is_file():
                    copy = LAB / 'installed-d-i-syslog-v8.log'
                    if copy.exists():
                        if digest(copy) != digest(logs / 'syslog'):
                            raise RuntimeError('Installer syslog export changed')
                    else:
                        shutil.copyfile(logs / 'syslog', copy)
                state = ROOT / 'var/lib/igloo'
                bootstrap = ROOT / 'opt/igloo/igloo-bootstrap.sh'
                service = ROOT / 'etc/systemd/system/igloo-bootstrap.service'
                if (not bootstrap.is_file() or not service.is_file() or
                        (state / 'selected-seed-partuuid').read_text().strip().lower() !=
                        ids['seedPartitionGuid'] or
                        (state / 'selected-seed-diskguid').read_text().strip().lower() !=
                        ids['seedDiskGuid']):
                    raise RuntimeError('Exact installed first-boot seed bootstrap is missing')
                result = {
                    'selectedDiskGuid': ids['selectedDiskGuid'],
                    'decoyDiskGuid': ids['decoyDiskGuid'],
                    'partitionTable': partitions,
                    'windowsEspPartitionGuid': ids['windowsEspGuid'],
                    'linuxEspPartitionGuid': ids['linuxEspGuid'],
                    'rootPartitionGuid': output('lsblk', '-dnro', 'PARTUUID', loop + 'p4').lower(),
                    'partitionSha256AfterInstall': hashes,
                    'windowsEspAndNtfsUnchanged': True,
                    'dedicatedLinuxEspChanged': True,
                    'linuxEspFilesystemUuid': esp_uuid,
                    'installedEspFstabEntry': esp_lines[0],
                    'linuxEspEfiFiles': efi_files,
                    'installedBootstrapMode': oct(bootstrap.stat().st_mode & 0o777),
                    'installedBootstrapServiceMode': oct(service.stat().st_mode & 0o777),
                    'manifestCopiedAtFirstBoot': not (state / 'manifest.json').exists(),
                }
            finally:
                subprocess.run(['umount', str(ESP)], check=True)
        finally:
            subprocess.run(['umount', str(ROOT)], check=True)
    finally:
        subprocess.run(['losetup', '--detach', loop], check=True)
    result['decoyDiskSha256AfterInstall'] = digest(DECOY)
    if result['decoyDiskSha256AfterInstall'] != before['decoyDiskSha256']:
        raise RuntimeError('Same-size decoy disk changed during selected installation')
    destination.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
