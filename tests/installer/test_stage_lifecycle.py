"""Process fixtures for the closed host caller; no QEMU or canonical authority."""
import contextlib
import datetime as dt
import io
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

import canonical_stage_acquisition as acquisition
import canonical_stage_completion as completion
import canonical_stage_lifecycle as lifecycle
import test_stage_completion as stage_fixture
from canonical_lab_runtime import checkpoint
from canonical_stage_guest import emit
from canonical_userdata_acquisition import read_controlled_channel
from test_stage_completion import (SOURCE, NEXT, fixture, fixture_roots)
from test_userdata_acquisition import FragmentChannel


CHALLENGE = '5'*64


def packet_wire(packet):
    output = io.StringIO()
    with contextlib.redirect_stdout(output):
        emit(packet, CHALLENGE)
    return output.getvalue().encode()+b'IGLOO_CAPTURE_EXIT:0\n'


def complete_reader_metadata(reader_root, stage):
    """Read identities from the .NET fixture export; never grant effect authority."""
    reader_root = Path(reader_root)
    authorization = json.loads((reader_root/('derived-authorization.json' if
        stage == 'configuration' else 'initramfs-authorization.json')).read_bytes())
    stores = reader_root/'effects'
    generations = [path for path in stores.iterdir() if path.is_dir()]
    assert len(generations) == 1
    records = sorted(generations[0].glob('*.json'))
    assert records
    first = json.loads(records[0].read_bytes())
    assert authorization['AttemptId'] == first['Provenance']['RunId']
    return {'RunId': authorization['AttemptId'],
            'OperationId': authorization['OperationId'],
            'GenerationId': authorization['GenerationId'],
            'SessionId': first['SessionId'],
            'PlanSha256': first['PlanSha256'],
            'TargetSha256': authorization['TargetSha256'],
            'JournalSha256': authorization['JournalSha256'],
            'ConfiguredRunId': (json.loads((reader_root/'configured'/
                'derived-authorization.json').read_bytes())['AttemptId'] if
                stage == 'initramfs' else None)}


def complete_reader_payload(raw, stage, reader_root):
    """Place exact exported .NET ancestry inside the existing capture fixture."""
    reader_root = Path(reader_root)
    if stage == 'configuration':
        shutil.copytree(reader_root/'import', raw/'input/predecessor')
        authorization_name = 'derived-authorization.json'
        names = ('continuation.json', 'continuation-inventory.json')
        plan_name = 'configuration-plan.json'
    else:
        shutil.copytree(reader_root/'configured',
                        raw/'input/configured-predecessor')
        authorization_name = 'initramfs-authorization.json'
        names = ('declaration.json', 'inventory.json')
        plan_name = 'initramfs-plan.json'
    authorization_raw = (reader_root/authorization_name).read_bytes()
    for name in names:
        target = raw/'guest-evidence/execution'/name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(reader_root/name, target)
    references = {}
    first = None
    for store in ('effects', 'session'):
        shutil.copytree(reader_root/store, raw/'guest-evidence/journals'/store)
        generations = [path for path in (reader_root/store).iterdir()
                       if path.is_dir()]
        assert len(generations) == 1
        records = sorted(generations[0].glob('*.json'))
        assert records
        if store == 'effects':
            first = json.loads(records[0].read_bytes())
        references[store] = [{'Reference': records[-1].name,
                              'Sha256': lifecycle.storage.sha(records[-1])}]
    plan = json.loads((reader_root/plan_name).read_bytes())
    preflight_raw = (reader_root.parent/f'{stage}-preflight.json').read_bytes()
    preflight = json.loads(preflight_raw)
    assert preflight['Plan'] == plan
    assert preflight['PlanSha256'] == first['PlanSha256']
    assert preflight['SessionId'] == first['SessionId']
    if stage == 'configuration':
        assert preflight['Predecessor'] == first['Predecessor']
    return {'References': references,
            'SourceAuthorization': json.loads(authorization_raw),
            'SourceAuthorizationRaw': authorization_raw,
            'Preflight': preflight, 'PreflightRaw': preflight_raw}


def fixture_child(stage, root, fault=None, reader_export=None):
    """Run the actual host main in a separate process with effect boundaries doubled."""
    base = Path(root)
    reader_root = (Path(reader_export)/stage if reader_export is not None
                   else None)
    lineage = (complete_reader_metadata(reader_root, stage) if reader_root
               else None)
    if lineage:
        with patch.multiple(stage_fixture,
                SOURCE=lineage['RunId'], GENERATION=lineage['GenerationId'],
                OPERATION=lineage['OperationId'], SESSION=lineage['SessionId'],
                PLAN=lineage['PlanSha256'], TARGET=lineage['TargetSha256'],
                JOURNAL=lineage['JournalSha256']):
            handoff, next_auth = fixture(base, stage,
                payload_builder=lambda raw, selected:
                    complete_reader_payload(raw, selected, reader_root))
    else:
        handoff, next_auth = fixture(base, stage)
    source_id = lineage['RunId'] if lineage else SOURCE
    pending = base/'store'/stage/source_id.replace('-', '')
    source_auth = base/'source-auth'/stage/source_id/'authorization.json'
    (source_auth.parent/'completion-controller-success.json').unlink()
    writer_disposition = (source_auth.parent/'completion-writer-disposition.json').read_bytes()
    (source_auth.parent/'completion-writer-disposition.json').unlink()
    launch_bytes = (source_auth.parent/'completion-launch.json').read_bytes()
    process_bytes = (source_auth.parent/'completion-process.json').read_bytes()
    (source_auth.parent/'completion-launch.json').unlink()
    (source_auth.parent/'completion-process.json').unlink()
    shutil.rmtree(handoff)
    previous = base/'runs'/stage/'previous'; previous.mkdir(parents=True)
    run = previous.parent/source_id.replace('-', '')
    checkpoint_dir = base/'checkpoint'; checkpoint_dir.mkdir()
    failed = base/'failed'; failed.mkdir()
    configured_source = (lineage['ConfiguredRunId'] if lineage and
                         stage == 'initramfs' else source_id)
    configured = base/'configured'/configured_source.replace('-', '')
    configured.mkdir(parents=True)
    pin = base/'pin.json'
    pin.write_text(json.dumps({'Use': 'DevelopmentImportOnly',
        'ProductionAuthentication': 'Unsupported',
        'DescriptorSha256': 'A'*64, 'PolicySha256': 'B'*64,
        'ManifestSha256': 'C'*64, 'ContentSha256': 'D'*64,
        'NotBeforeUtc': (dt.datetime.now(dt.timezone.utc)-dt.timedelta(days=1)).isoformat(),
        'NotAfterUtc': (dt.datetime.now(dt.timezone.utc)+dt.timedelta(days=1)).isoformat()}))
    real_reader = (reader_root is not None or
                   fault == 'actual-linux-reader-incomplete-ancestry')
    runtime = (Path(os.environ['IGLOO_STAGE_CALLER_DOTNET']) if real_reader
               else base/'dotnet')
    if not real_reader:
        runtime.write_bytes(b'fixture runtime, never executed')
    publish = base/'publish'; publish.mkdir()
    (base/'distros/debian/native').mkdir(parents=True)
    if real_reader:
        # The real Linux reader needs its built dependency and runtime closure.
        # The separate build-output location is a fixture input, never a
        # canonical deployment tool or a fallback module path.
        build = Path(os.environ['IGLOO_STAGE_CALLER_HARNESS']).parent
        for name in ('CanonicalImportQualification.dll',
                     'CanonicalImportQualification.deps.json',
                     'CanonicalImportQualification.runtimeconfig.json',
                     'Igloo.Core.dll', 'Igloo.Distro.Debian.dll',
                     'CryptSharp.dll',
                     'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
                     'Microsoft.Extensions.Logging.Abstractions.dll',
                     'distro.json'):
            shutil.copyfile(build/name, publish/name)
    else:
        (publish/'CanonicalImportQualification.dll').write_bytes(
            b'fixture harness, never executed')
    frozen_files = {str(path): lifecycle.storage.sha(path)
                    for path in publish.iterdir() if path.is_file()}
    request = base/'request.json'
    next_auth_bytes = next_auth.read_bytes()
    if fault == 'missing-next-authorization':
        next_auth.unlink()
    request.write_text(json.dumps({'Version': 1, 'Scope': 'InternalLabStageLifecycleV1',
        'Stage': stage, 'Previous': str(previous), 'Repo': str(base),
        'Publish': str(publish), 'SourceAuthorization': str(source_auth),
        'Pin': str(pin), 'ReaderRuntime': str(runtime),
        'ReaderRuntimeSha256': lifecycle.storage.sha(runtime),
        'NextAuthorization': str(next_auth),
        'ConfigurationCheckpoint': str(checkpoint_dir) if stage == 'configuration' else None,
        'FailedConfiguration': str(failed) if stage == 'configuration' else None,
        'Acceptance': str(base/'acceptance') if stage == 'configuration' else None,
        'AcceptanceSha256': 'A'*64 if stage == 'configuration' else None,
        'ConfiguredCompletion': str(base/'configured-completion') if stage == 'initramfs' else None,
        'ConfiguredPredecessor': str(configured) if stage == 'initramfs' else None}))
    ledger = []
    original_verify = completion.verify

    def verify(handoff_path, authorization_path, requested_stage):
        if (stage == 'initramfs' and requested_stage == 'configuration' and
                Path(handoff_path) == base/'configured-completion'):
            ledger.append('predecessor')
            return {'SourceRunId': configured_source}
        return original_verify(handoff_path, authorization_path, requested_stage)

    def create(*_args, **kwargs):
        if fault == 'provision-before-launch':
            raise ValueError('InjectedProvisioningFailure')
        kwargs['before_launch'](run)
        ledger.append('launch')
        run.mkdir()
        (source_auth.parent/'completion-launch.json').write_bytes(launch_bytes)
        (source_auth.parent/'completion-process.json').write_bytes(process_bytes)
        kwargs['on_launch'](type('Process', (), {'pid': os.getpid()})(), run)
        if fault == 'provision-after-launch':
            raise ValueError('InjectedPostLaunchFailure')
        return run

    def pin(*_args):
        ledger.append('pin')
        if fault == 'launch-observation':
            raise ValueError('InjectedLaunchObservationFailure')
        return os.open(os.devnull, os.O_RDONLY), {}, {}

    def observe(*_args, protected_binding):
        ledger.append('observe')
        if fault == 'backing-observation':
            raise ValueError('InjectedBackingObservationFailure')
        data = (pending/'raw/guest-evidence/execution/host-binding.json').read_bytes()
        host = json.loads(data)
        checkpoint(protected_binding.parent, protected_binding.name, host)
        checkpoint(run, 'host-binding.json', host)
        return run/'host-binding.json'

    def prepare(*_args):
        ledger.append('prepare')
        if fault == 'cancel-preparation':
            raise KeyboardInterrupt()
        if fault == 'guest-preparation':
            raise ValueError('InjectedGuestPreparationFailure')
        binding = (source_auth.parent/'completion-host-binding.json').read_bytes()
        command = lifecycle.acquisition.stage_preparation_command(
            stage, CHALLENGE, 'A'*64, run.name, binding)
        assert 'python3 -I -B -c ' in command
        wire = (b'echoed fixed command\r\nIGLOO_STAGE_PREPARED '+
                CHALLENGE.encode()+b' '+lifecycle.storage.sha(
                    source_auth.parent/'completion-host-binding.json').encode()+
                b'\r\nIGLOO_PREPARE_EXIT:0\r\n')
        channel = FragmentChannel(wire, 3, .001)
        assert acquisition._preparation_status(channel, CHALLENGE,
            lifecycle.storage.sha(source_auth.parent/'completion-host-binding.json'),
            clock=lambda: channel.now)['Acknowledged']

    def capture(*_args):
        ledger.append('capture')
        if fault == 'capture':
            raise ValueError('CaptureTimeout')
        (source_auth.parent/'completion-writer-disposition.json').write_bytes(
            writer_disposition)
        packet = (pending/'packet.bin').read_bytes()
        channel = FragmentChannel(packet_wire(packet), 37, .001)
        from canonical_userdata_acquisition import process_start_time
        actual = read_controlled_channel(channel, CHALLENGE,
            {'Pid': os.getpid(), 'StartTime': process_start_time(os.getpid())},
            30, stage=stage, guest_import_closure_sha256='A'*64,
            clock=lambda: channel.now)
        assert actual == packet
        assert completion.STAGES[stage]['readback'](
            pending/'raw/guest-evidence/execution',
            pending/'raw/guest-evidence/journals')[completion.STAGES[stage]['status']] == 'AppliedAndVerified'
        return pending/'completion-seal.json'

    original_controller = acquisition.record_controller_success
    def controller(*args):
        ledger.append('controller')
        if fault == 'controller':
            raise ValueError('InjectedControllerFailure')
        result = original_controller(*args)
        if fault == 'lost-controller-response':
            raise OSError('response lost after controller success')
        return result

    def reader(value, current, authorization):
        ledger.append('reader-fixture-double')
        if fault == 'reader':
            raise ValueError('InjectedLinuxReaderFailure')
        anchor = json.loads((current/'anchor.json').read_bytes())
        checkpoint(authorization.parent, 'completion-reader-result.json', {
            'Version': 1, 'Scope': 'LabStageReadOnlyLinuxReaderV1', 'Stage': stage,
            'AnchorSha256': lifecycle.storage.sha(current/'anchor.json'),
            'ControllerSuccessSha256': lifecycle.storage.sha(
                authorization.parent/'completion-controller-success.json'),
            'SourceManifestSha256': lifecycle.storage.sha(
                authorization.parent/'completion-caller-source.json'),
            'RuntimeSha256': lifecycle.storage.sha(runtime),
            'HarnessSha256': lifecycle.storage.sha(
                publish/'CanonicalImportQualification.dll'),
            'ReaderInput': str(authorization.parent/'completion-reader-input'),
            'ReaderResult': {'Scope': ('ReadOnlyConfiguredPredecessor' if
                stage == 'configuration' else 'ReadOnlyInitramfsPredecessor'),
                'ResultSha256': anchor['ResultSha256'],
                'CloseSha256': anchor['CloseSha256'],
                'PlanSha256': anchor['PlanSha256'],
                'EffectAuthorization': False},
            'EffectAuthorization': False})
        if fault == 'reader-record-changed':
            record_path = authorization.parent/'completion-reader-result.json'
            changed = json.loads(record_path.read_bytes())
            changed['ReaderResult']['ResultSha256'] = '0'*64
            record_path.write_text(json.dumps(changed))
        return lifecycle._reader_reopen(value, current, authorization)

    actual_reader = lifecycle._reader
    def read_with_linux(value, current, authorization):
        ledger.append('reader-linux')
        if fault == 'invalid-reader-ancestry':
            imported = current/'raw/input'/('predecessor' if stage ==
                'configuration' else 'configured-predecessor')/'storage.json'
            if stage == 'initramfs':
                imported = imported.parent/'import/storage.json'
            imported.unlink()
        if fault == 'source-manifest-changed':
            source = authorization.parent/'completion-caller-source.json'
            record = json.loads(source.read_bytes())
            record['ReaderRuntimeSha256'] = '0'*64
            source.write_text(json.dumps(record))
        return actual_reader(value, current, authorization)

    original_publish = completion.publish
    def publish_result(*args):
        ledger.append('publish')
        if fault == 'publication':
            raise ValueError('InjectedPublicationFailure')
        result = original_publish(*args)
        if fault == 'lost-publication-response':
            raise OSError('response lost after publication')
        return result

    def failed_disposition(*_args):
        ledger.append('failure-disposition-fixture-double')
        return True

    def source_manifest(value, authorization, request_sha):
        ledger.append('frozen-source-fixture-double')
        checkpoint(authorization.parent, 'completion-caller-source.json', {
            'Version': 1, 'Scope': 'FrozenLabStageCallerSourceV1',
            'Stage': stage, 'RequestSha256': request_sha,
            'Files': frozen_files, 'ReaderRuntimeSha256': value['ReaderRuntimeSha256']})
        return frozen_files

    def staged_closure(*_args):
        ledger.append('staged-closure-fixture-double')
        if fault == 'staged-closure':
            raise ValueError('InjectedStagedClosureFailure')

    with contextlib.ExitStack() as stack:
        scopes = [fixture_roots(base, actual_protection=reader_root is not None),
                patch.object(acquisition, 'RUN_PARENT', {stage: run.parent}),
                patch.object(acquisition, 'AUTH_PARENT',
                             {stage: source_auth.parent.parent}),
                patch.object(acquisition, 'COMPLETION_PARENT', base/'store'),
                patch.object(completion, 'verify', side_effect=verify),
                patch.object(lifecycle, '_source_files', return_value=frozen_files),
                patch.object(lifecycle, '_freeze_source', side_effect=source_manifest),
                patch.object(lifecycle, '_verify_staged_closure',
                             side_effect=staged_closure),
                patch.object(lifecycle.storage, 'create', side_effect=create),
                patch.object(acquisition, 'pin_writer', side_effect=pin),
                patch.object(lifecycle.storage, 'observe', side_effect=observe),
                patch.object(acquisition, 'prepare_live', side_effect=prepare),
                patch.object(acquisition, 'complete_live', side_effect=capture),
                patch.object(acquisition, 'failure_disposition',
                             side_effect=failed_disposition),
                patch.object(acquisition, 'record_controller_success',
                             side_effect=controller),
                patch.object(lifecycle, '_reader', side_effect=(
                    read_with_linux if real_reader else reader)),
                patch.object(completion, 'publish', side_effect=publish_result)]
        if reader_root is None:
            scopes += [patch.object(completion, 'protected'),
                       patch.object(acquisition, 'protected')]
        for scope in scopes:
            stack.enter_context(scope)
        if stage == 'configuration':
            declared = json.loads(source_auth.read_bytes())
            accepted = {'Candidate': str(checkpoint_dir)+'/',
                'CheckpointResultSha256': declared.get('CheckpointSha256'),
                'ImportResultSha256': declared.get('ImportSha256'),
                'CloseResultSha256': declared.get('CloseSha256')}
            with patch.dict(sys.modules, {'canonical_import_checkpoint_acceptance':
                    types.SimpleNamespace(verify_acceptance=lambda *_: accepted)}):
                code = lifecycle.main(['run', str(request)])
        else:
            code = lifecycle.main(['run', str(request)])
        reader_result = None
        if fault is None:
            assert code == 0, ledger
            reader_event = ('reader-linux' if real_reader else
                            'reader-fixture-double')
            assert ledger.index('staged-closure-fixture-double') < \
                   ledger.index('launch') < ledger.index('observe') < \
                   ledger.index('prepare') < \
                   ledger.index('capture') < ledger.index('controller') < \
                   ledger.index(reader_event) < ledger.index('publish'), ledger
            assert (handoff/'result.json').is_file()
            assert completion.verify(handoff, next_auth, stage)['EffectAuthorization'] is False
            before = tuple(ledger)
            assert lifecycle.main(['run', str(request)]) == 0
            assert tuple(ledger) == before
            record_path = source_auth.parent/'completion-reader-result.json'
            changed = json.loads(record_path.read_bytes())
            reader_result = changed['ReaderResult']
            if real_reader:
                plan_name = ('configuration-plan.json' if stage ==
                             'configuration' else 'initramfs-plan.json')
                reader_plan = source_auth.parent/'completion-reader-input'/plan_name
                assert reader_plan.read_bytes() == (reader_root/plan_name).read_bytes()
                assert original_verify(handoff, next_auth, stage)[
                    'EffectAuthorization'] is False
            else:
                changed['ReaderResult']['CloseSha256'] = '0'*64
                record_path.write_text(json.dumps(changed))
                try:
                    original_verify(handoff, next_auth, stage)
                    raise AssertionError('changed reader result admitted')
                except ValueError as error:
                    assert str(error) == 'StageCompletionCallerReaderChanged'
        elif fault == 'missing-next-authorization':
            assert code == 0 and 'publish' not in ledger, ledger
            assert (source_auth.parent/'completion-controller-success.json').is_file()
            next_auth.write_bytes(next_auth_bytes)
            before = tuple(ledger)
            assert lifecycle.main(['publish', str(request)]) == 0
            assert tuple(ledger) == before+('publish',), ledger
        else:
            assert code == 2, ledger
            failure = json.loads((source_auth.parent/'completion-caller-failure.json').read_bytes())
            assert failure['Stage'] == stage and failure['EffectAuthorization'] is False
            if fault in ('provision-after-launch', 'backing-observation',
                         'cancel-preparation', 'guest-preparation', 'capture'):
                assert 'failure-disposition-fixture-double' in ledger
                assert 'publish' not in ledger
            if fault in ('provision-before-launch', 'staged-closure',
                         'launch-observation'):
                assert 'publish' not in ledger and failure['WriterExcluded'] is not True
            if fault in ('reader', 'reader-record-changed',
                         'actual-linux-reader-incomplete-ancestry',
                         'invalid-reader-ancestry', 'source-manifest-changed',
                         'lost-controller-response', 'publication'):
                assert ('publish' in ledger) == (fault == 'publication')
                try:
                    original_publish(pending, next_auth, handoff, stage)
                    raise AssertionError('direct publisher bypassed failed caller')
                except ValueError as error:
                    assert str(error) in ('StageCompletionCallerFailurePresent',
                        'StageCompletionCallerReaderChanged',
                        'StageCompletionCallerReaderMissing',
                        'StageCompletionControllerSuccessUnavailable')
            if fault == 'lost-publication-response':
                assert handoff/'result.json' and (handoff/'result.json').is_file()
                before = tuple(ledger)
                assert lifecycle.main(['run', str(request)]) == 0
                assert tuple(ledger) == before
    print(json.dumps({'Stage': stage, 'Events': ledger,
                      'Fault': fault, 'FixtureOnly': True,
                      'LinuxReaderDoubled': not real_reader,
                      'ReaderResult': reader_result}))


class StageLifecycleTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0,
                         'ordinary protected fixture directory required')
    def test_exact_preflight_plan_bytes_survive_reader_shaping(self):
        from canonical_lab_runtime import checkpoint_exact_json_member
        with tempfile.TemporaryDirectory(dir='/var/lib/igloo') as temp:
            root = Path(temp)
            document = (b'{"Plan":{"Candidate":{"KernelRelease":"6.12.107\\u002Bdeb13-amd64",'
                        b'"Output":"/boot/initrd"}},"PlanSha256":"' + b'A'*64 + b'"}')
            checkpoint_exact_json_member(root, 'plan.json', document, 'Plan')
            self.assertEqual(b'{"Candidate":{"KernelRelease":"6.12.107\\u002Bdeb13-amd64",'
                             b'"Output":"/boot/initrd"}}', (root/'plan.json').read_bytes())
            with self.assertRaisesRegex(ValueError, 'PreflightDuplicateField'):
                checkpoint_exact_json_member(root, 'duplicate.json',
                    b'{"Plan":{},"Plan":{}}', 'Plan')
            self.assertFalse((root/'duplicate.json').exists())

    @unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0 and
                         os.environ.get('IGLOO_STAGE_CALLER_DOTNET') and
                         os.environ.get('IGLOO_STAGE_CALLER_HARNESS') and
                         os.environ.get('IGLOO_STAGE_CALLER_READER_EXPORT'),
                         'protected Linux runtime and complete .NET fixture required')
    def test_complete_ancestry_reaches_actual_linux_reader_in_both_callers(self):
        export = Path(os.environ['IGLOO_STAGE_CALLER_READER_EXPORT'])
        metadata = json.loads((export/'fixture-metadata.json').read_bytes())
        self.assertEqual('SyntheticReadOnlyStageReaderFixtureV1', metadata['Scope'])
        self.assertIs(metadata['EffectAuthorization'], False)
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory(
                    dir='/var/lib/igloo/stage-reader-fixtures') as temp:
                program = ('import sys;sys.path.insert(0,sys.argv[1]);'
                           'from test_stage_lifecycle import fixture_child;'
                           'fixture_child(sys.argv[2],sys.argv[3],None,sys.argv[4])')
                response = subprocess.run([sys.executable, '-B', '-c', program,
                    str(Path(__file__).parent), stage, temp, str(export)],
                    capture_output=True, text=True, timeout=60)
                self.assertEqual(0, response.returncode,
                                 response.stderr+response.stdout)
                result = json.loads(response.stdout.splitlines()[-1])
                self.assertEqual(stage, result['Stage'])
                self.assertIs(result['FixtureOnly'], True)
                self.assertIs(result['LinuxReaderDoubled'], False)
                self.assertLess(result['Events'].index('controller'),
                                result['Events'].index('reader-linux'))
                self.assertLess(result['Events'].index('reader-linux'),
                                result['Events'].index('publish'))
                run_id = metadata[stage.title()]['RunId']
                reader_record = json.loads((Path(temp)/'source-auth'/stage/run_id/
                    'completion-reader-result.json').read_bytes())
                self.assertEqual('LabStageReadOnlyLinuxReaderV1', reader_record['Scope'])
                self.assertIs(reader_record['EffectAuthorization'], False)
                self.assertIs(result['ReaderResult']['EffectAuthorization'], False)
                self.assertEqual(metadata[stage.title()]['ResultSha256'],
                                 result['ReaderResult']['ResultSha256'])
                self.assertEqual(metadata[stage.title()]['CloseSha256'],
                                 result['ReaderResult']['CloseSha256'])
                self.assertEqual(metadata[stage.title()]['PlanSha256'],
                                 result['ReaderResult']['PlanSha256'])
                handoff = Path(temp)/'store'/stage/(run_id.replace('-', '')+'-published')
                self.assertTrue((handoff/'result.json').is_file())
                # Fixture roots are deliberately outside the effectful default
                # completion and one-use authorization stores.
                with self.assertRaises(ValueError):
                    completion.verify(handoff, Path(temp)/'next-auth'/stage/NEXT/
                        'authorization.json', stage)

    @unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0 and
                         os.environ.get('IGLOO_STAGE_CALLER_DOTNET') and
                         os.environ.get('IGLOO_STAGE_CALLER_HARNESS') and
                         os.environ.get('IGLOO_STAGE_CALLER_READER_EXPORT'),
                         'protected Linux runtime and complete .NET fixture required')
    def test_complete_reader_rejects_changed_ancestry_and_scope(self):
        export = Path(os.environ['IGLOO_STAGE_CALLER_READER_EXPORT'])
        runtime = Path(os.environ['IGLOO_STAGE_CALLER_DOTNET'])
        harness = Path(os.environ['IGLOO_STAGE_CALLER_HARNESS'])
        for stage, command in (('configuration', '--reopen-configured-predecessor'),
                               ('initramfs', '--reopen-initramfs-predecessor')):
            for fault in ('missing-import', 'wrong-import-close',
                          'wrong-source-manifest', 'unknown-import-outcome',
                          'wrong-close',
                          'wrong-generation', 'wrong-target', 'substituted-result',
                          'substituted-observation', 'unknown-outcome'):
                with self.subTest(stage=stage, fault=fault), \
                        tempfile.TemporaryDirectory(
                            dir='/var/lib/igloo/stage-reader-fixtures') as temp:
                    root = Path(temp)/'reader'
                    shutil.copytree(export/stage, root)
                    imported = root/('import' if stage == 'configuration' else
                                     'configured/import')
                    if fault == 'missing-import':
                        (imported/'storage.json').unlink()
                    elif fault == 'wrong-import-close':
                        generations = [p for p in (imported/'session').iterdir()
                                       if p.is_dir()]
                        self.assertEqual(1, len(generations))
                        terminal = sorted(generations[0].glob('*.json'))[-1]
                        value = json.loads(terminal.read_bytes())
                        value['ImportResultReference'] = '00000000-'+'0'*64+'.json'
                        terminal.write_text(json.dumps(value))
                    elif fault in ('wrong-source-manifest',
                                   'unknown-import-outcome'):
                        generations = [p for p in (imported/'import').iterdir()
                                       if p.is_dir()]
                        self.assertEqual(1, len(generations))
                        terminal = sorted(generations[0].glob('*.json'))[-1]
                        value = json.loads(terminal.read_bytes())
                        if fault == 'wrong-source-manifest':
                            value['ManifestSha256'] = '0'*64
                        else:
                            value['Outcome'] = 'OutcomeUnknown'
                        terminal.write_text(json.dumps(value))
                    elif fault == 'wrong-close':
                        binding = root/'binding.json'
                        value = json.loads(binding.read_bytes())
                        value['CloseSha256'] = '0'*64
                        binding.write_text(json.dumps(value))
                    elif fault in ('wrong-generation', 'wrong-target'):
                        auth = root/('derived-authorization.json' if
                            stage == 'configuration' else 'initramfs-authorization.json')
                        value = json.loads(auth.read_bytes())
                        value['GenerationId' if fault == 'wrong-generation' else
                              'TargetSha256'] = ('00000000-0000-4000-8000-000000000000'
                                                 if fault == 'wrong-generation' else '0'*64)
                        auth.write_text(json.dumps(value))
                    else:
                        generations = [p for p in (root/'effects').iterdir() if p.is_dir()]
                        self.assertEqual(1, len(generations))
                        records = sorted(generations[0].glob('*.json'))
                        self.assertTrue(records)
                        terminal = (records[1] if fault ==
                                    'substituted-observation' else records[-1])
                        if fault == 'substituted-result':
                            terminal.write_bytes(terminal.read_bytes()+b' ')
                        elif fault == 'substituted-observation':
                            value = json.loads(terminal.read_bytes())
                            value['Evidence']['ObserverEvidenceSha256'] = '0'*64
                            terminal.write_text(json.dumps(value))
                        else:
                            value = json.loads(terminal.read_bytes())
                            value['Outcome'] = 'OutcomeUnknown'
                            terminal.write_text(json.dumps(value))
                    with self.assertRaisesRegex(ValueError,
                                                'StageCallerLinuxReaderFailed'):
                        lifecycle._bounded_reader([str(runtime), str(harness),
                                                   command, str(root)], seconds=30)
        with tempfile.TemporaryDirectory(
                dir='/var/lib/igloo/stage-reader-fixtures') as temp:
            with self.assertRaisesRegex(ValueError, 'StageCallerLinuxReaderFailed'):
                lifecycle._bounded_reader([str(runtime), str(harness),
                    '--reopen-initramfs-predecessor',
                    str(export/'configuration')], seconds=30)

    @unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0 and
                         os.environ.get('IGLOO_STAGE_CALLER_DOTNET') and
                         os.environ.get('IGLOO_STAGE_CALLER_HARNESS') and
                         os.environ.get('IGLOO_STAGE_CALLER_READER_EXPORT'),
                         'protected Linux runtime and complete .NET fixture required')
    def test_complete_caller_blocks_publication_on_source_or_controller_failure(self):
        export = os.environ['IGLOO_STAGE_CALLER_READER_EXPORT']
        for stage in completion.STAGES:
            for fault in ('invalid-reader-ancestry', 'source-manifest-changed',
                          'controller'):
                with self.subTest(stage=stage, fault=fault), \
                        tempfile.TemporaryDirectory(
                            dir='/var/lib/igloo/stage-reader-fixtures') as temp:
                    program = ('import sys;sys.path.insert(0,sys.argv[1]);'
                               'from test_stage_lifecycle import fixture_child;'
                               'fixture_child(sys.argv[2],sys.argv[3],sys.argv[4],sys.argv[5])')
                    response = subprocess.run([sys.executable, '-B', '-c', program,
                        str(Path(__file__).parent), stage, temp, fault, export],
                        capture_output=True, text=True, timeout=60)
                    self.assertEqual(0, response.returncode,
                                     response.stderr+response.stdout)
                    result = json.loads(response.stdout.splitlines()[-1])
                    self.assertNotIn('publish', result['Events'])
                    self.assertIs(result['LinuxReaderDoubled'], False)

    @unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0,
                         'protected Linux request fixture required')
    def test_closed_stage_and_predecessor_kind_reject_before_allocation(self):
        with tempfile.TemporaryDirectory(dir='/root') as temp:
            request = Path(temp)/'request.json'
            value = dict.fromkeys(lifecycle.REQUEST_FIELDS)
            value.update(Version=1, Scope='InternalLabStageLifecycleV1',
                Stage='configuration', ReaderRuntimeSha256='A'*64,
                AcceptanceSha256='A'*64, Previous='/root/previous',
                Repo='/root/repo', Publish='/root/publish',
                SourceAuthorization='/root/auth/authorization.json',
                Pin='/root/pin.json', ReaderRuntime='/root/dotnet',
                NextAuthorization='/root/next/authorization.json',
                ConfigurationCheckpoint='/root/checkpoint',
                FailedConfiguration='/root/failed', Acceptance='/root/acceptance')
            for changed, code in (({'Stage': 'userdata'},
                                   'StageCallerRequestChanged'),
                                  ({'ConfiguredCompletion': '/root/wrong'},
                                   'StageCallerPredecessorKindChanged'),
                                  ({'Scope': 'ProductionStageLifecycleV1'},
                                   'StageCallerRequestChanged')):
                request.write_text(json.dumps({**value, **changed}))
                with self.assertRaisesRegex(ValueError, code):
                    lifecycle._request(request)
            self.assertEqual([request.name], [p.name for p in Path(temp).iterdir()])

    @unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0,
                         'protected Linux staged-file fixture required')
    def test_actual_prelaunch_closure_joins_frozen_host_guest_and_harness_bytes(self):
        with tempfile.TemporaryDirectory(dir='/root') as temp:
            base = Path(temp)
            repo = base/'repo'
            source = Path(__file__).parent
            native_source = source.parent.parent/'distros/debian/native'
            installer = repo/'tests/installer'; installer.mkdir(parents=True)
            native = repo/'distros/debian/native'; native.mkdir(parents=True)
            publish = base/'publish'; publish.mkdir()
            (publish/'CanonicalImportQualification.dll').write_bytes(b'fixture-only DLL')
            stage = 'configuration'
            names = acquisition.HOST_CLOSURE | lifecycle.storage.STAGE_GUEST_FILES[stage]
            hashes = {str(publish/'CanonicalImportQualification.dll'):
                lifecycle.storage.sha(publish/'CanonicalImportQualification.dll')}
            for name in names:
                is_native = name in ('userdata_contract.py', 'debian_first_boot.py',
                                     'isolation_policy.py')
                target = (native if is_native else installer)/name
                shutil.copyfile((native_source if is_native else source)/name, target)
                hashes[str(target)] = lifecycle.storage.sha(target)
            run = base/'runs'/SOURCE.replace('-', '')
            guest = run/'input/lab'; guest.mkdir(parents=True)
            staged_harness = run/'input/harness'; staged_harness.mkdir()
            shutil.copyfile(publish/'CanonicalImportQualification.dll',
                            staged_harness/'CanonicalImportQualification.dll')
            authority = base/'auth'/SOURCE/'authorization.json'
            tools = authority.parent/'completion-tools'; tools.mkdir(parents=True)
            guest_names = lifecycle.storage.STAGE_GUEST_FILES[stage]
            for name in guest_names:
                shutil.copyfile(installer/name, guest/name)
            for name in acquisition.HOST_CLOSURE:
                source_path = (native if name in ('userdata_contract.py',
                    'debian_first_boot.py', 'isolation_policy.py') else installer)/name
                shutil.copyfile(source_path, tools/name)
            guest_record = {'Version': 1, 'Stage': stage,
                'Entry': 'canonical_configuration_guest.py',
                'Files': {name: hashes[str(installer/name)] for name in guest_names}}
            checkpoint(authority.parent, 'guest-import-closure.json', guest_record)
            shutil.copyfile(authority.parent/'guest-import-closure.json',
                            guest/'guest-import-closure.json')
            checkpoint(authority.parent, 'completion-tools.json', {
                'Version': 1, 'Scope': 'ProtectedStageAcquisitionToolsV1',
                'Stage': stage,
                'Files': {name: hashes[str((native if name in
                    ('userdata_contract.py', 'debian_first_boot.py',
                     'isolation_policy.py') else installer)/name)]
                    for name in acquisition.HOST_CLOSURE}})
            (run/'tools.iso').write_bytes(b'fixture-only ISO identity')
            checkpoint(run, 'launch-intent.json', {'FixtureOnly': True})
            checkpoint(authority.parent, 'completion-launch.json', {
                'GuestImportClosureSha256': lifecycle.storage.sha(
                    authority.parent/'guest-import-closure.json'),
                'ReceiverManifestSha256': lifecycle.storage.sha(
                    authority.parent/'completion-tools.json'),
                'LaunchSha256': lifecycle.storage.sha(run/'launch-intent.json'),
                'ToolsSha256': lifecycle.storage.sha(run/'tools.iso')})
            identities = {'lab/'+name: lifecycle.storage.sha(guest/name)
                          for name in guest_names}
            identities['harness/CanonicalImportQualification.dll'] = \
                lifecycle.storage.sha(staged_harness/'CanonicalImportQualification.dll')
            checkpoint(run, 'input-identities.json', identities)
            value = {'Stage': stage, 'Repo': str(repo), 'Publish': str(publish)}
            with patch.object(lifecycle, '_source_files', return_value=hashes):
                lifecycle._verify_staged_closure(value, authority, run, hashes)
                changed = guest/'canonical_configuration_guest.py'
                changed.write_bytes(changed.read_bytes()+b'\n# substituted')
                with self.assertRaisesRegex(ValueError,
                                            'StageCallerGuestSourceChanged'):
                    lifecycle._verify_staged_closure(value, authority, run, hashes)
                shutil.copyfile(installer/changed.name, changed)
                changed = tools/'canonical_stage_acquisition.py'
                changed.write_bytes(changed.read_bytes()+b'\n# substituted')
                with self.assertRaisesRegex(ValueError,
                                            'StageCallerHostSourceChanged'):
                    lifecycle._verify_staged_closure(value, authority, run, hashes)
                shutil.copyfile(installer/changed.name, changed)
                changed = staged_harness/'CanonicalImportQualification.dll'
                changed.write_bytes(b'substituted DLL')
                with self.assertRaisesRegex(ValueError,
                                            'StageCallerHarnessSourceChanged'):
                    lifecycle._verify_staged_closure(value, authority, run, hashes)

    @unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0 and
                         os.environ.get('IGLOO_STAGE_CALLER_DOTNET') and
                         os.environ.get('IGLOO_STAGE_CALLER_HARNESS'),
                         'protected Linux runtime and current harness required')
    def test_actual_linux_reader_rejects_incomplete_new_process_fixture(self):
        from canonical_initramfs_lab import stage_predecessor_raw as configured
        from canonical_userdata_lab import stage_predecessor_raw as initramfs
        runtime = Path(os.environ['IGLOO_STAGE_CALLER_DOTNET'])
        harness = Path(os.environ['IGLOO_STAGE_CALLER_HARNESS'])
        completion.protected(runtime)
        completion.protected(harness)
        for stage, shape, command in (
                ('configuration', configured, '--reopen-configured-predecessor'),
                ('initramfs', initramfs, '--reopen-initramfs-predecessor')):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory(
                    dir='/var/lib/igloo') as temp:
                base = Path(temp)
                with contextlib.redirect_stdout(io.StringIO()):
                    fixture_child(stage, base)
                raw = base/'store'/stage/SOURCE.replace('-', '')/'raw'
                reader_input = base/'reader-input'
                shape(raw, reader_input)
                # This process fixture intentionally has only a proof marker
                # for its import ancestry. It cannot yield a canonical reader
                # success or successor authority.
                missing = (reader_input/'import/storage.json' if
                    stage == 'configuration' else
                    reader_input/'configured/import/storage.json')
                self.assertFalse(missing.exists())
                with self.assertRaisesRegex(ValueError,
                                            'StageCallerLinuxReaderFailed'):
                    lifecycle._bounded_reader([str(runtime), str(harness),
                        command, str(reader_input)], seconds=30)

    @unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0 and
                         os.environ.get('IGLOO_STAGE_CALLER_DOTNET') and
                         os.environ.get('IGLOO_STAGE_CALLER_HARNESS'),
                         'protected Linux runtime and current harness required')
    def test_actual_linux_reader_rejection_blocks_both_process_callers(self):
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                program = ('import sys;sys.path.insert(0,sys.argv[1]);'
                           'from test_stage_lifecycle import fixture_child;'
                           'fixture_child(sys.argv[2],sys.argv[3],'
                           '"actual-linux-reader-incomplete-ancestry")')
                response = subprocess.run([sys.executable, '-B', '-c', program,
                    str(Path(__file__).parent), stage, temp],
                    capture_output=True, text=True, timeout=30)
                self.assertEqual(0, response.returncode,
                                 response.stderr+response.stdout)
                result = json.loads(response.stdout.splitlines()[-1])
                self.assertEqual(stage, result['Stage'])
                self.assertNotIn('publish', result['Events'])

    @unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0,
                         'protected Linux host source stage required')
    def test_actual_isolated_host_entrypoint_rejects_before_effects(self):
        with tempfile.TemporaryDirectory(dir='/root') as temp:
            source = Path(__file__).parent
            stage = Path(temp)/'repo/tests/installer'
            stage.mkdir(parents=True)
            for path in source.glob('*.py'):
                shutil.copyfile(path, stage/path.name)
            native = source.parent.parent/'distros/debian/native'
            for name in ('userdata_contract.py', 'debian_first_boot.py',
                         'isolation_policy.py'):
                shutil.copyfile(native/name, stage/name)
            entry = stage/'canonical_stage_lifecycle.py'
            self.assertEqual(entry.read_bytes(),
                             (source/'canonical_stage_lifecycle.py').read_bytes())
            response = subprocess.run([sys.executable, '-I', '-B', str(entry),
                'run', str(Path(temp)/'absent-request.json')],
                capture_output=True, text=True, timeout=10)
            self.assertEqual(2, response.returncode)
            self.assertEqual('Stopped', json.loads(response.stdout)['State'])
            self.assertFalse((Path(temp)/'absent-request.json').exists())

    def test_read_only_linux_subprocess_output_and_lifetime_are_bounded(self):
        self.assertEqual(b'{"Ready":true}\n', lifecycle._bounded_reader(
            [sys.executable, '-c', 'print("{\\"Ready\\":true}")']))
        with self.assertRaisesRegex(ValueError, 'StageCallerLinuxReaderOutputBound'):
            lifecycle._bounded_reader([sys.executable, '-c',
                'import os;os.write(1,b"A"*8193)'])
        with self.assertRaisesRegex(ValueError, 'StageCallerLinuxReaderTimeout'):
            lifecycle._bounded_reader([sys.executable, '-c',
                'import time;time.sleep(2)'], seconds=.05)
        with self.assertRaisesRegex(ValueError, 'StageCallerLinuxReaderFailed'):
            lifecycle._bounded_reader([sys.executable, '-c',
                'raise SystemExit(2)'])

    def test_preparation_acknowledgement_is_bounded_and_ordered(self):
        challenge, binding = 'A'*64, 'B'*64
        ready = b'IGLOO_STAGE_PREPARED '+challenge.encode()+b' '+binding.encode()+b'\r\n'
        valid = FragmentChannel(b'echo\r\n'+ready+b'IGLOO_PREPARE_EXIT:0\n', 1, .001)
        self.assertTrue(acquisition._preparation_status(valid, challenge, binding,
            clock=lambda: valid.now)['Acknowledged'])
        for wire, code in ((b'IGLOO_PREPARE_EXIT:0\n'+ready,
                            'StagePreparationAcknowledgementMissing'),
                           (ready+ready+b'IGLOO_PREPARE_EXIT:0\n',
                            'StagePreparationDuplicateAcknowledgement'),
                           (ready+b'IGLOO_PREPARE_EXIT:1\n',
                            'StagePreparationCommandFailed'),
                           (ready, 'StagePreparationTimeout')):
            channel = FragmentChannel(wire, 7, .001)
            with self.subTest(code=code), self.assertRaisesRegex(ValueError, code):
                acquisition._preparation_status(channel, challenge, binding,
                    deadline_seconds=.5 if code == 'StagePreparationTimeout' else 10,
                    clock=lambda: channel.now)

    def test_actual_main_process_configuration_and_initramfs_fixture_joins(self):
        for stage in completion.STAGES:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as temp:
                program = ('import sys;sys.path.insert(0,sys.argv[1]);'
                           'from test_stage_lifecycle import fixture_child;'
                           'fixture_child(sys.argv[2],sys.argv[3])')
                response = subprocess.run([sys.executable, '-B', '-c', program,
                    str(Path(__file__).parent), stage, temp],
                    capture_output=True, text=True, timeout=30)
                self.assertEqual(0, response.returncode, response.stderr+response.stdout)
                result = json.loads(response.stdout.splitlines()[-1])
                self.assertEqual(stage, result['Stage'])
                self.assertTrue(result['FixtureOnly'])

    def test_caller_failure_and_response_loss_process_boundaries(self):
        for stage, fault in (('configuration', 'cancel-preparation'),
                             ('configuration', 'provision-before-launch'),
                             ('initramfs', 'staged-closure'),
                             ('configuration', 'provision-after-launch'),
                             ('configuration', 'launch-observation'),
                             ('configuration', 'backing-observation'),
                             ('initramfs', 'guest-preparation'),
                             ('configuration', 'capture'),
                             ('initramfs', 'reader'),
                             ('configuration', 'reader-record-changed'),
                             ('initramfs', 'controller'),
                             ('configuration', 'lost-controller-response'),
                             ('configuration', 'publication'),
                             ('initramfs', 'missing-next-authorization'),
                             ('initramfs', 'lost-publication-response')):
            with self.subTest(stage=stage, fault=fault), \
                 tempfile.TemporaryDirectory() as temp:
                program = ('import sys;sys.path.insert(0,sys.argv[1]);'
                           'from test_stage_lifecycle import fixture_child;'
                           'fixture_child(sys.argv[2],sys.argv[3],sys.argv[4])')
                response = subprocess.run([sys.executable, '-B', '-c', program,
                    str(Path(__file__).parent), stage, temp, fault],
                    capture_output=True, text=True, timeout=30)
                self.assertEqual(0, response.returncode, response.stderr+response.stdout)


if __name__ == '__main__':
    unittest.main()
