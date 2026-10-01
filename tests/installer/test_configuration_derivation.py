"""Small copy fixtures, not canonical backing qualification."""
import hashlib
from pathlib import Path
import tempfile
import unittest

from canonical_configuration_derivation import copy_verified
from canonical_lab_runtime import checkpoint


class ConfigurationDerivationTests(unittest.TestCase):
    def test_host_attempt_reservation_cannot_be_recreated(self):
        with tempfile.TemporaryDirectory() as directory:
            checkpoint(Path(directory), 'reservation.json', {'AttemptId': 'fixture', 'OperationId': 'first'})
            before = (Path(directory)/'reservation.json').read_bytes()
            with self.assertRaises(FileExistsError):
                checkpoint(Path(directory), 'reservation.json', {'AttemptId': 'fixture', 'OperationId': 'second'})
            self.assertEqual(before, (Path(directory)/'reservation.json').read_bytes())

    def test_independent_copy_reopens_and_cannot_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)/'source'; target = Path(directory)/'target'
            source.write_bytes(b'checkpoint fixture\0' * 1000)
            sha = hashlib.sha256(source.read_bytes()).hexdigest().upper()
            before, after = copy_verified(source, target, sha)
            self.assertNotEqual(before[:2], after[:2])
            self.assertEqual(source.read_bytes(), target.read_bytes())
            with self.assertRaises(ValueError): copy_verified(source, target, sha)

    def test_changed_source_and_preexisting_hardlink_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)/'source'; target = Path(directory)/'target'
            source.write_bytes(b'changed')
            with self.assertRaises(ValueError): copy_verified(source, target, '0'*64)
            target.hardlink_to(source)
            with self.assertRaises(ValueError): copy_verified(source, target, hashlib.sha256(b'changed').hexdigest().upper())
