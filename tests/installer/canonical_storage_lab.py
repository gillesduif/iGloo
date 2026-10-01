"""Explicit storage-smoke host controller. New files only; no artifact delivery.

The trusted exclusive host checks powered-off ancestry and every QEMU backing FD.
Configured serials correlate this launch with guest observations, not Windows IDs.
"""
import hashlib
import json
import os
from pathlib import Path
import pwd
import shutil
import stat
import subprocess
import uuid

from canonical_lab_runtime import checkpoint
from canonical_lab_capacity import require_backing_capacity


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def create(previous, repo, publish, delivery=None, *, configuration_checkpoint=None, configuration_pin=None,
           derived_authorization=None, failed_configuration=None, initramfs_authorization=None, configured_predecessor=None,
           userdata_authorization=None, initramfs_predecessor=None, userdata_inputs=None):
    previous, repo, publish = map(Path, (previous, repo, publish))
    require_backing_capacity(74 * 1024**3)  # Runtime + root + journal + explicit reserve.
    source = previous / 'runtime.raw'
    if not stat.S_ISREG(source.lstat().st_mode) or source.resolve() != source or source.stat().st_size != 24 * 1024**3:
        raise ValueError('RuntimeBackingInvalid')
    for process in Path('/proc').iterdir():
        if not process.name.isdigit():
            continue
        try:
            for fd in (process / 'fd').iterdir():
                if fd.stat().st_ino == source.stat().st_ino and fd.stat().st_dev == source.stat().st_dev:
                    raise ValueError('RuntimeBackingStillOpen')
        except (FileNotFoundError, ProcessLookupError):
            pass
    if initramfs_authorization is not None and (derived_authorization is not None or configuration_checkpoint is not None or configured_predecessor is None):
        raise ValueError('MixedSuccessorProvisioning')
    if userdata_authorization is not None and (initramfs_authorization is not None or derived_authorization is not None or
            configuration_checkpoint is not None or initramfs_predecessor is None or userdata_inputs is None):
        raise ValueError('MixedUserDataProvisioning')
    authorization_path = userdata_authorization if userdata_authorization is not None else (initramfs_authorization if initramfs_authorization is not None else derived_authorization)
    authorization = json.loads(Path(authorization_path).read_bytes()) if authorization_path is not None else None
    run = previous.parent / (uuid.UUID(authorization['AttemptId']).hex if authorization is not None else uuid.uuid4().hex)
    run.mkdir(mode=0o755)
    before = {'Path': str(source), 'Device': source.stat().st_dev, 'Inode': source.stat().st_ino,
              'Length': source.stat().st_size, 'Sha256': sha(source)}
    checkpoint(run, 'derivation-intent.json', {'RunId': str(uuid.UUID(run.name)), 'Source': before, 'NewDisks': {'runtime.raw': 24 * 1024**3, **({'target.raw': 40 * 1024**3, 'journal.raw': 8 * 1024**3} if configuration_checkpoint is None else {})}, 'ContinuingTargetAndJournal': configuration_checkpoint is not None})
    if userdata_authorization is not None:
        # Apply the unchanged full-copy reserve BEFORE allocating any disk copy.
        # The successor owns its external reservation and target/journal copies.
        from canonical_userdata_lab import derive
        derive(userdata_authorization, initramfs_predecessor, run)
    subprocess.run(['cp', '--sparse=always', '--reflink=auto', '--no-clobber', str(source), str(run / 'runtime.raw')], check=True)
    if sha(run / 'runtime.raw') != before['Sha256'] or sha(source) != before['Sha256']:
        raise ValueError('RuntimeDerivationChanged')
    if initramfs_authorization is not None:
        from canonical_initramfs_lab import derive
        derive(initramfs_authorization, configured_predecessor, run)
    elif userdata_authorization is not None:
        pass  # Independently verified copies already created above.
    elif configuration_checkpoint is None:
        for name, size in [('target.raw', 40), ('journal.raw', 8)]:
            with (run / name).open('xb') as out:
                out.truncate(size * 1024**3); out.flush(); os.fsync(out.fileno())
    elif authorization is not None:
        from canonical_configuration_derivation import derive
        derive(derived_authorization, configuration_checkpoint, failed_configuration, run)
        checkpoint(run, 'configuration-checkpoint.json', {'CheckpointDirectory': str(configuration_checkpoint),
            'CheckpointSha256': sha(Path(configuration_checkpoint)/'result.json'), 'TargetContinuation': 'IndependentCheckpointCopy',
            'Predecessor': str(previous)})
    else:
        checkpoint_path = Path(configuration_checkpoint)
        result = json.loads((checkpoint_path / 'result.json').read_bytes())
        intent = json.loads((checkpoint_path / 'intent.json').read_bytes())
        if result['Outcome'] != 'AppliedAndVerified' or result['IntentSha256'] != sha(checkpoint_path/'intent.json') or intent['Predecessor'] != str(previous):
            raise ValueError('ConfigurationCheckpointChanged')
        for name in ('target.raw', 'journal.raw'):
            if sha(previous/name) != result['Backings'][name]['Sha256'] or sha(checkpoint_path/name) != result['Backings'][name]['Sha256']:
                raise ValueError('ConfigurationCheckpointBytesChanged')
            os.link(previous/name, run/name)  # Same nominated inode, NOT fresh creation/format evidence.
        checkpoint(run, 'configuration-checkpoint.json', {'CheckpointDirectory': str(checkpoint_path),
            'CheckpointSha256': sha(checkpoint_path/'result.json'), 'TargetContinuation': 'SameBacking', 'Predecessor': str(previous)})
    inputs = run / 'input'; inputs.mkdir()
    shutil.copytree(publish, inputs / 'harness')
    shutil.copytree(repo / 'distros/debian/native', inputs / 'native', ignore=shutil.ignore_patterns('__pycache__'))
    shutil.copyfile(repo / 'distros/_shared/installer/collect_inventory.py', inputs / 'collector.py')
    subprocess.run(['/usr/bin/gcc', '-static', '-O2', '-Wall', '-Wextra', '-Werror', '-o', str(inputs / 'command-gate'),
                    str(repo / 'distros/debian/native/command_gate.c')], check=True)
    # Reuse the predecessor's retained authenticated tool, not volatile /tmp
    # package-acquisition scratch which may disappear when WSL restarts.
    bwrap = previous / 'input/bwrap'
    if sha(bwrap) != '573236E5328AC2EBB08F59AE3A9805B4F8D12BDEF14BE8AF4450D5463294985F':
        raise ValueError('PinnedBubblewrapChanged')
    shutil.copyfile(bwrap, inputs / 'bwrap')
    scripts = inputs / 'lab'; scripts.mkdir()
    for name in ('canonical_storage_guest.py', 'canonical_import_guest.py', 'canonical_configuration_guest.py'):
        shutil.copyfile(repo / 'tests/installer' / name, scripts / name)
    if initramfs_authorization is not None:
        from canonical_initramfs_lab import stage_predecessor
        stage_predecessor(configured_predecessor, inputs/'configured-predecessor')
        shutil.copyfile(initramfs_authorization, inputs/'initramfs-authorization.json')
        shutil.copyfile(run/'initramfs-derivation.json', inputs/'initramfs-derivation.json')
        shutil.copyfile(repo/'tests/installer/canonical_initramfs_guest.py', scripts/'canonical_initramfs_guest.py')
        pin = Path(configuration_pin)
        for path in (pin, *pin.parents):
            if path.is_symlink() or path.stat().st_uid != 0 or path.stat().st_mode & 0o022:
                raise ValueError('ExternalInitramfsPinUnprotected')
        shutil.copyfile(pin, inputs/'external-pin.json')
    if userdata_authorization is not None:
        from canonical_userdata_lab import stage_predecessor
        stage_predecessor(initramfs_predecessor, inputs/'initramfs-predecessor')
        shutil.copyfile(userdata_authorization, inputs/'userdata-authorization.json')
        shutil.copyfile(run/'userdata-initial-copy.json', inputs/'userdata-initial-copy.json')
        for name in ('userdata-plan.json', 'synthetic-documents.json', 'payload-staging-binding.json'):
            shutil.copyfile(Path(userdata_inputs)/name, inputs/name)
        for name in ('canonical_userdata_guest.py', 'test_userdata_admission.py', 'test_userdata_session.py',
                     'canonical_userdata_readback.py', 'canonical_configuration_readback.py'):
            shutil.copyfile(repo/'tests/installer'/name, scripts/name)
        shutil.copytree(Path(userdata_inputs)/'wire', inputs/'wire')
        pin = Path(configuration_pin)
        for path in (pin, *pin.parents):
            if path.is_symlink() or path.stat().st_uid != 0 or path.stat().st_mode & 0o022:
                raise ValueError('ExternalUserDataPinUnprotected')
        shutil.copyfile(pin, inputs/'external-pin.json')
    if configuration_checkpoint is not None:
        pin = Path(configuration_pin)
        if not stat.S_ISREG(pin.lstat().st_mode) or pin.stat().st_uid != 0 or pin.stat().st_mode & 0o022:
            raise ValueError('ExternalConfigurationPinUnprotected')
        for parent in pin.parents:
            if parent.is_symlink() or parent.stat().st_uid != 0 or parent.stat().st_mode & 0o022:
                raise ValueError('ExternalConfigurationPinParentUnprotected')
        shutil.copyfile(pin, inputs / 'external-pin.json')
        predecessor = inputs / 'predecessor'; predecessor.mkdir()
        shutil.copyfile(previous/'guest-evidence/provisioning/storage.json', predecessor/'storage.json')
        for name in ('session', 'import'):
            shutil.copytree(previous/'guest-evidence/journals'/name, predecessor/name)
        shutil.copyfile(run/'configuration-checkpoint.json', predecessor/'checkpoint.json')
        if authorization is not None:
            shutil.copyfile(derived_authorization, inputs/'derived-authorization.json')
            shutil.copyfile(run/'configuration-derivation.json', inputs/'configuration-derivation.json')
    if delivery is not None:
        shutil.copytree(Path(delivery), inputs / 'delivery')
    for name in ['kernel', 'initrd']:
        shutil.copyfile(previous / name, run / name)
    for path in inputs.rglob('*'):
        path.chmod(0o755 if path.is_dir() or path.name in ('command-gate', 'bwrap') else 0o644)
    checkpoint(run, 'input-identities.json', {str(p.relative_to(inputs)): sha(p) for p in sorted(inputs.rglob('*')) if p.is_file()})
    subprocess.run(['xorriso', '-as', 'mkisofs', '-R', '-V', 'IGLOO_STORAGE_TOOLS', '-o', str(run / 'tools.iso'), str(inputs)],
                   check=True, capture_output=True)
    identity = pwd.getpwnam('gillesduif')
    for path in [run, *(run / n for n in ('runtime.raw', 'target.raw', 'journal.raw'))]:
        os.chown(path, identity.pw_uid, identity.pw_gid)
    disks = {name: {'Device': (run / name).stat().st_dev, 'Inode': (run / name).stat().st_ino,
                    'Length': (run / name).stat().st_size} for name in ('runtime.raw', 'target.raw', 'journal.raw')}
    args = ['/usr/bin/qemu-system-x86_64', '-no-user-config', '-nodefaults', '-machine', 'q35,accel=kvm',
        '-cpu', 'host', '-smp', '4', '-m', '8192', '-display', 'none', '-nic', 'none', '-monitor', 'none', '-no-reboot',
        '-sandbox', 'on,obsolete=deny,elevateprivileges=allow,spawn=deny,resourcecontrol=deny', '-runas', identity.pw_name,
        '-serial', f'unix:{run}/console.sock,server=on,wait=off', '-kernel', str(run / 'kernel'), '-initrd', str(run / 'initrd'),
        '-append', 'root=PARTUUID=86480d67-661c-4a36-8648-9659bce14063 ro console=ttyS0,115200n8 noresume']
    for name, serial in [('runtime', 'IGLOO-GPT-RUNTIME'), ('target', 'IGLOO-LAB-TARGET'), ('journal', 'IGLOO-LAB-JOURNAL')]:
        args += ['-drive', f'file={run}/{name}.raw,if=none,id={name},format=raw,cache=none',
                 '-device', f'virtio-blk-pci,drive={name},serial={serial}']
    args += ['-drive', f'file={run}/tools.iso,media=cdrom,if=ide,format=raw,readonly=on']
    checkpoint(run, 'launch-intent.json', {'RunId': str(uuid.UUID(run.name)), 'Arguments': args, 'Disks': disks,
        'ToolsSha256': sha(run / 'tools.iso'), 'Source': before})
    with (run / 'qemu.stderr').open('xb') as error:
        process = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=error, start_new_session=True)
    checkpoint(run, 'process.json', {'Pid': process.pid})
    return run


def observe(run, phase=None):
    run = Path(run)
    if phase not in (None, 'userdata-canonical'): raise ValueError('UnknownLaunchPhase')
    prefix = '' if phase is None else phase + '-'
    declared = json.loads((run / (prefix + 'launch-intent.json')).read_bytes())
    pid = json.loads((run / (prefix + 'process.json')).read_bytes())['Pid']
    if Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')[:-1] != [s.encode() for s in declared['Arguments']]:
        raise ValueError('LaunchChanged')
    expected = {str(run / name): 2 for name in declared['Disks']}
    expected[str(run / 'tools.iso')] = 0
    found = {}
    for fd in Path(f'/proc/{pid}/fd').iterdir():
        info = fd.stat(); path = os.readlink(fd)
        if stat.S_ISBLK(info.st_mode): raise ValueError('PhysicalBlockPassthrough')
        if not stat.S_ISREG(info.st_mode) or path == str(run / (prefix + 'qemu.stderr')): continue
        mode = int(next(s.split()[1] for s in Path(f'/proc/{pid}/fdinfo/{fd.name}').read_text().splitlines() if s.startswith('flags:')), 8) & 3
        if path not in expected or expected[path] != mode or path in found: raise ValueError('BackingFdChanged')
        found[path] = {'Device': info.st_dev, 'Inode': info.st_ino, 'Length': info.st_size}
    if set(found) != set(expected): raise ValueError('BackingFdMissing')
    for name, identity in declared['Disks'].items():
        if found[str(run / name)] != identity: raise ValueError('BackingAncestryChanged')
    checkpoint(run, prefix + 'host-readback.json', {'Pid': pid, 'LaunchSha256': sha(run / (prefix + 'launch-intent.json')), 'Files': found})
    bindings = []
    for name, serial in [('runtime', 'IGLOO-GPT-RUNTIME'), ('target', 'IGLOO-LAB-TARGET'), ('journal', 'IGLOO-LAB-JOURNAL')]:
        f = found[str(run / (name + '.raw'))]
        bindings.append({'Serial': serial, 'Length': f['Length'], 'HostDevice': f['Device'], 'HostInode': f['Inode'],
            'CreationSha256': sha(run / 'derivation-intent.json'), 'LaunchSha256': sha(run / (prefix + 'launch-intent.json'))})
    checkpoint(run, prefix + 'host-binding.json', {'RunId': declared['RunId'], 'HostObservationSha256': sha(run / (prefix + 'host-readback.json')),
        'CreatedBackings': bindings, 'ReopenedBackings': bindings})
    return run / (prefix + 'host-binding.json')
