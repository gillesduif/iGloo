"""Same-backing successor bootstrap. Never creates partitions or filesystems.

Host checkpoint and fresh launch correlation precede this nominated journal
bootstrap. The .NET authority independently validates placement and predecessor
chains before target acquisition. Artifact objects remain on the original payload.
"""
import json
import os
from pathlib import Path
import hashlib
import re
import stat
import subprocess
import sys
import uuid

if __name__ != '__main__':
    from canonical_storage_guest import ENV, digest, protect_runtime, require, run, save


def _isolated_import_closure(expected_sha256):
    """Join the host-nominated tools-ISO manifest before local imports."""
    script = Path(__file__).absolute()
    if not sys.flags.isolated or not sys.dont_write_bytecode or \
            script.name != 'canonical_configuration_guest.py' or \
            script.parent.name != 'lab' or '..' in script.parts:
        raise ValueError('ConfigurationGuestImportIsolationChanged')
    _verify_staged_files(script, expected_sha256)
    sys.path.insert(0, str(script.parent))


def _verify_staged_files(script, expected_sha256):
    """Closed file consistency against an independent host-supplied digest."""
    if type(expected_sha256) is not str or not re.fullmatch(r'[0-9A-F]{64}', expected_sha256):
        raise ValueError('ConfigurationGuestImportExpectationInvalid')
    names = {'canonical_storage_guest.py', 'canonical_import_guest.py',
        'canonical_configuration_guest.py', 'canonical_stage_guest.py',
        'canonical_stage_files.py', 'canonical_configuration_readback.py',
        'canonical_initramfs_readback.py'}
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
    directory = os.open('/', flags)
    try:
        for part in script.parent.parts[1:]:
            following = os.open(part, flags, dir_fd=directory)
            os.close(directory); directory = following
            metadata = os.fstat(directory)
            if metadata.st_uid != 0 or metadata.st_mode & 0o022:
                raise ValueError('ConfigurationGuestImportParentUnprotected')

        def exact(name, limit):
            fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC,
                         dir_fd=directory)
            try:
                before = os.fstat(fd)
                if not stat.S_ISREG(before.st_mode) or before.st_uid != 0 or \
                        before.st_nlink != 1 or before.st_mode & 0o022 or \
                        not 0 < before.st_size <= limit:
                    raise ValueError('ConfigurationGuestImportFileUnprotected')
                raw = os.read(fd, limit + 1)
                after = os.fstat(fd)
                if len(raw) != before.st_size or (before.st_dev, before.st_ino,
                        before.st_size, before.st_mtime_ns, before.st_ctime_ns) != \
                        (after.st_dev, after.st_ino, after.st_size,
                         after.st_mtime_ns, after.st_ctime_ns):
                    raise ValueError('ConfigurationGuestImportFileChanged')
                return raw
            finally:
                os.close(fd)

        def pairs(items):
            value = {}
            for key, item in items:
                if key in value:
                    raise ValueError('ConfigurationGuestImportManifestDuplicate')
                value[key] = item
            return value

        manifest_raw = exact('guest-import-closure.json', 16 * 1024)
        if hashlib.sha256(manifest_raw).hexdigest().upper() != expected_sha256:
            raise ValueError('ConfigurationGuestImportExpectationChanged')
        manifest = json.loads(manifest_raw, object_pairs_hook=pairs)
        if type(manifest) is not dict or set(manifest) != {'Version', 'Stage', 'Entry', 'Files'} or \
                type(manifest['Version']) is not int or manifest['Version'] != 1 or \
                manifest['Stage'] != 'configuration' or \
                manifest['Entry'] != script.name or type(manifest['Files']) is not dict or \
                set(manifest['Files']) != names or \
                set(os.listdir(directory)) != names | {'guest-import-closure.json'}:
            raise ValueError('ConfigurationGuestImportManifestChanged')
        for name in names:
            expected = manifest['Files'][name]
            if type(expected) is not str or not re.fullmatch(r'[0-9A-F]{64}', expected) or \
                    hashlib.sha256(exact(name, 2 * 1024 * 1024)).hexdigest().upper() != expected:
                raise ValueError('ConfigurationGuestImportHashChanged')
    except OSError as error:
        raise ValueError('ConfigurationGuestImportClosureUnprotected') from error
    finally:
        os.close(directory)


def prepare(host_path, stage):
    host_path, stage = Path(host_path), Path(stage)
    host = json.loads(host_path.read_bytes())
    require(os.geteuid() == 0 and host_path.stat().st_uid == 0 and not host_path.stat().st_mode & 0o022, 'HostDeclarationUnprotected')
    require(not Path('/sys/firmware/efi').exists() and sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo'], 'LabBoundaryChanged')
    predecessor = stage / 'predecessor'
    old = json.loads((predecessor / 'storage.json').read_bytes())
    checkpoint = json.loads((predecessor / 'checkpoint.json').read_bytes())
    derived = checkpoint['TargetContinuation'] == 'IndependentCheckpointCopy'
    derivation = json.loads((stage/'configuration-derivation.json').read_bytes()) if derived else None
    require(derived or checkpoint['TargetContinuation'] == 'SameBacking', 'CheckpointScopeChanged')
    for serial in ('IGLOO-LAB-TARGET', 'IGLOO-LAB-JOURNAL'):
        before = next(b for b in old['ReopenedBackings'] if b['Serial'] == serial)
        now = next(b for b in host['ReopenedBackings'] if b['Serial'] == serial)
        if not derived:
            require(all(before[k] == now[k] for k in ('HostDevice', 'HostInode', 'Length')), 'ContinuationBackingChanged')
        else:
            copied = next(c for c in derivation['Copies'] if c['Serial'] == serial)
            require((now['HostDevice'], now['HostInode'], now['Length']) ==
                (copied['DestinationDevice'], copied['DestinationInode'], copied['Length']) and
                (before['HostDevice'], before['HostInode']) != (now['HostDevice'], now['HostInode']), 'DerivedBackingChanged')
    evidence = Path('/root') / ('core-configuration-' + host['RunId']); evidence.mkdir(mode=0o700)
    save(evidence, 'host-binding.json', host_path.read_bytes())
    save(evidence, 'execution-closure.json', protect_runtime(stage))
    collector = stage / 'collector.py'; python = Path('/usr/bin/python3').resolve()
    native = ('block_session.py', 'mount_supervisor.py', 'package_broker.py', 'isolation_policy.py', 'isolation_observer.py',
        'target_files.py', 'configured_root.py', 'root_transport.py', 'configured_root_metadata.py', 'target_observer.py',
        'deployment_journal.py', 'session_import.py', 'session_configuration.py', 'session_entry.py')
    paths = [stage/'native'/n for n in native] + [python, stage/'command-gate', stage/'bwrap', collector,
        Path('/usr/bin/dpkg-query'), Path('/usr/bin/openssl'), Path('/usr/bin/localedef'), Path('/usr/lib/x86_64-linux-gnu/libcrypt.so.1.1.0')]
    hashes = {str(p): digest(p.read_bytes()) for p in paths}
    runtime = {'Python': str(python), 'Gate': str(stage/'command-gate'), 'Entry': str(stage/'native/session_entry.py'),
        'Collector': str(collector), 'Bubblewrap': str(stage/'bwrap'), 'Observer': str(stage/'native/isolation_observer.py'), 'ToolHashes': hashes}
    save(evidence, 'runtime.json', runtime)
    raw = run([str(python), '-I', '-B', str(collector)])
    inventory = json.loads(raw); require(inventory['availability'] == 'Available', 'WholeInventoryUnavailable')
    save(evidence, 'continuation-inventory.json', raw)
    journals = next(p for p in inventory['partitions'] if p['devicePath'] == '/dev/vdc1')
    require(journals['fileSystem'] == {'availability': 'Available', 'type': 'EXT4', 'uuid': 'b5071fc3-4433-4972-828e-da59eb0c27f6'}, 'JournalIdentityChanged')
    imported = 'A18E09649CD6E97EE8257EBF46F74CD0FFBF3A19E6EB49EDEF358133B2CEDDE9'
    closed = 'F102D92819EC0D42C6DEA88C475583E2707C2B65214BA610B2FFC1AF6164817A'
    declaration = {**host, 'OperationId': str(uuid.uuid4()), 'CheckpointSha256': checkpoint['CheckpointSha256'],
        'PredecessorImportSha256': imported, 'PredecessorCloseSha256': closed}
    if derived:
        authorization = json.loads((stage/'derived-authorization.json').read_bytes())
        require(derivation['Authorization'] == authorization and derivation['AuthorizationSha256'] == digest((stage/'derived-authorization.json').read_bytes()), 'AuthorizationChanged')
        declaration.update(OperationId=authorization['OperationId'], Derivation=derivation)
    save(evidence, 'continuation.json', declaration)
    negative = {**declaration, 'OperationId': str(uuid.uuid4()), 'PredecessorImportSha256': '0'*64}
    save(evidence, 'negative-continuation.json', negative)
    destination = Path('/lab-journal')
    require(destination.is_dir() and not destination.is_symlink() and not list(destination.iterdir()), 'JournalMountpointNotEmpty')
    save(evidence, 'journal-bootstrap-intent.json', {'Device': '/dev/vdc1', 'FileSystem': journals['fileSystem'],
        'Destination': str(destination), 'Predecessor': imported, 'OperationId': declaration['OperationId']})
    run(['mount', '-t', 'ext4', '-o', 'nodev,nosuid,noexec', '/dev/vdc1', str(destination)])
    parent = destination / ('configuration-derived' if derived else 'configuration')
    if not parent.exists(): parent.mkdir(mode=0o700)
    require(not parent.is_symlink() and parent.stat().st_uid == 0 and parent.stat().st_mode & 0o777 == 0o700, 'ConfigurationStoreParentUnprotected')
    stores = parent / (host['RunId'] if derived else imported); stores.mkdir(mode=0o700)
    for name in ('session', 'effects'): (stores/name).mkdir(mode=0o700)
    for path in (stores/'session', stores/'effects', stores, parent, destination):
        fd = os.open(path, os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    save(evidence, 'journals.json', {'SessionStore': str(stores/'session'), 'ImportStore': str(stores/'effects'),
        'ToolPath': str(stage/'native/deployment_journal.py'), 'ToolSha256': hashes[str(stage/'native/deployment_journal.py')],
        'RuntimeFileSystemUuid': journals['fileSystem']['uuid']})
    import hashlib
    with open('/dev/vdb1', 'rb') as stream: preserved = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
    save(evidence, 'preserved-before.json', {'Device': '/dev/vdb1', 'Sha256': preserved})
    return evidence


def execute(evidence, stage, negative=False):
    evidence, stage = Path(evidence), Path(stage)
    name = 'negative' if negative else 'configuration'
    command = ['/opt/dotnet/dotnet', str(stage/'harness/CanonicalImportQualification.dll'), '--configure-lab',
        str(stage/'predecessor'), str(evidence/('negative-continuation.json' if negative else 'continuation.json')),
        str(evidence/'runtime.json'), str(evidence/'journals.json'), str(stage/'external-pin.json')]
    if (stage/'derived-authorization.json').exists():
        command[2] = '--configure-derived-lab'; command.append(str(stage/'derived-authorization.json'))
    save(evidence, name+'-dispatch-intent.json', {'Command': command})
    with (evidence/(name+'.stdout')).open('xb') as output, (evidence/(name+'.stderr')).open('xb') as errors:
        result = subprocess.run(command, stdout=output, stderr=errors, env=ENV, timeout=5*3600+60)
        output.flush(); os.fsync(output.fileno()); errors.flush(); os.fsync(errors.fileno())
    save(evidence, name+'-exit.json', {'ExitCode': result.returncode,
        'StdoutSha256': digest((evidence/(name+'.stdout')).read_bytes()), 'StderrSha256': digest((evidence/(name+'.stderr')).read_bytes())})
    print(json.dumps({'Evidence': str(evidence), 'Operation': name, 'ExitCode': result.returncode}), flush=True)
    return result.returncode


if __name__ == '__main__':
    if len(sys.argv) == 5 and sys.argv[1] == '--prepare':
        _isolated_import_closure(sys.argv[3])
        from canonical_storage_guest import ENV, digest, protect_runtime, require, run, save
        stage = Path('/opt/igloo-lab')
        authorization = json.loads((stage/'derived-authorization.json').read_bytes())
        host_path = Path('/root/igloo-stage-host-binding-'+authorization['AttemptId'].replace('-', '')+'.json')
        require(Path(sys.argv[4]) == host_path and
                json.loads(host_path.read_bytes())['RunId'] == authorization['AttemptId'],
                'ConfigurationPreparationHostBindingChanged')
        prepare(host_path, stage)
        print('IGLOO_STAGE_PREPARED '+sys.argv[2]+' '+digest(host_path.read_bytes()), flush=True)
    elif len(sys.argv) != 4 or sys.argv[1] != '--execute-and-capture':
        raise ValueError('ConfigurationGuestCommandUnsupported')
    else:
        _isolated_import_closure(sys.argv[3])
        from canonical_storage_guest import ENV, digest, protect_runtime, require, run, save
        from canonical_stage_guest import capture_completed
        stage = Path('/opt/igloo-lab')
        authorization = json.loads((stage/'derived-authorization.json').read_bytes())
        evidence = Path('/root')/('core-configuration-'+authorization['AttemptId'])
        require((evidence/'journals.json').is_file() and
                (evidence/'continuation.json').is_file(), 'ConfigurationPreparationMissing')
        require(execute(evidence, stage) == 0, 'ConfigurationExecutionFailed')
        capture_completed('configuration', sys.argv[2])
