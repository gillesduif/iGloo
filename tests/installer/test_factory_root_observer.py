import importlib.util
import os
from pathlib import Path
import socket
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("factory_observer", Path(__file__).with_name("factory_root_observer.py"))
observer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(observer)


class FactoryReadbackTests(unittest.TestCase):
    def test_symlink_is_observed_without_reading_outside_root(self):
        with tempfile.TemporaryDirectory() as name:
            base = Path(name); root = base / "root"; root.mkdir()
            (base / "outside").write_bytes(b"outside sentinel")
            (root / "link").symlink_to(base / "outside")
            result = observer.inventory(root)
            self.assertEqual(result["Types"], {"SymbolicLink": 1})
            self.assertIsNone(result["Entries"][0]["Sha256"])
            self.assertEqual((base / "outside").read_bytes(), b"outside sentinel")

    def test_hardlinks_ownership_and_modes_retained(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name); (root / "file").write_bytes(b"payload"); os.chmod(root / "file", 0o640)
            os.link(root / "file", root / "alias")
            result = observer.inventory(root)
            self.assertEqual(result["HardlinkGroups"], [["/alias", "/file"]])
            self.assertTrue(all(e["Mode"] == 0o640 and e["Uid"] == os.getuid() for e in result["Entries"]))

    def test_unicode_paths_are_reported_not_normalized_or_dropped(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name); (root / "é").write_bytes(b"one"); (root / "e\u0301").write_bytes(b"two")
            result = observer.inventory(root)
            self.assertEqual(set(result["NonAsciiOrControlPaths"]), {"/é", "/e\u0301"})
            self.assertEqual(len(result["Entries"]), 2)

    def test_fifo_and_socket_are_reported_without_opening_their_content(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name); os.mkfifo(root / "fifo")
            with socket.socket(socket.AF_UNIX) as channel:
                channel.bind(str(root / "socket"))
                result = observer.inventory(root)
                self.assertEqual({e["Type"] for e in result["SpecialFiles"]}, {"Fifo", "Socket"})

    def test_xattr_bytes_preserved_in_audit(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name); (root / "file").write_bytes(b"data"); os.setxattr(root / "file", "user.fixture", b"exact")
            result = observer.inventory(root)
            self.assertEqual(result["XattrNames"], {"user.fixture": 1})
            self.assertEqual(result["Entries"][0]["Xattrs"], {"user.fixture": "ZXhhY3Q="})

    def test_arbitrary_root_is_not_a_factory_build_observation(self):
        with tempfile.TemporaryDirectory() as name:
            with self.assertRaisesRegex(ValueError, "FactoryReadbackPrecondition"):
                observer.observe(Path(name), {"BuildId": "99695826-82bc-4077-b4cf-a22cebc706da"})
