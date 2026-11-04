"""Run the Fedora Kickstart user-data copy blocks against FAT-like modes."""

import os
from pathlib import Path
import stat
import subprocess
import tempfile
import unittest


TEMPLATE = (Path(__file__).resolve().parents[2]
            / "distros/fedora-kde/kickstart/ks.cfg.template")


TEMPLATE_TEXT = TEMPLATE.read_text(encoding="utf-8")


def helper_block() -> str:
    start = TEMPLATE_TEXT.index("prepare_private_user_copy_target() {")
    end = TEMPLATE_TEXT.index("\n}\n", start) + len("\n}\n")
    return TEMPLATE_TEXT[start:end]


def copy_block() -> str:
    template = TEMPLATE_TEXT
    start = template.index("    while IFS='|' read -r brel bdst; do")
    end = template.index("\nSTAGED_BROWSERMAP\n", start) + len("\nSTAGED_BROWSERMAP\n")
    return template[start:end].replace(
        "{{STAGED_BROWSER_MAP}}",
        "browser-profiles/Firefox|.mozilla/firefox")


def user_folder_block() -> str:
    start = TEMPLATE_TEXT.index("        while IFS= read -r folder_name; do")
    end = TEMPLATE_TEXT.index("\nSTAGEDFOLDERS\n", start) + len("\nSTAGEDFOLDERS\n")
    return TEMPLATE_TEXT[start:end].replace("{{STAGED_FOLDER_NAMES}}", "Documents")


class FedoraGeckoPrivateModesTests(unittest.TestCase):
    def run_copy(self, seed: Path, home: Path, *, user_files: bool = False) -> subprocess.CompletedProcess[str]:
        env = os.environ.copy()
        env.update(OEMDRV=str(seed), USER_HOME=str(home))
        script = ("set -euo pipefail\nlog() { :; }\n" + helper_block() +
                  (user_folder_block() if user_files else copy_block()))
        return subprocess.run(["bash", "-c", script],
                              env=env, capture_output=True, text=True, check=False)

    def test_fat_like_profile_becomes_private_before_user_login(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            seed = root / "seed"
            profile = seed / "browser-profiles/Firefox"
            nested = profile / "bookmarkbackups"
            nested.mkdir(parents=True)
            (profile / "key4.db").write_bytes(b"synthetic-key")
            (nested / "bookmark.json").write_bytes(b"synthetic-bookmark")
            profile.chmod(0o777)
            nested.chmod(0o777)
            (profile / "key4.db").chmod(0o666)
            (nested / "bookmark.json").chmod(0o666)

            home = root / "home"
            home.mkdir()
            result = self.run_copy(seed, home)
            self.assertEqual(0, result.returncode, result.stderr)
            installed = home / ".mozilla/firefox"
            self.assertEqual(0o700, stat.S_IMODE(installed.stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE(
                (installed / "bookmarkbackups").stat().st_mode))
            self.assertEqual(0o600, stat.S_IMODE((installed / "key4.db").stat().st_mode))
            self.assertEqual(0o600, stat.S_IMODE(
                (installed / "bookmarkbackups/bookmark.json").stat().st_mode))
            self.assertEqual(b"synthetic-key", (installed / "key4.db").read_bytes())

    def test_redirected_destination_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            profile = root / "seed/browser-profiles/Firefox"
            profile.mkdir(parents=True)
            (profile / "key4.db").write_bytes(b"synthetic-key")
            home = root / "home"
            (home / ".mozilla").mkdir(parents=True)
            redirected = root / "redirected"
            redirected.mkdir()
            (home / ".mozilla/firefox").symlink_to(redirected, target_is_directory=True)

            result = self.run_copy(root / "seed", home)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((redirected / "key4.db").exists())

    def test_nested_redirected_gecko_destination_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            profile = root / "seed/browser-profiles/Firefox"
            (profile / "bookmarkbackups").mkdir(parents=True)
            (profile / "bookmarkbackups/bookmark.json").write_bytes(b"incoming")
            home = root / "home"
            dst = home / ".mozilla/firefox"
            dst.mkdir(parents=True)
            redirected = root / "redirected"
            redirected.mkdir()
            (redirected / "existing.txt").write_bytes(b"keep")
            (dst / "bookmarkbackups").symlink_to(redirected, target_is_directory=True)

            result = self.run_copy(root / "seed", home)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((redirected / "bookmark.json").exists())
            self.assertEqual(b"keep", (redirected / "existing.txt").read_bytes())

    def test_redirected_gecko_ancestor_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            profile = root / "seed/browser-profiles/Firefox"
            profile.mkdir(parents=True)
            (profile / "key4.db").write_bytes(b"incoming")
            home = root / "home"
            home.mkdir()
            redirected = root / "redirected"
            redirected.mkdir()
            (home / ".mozilla").symlink_to(redirected, target_is_directory=True)

            result = self.run_copy(root / "seed", home)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((redirected / "firefox/key4.db").exists())

    def test_fat_like_selected_user_folder_becomes_private_and_keeps_existing_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder = root / "seed/files/Documents"
            nested = folder / "Invoices"
            nested.mkdir(parents=True)
            (nested / "new.txt").write_bytes(b"synthetic-private-data")
            folder.chmod(0o777)
            nested.chmod(0o777)
            (nested / "new.txt").chmod(0o666)
            home = root / "home"
            dst = home / "Documents"
            dst.mkdir(parents=True)
            (dst / "existing.txt").write_bytes(b"keep-existing")
            (dst / "existing.sh").write_bytes(b"#!/bin/sh\nexit 0\n")
            (dst / "existing.sh").chmod(0o755)

            result = self.run_copy(root / "seed", home, user_files=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(b"keep-existing", (dst / "existing.txt").read_bytes())
            self.assertEqual(b"synthetic-private-data", (dst / "Invoices/new.txt").read_bytes())
            for path in (dst, dst / "Invoices"):
                self.assertEqual(0o700, stat.S_IMODE(path.stat().st_mode))
            for path in (dst / "existing.txt", dst / "Invoices/new.txt"):
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE((dst / "existing.sh").stat().st_mode))

    def test_nested_redirected_user_folder_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            nested = root / "seed/files/Documents/Invoices"
            nested.mkdir(parents=True)
            (nested / "new.txt").write_bytes(b"incoming")
            home = root / "home"
            dst = home / "Documents"
            dst.mkdir(parents=True)
            redirected = root / "redirected"
            redirected.mkdir()
            (dst / "Invoices").symlink_to(redirected, target_is_directory=True)

            result = self.run_copy(root / "seed", home, user_files=True)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((redirected / "new.txt").exists())


if __name__ == "__main__":
    unittest.main()
