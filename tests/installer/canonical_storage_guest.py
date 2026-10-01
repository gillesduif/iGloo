"""Bounded new-disk preparation for the tagged storage-smoke harness.

The host controller must first reopen the exact current QEMU backing FDs and
deliver host-binding.json through the console. No existing target is reusable.
Provisioning records live on runtime EXT4 before the separate session store exists.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import uuid

ENV = {'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C', 'DOTNET_ROOT': '/opt/dotnet',
       'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1'}


def require(value, code):
    if not value: raise ValueError(code)


def run(args, data=None, timeout=240):
    result = subprocess.run(args, input=data, capture_output=True, timeout=timeout, env=ENV, check=False)
    if result.returncode:
        raise RuntimeError(f'{Path(args[0]).name}:exit={result.returncode}:' + result.stdout.decode(errors='replace')[-4096:])
    return result.stdout


def digest(data): return hashlib.sha256(data).hexdigest().upper()
def encoded(value): return json.dumps(value, sort_keys=True, separators=(',', ':')).encode()


def new_disk_binding(host, device, serial, length):
    roles = {'vdb': ('IGLOO-LAB-TARGET', 40 * 1024**3), 'vdc': ('IGLOO-LAB-JOURNAL', 8 * 1024**3)}
    require(device in roles and (serial, length) == roles[device], 'NewDiskRoleChanged')
    matches = [b for b in host['ReopenedBackings'] if b['Serial'] == serial]
    require(len(matches) == 1 and matches[0]['Length'] == length and matches[0] in host['CreatedBackings'], 'NewDiskChanged')
    return matches[0]


def save(directory, name, value):
    data = value if isinstance(value, bytes) else encoded(value)
    with (directory / name).open('xb') as out:
        out.write(data); out.flush(); os.fsync(out.fileno())
    fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)
    require((directory / name).read_bytes() == data, 'ProvisioningReopenChanged')
    return data


def protect_runtime(stage):
    # Correct the copied runtime's tar-extracted UID 1000. All writes are to this
    # NEW runtime copy, never the retained runtime backing or workstation artifact.
    for root in (Path('/opt/dotnet'), stage):
        for p in [root, *root.rglob('*')]:
            require(not p.is_symlink(), 'RuntimeClosureUnexpectedSymlink')
            os.chown(p, 0, 0)
            p.chmod(0o755 if p.is_dir() or p.name in ('dotnet', 'command-gate', 'bwrap', 'createdump') else 0o644)
    observed = {}
    for root in (Path('/opt/dotnet'), stage):
        for p in [root, *root.rglob('*')]:
            st = p.lstat()
            require(st.st_uid == 0 and not st.st_mode & 0o022 and not p.is_symlink(), 'RuntimeClosureNotProtected')
            require(not any(x.startswith(('system.posix_acl', 'security.', 'trusted.')) for x in os.listxattr(p)), 'RuntimeClosureUnexpectedAcl')
            for parent in p.parents:
                info = parent.stat()
                require(info.st_uid == 0 and not info.st_mode & 0o022, 'RuntimeParentNotProtected')
            if p.is_file(): observed[str(p)] = {'Sha256': digest(p.read_bytes()), 'Uid': st.st_uid, 'Mode': stat.S_IMODE(st.st_mode)}
    return observed


def provision(host_path, stage, scope="StorageSmoke"):
    require(scope in ("StorageSmoke", "ConfiguredRootImport"), "UnknownPreparationScope")
    version = 1 if scope == "StorageSmoke" else 2
    intent_scope = {} if version == 1 else {"Scope": scope}
    host_path, stage = Path(host_path), Path(stage)
    host = json.loads(host_path.read_bytes())
    require(os.geteuid() == 0 and host_path.stat().st_uid == 0 and not host_path.stat().st_mode & 0o022, 'HostDeclarationUnprotected')
    require(not Path('/sys/firmware/efi').exists() and sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo'], 'LabBoundaryChanged')
    evidence = Path('/root') / ('storage-smoke-' + host['RunId']); evidence.mkdir(mode=0o700)
    save(evidence, 'host-binding.json', host_path.read_bytes())
    save(evidence, 'execution-closure.json', protect_runtime(stage))
    collector = stage / 'collector.py'
    hashes = {str(p): digest(p.read_bytes()) for p in (stage / 'native').glob('*.py')}
    required = ['block_session.py', 'mount_supervisor.py', 'package_broker.py', 'isolation_policy.py', 'isolation_observer.py',
                'target_files.py', 'configured_root.py', 'root_transport.py', 'configured_root_metadata.py', 'target_observer.py',
                'deployment_journal.py', 'session_import.py', 'session_configuration.py', 'session_entry.py']
    hashes = {str(stage / 'native' / n): hashes[str(stage / 'native' / n)] for n in required}
    python = Path('/usr/bin/python3').resolve()
    for p in [python, stage / 'command-gate', stage / 'bwrap', collector, Path('/usr/bin/dpkg-query'), Path('/usr/bin/openssl'), Path('/usr/bin/localedef'), Path('/usr/lib/x86_64-linux-gnu/libcrypt.so.1.1.0')]: hashes[str(p)] = digest(p.read_bytes())
    runtime = {'Python': str(python), 'Gate': str(stage / 'command-gate'), 'Entry': str(stage / 'native/session_entry.py'),
        'Collector': str(collector), 'Bubblewrap': str(stage / 'bwrap'), 'Observer': str(stage / 'native/isolation_observer.py'), 'ToolHashes': dict(sorted(hashes.items()))}
    save(evidence, 'runtime.json', runtime)
    dotnet = ['/opt/dotnet/dotnet', str(stage / 'harness/CanonicalImportQualification.dll')]
    save(evidence, 'dotnet-info.txt', run(['/opt/dotnet/dotnet', '--info']))
    def collect():
        raw = run([str(python), '-I', '-B', str(collector)]).decode()
        require(json.loads(raw)['availability'] == 'Available', 'WholeInventoryUnavailable')
        return raw
    # Complete blank observation is distinct from the later complete valid GPT snapshot.
    for dev in ('vdb', 'vdc'):
        serial = Path('/sys/class/block', dev, 'serial').read_text().strip()
        binding = new_disk_binding(host, dev, serial, int(Path('/sys/class/block', dev, 'size').read_text()) * 512)
        signatures = json.loads(run(['wipefs', '-n', '-J', '/dev/' + dev]))
        require(signatures['signatures'] == [] and not list(Path('/sys/block', dev).glob(dev + '*')), 'LabDiskNotBlank')
        save(evidence, dev + '-blank.json', {'HostBinding': binding, 'Wipefs': signatures})
    for dev, text in [('vdb', 'label:gpt\nunit:sectors\nstart=2048,size=524288,type=U\n'),
                      ('vdc', 'label:gpt\nunit:sectors\nstart=2048,type=L\n')]:
        save(evidence, dev + '-platform-intent.json', {'Device': dev, 'Table': text, 'RunId': host['RunId']})
        run(['sfdisk', '/dev/' + dev], text.encode()); run(['udevadm', 'settle'])
        save(evidence, dev + '-platform-readback.json', run(['sfdisk', '-J', '/dev/' + dev]))
    # Preserved test ESP and journal filesystem precede the owned preparation generation.
    for dev, cmd in [('vdb1', ['mkfs.vfat', '-F', '32']), ('vdc1', ['mkfs.ext4', '-q'])]:
        save(evidence, dev + '-platform-format-intent.json', {'Command': cmd, 'Device': dev})
        run(cmd + ['/dev/' + dev]); run(['udevadm', 'settle'])
        save(evidence, dev + '-platform-format-readback.txt', run(['blkid', '-p', '-o', 'export', '/dev/' + dev]))
    generation = str(uuid.uuid4())
    before = collect(); save(evidence, 'preparation-before.json', before.encode())
    save(evidence, 'generation.json', {'GenerationId': generation, 'RunId': host['RunId'], 'BeforeSha256': digest(before.encode()), 'Scope': scope})
    transitions = []
    for index, role, start, size, kind, fs in [(2, 0, 526336, 2097152, 'c12a7328-f81f-11d2-ba4b-00a0c93ec93b', 'FAT32'),
        (3, 1, 2623488, 16777216, 'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7', 'FAT32'),
        (4, 3, 19400704, 41943040, '0fc63daf-8483-4772-8e79-3d69d8477de4', 'EXT4')]:
        before = collect()
        intended = {'DevicePath': '/dev/vdb' + str(index), 'DiskDevicePath': '/dev/vdb', 'PartitionGuid': str(uuid.uuid4()),
            'PartitionType': kind, 'OffsetBytes': start * 512, 'SizeBytes': size * 512}
        intent = {**intent_scope, 'SchemaVersion': version, 'RunId': host['RunId'], 'GenerationId': generation, 'Role': role,
                  'Partition': intended, 'BeforeSha256': digest(before.encode())}
        raw_intent = save(evidence, f'{index}-create-intent.json', intent).decode()
        line = f'start={start},size={size},type={kind},uuid={intended["PartitionGuid"]}\n'
        save(evidence, f'{index}-create-provider.txt', run(['sfdisk', '--append', '/dev/vdb'], line.encode()))
        run(['udevadm', 'settle'])
        created = collect(); save(evidence, f'{index}-created.json', created.encode())
        fsuuid = str(uuid.uuid4()) if fs == 'EXT4' else uuid.uuid4().hex[:8].upper()
        filesystem = {'Type': fs, 'Uuid': fsuuid if fs == 'EXT4' else fsuuid[:4] + '-' + fsuuid[4:]}
        transition = {'Role': role, 'Intended': intended, 'Before': before, 'Created': created, 'Formatted': '',
            'FileSystem': filesystem, 'CreationIntent': raw_intent, 'FormatIntent': ''}
        request = {**host, 'Stage': 'Create', 'GenerationId': generation, 'Transition': transition}
        request_path = evidence / f'{index}-creation-validation.json'
        save(evidence, request_path.name, request)
        save(evidence, f'{index}-creation-receipt.json', run(dotnet + ['--verify-lab-transition', str(request_path), str(evidence / 'runtime.json')]))
        fmt = {**intent_scope, 'SchemaVersion': version, 'RunId': host['RunId'], 'GenerationId': generation,
            'PartitionGuid': intended['PartitionGuid'], 'FileSystem': filesystem, 'BeforeSha256': digest(created.encode())}
        transition['FormatIntent'] = save(evidence, f'{index}-format-intent.json', fmt).decode()
        command = ['mkfs.ext4', '-q', '-U', fsuuid] if fs == 'EXT4' else ['mkfs.vfat', '-F', '32', '-i', fsuuid]
        save(evidence, f'{index}-format-provider.txt', run(command + [intended['DevicePath']]))
        run(['udevadm', 'settle'])
        transition['Formatted'] = collect(); save(evidence, f'{index}-formatted.json', transition['Formatted'].encode())
        request = {**host, 'Stage': 'Format', 'GenerationId': generation, 'Transition': transition}
        request_path = evidence / f'{index}-format-validation.json'
        save(evidence, request_path.name, request)
        save(evidence, f'{index}-format-receipt.json', run(dotnet + ['--verify-lab-transition', str(request_path), str(evidence / 'runtime.json')]))
        transitions.append(transition)
    final = json.loads(collect())
    guest = [{'DevicePath': d['devicePath'], 'Serial': Path('/sys/class/block', Path(d['devicePath']).name, 'serial').read_text().strip(),
        'PhysicalSectorSize': int(Path('/sys/class/block', Path(d['devicePath']).name, 'queue/physical_block_size').read_text())} for d in final['disks']]
    save(evidence, 'storage.json', {**host, 'SchemaVersion': version, 'Provider': 'IsolatedFileBackedLab', 'Scope': scope,
        'GenerationId': generation, 'GuestDisks': guest, 'Transitions': transitions})
    destination = Path('/lab-journal')
    require(destination.is_dir() and not destination.is_symlink() and not list(destination.iterdir()), 'JournalMountpointNotEmpty')
    save(evidence, 'journal-bootstrap-intent.json', {'Device': '/dev/vdc1', 'Destination': str(destination)})
    run(['mount', '-t', 'ext4', '-o', 'nodev,nosuid,noexec', '/dev/vdc1', str(destination)])
    for name in ('session', 'import'): (destination / name).mkdir(mode=0o700)
    fd = os.open(destination, os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)
    journal_uuid = next(p['fileSystem']['uuid'] for p in final['partitions'] if p['devicePath'] == '/dev/vdc1')
    save(evidence, 'journals.json', {'SessionStore': '/lab-journal/session', 'ImportStore': '/lab-journal/import',
        'ToolPath': str(stage / 'native/deployment_journal.py'), 'ToolSha256': hashes[str(stage / 'native/deployment_journal.py')],
        'RuntimeFileSystemUuid': journal_uuid})
    with open('/dev/vdb1', 'rb') as f: preserved = hashlib.file_digest(f, 'sha256').hexdigest().upper()
    save(evidence, 'preserved-before.json', {'Device': '/dev/vdb1', 'Sha256': preserved})
    print(json.dumps({'Evidence': str(evidence), 'GenerationId': generation, 'Session': 'NotStarted', 'Import': 'NotInvoked'}))
    return evidence
