"""The kickstart stops before any write when the first EFI system partition is not the one iGloo named."""

from pathlib import Path
import shutil
import subprocess
from tempfile import TemporaryDirectory
import unittest


KICKSTART = Path(__file__).resolve().parents[2] / "distros/fedora-kde/kickstart/ks.cfg.template"
ESP = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b"
LINUX = "0fc63daf-8483-4772-8e79-3d69d8477de4"
WINDOWS_ESP = "d1b2894e-9378-4b98-a5b0-e4d3f7428c18"
DEBIAN_ESP = "1106f84f-f7b1-4b5b-abc3-9f53f4326f2d"


def check_block():
    """The lines of the %pre script that read the token and compare it."""
    text = KICKSTART.read_text(encoding="utf-8").replace("\r\n", "\n")
    start = text.index("# With more than one EFI system partition on the disk")
    end = text.index("the one iGloo named", start)
    end = text.index("\nfi\n", end) + len("\nfi\n")
    return text[start:end]


@unittest.skipUnless(shutil.which("bash"), "needs bash")
class FedoraBootEspCheckTest(unittest.TestCase):
    def run_check(self, cmdline, partitions, mode="dual-boot"):
        """partitions: (number, type GUID, partition GUID) on the disk nvme0n1."""
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "proc").mkdir()
            (root / "proc/cmdline").write_text(cmdline + "\n", encoding="ascii")
            table = []
            for number, kind, identity in partitions:
                folder = root / "sys/block/nvme0n1" / f"nvme0n1p{number}"
                folder.mkdir(parents=True)
                (folder / "partition").write_text(f"{number}\n", encoding="ascii")
                table.append(f"/dev/nvme0n1p{number} {kind} {identity}")
            (root / "table").write_text("\n".join(table) + "\n", encoding="ascii")
            stub = root / "bin/blkid"
            stub.parent.mkdir()
            stub.write_text(
                "#!/bin/bash\n"
                "# blkid -p -s <tag> -o value <device>\n"
                'while read -r device kind identity; do\n'
                '  [ "$device" = "$6" ] || continue\n'
                '  case "$3" in PART_ENTRY_TYPE) echo "$kind" ;; PART_ENTRY_UUID) echo "$identity" ;; esac\n'
                "  exit 0\n"
                f'done < "{root.as_posix()}/table"\n'
                "exit 2\n", encoding="ascii", newline="\n")
            stub.chmod(0o755)
            block = check_block().replace("/proc/cmdline", f"{root.as_posix()}/proc/cmdline") \
                                 .replace("/sys/block/", f"{root.as_posix()}/sys/block/")
            script = root / "check.sh"
            script.write_text(
                "set -euo pipefail\n"
                f'PATH="{root.as_posix()}/bin:$PATH"\n'
                'log() { echo "[igloo-pre] $*" >&2; }\n'
                f'TARGET_DISK=nvme0n1\nINSTALL_MODE="{mode}"\n' + block + "echo reached-storage\n",
                encoding="utf-8", newline="\n")
            return subprocess.run(["bash", str(script)], capture_output=True, text=True, check=False)

    def test_without_the_token_nothing_is_checked(self):
        done = self.run_check("inst.ks=hd:LABEL=OEMDRV igloo.seedpartuuid=" + DEBIAN_ESP,
                              [(1, ESP, WINDOWS_ESP), (6, ESP, DEBIAN_ESP)])
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertEqual("reached-storage\n", done.stdout)
        self.assertEqual("", done.stderr)

    def test_the_named_partition_is_the_lowest_numbered_one(self):
        done = self.run_check("quiet igloo.bootesppartuuid=" + WINDOWS_ESP.upper(),
                              [(6, ESP, DEBIAN_ESP), (7, LINUX, "aaaaaaaa-0000-4000-8000-000000000007"),
                               (1, ESP, WINDOWS_ESP)])
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertIn("Boot files go on EFI system partition 1", done.stderr)
        self.assertEqual("reached-storage\n", done.stdout)

    def test_numbers_are_compared_as_numbers(self):
        # The shell lists p10 before p2.
        done = self.run_check("igloo.bootesppartuuid=" + WINDOWS_ESP,
                              [(10, ESP, DEBIAN_ESP), (2, ESP, WINDOWS_ESP)])
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertIn("partition 2", done.stderr)

    def test_another_first_partition_stops_the_installation(self):
        done = self.run_check("igloo.bootesppartuuid=" + WINDOWS_ESP,
                              [(1, ESP, DEBIAN_ESP), (6, ESP, WINDOWS_ESP)])
        self.assertEqual(1, done.returncode)
        self.assertIn("nothing was written", done.stderr)
        self.assertIn(f"partition 1 ({DEBIAN_ESP})", done.stderr)
        self.assertEqual("", done.stdout)

    def test_a_missing_partition_stops_the_installation(self):
        done = self.run_check("igloo.bootesppartuuid=" + WINDOWS_ESP,
                              [(3, LINUX, "aaaaaaaa-0000-4000-8000-000000000003")])
        self.assertEqual(1, done.returncode)
        self.assertIn("partition none", done.stderr)

    def test_a_doubled_or_malformed_token_or_another_mode_stops_the_installation(self):
        layout = [(1, ESP, WINDOWS_ESP), (6, ESP, DEBIAN_ESP)]
        for cmdline, mode in (
                (f"igloo.bootesppartuuid={WINDOWS_ESP} igloo.bootesppartuuid={WINDOWS_ESP}", "dual-boot"),
                ("igloo.bootesppartuuid=", "dual-boot"),
                ("igloo.bootesppartuuid=1", "dual-boot"),
                (f"igloo.bootesppartuuid={WINDOWS_ESP}", "replace")):
            with self.subTest(cmdline=cmdline, mode=mode):
                done = self.run_check(cmdline, layout, mode)
                self.assertEqual(1, done.returncode)
                self.assertEqual("", done.stdout)


if __name__ == "__main__":
    unittest.main()
