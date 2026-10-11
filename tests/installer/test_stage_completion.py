"""Synthetic stage-completion contracts; no guest, custody or effect authority."""
import base64
import contextlib
import io
import json
import multiprocessing
import os
from pathlib import Path
import shlex
import shutil
import socket
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

import canonical_stage_completion as completion
import canonical_stage_files as stage_files
from canonical_configuration_readback import digest


SOURCE = '11111111-1111-4111-8111-111111111111'
NEXT = '22222222-2222-4222-8222-222222222222'
GENERATION = '33333333-3333-4333-8333-333333333333'
OPERATION = '44444444-4444-4444-8444-444444444444'
NEXT_OPERATION = '55555555-5555-4555-8555-555555555555'
SESSION = '66666666-6666-4666-8666-666666666666'
PLAN = 'A' * 64
TARGET = 'B' * 64
JOURNAL = 'C' * 64


def encoded(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':')).encode()


def put(root, name, data):
    path = root/name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data if isinstance(data, bytes) else encoded(data))
    return path


def chain(root, stage, plan, preflight):
    """Write real byte-hash chains consumed by the existing stage readers."""
    provenance = plan['Provenance']
    if stage == 'configuration':
        steps = ('Baseline', 'Files', 'Debconf', 'Exim', 'Tls', 'Locale',
                 'User', 'Credential', 'Sudo', 'Verify')
        effects = []
        for step in steps:
            obs = {'OperationId': OPERATION, 'PlanSha256': PLAN}
            common = {'Step': step, 'Bindings': [], 'ProtectedStateSha256': 'D'*64}
            effects += [{**common, 'Outcome': 'IntentDurable'},
                        {**common, 'Outcome': 'AppliedAndVerified',
                         'Evidence': {'ObserverEvidence': obs,
                                      'ObserverEvidenceSha256': digest(encoded(obs))}}]
        version = 3
    else:
        steps = ('Baseline', 'Generation', 'Candidate', 'Publication', 'Verify')
        image = {'Length': 17, 'Sha256': 'E'*64}
        effects = []
        for step in steps:
            obs = {'OperationId': OPERATION, 'PlanSha256': PLAN, 'Step': step,
                   'ConfiguredEntriesSha256': plan['ConfiguredEntriesSha256'],
                   'PackageStateSha256': 'F'*64, 'Image': image,
                   'CompleteImageQualification': True,
                   'ImageSemantics': {'ImageSha256': image['Sha256'],
                                      'ImageLength': image['Length']}}
            pub = {'Destination': plan['Candidate']['Output'],
                   'Policy': 'CreateNewInitramfsImageV1', 'Uid': 0,
                   'Gid': 0, 'Mode': 384, 'Candidate': image}
            common = {'Step': step, 'Bindings': [], 'ProtectedStateSha256': 'F'*64}
            effects += [{**common, 'Outcome': 'IntentDurable',
                         'Evidence': {'Publication': pub} if step == 'Publication' else {}},
                        {**common, 'Outcome': 'AppliedAndVerified',
                         'Evidence': {'ObserverEvidence': obs,
                                      'ObserverEvidenceSha256': digest(encoded(obs)),
                                      'HelperEvidence': {'State': 'Exited', 'ExitCode': 0},
                                      'Publication': image}}]
        version = 4
    sessions = []
    for action, path, before, after in (
            ('UnmountPayload', '/payload', [{'Path': '/root', 'Id': 1},
                                            {'Path': '/payload', 'Id': 2}],
             [{'Path': '/root', 'Id': 1}]),
            ('UnmountRoot', '/root', [{'Path': '/root', 'Id': 1}], [])):
        sessions.append({'Action': action, 'State': 'AppliedAndVerified',
                         'Evidence': {'Path': path, 'Before': {'Mounts': before},
                                      'After': {'Mounts': after}}})
    sessions.append({'Action': 'Close', 'State': 'AppliedAndVerified'})
    refs = {}
    for store, records in (('effects', effects), ('session', sessions)):
        directory = root/'guest-evidence/journals'/store/GENERATION
        directory.mkdir(parents=True)
        put(directory, 'plan.sha256', PLAN.encode())
        previous = None
        for index, item in enumerate(records):
            if store == 'session' and item['Action'] == 'Close':
                item['ConfigurationResultReference' if stage == 'configuration' else
                     'InitramfsResultReference'] = refs['effects'][-1]['Reference']
            record = {'SchemaVersion': version, 'GenerationId': GENERATION,
                      'SessionId': SESSION, 'OperationId': OPERATION,
                      'PlanSha256': PLAN, 'Provenance': provenance,
                      'Sequence': index, 'PreviousSha256': previous, **item}
            if stage == 'configuration':
                record['Predecessor'] = preflight['Predecessor']
            else:
                record['ConfigurationSha256'] = plan['ConfigurationSha256']
                record['ConfigurationCloseSha256'] = plan['ConfigurationCloseSha256']
            raw = encoded(record)
            previous = digest(raw)
            name = f'{index:08d}-{previous}.json'
            put(directory, name, raw)
            refs.setdefault(store, []).append({'Reference': name, 'Sha256': previous})
    return refs


def fixture(base, stage):
    spec = completion.STAGES[stage]
    handoff = base/'store'/stage/(SOURCE.replace('-', '')+'-published')
    pending = base/'store'/stage/SOURCE.replace('-', '')
    source_store = base/'source-auth'/stage/SOURCE
    raw = handoff/'raw'
    raw.mkdir(parents=True)
    provenance = {'Provider': 'IsolatedFileBackedLab', 'GenerationId': GENERATION}
    plan = {'OperationId': OPERATION, 'Provenance': provenance}
    if stage == 'initramfs':
        plan.update(ConfigurationSha256='1'*64, ConfigurationCloseSha256='2'*64,
                    ConfiguredEntriesSha256='3'*64,
                    Candidate={'Output': '/boot/initrd.img-test'})
    preflight = {'Phase': f'{stage.title()}Preflight', 'Plan': plan,
                 'PlanSha256': PLAN, 'SessionId': SESSION}
    if stage == 'configuration':
        preflight['Predecessor'] = {'ResultSha256': '4'*64}
    refs = chain(raw, stage, plan, preflight)
    source_auth = {'Version': 1, 'Scope': spec['source_scope'],
                   'AttemptId': SOURCE, 'OperationId': OPERATION,
                   'GenerationId': GENERATION}
    put(raw, 'input/source-authorization.json', source_auth)
    put(source_store, 'authorization.json', source_auth)
    manifest = {'Version': 1, 'Scope': 'ProtectedStageAcquisitionToolsV1',
                'Stage': stage, 'Files': {'fixture-tool.py': 'A'*64}}
    put(source_store, 'completion-tools.json', manifest)
    guest_import = {'Version': 1, 'Stage': stage,
                    'Entry': f'canonical_{stage}_guest.py',
                    'Files': {name: 'A'*64 for name in
                              completion.GUEST_IMPORT_FILES[stage]}}
    put(source_store, 'guest-import-closure.json', guest_import)
    launch = {'Scope': 'ControlledStageLaunchV1', 'Stage': stage, 'RunId': SOURCE,
              'LaunchSha256': '9'*64,
              'AuthorizationSha256': digest(encoded(source_auth)),
              'GuestImportClosureSha256': digest(encoded(guest_import)),
              'ReceiverManifestSha256': digest(encoded(manifest))}
    put(source_store, 'completion-launch.json', launch)
    process = {'Scope': 'ControlledStageProcessV1', 'Stage': stage,
               'Pid': 123, 'StartTime': 456,
               'LaunchWitnessSha256': digest(encoded(launch))}
    put(source_store, 'completion-process.json', process)
    put(raw, spec['predecessor']+'proof.json', {'Fixture': True})
    stdout = encoded(preflight)+b'\n'+encoded({stage.title(): 'AppliedAndVerified'})+b'\n'
    put(raw, f'guest-evidence/execution/{stage}.stdout', stdout)
    put(raw, f'guest-evidence/execution/{stage}-exit.json',
        {'ExitCode': 0, 'StdoutSha256': digest(stdout)})
    host = {'RunId': SOURCE, 'ReopenedBackings': [
        {'Serial': serial, 'HostDevice': 7, 'HostInode': inode, 'Length': 19,
         'LaunchSha256': '9'*64}
        for serial, inode in (('IGLOO-GPT-RUNTIME', 20),
                              ('IGLOO-LAB-TARGET', 21), ('IGLOO-LAB-JOURNAL', 22))]}
    put(raw, 'guest-evidence/execution/host-binding.json', host)
    put(raw, 'guest-evidence/execution/capture-journal-mount.json',
        {'MountId': 42, 'MajorMinor': '252:3', 'Root': '/',
         'MountPoint': '/lab-journal', 'FileSystem': 'ext4',
         'Source': '/dev/vdc1',
         'MountOptions': ['noexec', 'nodev', 'nosuid', 'rw'],
         'PrivatePropagation': True, 'ExactNoChildOrStackedMount': True})
    for name in ('inventory.json',
                 'continuation.json' if stage == 'configuration' else 'declaration.json',
                 'continuation-inventory.json' if stage == 'configuration' else 'candidate-plan.json',
                 'preserved-before.json', 'preserved-after.json',
                 'provisioning-journal-unmount.json'):
        put(raw, 'guest-evidence/execution/'+name, {'Fixture': True})
    observed = spec['readback'](raw/'guest-evidence/execution',
                                raw/'guest-evidence/journals')
    put(raw, 'guest-evidence/execution/independent-handoff.json', observed)
    captured = {p.relative_to(raw).as_posix(): digest(p.read_bytes())
                for p in raw.rglob('*') if p.is_file()}
    challenge = '5'*64
    packet = {'Version': 1, 'Scope': f'ControlledGuest{stage.title()}CaptureV1',
              'Stage': stage, 'Challenge': challenge, 'RunId': SOURCE,
              'GenerationId': GENERATION, 'OperationId': OPERATION,
              'SessionId': SESSION, 'PlanSha256': PLAN,
              'ResultSha256': refs['effects'][-1]['Sha256'],
              'CloseSha256': refs['session'][-1]['Sha256'],
              'HostBindingSha256': digest((raw/'guest-evidence/execution/host-binding.json').read_bytes()),
              'Files': [{'Path': name, 'Length': len(data), 'Sha256': digest(data),
                         'Bytes': base64.b64encode(data).decode('ascii')}
                        for name in sorted(captured)
                        for data in [(raw/name).read_bytes()]]}
    put(handoff, 'packet.bin', encoded(packet))
    capture = {'Version': 1, 'Scope': f'PendingControlled{stage.title()}CaptureV1',
               'Stage': stage, 'Challenge': challenge, 'RunId': SOURCE,
               'GenerationId': GENERATION, 'OperationId': OPERATION,
               'SessionId': SESSION, 'PlanSha256': PLAN,
               'ResultSha256': refs['effects'][-1]['Sha256'],
               'CloseSha256': refs['session'][-1]['Sha256'],
               'PacketSha256': digest(encoded(packet)),
               'LaunchWitnessSha256': digest(encoded(launch)),
               'ProcessWitnessSha256': digest(encoded(process)),
               'SourceAuthorizationSha256': digest(encoded(source_auth)),
               'Files': captured, 'Status': 'PendingBackingReadback'}
    put(handoff, 'capture.json', capture)
    put(source_store, 'completion-writer-disposition.json', {
        'Version': 1, 'Scope': 'ControlledStageWriterDispositionV1',
        'Stage': stage, 'RunId': SOURCE, 'Pid': process['Pid'],
        'StartTime': process['StartTime'],
        'LaunchWitnessSha256': digest(encoded(launch)),
        'ProcessWitnessSha256': digest(encoded(process)),
        'CaptureSha256': digest(encoded(capture)),
        'PoweroffRequested': True, 'ProcessExitedViaPidfd': True,
        'WriterExcluded': True, 'GuestShutdownVerified': False,
        'CanonicalCloseClaimed': False,
        'AttachmentObservation': {'FdWritersAbsent': True,
            'LoopAttachmentsAbsent': True, 'NbdAttachmentsAbsent': True,
            'HostMountReferencesAbsent': True}})
    backings = {name: {'Device': 7, 'Inode': inode, 'Length': 19,
                       'Links': 1, 'Sha256': sha}
                for name, inode, sha in (('target.raw', 21, TARGET),
                                         ('journal.raw', 22, JOURNAL))}
    retained = {'RunId': SOURCE, 'OperationId': OPERATION,
                'GenerationId': GENERATION, 'Outcome': 'AppliedAndVerified',
                'CanonicalTeardown': 'AppliedAndVerified',
                spec['result']: refs['effects'][-1], 'CloseResult': refs['session'][-1],
                'IndependentHandoffSha256': digest(encoded(observed)),
                'Backings': backings}
    put(raw, spec['retention'], retained)
    put(raw, f'{stage}-independent-host-readback.json',
        {'Verified': observed, 'CaptureSha256': digest(encoded(capture)),
         'WriterExcluded': True})
    inventory = {p.relative_to(raw).as_posix(): digest(p.read_bytes())
                 for p in raw.rglob('*') if p.is_file()}
    anchor = {'Version': 1, 'Scope': f'TrustedLive{stage.title()}TerminalV1',
              'Stage': stage, 'SourceRunId': SOURCE, 'GenerationId': GENERATION,
              'OperationId': OPERATION, 'SessionId': SESSION, 'PlanSha256': PLAN,
              'ResultSha256': refs['effects'][-1]['Sha256'],
              'CloseSha256': refs['session'][-1]['Sha256'],
              'RetentionSha256': digest(encoded(retained)), 'TargetSha256': TARGET,
              'JournalSha256': JOURNAL, 'CaptureSha256': digest(encoded(capture)),
              'Files': inventory}
    put(handoff, 'anchor.json', anchor)
    seal = {'Version': 1, 'Scope': 'ProtectedStageRetentionSealV1',
            'Stage': stage, 'RunId': SOURCE, 'GenerationId': GENERATION,
            'OperationId': OPERATION, 'SessionId': SESSION,
            'ResultSha256': anchor['ResultSha256'],
            'CloseSha256': anchor['CloseSha256'],
            'AnchorSha256': digest(encoded(anchor)),
            'CaptureSha256': digest(encoded(capture)),
            'WriterDispositionSha256': digest((source_store/
                'completion-writer-disposition.json').read_bytes()),
            'RetentionSha256': anchor['RetentionSha256'],
            'TargetSha256': TARGET, 'JournalSha256': JOURNAL,
            'Status': 'FinalizedAfterIndependentReopen'}
    put(handoff, 'completion-seal.json', seal)
    controller_success = {'Version': 1,
        'Scope': 'ProtectedStageControllerSuccessV1',
        'Stage': stage, 'RunId': SOURCE,
        'GenerationId': GENERATION, 'OperationId': OPERATION,
        'SessionId': SESSION, 'ResultSha256': anchor['ResultSha256'],
        'CloseSha256': anchor['CloseSha256'],
        'AnchorSha256': digest(encoded(anchor)),
        'SealSha256': digest(encoded(seal)),
        'CaptureSha256': digest(encoded(capture)),
        'SourceAuthorizationSha256': digest(encoded(source_auth)),
        'LaunchWitnessSha256': digest(encoded(launch)),
        'ProcessWitnessSha256': digest(encoded(process)),
        'WriterDispositionSha256': digest((source_store/
            'completion-writer-disposition.json').read_bytes()),
        'Status': 'CompletedAfterProtectedSealReopen'}
    put(source_store, 'completion-controller-success.json', controller_success)
    next_auth = {'Version': 1, 'Scope': spec['next_scope'],
                 'AttemptId': NEXT, 'OperationId': NEXT_OPERATION,
                 'GenerationId': GENERATION,
                 spec['next_result']: anchor['ResultSha256'],
                 'CloseSha256': anchor['CloseSha256'],
                 'RetentionSha256': anchor['RetentionSha256'],
                 'TargetSha256': TARGET, 'JournalSha256': JOURNAL}
    if stage == 'initramfs':
        next_auth['TransferSha256'] = '8'*64
    next_auth_path = put(base/'next-auth'/stage/NEXT, 'authorization.json', next_auth)
    put(handoff, 'result.json',
        {'Version': 1, 'Scope': f'Protected{stage.title()}CompletionV1',
         'Stage': stage, 'AnchorSha256': digest(encoded(anchor)),
         'SealSha256': digest(encoded(seal)),
         'ControllerSuccessSha256': digest(encoded(controller_success)),
         'AuthorizationSha256': digest(encoded(next_auth)),
         'Files': inventory, 'Outcome': 'AppliedAndVerified'})
    for name in ('anchor.json', 'capture.json', 'completion-seal.json',
                 'packet.bin'):
        put(pending, name, (handoff/name).read_bytes())
    shutil.copytree(raw, pending/'raw')
    return handoff, next_auth_path


@contextlib.contextmanager
def fixture_roots(base):
    with patch.object(completion, 'COMPLETION_ROOT', base/'store'), \
         patch.object(completion, 'NEXT_AUTH_ROOTS', {stage: base/'next-auth'/stage
                                                     for stage in completion.STAGES}), \
         patch.object(completion, 'SOURCE_AUTH_ROOTS', {stage: base/'source-auth'/stage
                                                       for stage in completion.STAGES}), \
         patch.object(stage_files, '_protected'):
        yield


def serve_stage_console(path, challenge, stage, ready, command_queue):
    from canonical_stage_guest import emit
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
        listener.bind(path)
        listener.listen(1)
        ready.set()
        connection, _ = listener.accept()
        with connection:
            command = bytearray()
            while not command.endswith(b'; echo IGLOO_CAPTURE_EXIT:$?\n') and \
                    len(command) <= 16 * 1024:
                part = connection.recv(1024)
                if not part:
                    break
                command.extend(part)
            command_queue.put(bytes(command))
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                emit(b'{}', challenge)
            connection.sendall(output.getvalue().encode()+b'IGLOO_CAPTURE_EXIT:0\n')


def serve_poweroff(path, ready, command_queue, hold=0):
    import time
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
        listener.bind(path)
        listener.listen(1)
        ready.set()
        connection, _ = listener.accept()
        with connection:
            command_queue.put(connection.recv(1024))
            if hold:
                time.sleep(hold)


class StageCompletionTests(unittest.TestCase):
    def test_mount_namespace_alias_and_unreadable_topology_fail_closed(self):
        import canonical_stage_acquisition as acquisition
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            backing = put(base, 'target.raw', b'fixture backing')
            proc = base/'proc'
            first = proc/'101'; second = proc/'202'
            for process in (first, second):
                (process/'ns').mkdir(parents=True)
                (process/'fd').mkdir()
                (process/'root').mkdir()
                put(process, 'ns/mnt', b'namespace')
            device = backing.stat().st_dev
            major_minor = f'{os.major(device)}:{os.minor(device)}'
            line = (f'42 1 {major_minor} / /alias rw - ext4 /dev/fixture rw\n')
            put(first, 'mountinfo', line.encode())
            put(second, 'mountinfo', line.encode())
            put(first, 'root/alias', b'unrelated')
            alias = second/'root/alias'
            os.link(backing, alias)
            with self.assertRaisesRegex(ValueError,
                                        'StageBackingHostMountIdentityAttached'):
                acquisition._mount_unattached([backing], proc)
            alias.unlink()
            put(second, 'root/alias', b'unrelated')
            acquisition._mount_unattached([backing], proc)
            (second/'mountinfo').unlink()
            with self.assertRaisesRegex(ValueError,
                                        'StageBackingMountNamespaceUnresolved'):
                acquisition._mount_unattached([backing], proc)

    @unittest.skipUnless(sys.platform.startswith('linux'),
                         'Linux loop identity fixture required')
    def test_loop_backing_inode_alias_rejected(self):
        import canonical_stage_acquisition as acquisition
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            backing = put(base, 'target.raw', b'fixture backing')
            device = put(base, 'dev/loop7', b'fixture loop')
            put(base, 'sys/loop7/loop/backing_file', b'/other/alias')

            def attached(_fd, _command, status, _mutate):
                struct.pack_into('=QQ', status, 0,
                                 backing.stat().st_dev, backing.stat().st_ino)

            with patch.object(acquisition.fcntl, 'ioctl', side_effect=attached), \
                 self.assertRaisesRegex(ValueError, 'StageBackingLoopAttached'):
                acquisition._loop_unattached([backing], base/'sys', base/'dev')
            device.unlink()
            with self.assertRaisesRegex(ValueError,
                                        'StageBackingLoopIdentityUnresolved'):
                acquisition._loop_unattached([backing], base/'sys', base/'dev')

    def test_fresh_journal_mount_identity_and_flags(self):
        from canonical_stage_guest import journal_mount_record
        line = ('42 21 252:3 / /lab-journal rw,nosuid,nodev,noexec '
                '- ext4 /dev/vdc1 rw\n')
        record = journal_mount_record(line, '252:3')
        self.assertEqual('/lab-journal', record['MountPoint'])
        completion.validate_journal_mount(encoded(record))
        for changed in (
                line.replace('rw,nosuid', 'ro,nosuid'),
                line.replace('252:3', '252:4'),
                line.replace('ext4', 'xfs'),
                line.replace('/dev/vdc1', '/dev/vdd1'),
                line.replace('/ /lab-journal ', '/sub /lab-journal '),
                line.replace('rw,nosuid,nodev,noexec', 'rw,nosuid,nodev'),
                line.replace(' - ext4', ' shared:5 - ext4'),
                line + '43 42 252:3 / /lab-journal/child rw - ext4 /dev/vdc1 rw\n',
                line + line.replace('42 21', '43 21')):
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                journal_mount_record(changed, '252:3')
        weakened = {**record, 'ExactNoChildOrStackedMount': False}
        with self.assertRaisesRegex(ValueError,
                                    'StageCompletionJournalMountChanged'):
            completion.validate_journal_mount(encoded(weakened))

    def test_receiver_rejects_changed_journal_mount_before_pending_create(self):
        import canonical_stage_acquisition as acquisition
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, _ = fixture(base, stage)
                source_store = base/'source-auth'/stage/SOURCE
                packet = json.loads((handoff/'packet.bin').read_bytes())
                mount = next(item for item in packet['Files'] if item['Path'] ==
                    'guest-evidence/execution/capture-journal-mount.json')
                value = json.loads(base64.b64decode(mount['Bytes']))
                value['PrivatePropagation'] = False
                changed = encoded(value)
                mount.update(Length=len(changed), Sha256=digest(changed),
                             Bytes=base64.b64encode(changed).decode('ascii'))
                run = base/'run'; pending = base/'pending'
                found = {str(run/name): {'Device': 7, 'Inode': inode, 'Length': 19}
                    for name, inode in (('runtime.raw', 20), ('target.raw', 21),
                                        ('journal.raw', 22))}
                launch = json.loads((source_store/'completion-launch.json').read_bytes())
                with patch.object(acquisition, 'protected'), \
                     patch.object(stage_files, '_protected'), self.assertRaisesRegex(
                        ValueError, 'StageCompletionJournalMountChanged'):
                    acquisition.accept_packet(stage, encoded(packet), '5'*64, run,
                        source_store/'authorization.json', pending, launch, {}, found)
                self.assertFalse(pending.exists())

    @unittest.skipUnless(sys.platform.startswith('linux'),
                         'Linux no-follow snapshot fixture required')
    def test_semantic_readback_uses_captured_bytes_and_rejects_later_substitution(self):
        for stage in completion.STAGES:
            for action in ('verify', 'publish'):
                with self.subTest(stage=stage, action=action), \
                     tempfile.TemporaryDirectory() as temp:
                    base = Path(temp)
                    handoff, authorization = fixture(base, stage)
                    pending = base/'store'/stage/SOURCE.replace('-', '')
                    root = handoff if action == 'verify' else pending
                    if action == 'publish':
                        shutil.rmtree(handoff)
                    journal = root/'raw/guest-evidence/journals/effects'/GENERATION
                    record = next(journal.glob('00000000-*.json'))
                    real_readback = completion.STAGES[stage]['readback']
                    observed_from_capture = []

                    def substitute_after_snapshot(evidence, journals, *, captured):
                        record.write_bytes(b'{}')
                        result = real_readback(evidence, journals, captured=captured)
                        observed_from_capture.append(result)
                        return result

                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         patch.dict(completion.STAGES, {stage: {
                             **completion.STAGES[stage],
                             'readback': substitute_after_snapshot}}), \
                         self.assertRaisesRegex(ValueError,
                             'StageCompletionIndependentReadbackChanged' if
                             action == 'verify' else
                             'StageCompletionPublicationTerminalChanged'):
                        if action == 'verify':
                            completion.verify(handoff, authorization, stage)
                        else:
                            completion.publish(pending, authorization, handoff, stage)
                    self.assertEqual(1, len(observed_from_capture))
                    self.assertEqual('AppliedAndVerified',
                                     observed_from_capture[0][stage.title()])
                    if action == 'publish':
                        self.assertFalse(handoff.exists())

    @unittest.skipUnless(sys.platform.startswith('linux') and
                         hasattr(os, 'geteuid') and os.geteuid() == 0,
                         'Linux root attachment fixture required')
    def test_retention_requires_independent_attachment_absence(self):
        import canonical_stage_acquisition as acquisition
        with tempfile.TemporaryDirectory(prefix='stage-attachment-', dir='/root') as temp:
            image = put(Path(temp), 'scratch.raw', b'small owned fixture')
            observed = acquisition.unattached([image])
            self.assertTrue(all(observed.values()))
            with image.open('rb'):
                with self.assertRaisesRegex(ValueError, 'BackingStillOpen'):
                    acquisition.unattached([image])
            with patch.object(acquisition.subprocess, 'check_output',
                              return_value=b'/dev/loop99: [fixture]'):
                with self.assertRaisesRegex(ValueError, 'StageBackingLoopAttached'):
                    acquisition.unattached([image])

    @unittest.skipUnless(sys.platform.startswith('linux') and
                         hasattr(os, 'pidfd_open'), 'Linux pidfd fixture required')
    def test_stage_writer_disposition_uses_exact_peer_and_pidfd(self):
        from canonical_userdata_acquisition import process_start_time
        import canonical_stage_acquisition as acquisition
        import pwd
        if os.geteuid() != pwd.getpwnam('gillesduif').pw_uid:
            self.skipTest('nominated unprivileged account required')
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                run = base/'run'; run.mkdir()
                for name in ('runtime.raw', 'target.raw', 'journal.raw', 'tools.iso'):
                    put(run, name, b'fixture-'+name.encode())
                authority = base/'authority'; authority.mkdir()
                put(authority, 'authorization.json', {'Fixture': True})
                pending = base/'completions'/stage/run.name
                pending.mkdir(parents=True)
                ready = multiprocessing.Event()
                commands = multiprocessing.Queue()
                child = multiprocessing.Process(target=serve_poweroff,
                    args=(str(run/'console.sock'), ready, commands))
                child.start()
                try:
                    self.assertTrue(ready.wait(5))
                    process = {'Pid': child.pid,
                               'StartTime': process_start_time(child.pid)}
                    launch = {'RunId': SOURCE,
                              'ToolsSha256': completion.digest((run/'tools.iso').read_bytes()),
                              'Disks': {name: {'Device': (run/name).stat().st_dev,
                                  'Inode': (run/name).stat().st_ino,
                                  'Length': (run/name).stat().st_size}
                                  for name in ('runtime.raw', 'target.raw', 'journal.raw')}}
                    put(authority, 'completion-launch.json', launch)
                    put(authority, 'completion-process.json', process)
                    put(pending, 'capture.json', {'Status': 'PendingBackingReadback',
                        'Stage': stage, 'RunId': SOURCE,
                        'LaunchWitnessSha256': completion.digest(encoded(launch)),
                        'ProcessWitnessSha256': completion.digest(encoded(process))})
                    with patch.object(acquisition, 'COMPLETION_PARENT', base/'completions'), \
                         patch.object(acquisition, 'inspect_controlled_process',
                                      return_value=(launch, process, {})), \
                         patch.object(acquisition, 'protected'), \
                         patch.object(acquisition, 'unattached', return_value={
                             'FdWritersAbsent': True, 'LoopAttachmentsAbsent': True,
                             'NbdAttachmentsAbsent': True,
                             'HostMountReferencesAbsent': True}):
                        outcome = acquisition.finish_writer(stage, run,
                            authority/'authorization.json', pending, wait_seconds=5)
                    self.assertEqual(b'poweroff\n', commands.get(timeout=2))
                    self.assertEqual('ControlledStageWriterDispositionV1',
                        json.loads(outcome.read_bytes())['Scope'])
                    self.assertFalse(json.loads(outcome.read_bytes())['CanonicalCloseClaimed'])
                    self.assertFalse(json.loads(outcome.read_bytes())['GuestShutdownVerified'])
                    child.join(5)
                    self.assertFalse(child.is_alive())
                finally:
                    if child.is_alive():
                        child.terminate(); child.join(5)

    def test_closed_controller_orders_effects_without_retry(self):
        import types
        with patch.dict(sys.modules, {'pwd': types.SimpleNamespace()}):
            import canonical_stage_acquisition as acquisition
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            calls = []
            def step(name):
                def apply(*_, **__):
                    calls.append(name)
                    return base/name
                return apply
            with patch.object(acquisition, 'COMPLETION_PARENT', base/'completions'), \
                 patch.object(acquisition, 'pin_writer', side_effect=lambda *_:
                    (os.open(os.devnull, os.O_RDONLY), {}, {})), \
                 patch.object(acquisition, 'receive_live', side_effect=step('receive')), \
                 patch.object(acquisition, 'finish_writer', side_effect=step('writer')), \
                 patch.object(acquisition, 'finalize_retention', side_effect=step('finalize')):
                self.assertEqual(base/'finalize', acquisition.complete_live(
                    'configuration', base/'run', base/'auth',
                    base/'completions/configuration/run'))
            self.assertEqual(['receive', 'writer', 'finalize'], calls)
            calls.clear()
            def fail(*_):
                calls.append('writer')
                raise ValueError('StageWriterExitTimeout')
            with patch.object(acquisition, 'COMPLETION_PARENT', base/'completions'), \
                 patch.object(acquisition, 'pin_writer', side_effect=lambda *_:
                    (os.open(os.devnull, os.O_RDONLY), {}, {})), \
                 patch.object(acquisition, 'receive_live', side_effect=step('receive')), \
                 patch.object(acquisition, 'finish_writer', side_effect=fail), \
                 patch.object(acquisition, 'finalize_retention', side_effect=step('finalize')), \
                 patch.object(acquisition, 'failure_disposition', side_effect=step('failure')), \
                 self.assertRaisesRegex(ValueError, 'StageWriterExitTimeout'):
                acquisition.complete_live('initramfs', base/'run', base/'auth',
                                          base/'completions/initramfs/run')
            self.assertEqual(['receive', 'writer', 'failure'], calls)

    def test_anchor_written_then_reopen_failure_is_not_admissible(self):
        import canonical_stage_acquisition as acquisition
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, authorization = fixture(base, stage)
                source_store = base/'source-auth'/stage/SOURCE
                pending = base/'store'/stage/SOURCE.replace('-', '')
                completion_parent = base/'store'
                run = base/'run'; run.mkdir()
                controller_pending = completion_parent/stage/run.name

                def receive(*_):
                    controller_pending.mkdir()

                def fail_after_anchor(*_):
                    put(controller_pending, 'anchor.json',
                        (pending/'anchor.json').read_bytes())
                    raise ValueError('StageCaptureAnchorReopenChanged')

                def record_failure(*_, **__):
                    put(source_store, 'completion-failure-intent.json',
                        {'FailureCode': 'StageCaptureAnchorReopenChanged'})
                    put(source_store, 'completion-failure-disposition.json',
                        {'WriterExcluded': True, 'CanonicalCloseClaimed': False})
                    return True

                with patch.object(acquisition, 'COMPLETION_PARENT', completion_parent), \
                     patch.object(acquisition, 'pin_writer', side_effect=lambda *_:
                         (os.open(os.devnull, os.O_RDONLY), {}, {})), \
                     patch.object(acquisition, 'receive_live', side_effect=receive), \
                     patch.object(acquisition, 'finish_writer'), \
                     patch.object(acquisition, 'finalize_retention',
                                  side_effect=fail_after_anchor), \
                     patch.object(acquisition, 'failure_disposition',
                                  side_effect=record_failure), \
                     self.assertRaisesRegex(ValueError,
                                            'StageCaptureAnchorReopenChanged'):
                    acquisition.complete_live(stage, run, authorization,
                                              controller_pending)
                self.assertTrue((controller_pending/'anchor.json').exists())
                self.assertTrue((source_store/'completion-failure-intent.json').exists())
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                                            'StageCompletionFailureDispositionPresent'):
                    completion.verify(handoff, authorization, stage)
                shutil.rmtree(handoff)
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                                            'StageCompletionFailureDispositionPresent'):
                    completion.publish(pending, authorization, handoff, stage)
                self.assertFalse(handoff.exists())

    def test_either_failure_marker_including_dangling_link_blocks_effects(self):
        for stage in completion.STAGES:
            for marker, dangling in (
                    ('completion-failure-intent.json', False),
                    ('completion-failure-disposition.json', False),
                    ('completion-failure-intent.json', True)):
                with self.subTest(stage=stage, marker=marker, dangling=dangling), \
                     tempfile.TemporaryDirectory() as temp:
                    base = Path(temp)
                    handoff, authorization = fixture(base, stage)
                    source_store = base/'source-auth'/stage/SOURCE
                    target = source_store/marker
                    if dangling:
                        target.symlink_to('missing-fixture-record')
                    else:
                        put(source_store, marker, {'FailureCode': 'FixtureFailure'})
                    pending = base/'store'/stage/SOURCE.replace('-', '')
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         self.assertRaisesRegex(ValueError,
                             'StageCompletionFailureDispositionPresent'):
                        completion.verify(handoff, authorization, stage)
                    shutil.rmtree(handoff)
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         self.assertRaisesRegex(ValueError,
                             'StageCompletionFailureDispositionPresent'):
                        completion.publish(pending, authorization, handoff, stage)
                    self.assertFalse(handoff.exists())

    def test_protected_guest_import_witness_mismatch_blocks_both_successors(self):
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, authorization = fixture(base, stage)
                source_store = base/'source-auth'/stage/SOURCE
                manifest = source_store/'guest-import-closure.json'
                manifest.write_bytes(manifest.read_bytes()+b'\n')
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                         'StageCompletionGuestImportWitnessChanged'):
                    completion.verify(handoff, authorization, stage)
                pending = base/'store'/stage/SOURCE.replace('-', '')
                shutil.rmtree(handoff)
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                         'StageCompletionGuestImportWitnessChanged'):
                    completion.publish(pending, authorization, handoff, stage)
                self.assertFalse(handoff.exists())

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux process witness required')
    def test_protected_launch_manifest_digest_precedes_peer_command(self):
        import canonical_stage_acquisition as acquisition
        from canonical_userdata_acquisition import process_start_time
        for stage in completion.STAGES:
            with self.subTest(stage=stage), \
                 tempfile.TemporaryDirectory(dir='/root') as temp:
                base = Path(temp)
                fixture(base, stage)
                source_store = base/'source-auth'/stage/SOURCE
                authorization = source_store/'authorization.json'
                run = base/'runs'/SOURCE.replace('-', '')
                run.mkdir(parents=True)
                put(run, 'launch-intent.json', {'Fixture': True})
                launch = json.loads((source_store/'completion-launch.json').read_bytes())
                launch.update(Version=1, RunPath=str(run),
                    LaunchSha256=digest((run/'launch-intent.json').read_bytes()),
                    Arguments=['/usr/bin/qemu-system-x86_64'], Disks={},
                    ToolsSha256='A'*64)
                launch['GuestImportClosureSha256'] = '0'*64
                put(source_store, 'completion-launch.json', launch)
                process = {'Version': 1, 'Scope': 'ControlledStageProcessV1',
                    'Stage': stage, 'Pid': os.getpid(),
                    'StartTime': process_start_time(os.getpid()),
                    'LaunchWitnessSha256': digest(encoded(launch))}
                put(source_store, 'completion-process.json', process)
                with patch.object(acquisition, 'RUN_PARENT', {stage: run.parent}), \
                     patch.object(acquisition, 'AUTH_PARENT',
                                  {stage: source_store.parent}), \
                     patch.object(acquisition, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                         'StageCaptureLaunchWitnessChanged'):
                    acquisition.inspect_controlled_process(stage, run, authorization)

                launch['GuestImportClosureSha256'] = digest(
                    (source_store/'guest-import-closure.json').read_bytes())
                put(source_store, 'completion-launch.json', launch)
                process['LaunchWitnessSha256'] = digest(encoded(launch))
                put(source_store, 'completion-process.json', process)
                with patch.object(acquisition, 'RUN_PARENT', {stage: run.parent}), \
                     patch.object(acquisition, 'AUTH_PARENT',
                                  {stage: source_store.parent}), \
                     patch.object(acquisition, 'protected'), \
                     self.assertRaisesRegex(ValueError, 'StageCaptureLaunchChanged'):
                    acquisition.inspect_controlled_process(stage, run, authorization)

    @unittest.skipUnless(sys.platform.startswith('linux'),
                         'Linux successor fixture required')
    def test_anchor_reopen_and_failure_marker_write_both_fail_without_seal(self):
        import canonical_stage_acquisition as acquisition
        from canonical_initramfs_lab import stage_predecessor as stage_configuration
        from canonical_userdata_lab import stage_predecessor as stage_initramfs
        for stage, successor in (('configuration', stage_configuration),
                                 ('initramfs', stage_initramfs)):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, authorization = fixture(base, stage)
                pending = base/'store'/stage/SOURCE.replace('-', '')
                seed = base/'seed-pending'
                shutil.copytree(pending, seed)
                shutil.rmtree(pending)
                run = base/SOURCE.replace('-', '')
                run.mkdir()

                def receive(*_):
                    shutil.copytree(seed, pending)
                    (pending/'anchor.json').unlink()
                    (pending/'completion-seal.json').unlink()

                def fail_reopen(*_):
                    put(pending, 'anchor.json', (seed/'anchor.json').read_bytes())
                    raise ValueError('StageCaptureAnchorReopenChanged')

                with patch.object(acquisition, 'COMPLETION_PARENT', base/'store'), \
                     patch.object(acquisition, 'pin_writer', side_effect=lambda *_:
                         (os.open(os.devnull, os.O_RDONLY), {}, {})), \
                     patch.object(acquisition, 'receive_live', side_effect=receive), \
                     patch.object(acquisition, 'finish_writer'), \
                     patch.object(acquisition, 'finalize_retention',
                                  side_effect=fail_reopen), \
                     patch.object(acquisition, 'failure_disposition',
                                  side_effect=OSError('FailureMarkerFsyncUnavailable')), \
                     self.assertRaisesRegex(ValueError,
                         'StageCaptureFailureDispositionUnavailable'):
                    acquisition.complete_live(stage, run, authorization, pending)
                self.assertTrue((pending/'anchor.json').exists())
                self.assertFalse((pending/'completion-seal.json').exists())
                self.assertFalse((base/'source-auth'/stage/SOURCE/
                    'completion-failure-intent.json').exists())
                from canonical_storage_lab import create
                kwargs = ({'initramfs_authorization': authorization,
                           'configured_predecessor': run,
                           'configured_completion': handoff}
                          if stage == 'configuration' else
                          {'userdata_authorization': authorization,
                           'initramfs_predecessor': run,
                           'initramfs_completion': handoff,
                           'userdata_inputs': base/'inputs'})
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     patch('canonical_storage_lab.require_backing_capacity') as capacity, \
                     self.assertRaisesRegex(ValueError,
                         'StageCompletionPendingCaptureUnavailable'):
                    create(base, base, base, **kwargs)
                capacity.assert_not_called()
                self.assertFalse((authorization.parent/'reservation.json').exists())
                destination = base/'next-input'
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                         'StageCompletionPendingCaptureUnavailable'):
                    successor(handoff, authorization, destination)
                self.assertFalse(destination.exists())
                shutil.rmtree(handoff)
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                         'StageCompletionRequiredRecordUnavailable'):
                    completion.publish(pending, authorization, handoff, stage)
                self.assertFalse(handoff.exists())

    def test_seal_binds_stage_anchor_terminal_and_writer_disposition(self):
        for stage in completion.STAGES:
            for field in ('Stage', 'AnchorSha256', 'ResultSha256',
                          'CloseSha256', 'WriterDispositionSha256'):
                with self.subTest(stage=stage, field=field), \
                     tempfile.TemporaryDirectory() as temp:
                    base = Path(temp)
                    handoff, authorization = fixture(base, stage)
                    pending = base/'store'/stage/SOURCE.replace('-', '')
                    seal = json.loads((pending/'completion-seal.json').read_bytes())
                    seal[field] = ('wrong-stage' if field == 'Stage' else '0'*64)
                    changed = encoded(seal)
                    put(pending, 'completion-seal.json', changed)
                    put(handoff, 'completion-seal.json', changed)
                    result_path = handoff/'result.json'
                    result = json.loads(result_path.read_bytes())
                    result['SealSha256'] = digest(changed)
                    put(handoff, 'result.json', result)
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         self.assertRaisesRegex(ValueError,
                                                'StageCompletionSealChanged'):
                        completion.verify(handoff, authorization, stage)
                    shutil.rmtree(handoff)
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         self.assertRaisesRegex(ValueError,
                                                'StageCompletionSealChanged'):
                        completion.publish(pending, authorization, handoff, stage)
                    self.assertFalse(handoff.exists())

    @unittest.skipUnless(sys.platform.startswith('linux'),
                         'Linux durable ordinary-file fixture required')
    def test_real_seal_writer_publisher_and_successor_reader(self):
        import canonical_stage_acquisition as acquisition
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, authorization = fixture(base, stage)
                pending = base/'store'/stage/SOURCE.replace('-', '')
                anchor = json.loads((pending/'anchor.json').read_bytes())
                capture = json.loads((pending/'capture.json').read_bytes())
                (pending/'completion-seal.json').unlink()
                (base/'source-auth'/stage/SOURCE/
                 'completion-controller-success.json').unlink()
                shutil.rmtree(handoff)
                run = base/'runs'/SOURCE.replace('-', '')
                run.mkdir(parents=True)
                source_auth = (base/'source-auth'/stage/SOURCE/
                               'authorization.json')
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     patch.object(acquisition, 'protected'), \
                     patch.object(acquisition, 'RUN_PARENT', {stage: run.parent}), \
                     patch.object(acquisition, 'AUTH_PARENT',
                                  {stage: source_auth.parent.parent}), \
                     patch.object(acquisition, 'COMPLETION_PARENT', base/'store'):
                    seal = acquisition.seal_finalized_anchor(
                        stage, pending, source_auth, anchor, capture)
                    self.assertEqual(seal.name, 'completion-seal.json')
                    with self.assertRaisesRegex(ValueError, 'StageCaptureSealReplay'):
                        acquisition.seal_finalized_anchor(
                            stage, pending, source_auth, anchor, capture)
                    success = acquisition.record_controller_success(
                        stage, run, source_auth, pending, seal)
                    self.assertTrue(success.is_file())
                    published = completion.publish(
                        pending, authorization, handoff, stage)
                    self.assertEqual(published,
                                     completion.verify(handoff, authorization, stage))

    @unittest.skipUnless(sys.platform.startswith('linux'),
                         'Linux durable ordinary-file fixture required')
    def test_indeterminate_seal_write_and_failed_disposition_cannot_publish(self):
        import canonical_stage_acquisition as acquisition
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, authorization = fixture(base, stage)
                pending = base/'store'/stage/SOURCE.replace('-', '')
                seed = base/'seed-pending'
                shutil.copytree(pending, seed)
                shutil.rmtree(pending)
                run = base/SOURCE.replace('-', '')
                run.mkdir()
                source_authorization = (base/'source-auth'/stage/SOURCE/
                                        'authorization.json')
                original_checkpoint = acquisition.checkpoint

                def receive(*_):
                    shutil.copytree(seed, pending)
                    (pending/'completion-seal.json').unlink()

                def indeterminate_checkpoint(directory, name, value):
                    original_checkpoint(directory, name, value)
                    if name == 'completion-seal-staged.json':
                        raise OSError('InjectedSealReopenFailure')

                def finalize(*_):
                    return acquisition.seal_finalized_anchor(
                        stage, pending, source_authorization,
                        json.loads((pending/'anchor.json').read_bytes()),
                        json.loads((pending/'capture.json').read_bytes()))

                with fixture_roots(base), patch.object(completion, 'protected'), \
                     patch.object(acquisition, 'protected'), \
                     patch.object(acquisition, 'COMPLETION_PARENT', base/'store'), \
                     patch.object(acquisition, 'pin_writer', side_effect=lambda *_:
                         (os.open(os.devnull, os.O_RDONLY), {}, {})), \
                     patch.object(acquisition, 'receive_live', side_effect=receive), \
                     patch.object(acquisition, 'finish_writer'), \
                     patch.object(acquisition, 'finalize_retention', side_effect=finalize), \
                     patch.object(acquisition, 'checkpoint',
                                  side_effect=indeterminate_checkpoint), \
                     patch.object(acquisition, 'failure_disposition',
                                  side_effect=OSError('InjectedFailureMarkerFsync')), \
                     self.assertRaisesRegex(ValueError,
                         'StageCaptureFailureDispositionUnavailable'):
                    acquisition.complete_live(stage, run, source_authorization,
                                              pending)
                self.assertTrue((pending/'anchor.json').exists())
                self.assertTrue((pending/'completion-seal-staged.json').exists())
                self.assertFalse((pending/'completion-seal.json').exists())
                shutil.rmtree(handoff)
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                         'StageCompletionSealUncommitted'):
                    completion.publish(pending, authorization, handoff, stage)
                self.assertFalse(handoff.exists())

    @unittest.skipUnless(sys.platform.startswith('linux'),
                         'Linux seal-finalization fault fixture required')
    def test_failed_post_unlink_flush_or_reopen_cannot_publish(self):
        import canonical_stage_acquisition as acquisition
        from canonical_storage_lab import create
        for stage in completion.STAGES:
            for fault in ('post-unlink-fsync', 'final-reopen'):
                with self.subTest(stage=stage, fault=fault), \
                     tempfile.TemporaryDirectory() as temp:
                    base = Path(temp)
                    handoff, authorization = fixture(base, stage)
                    pending = base/'store'/stage/SOURCE.replace('-', '')
                    seed = base/'seed-pending'
                    shutil.copytree(pending, seed)
                    shutil.rmtree(pending)
                    source_auth = (base/'source-auth'/stage/SOURCE/
                                   'authorization.json')
                    (source_auth.parent/
                     'completion-controller-success.json').unlink()
                    run = base/SOURCE.replace('-', '')
                    run.mkdir()

                    def receive(*_):
                        shutil.copytree(seed, pending)
                        (pending/'completion-seal.json').unlink()

                    def finalize(*_):
                        anchor = json.loads((pending/'anchor.json').read_bytes())
                        capture = json.loads((pending/'capture.json').read_bytes())
                        if fault == 'post-unlink-fsync':
                            original_fsync = os.fsync
                            count = 0

                            def fail_fourth(fd):
                                nonlocal count
                                count += 1
                                if count == 4:
                                    raise OSError('InjectedPostUnlinkFsyncFailure')
                                return original_fsync(fd)

                            with patch.object(acquisition.os, 'fsync',
                                              side_effect=fail_fourth):
                                return acquisition.seal_finalized_anchor(
                                    stage, pending, source_auth, anchor, capture)
                        original_exact = acquisition.exact_file

                        def fail_final(path, **kwargs):
                            if Path(path) == pending/'completion-seal.json':
                                raise OSError('InjectedFinalSealReopenFailure')
                            return original_exact(path, **kwargs)

                        with patch.object(acquisition, 'exact_file',
                                          side_effect=fail_final):
                            return acquisition.seal_finalized_anchor(
                                stage, pending, source_auth, anchor, capture)

                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         patch.object(acquisition, 'protected'), \
                         patch.object(acquisition, 'COMPLETION_PARENT', base/'store'), \
                         patch.object(acquisition, 'pin_writer', side_effect=lambda *_:
                             (os.open(os.devnull, os.O_RDONLY), {}, {})), \
                         patch.object(acquisition, 'receive_live', side_effect=receive), \
                         patch.object(acquisition, 'finish_writer'), \
                         patch.object(acquisition, 'finalize_retention',
                                      side_effect=finalize), \
                         patch.object(acquisition, 'failure_disposition',
                                      side_effect=OSError('InjectedFailureMarkerFsync')), \
                         self.assertRaisesRegex(ValueError,
                             'StageCaptureFailureDispositionUnavailable'):
                        acquisition.complete_live(stage, run, source_auth, pending)
                    self.assertTrue((pending/'anchor.json').exists())
                    self.assertTrue((pending/'completion-seal.json').exists())
                    self.assertFalse((pending/'completion-seal-staged.json').exists())
                    self.assertFalse((source_auth.parent/
                                      'completion-controller-success.json').exists())
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         self.assertRaisesRegex(ValueError,
                             'StageCompletionControllerSuccessUnavailable'):
                        completion.verify(handoff, authorization, stage)
                    kwargs = ({'initramfs_authorization': authorization,
                               'configured_predecessor': run,
                               'configured_completion': handoff}
                              if stage == 'configuration' else
                              {'userdata_authorization': authorization,
                               'initramfs_predecessor': run,
                               'initramfs_completion': handoff,
                               'userdata_inputs': base/'inputs'})
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         patch('canonical_storage_lab.require_backing_capacity') as capacity, \
                         self.assertRaisesRegex(ValueError,
                             'StageCompletionControllerSuccessUnavailable'):
                        create(base, base, base, **kwargs)
                    capacity.assert_not_called()
                    shutil.rmtree(handoff)
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         self.assertRaisesRegex(ValueError,
                             'StageCompletionControllerSuccessUnavailable'):
                        completion.publish(pending, authorization, handoff, stage)
                    self.assertFalse(handoff.exists())

    @unittest.skipUnless(sys.platform.startswith('linux'),
                         'Linux protected-link fixture required')
    def test_interrupted_seal_link_is_not_admissible(self):
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, authorization = fixture(base, stage)
                pending = base/'store'/stage/SOURCE.replace('-', '')
                os.link(pending/'completion-seal.json',
                        pending/'completion-seal-staged.json')
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                         'StageCompletionSealUncommitted'):
                    completion.verify(handoff, authorization, stage)
                from canonical_storage_lab import create
                kwargs = ({'initramfs_authorization': authorization,
                           'configured_predecessor': base/SOURCE.replace('-', ''),
                           'configured_completion': handoff}
                          if stage == 'configuration' else
                          {'userdata_authorization': authorization,
                           'initramfs_predecessor': base/SOURCE.replace('-', ''),
                           'initramfs_completion': handoff,
                           'userdata_inputs': base/'inputs'})
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     patch('canonical_storage_lab.require_backing_capacity') as capacity, \
                     self.assertRaisesRegex(ValueError,
                         'StageCompletionSealUncommitted'):
                    create(base, base, base, **kwargs)
                capacity.assert_not_called()
                shutil.rmtree(handoff)
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError,
                         'StageCompletionSealUncommitted'):
                    completion.publish(pending, authorization, handoff, stage)
                self.assertFalse(handoff.exists())

    @unittest.skipUnless(sys.platform.startswith('linux'),
                         'Linux exact-file fixture required')
    def test_complete_command_reports_distinct_anchor_and_seal(self):
        import canonical_stage_acquisition as acquisition
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            handoff, _ = fixture(base, 'configuration')
            pending = base/'store/configuration'/SOURCE.replace('-', '')
            run = base/'runs'/SOURCE.replace('-', '')
            run.mkdir(parents=True)
            source_auth = (base/'source-auth/configuration'/SOURCE/
                           'authorization.json')
            (source_auth.parent/'completion-controller-success.json').unlink()
            with fixture_roots(base), patch.object(acquisition, 'protected'), \
                 patch.object(acquisition, 'RUN_PARENT', {'configuration': run.parent}), \
                 patch.object(acquisition, 'AUTH_PARENT',
                              {'configuration': source_auth.parent.parent}), \
                 patch.object(acquisition, 'COMPLETION_PARENT', base/'store'), \
                 patch.object(acquisition, 'complete_live',
                              return_value=pending/'completion-seal.json'), \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                acquisition.main(['complete', 'configuration', str(run),
                                  str(source_auth)])
            record = json.loads(output.getvalue())
            self.assertEqual(record['Status'],
                             'ProtectedControllerCompletionRecorded')
            self.assertEqual(record['AnchorSha256'],
                             digest((pending/'anchor.json').read_bytes()))
            self.assertEqual(record['SealSha256'],
                             digest((pending/'completion-seal.json').read_bytes()))
            self.assertEqual(record['ControllerSuccessSha256'],
                             digest((source_auth.parent/
                                     'completion-controller-success.json').read_bytes()))
            self.assertNotEqual(record['AnchorSha256'], record['SealSha256'])

    def test_controller_success_record_binds_stage_terminal_and_launch(self):
        for stage in completion.STAGES:
            for field in ('Stage', 'ResultSha256', 'CloseSha256',
                          'AnchorSha256', 'SealSha256',
                          'LaunchWitnessSha256', 'WriterDispositionSha256'):
                with self.subTest(stage=stage, field=field), \
                     tempfile.TemporaryDirectory() as temp:
                    base = Path(temp)
                    handoff, authorization = fixture(base, stage)
                    source_store = base/'source-auth'/stage/SOURCE
                    success = json.loads((source_store/
                        'completion-controller-success.json').read_bytes())
                    success[field] = ('wrong-stage' if field == 'Stage' else '0'*64)
                    changed = encoded(success)
                    put(source_store, 'completion-controller-success.json', changed)
                    result = json.loads((handoff/'result.json').read_bytes())
                    result['ControllerSuccessSha256'] = digest(changed)
                    put(handoff, 'result.json', result)
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         self.assertRaisesRegex(ValueError,
                             'StageCompletionControllerSuccessChanged'):
                        completion.verify(handoff, authorization, stage)
                    shutil.rmtree(handoff)
                    pending = base/'store'/stage/SOURCE.replace('-', '')
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         self.assertRaisesRegex(ValueError,
                             'StageCompletionControllerSuccessChanged'):
                        completion.publish(pending, authorization, handoff, stage)
                    self.assertFalse(handoff.exists())

    @unittest.skipUnless(sys.platform.startswith('linux'),
                         'Linux durable success-record fixture required')
    def test_durable_success_record_reopen_resolves_delivery_uncertainty(self):
        import canonical_stage_acquisition as acquisition
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            handoff, next_auth = fixture(base, 'configuration')
            pending = base/'store/configuration'/SOURCE.replace('-', '')
            run = base/'runs'/SOURCE.replace('-', '')
            run.mkdir(parents=True)
            source_auth = (base/'source-auth/configuration'/SOURCE/
                           'authorization.json')
            (source_auth.parent/'completion-controller-success.json').unlink()
            original_checkpoint = acquisition.checkpoint

            def delivered_but_unreported(directory, name, value):
                original_checkpoint(directory, name, value)
                if name == 'completion-controller-success.json':
                    raise OSError('InjectedResponseAfterDurableSuccess')

            with fixture_roots(base), patch.object(completion, 'protected'), \
                 patch.object(acquisition, 'protected'), \
                 patch.object(acquisition, 'RUN_PARENT',
                              {'configuration': run.parent}), \
                 patch.object(acquisition, 'AUTH_PARENT',
                              {'configuration': source_auth.parent.parent}), \
                 patch.object(acquisition, 'COMPLETION_PARENT', base/'store'), \
                 patch.object(acquisition, 'complete_live',
                              return_value=pending/'completion-seal.json'), \
                 patch.object(acquisition, 'checkpoint',
                              side_effect=delivered_but_unreported), \
                 self.assertRaisesRegex(OSError,
                     'InjectedResponseAfterDurableSuccess'):
                acquisition.main(['complete', 'configuration', str(run),
                                  str(source_auth)])
            self.assertTrue((source_auth.parent/
                             'completion-controller-success.json').exists())
            shutil.rmtree(handoff)
            with fixture_roots(base), patch.object(completion, 'protected'):
                published = completion.publish(pending, next_auth, handoff,
                                               'configuration')
                self.assertEqual(published,
                                 completion.verify(handoff, next_auth,
                                                   'configuration'))


    @unittest.skipUnless(sys.platform.startswith('linux') and
                         hasattr(os, 'pidfd_open'), 'Linux pidfd fixture required')
    def test_failed_capture_records_exact_writer_disposition(self):
        from canonical_userdata_acquisition import process_start_time
        import canonical_stage_acquisition as acquisition
        import pwd
        if os.geteuid() != pwd.getpwnam('gillesduif').pw_uid:
            self.skipTest('nominated unprivileged account required')
        for hold, excluded in ((0, True), (2, False)):
            with self.subTest(hold=hold), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                run = base/'run'; run.mkdir()
                for name in ('runtime.raw', 'target.raw', 'journal.raw'):
                    put(run, name, b'fixture')
                authority = base/'authority'; authority.mkdir()
                authorization = put(authority, 'authorization.json', {'Fixture': True})
                launch = {'RunId': SOURCE}
                put(authority, 'completion-launch.json', launch)
                ready = multiprocessing.Event()
                commands = multiprocessing.Queue()
                child = multiprocessing.Process(target=serve_poweroff,
                    args=(str(run/'console.sock'), ready, commands, hold))
                child.start()
                try:
                    self.assertTrue(ready.wait(5))
                    process = {'Pid': child.pid,
                               'StartTime': process_start_time(child.pid)}
                    put(authority, 'completion-process.json', process)
                    handle = os.pidfd_open(child.pid)
                    try:
                        with patch.object(acquisition, 'COMPLETION_PARENT',
                                          base/'completions'), \
                             patch.object(acquisition, 'inspect_controlled_process',
                                          return_value=(launch, process, {})), \
                             patch.object(acquisition, 'protected'), \
                             patch.object(acquisition, 'unattached', return_value={
                                 'FdWritersAbsent': True,
                                 'LoopAttachmentsAbsent': True,
                                 'NbdAttachmentsAbsent': True,
                                 'HostMountReferencesAbsent': True}):
                            self.assertIs(excluded, acquisition.failure_disposition(
                                'configuration', run, authorization,
                                base/'completions/configuration/run', handle,
                                launch, process, ValueError('CaptureTimeout'),
                                wait_seconds=0.05 if hold else 5))
                    finally:
                        os.close(handle)
                    self.assertEqual(b'poweroff\n', commands.get(timeout=2))
                    result = json.loads((authority/'completion-failure-disposition.json').read_bytes())
                    self.assertIs(excluded, result['WriterExcluded'])
                    self.assertFalse(result['CanonicalCloseClaimed'])
                    self.assertFalse((authority/'completion-writer-disposition.json').exists())
                finally:
                    if child.is_alive():
                        child.terminate()
                    child.join(5)

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux peer credentials required')
    def test_stage_commands_use_real_unix_peer_and_receive_loop(self):
        from canonical_userdata_acquisition import (process_start_time,
            read_controlled_channel, stage_entry_command, STAGE_ENTRY_BOOTSTRAP)
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                path = str(Path(temp)/'stage.sock')
                challenge = 'A'*64
                ready = multiprocessing.Event()
                commands = multiprocessing.Queue()
                child = multiprocessing.Process(target=serve_stage_console,
                    args=(path, challenge, stage, ready, commands))
                child.start()
                try:
                    self.assertTrue(ready.wait(5))
                    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
                        channel.connect(path)
                        packet = read_controlled_channel(channel, challenge,
                            {'Pid': child.pid,
                             'StartTime': process_start_time(child.pid)},
                            5, stage=stage,
                            guest_import_closure_sha256='B'*64)
                    self.assertEqual(b'{}', packet)
                    command = commands.get(timeout=2)
                    self.assertEqual(stage_entry_command(stage, challenge,
                        'B'*64).encode(), command)
                    self.assertGreater(len(command), 4096)
                    self.assertLessEqual(max(map(len, command.splitlines())), 4000)
                    fixed = shlex.split(command.decode().split('; echo ')[0])
                    self.assertEqual(['python3', '-I', '-B', '-c'], fixed[:4])
                    self.assertEqual('/opt/igloo-lab/lab/canonical_'+stage+'_guest.py',
                                     fixed[5])
                    self.assertEqual([stage, challenge, 'B'*64], fixed[6:])
                    self.assertEqual(STAGE_ENTRY_BOOTSTRAP, fixed[4])
                finally:
                    child.join(5)
                    if child.is_alive():
                        child.terminate(); child.join(5)

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux peer credentials required')
    def test_stage_command_requires_host_nominated_manifest_digest(self):
        from canonical_userdata_acquisition import (process_start_time,
                                                    read_controlled_channel)
        process = {'Pid': os.getpid(), 'StartTime': process_start_time(os.getpid())}
        for stage, value in (('configuration', None), ('initramfs', 'bad'),
                             ('userdata', 'A'*64)):
            channel, peer = socket.socketpair()
            with self.subTest(stage=stage), channel, peer:
                with self.assertRaisesRegex(ValueError,
                        'CaptureGuestImportExpectationInvalid'):
                    read_controlled_channel(channel, 'A'*64, process, 1,
                        stage=stage, guest_import_closure_sha256=value)

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux peer simulation required')
    def test_stage_deadlines_allow_long_work_before_begin_but_bound_data_stall(self):
        from canonical_userdata_acquisition import (process_start_time,
                                                    read_controlled_channel)
        from test_userdata_acquisition import FragmentChannel
        challenge = 'A'*64
        begin = (b'IGLOO_USERDATA_CAPTURE_BEGIN '+challenge.encode()+b' 2 '+
                 completion.digest(b'{}').encode()+b'\n')
        frame = (begin+b'IGLOO_DATA e30=\nIGLOO_USERDATA_CAPTURE_END '+
                 challenge.encode()+b'\nIGLOO_CAPTURE_EXIT:0\n')
        process = {'Pid': os.getpid(), 'StartTime': process_start_time(os.getpid())}
        channel = FragmentChannel(b'ordinary output\n'*200+frame, 128, .1)
        self.assertEqual(b'{}', read_controlled_channel(channel, challenge, process,
            30, stage='configuration', guest_import_closure_sha256='B'*64,
            clock=lambda: channel.now,
            begin_timeout_seconds=20, inactivity_timeout_seconds=2,
            inactivity_after_begin_only=True, terminal_timeout_seconds=4))
        stalled = FragmentChannel(begin, 4096, .1)
        with self.assertRaisesRegex(ValueError, 'CaptureInactivityTimeout'):
            read_controlled_channel(stalled, challenge, process, 30,
                stage='initramfs', guest_import_closure_sha256='B'*64,
                clock=lambda: stalled.now,
                begin_timeout_seconds=20, inactivity_timeout_seconds=2,
                inactivity_after_begin_only=True, terminal_timeout_seconds=4)

    def test_actual_stage_packet_and_shared_frame_round_trip(self):
        import sys
        import types
        with patch.dict(sys.modules, {'pwd': types.SimpleNamespace()}):
            from canonical_userdata_acquisition import parse_frame
        from canonical_stage_guest import emit, packet_from_files
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                handoff, _ = fixture(Path(temp), stage)
                raw = handoff/'raw'
                source = json.loads((raw/'input/source-authorization.json').read_bytes())
                observed = completion.STAGES[stage]['readback'](
                    raw/'guest-evidence/execution', raw/'guest-evidence/journals')
                host = json.loads((raw/'guest-evidence/execution/host-binding.json').read_bytes())
                capture = json.loads((handoff/'capture.json').read_bytes())
                files = {name: (raw/name).read_bytes() for name in capture['Files']}
                packet = packet_from_files(stage, '5'*64, source, observed, host, files)
                self.assertEqual((handoff/'packet.bin').read_bytes(), packet)
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    emit(packet, '5'*64)
                wire = output.getvalue().encode() + b'IGLOO_CAPTURE_EXIT:0\n'
                self.assertEqual(packet, parse_frame(wire, '5'*64))

    def test_complete_synthetic_chains_join_stage_specific_successors(self):
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                handoff, authorization = fixture(Path(temp), stage)
                with fixture_roots(Path(temp)), patch.object(completion, 'protected'):
                    result = completion.verify(handoff, authorization, stage)
                self.assertEqual(stage, result['Stage'])
                self.assertFalse(result['EffectAuthorization'])

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux successor fixture required')
    def test_synthetic_verified_completion_stages_exact_next_input(self):
        from canonical_initramfs_lab import stage_predecessor as stage_configuration
        from canonical_userdata_lab import stage_predecessor as stage_initramfs
        for stage, successor, branch in (
                ('configuration', stage_configuration, 'import'),
                ('initramfs', stage_initramfs, 'configured')):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, authorization = fixture(base, stage)
                destination = base/'staged-next-input'
                with fixture_roots(base), patch.object(completion, 'protected'):
                    successor(handoff, authorization, destination)
                self.assertTrue((destination/branch/'proof.json').is_file())
                self.assertTrue((destination/'binding.json').is_file())
                self.assertTrue((destination/'session'/GENERATION).is_dir())
                self.assertTrue((destination/'effects'/GENERATION).is_dir())

    def test_wrong_binding_or_rehashed_export_cannot_authorize(self):
        for stage in completion.STAGES:
            for change in ('result', 'close', 'target', 'scope', 'record',
                           'missing-close', 'missing-witness', 'missing-pending',
                           'missing-disposition'):
                with self.subTest(stage=stage, change=change), tempfile.TemporaryDirectory() as temp:
                    handoff, authorization = fixture(Path(temp), stage)
                    if change in ('result', 'close', 'target', 'scope'):
                        a = json.loads(authorization.read_bytes())
                        key = {'result': completion.STAGES[stage]['next_result'],
                               'close': 'CloseSha256', 'target': 'TargetSha256',
                               'scope': 'Scope'}[change]
                        a[key] = '0'*64 if change != 'scope' else 'WrongStage'
                        authorization.write_bytes(encoded(a))
                    elif change == 'record':
                        root = handoff/'raw/guest-evidence/journals/effects'/GENERATION
                        next(root.glob('00000000-*.json')).write_bytes(b'{}')
                    elif change == 'missing-witness':
                        (Path(temp)/'source-auth'/stage/SOURCE/'completion-process.json').unlink()
                    elif change == 'missing-pending':
                        (Path(temp)/'store'/stage/SOURCE.replace('-', '')/'capture.json').unlink()
                    elif change == 'missing-disposition':
                        (Path(temp)/'source-auth'/stage/SOURCE/
                         'completion-writer-disposition.json').unlink()
                    else:
                        root = handoff/'raw/guest-evidence/journals/session'/GENERATION
                        next(root.glob('00000002-*.json')).unlink()
                    with fixture_roots(Path(temp)), patch.object(completion, 'protected'), self.assertRaises((ValueError, KeyError)):
                        completion.verify(handoff, authorization, stage)

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux directory-FD publication required')
    def test_synthetic_publication_and_independent_reopen(self):
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, authorization = fixture(base, stage)
                pending = base/'store'/stage/SOURCE.replace('-', '')
                shutil.rmtree(handoff)
                with fixture_roots(base), patch.object(completion, 'protected'):
                    published = completion.publish(pending, authorization, handoff, stage)
                    reopened = completion.verify(handoff, authorization, stage)
                self.assertEqual(published, reopened)
                self.assertEqual('AppliedAndVerified',
                                 json.loads((handoff/'result.json').read_bytes())['Outcome'])
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError, 'StageCompletionPublicationPathChanged'):
                    completion.publish(pending, authorization, handoff, stage)

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux no-follow publication required')
    def test_publication_rejects_bad_next_authority_and_link_before_create(self):
        for stage in completion.STAGES:
            for change in ('unknown-field', 'reused-operation', 'wrong-attempt',
                           'linked-record'):
                with self.subTest(stage=stage, change=change), \
                     tempfile.TemporaryDirectory() as temp:
                    base = Path(temp)
                    handoff, authorization = fixture(base, stage)
                    pending = base/'store'/stage/SOURCE.replace('-', '')
                    shutil.rmtree(handoff)
                    if change == 'linked-record':
                        record = pending/'raw/guest-evidence/execution/host-binding.json'
                        record.unlink()
                        record.symlink_to('configuration.stdout' if stage == 'configuration'
                                          else 'initramfs.stdout')
                    else:
                        value = json.loads(authorization.read_bytes())
                        if change == 'unknown-field':
                            value['Unknown'] = True
                        elif change == 'reused-operation':
                            value['OperationId'] = OPERATION
                        else:
                            value['AttemptId'] = SOURCE
                        authorization.write_bytes(encoded(value))
                    with fixture_roots(base), patch.object(completion, 'protected'), \
                         self.assertRaises((ValueError, OSError)):
                        completion.publish(pending, authorization, handoff, stage)
                    self.assertFalse(handoff.exists())

    def test_diagnostic_copy_and_invalid_guest_packet_are_not_authority(self):
        import sys
        import types
        with patch.dict(sys.modules, {'pwd': types.SimpleNamespace()}):
            import canonical_stage_acquisition as acquisition
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, authorization = fixture(base, stage)
                diagnostic = base/'root-owned-diagnostic-copy'
                shutil.copytree(handoff, diagnostic)
                with fixture_roots(base), patch.object(completion, 'protected'), \
                     self.assertRaisesRegex(ValueError, 'StageCompletionPathUnnominated'):
                    completion.verify(diagnostic, authorization, stage)
                self.assertFalse(acquisition._allowed(stage,
                    'guest-evidence/journals/../outside.json'))
                pending = base/'new-pending'
                with patch.object(acquisition, 'protected'), \
                     self.assertRaisesRegex(ValueError, 'StageCaptureSchemaChanged'):
                    acquisition.accept_packet(stage, (handoff/'packet.bin').read_bytes(),
                        '0'*64, base, authorization, pending,
                        {'RunId': SOURCE}, {}, {})
                self.assertFalse(pending.exists())

    def test_receiver_persists_synthetic_pending_capture(self):
        import sys
        import types
        with patch.dict(sys.modules, {'pwd': types.SimpleNamespace()}):
            import canonical_stage_acquisition as acquisition
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                base = Path(temp)
                handoff, _ = fixture(base, stage)
                source_store = base/'source-auth'/stage/SOURCE
                run = base/'run'
                pending = base/'received-pending'
                found = {str(run/'runtime.raw'): {'Device': 7, 'Inode': 20, 'Length': 19},
                         str(run/'target.raw'): {'Device': 7, 'Inode': 21, 'Length': 19},
                         str(run/'journal.raw'): {'Device': 7, 'Inode': 22, 'Length': 19}}
                launch = json.loads((source_store/'completion-launch.json').read_bytes())
                sync_file = put(base, 'directory-fsync-fixture', b'')
                real_open = os.open
                def fixture_open(path, flags, mode=0o777, **kwargs):
                    if kwargs:
                        return real_open(path, flags, mode, **kwargs)
                    if Path(path).is_dir() and Path(path).is_relative_to(pending):
                        return real_open(sync_file, os.O_RDWR)
                    return real_open(path, flags, mode)
                with patch.object(acquisition, 'protected'), \
                     patch.object(stage_files, '_protected'), \
                     patch.object(os, 'O_DIRECTORY', 0, create=True), \
                     patch.object(os, 'O_NOFOLLOW', 0, create=True), \
                     patch.object(os, 'open', side_effect=fixture_open):
                    actual = acquisition.accept_packet(stage,
                        (handoff/'packet.bin').read_bytes(), '5'*64, run,
                        source_store/'authorization.json', pending, launch, {}, found)
                self.assertEqual(pending, actual)
                self.assertEqual('PendingBackingReadback',
                    json.loads((pending/'capture.json').read_bytes())['Status'])
                self.assertEqual((handoff/'packet.bin').read_bytes(),
                                 (pending/'packet.bin').read_bytes())

    def test_unanchored_exports_rejected_before_run_creation(self):
        # The provisioner is Linux-only. Inject only the unavailable stdlib
        # account module; the real pre-reservation guard still executes.
        import sys
        import types
        with patch.dict(sys.modules, {'pwd': types.SimpleNamespace()}):
            from canonical_storage_lab import create
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            for stage, keyword in (('configuration', 'configured_completion'),
                                   ('initramfs', 'initramfs_completion')):
                with self.subTest(stage=stage):
                    kwargs = ({'initramfs_authorization': base/'auth.json',
                               'configured_predecessor': base/SOURCE.replace('-', '')}
                              if stage == 'configuration' else
                              {'userdata_authorization': base/'auth.json',
                               'initramfs_predecessor': base/SOURCE.replace('-', ''),
                               'userdata_inputs': base/'inputs'})
                    with self.assertRaisesRegex(ValueError,
                        'ConfiguredTrustedCompletionRequired' if stage == 'configuration'
                        else 'InitramfsTrustedCompletionRequired'):
                        create(base, base, base, **kwargs)
                    self.assertFalse((base/SOURCE.replace('-', '')).exists())

    def test_configuration_full_reserve_precedes_run_creation(self):
        from canonical_storage_lab import create
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            authorization = put(base, 'authorization.json', {'AttemptId': NEXT})
            with patch('canonical_storage_lab.require_backing_capacity',
                       side_effect=ValueError('FullReserveUnavailable')) as capacity, \
                 self.assertRaisesRegex(ValueError, 'FullReserveUnavailable'):
                create(base/'predecessor', base, base,
                       configuration_checkpoint=base/'checkpoint',
                       derived_authorization=authorization)
            capacity.assert_called_once_with(180 * 1024**3)
            self.assertFalse((base/'predecessor'/NEXT.replace('-', '')).exists())


if __name__ == '__main__':
    unittest.main()
