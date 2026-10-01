"""Small create-new file fixtures. No generator, block device or mount effects."""
import copy
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'distros/debian/native'))
import initramfs_publication as publication
import session_initramfs as session


@unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'Linux root-owned ordinary-file fixture')
class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.base = Path(self.temp.name)
        (self.base/'root/boot').mkdir(parents=True); (self.base/'root/boot').chmod(0o755)
        (self.base/'candidate').write_bytes(b'bounded-candidate-fixture'); (self.base/'candidate').chmod(0o600)
        self.root = os.open(self.base/'root', os.O_RDONLY | os.O_DIRECTORY)
        self.image = os.open(self.base/'candidate', os.O_RDONLY)
        self.intent = publication.declaration(self.root, self.image)
    def tearDown(self):
        os.close(self.root); os.close(self.image); self.temp.cleanup()
    def publish(self, intent=None, validate=lambda: None):
        return publication.publish(self.root, self.image, intent or self.intent, validate)
    def test_create_new_reopen_and_duplicate_rejection(self):
        result = self.publish()
        self.assertEqual(result['Sha256'], self.intent['Candidate']['Sha256'])
        self.assertEqual((self.base/'root/boot'/publication.LEAF).stat().st_mode & 0o777, 0o600)
        with self.assertRaisesRegex(ValueError, 'DestinationExists'): self.publish()
    def test_existing_file_or_link_never_overwritten(self):
        for link in (False, True):
            with self.subTest(link=link):
                dest = self.base/'root/boot'/publication.LEAF
                if link: dest.symlink_to(self.base/'candidate')
                else: dest.write_bytes(b'preserved')
                with self.assertRaisesRegex(ValueError, 'DestinationExists'): self.publish()
                dest.unlink()
        self.assertEqual((self.base/'candidate').read_bytes(), b'bounded-candidate-fixture')
    def test_candidate_substitution_is_rejected_before_write(self):
        (self.base/'candidate').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'CandidateSubstituted'): self.publish()
        self.assertFalse((self.base/'root/boot'/publication.LEAF).exists())
    def test_parent_replacement_and_symlink_rejected(self):
        (self.base/'root/boot').rename(self.base/'old-boot')
        (self.base/'root/boot').symlink_to(self.base/'old-boot')
        with self.assertRaises((ValueError, OSError)): self.publish()
        self.assertFalse((self.base/'old-boot'/publication.LEAF).exists())
    def test_immediate_revalidation_failure_prevents_write(self):
        def fail(): raise ValueError('FreshWitnessRejected')
        with self.assertRaisesRegex(ValueError, 'FreshWitnessRejected'): self.publish(validate=fail)
        self.assertFalse((self.base/'root/boot'/publication.LEAF).exists())
    def test_copy_and_flush_failures_retain_partial_destination_without_success(self):
        for function in ('write', 'fsync'):
            with self.subTest(function=function):
                with patch.object(publication.os, function, side_effect=OSError('injected')):
                    with self.assertRaises(OSError): self.publish()
                dest = self.base/'root/boot'/publication.LEAF
                self.assertTrue(dest.exists())
                with self.assertRaisesRegex(ValueError, 'DestinationExists'): self.publish()
                dest.unlink()  # Ordinary unit-fixture disposal only.
    def test_unknown_destination_or_metadata_rejected(self):
        for field, value in (('Destination', '/etc/hostname'), ('Mode', 0o644), ('Uid', 1000), ('Policy', 'overwrite')):
            altered = copy.deepcopy(self.intent); altered[field] = value
            with self.assertRaisesRegex(ValueError, 'DeclarationChanged'): self.publish(altered)


class InitramfsDispatchTests(unittest.TestCase):
    def test_root_binding_uses_the_shared_numeric_wire_roles(self):
        bindings = [{'Role': 0, 'Access': 1, 'FileSystem': 'EXT4', 'FileSystemUuid': '398f0d87-e17c-46d6-866f-24d10d747006'},
                    {'Role': 1, 'Access': 0, 'FileSystem': 'FAT32', 'FileSystemUuid': '1234-0001'},
                    {'Role': 2, 'Access': 0, 'FileSystem': 'FAT32', 'FileSystemUuid': '1234-0002'}]
        self.assertEqual(bindings[0]['FileSystemUuid'], session.bound_root_uuid(bindings))
        for change in ('string', 'order', 'missing', 'duplicate', 'bool', 'filesystem', 'access'):
            altered = copy.deepcopy(bindings)
            if change == 'string': altered[0]['Role'] = 'Root'
            if change == 'order': altered.reverse()
            if change == 'missing': altered.pop()
            if change == 'duplicate': altered[1]['Role'] = 0
            if change == 'bool': altered[0]['Role'] = False
            if change == 'filesystem': altered[0]['FileSystem'] = 'FAT32'
            if change == 'access': altered[0]['Access'] = 0
            with self.subTest(change=change), self.assertRaisesRegex(ValueError, 'CanonicalRootBinding'):
                session.bound_root_uuid(altered)
    def test_previous_scopes_cannot_dispatch_initramfs(self):
        from block_session import lab_provenance
        for scope in ('StorageSmoke', 'CoreConfiguration', 'ConfiguredRootImport', 'Windows'):
            with self.assertRaises(ValueError):
                lab_provenance({'LabInitramfs': {'Provider': 'IsolatedFileBackedLab', 'Version': 4, 'Scope': scope}})
    def test_configured_delta_never_adopts_an_unbound_baseline(self):
        with self.assertRaisesRegex(ValueError, 'SavedConfiguredBaselineChanged'):
            session.configured_baseline(None, {'Entries': []}, {'Observation': {'FilesystemDeltaSha256': '0'*64}},
                {'Entries': []}, {'Length': 1, 'Sha256': 'A'*64})


if __name__ == '__main__': unittest.main()
