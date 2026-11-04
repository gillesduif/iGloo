"""Exercise the Debian-family launcher without touching the host's /var or /opt."""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path


LAUNCHER = (Path(__file__).resolve().parents[2]
            / "distros" / "_debian-family" / "agent" / "first-boot.sh")


class FirstBootLauncherTests(unittest.TestCase):
    def run_launcher(self, agent_exit: int, reboot_exit: int | None = None,
                     already_done: bool = False, fail_cleanup: bool = False) -> tuple[int, bool, bool, bool, bool]:
        with tempfile.TemporaryDirectory(prefix="igloo-first-boot-") as temporary:
            root = Path(temporary)
            (root / "state").mkdir()
            fake_bin = root / "bin"
            fake_bin.mkdir()
            fake_python = fake_bin / "python3"
            fake_python.write_text(
                '#!/bin/sh\ncase "$1" in */agent.py) exit "$IGLOO_TEST_AGENT_EXIT" ;; '
                '*) exec /usr/bin/python3 "$@" ;; esac\n')
            fake_python.chmod(0o755)
            (root / "state" / "seed-retry").mkdir()
            (root / "state" / "seed-retry" / "preseed.cfg").write_text("original\n")
            (root / "state" / "manifest.retry.json").write_text("original\n")
            if already_done:
                (root / "state" / ".done").touch()
            if fail_cleanup:
                fake_rm = fake_bin / "rm"
                fake_rm.write_text('#!/bin/sh\ncase "$*" in *seed-retry*) exit 7 ;; '
                                   '*) exec /usr/bin/rm "$@" ;; esac\n')
                fake_rm.chmod(0o755)
            if reboot_exit is not None:
                (root / "state" / ".reboot-required").touch()
                fake_systemctl = fake_bin / "systemctl"
                fake_systemctl.write_text("#!/bin/sh\nexit \"$IGLOO_TEST_REBOOT_EXIT\"\n")
                fake_systemctl.chmod(0o755)

            script = LAUNCHER.read_text().replace("/var/lib/igloo", str(root / "state"))
            script = script.replace("/var/log/igloo", str(root / "logs"))
            script = script.replace("/opt/igloo", str(root / "agent"))
            boot_id = root / "boot-id"
            boot_id.write_text("11111111-1111-4111-8111-111111111111\n")
            script = script.replace("/proc/sys/kernel/random/boot_id", str(boot_id))
            isolated_launcher = root / "first-boot.sh"
            isolated_launcher.write_text(script)
            env = dict(os.environ, PATH=f"{fake_bin}{os.pathsep}{os.environ['PATH']}",
                       IGLOO_TEST_AGENT_EXIT=str(agent_exit),
                       IGLOO_TEST_REBOOT_EXIT=str(reboot_exit or 0))
            result = subprocess.run(["bash", str(isolated_launcher)], env=env,
                                    capture_output=True, text=True, check=False)
            return (result.returncode, (root / "state" / ".done").exists(),
                    (root / "state" / ".reboot-required").exists(),
                    (root / "state" / "seed-retry").exists(),
                    (root / "state" / "manifest.retry.json").exists())

    def test_agent_failure_is_not_marked_complete(self) -> None:
        self.assertEqual((23, False, False, True, True), self.run_launcher(23))

    def test_success_is_marked_complete(self) -> None:
        self.assertEqual((0, True, False, False, False), self.run_launcher(0))

    def test_failed_reboot_request_remains_retryable(self) -> None:
        self.assertEqual((1, True, True, False, False), self.run_launcher(0, reboot_exit=5))

    def test_done_branch_cleans_retry_backups_without_rerunning_agent(self) -> None:
        self.assertEqual((0, True, False, False, False),
                         self.run_launcher(23, already_done=True))

    def test_cleanup_failure_keeps_durable_done_for_next_boot_retry(self) -> None:
        self.assertEqual((1, True, False, True, True),
                         self.run_launcher(0, fail_cleanup=True))

    def test_accepted_reboot_keeps_reason_until_different_boot_id(self) -> None:
        with tempfile.TemporaryDirectory(prefix="igloo-first-boot-") as temporary:
            root = Path(temporary)
            state = root / "state"
            state.mkdir()
            (state / ".done").write_text("1\n")
            reason = state / ".reboot-required"
            reason.write_text("driver-reboot\n")
            boot_id = root / "boot-id"
            first_id = "11111111-1111-4111-8111-111111111111"
            second_id = "22222222-2222-4222-8222-222222222222"
            boot_id.write_text(first_id + "\n")
            fake_bin = root / "bin"
            fake_bin.mkdir()
            systemctl = fake_bin / "systemctl"
            systemctl.write_text('#!/bin/sh\nprintf call >> "$IGLOO_CALLS"\nexit 0\n')
            systemctl.chmod(0o755)
            script = LAUNCHER.read_text()
            script = script.replace("/var/lib/igloo", str(state))
            script = script.replace("/var/log/igloo", str(root / "logs"))
            script = script.replace("/opt/igloo", str(root / "agent"))
            script = script.replace("/proc/sys/kernel/random/boot_id", str(boot_id))
            isolated = root / "first-boot.sh"
            isolated.write_text(script)
            calls = root / "reboot-calls"
            environment = dict(os.environ,
                               PATH=f"{fake_bin}{os.pathsep}{os.environ['PATH']}",
                               IGLOO_CALLS=str(calls))

            def run() -> None:
                result = subprocess.run(["bash", str(isolated)], env=environment,
                                        capture_output=True, text=True, check=False)
                self.assertEqual(0, result.returncode, result.stderr)

            run()
            self.assertEqual("driver-reboot\n", reason.read_text())
            self.assertEqual(first_id + "\n", (state / ".reboot-requested-boot-id").read_text())
            self.assertEqual("call", calls.read_text())

            # A same-session retry cannot consume the reason.
            run()
            self.assertEqual("driver-reboot\n", reason.read_text())
            self.assertEqual("callcall", calls.read_text())

            boot_id.write_text(second_id + "\n")
            run()
            self.assertFalse(reason.exists())
            self.assertFalse((state / ".reboot-requested-boot-id").exists())
            self.assertEqual("callcall", calls.read_text())


if __name__ == "__main__":
    unittest.main()
