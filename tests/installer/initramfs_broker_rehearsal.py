"""Opt-in retained-configured-root candidate fixture inside the isolated lab only.

No canonical ownership, target publication, configuration replay or retry. A
generated candidate is not a qualified installed initramfs or a phase capability.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid


def run(stage, source, manifest_path, destination, predecessor_observation, predecessor_hash, *, diagnostic=False):
    stage, source, destination = map(Path, (stage, source, destination))
    sys.path.insert(0, str(stage/'native'))
    import configured_root as root
    import configured_successor as successor
    import initramfs_candidate as candidate
    from isolation_policy import canonical, digest, require
    from package_broker import DirectoryLease, PackageBroker, Runtime
    from target_files import mount_id
    require(os.geteuid() == 0 and not destination.exists() and os.statvfs(source).f_flag & os.ST_RDONLY,
            'FreshReadOnlyConfiguredFixtureRequired')
    require(sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo'] and not Path('/sys/firmware/efi').exists(),
            'InitramfsFixtureBoundaryChanged')
    raw = Path(manifest_path).read_bytes()
    require(digest(raw) == '5BE0814960F7D87B3FB3379688D60873028921D4D8F0BF504DD4C0EB6AE3B9B2', 'FixtureManifestChanged')
    original = root.validate_manifest(raw)
    destination.mkdir(mode=0o700)
    def record(name, value):
        data = canonical(value)
        with (destination/name).open('xb') as output:
            output.write(data); output.flush(); os.fsync(output.fileno())
        fd = os.open(destination, os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
        require((destination/name).read_bytes() == data, 'FixtureJournalReopenChanged')
    operation = str(uuid.uuid4())
    executed = {str(p.relative_to(stage)): digest(p.read_bytes()) for p in sorted((stage/'native').glob('*.py'))}
    executed.update({name: digest((stage/name).read_bytes()) for name in
        ('lab/initramfs_broker_rehearsal.py', 'command-gate', 'bwrap')})
    record('execution.json', executed)
    record('intent.json', {'Scope': 'RetainedInitramfsHelperFixture', 'OperationId': operation,
        'ConfigurationResultSha256': predecessor_hash, 'PublicationAuthorized': False,
        'ExecutionSha256': digest(canonical(executed))})
    with_lease = DirectoryLease(str(source))
    info = os.fstat(with_lease.fd)
    view = root.RootView(with_lease.fd, info.st_dev, info.st_ino, mount_id(with_lease.fd))
    try:
        configured = successor.configured_manifest(view, original, predecessor_observation)
        record('source-baseline.json', {'ConfiguredEntriesSha256': digest(canonical(configured['Entries'])),
            'PackagesSha256': root.verify_dpkg(view, original)})
        print('CONFIGURED_SOURCE_VERIFIED', flush=True)
        target = destination/'root'; target.mkdir(mode=0o700)
        subprocess.run(['/usr/bin/cp', '-a', '--reflink=auto', str(source) + '/.', str(target)], check=True)
    finally:
        view.close(); with_lease.close()
    lease = DirectoryLease(str(target)); info = os.fstat(lease.fd)
    view = root.RootView(lease.fd, info.st_dev, info.st_ino, mount_id(lease.fd))
    resources = {'root': lease}
    try:
        successor.configured_manifest(view, original, predecessor_observation)
        root_uuid = '398f0d87-e17c-46d6-866f-24d10d747006'
        plan = candidate.declaration(configured, root_uuid, predecessor_hash)
        import re
        candidate.verify_execution_inputs(view, configured, plan,
            set(re.findall(r'^vendor_id\s*:\s*(\S+)', Path('/proc/cpuinfo').read_text(), re.M)))
        record('plan.json', plan)
        workspace = candidate.prepare_workspace(destination/'workspace', view, configured)
        payload = destination/'empty-payload'; payload.mkdir(mode=0o700)
        resources.update(workspace=DirectoryLease(str(workspace)), payload=DirectoryLease(str(payload)))
        python = str(Path(sys.executable).resolve())
        runtime = Runtime(str(stage/'bwrap'), digest((stage/'bwrap').read_bytes()), str(stage/'command-gate'),
            digest((stage/'command-gate').read_bytes()), python, digest(Path(python).read_bytes()),
            str(stage/'native/isolation_observer.py'), digest((stage/'native/isolation_observer.py').read_bytes()))
        command = candidate.launch(operation, digest(canonical(plan)), plan)
        record('preflight.json', {'CommandSha256': command.public_identity(), 'ToolSha256': command.tool_hash,
            'Policy': candidate.POLICY, 'RootReadOnlyInChild': True, 'Workspace': str(workspace)})
        result = PackageBroker(runtime).execute(command, resources, lambda operation_hash, isolation_hash:
            record('generation-intent.json', {'OperationSha256': operation_hash, 'IsolationSha256': isolation_hash}))
        record('generation-result.json', result)
        print(json.dumps({'Generation': result['State'], 'ExitCode': result['ExitCode'], 'Code': result.get('Code')}), flush=True)
        successor.configured_manifest(view, original, predecessor_observation)
        record('unchanged-configured-root.json', {'ConfiguredEntriesSha256': digest(canonical(configured['Entries']))})
        require(result['State'] == 'Exited' and result['ExitCode'] == 0, 'InitramfsGeneratorFailed')
        output_fd = os.open(workspace/'candidate.img', os.O_RDONLY | os.O_NOFOLLOW)
        try: os.fsync(output_fd)
        finally: os.close(output_fd)
        output_fd = os.open(workspace, os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(output_fd)
        finally: os.close(output_fd)
        if diagnostic:
            # Test-only execution evidence is immutable. Later observer runs write
            # separate records; they cannot turn this into an acceptance receipt.
            result = {'DiagnosticOnly': True, 'CompleteImageQualification': False,
                'Candidate': str(workspace/'candidate.img'), 'GenerationExitCode': 0}
            record('diagnostic-result.json', result)
            return result
        # A fresh process receives only public declarations and read-only source
        # references. No account/credential/key contents enter fixture journals.
        request = {'Stage': str(stage), 'Target': str(target), 'Candidate': str(workspace/'candidate.img'),
            'Manifest': str(manifest_path), 'PredecessorObservation': predecessor_observation, 'Plan': plan}
        record('observer-request.json', request)
        observed = subprocess.run([python, '-I', '-B', str(stage/'lab/initramfs_broker_rehearsal.py'),
            str(destination/'observer-request.json')], capture_output=True, timeout=7200, check=False)
        record('observer-process.json', {'ExitCode': observed.returncode,
            'StdoutSha256': digest(observed.stdout), 'StderrSha256': digest(observed.stderr)})
        if observed.returncode != 0:
            require(len(observed.stderr) <= 65536, 'ObserverDiagnosticBudget')
            with (destination/'observer-private.stderr').open('xb') as output:
                output.write(observed.stderr); output.flush(); os.fsync(output.fileno())
        require(observed.returncode == 0 and len(observed.stdout) < 65536, 'InitramfsIndependentObserverFailed')
        readback = json.loads(observed.stdout)
        record('candidate-observation.json', readback)
        require(readback.get('CompleteImageQualification') is True, 'InitramfsImageContractIncomplete')
        require(all(digest((stage/path).read_bytes()) == expected for path, expected in executed.items()),
                'FixtureExecutionRevisionChanged')
        record('acceptance-result.json', {'ExecutionSha256': digest(canonical(executed)),
            'CandidateObservationSha256': digest(canonical(readback)), 'PublicationPerformed': False})
        return readback
    finally:
        view.close()
        for resource in resources.values(): resource.close()


def observe(request):
    sys.path.insert(0, str(Path(request['Stage'])/'native'))
    import configured_root as root
    import configured_successor as successor
    import initramfs_candidate as candidate
    from package_broker import DirectoryLease
    from target_files import mount_id
    lease = DirectoryLease(request['Target']); info = os.fstat(lease.fd)
    view = root.RootView(lease.fd, info.st_dev, info.st_ino, mount_id(lease.fd))
    try:
        original = root.validate_manifest(Path(request['Manifest']).read_bytes())
        configured = successor.configured_manifest(view, original, request['PredecessorObservation'])
        image = Path(request['Candidate'])
        info = image.lstat()
        from isolation_policy import require
        import stat
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_uid == 0 and info.st_gid == 0 and
                stat.S_IMODE(info.st_mode) == 0o600 and 0 < info.st_size <= candidate.MAX_IMAGE, 'CandidateOutputMetadata')
        queries = image.parent.parent/'independent-image-observer'
        result = candidate.observe_image(image.read_bytes(), configured, request['Plan'],
            root.read_small(view, '/boot/config-' + candidate.RELEASE),
            root.read_small(view, '/usr/lib/modules/' + candidate.RELEASE + '/modules.dep'), view,
            lambda members: observe_generated(members, configured, view, request, queries))
        successor.configured_manifest(view, original, request['PredecessorObservation'])
        result['CompleteImageQualification'] = True
        result['ConfiguredRootUnchanged'] = True
        return result
    finally:
        view.close(); lease.close()


def observe_generated(members, manifest, view, request, directory):
    from initramfs_observer import observe_generated as shared
    return shared(members, manifest, view, request, directory)


if __name__ == '__main__':
    print(json.dumps(observe(json.loads(Path(sys.argv[1]).read_bytes())), sort_keys=True))
