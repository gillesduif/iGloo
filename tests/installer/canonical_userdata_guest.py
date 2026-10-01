"""Provision lab-authored documents on the nominated independent FAT32 copy.

Staging is a separate, powered-off-and-rehashed provisioning phase. It never
mounts the target root or changes journals, and is not producer completion.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import uuid

if __name__ == "__main__":
    for parent in (Path(__file__).resolve().parent, *Path(__file__).resolve().parents):
        info = parent.stat()
        if info.st_uid != 0 or info.st_mode & 0o022: raise ValueError("UserDataStagingToolProtection")
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from canonical_storage_guest import ENV, require, run, save, digest, encoded, protect_runtime


def payload_inventory(fd):
    from target_files import beneath, mount_id
    result = {}; total = 0; mount = mount_id(fd)
    def visit(parent, prefix):
        nonlocal total
        for leaf in sorted(os.listdir(parent)):
            name = prefix+leaf; s = os.stat(leaf, dir_fd=parent, follow_symlinks=False)
            require(len(result) < 4096 and len(name) <= 1024 and (stat.S_ISDIR(s.st_mode) or stat.S_ISREG(s.st_mode)), 'PayloadInventoryUnsupported')
            child = beneath(parent, leaf, os.O_RDONLY | (os.O_DIRECTORY if stat.S_ISDIR(s.st_mode) else 0))
            try:
                require(mount_id(child) == mount, 'PayloadInventoryMountChanged')
                if stat.S_ISDIR(s.st_mode):
                    result[name] = {'Kind': 'Directory', 'Length': 0, 'Sha256': None}; visit(child, name+'/')
                else:
                    current = os.fstat(child); h = hashlib.sha256(); length = 0
                    require(current.st_nlink == 1, 'PayloadInventoryAlias')
                    while block := os.read(child, 1024*1024):
                        length += len(block); total += len(block); require(total <= 32*1024**3, 'PayloadInventoryBound'); h.update(block)
                    after = os.fstat(child)
                    require((current.st_ino, current.st_size, current.st_mtime_ns, current.st_ctime_ns) ==
                        (after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns) and length == current.st_size, 'PayloadChangedDuringRead')
                    result[name] = {'Kind': 'File', 'Length': length, 'Sha256': h.hexdigest().upper()}
            finally: os.close(child)
    visit(fd, '')
    return result


def stage_documents(evidence, stage, host):
    evidence, stage = Path(evidence), Path(stage)
    save(evidence, 'staging-execution-closure.json', protect_runtime(stage))
    sys.path.insert(0, str(stage/'native'))
    import userdata_contract as contract
    from target_files import beneath
    a = json.loads((stage/'userdata-authorization.json').read_bytes())
    copied = json.loads((stage/'userdata-initial-copy.json').read_bytes())
    raw = (stage/'userdata-plan.json').read_bytes(); plan = contract.validate(raw)
    require(os.geteuid() == 0 and a == copied['Authorization'] and a['Scope'] == 'OneInitramfsCheckpointSelectedDocuments' and
        host['RunId'] == a['AttemptId'] and plan['GenerationId'] == a['GenerationId'] and plan['OperationId'] == a['OperationId'] and
        digest(raw) == a['TransferSha256'] and not Path('/sys/firmware/efi').exists() and
        sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo'], 'UserDataStagingAuthority')
    for c in copied['Copies']:
        b = next(b for b in host['ReopenedBackings'] if b['Serial'] == c['Serial'])
        require(b in host['CreatedBackings'] and (b['HostDevice'], b['HostInode'], b['Length']) ==
            (c['DestinationDevice'], c['DestinationInode'], c['Length']) and (c['SourceDevice'], c['SourceInode']) !=
            (c['DestinationDevice'], c['DestinationInode']), 'UserDataStagingBackingAlias')
    inventory = json.loads(run([str(Path('/usr/bin/python3').resolve()), '-I', '-B', str(stage/'collector.py')]))
    require(inventory['availability'] == 'Available', 'UserDataWholeInventoryUnavailable')
    binding = json.loads((stage/'payload-staging-binding.json').read_bytes())
    p = next(p for p in inventory['partitions'] if p['devicePath'] == '/dev/vdb3')
    disk = next(d for d in inventory['disks'] if d['devicePath'] == p['diskDevicePath'])
    require(binding['Role'] == 2 and binding['FileSystem'] == 'FAT32' and p['fileSystem']['availability'] == 'Available' and
        p['fileSystem']['type'] == 'FAT32' and p['fileSystem']['uuid'].upper() == binding['FileSystemUuid'].upper() and
        p['partitionGuid'] == binding['StoragePartition']['PartitionGuid'] and
        p['partitionType'] == binding['StoragePartition']['PartitionType'] and
        p['offsetBytes'] == binding['StoragePartition']['OffsetBytes'] and p['sizeBytes'] == binding['StoragePartition']['SizeBytes'] and
        disk['gptDiskGuid'] == binding['StoragePartition']['Disk']['GptDiskGuid'], 'UserDataStagingPayloadChanged')
    require(not any(line.split()[2] == str(os.major(os.stat('/dev/vdb3').st_rdev))+':'+str(os.minor(os.stat('/dev/vdb3').st_rdev))
        for line in Path('/proc/self/mountinfo').read_text().splitlines()), 'UserDataPayloadAlreadyMounted')
    data = json.loads((stage/'synthetic-documents.json').read_bytes())
    require(set(data) == {e['Source'] for e in plan['Entries'] if e['Kind'] == 'File'}, 'UserDataSyntheticSelectionChanged')
    decoded = {name: base64.b64decode(value, validate=True) for name, value in data.items()}
    for e in plan['Entries']:
        if e['Kind'] == 'File': require(len(decoded[e['Source']]) == e['Length'] and digest(decoded[e['Source']]) == e['Sha256'], 'UserDataSyntheticContentChanged')
    prefix = 'userdata/'+plan['OperationId']+'/source'; mount = evidence/'payload-staging'; mount.mkdir(mode=0o700)
    intent = save(evidence, 'userdata-staging-intent.json', {'Scope': 'LabAuthoredStagedDocumentsV1', 'OperationId': plan['OperationId'],
        'TransferSha256': digest(raw), 'Payload': binding, 'SourcePrefix': prefix, 'HostBindingSha256': digest(encoded(host))})
    run(['mount', '-t', 'vfat', '-o', 'nodev,nosuid,noexec', '/dev/vdb3', str(mount)])
    fd = os.open(mount, os.O_RDONLY | os.O_DIRECTORY)
    try:
        before = payload_inventory(fd); require('userdata' not in before, 'UserDataStagingConflict')
        space = os.fstatvfs(fd)
        allocation = sum(((e['Length']+space.f_frsize-1)//space.f_frsize)*space.f_frsize for e in plan['Entries'])
        require(space.f_bavail*space.f_frsize >= allocation+(len(plan['Entries'])+8)*space.f_frsize+16*1024**2, 'UserDataPayloadCapacity')
        relative_dirs = {'userdata', 'userdata/'+plan['OperationId'], prefix}
        for e in plan['Entries']:
            name = prefix+'/'+e['Source']; pieces = name.split('/')
            relative_dirs.update('/'.join(pieces[:i]) for i in range(1, len(pieces)))
            if e['Kind'] == 'Directory': relative_dirs.add(name)
        for name in sorted(relative_dirs, key=lambda n: (n.count('/'), n)):
            parent, _, leaf = name.rpartition('/'); directory = beneath(fd, parent or '.', os.O_RDONLY | os.O_DIRECTORY)
            try: os.mkdir(leaf, dir_fd=directory); os.fsync(directory)
            finally: os.close(directory)
        decoded['unselected-sentinel.txt'] = b'Lab-only unselected source; never imported.\n'
        for name, value in sorted(decoded.items()):
            output = beneath(fd, prefix+'/'+name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            try:
                pending = memoryview(value)
                while pending:
                    n = os.write(output, pending); require(n > 0, 'UserDataStagingShortWrite'); pending = pending[n:]
                os.fsync(output)
            finally: os.close(output)
        os.fsync(fd)
        # Fresh protected observer process reopens every file through this exact
        # inherited filesystem handle. No alternative source or target path.
        observer = run_observer(stage, fd)
        unrelated = {k: v for k, v in observer.items() if k != 'userdata' and not k.startswith('userdata/')}
        require(unrelated == before, 'UserDataUnrelatedPayloadChanged')
        selected = {k[len(prefix)+1:]: v for k, v in observer.items() if k.startswith(prefix+'/')}
        from userdata_source import selected_content
        selected_content(plan, selected)
        save(evidence, 'userdata-guest-delivery.json', {'SchemaVersion': 1, 'Origin': 'LabAuthoredStagedDocumentsV1',
            'GenerationId': plan['GenerationId'], 'OperationId': plan['OperationId'], 'SourcePrefix': prefix,
            'PayloadUuid': binding['FileSystemUuid'], 'TransferSha256': digest(raw), 'Inventory': selected,
            'PayloadBeforeSha256': digest(encoded(before)), 'UnrelatedPayloadAfterSha256': digest(encoded(unrelated)),
            'StagingIntentSha256': digest(intent)})
    finally: os.close(fd)
    run(['umount', str(mount)])
    require(not any(str(mount) == line.split()[4] for line in Path('/proc/self/mountinfo').read_text().splitlines()), 'UserDataStagingUnmountUnproven')
    save(evidence, 'userdata-staging-close.json', {'OrdinaryUnmount': True, 'FreshMountAbsence': True})


def run_observer(stage, fd):
    result = subprocess.run(['/usr/bin/python3', '-I', '-B', str(stage/'lab/canonical_userdata_guest.py'), '--observe-payload', str(fd)],
        pass_fds=(fd,), capture_output=True, timeout=1800, check=False, env=ENV)
    require(result.returncode == 0 and 0 < len(result.stdout) < 1024*1024, 'UserDataPayloadObserverUnavailable')
    return json.loads(result.stdout)


def prepare(host_path, stage):
    stage, host_path = Path(stage), Path(host_path)
    host = json.loads(host_path.read_bytes())
    authorization = json.loads((stage/'userdata-authorization.json').read_bytes())
    derivation = json.loads((stage/'userdata-derivation.json').read_bytes())
    require(os.geteuid() == 0 and host_path.stat().st_uid == 0 and not host_path.stat().st_mode & 0o022 and
            authorization == derivation['Authorization'] and host['RunId'] == authorization['AttemptId'], 'UserDataBootstrapBinding')
    require(not Path('/sys/firmware/efi').exists() and sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo'], 'LabBoundaryChanged')
    for copy in derivation['Copies']:
        backing = next(b for b in host['ReopenedBackings'] if b['Serial'] == copy['Serial'])
        require((backing['HostDevice'], backing['HostInode'], backing['Length']) ==
            (copy['DestinationDevice'], copy['DestinationInode'], copy['Length']), 'UserDataLaunchCopyChanged')
    evidence = Path('/root')/('userdata-'+host['RunId']); evidence.mkdir(mode=0o700)
    save(evidence, 'host-binding.json', host_path.read_bytes())
    save(evidence, 'execution-closure.json', protect_runtime(stage))
    names = ('block_session.py', 'mount_supervisor.py', 'package_broker.py', 'isolation_policy.py', 'isolation_observer.py',
        'target_files.py', 'configured_root.py', 'root_transport.py', 'configured_root_metadata.py', 'target_observer.py',
        'deployment_journal.py', 'session_import.py', 'session_configuration.py', 'session_entry.py', 'configured_successor.py',
        'initramfs_archive.py', 'initramfs_generated.py', 'initramfs_candidate.py', 'initramfs_observer.py',
        'initramfs_publication.py', 'session_initramfs.py', 'debian_first_boot.py', 'userdata_contract.py',
        'userdata_producer.py', 'userdata_source.py', 'userdata_admission.py', 'userdata_successor.py', 'session_userdata.py')
    python = Path('/usr/bin/python3').resolve(); collector = stage/'collector.py'
    paths = [stage/'native'/n for n in names] + [python, stage/'command-gate', stage/'bwrap', collector,
        Path('/usr/bin/dpkg-query'), Path('/usr/bin/openssl'), Path('/usr/bin/localedef'), Path('/usr/lib/x86_64-linux-gnu/libcrypt.so.1.1.0')]
    hashes = {str(p): digest(p.read_bytes()) for p in paths}
    save(evidence, 'runtime.json', {'Python': str(python), 'Gate': str(stage/'command-gate'), 'Entry': str(stage/'native/session_entry.py'),
        'Collector': str(collector), 'Bubblewrap': str(stage/'bwrap'), 'Observer': str(stage/'native/isolation_observer.py'), 'ToolHashes': hashes})
    raw = run([str(python), '-I', '-B', str(collector)]); inventory = json.loads(raw)
    require(inventory['availability'] == 'Available', 'WholeInventoryUnavailable')
    save(evidence, 'inventory.json', raw)
    journal = next(p for p in inventory['partitions'] if p['devicePath'] == '/dev/vdc1')
    require(journal['fileSystem'] == {'availability': 'Available', 'type': 'EXT4', 'uuid': 'b5071fc3-4433-4972-828e-da59eb0c27f6'}, 'JournalIdentityChanged')
    declaration = {**host, 'OperationId': authorization['OperationId'], 'Derivation': derivation}
    save(evidence, 'declaration.json', declaration)
    save(evidence, 'negative-declaration.json', {**declaration, 'OperationId': str(uuid.uuid4())})
    destination = Path('/lab-journal')
    require(destination.is_dir() and not destination.is_symlink() and not list(destination.iterdir()), 'JournalMountpointNotEmpty')
    save(evidence, 'journal-bootstrap-intent.json', {'Device': '/dev/vdc1', 'FileSystem': journal['fileSystem'],
        'Destination': str(destination), 'OperationId': authorization['OperationId']})
    run(['mount', '-t', 'ext4', '-o', 'nodev,nosuid,noexec', '/dev/vdc1', str(destination)])
    parent = destination/'userdata'
    if not parent.exists(): parent.mkdir(mode=0o700)
    require(not parent.is_symlink() and parent.stat().st_uid == 0 and parent.stat().st_mode & 0o777 == 0o700, 'UserDataStoreParent')
    stores = parent/authorization['AttemptId']; stores.mkdir(mode=0o700)
    for name in ('session', 'effects'): (stores/name).mkdir(mode=0o700)
    for path in (stores/'session', stores/'effects', stores, parent, destination):
        fd = os.open(path, os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    save(evidence, 'journals.json', {'SessionStore': str(stores/'session'), 'ImportStore': str(stores/'effects'),
        'ToolPath': str(stage/'native/deployment_journal.py'), 'ToolSha256': hashes[str(stage/'native/deployment_journal.py')],
        'RuntimeFileSystemUuid': journal['fileSystem']['uuid']})
    with open('/dev/vdb1', 'rb') as stream: preserved = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
    save(evidence, 'preserved-before.json', {'Device': '/dev/vdb1', 'Sha256': preserved})
    return evidence


def execute(evidence, stage, negative=False):
    evidence, stage = Path(evidence), Path(stage); name = 'negative' if negative else 'userdata'
    command = ['/opt/dotnet/dotnet', str(stage/'harness/CanonicalImportQualification.dll'), '--userdata-derived-lab',
        str(stage/'initramfs-predecessor'), str(evidence/('negative-declaration.json' if negative else 'declaration.json')),
        str(stage/'userdata-authorization.json'), str(evidence/'runtime.json'), str(evidence/'journals.json'),
        str(stage/'external-pin.json'), str(stage/'userdata-plan.json'), str(stage/'userdata-delivery.json')]
    save(evidence, name+'-dispatch-intent.json', {'Command': command})
    with (evidence/(name+'.stdout')).open('xb') as output, (evidence/(name+'.stderr')).open('xb') as errors:
        result = subprocess.run(command, stdout=output, stderr=errors, env=ENV, timeout=5*3600+60)
        output.flush(); os.fsync(output.fileno()); errors.flush(); os.fsync(errors.fileno())
    save(evidence, name+'-exit.json', {'ExitCode': result.returncode,
        'StdoutSha256': digest((evidence/(name+'.stdout')).read_bytes()), 'StderrSha256': digest((evidence/(name+'.stderr')).read_bytes())})
    return result.returncode


if __name__ == '__main__':
    require(len(sys.argv) == 3 and sys.argv[1] == '--observe-payload', 'ReadOnlyPayloadObserverOnly')
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'native'))
    print(json.dumps(payload_inventory(int(sys.argv[2])), sort_keys=True, separators=(',', ':')), flush=True)
