"""Runtime construction phase only, inside the separately observed disposable VM.

The trusted host controller must record observe_construction() before invoking.
This never provisions migration ownership or claims a Windows preparation receipt.
The NEW vdb file backing is the sole disk this script partitions/formats.
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
    return subprocess.run(args, input=data, check=True, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, timeout=1800).stdout


def save(folder, name, value):
    data = json.dumps(value, sort_keys=True, indent=2).encode()
    path = folder / name
    with path.open('xb') as out:
        out.write(data); out.flush(); os.fsync(out.fileno())
    fd = os.open(folder, os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)
    if path.read_bytes() != data: raise OSError('EvidenceReopenChanged')


def build():
    require(os.getuid() == 0, "LabPrecondition32")
    require(sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo'], "LabPrecondition33")
    require(not Path('/sys/firmware/efi').exists(), "LabPrecondition34")
    expected = {'vda': 'IGLOO-CONSTRUCTION', 'vdb': 'IGLOO-GPT-RUNTIME', 'vdc': 'IGLOO-TOOLS'}
    visible = {p.name for p in Path('/sys/block').iterdir() if not p.name.startswith(('loop', 'ram'))}
    require(visible == set(expected), "LabPrecondition37")
    for name, serial in expected.items():
        require((Path('/sys/block') / name / 'serial').read_text().strip() == serial, "LabPrecondition39")
    require(run(['blockdev', '--getsize64', '/dev/vdb']).strip() == str(24 * 1024**3).encode(), "LabPrecondition40")
    before = json.loads(run(['wipefs', '-n', '-J', '/dev/vdb']))
    require(before['signatures'] == [], "LabPrecondition42")
    require(not any(p.name.startswith('vdb') for p in Path('/sys/block/vdb').iterdir()), "LabPrecondition43")
    evidence = Path('/runtime-construction'); evidence.mkdir(mode=0o700)
    disk, part = str(uuid.uuid4()), str(uuid.uuid4())
    table = f'label: gpt\nlabel-id: {disk}\nunit: sectors\nstart=2048, size=50327552, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, uuid={part}\n'
    save(evidence, 'intent.json', {'Before': before, 'DiskGuid': disk, 'PartitionGuid': part,
        'Table': table, 'Scope': 'NewDisposableRuntimeOnly', 'PreparationReceipt': False})
    run(['sfdisk', '/dev/vdb'], table.encode())
    run(['udevadm', 'settle'])
    created = json.loads(run(['sfdisk', '-J', '/dev/vdb']))
    require(created['partitiontable']['id'].lower() == disk, "LabPrecondition52")
    require(created['partitiontable']['partitions'][0]['uuid'].lower() == part, "LabPrecondition53")
    save(evidence, 'created.json', created)
    require(json.loads(run(['wipefs', '-n', '-J', '/dev/vdb1']))['signatures'] == [], "LabPrecondition55")
    save(evidence, 'format-intent.json', {'PartitionGuid': part, 'Filesystem': 'EXT4'})
    run(['mkfs.ext4', '-q', '/dev/vdb1'])
    fs = run(['blkid', '-p', '-o', 'export', '/dev/vdb1']).decode()
    require('TYPE=ext4\n' in fs, "LabPrecondition59")
    save(evidence, 'formatted.json', {'Blkid': fs})
    Path('/new-runtime').mkdir(); Path('/runtime-input').mkdir()
    run(['mount', '-t', 'ext4', '-o', 'nodev,nosuid', '/dev/vdb1', '/new-runtime'])
    run(['mount', '-t', 'iso9660', '-o', 'ro,nodev,nosuid,noexec', '/dev/vdc', '/runtime-input'])
    # Copy only runtime OS trees. No workstation artifact, factory workspace,
    # original evidence tree, root/home credentials or runtime pseudofilesystem.
    for name in ['usr', 'etc', 'var', 'boot']:
        run(['cp', '-a', '--one-file-system', '/' + name, '/new-runtime/' + name])
    for name in ['bin', 'sbin', 'lib', 'lib64']:
        run(['cp', '-a', '--no-dereference', '/' + name, '/new-runtime/' + name])
    for name in ['dev', 'proc', 'sys', 'run', 'tmp', 'root', 'home', 'mnt', 'media', 'opt', 'srv']:
        Path('/new-runtime', name).mkdir(mode=0o700 if name == 'root' else 0o755)
    os.chmod('/new-runtime/tmp', 0o1777)
    Path('/new-runtime/etc/fstab').write_text(f'PARTUUID={part} / ext4 defaults 0 1\n')
    Path('/new-runtime/etc/initramfs-tools/conf.d/resume').write_text('RESUME=none\n')
    tools = Path('/new-runtime/opt/igloo-lab'); tools.mkdir()
    run(['cp', '-a', '/runtime-input/harness', '/runtime-input/native', '/runtime-input/collector.py', str(tools)])
    Path('/new-runtime/opt/dotnet').mkdir()
    # This archive was authenticated by the host against Microsoft's release SHA512.
    runtime = Path('/runtime-input/dotnet.tar.gz')
    digest = hashlib.file_digest(runtime.open('rb'), 'sha512').hexdigest()
    require(digest == '4b801177e009ec710d5e2511500aa60bec95811add50fdc59b00a5c486840c66f00c48f272cf7a2f8129ed158ec5b887d1dd60594e612f749ba48fcf48af9cd2', "LabPrecondition81")
    run(['tar', '-xzf', str(runtime), '-C', '/new-runtime/opt/dotnet'])
    dependency = Path('/runtime-input/libicu76.deb')
    if hashlib.sha256(dependency.read_bytes()).hexdigest().upper() != 'C1BF762996DE9ECBA9B9D871E4928A8090F023A3E9E7FA3240B3D90F892A01DC':
        raise ValueError('RuntimeDependencyChanged')
    run(['cp', str(dependency), '/new-runtime/root/libicu76-runtime.deb'])
    # This is the disposable runtime factory, never the artifact or migration target.
    run(['chroot', '/new-runtime', '/usr/bin/dpkg', '--install', '/root/libicu76-runtime.deb'])
    save(evidence, 'runtime-copy.json', {'DiskGuid': disk, 'RootPartitionGuid': part,
        'DotnetSha512': digest, 'TargetImport': 'NotStarted'})
    run(['sync', '-f', '/new-runtime'])
    run(['umount', '/runtime-input']); run(['umount', '/new-runtime'])
    mounts = Path('/proc/self/mountinfo').read_text()
    require(' /new-runtime ' not in mounts and ' /runtime-input ' not in mounts, "LabPrecondition94")
    save(evidence, 'unmounted.json', {'MountinfoSha256': hashlib.sha256(mounts.encode()).hexdigest(), 'NormalUnmount': True})
    print(json.dumps({'RuntimePartitionGuid': part, 'Evidence': {p.name: json.loads(p.read_bytes()) for p in evidence.iterdir()}}))


if __name__ == '__main__':
    build()
