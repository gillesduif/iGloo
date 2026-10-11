"""Small host-side custody regressions; fixture records grant no authority."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import canonical_storage_lab
import canonical_agent_stage_lab
from canonical_userdata_completion import _files, digest, publish_from_protected_verifier, verify
from canonical_userdata_readback import verify as verify_terminal


class CompletionCustodyTests(unittest.TestCase):
    def setUp(self):
        if os.geteuid() != 0 or not Path('/var/lib/igloo').is_dir():
            self.skipTest('isolated root-owned lab workspace required')
        self.temporary = tempfile.TemporaryDirectory(prefix='userdata-completion-test-', dir='/var/lib/igloo')
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.auth = self.base / 'authorization.json'
        self.auth.write_text(json.dumps({'Version': 1, 'Scope': 'OneUserDataCheckpointOfflineAgentStage',
            'AttemptId': 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
            'OperationId': 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
            'GenerationId': 'cccccccc-cccc-cccc-cccc-cccccccccccc',
            'UserDataSha256': 'A'*64, 'CloseSha256': 'B'*64,
            'RetentionSha256': 'C'*64, 'TargetSha256': 'D'*64,
            'JournalSha256': 'E'*64}))

    def test_root_owned_diagnostic_copy_without_independent_anchor_rejected(self):
        copied = self.base / 'diagnostic-copy'; copied.mkdir()
        with self.assertRaises((FileNotFoundError, ValueError)):
            verify(copied, self.auth)
        harness = os.environ.get('IGLOO_AGENT_HARNESS')
        runtime = os.environ.get('IGLOO_AGENT_DOTNET_RUNTIME')
        if harness and runtime:
            process = subprocess.run([runtime, harness, '--agent-stage-completion-readiness',
                                      str(copied), str(self.auth)], capture_output=True, timeout=30, check=False)
            self.assertEqual(2, process.returncode)
            self.assertEqual('AgentStageTrustedCompletionUnavailable', json.loads(process.stdout)['Code'])

    def test_untrusted_anchor_and_terminal_substitution_rejected(self):
        copied = self.base / 'diagnostic-copy'; copied.mkdir()
        (copied / 'anchor.json').write_text(json.dumps({'Version': 1,
            'Scope': 'TrustedUserDataTerminalV1', 'SourceRunId': 'dddddddd-dddd-dddd-dddd-dddddddddddd',
            'GenerationId': 'cccccccc-cccc-cccc-cccc-cccccccccccc',
            'OperationId': 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
            'SessionId': 'ffffffff-ffff-ffff-ffff-ffffffffffff',
            'ResultSha256': '0'*64, 'CloseSha256': 'B'*64,
            'RetentionSha256': 'C'*64, 'TargetSha256': 'D'*64, 'JournalSha256': 'E'*64,
            'Files': {}}))
        (copied / 'result.json').write_text('{}')
        with self.assertRaisesRegex(ValueError, 'CompletionExternalAnchorChanged'):
            verify(copied, self.auth)

    def test_provisioner_refuses_missing_handoff_before_run_or_reservation(self):
        predecessor = self.base / 'predecessor'; predecessor.mkdir()
        copied = self.base / 'diagnostic-copy'; copied.mkdir()
        with patch.object(canonical_storage_lab, 'require_backing_capacity') as capacity:
            with self.assertRaises((FileNotFoundError, ValueError)):
                canonical_storage_lab.create(predecessor, self.base, self.base,
                    agent_stage_authorization=self.auth, userdata_predecessor=predecessor,
                    userdata_completion=copied, configuration_pin=self.auth)
            self.assertEqual(180 * 1024**3, capacity.call_args_list[0].args[0])
        self.assertEqual({'predecessor', 'diagnostic-copy', 'authorization.json'},
                         {p.name for p in self.base.iterdir()})

    def test_rejected_original_host_export_cannot_be_published(self):
        source = Path('/var/lib/igloo/initramfs-helper-labs/a1b883f4179f4846b16b9a91fb4bcf9d')
        if not source.exists():
            self.skipTest('retained export unavailable')
        with self.assertRaisesRegex(ValueError, 'CompletionPathUnprotected'):
            publish_from_protected_verifier(source, self.auth, self.auth, self.base / 'published')
        self.assertFalse((self.base / 'published').exists())

    def test_fixture_only_real_record_serializer_reader_and_substitution(self):
        original = Path(os.environ.get('IGLOO_USERDATA_UNTRUSTED_EXPORT', '/nonexistent'))
        if not original.exists():
            self.skipTest('retained raw record fixture not nominated')
        source = self.base / 'fixture-source'; source.mkdir()
        # This source and its anchor are TEST FIXTURES derived from an export.
        # They cannot prove the export's custody or authorize a canonical run.
        branches = ('input/initramfs-predecessor', 'guest-evidence/journals')
        for branch in branches:
            shutil.copytree(original / branch, source / branch)
        for name in ('userdata-retention.json', 'userdata-derivation.json',
                     'userdata-independent-host-readback.json', 'input/userdata-authorization.json',
                     'guest-evidence/execution/host-binding.json',
                     'guest-evidence/execution/inventory.json',
                     'guest-evidence/execution/userdata.stdout'):
            target = source / name; target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(original / name, target)
        raw = (source / 'guest-evidence/execution/userdata.stdout').read_bytes().splitlines()[0]
        preflight = json.loads(raw)
        terminal = verify_terminal(source / 'guest-evidence/execution', source / 'guest-evidence/journals')
        retained = json.loads((source / 'userdata-retention.json').read_bytes())
        self.assertEqual('AppliedAndVerified', terminal['Teardown'])
        a = json.loads(self.auth.read_bytes())
        a.update(GenerationId=preflight['Plan']['Provenance']['GenerationId'],
                 UserDataSha256=terminal['EffectRecords'][-1]['Sha256'],
                 CloseSha256=terminal['SessionRecords'][-1]['Sha256'],
                 RetentionSha256=digest((source / 'userdata-retention.json').read_bytes()),
                 TargetSha256=retained['Backings']['target.raw']['Sha256'],
                 JournalSha256=retained['Backings']['journal.raw']['Sha256'])
        self.auth.write_text(json.dumps(a))
        anchor = {'Version': 1, 'Scope': 'TrustedUserDataTerminalV1',
            'SourceRunId': retained['RunId'], 'GenerationId': a['GenerationId'],
            'OperationId': terminal['OperationId'], 'SessionId': terminal['SessionId'],
            'ResultSha256': a['UserDataSha256'], 'CloseSha256': a['CloseSha256'],
            'RetentionSha256': a['RetentionSha256'], 'TargetSha256': a['TargetSha256'],
            'JournalSha256': a['JournalSha256'],
            'Files': {name: digest((source / name).read_bytes()) for name in _files(source)}}
        anchor_path = self.base / 'synthetic-anchor.json'
        anchor_path.write_text(json.dumps(anchor))
        published = self.base / 'published'
        result = publish_from_protected_verifier(source, anchor_path, self.auth, published)
        self.assertEqual(a['UserDataSha256'], result['ResultSha256'])
        staged = self.base / 'fixture-tools-input'
        with self.assertRaisesRegex(ValueError, 'CompletionExternalAnchorChanged'):
            canonical_agent_stage_lab.stage_predecessor(published, self.auth, staged)
        self.assertFalse(staged.exists())
        wrong_auth = self.base / 'wrong-target-authorization.json'
        wrong_auth.write_text(json.dumps({**a, 'TargetSha256': '0'*64}))
        with self.assertRaisesRegex(ValueError, 'CompletionExternalAnchorChanged'):
            verify(published, wrong_auth)
        incomplete = self.base / 'interrupted-publication'; incomplete.mkdir()
        shutil.copyfile(published / 'anchor.json', incomplete / 'anchor.json')
        with self.assertRaises(FileNotFoundError):
            verify(incomplete, self.auth)
        harness = os.environ.get('IGLOO_AGENT_HARNESS')
        runtime = os.environ.get('IGLOO_AGENT_DOTNET_RUNTIME')
        if harness and runtime:
            process = subprocess.run([runtime, harness, '--agent-stage-completion-readiness',
                                      str(published), str(self.auth)], capture_output=True, timeout=90, check=False)
            self.assertEqual(0, process.returncode, process.stdout[:1024])
            self.assertEqual('Passed', json.loads(process.stdout)['Readiness'])
            self.assertFalse(json.loads(process.stdout)['EffectAuthorization'])
            refused = subprocess.run([runtime, harness, '--agent-stage-derived-lab',
                str(published), 'missing-declaration', str(self.auth), 'missing-runtime',
                'missing-journal', 'missing-pin'], capture_output=True, timeout=90, check=False)
            self.assertEqual(2, refused.returncode)
            denial = json.loads(refused.stdout)
            self.assertEqual('ProtectedPredecessor', denial['Boundary'])
            self.assertIs(False, denial['CanonicalResultPublished'])
        predecessor = self.base / retained['RunId'].replace('-', ''); predecessor.mkdir()
        for name in ('target.raw', 'journal.raw'):
            (predecessor / name).write_bytes(b'fixture-only-backing')
        def fixture_identity(path):
            entry = retained['Backings'][Path(path).name]
            return entry['Device'], entry['Inode'], entry['Length']
        def fixture_hash(path):
            return retained['Backings'][Path(path).name]['Sha256']
        with patch.object(canonical_agent_stage_lab, 'closed'), \
             patch.object(canonical_agent_stage_lab, 'identity', side_effect=fixture_identity), \
             patch.object(canonical_agent_stage_lab, 'sha', side_effect=fixture_hash), \
             patch.object(canonical_storage_lab, 'require_backing_capacity'):
            with self.assertRaisesRegex(ValueError, 'CompletionExternalAnchorChanged'):
                canonical_agent_stage_lab.preflight(self.auth, predecessor, published)
            with self.assertRaisesRegex(ValueError, 'CompletionExternalAnchorChanged'):
                canonical_storage_lab.create(predecessor, self.base, self.base,
                    agent_stage_authorization=self.auth, userdata_predecessor=predecessor,
                    userdata_completion=published, configuration_pin=self.auth)
        self.assertFalse((self.base / a['AttemptId'].replace('-', '')).exists())
        item = published / 'raw' / 'guest-evidence' / 'journals' / 'session' / a['GenerationId']
        close = sorted(item.glob('00000017-*.json'))[0]
        close.write_bytes(close.read_bytes() + b' ')
        with self.assertRaisesRegex(ValueError, 'CompletionRawBytesChanged'):
            verify(published, self.auth)
        close.unlink()
        with self.assertRaisesRegex(ValueError, 'CompletionInventoryChanged'):
            verify(published, self.auth)


if __name__ == '__main__':
    unittest.main()
