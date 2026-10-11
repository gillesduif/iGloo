"""Small root-owned record-copy fixtures; never a canonical guest source."""
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from canonical_stage_files import copy_tree, read_file, snapshot_tree


@unittest.skipUnless(hasattr(os, 'geteuid') and os.geteuid() == 0 and
                     hasattr(os, 'O_NOFOLLOW'), 'Linux root fixture required')
class StageRecordFileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='igloo-stage-files-', dir='/root')
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.source = self.base/'source'
        self.source.mkdir(mode=0o700)
        (self.source/'nested').mkdir(mode=0o700)
        (self.source/'empty').mkdir(mode=0o700)
        (self.source/'nested'/'record.json').write_bytes(b'{"Exact":"\\u002B"}\n')
        (self.source/'zero').write_bytes(b'')
        for path in (self.source/'nested'/'record.json', self.source/'zero'):
            path.chmod(0o600)

    def test_exact_fd_relative_copy_and_reopen_preserves_source(self):
        before = snapshot_tree(self.source)
        self.assertEqual(b'{"Exact":"\\u002B"}\n',
                         read_file(self.source/'nested'/'record.json'))
        self.assertEqual(before, copy_tree(self.source, self.base/'copy'))
        self.assertEqual(before, snapshot_tree(self.base/'copy'))
        self.assertEqual(before, snapshot_tree(self.source))

    def test_symlink_alias_mode_and_collision_fail_closed(self):
        (self.source/'nested'/'link').symlink_to('record.json')
        with self.assertRaises(ValueError):
            copy_tree(self.source, self.base/'refused')
        self.assertFalse((self.base/'refused').exists())
        (self.source/'nested'/'link').unlink()
        (self.source/'zero').chmod(0o666)
        with self.assertRaisesRegex(ValueError, 'StageRecordObjectUnprotected'):
            snapshot_tree(self.source)
        (self.source/'zero').chmod(0o600)
        (self.base/'refused').mkdir(mode=0o700)
        with self.assertRaisesRegex(ValueError, 'StageRecordCopyDestinationExists'):
            copy_tree(self.source, self.base/'refused')

    def test_file_bound_and_mount_substitution_rejected(self):
        with self.assertRaisesRegex(ValueError, 'StageRecordFileBoundOrMountChanged'):
            snapshot_tree(self.source, max_file=4)
        import canonical_stage_files as files
        real_mount = files._mount
        calls = 0
        def changed_mount(fd):
            nonlocal calls
            calls += 1
            value = real_mount(fd)
            return value + 1 if calls == 3 else value
        with patch.object(files, '_mount', side_effect=changed_mount):
            with self.assertRaisesRegex(ValueError,
                                        'StageRecord(DirectorySubstituted|.*MountChanged)'):
                snapshot_tree(self.source)

    def test_unprotected_ancestor_is_not_acquisition_authority(self):
        self.base.chmod(0o777)
        with self.assertRaisesRegex(ValueError, 'StageRecordObjectUnprotected'):
            read_file(self.source/'nested'/'record.json')


if __name__ == '__main__':
    unittest.main()
