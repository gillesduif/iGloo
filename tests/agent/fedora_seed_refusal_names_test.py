"""A refused installer seed says which file it is about, and the installer's limits are those of the writer."""

from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
import re
import stat
import subprocess
import sys
import uuid
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest import TestCase, main, mock


REPOSITORY = Path(__file__).resolve().parents[2]
KICKSTART = REPOSITORY / "distros/fedora-kde/kickstart/ks.cfg.template"
WRITER = REPOSITORY / "src/Igloo.Core/Preparation/SeedIdentityV1.cs"
SPEC = importlib.util.spec_from_file_location("fedora_refusal_agent", REPOSITORY / "distros/fedora-kde/agent/agent.py")
AGENT = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = AGENT
SPEC.loader.exec_module(AGENT)

# A name of seven letters outside the basic plane, as one of the owner's test files carries.
BOLD = "".join(chr(code) for code in (0x1D5DE, 0x1D5E0, 0x1D5E6, 0x1D5D8, 0x1D5DF, 0x1D5D7, 0x1D5DC)) + ".exe"
AGENT_FILES = ("agent.py", "first-boot.sh", "display-apply.py", "igloo_boot.py")


def validator():
    return KICKSTART.read_text(encoding="utf-8").split(
        "<<'IGLOO_SEED_VALIDATOR'\n", 1)[1].split("\nIGLOO_SEED_VALIDATOR", 1)[0]


class Seed:
    def __init__(self, root):
        self.root = root / "seed"
        self.device = root / "seed-partition"
        self.protected = root / "protected-identity.json"
        self.device.touch()
        (self.root / "igloo-agent").mkdir(parents=True)
        self.disk, self.partition = uuid.uuid4(), uuid.uuid4()
        self.names = ["migration-manifest.json", "ks.cfg"]
        for name in AGENT_FILES:
            (self.root / "igloo-agent" / name).write_bytes(name.encode())
            self.names.append("igloo-agent/" + name)
        manifest = {"files": {"folders": [{"name": "Downloads"}], "stagingPath": "files"}}
        (self.root / "migration-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        (self.root / "ks.cfg").write_bytes(b"# bound kickstart")
        (self.root / "files/Downloads").mkdir(parents=True)
        for name in ("2.exe", BOLD):
            (self.root / "files/Downloads" / name).write_bytes(b"selected " + name.encode())
            self.names.append("files/Downloads/" + name)
        self.entries = [{"path": name, "sha256": hashlib.sha256((self.root / name).read_bytes()).hexdigest().upper()}
                        for name in self.names]

    def identity(self):
        return json.dumps({"version": 1, "diskGuid": str(self.disk), "partitionGuid": str(self.partition),
                           "files": self.entries}).encode()

    def run_installer_check(self):
        (self.root / "igloo-seed-identity.json").write_bytes(self.identity())
        original = Path.read_text

        def read_text(path, *args, **kwargs):
            return "" if path.as_posix() == "/proc/cmdline" else original(path, *args, **kwargs)

        def read_id(argv, **_kwargs):
            if argv[:3] == ["blkid", "-s", "PARTUUID"]:
                return str(self.partition)
            if argv[:3] == ["blkid", "-s", "PTUUID"]:
                return str(self.disk)
            return "loop5\n"

        with mock.patch.object(sys, "argv", ["validator", str(self.device), str(self.root)]), \
                mock.patch.object(stat, "S_ISBLK", return_value=True), \
                mock.patch.object(subprocess, "check_output", side_effect=read_id), \
                mock.patch.object(Path, "read_text", read_text), \
                redirect_stdout(io.StringIO()):
            exec(compile(validator(), str(KICKSTART), "exec"), {})

    def run_first_boot_check(self):
        payload = self.identity()
        self.protected.write_bytes(payload)
        (self.root / "igloo-seed-identity.json").write_bytes(payload)

        def device_command(argv, **_kwargs):
            if argv[:3] == ["blkid", "-s", "PARTUUID"]:
                return SimpleNamespace(stdout=str(self.partition))
            if argv[:3] == ["blkid", "-s", "PTUUID"]:
                return SimpleNamespace(stdout=str(self.disk))
            return SimpleNamespace(stdout="loop5\n")

        with mock.patch.object(AGENT.Path, "is_block_device", return_value=True), \
                mock.patch.object(AGENT, "run_cmd", side_effect=device_command):
            return AGENT._read_bound_seed_files(self.root, "/dev/loop5p1", self.protected)


class FedoraSeedRefusalNamesTest(TestCase):
    def refusal(self, check):
        with self.assertRaises(RuntimeError) as raised:
            check()
        return str(raised.exception)

    def test_a_name_outside_the_basic_plane_is_bound_and_accepted(self):
        with TemporaryDirectory() as temporary:
            seed = Seed(Path(temporary))
            seed.run_installer_check()
            self.assertEqual(set(seed.run_first_boot_check()), {"migration-manifest.json", "ks.cfg"})

    def test_a_named_file_the_partition_does_not_show_is_named_with_its_folder(self):
        with TemporaryDirectory() as temporary:
            seed = Seed(Path(temporary))
            (seed.root / "files/Downloads" / BOLD).unlink()
            message = self.refusal(seed.run_installer_check)
            self.assertIn("names a file the partition does not show: 'files/Downloads/\\U0001d5de", message)
            self.assertIn("its folder holds 1 entries, the first are ['2.exe']", message)
            self.assertIn("names a file the partition does not show: 'files/Downloads/\\U0001d5de",
                          self.refusal(seed.run_first_boot_check))

    def test_a_named_file_whose_folder_is_gone_says_so(self):
        with TemporaryDirectory() as temporary:
            seed = Seed(Path(temporary))
            seed.entries.append({"path": "files/Downloads/KMSpico/setup.exe", "sha256": "A" * 64})
            self.assertIn("'files/Downloads/KMSpico/setup.exe'; its folder is not on the partition",
                          self.refusal(seed.run_installer_check))

    def test_a_path_that_leaves_the_partition_gets_no_listing(self):
        with TemporaryDirectory() as temporary:
            seed = Seed(Path(temporary))
            for hostile in ("../../etc/shadow", "/etc/shadow", "files/../../etc/shadow"):
                seed.entries.append({"path": hostile, "sha256": "A" * 64})
                message = self.refusal(seed.run_installer_check)
                self.assertIn("names a file the partition does not show: " + ascii(hostile), message)
                self.assertNotIn("its folder", message)
                seed.entries.pop()

    def test_a_double_entry_and_an_entry_without_a_hash_are_named(self):
        with TemporaryDirectory() as temporary:
            seed = Seed(Path(temporary))
            seed.entries.append(dict(seed.entries[1]))
            self.assertIn("names a file twice: 'ks.cfg'", self.refusal(seed.run_installer_check))
            self.assertIn("names a file twice: 'ks.cfg'", self.refusal(seed.run_first_boot_check))
            seed.entries.pop()
            seed.entries[1] = {"path": "ks.cfg", "sha256": seed.entries[1]["sha256"].lower()}
            self.assertIn("has no SHA-256 for: 'ks.cfg'", self.refusal(seed.run_installer_check))
            self.assertIn("has no SHA-256 for: 'ks.cfg'", self.refusal(seed.run_first_boot_check))

    def test_a_file_on_the_partition_that_is_not_named_is_named_in_the_refusal(self):
        with TemporaryDirectory() as temporary:
            seed = Seed(Path(temporary))
            (seed.root / "files/Downloads/extra.txt").write_bytes(b"not bound")
            self.assertIn("1 files on the partition are not named, the first are ['files/Downloads/extra.txt']",
                          self.refusal(seed.run_installer_check))

    def test_the_installer_admits_as_many_files_as_the_writer_and_the_first_boot_check(self):
        script = validator()
        writer = WRITER.read_text(encoding="utf-8")

        def written(name):
            return int(re.search(name + r" = ([0-9_]+);", writer).group(1).replace("_", ""))

        entries = int(re.search(r"entries_seen > ([0-9]+)", script).group(1))
        files = int(re.search(r"len\(observed\) >= ([0-9]+)", script).group(1))
        characters = int(re.search(r"path_characters > ([0-9]+)", script).group(1))
        roots = int(re.search(r"len\(roots\) >= ([0-9]+)", script).group(1))
        self.assertEqual((files, entries, characters, roots),
                         (written("MaxSelectedDataFiles"), written("MaxSelectedDataEntries"),
                          written("MaxSelectedPathCharacters"), written("MaxSelectedDataRoots")))
        self.assertEqual((files, entries, characters),
                         (AGENT._MAX_USER_FILES, AGENT._MAX_USER_ENTRIES, AGENT._MAX_USER_PATH_CHARACTERS))


if __name__ == "__main__":
    main()
