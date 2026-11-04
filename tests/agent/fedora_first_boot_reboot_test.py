"""The Fedora driver reboot request survives a stopped launcher."""

import importlib.util
import os
from pathlib import Path
import stat
import subprocess
from tempfile import TemporaryDirectory
import unittest
from unittest import mock


SOURCE = (Path(__file__).resolve().parents[2] / "distros" / "fedora-kde"
          / "agent" / "first-boot.sh")
AGENT_SOURCE = SOURCE.with_name("agent.py")
SPEC = importlib.util.spec_from_file_location("igloo_fedora_reboot_agent", AGENT_SOURCE)
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)


@unittest.skipIf(os.name == "nt", "The Fedora launcher needs POSIX fsync semantics")
class FedoraFirstBootRebootTests(unittest.TestCase):
    def test_driver_reboot_marker_is_private_and_write_failure_is_not_ignored(self):
        with TemporaryDirectory(prefix="igloo-fedora-driver-reboot-") as temporary:
            marker = Path(temporary) / ".reboot-required"
            AGENT._flag_driver_reboot_required(marker)
            self.assertEqual(b"nvidia-driver-installed\n", marker.read_bytes())
            self.assertEqual(0o600, stat.S_IMODE(marker.stat().st_mode))

            marker.unlink()
            with mock.patch.object(AGENT.os, "replace", side_effect=OSError("write failed")):
                with self.assertRaisesRegex(OSError, "write failed"):
                    AGENT._flag_driver_reboot_required(marker)
            self.assertFalse(marker.exists())
            self.assertEqual([], list(Path(temporary).iterdir()))

    def test_reboot_request_is_consumed_only_after_new_boot_id(self):
        with TemporaryDirectory(prefix="igloo-fedora-reboot-") as temporary:
            root = Path(temporary)
            state = root / "state"
            state.mkdir()
            (state / ".done").write_text("complete\n")
            marker = state / ".reboot-required"
            marker.write_text("nvidia-driver-installed\n")
            journal = state / ".reboot-requested-boot-id"
            boot_id = root / "boot-id"
            first_id = "78e98b79-b250-4e9f-86de-280159494eba"
            boot_id.write_text(first_id + "\n")

            fake_bin = root / "bin"
            fake_bin.mkdir()
            reboot_log = root / "reboot-calls"
            systemctl = fake_bin / "systemctl"
            systemctl.write_text('#!/bin/sh\nprintf "called\\n" >> "$REBOOT_LOG"\n')
            systemctl.chmod(0o755)

            script = SOURCE.read_text().replace("/var/lib/igloo", str(state))
            script = script.replace("/var/log/igloo", str(root / "logs"))
            script = script.replace("/proc/sys/kernel/random/boot_id", str(boot_id))
            launcher = root / "first-boot.sh"
            launcher.write_text(script)
            env = dict(os.environ, PATH=str(fake_bin) + os.pathsep + os.environ["PATH"],
                       REBOOT_LOG=str(reboot_log))

            def run():
                result = subprocess.run(["bash", str(launcher)], env=env,
                                        capture_output=True, text=True, check=False)
                self.assertEqual(0, result.returncode, result.stderr)

            run()
            self.assertTrue(marker.is_file())
            self.assertEqual(first_id, journal.read_text().strip())
            self.assertEqual(1, len(reboot_log.read_text().splitlines()))

            # A stopped service in the same boot must request the reboot again.
            run()
            self.assertTrue(marker.is_file())
            self.assertEqual(2, len(reboot_log.read_text().splitlines()))

            # A changed kernel boot ID is the observable completion condition.
            boot_id.write_text("9410ee19-8aa5-41eb-872c-c5410e1e332a\n")
            run()
            self.assertFalse(marker.exists())
            self.assertFalse(journal.exists())
            self.assertEqual(2, len(reboot_log.read_text().splitlines()))


if __name__ == "__main__":
    unittest.main()
