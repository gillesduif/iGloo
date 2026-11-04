"""Pin disposable v8 target/decoy partitions before the installer runs."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
IDS = json.loads((LAB / 'base-identities.json').read_text())


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def sha(path):
    digest = hashlib.sha256()
    with open(path, 'rb', buffering=0) as source:
        for block in iter(lambda: source.read(8 * 1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def pin_disk(name, expected_guid):
    image = LAB / (name + '.raw')
    if image.is_symlink() or image.stat().st_size != 30 << 30:
        raise RuntimeError('Exact v8 disk image changed: ' + name)
    loop = output('losetup', '--find', '--show', '--read-only', '--partscan', str(image))
    try:
        if (Path(output('losetup', '--noheadings', '--output', 'BACK-FILE', loop)).resolve()
                != image.resolve()):
            raise RuntimeError('Exact v8 loop/GPT differs: ' + name)
        for _ in range(30):
            if output('lsblk', '-dnro', 'PTUUID', loop).lower() == expected_guid:
                break
            time.sleep(.1)
        else:
            raise RuntimeError('Exact v8 loop/GPT differs: ' + name)
        table = json.loads(output('sfdisk', '--json', loop))['partitiontable']
        parts = table['partitions']
        if len(parts) != 3:
            raise RuntimeError('Exact v8 preinstalled partition count differs: ' + name)
        hashes = {f'p{number}': sha(loop + f'p{number}') for number in (1, 2, 3)}
        return {'diskGuid': expected_guid, 'partitionTable': parts, 'partitionSha256': hashes}
    finally:
        subprocess.run(['losetup', '--detach', loop], check=True)


def main():
    if (os.geteuid() != 0 or LAB.resolve() != LAB or
            (LAB / 'preboot-hashes-v8.json').exists() or
            output('losetup', '-a') or
            subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'],
                           capture_output=True).returncode != 1):
        raise RuntimeError('Exact offline v8 preboot state is unavailable')
    selected = pin_disk('selected', IDS['selectedDiskGuid'])
    decoy = pin_disk('decoy', IDS['decoyDiskGuid'])
    if (selected['partitionTable'][2]['uuid'].lower() != IDS['linuxEspGuid'] or
            selected['partitionTable'][0]['uuid'].lower() != IDS['windowsEspGuid']):
        raise RuntimeError('Selected dedicated/Windows ESP identity differs')
    result = {'selected': selected, 'decoy': decoy,
              'decoyDiskSha256': sha(LAB / 'decoy.raw'),
              'seedIdentitySha256': sha(LAB / 'seed-identity-v8.json')}
    (LAB / 'preboot-hashes-v8.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'selectedDiskGuid': selected['diskGuid'],
                      'decoyDiskGuid': decoy['diskGuid'],
                      'selectedPartitionSha256': selected['partitionSha256'],
                      'decoyDiskSha256': result['decoyDiskSha256'],
                      'seedIdentitySha256': result['seedIdentitySha256']}, indent=2))


if __name__ == '__main__':
    main()
