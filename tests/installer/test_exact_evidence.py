"""Complete-byte identities, not JSON equivalence or release authentication."""
import hashlib
from pathlib import Path
import unittest


class ExactEvidenceTests(unittest.TestCase):
    def verify(self, name, length, digest):
        data = (Path(__file__).resolve().parents[2] / 'docs/architecture' / name).read_bytes()
        self.assertEqual(length, len(data))
        self.assertEqual(digest, hashlib.sha256(data).hexdigest().upper())
        for changed in (data + b'\n', data + b'\r\n', b'\xef\xbb\xbf' + data):
            self.assertNotEqual(digest, hashlib.sha256(changed).hexdigest().upper())

    def test_retained_transport_bytes(self):
        self.verify('debian-root-chunk-transport-manifest.json', 1121,
                    'A093432AE281F4620959B56E35774DC0E5D01C74443DCD33F248DA723B364757')

    def test_retained_neutralization_export_bytes(self):
        self.verify('debian-configured-root-neutralization-evidence.json', 55916,
                    'D38FAE0A29E58190EB00D7F163478D35CB5711E42C1A7105F480408E1AC23FD3')


if __name__ == '__main__': unittest.main()
