import importlib.util
import json
import pathlib
import unittest
import uuid

MODULE = pathlib.Path(__file__).resolve().parents[2] / "distros/_shared/installer/collect_inventory.py"
spec = importlib.util.spec_from_file_location("collect_inventory", MODULE)
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


def identity(number):
    return str(uuid.UUID(int=number))


class Probe:
    def __init__(self, change=None, reverse=False):
        self.change, self.reverse, self.calls, self.passes = change, reverse, [], 0

    def __call__(self, argv):
        self.calls.append(argv)
        name = argv[0]
        if self.change in ("AccessDenied", "Unavailable", "Unsupported"):
            raise collector.ObservationError(self.change, "fixture")
        if name == "lsblk":
            self.passes += 1
            rows = [{"name": "/dev/sdz", "type": "disk", "pkname": None, "size": 100000000, "log-sec": 512}]
            rows += [{"name": "/dev/sdz" + str(i), "type": "part", "pkname": "/dev/sdz", "size": 512000, "log-sec": 512} for i in range(1, 6)]
            if self.change == "parent":
                rows[-1]["pkname"] = "/dev/wrong"
            if self.change == "layered":
                rows[-1]["type"] = "lvm"
            return json.dumps({"blockdevices": list(reversed(rows)) if self.reverse else rows})
        if name == "sfdisk":
            parts = [{"node": "/dev/sdz" + str(i), "start": i * 2048, "size": 1000,
                      "uuid": identity(i), "type": "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" if i in (1, 3) else "0fc63daf-8483-4772-8e79-3d69d8477de4"} for i in range(1, 6)]
            if self.change == "duplicate-partuuid":
                parts[-1]["uuid"] = parts[0]["uuid"]
            if self.change == "changed" and self.passes == 2:
                parts[-1]["start"] += 1
            if self.change == "overflow":
                parts[-1]["size"] = 2**64
            return json.dumps({"partitiontable": {"label": "gpt", "device": "/dev/sdz", "id": identity(99),
                              "unit": "sectors", "sectorsize": 512, "partitions": parts}})
        path = argv[-1]
        index = int(path[-1])
        fs_uuid = "1111-AAAA" if self.change == "duplicate-uuid" else f"{index:04}-AAAA"
        if name == "wipefs":
            if self.change == "malformed":
                return "{"
            signatures = [] if index == 5 else [{"type": "vfat", "uuid": fs_uuid}]
            if self.change == "ambivalent" and index == 4:
                signatures.append({"type": "ext4", "uuid": identity(88)})
            return json.dumps({"signatures": signatures})
        if name == "blkid":
            return f"TYPE=vfat\nVERSION={'FAT16' if self.change == 'fat16' else 'FAT32'}\nUUID={fs_uuid}\n"
        raise AssertionError("unexpected native command")


class InventoryTests(unittest.TestCase):
    def test_two_esps_order_independent_with_absent_root_signature(self):
        one, two = collector.collect(Probe()), collector.collect(Probe(reverse=True))
        self.assertEqual("Available", one["availability"])
        self.assertEqual(one, two)
        self.assertEqual("Absent", one["partitions"][-1]["fileSystem"]["availability"])
        self.assertEqual(2, sum(p["partitionType"] == "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" for p in one["partitions"]))

    def test_commands_are_read_only_and_do_not_mount_or_write(self):
        probe = Probe()
        collector.collect(probe)
        for argv in probe.calls:
            self.assertIn(argv[0], ("lsblk", "sfdisk", "wipefs", "blkid"))
            if argv[0] == "sfdisk":
                self.assertEqual(["sfdisk", "--json", "/dev/sdz"], argv)
            if argv[0] == "wipefs":
                self.assertIn("--no-act", argv)
            if argv[0] == "blkid":
                self.assertIn("--probe", argv)

    def test_duplicate_partuuid(self):
        self.assertEqual("Ambiguous", collector.collect(Probe("duplicate-partuuid"))["availability"])

    def test_duplicate_uuid(self):
        self.assertEqual("Ambiguous", collector.collect(Probe("duplicate-uuid"))["availability"])

    def test_parent_changed(self):
        self.assertEqual("Ambiguous", collector.collect(Probe("parent"))["availability"])

    def test_changed_between_reads(self):
        self.assertEqual("InventoryChangedDuringRead", collector.collect(Probe("changed"))["code"])

    def test_overflow(self):
        self.assertEqual("Ambiguous", collector.collect(Probe("overflow"))["availability"])

    def test_fat16_not_fat32(self):
        self.assertEqual("Ambiguous", collector.collect(Probe("fat16"))["availability"])

    def test_ambivalent_signatures(self):
        self.assertEqual("Ambiguous", collector.collect(Probe("ambivalent"))["availability"])

    def test_malformed_native_output(self):
        self.assertEqual("Ambiguous", collector.collect(Probe("malformed"))["availability"])

    def test_layered_topology_not_guessed(self):
        self.assertNotEqual("Available", collector.collect(Probe("layered"))["availability"])

    def test_native_failure_states_not_absence(self):
        for state in ("AccessDenied", "Unavailable", "Unsupported"):
            with self.subTest(state=state):
                self.assertEqual(state, collector.collect(Probe(state))["availability"])


if __name__ == "__main__":
    unittest.main()
