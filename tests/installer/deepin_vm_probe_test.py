"""The VM probe must never expose writable host storage or start the installer."""

import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock


REPO = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("deepin_vm_probe", REPO / "tools/deepin-vm/probe.py")
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)


class ProbeTests(unittest.TestCase):
    def test_image_range_accounts_for_skip_and_checks_length(self):
        spec = importlib.util.spec_from_file_location(
            "immutable_probe", REPO / "tools/deepin-vm/immutable_probe.py")
        experiment = importlib.util.module_from_spec(spec)
        with mock.patch.dict("sys.modules", {"probe": probe}):
            spec.loader.exec_module(experiment)
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            image = directory / "fixture.qcow2"
            image.write_bytes(b"synthetic fixture")
            destination = directory / "sample"
            with mock.patch.object(experiment.subprocess, "run") as run:
                # A successful utility exit alone is not evidence of a read.
                with self.assertRaises(RuntimeError):
                    experiment.extract_image_range(image, destination, 4096, 1024)
                args = run.call_args.args[0]
                self.assertIn("skip=8", args)
                self.assertIn("count=10", args)
                with self.assertRaises(FileExistsError):
                    experiment.extract_image_range(image, destination, 4096, 1024)

    def test_image_range_rejects_bad_geometry_before_running_tool(self):
        spec = importlib.util.spec_from_file_location(
            "immutable_probe", REPO / "tools/deepin-vm/immutable_probe.py")
        experiment = importlib.util.module_from_spec(spec)
        with mock.patch.dict("sys.modules", {"probe": probe}):
            spec.loader.exec_module(experiment)
        for offset, length in ((-512, 512), (0, 0), (1, 512), (512, 513), (True, 512)):
            with mock.patch.object(experiment.subprocess, "run") as run:
                with self.subTest(offset=offset, length=length), self.assertRaises(ValueError):
                    experiment.extract_image_range(Path("absent"), Path("absent-output"), offset, length)
                run.assert_not_called()

    def test_only_attached_storage_is_read_only_iso(self):
        args = probe.qemu_arguments(Path("/tmp/image,with,commas.iso"), Path("/tmp/probe"))
        self.assertNotIn("-drive", args)
        self.assertNotIn("-hda", args)
        self.assertNotIn("-virtfs", args)
        self.assertIn("-nodefaults", args)
        self.assertEqual(args.count("-blockdev"), 1)
        block = json.loads(args[args.index("-blockdev") + 1])
        self.assertIs(block["read-only"], True)
        self.assertEqual(block["file"]["filename"], "/tmp/image,with,commas.iso")
        self.assertEqual(args[args.index("-device") + 1], "ide-cd,drive=iso")

    def test_no_network_and_no_stock_installer(self):
        args = probe.qemu_arguments(Path("/tmp/image.iso"), Path("/tmp/probe"))
        self.assertEqual(args[args.index("-nic") + 1], "none")
        command = args[args.index("-append") + 1]
        self.assertIn("init=/bin/bash", command)
        self.assertNotIn("livecd-installer", command)

    def test_rejects_wrong_iso_before_vm_launch(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "fake.iso"
            path.write_bytes(b"not the official ISO")
            with self.assertRaises(ValueError):
                probe.verify_iso(path)

    def test_console_preserves_trailing_output(self):
        class Connection:
            def recv(self, size):
                return b"first marker second marker"

        log = io.BytesIO()
        console = probe.Console(Connection(), log)
        self.assertEqual(console.until(b"marker"), b"first marker")
        self.assertEqual(console.until(b"marker"), b" second marker")
        self.assertEqual(log.getvalue(), b"first marker second marker")

    def test_console_disconnect_is_failure(self):
        class Connection:
            def recv(self, size):
                return b""

        with self.assertRaises(RuntimeError):
            probe.Console(Connection(), io.BytesIO()).until(b"not reached")

    def test_upload_cannot_choose_guest_destination(self):
        console = probe.Console(None, io.BytesIO())
        with self.assertRaises(ValueError):
            console.upload(b"anything", "../../etc/fstab")

    def test_immutable_experiment_refuses_existing_output_directory(self):
        spec = importlib.util.spec_from_file_location(
            "immutable_probe", REPO / "tools/deepin-vm/immutable_probe.py")
        experiment = importlib.util.module_from_spec(spec)
        with mock.patch.dict("sys.modules", {"probe": probe}):
            spec.loader.exec_module(experiment)
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            existing = directory / "disposable.img"
            existing.write_bytes(b"pre-existing image must be preserved")
            with mock.patch.object(probe, "verify_iso", return_value=directory / "iso"), \
                    mock.patch.object(experiment.subprocess, "Popen") as launch, \
                    self.assertRaises(FileExistsError):
                experiment.main(directory / "iso", directory)
            launch.assert_not_called()
            self.assertEqual(existing.read_bytes(), b"pre-existing image must be preserved")

    def test_preservation_samples_allow_root_writes_and_detect_reserved_region_writes(self):
        spec = importlib.util.spec_from_file_location(
            "immutable_probe", REPO / "tools/deepin-vm/immutable_probe.py")
        experiment = importlib.util.module_from_spec(spec)
        with mock.patch.dict("sys.modules", {"probe": probe}):
            spec.loader.exec_module(experiment)
        manifest = {"installationTarget": {
            "rootPartitionGuid": "root", "disk": {
                "diskSizeBytes": 8 * 1024 * 1024, "partitions": [
                    {"partitionGuid": "other", "offsetBytes": 1024 * 1024,
                     "lengthBytes": 1024 * 1024},
                    {"partitionGuid": "root", "offsetBytes": 3 * 1024 * 1024,
                     "lengthBytes": 2 * 1024 * 1024}]}}}
        with tempfile.TemporaryDirectory() as folder:
            image = Path(folder) / "fixture.img"
            with image.open("wb") as stream:
                stream.truncate(8 * 1024 * 1024)
            before = experiment.preservation_samples(image, manifest)
            with image.open("r+b") as stream:
                stream.seek(3 * 1024 * 1024)
                stream.write(b"authorized root data")
            self.assertEqual(experiment.preservation_samples(image, manifest), before)
            with image.open("r+b") as stream:
                stream.seek(64 * 512)
                stream.write(b"unauthorized reserved-area write")
            self.assertNotEqual(experiment.preservation_samples(image, manifest), before)

    def test_preservation_samples_reject_short_image(self):
        spec = importlib.util.spec_from_file_location(
            "immutable_probe", REPO / "tools/deepin-vm/immutable_probe.py")
        experiment = importlib.util.module_from_spec(spec)
        with mock.patch.dict("sys.modules", {"probe": probe}):
            spec.loader.exec_module(experiment)
        with tempfile.TemporaryDirectory() as folder:
            image = Path(folder) / "fixture.img"
            image.write_bytes(b"truncated")
            with self.assertRaisesRegex(RuntimeError, "Truncated"):
                experiment.preservation_samples(image, {"installationTarget": {
                    "rootPartitionGuid": "root", "disk": {
                        "diskSizeBytes": 8 * 1024 * 1024, "partitions": []}}})


if __name__ == "__main__":
    unittest.main()
