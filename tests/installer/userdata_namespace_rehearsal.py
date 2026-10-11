"""Opt-in isolated-VM journal join fixture; never a canonical capability.

The host must nominate a new 64-MiB blank file-backed disk with the exact fixture
serial. No retained target is attached. The only formatted object is this new
fixture disk. Uses the real command gate, protected loader and mount observer.
"""
import copy
import json
import os
from pathlib import Path
import subprocess
import sys


def child(request):
    stage = Path(request['Stage'])
    sys.path.insert(0, str(stage/'native'))
    import session_entry
    session_entry.load_verified_modules(request['Declaration'])
    from isolation_policy import canonical, require
    from isolation_observer import namespace_ids
    from mount_supervisor import ExactMountSupervisor
    from package_broker import DirectoryLease, Runtime
    from session_userdata import verify_store_mount
    from userdata_producer import identity
    runtime = Runtime(**request['Runtime'])
    supervisor = ExactMountSupervisor(request['Generation'], request['PlanSha256'],
        request['ParentNamespace'], runtime, lambda: None, lambda value: None)
    witness = request['Witness']; path = witness['Path']
    lease = DirectoryLease(path)
    try:
        require(os.fstat(lease.fd).st_uid == 0 and os.fstat(lease.fd).st_mode & 0o777 == 0o700,
                'FixtureStoreProtection')
        observed = supervisor._observe(path); current = identity(lease.fd)
        verify_store_mount(observed, supervisor.namespace, witness, current)
        lease.verify()
        failures = []
        def rejected(name, observation, binding=witness):
            try: verify_store_mount(observation, supervisor.namespace, binding, current)
            except ValueError as error: failures.append({'Case': name, 'Code': str(error)})
            else: raise ValueError('FixtureRejectionMissing:'+name)
        rejected('WrongDirectory', observed, {**witness, 'Inode': witness['Inode']+1})
        rejected('WrongNamespace', {**observed, 'Namespace': request['ParentNamespace']})
        selected = next(m for m in observed['Mounts'] if m['Id'] == current[2])
        for name, change in (
            ('ChangedMountId', {'Id': current[2]+100000}),
            ('WrongMountRoot', {'Root': '/alias'}),
            ('MissingNoexec', {'Options': [v for v in selected['Options'] if v != 'noexec']})):
            altered = copy.deepcopy(observed)
            next(m for m in altered['Mounts'] if m['Id'] == current[2]).update(change)
            rejected(name, altered)
        altered = copy.deepcopy(observed)
        altered['Mounts'].append({**selected, 'Id': current[2]+100000, 'Device': '0:9999'})
        rejected('StackedDifferentDevice', altered)
        # A real additional bind mount in this fixture-only private namespace.
        alias = Path(request['Evidence'])/'alias'; alias.mkdir(mode=0o700)
        subprocess.run(['/usr/bin/mount', '--bind', '/lab-journal', str(alias)], check=True)
        try: rejected('NativeAliasMount', supervisor._observe(path))
        finally: subprocess.run(['/usr/bin/umount', str(alias)], check=True)
        verify_store_mount(supervisor._observe(path), supervisor.namespace, witness, current)
        lease.verify()
        before = supervisor._observe('/lab-journal')
    finally: lease.close()
    parent = os.open('/', os.O_DIRECTORY | os.O_RDONLY)
    try: supervisor.calls.unmount(parent, 'lab-journal')
    finally: os.close(parent)
    after = supervisor._observe('/lab-journal')
    require(after['Mounts'] == [m for m in before['Mounts'] if m['Id'] != current[2]] and
        not any(m['Path'] == '/lab-journal' for m in after['Mounts']), 'FixtureChildUnmountDelta')
    print(canonical({'Scope': 'NativeJournalJoinFixtureOnly', 'Qualified': True,
        'ParentNamespace': request['ParentNamespace'], 'ChildNamespace': namespace_ids('self')['mnt'],
        'ParentWitness': witness, 'ChildIdentity': current, 'Observation': observed,
        'Rejections': failures, 'OrdinaryChildUnmount': True, 'ChildMountDeltaVerified': True,
        'CanonicalAuthority': False}).decode(), flush=True)


def run(stage, evidence):
    stage, evidence = Path(stage), Path(evidence)
    sys.path[:0] = [str(stage/'native'), str(stage/'lab')]
    from canonical_storage_guest import save, protect_runtime
    from isolation_policy import canonical, digest, require
    from isolation_observer import namespace_ids, mount_records
    from session_import import journal_store_observation
    require(os.geteuid() == 0 and not evidence.exists() and not Path('/sys/firmware/efi').exists() and
        sorted(p.name for p in Path('/sys/class/net').iterdir()) == ['lo'], 'IsolatedFixtureRequired')
    evidence.mkdir(mode=0o700)
    save(evidence, 'execution.json', protect_runtime(stage))
    require(Path('/sys/class/block/vdb/serial').read_text().strip() == 'IGLOO-UD-JOURNAL' and
        int(Path('/sys/class/block/vdb/size').read_text())*512 == 64*1024**2, 'FixtureDiskBinding')
    mount = Path('/lab-journal')
    block = os.stat('/dev/vdb').st_rdev
    device = f'{os.major(block)}:{os.minor(block)}'
    require(mount.is_dir() and not mount.is_symlink() and not list(mount.iterdir()) and
        not any(m['Path'] == str(mount) or m['Device'] == device for m in mount_records(Path('/proc/self/mountinfo').read_text())),
        'FixtureMountBeforeState')
    with open('/dev/vdb', 'rb') as source:
        require(not any(source.read(1024*1024)), 'FixtureDiskNotBlank')
    save(evidence, 'intent.json', {'Scope': 'NativeJournalJoinFixtureOnly', 'Disk': '/dev/vdb',
        'Serial': 'IGLOO-UD-JOURNAL', 'Length': 64*1024**2, 'CanonicalAuthority': False})
    subprocess.run(['/usr/sbin/mkfs.ext4', '-q', '-E', 'lazy_itable_init=0,lazy_journal_init=0', '/dev/vdb'], check=True)
    before = mount_records(Path('/proc/self/mountinfo').read_text())
    subprocess.run(['/usr/bin/mount', '-t', 'ext4', '-o', 'nodev,nosuid,noexec', '/dev/vdb', str(mount)], check=True)
    folder = mount/'userdata-fixture'; folder.mkdir(mode=0o700)
    for name in ('session', 'effects'): (folder/name).mkdir(mode=0o700)
    witness = journal_store_observation([str(folder/'session'), str(folder/'effects')])[1]
    require(witness['Device'] != os.stat('/').st_dev, 'FixtureRuntimeJournalSeparation')
    save(evidence, 'parent-witness.json', witness)
    python = str(Path('/usr/bin/python3').resolve())
    runtime = {'bubblewrap': str(stage/'bwrap'), 'gate': str(stage/'command-gate'), 'python': python,
        'observer': str(stage/'native/isolation_observer.py')}
    runtime.update({k+'_hash': digest(Path(v).read_bytes()) for k, v in list(runtime.items())})
    request = {'Stage': str(stage), 'Evidence': str(evidence), 'Witness': witness, 'Runtime': runtime,
        'Generation': '0e0c3830-47ef-4b94-920b-d8af1d270c65', 'PlanSha256': digest(canonical(witness)),
        'ParentNamespace': namespace_ids('self')['mnt'],
        'Declaration': {'LabUserData': {'FixtureOnly': True},
            'ToolHashes': {str(p): digest(p.read_bytes()) for p in (stage/'native').glob('*.py')}}}
    result = subprocess.run([runtime['gate'], '--supervise', python, '-I', '-B',
        str(stage/'lab/userdata_namespace_rehearsal.py'), '--child'], input=canonical(request)+b'\n',
        capture_output=True, timeout=180, check=False)
    save(evidence, 'child-exit.json', {'ExitCode': result.returncode, 'StderrSha256': digest(result.stderr)})
    save(evidence, 'child.stdout', result.stdout); save(evidence, 'child.stderr', result.stderr)
    require(result.returncode == 0 and len(result.stdout) < 256*1024, 'FixtureChildUnavailable')
    observed = json.loads(result.stdout); require(observed['Qualified'], 'FixtureChildNotQualified')
    require(journal_store_observation([str(folder/'session'), str(folder/'effects')])[1] == witness,
        'FixtureParentMountChanged')
    subprocess.run(['/usr/bin/umount', str(mount)], check=True)
    require(mount_records(Path('/proc/self/mountinfo').read_text()) == before, 'FixtureParentUnmountDelta')
    save(evidence, 'result.json', {**observed, 'ParentOrdinaryUnmount': True, 'ParentMountDeltaVerified': True})
    print('NATIVE_JOURNAL_FIXTURE_PASSED', flush=True)


if __name__ == '__main__':
    if len(sys.argv) == 2 and sys.argv[1] == '--child': child(json.loads(sys.stdin.buffer.readline(1024*1024)))
    else: raise ValueError('ExplicitFixtureCompositionRequired')
