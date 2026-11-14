"""Migrated folders and the wallpaper land where the desktop session keeps them."""

from contextlib import contextmanager
import getpass
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


SOURCE = (Path(__file__).resolve().parents[2]
          / 'distros' / '_debian-family' / 'agent' / 'agent.py')
SPEC = importlib.util.spec_from_file_location('igloo_debian_session_folders', SOURCE)
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)

REAL_RUN = agent.run_cmd
ME = getpass.getuser()
DUTCH = ('XDG_DESKTOP_DIR="$HOME/Bureaublad"\n'
         'XDG_DOWNLOAD_DIR="$HOME/Downloads"\n'
         'XDG_TEMPLATES_DIR="$HOME/Sjablonen"\n'
         'XDG_DOCUMENTS_DIR="$HOME/Documenten"\n'
         'XDG_PICTURES_DIR="$HOME/Afbeeldingen"\n')


@contextmanager
def lab(root, listing=None, tool=True):
    """A fake home, system locale files and a scripted folder tool."""
    home = root / 'home' / ME
    for path in (home, root / 'etc/default', root / 'opt/igloo'):
        path.mkdir(parents=True)
    (root / 'etc/default/locale').write_text('LANG="nl_BE.UTF-8"\nLANGUAGE="nl_BE:nl"\n')
    commands = []

    def lab_path(value):
        text = str(value)
        if not text.startswith('/') or text.startswith(str(root)):
            return Path(text)
        for original in ('/etc', '/opt/igloo', '/home'):
            if text == original or text.startswith(original + '/'):
                return root / text.lstrip('/')
        raise AssertionError('Unexpected real filesystem path: ' + text)

    def run_cmd(cmd, **options):
        commands.append((cmd, options.get('env') or {}))
        if cmd == ['xdg-user-dirs-update'] and listing is not None:
            config = Path(options['env']['XDG_CONFIG_HOME'])
            config.mkdir(parents=True, exist_ok=True)
            (config / 'user-dirs.dirs').write_text(listing)
            (config / 'user-dirs.locale').write_text('nl_BE')
        return subprocess.CompletedProcess(cmd, 0)

    with (patch.object(agent, 'Path', side_effect=lab_path),
          patch.object(agent, 'run_cmd', side_effect=run_cmd),
          patch.object(agent.shutil, 'which',
                       side_effect=lambda name: '/usr/bin/' + name if tool else None)):
        yield home, commands


class SessionFolderTests(unittest.TestCase):
    def test_the_tool_runs_as_the_session_would_and_its_answer_is_used(self):
        with TemporaryDirectory(prefix='igloo-folders-') as temporary:
            with lab(Path(temporary), DUTCH) as (home, commands):
                folders = agent._session_folders(home, ME)
            self.assertEqual({'Desktop': home / 'Bureaublad', 'Downloads': home / 'Downloads',
                              'Documents': home / 'Documenten', 'Pictures': home / 'Afbeeldingen'},
                             folders)
            tool, env = commands[0]
            self.assertEqual(['xdg-user-dirs-update'], tool)
            self.assertEqual({'LANG': 'nl_BE.UTF-8', 'LANGUAGE': 'nl_BE:nl', 'LC_ALL': '',
                              'HOME': str(home), 'XDG_CONFIG_HOME': str(home / '.config')}, env)
            owner, _ = commands[1]
            self.assertEqual(['chown', '-h', f'{ME}:{ME}'], owner[:3])
            self.assertIn(str(home / '.config/user-dirs.dirs'), owner)

    def test_an_existing_list_is_read_and_the_tool_is_left_alone(self):
        with TemporaryDirectory(prefix='igloo-folders-') as temporary:
            with lab(Path(temporary)) as (home, commands):
                (home / '.config').mkdir()
                (home / '.config/user-dirs.dirs').write_text('XDG_PICTURES_DIR="$HOME/Bilder"\n')
                folders = agent._session_folders(home, ME)
            self.assertEqual({'Pictures': home / 'Bilder'}, folders)
            self.assertNotIn(['xdg-user-dirs-update'], [cmd for cmd, _ in commands])

    def test_entries_that_leave_the_home_are_ignored(self):
        listing = ('XDG_DOCUMENTS_DIR="$HOME/../etc"\n'
                   'XDG_PICTURES_DIR="/srv/pictures"\n'
                   'XDG_DESKTOP_DIR="$HOME/"\n'
                   'XDG_MUSIC_DIR="$HOME/./Muziek"\n'
                   'XDG_VIDEOS_DIR="$HOME/Video\'s"\n')
        with TemporaryDirectory(prefix='igloo-folders-') as temporary:
            with lab(Path(temporary), listing) as (home, _):
                folders = agent._session_folders(home, ME)
            self.assertEqual({'Videos': home / "Video's"}, folders)

    def test_without_the_tool_the_windows_names_stay(self):
        with TemporaryDirectory(prefix='igloo-folders-') as temporary:
            with lab(Path(temporary), DUTCH, tool=False) as (home, commands):
                self.assertEqual({}, agent._session_folders(home, ME))
            self.assertEqual([], commands)

    def test_a_selected_folder_is_copied_into_the_session_folder(self):
        with TemporaryDirectory(prefix='igloo-folders-') as temporary:
            root = Path(temporary)
            staged = root / 'seed/files'
            (staged / 'Documents').mkdir(parents=True)
            (staged / 'Documents/brief.txt').write_text('tekst')
            (staged / 'Music').mkdir()
            with lab(root, DUTCH) as (home, _), \
                    patch.object(agent, '_STAGED_FILES_DIR', staged), \
                    patch.object(agent, '_find_windows_home') as ntfs:
                for name in ('ensure_matching_firefox', 'relocate_gecko_profiles',
                             'normalise_gecko_profiles'):
                    patch.object(agent, name).start()
                self.addCleanup(patch.stopall)
                agent.migrate_user_files({
                    'user': {'preferredLinuxUsername': ME, 'windowsUsername': 'Windows'},
                    'files': {'folders': [{'name': 'Documents'}, {'name': 'Music'}]}})
                ntfs.assert_not_called()
            self.assertEqual('tekst', (home / 'Documenten/brief.txt').read_text())
            self.assertFalse((home / 'Documents').exists())
            # The list has no music folder, so that one keeps its Windows name.
            self.assertTrue((home / 'Music').is_dir())

    def test_the_wallpaper_goes_to_the_session_pictures_folder(self):
        listing = 'XDG_PICTURES_DIR="$HOME/Imágenes de \'casa\'"\n'
        with TemporaryDirectory(prefix='igloo-folders-') as temporary:
            root = Path(temporary)
            with lab(root, listing) as (home, _):
                (root / 'opt/igloo/igloo-wallpaper.jpg').write_bytes(b'image')
                agent.migrate_wallpaper({'user': {'preferredLinuxUsername': ME},
                                         'wallpaper': {'fileName': 'igloo-wallpaper.jpg'}})
                keyfile = (root / 'etc/dconf/db/local.d/00-igloo-wallpaper').read_text()
            picture = home / "Imágenes de 'casa'" / 'wallpaper.jpg'
            self.assertEqual(b'image', picture.read_bytes())
            self.assertIn(f"picture-uri='{picture.as_uri()}'\n", keyfile)
            self.assertIn('Im%C3%A1genes%20de%20%27casa%27', keyfile)


@unittest.skipUnless(shutil.which('xdg-user-dirs-update'), 'needs xdg-user-dirs')
class InstalledToolTests(unittest.TestCase):
    def test_the_installed_tool_writes_a_list_the_agent_understands(self):
        with TemporaryDirectory(prefix='igloo-folders-') as temporary:
            home = Path(temporary) / 'home'
            home.mkdir()
            with patch.object(agent, '_system_locale', return_value={'LANG': 'C.UTF-8'}), \
                    patch.object(agent, 'run_cmd', side_effect=REAL_RUN):
                folders = agent._session_folders(home, ME)
            self.assertEqual(set(agent._XDG_FOLDER_KEYS), set(folders))
            for name, path in folders.items():
                with self.subTest(folder=name):
                    self.assertTrue(path.is_dir())
                    self.assertEqual(home, path.parent)
            self.assertTrue((home / '.config/user-dirs.locale').is_file())


if __name__ == '__main__':
    unittest.main()
