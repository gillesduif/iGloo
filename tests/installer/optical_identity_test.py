"""Exact optical-media exemptions use only synthetic streams and sysfs files."""

import hashlib
import importlib.util
import io
from pathlib import Path
import struct
import tempfile
import unittest
from unittest import mock


REPO = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("optical_target", REPO / "distros/_shared/installer/igloo_target.py")
target = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(target)


class Stream(io.BytesIO):
    def fileno(self):
        return 12345  # Never passed to the real ioctl.


class OpticalTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.node = Path(self.folder.name)
        (self.node / "device").mkdir()
        (self.node / "device/type").write_text("5")
        (self.node / "ro").write_text("1")
        self.content = bytes(range(256)) * 32
        self.pins = {hashlib.sha256(self.content).hexdigest(): len(self.content)}

    def ioctl(self, fd, request, buffer):
        self.assertEqual((fd, request, buffer), (12345, 0x125E, bytes(4)))
        return struct.pack("=I", 1)

    def verify(self, **kwargs):
        args = dict(stream=Stream(self.content), node=self.node, sector=2048,
                    size=len(self.content), pins=self.pins, ioctl=self.ioctl)
        args.update(kwargs)
        return target._verified_optical(**args)

    def test_exact_optical_content_accepted(self):
        self.assertTrue(self.verify())

    def test_missing_pin_and_wrong_geometry_never_exempt(self):
        for args in ({"pins": {}}, {"sector": 512}, {"sector": 4096},
                     {"size": len(self.content) * 2}):
            with self.subTest(args=args):
                self.assertFalse(self.verify(**args))

    def test_same_size_modified_content_fails(self):
        with self.assertRaisesRegex(target.IdentityError, "checksum"):
            self.verify(stream=Stream(b"x" + self.content[1:]))

    def test_truncated_content_fails(self):
        with self.assertRaisesRegex(target.IdentityError, "short read"):
            self.verify(stream=Stream(self.content[:-1]))

    def test_hard_disks_and_writable_media_never_exempt(self):
        (self.node / "device/type").write_text("0")
        self.assertFalse(self.verify())
        (self.node / "device/type").write_text("5")
        (self.node / "ro").write_text("0")
        self.assertFalse(self.verify())
        (self.node / "ro").write_text("1")
        self.assertFalse(self.verify(ioctl=lambda *_: struct.pack("=I", 0)))

    def test_read_only_state_change_fails(self):
        ioctl = mock.Mock(side_effect=[struct.pack("=I", 1), struct.pack("=I", 0)])
        with self.assertRaisesRegex(target.IdentityError, "changed"):
            self.verify(ioctl=ioctl)

    def test_pin_validation_rejects_malformed_policy(self):
        for value in ([], {"invalid": 8192}, {"A" * 64: 8192},
                      {"a" * 64: True}, {"a" * 64: 8193}, {"a" * 64: 0}):
            with self.subTest(value=value), self.assertRaises(target.IdentityError):
                target._optical_pins(value)
        self.assertEqual(target._optical_pins(None), {})
        self.assertEqual(target._optical_pins(self.pins), self.pins)

    def test_pin_policy_is_copied(self):
        captured = target._optical_pins(self.pins)
        self.pins.clear()
        self.assertEqual(len(captured), 1)


if __name__ == "__main__":
    unittest.main()
