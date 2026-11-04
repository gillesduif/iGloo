"""Exercise the Debian partman choice's exact precreated ESP boundary.

The fixture replaces only installer device discovery and partman IPC. The
source script still runs through its free-space, GPT, ESP, and action checks.
"""

from pathlib import Path
import os
import subprocess
import tempfile
import unittest


SOURCE = (Path(__file__).resolve().parents[2] / "distros/debian/preseed"
          / "igloo-bound-free-do-option.sh")
TARGET_GUID = "b98088eb-d9eb-4093-ae35-324f2347156c"
DECOY_GUID = "94940e1f-191d-47e6-ad2d-d30330303030"
LINUX_ESP = "41f8948c-43a5-46fa-a05f-2fef79cb5c74"
WINDOWS_ESP = "2ec3247f-f31f-4fc3-902b-f73773737373"
DECOY_ESP = "51db17cd-37b9-4f43-beca-8e8e8e8e8e8e"
ESP_TYPE = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b"
DISK_BYTES = 30 * 1024 * 1024 * 1024
FREE_ID = "6979321856-32212237823"
FREE_SIZE = 25232915968


class DebianPartmanEspTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="igloo-debian-esp-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.dev = self.root / "dev"
        self.dev.mkdir()
        self.devices = self.root / "devices"
        self.devices.mkdir()
        self.sys_block = self.root / "sys-block"
        self.sys_block.mkdir()
        for disk in ("vda", "vdb"):
            (self.dev / disk).touch()
            (self.sys_block / disk).touch()
            (self.devices / disk).mkdir()
        for part in ("vda1", "vda2", "vda3", "vdb1"):
            (self.dev / part).touch()
        self._partition("vda", "1", "efi")
        self._partition("vda", "3", "efi")
        self._partition("vdb", "1", "efi")
        selected = self.devices / "vda"
        decoy = self.devices / "vdb"
        (selected / "partitions").write_text(
            f"1 1 536870912 primary fat32 {self.dev / 'vda1'} windows\n"
            f"2 2 4294967296 primary ntfs {self.dev / 'vda2'} ntfs\n"
            f"3 3 1073741824 primary fat32 {self.dev / 'vda3'} linux\n"
            f"0 {FREE_ID} {FREE_SIZE} primary free - free\n")
        (decoy / "partitions").write_text(
            f"1 1 536870912 primary fat32 {self.dev / 'vdb1'} decoy\n")
        (self.root / "igloo-bound-disk").write_text(str(self.dev / "vda") + "\n")
        (self.root / "igloo-bound-free-choice").write_text(
            str(selected) + "//" + FREE_ID + "\n")
        (self.root / "igloo-bound-free-size").write_text(str(FREE_SIZE) + "\n")
        (self.root / "igloo-bound-free-extent").write_text(
            f"fallback 0 0 {20 * 1024 * 1024 * 1024}\n")
        (self.root / "igloo-bound-identity").write_text(
            f"{TARGET_GUID} {DISK_BYTES}\n")
        (self.root / "igloo-bound-linux-esp").write_text(LINUX_ESP + "\n")
        self._write_executable("blockdev", f"#!/bin/sh\ncase \"$2\" in\n"
                               f"  {self.dev / 'vda'}|{self.dev / 'vdb'}) echo {DISK_BYTES} ;;\n"
                               "  *) exit 1 ;;\nesac\n")
        self._write_executable("blkid", f"""#!/bin/sh
field=
for value in "$@"; do
    case "$value" in PTUUID|PART_ENTRY_UUID|PART_ENTRY_TYPE) field=$value ;; esac
    path=$value
done
case "$field:$path" in
    PTUUID:{self.dev / 'vda'}) echo {TARGET_GUID} ;;
    PTUUID:{self.dev / 'vdb'}) echo {DECOY_GUID} ;;
    PART_ENTRY_UUID:{self.dev / 'vda1'}) echo {WINDOWS_ESP} ;;
    PART_ENTRY_UUID:{self.dev / 'vda3'}) echo {LINUX_ESP} ;;
    PART_ENTRY_UUID:{self.dev / 'vdb1'}) echo {DECOY_ESP} ;;
    PART_ENTRY_TYPE:{self.dev / 'vda1'}|PART_ENTRY_TYPE:{self.dev / 'vda3'}|PART_ENTRY_TYPE:{self.dev / 'vdb1'}) echo {ESP_TYPE} ;;
    *) exit 1 ;;
esac
""")
        self._write_executable("logger", "#!/bin/sh\nexit 0\n")
        self.lib = self.root / "partman-stub.sh"
        self.lib.write_text(f"""DEVICES='{self.devices}'
dev_to_partman() {{ printf '%s\\n' "$DEVICES/${{1##*/}}"; }}
longint_le() {{ [ "$1" -le "$2" ]; }}
open_dialog() {{ exec 3< "$PWD/partitions"; }}
close_dialog() {{ exec 3<&-; }}
read_line() {{
    line=
    IFS= read -r line <&3 || line=
    set -- $line
    num=${{1-}}; current_id=${{2-}}; partition_id=${{2-}}
    size=${{3-}}; partition_size=${{3-}}; type=${{4-}}
    fs=${{5-}}; partition_fs=${{5-}}; path=${{6-}}; name=${{7-}}
}}
autopartition() {{
    printf 'called\\n' > '{self.root / 'action'}'
    for disk in "$DEVICES"/*; do
        for part in "$disk"/1 "$disk"/3; do
            [ -f "$part/method" ] && mv "$part/method" "$part/method-old"
        done
    done
    printf 'efi\\n' > '{self.devices / 'vda/3/method'}'
}}
""")
        original = SOURCE.read_text()
        script = original.replace(". /lib/partman/lib/base.sh",
                                  f". '{self.lib}'")
        script = script.replace(". /lib/partman/lib/auto-shared.sh", "")
        script = script.replace("/tmp/igloo-bound-", str(self.root / "igloo-bound-"))
        script = script.replace("for sys in /sys/block/*; do",
                                f"for sys in '{self.sys_block}'/*; do")
        script = script.replace('disk="/dev/${sys##*/}"',
                                f'disk="{self.dev}/${{sys##*/}}"')
        script = script.replace("$(cat /proc/cmdline)",
                                f"$(cat '{self.root / 'cmdline'}')")
        script = script.replace(" -b ", " -e ")
        for needle in (str(self.lib), str(self.sys_block), str(self.root / "cmdline")):
            self.assertIn(needle, script)
        self.script = self.root / "do-option.sh"
        self.script.write_text(script)

    def _partition(self, disk: str, number: str, method: str) -> None:
        partition = self.devices / disk / number
        partition.mkdir()
        (partition / "method").write_text(method + "\n")
        (partition / "old_efi").touch()

    def _write_executable(self, name: str, contents: str) -> None:
        path = self.bin / name
        path.write_text(contents)
        path.chmod(0o755)

    def run_choice(self, cmdline: str) -> subprocess.CompletedProcess[str]:
        (self.root / "cmdline").write_text(cmdline + "\n")
        env = os.environ.copy()
        env["PATH"] = str(self.bin) + os.pathsep + env["PATH"]
        return subprocess.run(["sh", str(self.script),
                               str(self.devices / "vda") + "//" + FREE_ID],
                              env=env, cwd=self.root, text=True,
                              capture_output=True, timeout=20)

    def test_exact_prepared_linux_esp_is_only_efi_method(self) -> None:
        result = self.run_choice("auto=true igloo.linuxesppartuuid=" + LINUX_ESP)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue((self.root / "action").exists())
        self.assertFalse((self.devices / "vda/1/method").exists())
        self.assertTrue((self.devices / "vda/1/old_efi").exists())
        self.assertEqual("efi\n", (self.devices / "vda/3/method").read_text())
        self.assertFalse((self.devices / "vda/3/format").exists())

    def test_reviewed_free_extent_requires_exact_linux_esp_token(self) -> None:
        (self.root / "igloo-bound-free-extent").write_text(
            f"exact {FREE_ID.split('-')[0]} {FREE_SIZE} {20 * 1024 * 1024 * 1024}\n")
        missing = self.run_choice("auto=true")
        self.assertEqual(99, missing.returncode, missing.stderr)
        self.assertFalse((self.root / "action").exists())
        bound = self.run_choice("auto=true igloo.linuxesppartuuid=" + LINUX_ESP)
        self.assertEqual(0, bound.returncode, bound.stderr)
        self.assertTrue((self.root / "action").exists())

    def test_duplicate_or_wrong_esp_token_stops_before_autopartition(self) -> None:
        for cmdline in (
                "igloo.linuxesppartuuid=" + LINUX_ESP + " igloo.linuxesppartuuid=" + LINUX_ESP,
                "igloo.linuxesppartuuid=" + WINDOWS_ESP,
                "igloo.linuxesppartuuid=" + DECOY_ESP,
                "igloo.linuxesppartuuid=invalid"):
            with self.subTest(cmdline=cmdline):
                result = self.run_choice(cmdline)
                self.assertEqual(99, result.returncode, result.stderr)
                self.assertFalse((self.root / "action").exists())

    def test_reviewed_esp_must_match_rendered_binding_before_partman_changes(self) -> None:
        (self.root / "igloo-bound-free-extent").write_text(
            f"exact {FREE_ID.split('-')[0]} {FREE_SIZE} {20 * 1024 * 1024 * 1024}\n")
        binding = self.root / "igloo-bound-linux-esp"
        for value in (WINDOWS_ESP, DECOY_ESP, "none", "", LINUX_ESP + "\n" + LINUX_ESP):
            with self.subTest(binding=value):
                binding.write_text(value + "\n")
                result = self.run_choice("igloo.linuxesppartuuid=" + LINUX_ESP)
                self.assertEqual(99, result.returncode, result.stderr)
                self.assertFalse((self.root / "action").exists())
                self.assertEqual("efi\n", (self.devices / "vda/1/method").read_text())
                self.assertEqual("efi\n", (self.devices / "vda/3/method").read_text())
        binding.unlink()
        result = self.run_choice("igloo.linuxesppartuuid=" + LINUX_ESP)
        self.assertEqual(99, result.returncode, result.stderr)
        self.assertFalse((self.root / "action").exists())

    def test_preexisting_format_request_stops_before_autopartition(self) -> None:
        (self.devices / "vda/3/format").touch()
        result = self.run_choice("igloo.linuxesppartuuid=" + LINUX_ESP)
        self.assertEqual(99, result.returncode, result.stderr)
        self.assertFalse((self.root / "action").exists())

    def test_duplicate_partition_guid_on_decoy_stops_before_autopartition(self) -> None:
        fake_blkid = self.bin / "blkid"
        fake_blkid.write_text(fake_blkid.read_text().replace(DECOY_ESP, LINUX_ESP))
        result = self.run_choice("igloo.linuxesppartuuid=" + LINUX_ESP)
        self.assertEqual(99, result.returncode, result.stderr)
        self.assertFalse((self.root / "action").exists())


if __name__ == "__main__":
    unittest.main()
