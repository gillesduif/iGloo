"""Exercise Mint's Ubiquity success_command: the two-phase first-boot bootstrap (ADR-012)."""

import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
PRESEED = ROOT / "distros/linuxmint-cinnamon/preseed/preseed.cfg.template"
BOOTSTRAP = ROOT / "distros/linuxmint-cinnamon/preseed/igloo-bootstrap.sh"
SERVICE = ROOT / "distros/linuxmint-cinnamon/preseed/igloo-bootstrap.service"
VERIFIER = ROOT / "distros/_debian-family/agent/igloo-verify-seed.py"
PARTUUID = "90fac514-2dc6-40b7-afda-9bb92f4dc18e"
DISKGUID = "96e21da7-ac4d-4266-9902-cdcae32e3530"
LINUX_ESP_PARTUUID = "6ff59057-dfc2-4235-8100-df6dd3185b23"


def rendered_bootstrap(verifier: bytes) -> bytes:
    return BOOTSTRAP.read_bytes().replace(
        b"{{IGLOO_SEED_VERIFIER_SHA256}}",
        hashlib.sha256(verifier).hexdigest().upper().encode()).replace(
        b"{{IGLOO_BOOTSTRAP_SERVICE_SHA256}}",
        hashlib.sha256(SERVICE.read_bytes()).hexdigest().upper().encode())


def success_command(cmdline: Path, bootstrap: bytes, service: bytes) -> str:
    lines = PRESEED.read_text().splitlines()
    prefix = "ubiquity ubiquity/success_command string "
    first = next(i for i, line in enumerate(lines) if line.startswith(prefix))
    selected = [lines[first][len(prefix):]]
    for line in lines[first + 1:]:
        selected.append(line)
        if not line.rstrip().endswith("\\"):
            break
    command = " ".join(line.strip().removesuffix("\\").strip() for line in selected)
    return (command.replace("/proc/cmdline", str(cmdline))
            .replace("{{IGLOO_BOOTSTRAP_SHA256}}", hashlib.sha256(bootstrap).hexdigest().upper()))


class MintPreseedBootstrapTests(unittest.TestCase):
    def run_installer(self, root: Path, cmdline: str | None = None,
                      changed: str | None = None) -> tuple[subprocess.CompletedProcess, Path, Path, Path]:
        target = root / "target"
        seed = root / "seed"
        bin_dir = root / "bin"
        (target / "var/log").mkdir(parents=True)
        (target / "boot/efi").mkdir(parents=True)
        (target / "etc").mkdir()
        (seed / "igloo-agent").mkdir(parents=True)
        bin_dir.mkdir()
        esp = root / "esp-device"
        esp.write_bytes(b"disposable ESP fixture")
        (target / "etc/fstab").write_text("UUID=MINT-ESP /boot/efi vfat umask=0077 0 1\n")
        protected = root / "protected-esp-hashes"
        protected.write_text("")
        by_partuuid = root / "by-partuuid"
        by_partuuid.mkdir()
        (by_partuuid / PARTUUID).symlink_to(seed, target_is_directory=True)
        (by_partuuid / LINUX_ESP_PARTUUID).symlink_to(esp)
        kernel = root / "cmdline"
        kernel.write_text(cmdline if cmdline is not None else
                          f"boot=casper igloo.seedpartuuid={PARTUUID} "
                          f"igloo.seeddiskguid={DISKGUID} "
                          f"igloo.linuxesppartuuid={LINUX_ESP_PARTUUID} ---")
        verifier = b"raise SystemExit(0)\n"
        bootstrap = rendered_bootstrap(verifier).decode()
        bootstrap = (bootstrap.replace("/proc/cmdline", str(kernel))
                     .replace("/target", str(target))
                     .replace("/run/igloo-seed", str(seed))
                     .replace("/dev/disk/by-partuuid", str(by_partuuid))
                     .replace("/run/igloo-protected-esp-hashes", str(protected))
                     # This unit fixture has a regular file in place of a block
                     # device. The native installer validates the actual ESP.
                     .replace('test -b "$esp_path"', 'test -f "$esp_path"')).encode()
        service = SERVICE.read_bytes()
        (seed / "igloo-agent/igloo-bootstrap.sh").write_bytes(bootstrap)
        (seed / "igloo-agent/igloo-bootstrap.service").write_bytes(service)
        (seed / "igloo-agent/igloo-verify-seed.py").write_bytes(verifier)
        if changed:
            with (seed / "igloo-agent" / changed).open("ab") as stream:
                stream.write(b"tampered\n")
        mount_log = root / "mount-arguments"
        (bin_dir / "mount").write_text(
            f"#!/bin/sh\nprintf '%s\\n' \"$@\" >> '{mount_log}'\nexit 0\n")
        (bin_dir / "findmnt").write_text(
            "#!/bin/sh\n"
            "case \"$*\" in\n"
            "  *' -o OPTIONS '*) printf 'ro\\n' ;;\n"
            f"  *'{target}/boot/efi'*) printf '%s\\n' '{esp}' ;;\n"
            f"  *) printf '%s\\n' '{seed}' ;;\n"
            "esac\n")
        (bin_dir / "blkid").write_text("#!/bin/sh\nprintf 'MINT-ESP\\n'\n")
        (bin_dir / "umount").write_text(
            f"#!/bin/sh\nprintf 'umount %s\\n' \"$@\" >> '{mount_log}'\nexit 0\n")
        (bin_dir / "mount").chmod(0o755)
        (bin_dir / "findmnt").chmod(0o755)
        (bin_dir / "blkid").chmod(0o755)
        (bin_dir / "umount").chmod(0o755)
        command = success_command(kernel, bootstrap, service)
        command = command.replace("/target", str(target))
        command = command.replace("/run/igloo-seed", str(seed))
        command = command.replace("/dev/disk/by-partuuid", str(root / "by-partuuid"))
        env = dict(os.environ, PATH=str(bin_dir) + os.pathsep + os.environ["PATH"])
        result = subprocess.run(["sh", "-c", command], env=env, capture_output=True,
                                text=True, check=False)
        return result, target, seed, mount_log

    def test_success_writes_the_two_phase_bootstrap_without_touching_the_seed(self) -> None:
        # v0.2-alpha / ADR-012: success_command records the seed identity from the
        # kernel command line and writes a self-contained bootstrap; the seed is
        # mounted and verified on the first boot of the installed system, not here.
        with tempfile.TemporaryDirectory(prefix="igloo-mint-install-") as temporary:
            result, target, seed, mount_log = self.run_installer(Path(temporary))
            self.assertEqual(0, result.returncode,
                             (target / "var/log/igloo-install.log").read_text())
            installed = target / "opt/igloo/igloo-bootstrap.sh"
            self.assertTrue(installed.is_file())
            self.assertEqual(0o755, installed.stat().st_mode & 0o777)
            self.assertEqual(0o755, (target / "opt").stat().st_mode & 0o777)
            self.assertEqual(0o755, (target / "opt/igloo").stat().st_mode & 0o777)
            self.assertEqual(PARTUUID + "\n", (target / "var/lib/igloo/selected-seed-partuuid").read_text())
            self.assertEqual(DISKGUID + "\n", (target / "var/lib/igloo/selected-seed-diskguid").read_text())
            self.assertEqual(0o600, (target / "var/lib/igloo/selected-seed-partuuid").stat().st_mode & 0o777)
            self.assertEqual(0o600, (target / "var/lib/igloo/selected-seed-diskguid").stat().st_mode & 0o777)
            service = target / "etc/systemd/system/igloo-bootstrap.service"
            self.assertIn("Before=display-manager.service", service.read_text())
            self.assertIn("ExecStart=/opt/igloo/igloo-bootstrap.sh", service.read_text())
            self.assertTrue((target / "etc/systemd/system/multi-user.target.wants/igloo-bootstrap.service").is_symlink())
            # Nothing is mounted or read from the seed at install time.
            self.assertFalse(mount_log.exists(), mount_log.read_text() if mount_log.exists() else "")
            self.assertFalse((seed / "igloo-agent/igloo-bootstrap.sh").read_bytes() == installed.read_bytes())
            self.assertEqual(0, subprocess.run(["sh", "-n", str(installed)], check=False).returncode)
            bootstrap = installed.read_text()
            self.assertIn("/by-partuuid/$seed_partuuid", bootstrap)
            self.assertIn("mount -t vfat -o ro", bootstrap)
            self.assertLess(bootstrap.index("igloo-agent/igloo-verify-seed.py /opt/igloo/igloo-verify-seed.py"),
                            bootstrap.index("python3 /opt/igloo/igloo-verify-seed.py"))
            self.assertLess(bootstrap.index("python3 /opt/igloo/igloo-verify-seed.py"),
                            bootstrap.index("cp " + str(seed) + "/igloo-agent/agent.py"))
            self.assertIn("igloo-seed-identity.json /var/lib/igloo/seed-identity.json", bootstrap)
            self.assertTrue(bootstrap.rstrip().endswith("exec /opt/igloo/first-boot.sh"))

    def test_rejects_a_missing_ambiguous_or_malformed_seed_selection(self) -> None:
        seed = f"igloo.seedpartuuid={PARTUUID}"
        disk = f"igloo.seeddiskguid={DISKGUID}"
        invalid = (
            "boot=casper ---",
            f"{disk}",
            f"{seed}",
            f"{seed} {seed} {disk}",
            f"{seed} {disk} {disk}",
            f"igloo.seedpartuuid=bad {disk}",
            f"{seed} igloo.seeddiskguid=not-a-guid",
        )
        for cmdline in invalid:
            with self.subTest(cmdline=cmdline), tempfile.TemporaryDirectory() as temporary:
                result, target, _, _ = self.run_installer(Path(temporary), cmdline)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse((target / "opt/igloo/igloo-bootstrap.sh").exists())
                self.assertFalse((target / "var/lib/igloo/selected-seed-partuuid").exists())

    def test_first_boot_verifies_exact_helper_before_payload_import(self) -> None:
        bootstrap = rendered_bootstrap(VERIFIER.read_bytes()).decode()
        self.assertLess(bootstrap.index("igloo-verify-seed.py | sha256sum"),
                        bootstrap.index("python3 /opt/igloo/igloo-verify-seed.py"))
        self.assertLess(bootstrap.index("python3 /opt/igloo/igloo-verify-seed.py"),
                        bootstrap.index("cp /run/igloo-seed/igloo-agent/agent.py"))
        self.assertLess(bootstrap.index("cp /run/igloo-seed/igloo-agent/agent.py"),
                        bootstrap.index("install -m 0600 /run/igloo-seed/migration-manifest.json"))
        self.assertNotIn("{{", bootstrap)

    def test_first_boot_copies_only_exact_selected_media(self) -> None:
        for omit_avatar in (False, True):
            with self.subTest(omit_avatar=omit_avatar), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                seed = root / "seed"
                agent = seed / "igloo-agent"
                opt = root / "opt/igloo"
                opt_parent = root / "opt"
                state = root / "var/lib/igloo"
                log = root / "var/log/igloo"
                bin_dir = root / "bin"
                for directory in (agent, opt, state, log, bin_dir):
                    directory.mkdir(parents=True)
                opt.chmod(0o700)
                opt_parent.chmod(0o700)
                state.chmod(0o700)
                (state / "selected-seed-partuuid").write_text(PARTUUID)
                (state / "selected-seed-diskguid").write_text(DISKGUID)
                by_partuuid = root / "by-partuuid"
                by_partuuid.mkdir()
                (by_partuuid / PARTUUID).symlink_to(seed, target_is_directory=True)
                (seed / "migration-manifest.json").write_text("{}")
                (seed / "igloo-seed-identity.json").write_text("{}")
                (seed / "IGLOO-WALLPAPER.PNG").write_bytes(b"selected wallpaper")
                if not omit_avatar:
                    (seed / "igloo-avatar.jpg").write_bytes(b"selected avatar")
                verification_log = root / "verification-runs"
                verifier = (
                    f"with open('{verification_log}', 'a') as log: log.write('verified\\n')\n"
                    "print('igloo-avatar.jpg')\nprint('IGLOO-WALLPAPER.PNG')\n"
                ).encode()
                (agent / "igloo-verify-seed.py").write_bytes(verifier)
                for name in ("agent.py", "display-apply.py", "display-apply-gnome.py",
                             "igloo_boot.py", "igloo-first-boot.service",
                             "grub-theme-stylish-1080p.tar.gz",
                             "grub-theme-stylish-4k.tar.gz"):
                    (agent / name).write_bytes(b"fixture\n")
                marker = root / "first-boot-ran"
                attempted = root / "first-boot-attempted"
                (agent / "first-boot.sh").write_text(
                    f"#!/bin/sh\nif [ ! -e '{attempted}' ]; then "
                    f"touch '{attempted}'; exit 23; fi\n"
                    f"printf 'yes' > '{marker}'\n")
                mounted = root / "seed-mounted"
                mount_log = root / "mount-arguments"
                source = root / "mounted-source"
                options = root / "mount-options"
                source.write_text(str(seed) + "\n")
                options.write_text("ro\n")
                (bin_dir / "mount").write_text(
                    f"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{mount_log}'\n"
                    f"touch '{mounted}'\n")
                (bin_dir / "mountpoint").write_text(
                    f"#!/bin/sh\ntest -f '{mounted}'\n")
                (bin_dir / "findmnt").write_text(
                    "#!/bin/sh\ncase \"$*\" in\n"
                    f"  *' -o OPTIONS '*) cat '{options}' ;;\n"
                    f"  *) cat '{source}' ;;\n"
                    "esac\n")
                for name in ("mount", "mountpoint", "findmnt"):
                    (bin_dir / name).chmod(0o755)
                bootstrap = (rendered_bootstrap(verifier).decode()
                             .replace("/run/igloo-seed", str(seed))
                             .replace("/run/igloo-selected-media", str(root / "selected-media"))
                             .replace("/opt/igloo", str(opt))
                             .replace("/var/lib/igloo", str(state))
                             .replace("/var/log/igloo", str(log))
                             .replace("/dev/disk/by-partuuid", str(root / "by-partuuid")))
                bootstrap = bootstrap.replace('test -b "$seed_device"',
                                              'test -d "$seed_device"')
                script = root / "bootstrap.sh"
                script.write_text(bootstrap)
                env = dict(os.environ, PATH=str(bin_dir) + os.pathsep + os.environ["PATH"])
                result = subprocess.run(["sh", str(script)], env=env, capture_output=True,
                                        text=True, check=False)
                if omit_avatar:
                    self.assertNotEqual(0, result.returncode)
                    self.assertFalse(marker.exists())
                else:
                    self.assertEqual(23, result.returncode,
                                     (log / "bootstrap.log").read_text())
                    self.assertFalse(marker.exists())
                    options.write_text("rw\n")
                    retry = subprocess.run(["sh", str(script)], env=env,
                                           capture_output=True, text=True, check=False)
                    self.assertNotEqual(0, retry.returncode)
                    self.assertFalse(marker.exists())
                    options.write_text("ro\n")
                    source.write_text(str(root / "other-seed") + "\n")
                    retry = subprocess.run(["sh", str(script)], env=env,
                                           capture_output=True, text=True, check=False)
                    self.assertNotEqual(0, retry.returncode)
                    self.assertFalse(marker.exists())
                    source.write_text(str(seed) + "\n")
                    retry = subprocess.run(["sh", str(script)], env=env,
                                           capture_output=True, text=True, check=False)
                    self.assertEqual(0, retry.returncode,
                                     (log / "bootstrap.log").read_text())
                    self.assertEqual(b"selected wallpaper", (opt / "IGLOO-WALLPAPER.PNG").read_bytes())
                    self.assertEqual(b"selected avatar", (opt / "igloo-avatar.jpg").read_bytes())
                    self.assertEqual(0o755, opt.stat().st_mode & 0o777)
                    self.assertEqual(0o755, opt_parent.stat().st_mode & 0o777)
                    self.assertEqual(0o644, (opt / "igloo_boot.py").stat().st_mode & 0o777)
                    self.assertEqual(0o700, state.stat().st_mode & 0o777)
                    self.assertTrue(marker.exists())
                    self.assertEqual(1, len(mount_log.read_text().splitlines()),
                                     "Same-session retries must not stack OEMDRV mounts")
                    self.assertEqual(2, len(verification_log.read_text().splitlines()),
                                     "Both agent passes must validate the pinned seed")


if __name__ == "__main__":
    unittest.main()
