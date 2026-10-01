"""Nonprivileged failure fixtures; these do not certify VM or disk ownership."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import canonical_lab_runtime as runtime
import canonical_lab_guest_build as build
import canonical_lab_guest_storage as storage


class CanonicalLabRuntimeTests(unittest.TestCase):
    def test_checkpoint_is_create_new_and_independently_reopened(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime.checkpoint(root, 'intent.json', {'Scope': 'Fixture'})
            original = (root / 'intent.json').read_bytes()
            self.assertEqual(json.loads(original), {'Scope': 'Fixture'})
            with self.assertRaises(FileExistsError):
                runtime.checkpoint(root, 'intent.json', {'Scope': 'Replayed'})
            self.assertEqual((root / 'intent.json').read_bytes(), original)

    def test_fsync_failure_is_not_publication_success(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(runtime.os, 'fsync', side_effect=OSError('injected')):
                with self.assertRaises(OSError):
                    runtime.checkpoint(Path(directory), 'intent.json', {'Intent': True})
            self.assertTrue((Path(directory) / 'intent.json').exists())

    def test_creation_rejects_nonprivileged_host_before_effects(self):
        with patch.object(runtime.os, 'geteuid', return_value=1000), patch.object(runtime.subprocess, 'run') as effect:
            with self.assertRaises(PermissionError):
                runtime.create(Path('/fixture'), Path('/fixture'))
            effect.assert_not_called()

    def test_unknown_phase_rejected_before_native_observation(self):
        with self.assertRaisesRegex(ValueError, 'UnknownRuntimePhase'):
            runtime.observe_gpt(Path('/fixture'), 'production')

    def test_guest_preconditions_are_not_optimization_dependent_assertions(self):
        for module in (build, storage):
            with self.assertRaisesRegex(ValueError, 'fixture'):
                module.require(False, 'fixture')
            self.assertNotIn('assert ', Path(module.__file__).read_text())


if __name__ == '__main__': unittest.main()
