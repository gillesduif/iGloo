"""Fedora's %pre reads the seed through its own read-only loop device with UTF-8 names, and lets go of it on a stop."""

from pathlib import Path
import shutil
import subprocess
from tempfile import TemporaryDirectory
import unittest


KICKSTART = Path(__file__).resolve().parents[2] / "distros/fedora-kde/kickstart/ks.cfg.template"
AGENT = Path(__file__).resolve().parents[2] / "distros/fedora-kde/agent/agent.py"
SEED_DISK = "db67189d-5d5d-4398-bc61-98b983f5e2fe"

STUB = """#!/bin/bash
echo "$(basename "$0") $*" >> "{log}"
case "$(basename "$0") $*" in
    "blkid -t LABEL=OEMDRV -o device") echo /dev/fake4 ;;
    "losetup --find --show --read-only /dev/fake4") echo /dev/loop7 ;;
    python3*) [ ! -e "{root}/validator-fails" ] || {{ echo "RuntimeError: fixture" >&2; exit 1; }}; echo {disk} ;;
esac
"""


def pre_script(root):
    """The top of %pre and the lines that mount and validate the seed, with their paths moved into a sandbox."""
    text = KICKSTART.read_text(encoding="utf-8").replace("\r\n", "\n")
    start = text.index("%pre --erroronfail --interpreter=/bin/bash\n") + len("%pre --erroronfail --interpreter=/bin/bash\n")
    head_end = text.index("trap igloo_pre_report EXIT\n", start) + len("trap igloo_pre_report EXIT\n")
    block_start = text.index("mapfile -t SEED_DEVICES < <(blkid -t LABEL=OEMDRV -o device 2>/dev/null)\n", head_end)
    block_end = text.index('losetup --detach "$IGLOO_SEED_LOOP"\nIGLOO_SEED_LOOP=""\n', block_start)
    block_end += len('losetup --detach "$IGLOO_SEED_LOOP"\nIGLOO_SEED_LOOP=""\n')
    script = (text[start:head_end] + 'log() { echo "[igloo-pre] $*" >&2; }\n' + text[block_start:block_end]
              + 'echo "seed disk: $SEED_DISK_GUID"\n')
    for path in ("/tmp/igloo-pre.log", "/tmp/anaconda.log", "/tmp/igloo_pre_oemdrv"):
        assert path in script, path
        script = script.replace(path, f"{root}{path}")
    return script


@unittest.skipUnless(shutil.which("bash"), "needs bash")
class FedoraSeedMountTest(unittest.TestCase):
    def run_pre(self, validator_fails=False):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "tmp").mkdir()
            (root / "bin").mkdir()
            log = root / "calls.log"
            for name in ("blkid", "losetup", "mount", "umount", "python3"):
                stub = root / "bin" / name
                stub.write_text(STUB.format(log=log.as_posix(), root=root.as_posix(), disk=SEED_DISK),
                                encoding="ascii", newline="\n")
                stub.chmod(0o755)
            if validator_fails:
                (root / "validator-fails").touch()
            script = root / "pre.sh"
            script.write_text(f'PATH="{root.as_posix()}/bin:$PATH"\n' + pre_script(root.as_posix()),
                              encoding="utf-8", newline="\n")
            done = subprocess.run(["bash", str(script)], stdin=subprocess.DEVNULL, capture_output=True,
                                  text=True, start_new_session=True, check=False)
            calls = log.read_text(encoding="utf-8").splitlines() if log.exists() else []
            return done, [call.replace(root.as_posix(), "") for call in calls]

    def test_the_seed_is_read_through_a_loop_device_of_its_own_with_utf8_names(self):
        done, calls = self.run_pre()
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertEqual(f"seed disk: {SEED_DISK}\n", done.stdout)
        self.assertEqual([
            "blkid -t LABEL=OEMDRV -o device",
            "losetup --find --show --read-only /dev/fake4",
            "mount -t vfat -o ro,nosuid,nodev,noexec,utf8 /dev/loop7 /tmp/igloo_pre_oemdrv",
            # The validator is given the partition itself: its GPT identity is not the loop device's.
            "python3 /tmp/igloo-validate-seed.py /dev/fake4 /tmp/igloo_pre_oemdrv",
            "umount /tmp/igloo_pre_oemdrv",
            "losetup --detach /dev/loop7",
        ], calls)

    def test_a_stop_in_the_validator_releases_the_loop_device(self):
        done, calls = self.run_pre(validator_fails=True)
        self.assertEqual(1, done.returncode)
        self.assertEqual("", done.stdout)
        self.assertIn("RuntimeError: fixture", done.stderr)
        # A loop device left behind would be a second device with the seed's label.
        self.assertEqual(["umount /tmp/igloo_pre_oemdrv", "losetup --detach /dev/loop7"], calls[-2:])
        self.assertEqual(1, calls.count("losetup --detach /dev/loop7"))

    def test_post_and_the_first_boot_agent_ask_for_utf8_names_too(self):
        text = KICKSTART.read_text(encoding="utf-8")
        post = text[text.index("%post --nochroot"):]
        self.assertIn('OEMDRV_LOOP=$(losetup --find --show --read-only "$OEMDRV_DEV")', post)
        self.assertIn('mount -t vfat -o ro,nosuid,nodev,noexec,utf8 "$OEMDRV_LOOP" "$OEMDRV"', post)
        self.assertIn('losetup --detach "$OEMDRV_LOOP"', post)
        # No mount of the seed is left that takes the kernel's default character set.
        self.assertNotIn('noexec "', text)
        agent = AGENT.read_text(encoding="utf-8")
        self.assertEqual(3, agent.count('"mount", "-t", "vfat"'))
        self.assertEqual(3, agent.count('"mount", "-t", "vfat", "-o", "') )
        for options in ('"ro,nosuid,nodev,noexec,utf8"', '"rw,nosuid,nodev,noexec,utf8"', '"-o", "utf8"'):
            self.assertIn(options, agent)

    def test_the_validator_names_the_path_it_refuses(self):
        text = KICKSTART.read_text(encoding="utf-8")
        self.assertEqual(2, text.count('"Selected OEMDRV user-data path is unsafe: " + ascii('))
        self.assertNotIn('"Selected OEMDRV user-data path is unsafe")', text)


if __name__ == "__main__":
    unittest.main()
