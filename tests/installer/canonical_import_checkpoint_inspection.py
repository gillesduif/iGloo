"""One nominated, read-only import-checkpoint observation; never a deployment.

Run inside a fresh private mount namespace from a root-protected tool stage.
The caller supplies only a create-new protected observation directory.
"""

import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent / 'native')]

from canonical_lab_runtime import checkpoint
from canonical_import_readback import verify as verify_journals
from checkpoint_readonly_inspection import MountRejected, observe, require_mount
from factory_artifact_verifier import authenticate
import configured_root as root

BASE = Path('/var/lib/igloo/config-checkpoints/import-daa72a8f-20260929')
EXPORT = Path('/var/lib/igloo/canonical-labs/daa72a8f914a457492ef4d39ffb0c1e1/guest-evidence')
DELIVERY = Path('/var/lib/igloo/import-delivery-20260929')
CHECKPOINT = '518AF140A5D7241948FE4C1C5B054BB35E732349524FAA74297863ED7EDA3486'
IMPORT = 'A18E09649CD6E97EE8257EBF46F74CD0FFBF3A19E6EB49EDEF358133B2CEDDE9'
CLOSE = 'F102D92819EC0D42C6DEA88C475583E2707C2B65214BA610B2FFC1AF6164817A'
PIN = '0029CB409F537807E91B1DF39AE2B2A0491AA4487294CDBA77674C207A022D8F'
STDOUT = 'BC2B49D645E3DD548BE7EC8CD05A396809D305730CB27BD5679ED1DD3842D522'
EXPECTED = {
    'target': {'Device': 2096, 'Inode': 77587, 'Length': 42949672960,
               'MtimeSeconds': 1790659264, 'Offset': 9933160448, 'PartitionLength': 21474836480,
               'Sha256': '1C63ADEECBAE295E87766D02503F7D7704BE28036832D64FA3FA66738549AE4C'},
    'journal': {'Device': 2096, 'Inode': 77592, 'Length': 8589934592,
                'MtimeSeconds': 1790659338, 'Offset': 1048576, 'PartitionLength': 8587837440,
                'Sha256': '4B501D7FDA872A3E785F077548D40B38FD524B33DBD3062DC172E1A1B57906BB'},
}
EXPECTED_SEMANTICS = {
    'FilesystemSha256': '1F842DC9D53C5C57B2CD9D77FE5731C29E777C4599C445CC8A3892222687BF13',
    'PackageStateSha256': 'FE0F441041764FEF253382BCD6BD93933CF4EF63E8F086B10D93363B2B44719B',
    'NeutralStateSha256': '751CDEE71BF90671367B5B95F8FB17C660A5595E69F8AD6658EA600288FDD358',
}


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def protected(path, *, file=False):
    path = Path(path)
    for current in (path, *path.parents):
        info = current.lstat()
        if info.st_uid != 0 or info.st_mode & 0o022 or stat.S_ISLNK(info.st_mode):
            raise ValueError('ProtectedPathChanged')
    info = path.lstat()
    if file and (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1):
        raise ValueError('ProtectedFileTypeChanged')


def identity(path):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_gid != 0 or \
            stat.S_IMODE(info.st_mode) != 0o400 or info.st_nlink != 1:
        raise ValueError('CheckpointBackingProtectionChanged')
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)


def no_writer(paths):
    identities = {(p.lstat().st_dev, p.lstat().st_ino) for p in paths}
    for process in Path('/proc').iterdir():
        if not process.name.isdigit():
            continue
        try:
            for descriptor in (process/'fd').iterdir():
                info = descriptor.stat()
                if (info.st_dev, info.st_ino) in identities:
                    raise ValueError('CheckpointBackingStillOpen')
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue
    for path in paths:
        if subprocess.check_output(['losetup', '-j', str(path)]).strip():
            raise ValueError('CheckpointLoopStillAttached')


def preflight():
    protected(BASE)
    intent = BASE/'intent.json'; result_path = BASE/'result.json'
    protected(intent, file=True); protected(result_path, file=True)
    if sha(intent.read_bytes()) != '44E9F5C6F6F27AD7B1C82A72B0E48A624F372869392435BC63560F0EAC8D01D1' or \
            sha(result_path.read_bytes()) != CHECKPOINT:
        raise ValueError('CheckpointRecordChanged')
    result = json.loads(result_path.read_bytes())
    if result['Outcome'] != 'AppliedAndVerified' or result['IntentSha256'] != sha(intent.read_bytes()) or \
            result['ImportResult']['Sha256'] != IMPORT or result['CloseResult']['Sha256'] != CLOSE:
        raise ValueError('CheckpointLineageChanged')
    if sha((EXPORT/'provisioning/canonical-import.stdout').read_bytes()) != STDOUT:
        raise ValueError('NominatedImportTranscriptChanged')
    images = [BASE/'journal.raw', BASE/'target.raw']
    current = {}
    for role in ('journal', 'target'):
        path = BASE/(role+'.raw'); protected(path, file=True)
        info = identity(path); expected = EXPECTED[role]
        if info[:3] != (expected['Device'], expected['Inode'], expected['Length']) or \
                info[3] // 1_000_000_000 != expected['MtimeSeconds'] or \
                result['Backings'][role+'.raw']['Sha256'] != expected['Sha256']:
            raise ValueError('CheckpointBackingChangedSinceFullHash')
        current[role] = list(info)
    no_writer(images)
    protected(DELIVERY/'external-pin.json', file=True)
    pin_bytes = (DELIVERY/'external-pin.json').read_bytes()
    if sha(pin_bytes) != PIN:
        raise ValueError('DevelopmentPinChanged')
    pin = json.loads(pin_bytes, object_pairs_hook=root.unique_pairs)
    now = dt.datetime.now(dt.timezone.utc)
    descriptor = (DELIVERY/'artifact/descriptor.json').read_bytes()
    artifact = authenticate(descriptor, pin, now)
    manifest_bytes = (DELIVERY/'artifact/root.manifest.json').read_bytes()
    if sha(manifest_bytes) != pin['ManifestSha256'] or len(root.validate_manifest(manifest_bytes)['Entries']) != 144911:
        raise ValueError('AuthenticatedManifestChanged')
    return {'Images': current, 'PinSha256': sha(pin_bytes), 'PinNotAfterUtc': pin['NotAfterUtc'],
            'DescriptorSha256': sha(descriptor), 'ManifestSha256': sha(manifest_bytes),
            'BuildId': artifact['BuildId'], 'AtUtc': now.isoformat()}, root.validate_manifest(manifest_bytes)


def compact(observation):
    selected = [m for m in observation['Mounts'] if m['Path'] == observation.get('RequestedPath')]
    return {k: v for k, v in observation.items() if k != 'Mounts'} | {
        'SelectedMounts': selected, 'TotalMounts': len(observation['Mounts'])}


def run(store):
    store = Path(store)
    protected(store)
    stage = 'Preflight'; mounted = []; space = None; failure = None; result = None
    before = None
    try:
        before, manifest = preflight()
        checkpoint(store, 'preflight.json', before)
        space = Path(tempfile.mkdtemp(prefix='igloo-import-checkpoint-', dir='/run'))
        namespace = os.stat('/proc/self/ns/mnt').st_ino
        for role in ('journal', 'target'):
            if role == 'target' and result is None:
                raise ValueError('JournalReadbackRequiredBeforeTarget')
            stage = role.capitalize() + 'Mount'
            point = space/role; point.mkdir(mode=0o700)
            image = BASE/(role+'.raw'); expected = EXPECTED[role]
            command = ['mount', '-t', 'ext4', '-o',
                f"loop,ro,noload,nosuid,nodev,noexec,offset={expected['Offset']},sizelimit={expected['PartitionLength']}",
                str(image), str(point)]
            checkpoint(store, role+'-mount-intent.json', {'Command': command, 'Namespace': namespace,
                'ImageIdentity': before['Images'][role], 'NoReplayRequested': True})
            process = subprocess.run(command, capture_output=True, timeout=30)
            if process.returncode:
                raise ValueError('ReadOnlyNoReplayMountFailed')
            mounted.append(point)
            observation = observe(point, image)
            observation['RequestedPath'] = str(point)
            try:
                checked = require_mount(observation, path=str(point), image=str(image),
                    image_identity=before['Images'][role], namespace=namespace,
                    offset=expected['Offset'], size=expected['PartitionLength'])
            except MountRejected as error:
                checkpoint(store, role+'-mount-rejection.json', compact(error.observation))
                raise
            checkpoint(store, role+'-mount-observation.json', compact(checked))
            if role == 'journal':
                stage = 'DirectJournalReadback'
                result = verify_journals(EXPORT/'provisioning', point)
                if result['ImportResult']['Sha256'] != IMPORT or result['CloseResult']['Sha256'] != CLOSE or \
                        result['Provenance']['RunId'] != 'daa72a8f-914a-4574-92ef-4d39ffb0c1e1' or \
                        result['Provenance']['GenerationId'] != 'ff104c9d-505c-4d88-8620-19b1153bd91e':
                    raise ValueError('DirectJournalBindingChanged')
                expected_observation = result['IndependentObserver']
                if any(expected_observation[k] != value for k, value in EXPECTED_SEMANTICS.items()):
                    raise ValueError('HistoricalObserverBindingChanged')
                checkpoint(store, 'direct-journal-readback.json', {'ImportRecords': len(result['ImportRecords']),
                    'SessionRecords': len(result['SessionRecords']), 'GenerationId': result['GenerationId'],
                    'SessionId': result['SessionId'], 'ImportResult': result['ImportResult'],
                    'CloseResult': result['CloseResult'], 'ExactTeardownVerified': result['ExactTeardownVerified'],
                    'ExpectedSemanticHashes': EXPECTED_SEMANTICS})
            else:
                stage = 'CompleteRootReadback'
                fd = os.open(point, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                info = os.fstat(fd)
                view = root.RootView(fd, info.st_dev, info.st_ino, root.mount_id(fd))
                try:
                    semantic = {'FilesystemSha256': root.verify_tree(view, manifest),
                        'PackageStateSha256': root.verify_dpkg(view, manifest),
                        'NeutralStateSha256': root.verify_neutral(view, manifest)}
                finally:
                    view.close(); os.close(fd)
                if semantic != EXPECTED_SEMANTICS or len(manifest['Packages']) != 1594:
                    raise ValueError('CompleteRootSemanticMismatch')
                checkpoint(store, 'complete-root-readback.json', {**semantic,
                    'ManifestEntries': len(manifest['Entries']), 'ManifestPackages': len(manifest['Packages'])})
        stage = 'InspectionTeardown'
    except Exception as error:
        failure = {'Stage': stage, 'Code': error.code if isinstance(error, MountRejected) else
                   (str(error) if isinstance(error, ValueError) and len(str(error)) < 80 else type(error).__name__)}
    finally:
        teardown_error = None
        for point in reversed(mounted):
            try:
                subprocess.run(['umount', str(point)], check=True, timeout=30, capture_output=True)
            except Exception:
                teardown_error = 'OrdinaryInspectionUnmountFailed'
        if space is not None and teardown_error is None:
            for point in (space/'target', space/'journal'):
                if point.exists(): point.rmdir()
            space.rmdir()
        if teardown_error is None:
            try:
                no_writer([BASE/'journal.raw', BASE/'target.raw'])
                if before is not None and any(list(identity(BASE/(role+'.raw'))) != before['Images'][role]
                                              for role in ('journal', 'target')):
                    raise ValueError('CheckpointBackingChangedAfterInspection')
            except Exception as error:
                teardown_error = str(error) if isinstance(error, ValueError) else type(error).__name__
        checkpoint(store, 'inspection-teardown.json', {'OrdinaryUnmountAndWriterExclusion': teardown_error is None,
            'FailureCode': teardown_error, 'MountedRoles': [p.name for p in mounted]})
        if failure is not None or teardown_error is not None:
            checkpoint(store, 'failure.json', {'Outcome': 'PendingRequiredVerification',
                'Failure': failure, 'TeardownFailure': teardown_error, 'EffectAuthority': False})
        else:
            checkpoint(store, 'result.json', {'Outcome': 'CurrentCheckpointReadOnlyVerified',
                'PreflightSha256': sha((store/'preflight.json').read_bytes()),
                'DirectJournalSha256': sha((store/'direct-journal-readback.json').read_bytes()),
                'CompleteRootSha256': sha((store/'complete-root-readback.json').read_bytes()),
                'TeardownSha256': sha((store/'inspection-teardown.json').read_bytes()),
                'EffectAuthority': False})
        print(json.dumps({'Outcome': 'Passed' if failure is None and teardown_error is None else 'Incomplete',
            'Failure': failure, 'TeardownFailure': teardown_error, 'Store': str(store)}), flush=True)
    return 0 if failure is None and teardown_error is None else 2


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('OneProtectedStoreRequired')
    raise SystemExit(run(sys.argv[1]))
