"""Fedora's first boot moves the folders the installer copied into the folders of the session's language."""

import importlib.util
import sys
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest import TestCase, main, mock

REPOSITORY = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("fedora_folders_agent", REPOSITORY / "distros/fedora-kde/agent/agent.py")
AGENT = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = AGENT
SPEC.loader.exec_module(AGENT)

# What xdg-user-dirs-update writes for nl_BE, the locale of the r31 run.
DUTCH = ('XDG_DESKTOP_DIR="$HOME/Bureaublad"\nXDG_DOWNLOAD_DIR="$HOME/Downloads"\n'
         'XDG_TEMPLATES_DIR="$HOME/Sjablonen"\nXDG_PUBLICSHARE_DIR="$HOME/Openbaar"\n'
         'XDG_DOCUMENTS_DIR="$HOME/Documenten"\nXDG_MUSIC_DIR="$HOME/Muziek"\n'
         'XDG_PICTURES_DIR="$HOME/Afbeeldingen"\nXDG_VIDEOS_DIR="$HOME/Video\'s"\n')
MANIFEST = {"user": {"preferredLinuxUsername": "gilles"},
            "files": {"folders": [{"name": name} for name in ("Documents", "Downloads", "Pictures", "Desktop")]}}


class FedoraSessionFoldersTest(TestCase):
    def home(self, root, listing=DUTCH, make_session_folders=True):
        """A home as the installer leaves it: the selected folders under their Windows names."""
        home = root / "home" / "gilles"
        for path, text in (("Documents/report.txt", "report"), ("Documents/sub/deep.txt", "deep"),
                           ("Downloads/setup.exe", "setup"), ("Pictures/me.jpg", "me"),
                           ("Desktop/Opera-browser.lnk", "link")):
            (home / path).parent.mkdir(parents=True, exist_ok=True)
            (home / path).write_text(text, encoding="utf-8")
        (home / ".config").mkdir()
        if listing is not None:
            (home / ".config/user-dirs.dirs").write_text(listing, encoding="utf-8")
            if make_session_folders:
                for name in ("Bureaublad", "Documenten", "Afbeeldingen", "Muziek", "Video's"):
                    (home / name).mkdir()
        return home

    def adopt(self, root, manifest=MANIFEST):
        commands = []

        def run_cmd(argv, **kwargs):
            commands.append(argv)
            return SimpleNamespace(returncode=0, stdout="", stderr="")

        real_path = AGENT.Path
        with mock.patch.object(AGENT, "run_cmd", side_effect=run_cmd), \
                mock.patch.object(AGENT, "Path", side_effect=lambda *parts: real_path(
                    str(root / "home") if parts == ("/home",) else real_path(*parts))), \
                mock.patch.object(AGENT.shutil, "which", return_value=None):
            AGENT.adopt_session_folder_names(manifest)
        return commands

    def files(self, home):
        return sorted(path.relative_to(home).as_posix() for path in home.rglob("*")
                      if path.is_file() and ".config" not in path.parts)

    def test_every_file_ends_up_in_the_folder_of_the_sessions_language(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = self.home(root)
            commands = self.adopt(root)

            self.assertEqual(self.files(home), ["Afbeeldingen/me.jpg", "Bureaublad/Opera-browser.lnk",
                                                "Documenten/report.txt", "Documenten/sub/deep.txt",
                                                "Downloads/setup.exe"])
            # The Windows names are gone where the session uses another one; Downloads has the same name in both.
            for name in ("Documents", "Pictures", "Desktop"):
                self.assertFalse((home / name).exists(), name)
            owned = [argv for argv in commands if argv[:2] == ["chown", "-R"]]
            self.assertEqual(len(owned), 1)
            self.assertEqual(owned[0][2], "gilles:gilles")
            self.assertEqual(sorted(Path(item).name for item in owned[0][3:]),
                             ["Afbeeldingen", "Bureaublad", "Documenten"])

    def test_a_folder_the_session_did_not_make_yet_is_created(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = self.home(root, make_session_folders=False)
            self.adopt(root)
            self.assertEqual((home / "Documenten/sub/deep.txt").read_text(encoding="utf-8"), "deep")
            self.assertFalse((home / "Documents").exists())

    def test_an_entry_the_session_folder_already_has_stays_in_both_places(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = self.home(root)
            (home / "Documenten/report.txt").write_text("made at the first sign-in", encoding="utf-8")
            (home / "Documenten/sub").mkdir()
            self.adopt(root)

            self.assertEqual((home / "Documenten/report.txt").read_text(encoding="utf-8"), "made at the first sign-in")
            self.assertEqual((home / "Documents/report.txt").read_text(encoding="utf-8"), "report")
            # A folder of the same name is merged; only the clash itself stays behind.
            self.assertEqual((home / "Documenten/sub/deep.txt").read_text(encoding="utf-8"), "deep")
            self.assertEqual(sorted(path.name for path in (home / "Documents").iterdir()), ["report.txt"])

    def test_a_second_pass_changes_nothing(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = self.home(root)
            self.adopt(root)
            first = self.files(home)
            self.adopt(root)
            self.assertEqual(self.files(home), first)

    def test_an_english_session_keeps_the_names_and_moves_nothing(self):
        english = DUTCH
        for dutch, name in (("Bureaublad", "Desktop"), ("Documenten", "Documents"), ("Afbeeldingen", "Pictures"),
                            ("Muziek", "Music"), ("Video's", "Videos")):
            english = english.replace(dutch, name)
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            home = self.home(root, listing=english, make_session_folders=False)
            before = self.files(home)
            commands = self.adopt(root)
            self.assertEqual(self.files(home), before)
            self.assertFalse([argv for argv in commands if argv[:2] == ["chown", "-R"]])

    def test_an_unsafe_folder_name_stops_the_step(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.home(root)
            unsafe = {"user": MANIFEST["user"], "files": {"folders": [{"name": "../etc"}]}}
            with self.assertRaisesRegex(RuntimeError, "unsafe name"):
                self.adopt(root, unsafe)

    def test_the_wallpaper_hook_names_the_sessions_pictures_folder(self):
        source = (REPOSITORY / "distros/fedora-kde/agent/agent.py").read_text(encoding="utf-8")
        self.assertIn('pictures = _session_folders(home, username).get("Pictures", home / "Pictures")', source)
        self.assertIn('WALLPAPER="$HOME"/{shlex.quote(dst.relative_to(home).as_posix())}', source)
        self.assertNotIn('"$HOME/Pictures/', source)
        steps = source[source.index('("display-layout",'):source.index('("account-picture",')]
        self.assertLess(steps.index('("session-folders",'), steps.index('("wallpaper",'))


if __name__ == "__main__":
    main()
