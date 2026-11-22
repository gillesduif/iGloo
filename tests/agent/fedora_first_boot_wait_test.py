"""While Fedora's first boot holds the sign-in screen back, the screen says what is going on."""

import os
import stat
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import TestCase, main

LAUNCHER = Path(__file__).resolve().parents[2] / "distros/fedora-kde/agent/first-boot.sh"
TEXT = ("iGloo Community is installing drivers and apps. This takes several minutes. "
        "The sign-in screen appears when it is done; do not switch the computer off.")


def show_wait_source():
    script = LAUNCHER.read_text(encoding="utf-8")
    start = script.index("show_wait() {")
    return script[start:script.index("\n}\n", start) + 3]


class FedoraFirstBootWaitTest(TestCase):
    def run_show_wait(self, root, plymouth):
        """Runs the function with a stand-in for plymouth: 'answers', 'silent' (installed, no splash) or None."""
        bin_dir = root / "bin"
        bin_dir.mkdir()
        calls = root / "plymouth-calls"
        if plymouth is not None:
            stub = bin_dir / "plymouth"
            stub.write_text("#!/usr/bin/env bash\n"
                            f'printf "%s\\n" "$*" >> "{calls}"\n'
                            + ('[ "$1" = "--ping" ] && exit 1\n' if plymouth == "silent" else "") + "exit 0\n",
                            encoding="utf-8")
            stub.chmod(stub.stat().st_mode | stat.S_IXUSR)
        console = root / "console"
        console.write_text("", encoding="utf-8")
        script = root / "run.sh"
        script.write_text("#!/usr/bin/env bash\nset -euo pipefail\n" + show_wait_source() +
                          f'\nshow_wait "{console}"\nshow_wait "{root / "no-such-console"}"\n', encoding="utf-8")
        environment = {**os.environ, "PATH": f"{bin_dir}:/usr/bin:/bin"}
        result = subprocess.run(["bash", str(script)], capture_output=True, text=True, env=environment, check=False)
        return result, (calls.read_text(encoding="utf-8") if calls.exists() else ""), console.read_text(encoding="utf-8")

    def test_the_splash_and_the_console_both_get_the_line(self):
        with TemporaryDirectory() as temporary:
            result, calls, console = self.run_show_wait(Path(temporary), "answers")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(f"display-message --text={TEXT}", calls)
        self.assertEqual(console, "\n" + TEXT + "\n")

    def test_without_a_splash_the_console_still_gets_it(self):
        for plymouth in ("silent", None):
            with self.subTest(plymouth=plymouth), TemporaryDirectory() as temporary:
                result, calls, console = self.run_show_wait(Path(temporary), plymouth)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertNotIn("display-message", calls)
                self.assertEqual(console, "\n" + TEXT + "\n")

    def test_the_launcher_shows_it_before_it_starts_the_agent(self):
        script = LAUNCHER.read_text(encoding="utf-8")
        self.assertLess(script.index("\nshow_wait /dev/tty1\n"), script.index('python3 "$AGENT_DIR/agent.py"'))


if __name__ == "__main__":
    main()
