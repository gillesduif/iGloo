"""NEW lab disk provisioning only; not native Windows preparation or import.

Invoke only after canonical_lab_runtime.observe_gpt independently recorded all
QEMU backing FDs, their creation inodes, modes and exact offline launch arguments.
Blank before-state and construction evidence are retained, never manufactured.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid


def require(value, code):
    if not value:
        raise ValueError(code)


def run(args, data=None):
    return subprocess.run(args, input=data, check=True, capture_output=True, timeout=180).stdout


def main():
    require(os.geteuid() == 0 and not Path('/sys/firmware/efi').exists(), "LabPrecondition20")
    require(sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo'], "LabPrecondition21")
    require(sorted(p.name for p in Path('/sys/block').iterdir() if not p.name.startswith(('loop', 'ram'))) == ['vda', 'vdb', 'vdc'], "LabPrecondition22")
    expected = [('vda', 'IGLOO-GPT-RUNTIME', 24), ('vdb', 'IGLOO-LAB-TARGET', 40), ('vdc', 'IGLOO-LAB-JOURNAL', 8)]
    for dev, serial, gib in expected:
        require(Path('/sys/block', dev, 'serial').read_text().strip() == serial, "LabPrecondition25")
        require(int(Path('/sys/block', dev, 'size').read_text()) * 512 == gib * 1024**3, "LabPrecondition26")
    evidence = Path('/root/gpt-lab-provisioning'); evidence.mkdir(mode=0o700)

    def save(name, value):
        data = json.dumps(value, sort_keys=True, indent=2).encode()
        with (evidence / (name + '.json')).open('xb') as out:
            out.write(data); out.flush(); os.fsync(out.fileno())
        fd = os.open(evidence, os.O_DIRECTORY)
        try: os.fsync(fd)
        finally: os.close(fd)
        require((evidence / (name + '.json')).read_bytes() == data, "LabPrecondition36")

    def table(dev): return json.loads(run(['sfdisk', '-J', '/dev/' + dev]))
    for dev in ['vdb', 'vdc']:
        require(json.loads(run(['wipefs', '-n', '-J', '/dev/' + dev]))['signatures'] == [], "LabPrecondition40")
        require(not list(Path('/sys/block', dev).glob(dev + '*')), "LabPrecondition41")
        save(dev + '-blank', {'Serial': Path('/sys/block', dev, 'serial').read_text().strip(),
            'Signatures': [], 'Scope': 'HostBoundNewLabStorage'})
    # Establish platform/preserved lab objects BEFORE owned-partition preparation.
    for dev, text in [('vdb', 'label: gpt\nunit: sectors\nstart=2048,size=524288,type=U\n'),
                      ('vdc', 'label: gpt\nunit: sectors\nstart=2048,type=L\n')]:
        save(dev + '-platform-intent', {'Table': text})
        run(['sfdisk', '/dev/' + dev], text.encode()); run(['udevadm', 'settle'])
        save(dev + '-platform-readback', table(dev))
    generation = str(uuid.uuid4())
    save('preparation-before', {'GenerationId': generation, 'Target': table('vdb'), 'Journal': table('vdc')})
    for index, line in [(2, 'start=526336,size=2097152,type=U\n'),
                        (3, 'start=2623488,size=16777216,type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7\n'),
                        (4, 'start=19400704,size=41943040,type=L\n')]:
        save(f'create-{index}-intent', {'GenerationId': generation, 'Before': table('vdb'), 'Allocation': line})
        run(['sfdisk', '--append', '/dev/vdb'], line.encode()); run(['udevadm', 'settle'])
        save(f'create-{index}-readback', table('vdb'))
    for dev, kind in [('vdb1', 'FAT32'), ('vdb2', 'FAT32'), ('vdb3', 'FAT32'), ('vdb4', 'EXT4'), ('vdc1', 'EXT4')]:
        before = json.loads(run(['wipefs', '-n', '-J', '/dev/' + dev])); require(before['signatures'] == [], "LabPrecondition59")
        save(dev + '-format-intent', {'GenerationId': generation, 'Before': before, 'FileSystem': kind})
        run(['mkfs.vfat', '-F', '32', '/dev/' + dev] if kind == 'FAT32' else ['mkfs.ext4', '-q', '/dev/' + dev])
        save(dev + '-format-readback', {'Blkid': run(['blkid', '-p', '-o', 'export', '/dev/' + dev]).decode()})
    # Persistent provisioning evidence exists on runtime EXT4 before journal creation.
    Path('/lab-journal').mkdir(mode=0o700)
    save('journal-mount-intent', {'Device': '/dev/vdc1', 'Filesystem': 'EXT4', 'Destination': '/lab-journal'})
    run(['mount', '-t', 'ext4', '-o', 'nodev,nosuid,noexec', '/dev/vdc1', '/lab-journal'])
    for name in ['session', 'import']:
        Path('/lab-journal', name).mkdir(mode=0o700)
    fd = os.open('/lab-journal', os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)
    save('journal-directory-readback', {'Mountinfo': Path('/proc/self/mountinfo').read_text(),
        'Directories': {name: list(os.stat('/lab-journal/' + name)) for name in ['session', 'import']},
        'CanonicalPlacementVerifier': 'NotInvoked'})
    print(json.dumps({'GenerationId': generation, 'EvidenceDirectory': str(evidence), 'Import': 'NotStarted'}))


if __name__ == '__main__': main()
