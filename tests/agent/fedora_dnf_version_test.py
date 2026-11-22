"""The codecs step reads the DNF major version whatever language the system speaks."""

import importlib.util
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace
from unittest import TestCase, main, mock

REPOSITORY = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("fedora_dnf_agent", REPOSITORY / "distros/fedora-kde/agent/agent.py")
AGENT = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = AGENT
SPEC.loader.exec_module(AGENT)


class FedoraDnfVersionTest(TestCase):
    def test_the_first_line_gives_the_major_version_in_any_language(self):
        self.assertEqual(AGENT.dnf_major_version("dnf5 version 5.2.13.1"), 5)
        # The r31 run stopped here: its system speaks Dutch and the agent looked for the word "version".
        self.assertEqual(AGENT.dnf_major_version("dnf5 versie 5.2.13.1"), 5)
        self.assertEqual(AGENT.dnf_major_version("dnf5 Version 5.3.0.0"), 5)
        self.assertEqual(AGENT.dnf_major_version("4.19.2"), 4)
        self.assertEqual(AGENT.dnf_major_version("dnf version 4.14.0"), 4)

    def test_a_line_without_a_version_gives_nothing(self):
        for line in ("", "dnf5", "version five", "dnf5 version 6.0.0", "5", "something 5.x"):
            self.assertIsNone(AGENT.dnf_major_version(line), line)

    def test_dnf_is_asked_in_the_c_locale_and_an_unknown_answer_is_quoted(self):
        calls = []

        def fake_run(argv, **kwargs):
            calls.append((argv, kwargs["env"]))
            return SimpleNamespace(returncode=0, stdout=answer, stderr="")

        with mock.patch.object(subprocess, "run", side_effect=fake_run), \
                mock.patch.dict(AGENT.os.environ, {"LANG": "nl_BE.UTF-8"}, clear=False):
            answer = "dnf5 version 5.2.13.1\ndnf5 plugin API version 2.0\n"
            self.assertTrue(AGENT._is_dnf5())
            answer = "4.19.2\n  Installed: dnf-0:4.19.2\n"
            self.assertFalse(AGENT._is_dnf5())
            answer = "iets heel anders\n"
            with self.assertRaisesRegex(RuntimeError, "unknown format: 'iets heel anders'"):
                AGENT._is_dnf5()
        self.assertEqual(calls[0][0], ["dnf", "--version"])
        for _argv, env in calls:
            self.assertEqual((env["LC_ALL"], env["LANGUAGE"], env["LANG"]), ("C.UTF-8", "C", "nl_BE.UTF-8"))


if __name__ == "__main__":
    main()
