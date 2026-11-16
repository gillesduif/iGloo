"""Debian's partman choice bound to the space directly behind the installer partition.

Windows puts the installer partition where only the size asked for Linux is unallocated
behind it and passes the bounds of that space in the boot arguments. The fixture replaces
device discovery and partman IPC; the three source scripts run their own checks.
"""

from pathlib import Path
import os
import subprocess
import tempfile
import unittest


PRESEED = Path(__file__).resolve().parents[2] / "distros/debian/preseed"
GIB = 1024 ** 3
MIB = 1024 ** 2
DISK_GUID = "db67189d-5d5d-4398-bc61-98b983f5e2fe"
DISK_BYTES = 164 * GIB
SEED = "4328e264-400d-49dd-885c-c541bf66e8a2"
LINUX = 35 * GIB
MINIMUM = 20 * GIB
BOUNDS = f"igloo.linuxmin={LINUX - 2 * MIB} igloo.linuxmax={LINUX + 64 * MIB}"
SURPLUS_ID = "68717379584-134213632000"
SEED_ID = "134213632000-138642817023"
LINUX_ID = "138642817024-176092610559"


class DebianBoundToSeedTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="igloo-debian-seed-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for name in ("bin", "dev", "devices/vda", "sys-block", "partman/igloo_stock"):
            (self.root / name).mkdir(parents=True)
        self.dev = self.root / "dev"
        self.disk = self.root / "devices/vda"
        (self.root / "sys-block/vda").touch()
        for node in ("vda", "vda1", "vda3", "vda4", "vda5"):
            (self.dev / node).touch()
        self.layout(linux_size=LINUX + MIB)
        self._executable("blockdev", f"#!/bin/sh\n[ \"$2\" = '{self.dev / 'vda'}' ] && echo {DISK_BYTES}\n")
        self._executable("logger", "#!/bin/sh\nexit 0\n")
        self.seed_node = "vda5"
        self._blkid()
        self.lib = self.root / "partman-stub.sh"
        self.lib.write_text(f"""DEVICES='{self.root / 'devices'}'
dev_to_partman() {{ printf '%s\\n' "$DEVICES/${{1##*/}}"; }}
longint_le() {{ [ "$1" -le "$2" ]; }}
open_dialog() {{ exec 3< "$PWD/partitions"; }}
close_dialog() {{ exec 3<&-; }}
read_line() {{
    # As partman's own: one field per variable named by the caller, and no other variable touched.
    stub_line=
    IFS= read -r stub_line <&3 || stub_line=
    for stub_name in "$@"; do
        stub_value=${{stub_line%% *}}
        if [ "$stub_line" = "$stub_value" ]; then stub_line=; else stub_line=${{stub_line#* }}; fi
        eval "$stub_name=\\$stub_value"
    done
}}
db_metaget() {{ RET='Use the free space'; }}
autopartition() {{ printf '%s\\n' "$2" > '{self.root / 'action'}'; }}
""")

    def layout(self, linux_size: int, behind_seed: str = "free", surplus: int = 60 * GIB) -> None:
        """Windows, 60 GiB unallocated, the installer partition, then what is behind it."""
        behind = {"free": f"0 {LINUX_ID} {linux_size} primary free - free\n",
                  "unusable": f"0 {LINUX_ID} {linux_size} unusable free - free\n",
                  "partition": f"6 {LINUX_ID} {linux_size} primary ext4 {self.dev / 'vda3'} other\n",
                  "nothing": ""}[behind_seed]
        (self.disk / "partitions").write_text(
            f"1 1048576-210763775 209715200 primary fat32 {self.dev / 'vda1'} esp\n"
            f"3 227540992-67779952639 67552411648 primary ntfs {self.dev / 'vda3'} windows\n"
            f"4 67781001216-68717379583 936378368 primary ntfs {self.dev / 'vda4'} recovery\n"
            f"0 {SURPLUS_ID} {surplus} primary free - free\n"
            f"5 {SEED_ID} 4429185024 primary fat32 {self.dev / 'vda5'} OEMDRV\n" + behind)

    def _blkid(self) -> None:
        self._executable("blkid", f"""#!/bin/sh
field=
for value in "$@"; do
    case "$value" in PTUUID|PART_ENTRY_UUID) field=$value ;; esac
    path=$value
done
case "$field:$path" in
    PTUUID:{self.dev / 'vda'}) echo {DISK_GUID} ;;
    PART_ENTRY_UUID:{self.dev / self.seed_node}) echo {SEED.upper()} ;;
    PART_ENTRY_UUID:*) echo 00000000-0000-4000-8000-000000000000 ;;
    *) exit 1 ;;
esac
""")

    def _executable(self, name: str, contents: str) -> None:
        path = self.root / "bin" / name
        path.write_text(contents)
        path.chmod(0o755)

    def _script(self, name: str) -> Path:
        script = (PRESEED / name).read_text()
        for token, value in (("{{TARGET_DISK_GUID}}", DISK_GUID), ("{{TARGET_DISK_BYTES}}", str(DISK_BYTES)),
                             ("{{INSTALLER_FREE_OFFSET_BYTES}}", "none"), ("{{INSTALLER_FREE_SIZE_BYTES}}", "none"),
                             ("{{LINUX_ESP_PARTITION_GUID}}", "none"), ("{{MINIMUM_INSTALL_BYTES}}", str(MINIMUM))):
            script = script.replace(token, value)
        script = script.replace(". /lib/partman/lib/base.sh", f". '{self.lib}'")
        script = script.replace(". /lib/partman/lib/auto-shared.sh", "")
        for prefix in ("igloo-bound-", "igloo-disabled-"):
            script = script.replace("/tmp/" + prefix, str(self.root / prefix))
        script = script.replace("for sys in /sys/block/*; do", f"for sys in '{self.root / 'sys-block'}'/*; do")
        script = script.replace('disk="/dev/${sys##*/}"', f'disk="{self.dev}/${{sys##*/}}"')
        script = script.replace("$(cat /proc/cmdline)", f"$(cat '{self.root / 'cmdline'}')")
        script = script.replace("/lib/partman/automatically_partition", str(self.root / "partman"))
        # The initrd holds the helpers at its root; the checkout path may contain spaces and quotes.
        initrd = self.root / "initrd"
        initrd.mkdir(exist_ok=True)
        for helper in ("igloo-bound-free-choices.sh", "igloo-bound-free-do-option.sh"):
            (initrd / helper).write_text((PRESEED / helper).read_text())
        script = script.replace("cp /igloo-bound-free-", f"cp {initrd}/igloo-bound-free-")
        script = script.replace(" -b ", " -e ")
        target = self.root / name
        target.write_text(script)
        return target

    def _run(self, name: str, cmdline: str, *arguments: str) -> subprocess.CompletedProcess[str]:
        (self.root / "cmdline").write_text(cmdline + "\n")
        env = os.environ.copy()
        env["PATH"] = str(self.root / "bin") + os.pathsep + env["PATH"]
        return subprocess.run(["sh", str(self._script(name)), *arguments], env=env, cwd=self.root,
                              text=True, capture_output=True, timeout=20)

    def setup(self, cmdline: str) -> subprocess.CompletedProcess[str]:
        return self._run("igloo-bound-free-setup.sh", cmdline)

    def choices(self, cmdline: str) -> subprocess.CompletedProcess[str]:
        return self._run("igloo-bound-free-choices.sh", cmdline)

    def do_option(self, cmdline: str) -> subprocess.CompletedProcess[str]:
        choice = (self.root / "igloo-bound-free-choice").read_text().strip()
        return self._run("igloo-bound-free-do-option.sh", cmdline, choice)

    def bound_cmdline(self, extra: str = BOUNDS) -> str:
        return f"auto=true igloo.seedpartuuid={SEED} igloo.seeddiskguid={DISK_GUID} {extra}".strip()

    def test_the_space_behind_the_installer_partition_is_partitioned_and_other_space_is_left(self) -> None:
        self.assertEqual(0, self.setup(self.bound_cmdline()).returncode)
        self.assertEqual(f"seed {LINUX - 2 * MIB} {LINUX + 64 * MIB} {MINIMUM}\n",
                         (self.root / "igloo-bound-free-extent").read_text())
        self.assertEqual(SEED + "\n", (self.root / "igloo-bound-seed").read_text())

        offered = self.choices(self.bound_cmdline())

        self.assertEqual(0, offered.returncode, offered.stderr)
        self.assertEqual(f"{self.disk}//{LINUX_ID}\tUse the free space\n", offered.stdout)
        applied = self.do_option(self.bound_cmdline())
        self.assertEqual(0, applied.returncode, applied.stderr)
        self.assertEqual(LINUX_ID + "\n", (self.root / "action").read_text())

    def test_without_bounds_two_regions_of_the_minimum_size_stay_refused(self) -> None:
        self.assertEqual(0, self.setup(self.bound_cmdline(extra="")).returncode)
        self.assertTrue((self.root / "igloo-bound-free-extent").read_text().startswith("fallback "))

        offered = self.choices(self.bound_cmdline(extra=""))

        self.assertIn("invalid\t", offered.stdout)
        self.assertFalse((self.root / "igloo-bound-free-choice").exists())

    def test_space_of_another_size_behind_the_installer_partition_is_refused(self) -> None:
        for size in (LINUX - 3 * MIB, LINUX + 65 * MIB, 60 * GIB):
            with self.subTest(size=size):
                self.layout(linux_size=size)
                self.assertEqual(0, self.setup(self.bound_cmdline()).returncode)
                self.assertIn("invalid\t", self.choices(self.bound_cmdline()).stdout)

    def test_anything_but_usable_free_space_behind_the_installer_partition_is_refused(self) -> None:
        for behind in ("partition", "unusable", "nothing"):
            with self.subTest(behind=behind):
                self.layout(linux_size=LINUX, behind_seed=behind)
                self.assertEqual(0, self.setup(self.bound_cmdline()).returncode)
                self.assertIn("invalid\t", self.choices(self.bound_cmdline()).stdout)

    def test_two_partitions_with_the_installer_identity_are_refused(self) -> None:
        self._executable("blkid", f"""#!/bin/sh
case "$*" in
    *PTUUID*) echo {DISK_GUID} ;;
    *) echo {SEED} ;;
esac
""")
        self.assertEqual(0, self.setup(self.bound_cmdline()).returncode)

        self.assertIn("invalid\t", self.choices(self.bound_cmdline()).stdout)

    def test_a_missing_installer_partition_is_refused(self) -> None:
        self.seed_node = "absent"
        self._blkid()
        self.assertEqual(0, self.setup(self.bound_cmdline()).returncode)

        self.assertIn("invalid\t", self.choices(self.bound_cmdline()).stdout)

    def test_the_action_checks_the_space_again(self) -> None:
        self.assertEqual(0, self.setup(self.bound_cmdline()).returncode)
        self.assertEqual(0, self.choices(self.bound_cmdline()).returncode)
        self.layout(linux_size=LINUX + 65 * MIB)

        applied = self.do_option(self.bound_cmdline())

        self.assertEqual(99, applied.returncode)
        self.assertFalse((self.root / "action").exists())

    def test_the_action_refuses_the_other_unallocated_space(self) -> None:
        self.assertEqual(0, self.setup(self.bound_cmdline()).returncode)
        self.assertEqual(0, self.choices(self.bound_cmdline()).returncode)
        (self.root / "igloo-bound-free-choice").write_text(f"{self.disk}//{SURPLUS_ID}\n")
        (self.root / "igloo-bound-free-size").write_text(f"{60 * GIB}\n")

        self.assertEqual(99, self.do_option(self.bound_cmdline()).returncode)
        self.assertFalse((self.root / "action").exists())

    def test_incomplete_or_malformed_bounds_stop_the_installer(self) -> None:
        for extra in (f"igloo.linuxmin={LINUX}", f"igloo.linuxmax={LINUX}",
                      f"igloo.linuxmin={LINUX} igloo.linuxmax=35G",
                      f"{BOUNDS} igloo.linuxmax={LINUX}"):
            with self.subTest(extra=extra):
                self.assertEqual(1, self.setup(self.bound_cmdline(extra=extra)).returncode)
        without_seed = f"auto=true igloo.seeddiskguid={DISK_GUID} {BOUNDS}"
        self.assertEqual(1, self.setup(without_seed).returncode)
        # The late command demands the installer identity exactly once; so does the binding.
        twice = self.bound_cmdline() + f" igloo.seedpartuuid={SEED}"
        self.assertEqual(1, self.setup(twice).returncode)

    def test_the_other_guided_choices_are_set_aside(self) -> None:
        self.assertEqual(0, self.setup(self.bound_cmdline()).returncode)

        self.assertFalse((self.root / "partman/igloo_stock").exists())
        self.assertTrue((self.root / "partman/igloo_bound_free/do_option").is_file())


if __name__ == "__main__":
    unittest.main()
