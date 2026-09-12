"""Installation identity tests using dictionaries, fake sysfs and ordinary files.

No test enumerates host disks, opens a host block device, mounts an image, or
executes a partition utility. Sparse GPT files exercise the production parser.
Run: python -m unittest discover -s tests/installer -p '*_test.py'
"""

from __future__ import annotations

from contextlib import redirect_stderr, redirect_stdout
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import random
import stat
import struct
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock
import uuid
import zlib


REPO = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "igloo_target", REPO / "distros/_shared/installer/igloo_target.py")
target = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(target)
FIXTURE = json.loads((REPO / "tests/fixtures/installation-target.json").read_text(encoding="utf-8"))
INSTALLATION_ID = FIXTURE["installationId"]
MIB = 1024 * 1024
OTHER_GUID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"


def observed(manifest=None, device="/dev/nvme9n2"):
    manifest = manifest or FIXTURE
    disk = copy.deepcopy(manifest["installationTarget"]["disk"])
    disk["devicePath"] = device
    disk["model"] = "identical models do not identify disks"
    for index, part in enumerate(disk["partitions"]):
        part["devicePath"] = f"{device}p{(index + 1) * 2}"
        part["partitionNumber"] = (index + 1) * 2
        part["label"] = "duplicate-label"
    return disk


def image_manifest(sector=512):
    result = copy.deepcopy(FIXTURE)
    disk = result["installationTarget"]["disk"]
    disk["diskSizeBytes"] = 32 * MIB
    disk["logicalSectorSize"] = sector
    for part, offset, length in zip(disk["partitions"], (1, 3, 9, 16, 30), (1, 4, 3, 8, 1)):
        part["offsetBytes"] = offset * MIB
        part["lengthBytes"] = length * MIB
    return result


def make_gpt(path, manifest, slots=(1, 3, 5, 7, 9)):
    """Build an unmounted ordinary sparse file; never use a disk utility."""
    disk = manifest["installationTarget"]["disk"]
    sector, size = disk["logicalSectorSize"], disk["diskSizeBytes"]
    count, entry_size = 128, 128
    table = bytearray(count * entry_size)
    for part, slot in zip(disk["partitions"], slots):
        offset = (slot - 1) * entry_size
        table[offset:offset + 16] = uuid.UUID(part["gptType"]).bytes_le
        table[offset + 16:offset + 32] = uuid.UUID(part["partitionGuid"]).bytes_le
        first = part["offsetBytes"] // sector
        last = first + part["lengthBytes"] // sector - 1
        struct.pack_into("<QQQ", table, offset + 32, first, last, 0)
        name = "same-label".encode("utf-16-le")
        table[offset + 56:offset + 56 + len(name)] = name
    final = size // sector - 1
    table_sectors = len(table) // sector
    primary_table, backup_table = 2, final - table_sectors
    first_usable, last_usable = primary_table + table_sectors, backup_table - 1
    mbr = bytearray(sector)
    mbr[450] = 0xEE
    struct.pack_into("<II", mbr, 454, 1, min(final, 0xFFFFFFFF))
    mbr[510:512] = b"\x55\xaa"

    def header(current, other, table_lba):
        block = bytearray(sector)
        block[:8] = b"EFI PART"
        struct.pack_into("<IIIIQQQQ", block, 8, 0x10000, 92, 0, 0,
                         current, other, first_usable, last_usable)
        block[56:72] = uuid.UUID(disk["diskGuid"]).bytes_le
        struct.pack_into("<QIII", block, 72, table_lba, count, entry_size, zlib.crc32(table))
        struct.pack_into("<I", block, 16, zlib.crc32(block[:92]))
        return block

    with path.open("w+b") as stream:
        stream.truncate(size)
        for offset, data in ((0, mbr), (sector, header(1, final, primary_table)),
                             (primary_table * sector, table), (backup_table * sector, table),
                             (final * sector, header(final, 1, backup_table))):
            stream.seek(offset)
            stream.write(data)
    return {"sector": sector, "size": size, "primary": sector,
            "backup": final * sector, "primaryTable": primary_table * sector,
            "backupTable": backup_table * sector, "tableBytes": len(table)}


def change_header(path, geometry, side, offset, data):
    with path.open("r+b") as stream:
        stream.seek(geometry[side])
        header = bytearray(stream.read(geometry["sector"]))
        header[offset:offset + len(data)] = data
        header[16:20] = b"\0" * 4
        struct.pack_into("<I", header, 16, zlib.crc32(header[:92]))
        stream.seek(geometry[side])
        stream.write(header)


def change_table(path, geometry, offset, data, sides=("primary", "backup")):
    for side in sides:
        with path.open("r+b") as stream:
            stream.seek(geometry[side + "Table"])
            table = bytearray(stream.read(geometry["tableBytes"]))
            table[offset:offset + len(data)] = data
            stream.seek(geometry[side + "Table"])
            stream.write(table)
        change_header(path, geometry, side, 88, struct.pack("<I", zlib.crc32(table)))


class ResolveTests(unittest.TestCase):
    def setUp(self):
        self.manifest = copy.deepcopy(FIXTURE)
        self.layout = observed()

    def run_resolve(self, manifest=None, layouts=None, **kwargs):
        return target.resolve(self.manifest if manifest is None else manifest,
                              [self.layout] if layouts is None else layouts,
                              kwargs.pop("expected_installation_id", INSTALLATION_ID), **kwargs)

    def test_shared_fixture_selects_owned_root_not_existing_linux(self):
        result = self.run_resolve()
        self.assertEqual(result, {"installationId": INSTALLATION_ID,
                                 "diskDevice": "/dev/nvme9n2",
                                 "rootDevice": "/dev/nvme9n2p8",
                                 "espDevice": "/dev/nvme9n2p2"})

    def test_order_names_numbers_and_labels_do_not_select(self):
        self.layout["partitions"].reverse()
        for index, part in enumerate(self.layout["partitions"]):
            part["partitionNumber"] = 200 + index
            part["label"] = "not-the-original-label"
        self.manifest["installationTarget"]["disk"]["partitions"].reverse()
        self.assertEqual(self.run_resolve()["rootDevice"], "/dev/nvme9n2p8")
        renamed = observed(device="/dev/sdz")
        self.assertEqual(self.run_resolve(layouts=[renamed])["rootDevice"], "/dev/sdzp8")

    def test_missing_fields_at_every_contract_level_fail(self):
        for keys in (("schemaVersion",), ("installationId",), ("installationTarget",),
                     *(("installationTarget", key) for key in target._TARGET_KEYS),
                     *(("installationTarget", "disk", key) for key in target._DISK_KEYS),
                     *(("installationTarget", "disk", "partitions", 0, key)
                       for key in target._PARTITION_KEYS)):
            with self.subTest(keys=keys):
                manifest = copy.deepcopy(self.manifest)
                obj = manifest
                for key in keys[:-1]:
                    obj = obj[key]
                del obj[keys[-1]]
                with self.assertRaises(target.IdentityError):
                    self.run_resolve(manifest)

    def test_unknown_target_semantics_rejected_manifest_extras_retained(self):
        self.manifest["futureMigrationField"] = {"anything": 42}
        self.run_resolve()
        for path in (("installationTarget",), ("installationTarget", "disk"),
                     ("installationTarget", "disk", "partitions", 0)):
            manifest = copy.deepcopy(self.manifest)
            obj = manifest
            for key in path:
                obj = obj[key]
            obj["permissionToFormatAnotherDisk"] = True
            with self.subTest(path=path), self.assertRaises(target.IdentityError):
                self.run_resolve(manifest)

    def test_guid_formats_and_missing_id_fail(self):
        bad_guids = (None, "", target._ZERO_GUID, "{11111111-1111-4111-8111-111111111111}",
                     OTHER_GUID.upper(), "1" * 32, "not-a-guid", 1, True)
        for value in bad_guids:
            for keys in (("installationId",), ("installationTarget", "installationId"),
                         ("installationTarget", "rootPartitionGuid"),
                         ("installationTarget", "disk", "diskGuid"),
                         ("installationTarget", "disk", "partitions", 0, "partitionGuid"),
                         ("installationTarget", "disk", "partitions", 0, "gptType")):
                manifest = copy.deepcopy(self.manifest)
                obj = manifest
                for key in keys[:-1]:
                    obj = obj[key]
                obj[keys[-1]] = value
                with self.subTest(value=value, keys=keys), self.assertRaises(target.IdentityError):
                    self.run_resolve(manifest)
            with self.subTest(expected=value), self.assertRaises(target.IdentityError):
                self.run_resolve(expected_installation_id=value)

    def test_stale_or_mismatched_run_rejected(self):
        with self.assertRaises(target.IdentityError):
            self.run_resolve(expected_installation_id=OTHER_GUID)
        self.manifest["installationTarget"]["installationId"] = OTHER_GUID
        with self.assertRaises(target.IdentityError):
            self.run_resolve()

    def test_numeric_types_ranges_and_versions_rejected(self):
        fields = (("schemaVersion",), ("installationTarget", "version"),
                  ("installationTarget", "disk", "diskSizeBytes"),
                  ("installationTarget", "disk", "logicalSectorSize"),
                  ("installationTarget", "disk", "partitions", 0, "offsetBytes"),
                  ("installationTarget", "disk", "partitions", 0, "lengthBytes"))
        for value in (None, "512", False, True, -1, 0, 1.0, 1 << 63, float("nan")):
            for keys in fields:
                manifest = copy.deepcopy(self.manifest)
                obj = manifest
                for key in keys[:-1]:
                    obj = obj[key]
                obj[keys[-1]] = value
                with self.subTest(value=value, keys=keys), self.assertRaises(target.IdentityError):
                    self.run_resolve(manifest)
        for keys, value in ((("schemaVersion",), 2), (("installationTarget", "version"), 2),
                            (("installationTarget", "disk", "logicalSectorSize"), 2048)):
            manifest = copy.deepcopy(self.manifest)
            obj = manifest
            for key in keys[:-1]:
                obj = obj[key]
            obj[keys[-1]] = value
            with self.assertRaises(target.IdentityError):
                self.run_resolve(manifest)

    def test_missing_or_wrong_root_ownership_type_and_esp_rejected(self):
        for key, value in (("ownership", "adopted"), ("ownership", None),
                           ("rootPartitionGuid", OTHER_GUID),
                           ("rootPartitionGuid", self.manifest["installationTarget"]["espPartitionGuid"]),
                           ("espPartitionGuid", OTHER_GUID),
                           ("espPartitionGuid", self.manifest["installationTarget"]["rootPartitionGuid"]),
                           ("espPartitionGuid", None)):
            manifest = copy.deepcopy(self.manifest)
            manifest["installationTarget"][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(target.IdentityError):
                self.run_resolve(manifest)
        self.manifest["installationTarget"]["espPartitionGuid"] = None
        self.assertIsNone(self.run_resolve(require_esp=False)["espDevice"])
        del self.manifest["installationTarget"]["espPartitionGuid"]
        with self.assertRaises(target.IdentityError):
            self.run_resolve(require_esp=False)

    def test_root_bounds_are_stricter_than_existing_partition_bounds(self):
        disk = self.manifest["installationTarget"]["disk"]
        disk["partitions"][0]["offsetBytes"] = 34 * 512
        disk["partitions"][-1]["lengthBytes"] = disk["diskSizeBytes"] - 512 - disk["partitions"][-1]["offsetBytes"]
        self.run_resolve(layouts=[observed(self.manifest)])
        disk["partitions"][3]["offsetBytes"] = 512
        with self.assertRaises(target.IdentityError):
            self.run_resolve(layouts=[observed(self.manifest)])

    def test_malformed_geometry_and_partition_lists_rejected(self):
        for key, value in (("offsetBytes", 513), ("offsetBytes", 0),
                           ("offsetBytes", self.layout["diskSizeBytes"]),
                           ("lengthBytes", 513), ("lengthBytes", self.layout["diskSizeBytes"]),
                           ("lengthBytes", (1 << 63) - 1)):
            layout = copy.deepcopy(self.layout)
            layout["partitions"][1][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(target.IdentityError):
                self.run_resolve(layouts=[layout])
        for value in (None, {}, "partitions", [None], [False], [self.layout["partitions"][0]] * 4097):
            layout = copy.deepcopy(self.layout)
            layout["partitions"] = value
            with self.subTest(value=type(value)), self.assertRaises(target.IdentityError):
                self.run_resolve(layouts=[layout])
        self.layout["partitions"][1]["offsetBytes"] = self.layout["partitions"][0]["offsetBytes"]
        with self.assertRaises(target.IdentityError):
            self.run_resolve()

    def test_entire_layout_changes_are_rejected(self):
        for key, value in (("diskGuid", OTHER_GUID), ("logicalSectorSize", 4096),
                           ("diskSizeBytes", self.layout["diskSizeBytes"] + 512)):
            layout = copy.deepcopy(self.layout)
            layout[key] = value
            with self.subTest(key=key), self.assertRaises(target.IdentityError):
                self.run_resolve(layouts=[layout])
        for key, value in (("partitionGuid", OTHER_GUID), ("gptType", target.LINUX_FILESYSTEM),
                           ("offsetBytes", 2 * 1024 * MIB), ("lengthBytes", 1 * MIB)):
            layout = copy.deepcopy(self.layout)
            layout["partitions"][1][key] = value
            with self.subTest(key=key), self.assertRaises(target.IdentityError):
                self.run_resolve(layouts=[layout])
        layout = copy.deepcopy(self.layout)
        layout["partitions"].pop()
        with self.assertRaises(target.IdentityError):
            self.run_resolve(layouts=[layout])
        layout = copy.deepcopy(self.layout)
        layout["partitions"].append({"partitionGuid": OTHER_GUID, "gptType": target.LINUX_FILESYSTEM,
                                     "offsetBytes": 74 * 1024 * MIB, "lengthBytes": MIB,
                                     "devicePath": "/dev/nvme9n2p99"})
        with self.assertRaises(target.IdentityError):
            self.run_resolve(layouts=[layout])

    def test_clone_ambiguity_rejected_globally(self):
        clone = observed(device="/dev/nvme10n2")
        with self.assertRaises(target.IdentityError):
            self.run_resolve(layouts=[self.layout, clone])
        clone["diskGuid"] = OTHER_GUID
        with self.assertRaises(target.IdentityError):
            self.run_resolve(layouts=[self.layout, clone])
        clone["partitions"] = []
        self.run_resolve(layouts=[clone, self.layout])
        clone2 = copy.deepcopy(clone)
        clone2["devicePath"] = "/dev/sdb"
        with self.assertRaises(target.IdentityError):
            self.run_resolve(layouts=[clone, self.layout, clone2])

    def test_observed_paths_are_validated_before_output(self):
        for path in (None, "", "/dev/../sda", "/dev/disk/by-id/anything", "/tmp/disk", "/dev/a\nb"):
            layout = copy.deepcopy(self.layout)
            layout["partitions"][0]["devicePath"] = path
            with self.subTest(path=path), self.assertRaises(target.IdentityError):
                self.run_resolve(layouts=[layout])
        self.layout["partitions"][0]["devicePath"] = self.layout["devicePath"]
        with self.assertRaises(target.IdentityError):
            self.run_resolve()

    def test_absent_or_malformed_inventory_fails(self):
        for layouts in ([], None, {}, [None]):
            with self.subTest(layouts=layouts), self.assertRaises(target.IdentityError):
                target.resolve(self.manifest, layouts, INSTALLATION_ID)
        with self.assertRaises(target.IdentityError):
            self.run_resolve(require_esp=1)


class JsonAndCliTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "manifest.json"

    def write(self, text):
        self.path.write_text(text, encoding="utf-8")

    def test_valid_json_and_escaped_braces(self):
        manifest = copy.deepcopy(FIXTURE)
        manifest["extra"] = '{"[\\"' * 100
        self.write(json.dumps(manifest))
        self.assertEqual(target.read_claim(self.path, INSTALLATION_ID), manifest)

    def test_duplicate_fields_rejected_at_every_level(self):
        for field in ("schemaVersion", "ownership", "diskGuid", "partitionGuid", "offsetBytes"):
            text = json.dumps(FIXTURE)
            import re
            match = re.search(r'"' + field + r'": ("[^"]*"|[0-9]+)', text)
            text = text[:match.end()] + ", " + match.group() + text[match.end():]
            self.write(text)
            with self.subTest(field=field), self.assertRaises(target.IdentityError):
                target.read_claim(self.path, INSTALLATION_ID)

    def test_malformed_nonfinite_and_wrong_envelope(self):
        for text in ("null", "[]", "{", "true", "1", "{\"schemaVersion\":NaN}",
                     json.dumps(FIXTURE)[:-1] + ', "extra": Infinity}'):
            self.write(text)
            with self.subTest(text=text[:35]), self.assertRaises(target.IdentityError):
                target.read_claim(self.path, INSTALLATION_ID)

    def test_bounded_size_and_depth_before_recursive_parse(self):
        self.path.write_bytes(b" " * 129)
        with mock.patch.object(target, "_MAX_MANIFEST_BYTES", 128), self.assertRaises(target.IdentityError):
            target.read_claim(self.path, INSTALLATION_ID)
        self.write('{"extra":' + "[" * 64 + "0" + "]" * 64 + "}")
        with self.assertRaisesRegex(target.IdentityError, "nesting"):
            target.read_claim(self.path, INSTALLATION_ID)
        self.path.write_bytes(b"\xff")
        with self.assertRaises(target.IdentityError):
            target.read_claim(self.path, INSTALLATION_ID)

    def test_cli_success_is_paths_json_only_and_requires_external_run_id(self):
        self.write(json.dumps(FIXTURE))
        stdout, stderr = io.StringIO(), io.StringIO()
        with mock.patch.object(target, "collect_layouts", return_value=[observed()]) as collector:
            with redirect_stdout(stdout), redirect_stderr(stderr):
                code = target.main(["--manifest", str(self.path), "--expected-installation-id", INSTALLATION_ID])
            self.assertEqual(code, 0)
            self.assertEqual(stderr.getvalue(), "")
            self.assertEqual(json.loads(stdout.getvalue())["rootDevice"], "/dev/nvme9n2p8")
            collector.assert_called_once()
        with mock.patch.object(target, "collect_layouts") as collector, redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                target.main(["--manifest", str(self.path)])
            collector.assert_not_called()

    def test_cli_failure_has_no_path_output_and_does_not_inspect_for_stale_receipt(self):
        self.write(json.dumps(FIXTURE))
        stdout, stderr = io.StringIO(), io.StringIO()
        with mock.patch.object(target, "collect_layouts") as collector:
            with redirect_stdout(stdout), redirect_stderr(stderr):
                code = target.main(["--manifest", str(self.path), "--expected-installation-id", OTHER_GUID])
            self.assertEqual(code, 1)
            self.assertEqual(stdout.getvalue(), "")
            self.assertIn("refused", stderr.getvalue())
            collector.assert_not_called()
        with mock.patch.object(target, "collect_layouts", side_effect=PermissionError("denied")):
            with redirect_stdout(io.StringIO()) as stdout, redirect_stderr(io.StringIO()):
                self.assertEqual(target.main(["--manifest", str(self.path), "--expected-installation-id", INSTALLATION_ID]), 1)
                self.assertEqual(stdout.getvalue(), "")


class GptFileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "disposable-gpt.img"
        self.manifest = image_manifest()
        self.geometry = make_gpt(self.path, self.manifest)

    def read(self):
        with self.path.open("rb") as stream:
            return target.read_gpt(stream, self.geometry["sector"], self.geometry["size"])

    def test_sparse_images_512_and_4096_with_gaps_use_production_parser(self):
        for sector in (512, 4096):
            manifest = image_manifest(sector)
            self.geometry = make_gpt(self.path, manifest)
            layout = self.read()
            self.assertEqual([part["partitionNumber"] for part in layout["partitions"]], [1, 3, 5, 7, 9])
            layout["devicePath"] = "/dev/fake"
            for part in layout["partitions"]:
                part["devicePath"] = f'/dev/fake{part["partitionNumber"]}'
            self.assertEqual(target.resolve(manifest, [layout], INSTALLATION_ID)["rootDevice"], "/dev/fake7")

    def test_primary_and_backup_header_and_table_crc_corruption(self):
        for part, delta in (("primary", 56), ("backup", 56), ("primaryTable", 0), ("backupTable", 0)):
            self.geometry = make_gpt(self.path, self.manifest)
            with self.path.open("r+b") as stream:
                stream.seek(self.geometry[part] + delta)
                data = stream.read(1)
                stream.seek(-1, 1)
                stream.write(bytes([data[0] ^ 1]))
            with self.subTest(part=part), self.assertRaisesRegex(target.IdentityError, "CRC"):
                self.read()

    def test_independently_valid_gpt_copies_must_agree(self):
        change_table(self.path, self.geometry, 56, "changed".encode("utf-16-le"), sides=("backup",))
        with self.assertRaisesRegex(target.IdentityError, "disagree"):
            self.read()
        self.geometry = make_gpt(self.path, self.manifest)
        change_header(self.path, self.geometry, "backup", 56, uuid.UUID(OTHER_GUID).bytes_le)
        with self.assertRaisesRegex(target.IdentityError, "disagree"):
            self.read()

    def test_zero_or_duplicate_partition_identity_and_overlap_rejected(self):
        first_guid = uuid.UUID(self.manifest["installationTarget"]["disk"]["partitions"][0]["partitionGuid"]).bytes_le
        cases = ((16, bytes(16)), (2 * 128 + 16, first_guid),
                 (2 * 128 + 32, struct.pack("<QQ", 2048, 4095)),
                 (32, struct.pack("<QQ", 1, 2048)),
                 (32, struct.pack("<QQ", 4096, 2048)))
        for offset, data in cases:
            self.geometry = make_gpt(self.path, self.manifest)
            change_table(self.path, self.geometry, offset, data)
            with self.subTest(offset=offset, data=data), self.assertRaises(target.IdentityError):
                self.read()

    def test_malformed_headers_with_correct_crc_rejected(self):
        for offset, data in ((8, struct.pack("<I", 0x20000)), (12, struct.pack("<I", 128)),
                             (20, struct.pack("<I", 1)), (24, struct.pack("<Q", 2)),
                             (32, struct.pack("<Q", 2)), (40, struct.pack("<Q", 1)),
                             (48, struct.pack("<Q", self.geometry["size"] // 512)),
                             (56, bytes(16)), (72, struct.pack("<Q", 1000)),
                             (80, struct.pack("<I", 0)), (80, struct.pack("<I", 4097)),
                             (84, struct.pack("<I", 256))):
            self.geometry = make_gpt(self.path, self.manifest)
            change_header(self.path, self.geometry, "primary", offset, data)
            with self.subTest(offset=offset, data=data), self.assertRaises(target.IdentityError):
                self.read()
        self.geometry = make_gpt(self.path, self.manifest)
        change_header(self.path, self.geometry, "backup", 72, struct.pack("<Q", 100))
        with self.assertRaises(target.IdentityError):
            self.read()

    def test_invalid_hybrid_mbr_and_missing_gpt_signature_rejected(self):
        for offset, data in ((510, b"\0\0"), (466, b"\x07"), (454, struct.pack("<I", 2)),
                             (458, struct.pack("<I", 10)), (512, b"BAD PART")):
            self.geometry = make_gpt(self.path, self.manifest)
            with self.path.open("r+b") as stream:
                stream.seek(offset)
                stream.write(data)
            with self.subTest(offset=offset), self.assertRaises(target.IdentityError):
                self.read()

    def test_non_gpt_media_can_be_excluded_including_2048_sector_optical(self):
        for sector in (512, 2048, 4096):
            self.assertIsNone(target.read_gpt(io.BytesIO(bytes(8 * sector)), sector, 8 * sector))
        data = bytearray(8 * 2048)
        data[450] = 0xEE
        with self.assertRaisesRegex(target.IdentityError, "unsupported"):
            target.read_gpt(io.BytesIO(data), 2048, len(data))

    def test_optical_gpt_identities_remain_visible_to_collision_checks(self):
        with self.path.open("rb") as stream:
            disks, partitions = target._optical_identities(stream, self.geometry["size"])
        claim = self.manifest["installationTarget"]["disk"]
        self.assertEqual(disks, {claim["diskGuid"]})
        self.assertEqual(partitions, {part["partitionGuid"] for part in claim["partitions"]})
        with self.assertRaisesRegex(target.IdentityError, "disk GUID"):
            target._check_optical_collisions([claim], disks, set())
        with self.assertRaisesRegex(target.IdentityError, "partition GUID"):
            target._check_optical_collisions([claim], set(), partitions)
        target._check_optical_collisions([claim], {OTHER_GUID}, {OTHER_GUID})

    def test_optical_identity_check_rejects_corrupt_gpt(self):
        with self.path.open("r+b") as stream:
            stream.seek(self.geometry["size"] - 512 + 16)
            stream.write(b"BAD!")
        with self.path.open("rb") as stream, self.assertRaises(target.IdentityError):
            target._optical_identities(stream, self.geometry["size"])

    def test_empty_gpt_keeps_disk_identity_for_clone_detection(self):
        self.manifest["installationTarget"]["disk"]["partitions"] = []
        self.geometry = make_gpt(self.path, self.manifest)
        layout = self.read()
        self.assertEqual(layout["partitions"], [])
        self.assertEqual(layout["diskGuid"], FIXTURE["installationTarget"]["disk"]["diskGuid"])

    def test_labels_and_attributes_not_identity_but_gpt_copies_agree(self):
        change_table(self.path, self.geometry, 48, struct.pack("<Q", 0x1000000000000000))
        change_table(self.path, self.geometry, 56, "renamed".encode("utf-16-le"))
        parsed = self.read()
        self.assertEqual(target._identity(parsed), target._identity(self.manifest["installationTarget"]["disk"]))

    def test_unused_nonempty_slot_and_short_reads_fail(self):
        change_table(self.path, self.geometry, 128 + 16, uuid.UUID(OTHER_GUID).bytes_le)
        with self.assertRaisesRegex(target.IdentityError, "unused"):
            self.read()
        with self.assertRaisesRegex(target.IdentityError, "short read"):
            target.read_gpt(io.BytesIO(b"abc"), 512, 4096)

    def test_deterministic_bit_corruption_never_passes_crc(self):
        rng = random.Random(72)
        original = self.path.read_bytes()[:512 + 92]
        for _ in range(12):
            self.geometry = make_gpt(self.path, self.manifest)
            offset = 512 + rng.randrange(8, 92)
            with self.path.open("r+b") as stream:
                stream.seek(offset)
                stream.write(bytes([original[offset] ^ (1 << rng.randrange(8))]))
            with self.assertRaises(target.IdentityError):
                self.read()


class KernelBindingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.image = self.directory / "gpt.img"
        self.manifest = image_manifest(4096)
        self.geometry = make_gpt(self.image, self.manifest)
        with self.image.open("rb") as stream:
            self.disk = target.read_gpt(stream, 4096, self.geometry["size"])
        self.disk_node = self.directory / "fake0"
        self.disk_node.mkdir()
        (self.disk_node / "queue").mkdir()
        (self.disk_node / "queue/logical_block_size").write_text("4096")
        (self.disk_node / "dev").write_text("240:0")
        (self.disk_node / "size").write_text(str(self.geometry["size"] // 512))
        self.nodes = [self.disk_node]
        self.ids = {"/dev/fake0": (240, 0)}
        for part in self.disk["partitions"]:
            number = part["partitionNumber"]
            node = self.disk_node / f"fake0p{number}"
            node.mkdir()
            for name, value in (("partition", number), ("start", part["offsetBytes"] // 512),
                                ("size", part["lengthBytes"] // 512), ("dev", f"240:{number}")):
                (node / name).write_text(str(value))
            self.nodes.append(node)
            self.ids[f"/dev/{node.name}"] = (240, number)
        self.real_stat = os.stat

    def fake_stat(self, path, *args, **kwargs):
        value = str(path)
        if value.startswith("/dev/"):
            if value not in self.ids:
                raise AssertionError("test attempted to inspect a host device")
            major, minor = self.ids[value]
            return SimpleNamespace(st_mode=stat.S_IFBLK, st_rdev=os.makedev(major, minor))
        return self.real_stat(path, *args, **kwargs)

    def bind(self):
        with mock.patch.object(target.os, "stat", side_effect=self.fake_stat):
            return target._bind_kernel_table(copy.deepcopy(self.disk), self.disk_node, self.nodes)

    def test_4kn_sysfs_still_counts_512_byte_sectors_and_handles_slot_gaps(self):
        bound = self.bind()
        self.assertEqual(bound["partitions"][3]["devicePath"], "/dev/fake0p7")

    def test_stale_kernel_offset_size_and_number_fail(self):
        part = self.nodes[1]
        for name, value in (("start", "999"), ("size", "999"), ("partition", "99"),
                            ("partition", "0"), ("partition", "-1")):
            path = part / name
            original = path.read_text()
            path.write_text(value)
            with self.subTest(name=name, value=value), self.assertRaises(target.IdentityError):
                self.bind()
            path.write_text(original)

    def test_missing_extra_and_duplicate_kernel_partitions_fail(self):
        original_nodes = list(self.nodes)
        self.nodes.pop()
        with self.assertRaises(target.IdentityError):
            self.bind()
        self.nodes = original_nodes + [original_nodes[-1]]
        with self.assertRaises(target.IdentityError):
            self.bind()
        self.nodes = original_nodes
        extra = self.disk_node / "fake0p99"
        extra.mkdir()
        (extra / "partition").write_text("99")
        self.nodes.append(extra)
        with self.assertRaises(target.IdentityError):
            self.bind()

    def test_wrong_device_id_or_nonblock_node_fail(self):
        (self.nodes[1] / "dev").write_text("240:200")
        with self.assertRaises(target.IdentityError):
            self.bind()
        (self.nodes[1] / "dev").write_text("240:1")
        fake = self.fake_stat

        def regular_device(path, *args, **kwargs):
            if str(path).startswith("/dev/"):
                return SimpleNamespace(st_mode=stat.S_IFREG, st_rdev=0)
            return fake(path, *args, **kwargs)

        with mock.patch.object(target.os, "stat", side_effect=regular_device), self.assertRaises(target.IdentityError):
            target._bind_kernel_table(self.disk, self.disk_node, self.nodes)

    def collect(self, ioctl_override=None, inventory_changed=False, open_error=None, change_after_bind=None,
                optical_pins=None):
        """Exercise collector with every external surface replaced by fixtures."""
        fake_root = mock.Mock()
        fake_root.iterdir.side_effect = [iter(self.nodes), iter(self.nodes[:-1] if inventory_changed else self.nodes)]
        path_class = Path

        def fake_path(value):
            if str(value) != "/sys/class/block":
                raise AssertionError("collector unexpectedly requested another root")
            return fake_root

        def fake_open(path, mode, buffering):
            self.assertEqual((path, mode, buffering), ("/dev/fake0", "rb", 0))
            if open_error:
                raise open_error
            return self.image.open(mode, buffering=buffering)

        def ioctl(fd, request, buffer):
            if ioctl_override:
                return ioctl_override(fd, request, buffer)
            if request == 0x80081272:
                return struct.pack("=Q", self.geometry["size"])
            if request == 0x1268:
                return struct.pack("=I", 4096)
            raise AssertionError("unexpected ioctl")

        fake_fcntl = SimpleNamespace(ioctl=ioctl)
        real_bind = target._bind_kernel_table

        def bind_and_change(*args):
            result = real_bind(*args)
            if change_after_bind:
                change_after_bind()
            return result

        with mock.patch.object(target, "Path", side_effect=fake_path), \
                mock.patch.object(target, "open", create=True, side_effect=fake_open), \
                mock.patch.object(target.os, "stat", side_effect=self.fake_stat), \
                mock.patch.object(target.os, "fstat", return_value=SimpleNamespace(st_mode=stat.S_IFBLK, st_rdev=os.makedev(240, 0))), \
                mock.patch.object(target.sys, "platform", "linux"), \
                mock.patch.dict("sys.modules", {"fcntl": fake_fcntl}), \
                mock.patch.object(target, "_bind_kernel_table", side_effect=bind_and_change):
            return target.collect_layouts(verified_optical_media=optical_pins)

    def test_hybrid_optical_exemption_requires_exact_pin_in_real_collector(self):
        data = bytearray(8192)
        data[450] = 0xEE
        self.image.write_bytes(data)
        self.nodes = [self.disk_node]
        (self.disk_node / "queue/logical_block_size").write_text("2048")
        (self.disk_node / "size").write_text("16")
        (self.disk_node / "device").mkdir()
        (self.disk_node / "device/type").write_text("5")
        (self.disk_node / "ro").write_text("1")

        def optical_ioctl(fd, request, buffer):
            return {0x80081272: struct.pack("=Q", 8192),
                    0x1268: struct.pack("=I", 2048),
                    0x125E: struct.pack("=I", 1)}[request]

        with self.assertRaises(target.IdentityError):
            self.collect(ioctl_override=optical_ioctl)
        pins = {hashlib.sha256(data).hexdigest(): len(data)}
        self.assertEqual(self.collect(ioctl_override=optical_ioctl, optical_pins=pins), [])
        with self.assertRaisesRegex(target.IdentityError, "checksum"):
            self.collect(ioctl_override=optical_ioctl, optical_pins={"0" * 64: 8192})
        with self.assertRaisesRegex(target.IdentityError, "inventory changed"):
            self.collect(ioctl_override=optical_ioctl, optical_pins=pins, inventory_changed=True)

    def test_full_collector_uses_only_fake_devices_and_production_gpt_parser(self):
        layouts = self.collect()
        self.assertEqual(target.resolve(self.manifest, layouts, INSTALLATION_ID)["rootDevice"], "/dev/fake0p7")

    def test_collection_permissions_ioctl_geometry_and_inventory_changes_fail(self):
        with self.assertRaises(PermissionError):
            self.collect(open_error=PermissionError("fixture denied"))

        def wrong_size(fd, request, buffer):
            return struct.pack("=Q", self.geometry["size"] + 512) if request == 0x80081272 else struct.pack("=I", 4096)

        with self.assertRaises(target.IdentityError):
            self.collect(ioctl_override=wrong_size)
        with self.assertRaises(target.IdentityError):
            self.collect(inventory_changed=True)

    def test_gpt_or_sysfs_change_during_collection_fails(self):
        def change_guid():
            for side in ("primary", "backup"):
                change_header(self.image, self.geometry, side, 56, uuid.UUID(OTHER_GUID).bytes_le)

        with self.assertRaisesRegex(target.IdentityError, "GPT changed"):
            self.collect(change_after_bind=change_guid)
        make_gpt(self.image, self.manifest)
        with self.assertRaisesRegex(target.IdentityError, "disk changed"):
            self.collect(change_after_bind=lambda: (self.disk_node / "size").write_text("99"))


if __name__ == "__main__":
    unittest.main()
