"""Fedora's kickstart, as the renderer writes it, is read by pykickstart, the parser the installer uses.

The renderings come from the .NET tests (tests/Igloo.Migration.Tests keeps them beside its binaries) and from the
reviewed fixture. pykickstart is optional: without it every test here is skipped, and the run says so.
"""

import re
import sys
import warnings
from pathlib import Path
from unittest import TestCase, main, skipUnless

REPOSITORY = Path(__file__).resolve().parents[2]
TEMPLATE = REPOSITORY / "distros/fedora-kde/kickstart/ks.cfg.template"
FIXTURE = REPOSITORY / "tests/installer/FedoraDeferredReviewedFixture.ks"
RENDERED = sorted((REPOSITORY / "tests/Igloo.Migration.Tests/bin").glob("*/*/rendered-kickstarts/*.ks"))

# Unpacked there by hand when no package manager offers it; never required.
CACHE = Path.home() / ".cache/igloo-pykickstart/site"
if CACHE.is_dir():
    sys.path.insert(0, str(CACHE))
try:
    from pykickstart.errors import KickstartError
    from pykickstart.parser import KickstartParser
    from pykickstart.version import DEVEL, makeVersion
    PARSER = True
except ImportError:
    PARSER = False


def read(text):
    """Parses kickstart text as the installer does before it acts; returns the warnings the parser gave."""
    parser = KickstartParser(makeVersion(DEVEL), followIncludes=False, errorsAreFatal=True,
                             missingIncludeIsFatal=False)
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        parser.readKickstartFromString(text)
    return [str(item.message) for item in caught]


def storage_commands(mode):
    """The lines the pre-install script writes to /tmp/ks-storage.cfg for one install mode, for a disk named nvme0n1."""
    script = TEMPLATE.read_text(encoding="utf-8")
    block = script[:script.index("} > /tmp/ks-storage.cfg")]
    block = block[block.rindex("\n    {\n"):]
    lines = []
    branch = None
    for line in block.splitlines():
        stripped = line.strip()
        if stripped.startswith('if [ "$INSTALL_MODE" = "dual-boot" ]'):
            branch = "dual-boot"
        elif stripped == "else":
            branch = "replace"
        elif stripped == "fi":
            branch = None
        match = re.fullmatch(r'echo "(.*)"', stripped)
        if match and branch in (None, mode):
            lines.append(match.group(1).replace("${TARGET_DISK}", "nvme0n1"))
    return lines


@skipUnless(PARSER, "pykickstart is not installed; the kickstart was not read by a parser")
class FedoraKickstartParseTest(TestCase):
    def test_the_reviewed_fixture_parses(self):
        self.assertEqual(read(FIXTURE.read_text(encoding="utf-8")), [])

    def test_every_rendering_the_dotnet_tests_kept_parses(self):
        if not RENDERED:
            self.skipTest("no rendering found; run the .NET tests of Igloo.Migration.Tests first")
        names = {path.name for path in RENDERED}
        self.assertTrue({"fedora-two-browsers-of-each-kind.ks", "fedora-dual-boot-with-password.ks",
                         "fedora-replace-with-password.ks"} <= names, names)
        for path in RENDERED:
            with self.subTest(path=path.name):
                self.assertEqual(read(path.read_text(encoding="utf-8")), [])

    def test_the_storage_commands_of_both_modes_parse(self):
        dual_boot, replace = storage_commands("dual-boot"), storage_commands("replace")
        self.assertEqual(dual_boot, ["bootloader --boot-drive=nvme0n1", "ignoredisk --only-use=nvme0n1",
                                     "clearpart --none", "autopart --type=plain --nohome"])
        self.assertIn("clearpart --drives=nvme0n1 --all --initlabel", replace)
        for commands in (dual_boot, replace):
            self.assertEqual(read("\n".join(commands) + "\n"), [])

    def test_the_line_that_stopped_r30_is_refused_by_this_parser(self):
        # The second line of a two-browser map outside its comment, as Anaconda met it on line 24.
        text = FIXTURE.read_text(encoding="utf-8").replace(
            "\ncmdline\n", "\nOpera|browser-profiles/AppData/Roaming/Opera Software/Opera Stable\ncmdline\n", 1)
        with self.assertRaises(KickstartError) as refused:
            read(text)
        self.assertIn("Unknown command: Opera|browser-profiles/AppData/Roaming/Opera", str(refused.exception))


if __name__ == "__main__":
    main()
