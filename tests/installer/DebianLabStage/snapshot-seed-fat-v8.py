"""Read only the disposable v8 OEMDRV FAT state at three installer phases."""

import json
import os
from pathlib import Path
import subprocess
import sys
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
SEED = LAB / 'seed.raw'
PHASE = sys.argv[1]
if PHASE not in ('staged', 'postinstaller', 'postfirstboot'):
    raise RuntimeError('Expected staged, postinstaller, or postfirstboot')
if (os.geteuid() != 0 or LAB.resolve() != LAB or SEED.is_symlink() or
        SEED.stat().st_size != 5 << 30 or
        subprocess.check_output(['losetup', '-a']).strip() or
        subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'], capture_output=True).returncode != 1):
    raise RuntimeError('Exact offline disposable v8 seed is unavailable')
result_path = LAB / f'seed-fat-{PHASE}-v8.json'
if result_path.exists():
    raise RuntimeError('This FAT phase was already snapshotted')
ids = json.loads((LAB / 'base-identities.json').read_text())


def output(*args):
    return subprocess.check_output(args, text=True).strip()


loop = output('losetup', '--find', '--show', '--partscan', '--read-only', str(SEED))
try:
    if Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve() != SEED.resolve():
        raise RuntimeError('OEMDRV loop backing path differs')
    part = loop + 'p1'
    for _ in range(30):
        if (output('lsblk', '-dnro', 'PTUUID', loop).lower() == ids['seedDiskGuid'] and
                output('lsblk', '-dnro', 'PARTUUID', part).lower() == ids['seedPartitionGuid']):
            break
        time.sleep(.1)
    else:
        raise RuntimeError('Exact OEMDRV GPT identity unavailable')

    start_sector = int((Path('/sys/class/block') / Path(part).name / 'start').read_text())
    with SEED.open('rb') as stream:
        stream.seek(start_sector * 512)
        primary = stream.read(512)
        if primary[82:90] != b'FAT32   ':
            raise RuntimeError('OEMDRV partition is not a FAT32 volume')
        bytes_per_sector = int.from_bytes(primary[11:13], 'little')
        reserved_sectors = int.from_bytes(primary[14:16], 'little')
        backup_sector = int.from_bytes(primary[50:52], 'little')
        if bytes_per_sector not in (512, 4096) or not 1 <= backup_sector < reserved_sectors:
            raise RuntimeError('OEMDRV FAT geometry is unexpected')
        stream.seek(start_sector * 512 + backup_sector * bytes_per_sector)
        backup = stream.read(bytes_per_sector)
        stream.seek(start_sector * 512 + reserved_sectors * bytes_per_sector + 4)
        fat_entry_1 = int.from_bytes(stream.read(4), 'little')
    check = subprocess.run(['fsck.fat', '-n', part], text=True,
                           capture_output=True, check=False, timeout=120)
    record = {
        'phase': PHASE,
        'seedDiskGuid': ids['seedDiskGuid'],
        'seedPartitionGuid': ids['seedPartitionGuid'],
        'partitionStartSector': start_sector,
        'bytesPerSector': bytes_per_sector,
        'backupBootSector': backup_sector,
        'primaryBootByte65': primary[65],
        'backupBootByte65': backup[65],
        'primaryAndBackupBootByte65Equal': primary[65] == backup[65],
        'fatEntry1Hex': f'{fat_entry_1:08x}',
        'fatCleanBit': bool(fat_entry_1 & 0x08000000),
        'fatNoIoErrorBit': bool(fat_entry_1 & 0x04000000),
        'fsckFatReadOnlyExitCode': check.returncode,
        'fsckFatReadOnlyOutputTail': (check.stdout + check.stderr)[-3000:],
    }
    with result_path.open('x') as result:
        json.dump(record, result, indent=2)
        result.write('\n')
    print(json.dumps(record, indent=2))
finally:
    subprocess.run(['losetup', '--detach', loop], check=True)
