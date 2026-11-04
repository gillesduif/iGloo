"""Retire only fully read-back disposable Debian v8 raw media.

The signed ISO, OVMF variables, compact results, logs, and screenshots remain.
This never selects a device by ordinal or touches a mounted image.
"""

import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import uuid


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
IMAGES = {
    'selected.raw': (30 << 30, '8cde0d78-3ea8-437e-b20c-831734f07404'),
    'seed.raw': (5 << 30, '357b156e-aaef-4e6f-9001-e84a2e93fa99'),
    'decoy.raw': (30 << 30, '85687ecc-d37d-42fe-8f18-1d2ec08f5574'),
}


def read_result(name):
    return json.loads((LAB / name).read_text())


def exact_image(name, size, disk_guid):
    path = LAB / name
    metadata = path.lstat()
    if (not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1 or
            metadata.st_size != size or path.resolve() != path):
        raise RuntimeError('Disposable v8 image path, size, or link differs: ' + name)
    with path.open('rb') as stream:
        stream.seek(512)
        header = stream.read(92)
    if (header[:8] != b'EFI PART' or
            str(uuid.UUID(bytes_le=header[56:72])) != disk_guid):
        raise RuntimeError('Disposable v8 GPT GUID differs: ' + name)
    info = json.loads(subprocess.check_output(
        ['qemu-img', 'info', '--output=json', str(path)], text=True))
    if info.get('format') != 'raw' or info.get('virtual-size') != size or info.get('backing-filename'):
        raise RuntimeError('Disposable v8 image format/backing differs: ' + name)
    return {'path': str(path), 'sizeBytes': size, 'allocatedBytes': metadata.st_blocks * 512,
            'inode': metadata.st_ino, 'diskGuid': disk_guid}


def publish(path, record):
    payload = (json.dumps(record, indent=2) + '\n').encode()
    with path.open('wb') as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    directory = os.open(str(LAB), os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def main():
    if os.geteuid() != 0 or LAB.resolve() != LAB:
        raise RuntimeError('Exact root-owned disposable v8 lab required')
    if subprocess.check_output(['losetup', '-a']).strip() or subprocess.run(
            ['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode != 1:
        raise RuntimeError('QEMU or a loop device remains active')
    if str(LAB) in subprocess.check_output(['findmnt', '-rn', '-o', 'SOURCE,TARGET'], text=True):
        raise RuntimeError('A disposable v8 mount remains active')

    first = read_result('firstboot-readback-v8.json')
    second = read_result('secondboot-readback-v8.json')
    seed_fat = read_result('seed-fat-postfirstboot-v8.json')
    first_capacity = read_result('capacity-watch-firstboot-v8.json')
    second_capacity = read_result('capacity-watch-secondboot-v8.json')
    second_command = read_result('qemu-secondboot-v8-command.json')
    if (first['selectedDiskGuid'] != IMAGES['selected.raw'][1] or
            first['seedDiskGuid'] != IMAGES['seed.raw'][1] or
            second['selectedDiskGuid'] != IMAGES['selected.raw'][1] or
            seed_fat['seedDiskGuid'] != IMAGES['seed.raw'][1] or
            not first['done'] or not second['doneDurable'] or
            not second['seedAbsentFromSecondBoot'] or
            not second['chromeV10SyntheticValueVerified'] or
            not second['welcomeMarkerPresent'] or
            not seed_fat['fatCleanBit'] or seed_fat['fsckFatReadOnlyExitCode'] != 0 or
            first_capacity['capacityStopSent'] or second_capacity['capacityStopSent'] or
            not first_capacity['guestExited'] or not second_capacity['guestExited'] or
            any('seed.raw' in arg for arg in second_command)):
        raise RuntimeError('Disposable v8 readback/boot closure is incomplete')
    for name in ('postinstaller-readback-v8.json', 'seed-fat-staged-v8.json',
                 'seed-fat-postinstaller-v8.json', 'screen-secondboot-desktop.png',
                 'installed-d-i-syslog-v8.log'):
        if not (LAB / name).is_file():
            raise RuntimeError('Compact disposable v8 evidence is missing: ' + name)

    images = {name: exact_image(name, *expected) for name, expected in IMAGES.items()}
    retired_paths = {item['path'] for item in images.values()}
    for overlay in Path('/home/gillesduif').rglob('*.qcow2'):
        info = json.loads(subprocess.check_output(
            ['qemu-img', 'info', '--output=json', str(overlay)], text=True))
        if info.get('full-backing-filename') in retired_paths:
            raise RuntimeError('An active qcow2 backing dependency remains: ' + str(overlay))

    record = {
        'status': 'verified-for-retirement',
        'lab': str(LAB),
        'images': images,
        'allocatedBytesTotal': sum(item['allocatedBytes'] for item in images.values()),
        'compactEvidenceRetained': [
            'postinstaller-readback-v8.json', 'firstboot-readback-v8.json',
            'secondboot-readback-v8.json', 'seed-fat-staged-v8.json',
            'seed-fat-postinstaller-v8.json', 'seed-fat-postfirstboot-v8.json',
            'capacity-watch-firstboot-v8.json', 'capacity-watch-secondboot-v8.json',
            'installed-d-i-syslog-v8.log', 'screen-secondboot-desktop.png',
        ],
        'cFreeBeforeBytes': shutil.disk_usage('/mnt/c').free,
        'wslFreeBeforeBytes': shutil.disk_usage('/').free,
    }
    receipt = LAB / 'media-retirement-v8.json'
    if receipt.exists():
        raise RuntimeError('Disposable v8 media retirement was already attempted')
    publish(receipt, record)
    for name in IMAGES:
        (LAB / name).unlink()
    record['status'] = 'retired'
    record['cFreeAfterBytes'] = shutil.disk_usage('/mnt/c').free
    record['wslFreeAfterBytes'] = shutil.disk_usage('/').free
    publish(receipt, record)
    print(json.dumps(record, indent=2))


if __name__ == '__main__':
    main()
