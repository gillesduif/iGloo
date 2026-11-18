"""A stop in Fedora's %pre script reaches the installer screen with its reason; a clean run only passes its log on."""

import fcntl
import os
from pathlib import Path
import select
import shutil
import subprocess
from tempfile import TemporaryDirectory
import termios
import unittest


KICKSTART = Path(__file__).resolve().parents[2] / "distros/fedora-kde/kickstart/ks.cfg.template"


def prelude(log):
    """The lines at the top of %pre that keep the log and report a stop."""
    text = KICKSTART.read_text(encoding="utf-8").replace("\r\n", "\n")
    start = text.index("%pre --erroronfail --interpreter=/bin/bash\n") + len("%pre --erroronfail --interpreter=/bin/bash\n")
    end = text.index("trap igloo_pre_report EXIT\n", start) + len("trap igloo_pre_report EXIT\n")
    block = text[start:end]
    assert block.startswith("set -euo pipefail\n") and block.count("/tmp/igloo-pre.log") == 1
    assert block.count("/tmp/anaconda.log") == 1
    return block.replace("/tmp/igloo-pre.log", log).replace("/tmp/anaconda.log", log + ".anaconda")


def run(body, terminal, installer_log=None):
    """Runs the prelude with a body; returns the exit status, what reached stderr and what reached the terminal."""
    with TemporaryDirectory() as temporary:
        script = Path(temporary) / "pre.sh"
        script.write_text(prelude(f"{temporary}/igloo-pre.log") + body, encoding="utf-8", newline="\n")
        master, slave = os.openpty()

        def attach():
            os.setsid()
            if terminal:
                fcntl.ioctl(slave, termios.TIOCSCTTY, 0)

        done = subprocess.run(["bash", str(script)], stdin=subprocess.DEVNULL, capture_output=True,
                              text=True, preexec_fn=attach, check=False)
        os.close(slave)
        shown = b""
        while select.select([master], [], [], 0.2)[0]:
            try:
                chunk = os.read(master, 4096)
            except OSError:
                break
            if not chunk:
                break
            shown += chunk
        os.close(master)
        if installer_log is not None:
            written = Path(f"{temporary}/igloo-pre.log.anaconda")
            installer_log.append(written.read_text(encoding="utf-8") if written.exists() else None)
        return done.returncode, done.stderr, shown.decode("utf-8", "replace").replace("\r\n", "\n")


@unittest.skipUnless(shutil.which("bash") and hasattr(os, "openpty"), "needs bash and a pseudo terminal")
class FedoraPreReportTest(unittest.TestCase):
    def test_a_clean_run_passes_its_log_on_and_shows_nothing(self):
        installer_log = []
        code, stderr, shown = run('echo "[igloo-pre] Target disk: /dev/nvme0n1" >&2\n', True, installer_log)
        self.assertEqual(0, code)
        self.assertEqual([None], installer_log)
        self.assertEqual("[igloo-pre] Target disk: /dev/nvme0n1\n", stderr)
        self.assertEqual("", shown)

    def test_a_stop_is_shown_with_its_reason(self):
        body = 'echo "[igloo-pre] Searching" >&2\necho "[igloo-pre] ERROR: expected exactly one OEMDRV partition; found 2" >&2\nexit 1\n'
        code, stderr, shown = run(body, terminal=True)
        self.assertEqual(1, code)
        self.assertIn("ERROR: expected exactly one OEMDRV partition; found 2", stderr)
        self.assertIn("iGloo stopped the installation before anything was written to the disk (status 1).", shown)
        self.assertIn("   [igloo-pre] ERROR: expected exactly one OEMDRV partition; found 2", shown)
        self.assertIn("Restart the computer to return to Windows.", shown)

    def test_a_failing_command_is_shown_with_what_it_wrote(self):
        body = 'python3 -c "raise RuntimeError(\'Selected OEMDRV user-data root is missing\')"\necho not-reached >&2\n'
        code, stderr, shown = run(body, terminal=True)
        self.assertEqual(1, code)
        self.assertIn("RuntimeError: Selected OEMDRV user-data root is missing", shown)
        self.assertNotIn("not-reached", stderr + shown)

    def test_without_a_terminal_the_stop_still_ends_with_its_status_and_log(self):
        installer_log = []
        code, stderr, shown = run('echo "[igloo-pre] ERROR: reason" >&2\nexit 3\n', False, installer_log)
        self.assertEqual(3, code)
        self.assertEqual("[igloo-pre] ERROR: reason\n", stderr)
        self.assertEqual("", shown)
        # The file the installer's log tab follows gets the reason whether or not a terminal is there.
        self.assertEqual(["iGloo: the pre-install script stopped with status 3:\n[igloo-pre] ERROR: reason\n"],
                         installer_log)


if __name__ == "__main__":
    unittest.main()
