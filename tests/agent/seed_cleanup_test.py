"""Installer-seed removal at first boot: exact GPT identity or nothing. Only sfdisk on an image file runs."""

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
from tempfile import TemporaryDirectory
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]
HAS_SFDISK = shutil.which("sfdisk") is not None


def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    # The dataclass in the shared module resolves its annotations through sys.modules.
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


boot = load("igloo_boot_seed_cleanup", "distros/_shared/agent/igloo_boot.py")
debian = load("debian_family_seed_cleanup_agent", "distros/_debian-family/agent/agent.py")
fedora = load("fedora_seed_cleanup_agent", "distros/fedora-kde/agent/agent.py")

DISK = "11111111-2222-3333-4444-555555555555"
SEED = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
WINDOWS_ESP = "99999999-8888-7777-6666-555555555555"
RESERVED = "22222222-3333-4444-5555-666666666666"
WINDOWS = "33333333-4444-5555-6666-777777777777"
LINUX = "44444444-5555-6666-7777-888888888888"
BASIC = "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"
ESP = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B"
MSR = "E3C9E316-0B5C-4DB8-817D-F92DF00215AE"
LINUX_FS = "0FC63DAF-8483-4772-8E79-3D69D8477DE4"
IDENTITY_PATH = ("/var/lib/igloo/seed-identity.json",)
READ_ONLY = (["lsblk"], ["blkid"], ["findmnt"], ["sfdisk", "--dump"], ["efibootmgr", "-v"])
ENTRIES = [["efibootmgr", "-b", "0005", "-B"], ["efibootmgr", "-b", "0080", "-B"]]


def efi_listing(seed=SEED):
    return "\n".join([
        "BootCurrent: 0003",
        "BootOrder: 0003,0080,0000,0005,0081",
        "Boot0000* Windows Boot Manager\tHD(1,GPT," + WINDOWS_ESP + ",0x800,0x32000)/File(\\EFI\\Microsoft\\Boot\\bootmgfw.efi)",
        "Boot0003* debian\tHD(1,GPT," + WINDOWS_ESP + ",0x800,0x32000)/File(\\EFI\\debian\\shimx64.efi)",
        "Boot0005* UEFI OS\tHD(4,GPT," + seed.upper() + ",0xf4240,0x800000)/\\EFI\\BOOT\\BOOTX64.EFI",
        "Boot0080  iGloo distribution installer\tHD(4,GPT," + seed + ",0xf4240,0x800000)/File(\\EFI\\BOOT\\BOOTX64.EFI)",
        "      dp: 04 01 2a 00 04 00 00 00 / 7f ff 04 00",
        "Boot0081* iGloo distribution installer\tHD(2,GPT,12345678-1234-1234-1234-123456789abc,0x1,0x2)/File(\\EFI\\BOOT\\BOOTX64.EFI)",
    ]) + "\n"


def node(disk, number):
    return disk + ("p" if disk[-1].isdigit() else "") + str(number)


def table(disk="/dev/sda", seed_node="/dev/sda4", seed_type=BASIC, label="gpt", with_seed=True, with_esp=True):
    lines = [f"label: {label}", f"label-id: {DISK.upper()}", f"device: {disk}", "unit: sectors",
             "first-lba: 34", "sector-size: 512", ""]
    if with_esp:
        lines.append(f"{node(disk, 1)} : start=2048, size=204800, type={ESP}, uuid={WINDOWS_ESP.upper()}, "
                     'name="EFI system partition", attrs="GUID:63"')
    if with_seed:
        lines.append(f"{seed_node} : start=     1000000, size=     8388608, type={seed_type}, "
                     f'uuid={SEED.upper()}, name="Basic data partition"')
    return "\n".join(lines) + "\n"


def make_volume(root, extra=()):
    """What the seed volume looks like when mounted: iGloo's files, its receipt and what Windows adds."""
    listed = ["igloo-seed-identity.json", "EFI/BOOT/BOOTX64.EFI", "igloo-boot/linux",
              "migration-manifest.json", "files/Documents/Résumé.docx"]
    present = ["igloo-seed-identity.json", "EFI/BOOT/BOOTX64.EFI", "igloo-boot/linux",
               "migration-manifest.json", "FILES/Documents/Résumé.docx",
               "System Volume Information/WPSettings.dat", "igloo-logs/agent.log",
               "igloo-boot/grubenv", *extra]
    volume = Path(root) / "volume"
    for name in present:
        (volume / name).parent.mkdir(parents=True, exist_ok=True)
        (volume / name).write_bytes(b"fixture")
    (volume / "igloo-seed-reuse.json").write_text(json.dumps(
        {"version": 1, "target": {}, "files": [{"path": name, "sha256": "0" * 64} for name in listed]}),
        encoding="utf-8")
    return volume


class Commands:
    """A scripted run_cmd: answers the reads it knows, records the writes, rejects everything else."""

    def __init__(self, after=None, disk="/dev/sda", partition="/dev/sda4", fail=(), **answers):
        self.calls = []
        self.events = []
        self.deleted = False
        self.fail = set(fail)
        self.inventory = [["mount", "-t", "vfat", "-o", "ro,nosuid,nodev,noexec,utf8", partition,
                           str(boot._SEED_INVENTORY_MOUNT)], ["umount", str(boot._SEED_INVENTORY_MOUNT)]]
        self.number = partition[len(disk):].lstrip("p")
        self.delete = ["sfdisk", "--no-tell-kernel", "--delete", disk, self.number]
        self.before = answers.pop("table", table(disk, partition))
        self.after = table(disk, with_seed=False) if after is None else after
        self.answers = {
            "lsblk -rno PATH,PARTUUID": f"{disk}\n{node(disk, 1)} {WINDOWS_ESP}\n{partition} {SEED.upper()}\n",
            f"lsblk -rno PKNAME {partition}": disk[len("/dev/"):] + "\n",
            f"blkid -s PTUUID -o value {disk}": DISK + "\n",
            f"lsblk -dnro RM,TRAN {disk}": "0 nvme\n",
            f"blkid -p -s TYPE -o value {partition}": "vfat\n",
            f"findmnt -rno TARGET -S {partition}": "",
            "efibootmgr -v": efi_listing(),
        }
        self.answers.update(answers)
        self.allowed = [self.delete, ["partx", "--delete", "--nr", self.number, disk], *ENTRIES,
                        *(["umount", target] for target in boot._SEED_MOUNTPOINTS)]

    def __call__(self, cmd, check=True, **_):
        cmd = list(cmd)
        self.calls.append(cmd)
        text = " ".join(cmd)
        if text in self.fail:
            if check:
                raise RuntimeError("fixture command failure: " + text)
            return SimpleNamespace(stdout="", returncode=1)
        if cmd[:2] == ["sfdisk", "--dump"]:
            return SimpleNamespace(stdout=self.after if self.deleted else self.before, returncode=0)
        if cmd == self.delete:
            self.deleted = True
            self.events.append("delete")
            # The exit code of a delete on a busy disk carries no meaning; the table does.
            return SimpleNamespace(stdout="", returncode=1)
        if text in self.answers or cmd in self.allowed or cmd in self.inventory:
            return SimpleNamespace(stdout=self.answers.get(text, ""), returncode=0)
        raise AssertionError("The fixture has no answer for: " + text)

    def writes(self):
        return [call for call in self.calls if call not in self.inventory
                and not any(call[:len(read)] == read for read in READ_ONLY)]


def platform(commands):
    return boot.Boot(commands, Mock(), Path("/unused"), Path("/unused"), [], Path("/unused"))


class SeedCleanupTests(unittest.TestCase):
    def setUp(self):
        self.temporary = TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.state = Path(self.temporary.name) / "state"
        self.state.mkdir()
        self.identity = self.state / "seed-identity.json"
        self.done = self.state / ".done"
        self.write_identity()
        self.mount(make_volume(self.temporary.name))
        tools = patch.object(boot.shutil, "which", side_effect=lambda name: "/usr/sbin/" + name)
        tools.start()
        self.addCleanup(tools.stop)

    def mount(self, volume):
        mounted = patch.object(boot, "_SEED_INVENTORY_MOUNT", volume)
        mounted.start()
        self.addCleanup(mounted.stop)
        self.volume = volume

    def write_identity(self, **changes):
        identity = {"version": 1, "diskGuid": DISK, "partitionGuid": SEED, "files": []}
        identity.update(changes)
        self.identity.write_text(json.dumps(identity), encoding="utf-8")

    def remove(self, commands):
        boot.remove_installer_seed(platform(commands), self.identity, self.done)

    def identity_for(self, agent):
        return patch.object(agent, "Path", side_effect=lambda *parts: self.identity
                            if parts == IDENTITY_PATH else Path(*parts))

    def test_the_partition_with_the_seed_identity_and_its_entries_are_removed(self):
        commands = Commands(**{"findmnt -rno TARGET -S /dev/sda4": "/run/igloo-seed\n"})

        self.remove(commands)

        self.assertEqual(commands.writes(), [
            ["umount", "/run/igloo-seed"],
            ["sfdisk", "--no-tell-kernel", "--delete", "/dev/sda", "4"],
            ["partx", "--delete", "--nr", "4", "/dev/sda"],
            *ENTRIES,
        ])
        order = [commands.calls.index(call) for call in (["umount", "/run/igloo-seed"], *commands.inventory,
                                                         commands.delete)]
        self.assertEqual(order, sorted(order))

    def test_a_file_igloo_did_not_write_keeps_the_partition(self):
        for stranger in ("Vakantie/foto.jpg", "notes.txt", "$RECYCLE.BIN/S-1-5-21/$R1A2B3.docx",
                         "EFI/BOOT/extra.efi", ".igloo-redact-0123456789abcdef0123456789abcdef"):
            with self.subTest(stranger), TemporaryDirectory() as other:
                with patch.object(boot, "_SEED_INVENTORY_MOUNT", make_volume(other, [stranger])):
                    commands = Commands()
                    with self.assertRaisesRegex(RuntimeError, "1 file.s. iGloo did not write, such as "):
                        self.remove(commands)
                    self.assertEqual(commands.writes(), [])
                    self.assertEqual(commands.calls[-1], commands.inventory[1])
                self.assertFalse(self.done.exists())

    def test_a_seed_without_a_readable_receipt_is_left_alone(self):
        receipt = self.volume / "igloo-seed-reuse.json"
        for content in (None, b"{}", b"[]", b'{"files": [{"sha256": "0"}]}'):
            with self.subTest(content):
                receipt.unlink(missing_ok=True)
                if content is not None:
                    receipt.write_bytes(content)
                commands = Commands()
                with self.assertRaises((OSError, RuntimeError)):
                    self.remove(commands)
                self.assertEqual(commands.writes(), [])
                self.assertFalse(self.done.exists())

    def test_a_volume_that_cannot_be_mounted_or_released_is_left_alone(self):
        for failing in Commands().inventory:
            with self.subTest(failing[0]):
                commands = Commands(fail=[" ".join(failing)])
                with self.assertRaisesRegex(RuntimeError, "fixture command failure"):
                    self.remove(commands)
                self.assertEqual(commands.writes(), [])
                self.assertFalse(self.done.exists())

    def test_a_two_digit_partition_on_an_nvme_disk_keeps_its_number(self):
        commands = Commands(disk="/dev/nvme0n1", partition="/dev/nvme0n1p14")

        self.remove(commands)

        self.assertEqual(commands.writes()[:2], [
            ["sfdisk", "--no-tell-kernel", "--delete", "/dev/nvme0n1", "14"],
            ["partx", "--delete", "--nr", "14", "/dev/nvme0n1"],
        ])

    def test_a_braced_upper_case_identity_names_the_same_partition(self):
        self.write_identity(diskGuid="{" + DISK.upper() + "}", partitionGuid="{" + SEED.upper() + "}")
        commands = Commands()

        self.remove(commands)

        self.assertIn(commands.delete, commands.writes())

    def test_a_disk_without_a_named_transport_is_not_a_usb_disk(self):
        commands = Commands(**{"lsblk -dnro RM,TRAN /dev/sda": "0 \n"})

        self.remove(commands)

        self.assertIn(commands.delete, commands.writes())

    def test_completion_is_synced_to_disk_before_the_partition_goes(self):
        commands = Commands()
        synced = os.fsync

        def record(descriptor):
            kind = "directory" if stat.S_ISDIR(os.fstat(descriptor).st_mode) else "file"
            commands.events.append("fsync " + kind)
            synced(descriptor)

        with patch.object(boot.os, "fsync", side_effect=record):
            self.remove(commands)

        self.assertEqual(commands.events, ["fsync file", "fsync directory", "delete"])
        self.assertEqual(self.done.read_bytes(), b"complete\n")
        self.assertEqual(sorted(path.name for path in self.state.iterdir()), [".done", "seed-identity.json"])

    def test_a_marker_that_cannot_be_written_stops_before_the_delete(self):
        commands = Commands()

        with patch.object(boot, "publish_completion", side_effect=OSError("read-only state directory")):
            with self.assertRaises(OSError):
                self.remove(commands)

        self.assertEqual(commands.writes(), [])

    def test_an_existing_completion_marker_is_kept(self):
        self.done.write_bytes(b"1\n")

        boot.publish_completion(self.done)

        self.assertEqual(self.done.read_bytes(), b"1\n")

    def test_an_entry_is_chosen_by_its_device_path_not_its_name(self):
        numbers = boot.boot_entries_for_partition(efi_listing(), boot.uuid.UUID(SEED))

        self.assertEqual(numbers, ["0005", "0080"])

    def test_a_partition_that_is_already_gone_only_loses_its_entries(self):
        commands = Commands(**{"lsblk -rno PATH,PARTUUID": f"/dev/sda\n/dev/sda1 {WINDOWS_ESP}\n"})

        self.remove(commands)

        self.assertEqual(commands.writes(), ENTRIES)
        self.assertFalse(self.done.exists())

    def test_any_doubt_about_the_partition_stops_before_the_first_write(self):
        row_without_guid = table() + f"/dev/sda5 : start=9000000, size=2048, type={BASIC}\n"
        doubts = {
            "More than one partition": {"lsblk -rno PATH,PARTUUID": f"/dev/sda4 {SEED}\n/dev/sdb1 {SEED}\n"},
            "a disk its identity does not name": {"blkid -s PTUUID -o value /dev/sda": WINDOWS_ESP + "\n"},
            "no single parent disk": {"lsblk -rno PKNAME /dev/sda4": "\n"},
            "removable or USB disk": {"lsblk -dnro RM,TRAN /dev/sda": "0 usb\n"},
            "a removable or USB disk": {"lsblk -dnro RM,TRAN /dev/sda": "1 sata\n"},
            "on a removable or USB disk": {"lsblk -dnro RM,TRAN /dev/sda": "\n"},
            "is not FAT32": {"blkid -p -s TYPE -o value /dev/sda4": "ntfs\n"},
            "is not a data partition": {"table": table(seed_type=ESP)},
            "does not match the running kernel": {"table": table(seed_node="/dev/sda3")},
            "is not on a GPT disk": {"table": table(label="dos")},
            "is not understood": {"table": row_without_guid},
            "is mounted at /boot/efi": {"findmnt -rno TARGET -S /dev/sda4": "/boot/efi\n"},
        }
        for reason, answers in doubts.items():
            with self.subTest(reason):
                commands = Commands(**answers)
                with self.assertRaisesRegex(RuntimeError, reason):
                    self.remove(commands)
                self.assertEqual(commands.writes(), [])
                self.assertFalse(self.done.exists())

    def test_a_missing_tool_stops_before_any_command(self):
        commands = Commands()
        with patch.object(boot.shutil, "which",
                          side_effect=lambda name: None if name == "partx" else "/usr/sbin/" + name):
            with self.assertRaisesRegex(RuntimeError, "Not installed: partx"):
                self.remove(commands)

        self.assertEqual(commands.calls, [])

    def test_a_seed_that_cannot_be_unmounted_is_left_alone(self):
        commands = Commands(fail=["umount /run/igloo-seed"],
                            **{"findmnt -rno TARGET -S /dev/sda4": "/run/igloo-seed\n"})

        with self.assertRaisesRegex(RuntimeError, "umount"):
            self.remove(commands)

        self.assertEqual(commands.writes(), [["umount", "/run/igloo-seed"]])
        self.assertFalse(self.done.exists())

    def test_a_delete_that_did_not_take_leaves_the_firmware_entries_alone(self):
        commands = Commands(after=table())

        with self.assertRaisesRegex(RuntimeError, "still in the partition table"):
            self.remove(commands)

        self.assertEqual(commands.writes(), [commands.delete])

    def test_a_table_that_lost_more_than_the_seed_stops_before_the_firmware_entries(self):
        commands = Commands(after=table(with_seed=False, with_esp=False))

        with self.assertRaisesRegex(RuntimeError, "changed beyond the installer partition"):
            self.remove(commands)

        self.assertEqual(commands.writes(), [commands.delete])

    def test_a_kernel_that_keeps_the_partition_still_loses_the_entries(self):
        commands = Commands(fail=["partx --delete --nr 4 /dev/sda"])

        self.remove(commands)

        self.assertEqual(commands.writes()[-2:], ENTRIES)

    def test_an_entry_the_firmware_keeps_is_a_warning(self):
        commands = Commands(fail=["efibootmgr -b 0005 -B"])
        target = platform(commands)

        boot.remove_installer_seed(target, self.identity, self.done)

        self.assertEqual(commands.writes()[-2:], ENTRIES)
        self.assertIn("Boot%s", target.logger.warning.call_args.args[0])
        self.assertEqual(target.logger.warning.call_args.args[1], "0005")

    def test_an_unusable_identity_stops_before_any_command(self):
        for change in ({"version": 2}, {"partitionGuid": "not-a-guid"},
                       {"diskGuid": "00000000-0000-0000-0000-000000000000"}):
            with self.subTest(change):
                self.write_identity(**change)
                commands = Commands()
                with self.assertRaises(RuntimeError):
                    self.remove(commands)
                self.assertEqual(commands.calls, [])
        self.identity.unlink()
        with self.assertRaises(OSError):
            self.remove(Commands())

    def test_a_refusal_is_a_warning_in_both_agents_and_never_a_failed_pass(self):
        for agent in (debian, fedora):
            with self.subTest(agent.__name__), \
                    patch.object(agent, "igloo_boot", boot), \
                    patch.object(agent, "run_cmd") as run_cmd, \
                    patch.object(agent, "logger") as logger, \
                    patch.object(agent.Path, "is_mount", return_value=False), \
                    patch.object(boot, "seed_carries_user_data", return_value=False), \
                    patch.object(fedora, "_require_copied_user_files"), \
                    patch.object(boot, "remove_installer_seed",
                                 side_effect=RuntimeError("fixture doubt")) as remove:
                agent.cleanup_installer_partitions({})
                self.assertIn("did not finish", logger.warning.call_args.args[0])
                self.assertEqual(str(logger.warning.call_args.args[1]), "fixture doubt")
                run_cmd.assert_not_called()
                self.assertEqual(remove.call_args.args[1:],
                                 (Path("/var/lib/igloo/seed-identity.json"), Path("/var/lib/igloo/.done")))

    def test_a_busy_seed_mount_is_a_warning_in_the_debian_family(self):
        with patch.object(debian, "igloo_boot", boot), patch.object(debian, "logger") as logger, \
                patch.object(debian.Path, "is_mount", return_value=True), \
                patch.object(debian, "run_cmd", side_effect=RuntimeError("umount: target is busy")), \
                patch.object(boot, "remove_installer_seed") as remove:
            debian.cleanup_installer_partitions({})

        remove.assert_not_called()
        self.assertIn("target is busy", str(logger.warning.call_args.args[1]))

    def test_an_agent_without_the_shared_module_leaves_the_seed_in_place(self):
        for agent in (debian, fedora):
            with self.subTest(agent.__name__), patch.object(agent, "igloo_boot", None), \
                    patch.object(agent.Path, "is_mount", return_value=False), \
                    patch.object(agent, "run_cmd") as run_cmd, patch.object(agent, "logger") as logger:
                agent.cleanup_installer_partitions({})
                self.assertIn("not staged", str(logger.warning.call_args.args[1]))
                run_cmd.assert_not_called()

    def test_only_the_users_own_files_count_as_user_data(self):
        self.write_identity(files=[{"path": "preseed.cfg", "sha256": "0" * 64},
                                   {"path": "igloo-agent/agent.py", "sha256": "0" * 64}])
        self.assertFalse(boot.seed_carries_user_data(self.identity))
        for path in ("files/Documents/a.txt", "Browser-Profiles/Mozilla/places.sqlite"):
            with self.subTest(path):
                self.write_identity(files=[{"path": path, "sha256": "0" * 64}])
                self.assertTrue(boot.seed_carries_user_data(self.identity))
        self.write_identity(files=None)
        with self.assertRaises(RuntimeError):
            boot.seed_carries_user_data(self.identity)

    def test_debian_family_keeps_a_seed_that_carries_user_files(self):
        self.write_identity(files=[{"path": "files/Documents/a.txt", "sha256": "0" * 64}])
        with self.identity_for(debian), patch.object(debian, "igloo_boot", boot), \
                patch.object(Path, "is_mount", return_value=False), \
                patch.object(debian, "run_cmd") as run_cmd, patch.object(debian, "logger") as logger, \
                patch.object(boot, "remove_installer_seed") as remove:
            debian.cleanup_installer_partitions({})

        remove.assert_not_called()
        run_cmd.assert_not_called()
        self.assertIn("holds user files", str(logger.warning.call_args.args[1]))

    def test_fedora_keeps_the_seed_until_the_copied_files_match_its_identity(self):
        home = Path(self.temporary.name) / "home"
        (home / "tester" / "Documents" / "Nested").mkdir(parents=True)
        kept = home / "tester" / "Documents" / "Nested" / "kept.txt"
        kept.write_bytes(b"selected data")
        digest = hashlib.sha256(b"selected data").hexdigest().upper()
        self.write_identity(files=[{"path": "files/Documents/Nested/kept.txt", "sha256": digest},
                                   {"path": "ks.cfg", "sha256": "0" * 64}])
        manifest = {"user": {"preferredLinuxUsername": "tester"}}

        fedora._require_copied_user_files(manifest, self.identity, home)

        kept.write_bytes(b"changed after the copy")
        with self.assertRaisesRegex(RuntimeError, "differs from the installer partition"):
            fedora._require_copied_user_files(manifest, self.identity, home)
        kept.unlink()
        with self.assertRaises(RuntimeError):
            fedora._require_copied_user_files(manifest, self.identity, home)
        with self.assertRaisesRegex(RuntimeError, "without a Linux user"):
            fedora._require_copied_user_files({}, self.identity, home)

    def test_fedora_does_not_touch_the_seed_when_a_copied_file_is_missing(self):
        self.write_identity(files=[{"path": "files/Documents/gone.txt", "sha256": "A" * 64}])
        with self.identity_for(fedora), patch.object(fedora, "igloo_boot", boot), \
                patch.object(fedora, "run_cmd") as run_cmd, patch.object(fedora, "logger") as logger, \
                patch.object(boot, "remove_installer_seed") as remove:
            fedora.cleanup_installer_partitions({"user": {"preferredLinuxUsername": "tester"}})

        remove.assert_not_called()
        run_cmd.assert_not_called()
        self.assertIn("missing or redirected file", str(logger.warning.call_args.args[1]))


@unittest.skipUnless(HAS_SFDISK, "sfdisk is not installed")
class RealPartitionTableTests(unittest.TestCase):
    """The table reads and the delete run through the installed sfdisk, on an image file."""

    def setUp(self):
        self.temporary = TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.identity = self.root / "seed-identity.json"
        self.identity.write_text(json.dumps({"version": 1, "diskGuid": DISK, "partitionGuid": SEED,
                                             "files": []}), encoding="utf-8")
        self.done = self.root / ".done"
        self.calls = []
        self.delete_status = None
        for fixture in (patch.object(boot.shutil, "which", side_effect=lambda name: "/usr/sbin/" + name),
                        patch.object(boot, "_SEED_INVENTORY_MOUNT", make_volume(self.root))):
            fixture.start()
            self.addCleanup(fixture.stop)

    def build(self, name, partitions):
        """An image called name with the given (type, GUID) partitions; returns the seed's device node."""
        self.image = self.root / name
        self.device = "/dev/" + name
        with self.image.open("wb") as image:
            image.truncate(64 * 1024 * 1024)
        rows = [f'start={2048 + index * 4096}, size=4096, type={kind}, uuid={guid}, name="Basic data partition", attrs="GUID:63"'
                for index, (kind, guid) in enumerate(partitions)]
        layout = "\n".join(["label: gpt", f"label-id: {DISK}", "unit: sectors", "", *rows]) + "\n"
        subprocess.run(["sfdisk", str(self.image)], input=layout, text=True,
                       capture_output=True, check=True, timeout=60)
        seed = next(row.node for row in self.rows() if str(row.guid) == SEED).replace(str(self.image), self.device)
        self.answers = {
            "lsblk -rno PATH,PARTUUID": f"{self.device}\n{seed} {SEED}\n",
            f"lsblk -rno PKNAME {seed}": name + "\n",
            f"blkid -s PTUUID -o value {self.device}": DISK + "\n",
            f"lsblk -dnro RM,TRAN {self.device}": "0 nvme\n",
            f"blkid -p -s TYPE -o value {seed}": "vfat\n",
            f"findmnt -rno TARGET -S {seed}": "",
            "efibootmgr -v": efi_listing(),
        }
        return seed

    def run_cmd(self, cmd, check=True, **_):
        self.calls.append(list(cmd))
        if cmd[0] != "sfdisk":
            return SimpleNamespace(stdout=self.answers.get(" ".join(cmd), ""), returncode=0)
        # Only the image may reach the real tool, never a device this machine has.
        if [part for part in cmd if part.startswith("/")] != [self.device]:
            raise AssertionError(f"sfdisk was given an unexpected device: {cmd}")
        real = [str(self.image) if part == self.device else part for part in cmd]
        done = subprocess.run(real, capture_output=True, text=True, timeout=60)
        if "--delete" in cmd:
            self.delete_status = done.returncode
        return SimpleNamespace(stdout=done.stdout.replace(str(self.image), self.device),
                               returncode=done.returncode)

    def rows(self):
        dump = subprocess.run(["sfdisk", "--dump", str(self.image)], capture_output=True,
                              text=True, check=True, timeout=60).stdout
        return boot._table_rows(dump)

    def test_the_installed_sfdisk_removes_exactly_the_seed_partition(self):
        self.build("igloo-fixture-disk", [(ESP, WINDOWS_ESP), (MSR, RESERVED), (BASIC, WINDOWS),
                                          (BASIC, SEED), (LINUX_FS, LINUX)])
        before = self.rows()

        boot.remove_installer_seed(platform(self.run_cmd), self.identity, self.done)

        after = self.rows()
        self.assertEqual(self.delete_status, 0)
        self.assertEqual([(row.number, str(row.guid)) for row in after],
                         [("1", WINDOWS_ESP), ("2", RESERVED), ("3", WINDOWS), ("5", LINUX)])
        self.assertEqual([row for row in before if str(row.guid) != SEED], after)
        self.assertTrue(self.done.exists())
        self.assertIn(["partx", "--delete", "--nr", "4", self.device], self.calls)
        self.assertEqual([call for call in self.calls if call[:2] == ["efibootmgr", "-b"]], ENTRIES)

    def test_the_twelfth_partition_of_an_nvme_disk_is_the_one_that_goes(self):
        fillers = [(LINUX_FS, f"{index:08d}-0000-4000-8000-000000000000") for index in range(4, 12)]
        seed = self.build("nvme9n9", [(ESP, WINDOWS_ESP), (MSR, RESERVED), (BASIC, WINDOWS), *fillers,
                                      (BASIC, SEED), (LINUX_FS, LINUX)])
        self.assertEqual(seed, "/dev/nvme9n9p12")
        before = self.rows()

        boot.remove_installer_seed(platform(self.run_cmd), self.identity, self.done)

        self.assertIn(["sfdisk", "--no-tell-kernel", "--delete", self.device, "12"], self.calls)
        self.assertEqual([row for row in before if str(row.guid) != SEED], self.rows())
        self.assertEqual(len(self.rows()), 12)

    def test_a_seed_on_another_table_is_left_alone(self):
        self.build("igloo-fixture-disk", [(ESP, WINDOWS_ESP), (BASIC, WINDOWS), (BASIC, SEED)])
        self.answers[f"blkid -s PTUUID -o value {self.device}"] = WINDOWS + "\n"
        before = self.rows()

        with self.assertRaises(RuntimeError):
            boot.remove_installer_seed(platform(self.run_cmd), self.identity, self.done)

        self.assertEqual(self.rows(), before)
        self.assertFalse(self.done.exists())


if __name__ == "__main__":
    unittest.main()
