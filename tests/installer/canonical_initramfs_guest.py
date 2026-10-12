"""Initramfs successor bootstrap; no target provisioning or manual target mount."""
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
            script.name != 'canonical_initramfs_guest.py' or \
            script.parent.name != 'lab' or '..' in script.parts:
        raise ValueError('InitramfsGuestImportIsolationChanged')
    _verify_staged_files(script, expected_sha256)
    sys.path.insert(0, str(script.parent))


def _verify_staged_files(script, expected_sha256):
    """Closed file consistency against an independent host-supplied digest."""
    if type(expected_sha256) is not str or not re.fullmatch(r'[0-9A-F]{64}', expected_sha256):
        raise ValueError('InitramfsGuestImportExpectationInvalid')
    names = {'canonical_storage_guest.py', 'canonical_import_guest.py',
        'canonical_configuration_guest.py', 'canonical_initramfs_guest.py',
        'canonical_stage_guest.py', 'canonical_stage_files.py',
        'canonical_configuration_readback.py',
        'canonical_initramfs_readback.py'}
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
    directory = os.open('/', flags)
    try:
        for part in script.parent.parts[1:]:
            following = os.open(part, flags, dir_fd=directory)
            os.close(directory); directory = following
            metadata = os.fstat(directory)
            if metadata.st_uid != 0 or metadata.st_mode & 0o022:
                raise ValueError('InitramfsGuestImportParentUnprotected')

        def exact(name, limit):
            fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC,
                         dir_fd=directory)
            try:
                before = os.fstat(fd)
                if not stat.S_ISREG(before.st_mode) or before.st_uid != 0 or \
                        before.st_nlink != 1 or before.st_mode & 0o022 or \
                        not 0 < before.st_size <= limit:
                    raise ValueError('InitramfsGuestImportFileUnprotected')
                raw = os.read(fd, limit + 1)
                after = os.fstat(fd)
                if len(raw) != before.st_size or (before.st_dev, before.st_ino,
                        before.st_size, before.st_mtime_ns, before.st_ctime_ns) != \
                        (after.st_dev, after.st_ino, after.st_size,
                         after.st_mtime_ns, after.st_ctime_ns):
                    raise ValueError('InitramfsGuestImportFileChanged')
                return raw
            finally:
                os.close(fd)

        def pairs(items):
            value = {}
            for key, item in items:
                if key in value:
                    raise ValueError('InitramfsGuestImportManifestDuplicate')
                value[key] = item
            return value

        manifest_raw = exact('guest-import-closure.json', 16 * 1024)
        if hashlib.sha256(manifest_raw).hexdigest().upper() != expected_sha256:
            raise ValueError('InitramfsGuestImportExpectationChanged')
        manifest = json.loads(manifest_raw, object_pairs_hook=pairs)
        if type(manifest) is not dict or set(manifest) != {'Version', 'Stage', 'Entry', 'Files'} or \
                type(manifest['Version']) is not int or manifest['Version'] != 1 or \
                manifest['Stage'] != 'initramfs' or \
                manifest['Entry'] != script.name or type(manifest['Files']) is not dict or \
                set(manifest['Files']) != names or \
                set(os.listdir(directory)) != names | {'guest-import-closure.json'}:
            raise ValueError('InitramfsGuestImportManifestChanged')
        for name in names:
            expected = manifest['Files'][name]
            if type(expected) is not str or not re.fullmatch(r'[0-9A-F]{64}', expected) or \
                    hashlib.sha256(exact(name, 2 * 1024 * 1024)).hexdigest().upper() != expected:
                raise ValueError('InitramfsGuestImportHashChanged')
    except OSError as error:
        raise ValueError('InitramfsGuestImportClosureUnprotected') from error
    finally:
        os.close(directory)


def prepare(host_path, stage):
    stage, host_path = Path(stage), Path(host_path)
    host = json.loads(host_path.read_bytes())
    authorization = json.loads((stage/'initramfs-authorization.json').read_bytes())
    derivation = json.loads((stage/'initramfs-derivation.json').read_bytes())
    require(os.geteuid() == 0 and host_path.stat().st_uid == 0 and not host_path.stat().st_mode & 0o022 and
            authorization == derivation['Authorization'] and host['RunId'] == authorization['AttemptId'], 'InitramfsBootstrapBinding')
    require(not Path('/sys/firmware/efi').exists() and sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo'], 'LabBoundaryChanged')
    for copy in derivation['Copies']:
        backing = next(b for b in host['ReopenedBackings'] if b['Serial'] == copy['Serial'])
        require((backing['HostDevice'], backing['HostInode'], backing['Length']) ==
            (copy['DestinationDevice'], copy['DestinationInode'], copy['Length']), 'InitramfsLaunchCopyChanged')
    evidence = Path('/root')/('initramfs-'+host['RunId']); evidence.mkdir(mode=0o700)
    save(evidence, 'host-binding.json', host_path.read_bytes())
    save(evidence, 'execution-closure.json', protect_runtime(stage))
    names = ('block_session.py', 'mount_supervisor.py', 'package_broker.py', 'isolation_policy.py', 'isolation_observer.py',
        'target_files.py', 'configured_root.py', 'root_transport.py', 'configured_root_metadata.py', 'target_observer.py',
        'deployment_journal.py', 'session_import.py', 'session_configuration.py', 'session_entry.py', 'configured_successor.py',
        'initramfs_archive.py', 'initramfs_generated.py', 'initramfs_candidate.py', 'initramfs_observer.py',
        'initramfs_publication.py', 'session_initramfs.py')
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
    parent = destination/'initramfs'
    if not parent.exists(): parent.mkdir(mode=0o700)
    require(not parent.is_symlink() and parent.stat().st_uid == 0 and parent.stat().st_mode & 0o777 == 0o700, 'InitramfsStoreParent')
    stores = parent/authorization['AttemptId']; stores.mkdir(mode=0o700)
    for name in ('session', 'effects'): (stores/name).mkdir(mode=0o700)
    for path in (stores/'session', stores/'effects', stores, parent, destination):
        fd = os.open(path, os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
    save(evidence, 'journals.json', {'SessionStore': str(stores/'session'), 'ImportStore': str(stores/'effects'),
        'ToolPath': str(stage/'native/deployment_journal.py'), 'ToolSha256': hashes[str(stage/'native/deployment_journal.py')],
        'RuntimeFileSystemUuid': journal['fileSystem']['uuid']})
    # mkinitramfs invokes copied boot scripts with "prereqs" in its workspace.
    # Keep the journal's noexec mount intact; use the already qualified runtime
    # EXT4 execution workspace, with no target or ESP exposure.
    workspace_parent = Path('/var/lib/igloo/initramfs-workspaces')
    if not workspace_parent.exists(): workspace_parent.mkdir(mode=0o700)
    require(not workspace_parent.is_symlink() and workspace_parent.stat().st_uid == 0 and
        workspace_parent.stat().st_mode & 0o777 == 0o700 and not os.statvfs(workspace_parent).f_flag & os.ST_NOEXEC,
        'InitramfsWorkspaceParent')
    save(evidence, 'workspace-bootstrap-intent.json', {'Path': str(workspace_parent/host['RunId']),
        'Scope': 'RuntimeOnlyInitramfsWorkspace', 'Reason': 'Retained boot-script prereqs invocation', 'OperationId': authorization['OperationId']})
    (workspace_parent/host['RunId']).mkdir(mode=0o700)
    fd = os.open(workspace_parent, os.O_DIRECTORY | os.O_NOFOLLOW)
    try: os.fsync(fd)
    finally: os.close(fd)
    # This previously qualified plan is only a declaration. The canonical phase
    # rederives its exact inputs from the authenticated configured baseline.
    source = Path('/var/lib/igloo/initramfs-fixture-2/plan.json')
    require(digest(source.read_bytes()) == '8EC00C0E02C82087AD4639B1CA65CAF32D85D36F9522AD48733803BFF18E3F95', 'QualifiedCandidateDeclarationChanged')
    save(evidence, 'candidate-plan.json', source.read_bytes())
    import hashlib
    with open('/dev/vdb1', 'rb') as stream: preserved = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
    save(evidence, 'preserved-before.json', {'Device': '/dev/vdb1', 'Sha256': preserved})
    return evidence


def execute(evidence, stage, negative=False, *, runtime_name='runtime.json'):
    evidence, stage = Path(evidence), Path(stage); name = 'negative' if negative else 'initramfs'
    require(runtime_name in ('runtime.json', 'runtime-final.json'), 'InitramfsRuntimeDeclarationName')
    command = ['/opt/dotnet/dotnet', str(stage/'harness/CanonicalImportQualification.dll'), '--initramfs-derived-lab',
        str(stage/'configured-predecessor'), str(evidence/('negative-declaration.json' if negative else 'declaration.json')),
        str(stage/'initramfs-authorization.json'), str(evidence/runtime_name),
        str(evidence/('journals-final.json' if runtime_name == 'runtime-final.json' else 'journals.json')),
        str(stage/'external-pin.json'), str(evidence/'candidate-plan.json')]
    save(evidence, name+'-dispatch-intent.json', {'Command': command})
    with (evidence/(name+'.stdout')).open('xb') as output, (evidence/(name+'.stderr')).open('xb') as errors:
        result = subprocess.run(command, stdout=output, stderr=errors, env=ENV, timeout=5*3600+60)
        output.flush(); os.fsync(output.fileno()); errors.flush(); os.fsync(errors.fileno())
    save(evidence, name+'-exit.json', {'ExitCode': result.returncode, 'StdoutSha256': digest((evidence/(name+'.stdout')).read_bytes()),
        'StderrSha256': digest((evidence/(name+'.stderr')).read_bytes())})
    print(json.dumps({'Evidence': str(evidence), 'Operation': name, 'ExitCode': result.returncode}), flush=True)
    return result.returncode


if __name__ == '__main__':
    if len(sys.argv) == 5 and sys.argv[1] == '--prepare':
        _isolated_import_closure(sys.argv[3])
        from canonical_storage_guest import ENV, digest, protect_runtime, require, run, save
        stage = Path('/opt/igloo-lab')
        authorization = json.loads((stage/'initramfs-authorization.json').read_bytes())
        host_path = Path('/root/igloo-stage-host-binding-'+authorization['AttemptId'].replace('-', '')+'.json')
        require(Path(sys.argv[4]) == host_path and
                json.loads(host_path.read_bytes())['RunId'] == authorization['AttemptId'],
                'InitramfsPreparationHostBindingChanged')
        prepare(host_path, stage)
        print('IGLOO_STAGE_PREPARED '+sys.argv[2]+' '+digest(host_path.read_bytes()), flush=True)
    elif len(sys.argv) != 4 or sys.argv[1] != '--execute-and-capture':
        raise ValueError('InitramfsGuestCommandUnsupported')
    else:
        _isolated_import_closure(sys.argv[3])
        from canonical_storage_guest import ENV, digest, protect_runtime, require, run, save
        from canonical_stage_guest import capture_completed
        stage = Path('/opt/igloo-lab')
        authorization = json.loads((stage/'initramfs-authorization.json').read_bytes())
        evidence = Path('/root')/('initramfs-'+authorization['AttemptId'])
        require((evidence/'journals.json').is_file() and
                (evidence/'declaration.json').is_file(), 'InitramfsPreparationMissing')
        require(execute(evidence, stage) == 0, 'InitramfsExecutionFailed')
        capture_completed('initramfs', sys.argv[2])
