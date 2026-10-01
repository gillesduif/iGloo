"""Closed lab delivery and execution. Only a newly receipted payload is writable.

The tooling CD is provisioning input only. The native importer receives neither
its mount nor artifact descriptors from it: canonical payload FDs supply all bytes.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys

from canonical_storage_guest import require, run, save, digest


def stage_payload(evidence, stage, delivery, corrupt=False):
    evidence, stage, delivery = map(Path, (evidence, stage, delivery))
    storage = json.loads((evidence / 'storage.json').read_bytes())
    require(storage['SchemaVersion'] == 2 and storage['Scope'] == 'ConfiguredRootImport', 'ImportPreparationRequired')
    transition = storage['Transitions'][1]
    require(transition['Role'] == 1 and transition['Intended']['DevicePath'] == '/dev/vdb3', 'PayloadRoleChanged')
    filesystem = dict(line.split('=', 1) for line in run(['blkid', '-p', '-o', 'export', '/dev/vdb3']).decode().splitlines() if '=' in line)
    require(filesystem['UUID'] == transition['FileSystem']['Uuid'] and filesystem['TYPE'] == 'vfat', 'PayloadFilesystemChanged')
    sys.path.insert(0, str(stage / 'native'))
    import root_transport
    source = delivery / 'artifact'
    expected = json.loads((delivery / 'delivery-input-result.json').read_bytes())
    transport_bytes = (source / 'transport.json').read_bytes()
    transport = root_transport.parse(transport_bytes)
    names = ['descriptor.json', 'root.manifest.json', 'transport.json'] + [c['Name'] for c in transport['Chunks']]
    require(set(names) == set(expected['Objects']) and len(names) == 8, 'DeliveryObjectSetChanged')
    require(os.statvfs(source).f_flag & os.ST_RDONLY, 'ProvisioningSourceNotReadOnly')
    mountpoint = evidence / 'payload-staging'; mountpoint.mkdir(mode=0o700)
    save(evidence, 'payload-delivery-intent.json', {'GenerationId': storage['GenerationId'], 'Payload': transition['Intended'],
        'FileSystem': transition['FileSystem'], 'Objects': expected['Objects'], 'CorruptDerivative': corrupt})
    run(['mount', '-t', 'vfat', '-o', 'nodev,nosuid,noexec', '/dev/vdb3', str(mountpoint)])
    require(not list(mountpoint.iterdir()), 'PayloadNotEmpty')
    capacity = os.statvfs(mountpoint)
    needed = root_transport.required_capacity([expected['Objects'][n]['Length'] for n in names], capacity.f_frsize, 0)
    require(capacity.f_bavail * capacity.f_frsize >= needed, 'PayloadCapacityInsufficient')
    target = mountpoint / 'configured-root' / transport['BuildId'] / transport['DerivationId']
    target.mkdir(parents=True)
    for name in names:
        fd = os.open(source / name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
        with os.fdopen(fd, 'rb') as src, (target / name).open('xb') as dest:
            require(stat.S_ISREG(os.fstat(src.fileno()).st_mode), 'DeliverySourceNotRegular')
            shutil.copyfileobj(src, dest, 1024 * 1024); dest.flush(); os.fsync(dest.fileno())
    # Fresh reopen of the actual FAT32 objects, including the aggregate logical bytes.
    actual = {}; aggregate = hashlib.sha256()
    for name in names:
        h = hashlib.sha256(); length = 0
        with (target / name).open('rb') as source_stream:
            while data := source_stream.read(1024 * 1024):
                h.update(data); length += len(data)
                if name.startswith('root.content.'): aggregate.update(data)
        actual[name] = {'Length': length, 'Sha256': h.hexdigest().upper()}
    require(actual == expected['Objects'] and aggregate.hexdigest().upper() == transport['ContentSha256'], 'PayloadReadbackChanged')
    save(evidence, 'payload-delivery-readback.json', {'Objects': actual, 'ContentSha256': aggregate.hexdigest().upper(),
        'CapacityBytes': capacity.f_blocks * capacity.f_frsize, 'RequiredBytes': needed, 'GenerationId': storage['GenerationId']})
    if corrupt:
        path = target / 'root.content.0000'
        save(evidence, 'negative-source-intent.json', {'Path': str(path.relative_to(mountpoint)), 'Offset': 4096,
            'OriginalSha256': actual[path.name]['Sha256'], 'ExpectedBindingUnchanged': True})
        with path.open('r+b') as stream:
            stream.seek(4096); value = stream.read(1); stream.seek(4096); stream.write(bytes([value[0] ^ 1])); stream.flush(); os.fsync(stream.fileno())
        with path.open('rb') as stream: changed = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
        require(changed != actual[path.name]['Sha256'], 'NegativeSourceNotChanged')
        save(evidence, 'negative-source-readback.json', {'Sha256': changed})
    fd = os.open(target, os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)
    run(['umount', str(mountpoint)])
    require(not any(str(mountpoint) == line.split()[4] for line in Path('/proc/self/mountinfo').read_text().splitlines()), 'DeliveryUnmountUnproven')
    save(evidence, 'payload-delivery-teardown.json', {'OrdinaryUnmount': True, 'FreshMountinfoAbsent': True})


def execute(evidence, stage, delivery):
    evidence, stage, delivery = map(Path, (evidence, stage, delivery))
    command = ['/opt/dotnet/dotnet', str(stage / 'harness/CanonicalImportQualification.dll'), '--import-lab-development',
        str(evidence / 'storage.json'), str(delivery / 'artifact/descriptor.json'), str(delivery / 'external-pin.json'),
        str(delivery / 'artifact/transport.json'), str(evidence / 'runtime.json'), str(evidence / 'journals.json')]
    save(evidence, 'canonical-import-dispatch-intent.json', {'Command': command})
    with (evidence / 'canonical-import.stdout').open('xb') as output, (evidence / 'canonical-import.stderr').open('xb') as errors:
        result = subprocess.run(command, stdout=output, stderr=errors, timeout=5 * 3600 + 60,
            env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C', 'DOTNET_ROOT': '/opt/dotnet', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1'})
        output.flush(); os.fsync(output.fileno()); errors.flush(); os.fsync(errors.fileno())
    save(evidence, 'canonical-import-exit.json', {'ExitCode': result.returncode,
        'StdoutSha256': digest((evidence / 'canonical-import.stdout').read_bytes()),
        'StderrSha256': digest((evidence / 'canonical-import.stderr').read_bytes())})
    print(json.dumps({'Evidence': str(evidence), 'ExitCode': result.returncode}), flush=True)
    return result.returncode
