"""Small read-only retained-chain and ordinary-file first-boot input tests.

Set IGLOO_FIRSTBOOT_INITRAMFS_EVIDENCE and IGLOO_FIRSTBOOT_USERDATA_EVIDENCE
for the protected-record cases. Copies made for negative tests are temporary
test data, never canonical resources or receipt authority.
"""
import base64
import os
from pathlib import Path
import shutil
import tempfile
import unittest

import debian_first_boot as worker
from firstboot_retained_input import compose


class FirstBootInputCompositionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.imported = os.environ.get('IGLOO_FIRSTBOOT_IMPORT_EVIDENCE')
        cls.configured = os.environ.get('IGLOO_FIRSTBOOT_CONFIGURATION_EVIDENCE')
        cls.initramfs = os.environ.get('IGLOO_FIRSTBOOT_INITRAMFS_EVIDENCE')
        cls.userdata = os.environ.get('IGLOO_FIRSTBOOT_USERDATA_EVIDENCE')

    def require_retained(self):
        if not all((self.imported, self.configured, self.initramfs, self.userdata)):
            self.skipTest('protected canonical evidence paths not supplied')

    def test_real_retained_chain_reaches_receipt_consumer(self):
        self.require_retained()
        value = compose(self.initramfs, self.userdata, self.imported, self.configured)
        receipt = base64.b64decode(value['Receipt'], validate=True)
        worker.validate_completion_receipt(worker.decode(receipt),
                                           worker.decode(receipt)['GenerationId'], 'UserData')
        self.assertEqual(worker.digest(receipt), 'C0A57DC157A719F1B4D3389ED35DE5C0356ABD6E88F0D49080DB60C043A5406B')
        parsed = worker.decode(receipt)
        for changed in ({**parsed, 'Kind': 'DeploymentContent'},
                        {**parsed, 'GenerationId': '00000000-0000-0000-0000-000000000001'},
                        {**parsed, 'Unexpected': True}):
            with self.assertRaises(worker.Rejected):
                worker.validate_completion_receipt(changed, parsed['GenerationId'], 'UserData')
        with self.assertRaises(worker.Rejected):
            worker.decode(b'{"Kind":"UserData","Kind":"DeploymentContent"}')

    def test_changed_record_and_missing_close_cannot_compose(self):
        self.require_retained()
        with tempfile.TemporaryDirectory(prefix='igloo-firstboot-input-') as temp:
            copied = Path(temp)/'userdata'
            shutil.copytree(self.userdata, copied, symlinks=True)
            generation = 'ff104c9d-505c-4d88-8620-19b1153bd91e'
            close = sorted((copied/'journals/session'/generation).glob('*.json'))[-1]
            close.unlink()
            with self.assertRaises((AssertionError, ValueError, FileNotFoundError)):
                compose(self.initramfs, copied, self.imported, self.configured)
            shutil.copytree(self.userdata, copied, dirs_exist_ok=True, symlinks=True)
            record = sorted((copied/'journals/effects'/generation).glob('*.json'))[3]
            record.write_bytes(record.read_bytes() + b' ')
            with self.assertRaises((AssertionError, ValueError, FileNotFoundError)):
                compose(self.initramfs, copied, self.imported, self.configured)

    def test_worker_protected_ordinary_file_reader(self):
        with tempfile.TemporaryDirectory(prefix='igloo-firstboot-input-') as temp:
            root = Path(temp)
            data = b'{"synthetic":"receipt fixture"}'
            path = root/'userdata.json'
            path.write_bytes(data)
            path.chmod(0o600)
            fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
            try:
                self.assertEqual(worker.read_at(fd, path.name, os.geteuid()), data)
                path.chmod(0o666)
                with self.assertRaises(worker.Rejected):
                    worker.read_at(fd, path.name, os.geteuid())
                path.unlink()
                path.symlink_to('/etc/passwd')
                with self.assertRaises(OSError):
                    worker.read_at(fd, path.name, os.geteuid())
            finally:
                os.close(fd)


if __name__ == '__main__':
    unittest.main()
