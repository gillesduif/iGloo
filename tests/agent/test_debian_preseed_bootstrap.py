"""Exercise the Debian preseed's generated first-boot bootstrap."""

import os
import shlex
from pathlib import Path
import subprocess
import tempfile
import unittest


PRESEED = (Path(__file__).resolve().parents[2] / "distros" / "debian"
           / "preseed" / "preseed.cfg.template")
LATE_COMMAND = "d-i preseed/late_command string "
REQUIRED = ("agent.py", "first-boot.sh", "display-apply.py",
            "display-apply-gnome.py", "igloo_boot.py", "igloo-first-boot.service",
            "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz")
PART_GUID = "6303f619-17ad-4017-acc1-05d3a5c51c38"
DISK_GUID = "4e76d226-9262-45e1-8571-c74a53c0ebbf"


def late_command() -> str:
    lines = PRESEED.read_text().splitlines()
    first = next(i for i, line in enumerate(lines) if line.startswith(LATE_COMMAND))
    command_lines = [lines[first][len(LATE_COMMAND):]]
    for line in lines[first + 1:]:
        command_lines.append(line)
        if not line.rstrip().endswith("\\"):
            break
    command = " ".join(line.strip().removesuffix("\\").strip() for line in command_lines)
    return command


class DebianPreseedBootstrapTests(unittest.TestCase):
    def write_bootstrap(self, root: Path) -> Path:
        target = root / "target"
        (target / "etc" / "apt").mkdir(parents=True)
        (target / "etc" / "default").mkdir(parents=True)
        cmdline = root / "installer-cmdline"
        cmdline.write_text(f"auto=true igloo.seedpartuuid={PART_GUID} igloo.seeddiskguid={DISK_GUID} ---\n")
        command = (late_command().replace("/target", str(target))
                   .replace("/proc/cmdline", str(cmdline)))
        verifier = PRESEED.parents[2] / "_debian-family/agent/igloo-verify-seed.py"
        command = command.replace("cp /igloo-verify-seed.py ", f"cp {shlex.quote(str(verifier))} ")
        previous = os.umask(0o077)
        try:
            result = subprocess.run(["sh", "-c", command], capture_output=True, text=True)
        finally:
            os.umask(previous)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(0o755, (target / "opt").stat().st_mode & 0o777)
        self.assertEqual(0o755, (target / "opt/igloo").stat().st_mode & 0o777)
        bootstrap = target / "opt" / "igloo" / "igloo-bootstrap.sh"
        self.assertEqual((PRESEED.parents[2] / "_debian-family/agent/igloo-verify-seed.py").read_bytes(),
                         (target / "opt/igloo/igloo-verify-seed.py").read_bytes())
        syntax = subprocess.run(["sh", "-n", str(bootstrap)], capture_output=True, text=True)
        self.assertEqual(0, syntax.returncode, syntax.stderr)
        unit = (target / "etc/systemd/system/igloo-bootstrap.service").read_text()
        self.assertNotIn("ConditionPathExists=!/var/lib/igloo/.done", unit)
        self.assertEqual(0o644, (target / "etc/systemd/system/igloo-bootstrap.service").stat().st_mode & 0o777)
        self.assertEqual(PART_GUID + "\n", (target / "var/lib/igloo/selected-seed-partuuid").read_text())
        self.assertEqual(DISK_GUID + "\n", (target / "var/lib/igloo/selected-seed-diskguid").read_text())
        self.assertEqual(0o600, (target / "var/lib/igloo/selected-seed-partuuid").stat().st_mode & 0o777)
        self.assertEqual(0o600, (target / "var/lib/igloo/selected-seed-diskguid").stat().st_mode & 0o777)
        return bootstrap

    def test_generated_bootstrap_copies_staged_payload_and_protects_manifest(self) -> None:
        with tempfile.TemporaryDirectory(prefix="igloo-debian-preseed-") as temporary:
            root = Path(temporary)
            bootstrap = self.write_bootstrap(root)
            seed = root / "seed"
            agent_files = seed / "igloo-agent"
            agent_files.mkdir(parents=True)
            for name in REQUIRED:
                (agent_files / name).write_text("fixture\n")
            (agent_files / "first-boot.sh").write_text(
                "#!/bin/sh\ntest -f " + str(seed / "files" / "Documents" / "probe.txt") + "\n")
            (seed / "migration-manifest.json").write_text('{"schemaVersion":1}\n')
            (seed / "igloo-seed-identity.json").write_text('{"version":1}\n')
            (seed / "files" / "Documents").mkdir(parents=True)
            (seed / "files" / "Documents" / "probe.txt").write_text("staged\n")
            (seed / "igloo-wallpaper.PNG").write_bytes(b"selected wallpaper")
            (seed / "igloo-avatar.jpg").write_bytes(b"selected avatar")
            (seed / "igloo-wallpaper.other").write_bytes(b"unselected media")

            opt_parent = root / "system-opt"
            opt = opt_parent / "igloo"
            state = root / "state"
            logs = root / "logs"
            state.mkdir()
            (state / "selected-seed-partuuid").write_text(PART_GUID + "\n")
            (state / "selected-seed-diskguid").write_text(DISK_GUID + "\n")
            bin_dir = root / "bin"
            bin_dir.mkdir()
            for name in ("mount", "mountpoint", "findmnt", "modprobe", "python3"):
                fake = bin_dir / name
                if name == "mount":
                    body = "echo mounted >> " + shlex.quote(str(root / "mount-invocations")) + "\n"
                elif name == "mountpoint":
                    body = "test -e " + shlex.quote(str(root / "already-mounted")) + "\n"
                elif name == "findmnt":
                    body = "cat " + shlex.quote(str(root / "mount-options")) + "\n"
                elif name == "python3":
                    body = "printf 'igloo-wallpaper.PNG\\nigloo-avatar.jpg\\n'\n"
                else:
                    body = ""
                fake.write_text("#!/bin/sh\n" + body +
                                ("" if name == "mountpoint" else "exit 0\n"))
                fake.chmod(0o755)
            script = bootstrap.read_text()
            for source, replacement in (("/opt/igloo", opt), ("/var/lib/igloo", state),
                                        ("/var/log/igloo", logs), ("/run/igloo-seed", seed)):
                script = script.replace(source, str(replacement))
            script = script.replace("chmod 0755 /opt ",
                                    "chmod 0755 " + str(opt_parent) + " ")
            script = script.replace("/dev/disk/by-partuuid/", str(root / "by-partuuid") + "/")
            runnable = root / "bootstrap.sh"
            runnable.write_text(script)
            env = dict(os.environ, PATH=str(bin_dir) + os.pathsep + os.environ["PATH"])
            previous = os.umask(0o077)
            try:
                result = subprocess.run(["sh", str(runnable)], env=env,
                                        capture_output=True, text=True)
            finally:
                os.umask(previous)
            self.assertEqual(0, result.returncode, (logs / "bootstrap.log").read_text())
            self.assertEqual(0o755, opt_parent.stat().st_mode & 0o777)
            self.assertEqual(0o755, opt.stat().st_mode & 0o777)
            self.assertEqual(0o644, (opt / "igloo_boot.py").stat().st_mode & 0o777)
            self.assertEqual(0o600, (state / "manifest.json").stat().st_mode & 0o777)
            self.assertEqual(0o600, (state / "seed-identity.json").stat().st_mode & 0o777)
            self.assertEqual(b"selected wallpaper", (opt / "igloo-wallpaper.PNG").read_bytes())
            self.assertEqual(b"selected avatar", (opt / "igloo-avatar.jpg").read_bytes())
            self.assertFalse((opt / "igloo-wallpaper.other").exists())
            self.assertNotIn("umount", (logs / "bootstrap.log").read_text())
            self.assertIn(str(root / "by-partuuid" / PART_GUID), (logs / "bootstrap.log").read_text())
            self.assertEqual(["mounted"], (root / "mount-invocations").read_text().splitlines())

            (state / "manifest.retry.json").write_text('{"retry":"original"}\n')
            (seed / "migration-manifest.json").write_text('{"retry":"redacted"}\n')
            (root / "already-mounted").touch()
            (root / "mount-options").write_text("rw,nosuid,nodev\n")
            (state / "manifest.json").write_text('{"prior":"unchanged"}\n')
            result = subprocess.run(["sh", str(runnable)], env=env, capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertEqual('{"prior":"unchanged"}\n', (state / "manifest.json").read_text())
            self.assertEqual(["mounted"], (root / "mount-invocations").read_text().splitlines())

            (root / "mount-options").write_text("ro,nosuid,nodev\n")
            result = subprocess.run(["sh", str(runnable)], env=env, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, (logs / "bootstrap.log").read_text())
            self.assertEqual('{"retry":"original"}\n', (state / "manifest.json").read_text())
            self.assertEqual(["mounted"], (root / "mount-invocations").read_text().splitlines())

            (state / "manifest.json").unlink()
            (agent_files / "agent.py").unlink()
            result = subprocess.run(["sh", str(runnable)], env=env, capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((state / "manifest.json").exists())

    def test_late_command_requires_unique_seed_kernel_guid_pair(self) -> None:
        with tempfile.TemporaryDirectory(prefix="igloo-debian-args-") as temporary:
            root = Path(temporary)
            target = root / "target"
            (target / "etc/apt").mkdir(parents=True)
            (target / "etc/default").mkdir(parents=True)
            cmdline = root / "cmdline"
            command = (late_command().replace("/target", str(target))
                       .replace("/proc/cmdline", str(cmdline)))
            for args in ("auto=true", f"igloo.seedpartuuid={PART_GUID}",
                         f"igloo.seedpartuuid={PART_GUID} igloo.seedpartuuid={PART_GUID} igloo.seeddiskguid={DISK_GUID}",
                         f"igloo.seedpartuuid={PART_GUID} igloo.seeddiskguid={DISK_GUID} igloo.seeddiskguid={DISK_GUID}"):
                cmdline.write_text(args + "\n")
                result = subprocess.run(["sh", "-c", command], capture_output=True, text=True)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse((target / "opt/igloo/igloo-bootstrap.sh").exists())


if __name__ == "__main__":
    unittest.main()
