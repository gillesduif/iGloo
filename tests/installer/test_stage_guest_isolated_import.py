"""Small root-owned process fixtures for staged, isolated guest imports.

These do not run a stage, a QEMU guest, or a canonical operation.
"""
import json
import hashlib
import os
from pathlib import Path
import pty
import select
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest

import canonical_configuration_guest as configuration_guest
import canonical_initramfs_guest as initramfs_guest
from canonical_storage_lab import (STAGE_GUEST_FILES, stage_guest_import_closure,
                                   stage_guest_manifest_from_iso)
from canonical_userdata_acquisition import (STAGE_ENTRY_BOOTSTRAP,
                                            stage_entry_command)


SOURCE = Path(__file__).resolve().parent
CHALLENGE = 'A' * 64
GUESTS = {'configuration': configuration_guest, 'initramfs': initramfs_guest}


@unittest.skipUnless(sys.platform.startswith('linux') and os.geteuid() == 0,
                     'Protected Linux root-owned fixture required')
class StageGuestIsolatedImportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir='/root', prefix='igloo-guest-import-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def stage(self, stage):
        lab = self.root/stage/'tools'/'lab'
        lab.mkdir(parents=True)
        for name in STAGE_GUEST_FILES[stage]:
            shutil.copyfile(SOURCE/name, lab/name)
        stage_guest_import_closure(lab, stage)
        return lab

    def run_guest(self, lab, stage, *args):
        fake = self.root/'untrusted'; fake.mkdir(exist_ok=True)
        (fake/'canonical_storage_guest.py').write_text("raise RuntimeError('PYTHONPATH_USED')\n")
        environment = {'PATH': '/usr/bin:/bin', 'PYTHONPATH': str(fake)}
        return subprocess.run(['/usr/bin/python3', '-I', '-B',
            str(lab/f'canonical_{stage}_guest.py'), *args],
            cwd='/root', env=environment, capture_output=True, text=True, timeout=5)

    def fixture_command(self, lab, stage, expected):
        command = stage_entry_command(stage, CHALLENGE, expected)
        self.assertGreater(len(command.encode()), 4096)
        self.assertLessEqual(max(len(line.encode()) for line in command.splitlines()), 4000)
        fixed = shlex.split(command.split('; echo ')[0])
        self.assertEqual(['python3', '-I', '-B', '-c'], fixed[:4])
        self.assertEqual('/opt/igloo-lab/lab/canonical_'+stage+'_guest.py', fixed[5])
        self.assertEqual([stage, CHALLENGE, expected], fixed[6:])
        self.assertEqual(STAGE_ENTRY_BOOTSTRAP, fixed[4])
        # Substitute only the fixed fixture entry path. The shell receives
        # the real generated multi-line command and its exact -c code.
        return command.replace(fixed[5],
            shlex.quote(str(lab/f'canonical_{stage}_guest.py')))

    def run_bootstrap(self, lab, stage, expected):
        fake = self.root/'untrusted'; fake.mkdir(exist_ok=True)
        (fake/'canonical_storage_guest.py').write_text("raise RuntimeError('PYTHONPATH_USED')\n")
        command = self.fixture_command(lab, stage, expected)
        return subprocess.run(['/bin/sh', '-c', command], cwd='/root',
            env={'PATH': '/usr/bin:/bin', 'PYTHONPATH': str(fake)},
            capture_output=True, text=True, timeout=5)

    def test_both_actual_entrypoints_join_host_nominated_digest_before_readiness(self):
        for stage in STAGE_GUEST_FILES:
            with self.subTest(stage=stage):
                lab = self.stage(stage)
                expected = hashlib.sha256((lab/'guest-import-closure.json').read_bytes()).hexdigest().upper()
                result = self.run_bootstrap(lab, stage, expected)
                self.assertIn('IGLOO_CAPTURE_EXIT:1', result.stdout)
                self.assertIn('FileNotFoundError', result.stderr)
                self.assertIn('/opt/igloo-lab/', result.stderr)
                self.assertNotIn('ModuleNotFoundError', result.stderr)
                self.assertNotIn('PYTHONPATH_USED', result.stderr)
                changed = lab/'canonical_stage_guest.py'
                changed.write_bytes(changed.read_bytes() + b'\n# root-owned copy\n')
                manifest = lab/'guest-import-closure.json'
                declaration = json.loads(manifest.read_bytes())
                declaration['Files'][changed.name] = hashlib.sha256(changed.read_bytes()).hexdigest().upper()
                manifest.write_text(json.dumps(declaration))
                copied = self.run_bootstrap(lab, stage, expected)
                self.assertIn('StageGuestBootstrapExpectationChanged', copied.stderr)
                self.assertNotIn('FileNotFoundError', copied.stderr)
                self.assertFalse((self.root/('core-configuration-'+CHALLENGE)).exists())
                self.assertFalse((self.root/('initramfs-'+CHALLENGE)).exists())

    def test_review_argument_is_unsupported_before_any_local_import(self):
        for stage in STAGE_GUEST_FILES:
            with self.subTest(stage=stage):
                lab = self.root/stage/'tools'/'lab'; lab.mkdir(parents=True)
                shutil.copyfile(SOURCE/f'canonical_{stage}_guest.py',
                                lab/f'canonical_{stage}_guest.py')
                result = self.run_guest(lab, stage, '--review-import-only')
                self.assertIn(f'{stage.title()}GuestCommandUnsupported', result.stderr)
                self.assertNotIn('ModuleNotFoundError', result.stderr)

    def test_substituted_missing_and_unsafe_closure_rejected_by_file_validator(self):
        for stage in STAGE_GUEST_FILES:
            with self.subTest(stage=stage):
                lab = self.stage(stage)
                expected = hashlib.sha256((lab/'guest-import-closure.json').read_bytes()).hexdigest().upper()
                GUESTS[stage]._verify_staged_files(lab/f'canonical_{stage}_guest.py', expected)
                helper = lab/'canonical_stage_guest.py'
                helper.write_bytes(helper.read_bytes() + b'\n# substitute\n')
                with self.assertRaisesRegex(ValueError, f'{stage.title()}GuestImportHashChanged'):
                    GUESTS[stage]._verify_staged_files(lab/f'canonical_{stage}_guest.py', expected)

                helper.unlink()
                helper.symlink_to(SOURCE/'canonical_stage_guest.py')
                with self.assertRaisesRegex(ValueError, f'{stage.title()}GuestImportClosureUnprotected'):
                    GUESTS[stage]._verify_staged_files(lab/f'canonical_{stage}_guest.py', expected)

                helper.unlink()
                shutil.copyfile(SOURCE/'canonical_stage_guest.py', helper)
                manifest = lab/'guest-import-closure.json'
                manifest.unlink()
                with self.assertRaisesRegex(ValueError, f'{stage.title()}GuestImportClosureUnprotected'):
                    GUESTS[stage]._verify_staged_files(lab/f'canonical_{stage}_guest.py', expected)

    def test_wrong_stage_extra_file_and_writable_parent_rejected(self):
        for stage in STAGE_GUEST_FILES:
            with self.subTest(stage=stage):
                lab = self.stage(stage)
                manifest = lab/'guest-import-closure.json'
                data = json.loads(manifest.read_bytes()); data['Stage'] = 'other'
                manifest.write_text(json.dumps(data))
                expected = hashlib.sha256(manifest.read_bytes()).hexdigest().upper()
                with self.assertRaisesRegex(ValueError, f'{stage.title()}GuestImportManifestChanged'):
                    GUESTS[stage]._verify_staged_files(lab/f'canonical_{stage}_guest.py', expected)

                data['Stage'] = stage; manifest.write_text(json.dumps(data))
                expected = hashlib.sha256(manifest.read_bytes()).hexdigest().upper()
                (lab/'unexpected.py').write_text('pass\n')
                with self.assertRaisesRegex(ValueError, f'{stage.title()}GuestImportManifestChanged'):
                    GUESTS[stage]._verify_staged_files(lab/f'canonical_{stage}_guest.py', expected)
                (lab/'unexpected.py').unlink()

                lab.chmod(0o777)
                with self.assertRaisesRegex(ValueError, f'{stage.title()}GuestImportParentUnprotected'):
                    GUESTS[stage]._verify_staged_files(lab/f'canonical_{stage}_guest.py', expected)
                lab.chmod(0o755)

    def test_host_packager_rejects_nonclosed_script_set(self):
        lab = self.root/'tools'/'lab'; lab.mkdir(parents=True)
        for name in STAGE_GUEST_FILES['configuration']:
            shutil.copyfile(SOURCE/name, lab/name)
        (lab/'surprise.py').write_text('pass\n')
        with self.assertRaisesRegex(ValueError, 'StageGuestImportClosureChanged'):
            stage_guest_import_closure(lab, 'configuration')
        self.assertFalse((lab/'guest-import-closure.json').exists())

    @unittest.skipUnless(shutil.which('xorriso'), 'Existing ISO packer required')
    def test_actual_tools_iso_contains_exact_closed_stage_and_runs_after_extraction(self):
        for stage in STAGE_GUEST_FILES:
            with self.subTest(stage=stage):
                lab = self.stage(stage)
                iso = self.root/stage/'tools.iso'
                subprocess.run(['xorriso', '-as', 'mkisofs', '-R',
                    '-V', 'IGLOO_STORAGE_TOOLS', '-o', str(iso), str(lab.parent)],
                    check=True, capture_output=True, timeout=10)
                nominated = hashlib.sha256(iso.read_bytes()).hexdigest().upper()
                self.assertEqual(64, len(nominated))
                expected = hashlib.sha256((lab/'guest-import-closure.json').read_bytes()).hexdigest().upper()
                protected_copy = self.root/stage/'protected-guest-import-closure.json'
                self.assertEqual(expected, stage_guest_manifest_from_iso(
                    iso, protected_copy, expected))
                self.assertEqual((lab/'guest-import-closure.json').read_bytes(),
                                 protected_copy.read_bytes())
                extracted = self.root/stage/'extracted'/'lab'
                extracted.parent.mkdir()
                subprocess.run(['xorriso', '-osirrox', 'on', '-indev', str(iso),
                    '-extract', '/lab', str(extracted)],
                    check=True, capture_output=True, timeout=10)
                self.assertEqual({p.name for p in lab.iterdir()},
                                 {p.name for p in extracted.iterdir()})
                for name in STAGE_GUEST_FILES[stage] | {'guest-import-closure.json'}:
                    self.assertEqual((lab/name).read_bytes(), (extracted/name).read_bytes())
                GUESTS[stage]._verify_staged_files(extracted/f'canonical_{stage}_guest.py', expected)
                result = self.run_bootstrap(extracted, stage, expected)
                self.assertIn('FileNotFoundError', result.stderr)
                self.assertNotIn('ModuleNotFoundError', result.stderr)

    def test_missing_or_malformed_host_expectation_rejected(self):
        for stage in STAGE_GUEST_FILES:
            with self.subTest(stage=stage):
                lab = self.stage(stage)
                with self.assertRaisesRegex(ValueError,
                                            'CaptureGuestImportExpectationInvalid'):
                    stage_entry_command(stage, CHALLENGE, 'not-a-hash')
                wrong = self.run_bootstrap(lab, stage, '0'*64)
                self.assertIn('StageGuestBootstrapExpectationChanged', wrong.stderr)
                self.assertNotIn('FileNotFoundError', wrong.stderr)

    def test_modified_entry_top_level_effect_is_rejected_before_execution(self):
        for stage in STAGE_GUEST_FILES:
            with self.subTest(stage=stage):
                lab = self.stage(stage)
                manifest = lab/'guest-import-closure.json'
                expected = hashlib.sha256(manifest.read_bytes()).hexdigest().upper()
                entry = lab/f'canonical_{stage}_guest.py'
                marker = self.root/stage/'unexpected-effect'
                entry.write_bytes((f"from pathlib import Path\nPath({str(marker)!r}).write_text('effect')\n"
                                   ).encode()+entry.read_bytes())
                unchanged_manifest = self.run_bootstrap(lab, stage, expected)
                self.assertIn('StageGuestBootstrapFileHashChanged',
                              unchanged_manifest.stderr)
                self.assertFalse(marker.exists())
                declaration = json.loads(manifest.read_bytes())
                declaration['Files'][entry.name] = hashlib.sha256(entry.read_bytes()).hexdigest().upper()
                manifest.write_text(json.dumps(declaration))
                rehashed_copy = self.run_bootstrap(lab, stage, expected)
                self.assertIn('StageGuestBootstrapExpectationChanged',
                              rehashed_copy.stderr)
                self.assertFalse(marker.exists())

    def test_generated_multiline_command_on_real_canonical_pty(self):
        for stage in STAGE_GUEST_FILES:
            with self.subTest(stage=stage):
                lab = self.stage(stage)
                manifest = lab/'guest-import-closure.json'
                expected = hashlib.sha256(manifest.read_bytes()).hexdigest().upper()
                entry = lab/f'canonical_{stage}_guest.py'
                marker = self.root/stage/'pty-effect'
                entry.write_bytes((f"from pathlib import Path\nPath({str(marker)!r}).write_text('effect')\n"
                                   ).encode()+entry.read_bytes())
                declaration = json.loads(manifest.read_bytes())
                declaration['Files'][entry.name] = hashlib.sha256(entry.read_bytes()).hexdigest().upper()
                manifest.write_text(json.dumps(declaration))
                command = self.fixture_command(lab, stage, expected).encode()
                master, slave = pty.openpty()
                shell = subprocess.Popen(['/bin/sh', '-i'], stdin=slave,
                    stdout=slave, stderr=slave, cwd='/root', start_new_session=True)
                os.close(slave)
                written = []

                def send():
                    offset = 0
                    while offset < len(command):
                        offset += os.write(master, command[offset:])
                    written.append(offset)

                sender = threading.Thread(target=send, daemon=True)
                sender.start()
                observed = bytearray()
                try:
                    deadline = time.monotonic()+10
                    while b'IGLOO_CAPTURE_EXIT:1' not in observed and \
                            time.monotonic() < deadline and len(observed) < 64 * 1024:
                        if select.select([master], [], [], 0.2)[0]:
                            observed.extend(os.read(master, 4096))
                    sender.join(2)
                    self.assertEqual([len(command)], written)
                    self.assertIn(b'python3 -I -B -c', observed)
                    self.assertIn(b'IGLOO_CAPTURE_EXIT:1', observed)
                    self.assertIn(b'StageGuestBootstrapExpectationChanged', observed)
                    self.assertFalse(marker.exists())
                finally:
                    try: os.write(master, b'exit\n')
                    except OSError: pass
                    try: shell.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        shell.terminate(); shell.wait(timeout=2)
                    os.close(master)


if __name__ == '__main__':
    unittest.main()
