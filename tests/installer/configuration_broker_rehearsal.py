"""Opt-in privileged fixtures in the isolated lab runtime, never its target disk.

The generic view cases use a test executable. retained_helpers uses the actual
authenticated helper/dependency tree and shared independent delta validator on
a separately copied ordinary directory root. Neither produces canonical storage
authority or installation evidence. Failed full-input roots are retained.
"""
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import uuid
import subprocess


def run(stage):
    stage = Path(stage)
    if os.geteuid() != 0: raise RuntimeError('IsolatedPrivilegedFixtureRequired')
    sys.path.insert(0, str(stage/'native'))
    from package_broker import DirectoryLease, PackageBroker, Runtime
    from isolation_policy import Launch, Profile, digest
    from isolation_observer import mount_records
    python = str(Path(sys.executable).resolve())
    runtime = Runtime(str(stage/'bwrap'), digest((stage/'bwrap').read_bytes()), str(stage/'command-gate'),
        digest((stage/'command-gate').read_bytes()), python, digest(Path(python).read_bytes()),
        str(stage/'native/isolation_observer.py'), digest((stage/'native/isolation_observer.py').read_bytes()))
    results = []
    with tempfile.TemporaryDirectory(prefix='igloo-configuration-view-') as temporary:
        base = Path(temporary); target = base/'root'; payload = base/'payload'
        for path in [target, payload]: path.mkdir(mode=0o755)
        for name in ('usr/bin', 'etc', 'boot', 'mnt', 'dev', 'proc', 'sys', 'run', 'tmp'):
            (target/name).mkdir(parents=True, exist_ok=True)
        # The probe uses only libc; copy the already protected lab runtime's exact
        # loader/libc, recording them rather than installing a package or networking.
        libraries = {}
        for name in ('/lib/x86_64-linux-gnu/libc.so.6', '/lib64/ld-linux-x86-64.so.2'):
            source = Path(name).resolve(strict=True)
            if source.stat().st_uid != 0 or source.stat().st_mode & 0o022:
                raise RuntimeError('FixtureRuntimeLibraryUnprotected')
            destination = target/name[1:]; destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination); destination.chmod(0o755)
            libraries[name] = digest(source.read_bytes())
        (target/'boot/fixture-kernel').write_bytes(b'unchanged fixture, not a kernel')
        shutil.copyfile(stage/'probe', target/'usr/bin/probe'); (target/'usr/bin/probe').chmod(0o755)
        resources = {name: DirectoryLease(str(path)) for name, path in [('root', target), ('esp', target/'mnt'), ('payload', payload)]}
        try:
            for name in ('success', 'esp', 'payload', 'network', 'write'):
                intents = []
                launch = Launch(str(uuid.uuid4()), 'A'*64, 'ConfigureIdentity', Profile.CONFIGURATION,
                    '/usr/bin/probe', digest((stage/'probe').read_bytes()), (name,), 60, 'None', 'igloo-lab-config', 'CoreConfiguration')
                result = PackageBroker(runtime).execute(launch, resources, lambda operation, isolation: intents.append((operation, isolation)))
                if result['State'] != 'Exited' or result['ExitCode'] != 0 or len(intents) != 1:
                    raise RuntimeError(json.dumps(result, sort_keys=True))
                if result['Observation']['Hostname'] != 'igloo-lab-config' or (target/'boot/efi').exists():
                    raise RuntimeError('PrivateViewWroteTargetScaffolding')
                if (target/'boot/fixture-kernel').read_bytes() != b'unchanged fixture, not a kernel':
                    raise RuntimeError('PrivateViewChangedBootContent')
                if any(m['Path'].startswith(str(base)+'/') for m in mount_records(Path('/proc/self/mountinfo').read_text())):
                    raise RuntimeError('FixtureMountRetained')
                results.append({'Case': name, 'IsolationSha256': result['IsolationSha256'], 'ToolSha256': result['ToolSha256'],
                    'SeccompSha256': result['SeccompSha256'], 'Observation': result['Observation'], 'NoTargetBootEfiCreated': True,
                    'RuntimeLibraries': libraries})
        finally:
            for resource in resources.values(): resource.close()
    return results


def retained_helpers(stage, source, manifest_path, destination, parameters):
    """One create-new full-input fixture; never a canonical target or receipt.

    The isolated lab supplies a read-only, noload checkpoint mount. Copying that
    tree preserves the retained helper/dependency bytes and all numeric metadata.
    Every observer is a fresh process using the production delta validator. Failed
    roots and records are retained; this function has no retry or cleanup path.
    """
    stage, source, destination = map(Path, (stage, source, destination))
    if os.geteuid() != 0 or destination.exists():
        raise RuntimeError('FreshPrivilegedFixtureRequired')
    sys.path.insert(0, str(stage/'native'))
    import session_configuration as config
    from isolation_policy import ENVIRONMENT, Launch, Profile, canonical, digest, require
    from package_broker import DirectoryLease, PackageBroker, Runtime, sealed_copy
    from target_files import TargetFiles, mount_id
    manifest_data = Path(manifest_path).read_bytes()
    require(digest(manifest_data) == '5BE0814960F7D87B3FB3379688D60873028921D4D8F0BF504DD4C0EB6AE3B9B2', 'FixtureManifestChanged')
    manifest = config.root.validate_manifest(manifest_data)
    require(os.statvfs(source).f_flag & os.ST_RDONLY, 'FixtureSourceMustBeReadOnly')
    destination.mkdir(mode=0o700)
    def record(name, value):
        data = canonical(value)
        with (destination/name).open('xb') as out:
            out.write(data); out.flush(); os.fsync(out.fileno())
        fd = os.open(destination, os.O_RDONLY | os.O_DIRECTORY)
        try: os.fsync(fd)
        finally: os.close(fd)
        require((destination/name).read_bytes() == data, 'FixtureRecordReopenChanged')
    record('intent.json', {'Scope': 'RetainedHelperContractFixture', 'Source': str(source),
        'ManifestSha256': digest(manifest_data), 'Policy': config.TRANSFORMATION_POLICY,
        'ExecutorSha256': digest(Path(config.__file__).read_bytes())})
    target = destination/'root'; payload = destination/'payload'; payload.mkdir(mode=0o755)
    subprocess.run(['/bin/cp', '--archive', '--reflink=auto', str(source), str(target)], check=True)
    require(target.stat().st_ino != source.stat().st_ino or target.stat().st_dev != source.stat().st_dev, 'FixtureCopyAliasesSource')
    resources = {name: DirectoryLease(str(path)) for name, path in [('root', target), ('esp', target/'mnt'), ('payload', payload)]}
    lease = resources['root']; info = os.fstat(lease.fd)
    view = config.root.RootView(lease.fd, info.st_dev, info.st_ino, mount_id(lease.fd))
    python = str(Path(sys.executable).resolve())
    runtime = Runtime(str(stage/'bwrap'), digest((stage/'bwrap').read_bytes()), str(stage/'command-gate'),
        digest((stage/'command-gate').read_bytes()), python, digest(Path(python).read_bytes()),
        str(stage/'native/isolation_observer.py'), digest((stage/'native/isolation_observer.py').read_bytes()))
    plan = {'SchemaVersion': 1, 'TransformationPolicy': config.TRANSFORMATION_POLICY,
        'Provenance': {'Scope': 'CoreConfiguration'}, 'Hostname': 'igloo-lab-config', 'Username': 'iglootest',
        'UserId': 1000, 'Locale': 'en_US.UTF-8', 'Timezone': 'Etc/UTC', 'Keyboard': 'us', 'OperationId': str(uuid.uuid4())}
    plan_hash = digest(canonical(plan)); reference = None
    baseline = {'Exim': config.root.read_small(view, '/etc/exim4/update-exim4.conf.conf').decode(),
        'UserDefaults': config.root.read_small(view, '/etc/default/useradd').decode(),
        'Debconf': config.root.read_small(view, '/var/cache/debconf/config.dat').decode(),
        'AccountMetadata': {}, **{p: config.root.read_small(view, p).decode() for p in config.ACCOUNT_FILES}}
    with TargetFiles(view.fd, view.expected[0], view.expected[2]) as files:
        for path in config.ACCOUNT_FILES:
            observed = files.observe(path)
            baseline['AccountMetadata'][path] = [observed['Owner'], observed['Group'], observed['Mode']]
    def observe(step):
        # Sensitive baseline account bytes exist only in this private pipe/memory.
        request = {'RootFd': view.fd, 'ExpectedRoot': list(view.expected), 'ManifestPath': str(manifest_path),
            'Plan': plan, 'Parameters': parameters, 'Step': step, 'Baseline': baseline, 'CredentialReference': reference}
        child = subprocess.run([python, '-I', '-B', str(stage/'lab/configuration_broker_rehearsal.py'),
            '--observe-retained-helper', str(stage)], input=canonical(request), pass_fds=(view.fd,),
            capture_output=True, env=ENVIRONMENT, timeout=7200, check=False)
        require(child.returncode == 0 and len(child.stdout) < 65536, 'FixtureIndependentObserverUnavailable')
        result = json.loads(child.stdout)
        require(result.get('State') == 'Verified', 'FixtureObserverRejected:' + result.get('Code', 'Unavailable'))
        return result
    tools = {e['Path']: e for e in manifest['Entries']}
    commands = config.helper_commands(plan); broker = PackageBroker(runtime); active = 'Baseline'; results = []
    try:
        result = observe('Baseline'); record('00-Baseline.json', result)
        config.account_before(baseline, plan); config.preflight_files(view, manifest, plan, parameters, baseline)
        for step in config.STEPS[1:]:
            active = step; command = None
            def intent(operation=None, isolation=None):
                record(step+'-intent.json', {'Step': step, 'PlanSha256': plan_hash,
                    'OperationSha256': operation, 'IsolationSha256': isolation})
                view.check()
            if step == 'Files':
                intent()
                with TargetFiles(view.fd, view.expected[0], view.expected[2]) as files:
                    for path, (kind, value, mode) in config.files(plan, parameters, baseline['Exim'], baseline['UserDefaults']).items():
                        files.apply(path, kind, value, mode, config.before_state(tools.get(path)))
            else:
                if step == 'Verify':
                    stage_name, executable, args, input_kind = 'InspectArtifacts', '/usr/sbin/visudo', ('--check', '--strict', '--quiet'), 'None'
                else: stage_name, executable, args, input_kind = commands[step]
                input_fd = None
                if step == 'Debconf':
                    input_fd = sealed_copy(('SET exim4/mailname '+plan['Hostname']+'\nSET exim4/dc_other_hostnames '+
                        plan['Hostname']+'\nFSET exim4/dc_other_hostnames mailname true\n').encode(), 'igloo-fixture-debconf')
                if step == 'Credential': input_fd, reference = config.credential_input(plan['Username'])
                launch = Launch(plan['OperationId'], plan_hash, stage_name,
                    Profile.OBSERVER if step == 'Verify' else Profile.CONFIGURATION, executable,
                    tools[executable]['Sha256'], args, 600, input_kind, plan['Hostname'], 'CoreConfiguration')
                try: command = broker.execute(launch, resources, intent, input_fd=input_fd)
                finally:
                    if input_fd is not None: os.close(input_fd)
                record(step+'-helper.json', command)
                require(command['State'] == 'Exited' and command['ExitCode'] == 0, 'FixtureHelperFailed:'+step)
            require(config.ctypes.CDLL(None, use_errno=True).syncfs(view.fd) == 0, 'FixtureFlushFailed')
            observed = observe(step)
            record(step+'-result.json', observed); results.append(step)
            print('HELPER_CONTRACT_VERIFIED', step, flush=True)
        record('result.json', {'State': 'Verified', 'Scope': 'RetainedHelperContractFixture', 'Steps': results,
            'Policy': config.TRANSFORMATION_POLICY, 'CanonicalQualification': False})
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        record('failure.json', {'State': 'Failed', 'Step': active, 'Code': str(error), 'VerifiedSteps': results,
            'CanonicalQualification': False})
        raise
    finally:
        view.close()
        for resource in resources.values(): resource.close()


def observe_retained_helper(stage):
    sys.path.insert(0, str(Path(stage)/'native'))
    import session_configuration as config
    from isolation_policy import canonical, digest
    request = json.load(sys.stdin)
    data = Path(request['ManifestPath']).read_bytes()
    if digest(data) != '5BE0814960F7D87B3FB3379688D60873028921D4D8F0BF504DD4C0EB6AE3B9B2':
        raise ValueError('FixtureManifestChanged')
    manifest = config.root.validate_manifest(data)
    view = config.root.RootView(request['RootFd'], *request['ExpectedRoot'])
    try:
        if request['Step'] == 'Baseline':
            result = {'FilesystemSha256': config.root.verify_tree(view, manifest),
                'PackageStateSha256': config.root.verify_dpkg(view, manifest), 'NeutralSha256': config.root.verify_neutral(view, manifest)}
        else:
            result = config.verify_delta(view, manifest, request['Plan'], request['Parameters'], request['Step'],
                request['Baseline'], request['CredentialReference'])
        print(canonical({'State': 'Verified', **result}).decode())
    except (OSError, ValueError, KeyError) as error:
        # Validators emit fixed diagnostic codes, never account/key contents.
        print(canonical({'State': 'Rejected', 'Code': str(error)}).decode())
    finally: view.close()


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--observe-retained-helper':
        observe_retained_helper(sys.argv[2])
    else: raise SystemExit('This module is invoked only by the isolated qualification harness.')
