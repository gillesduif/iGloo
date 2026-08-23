#!/usr/bin/env python3
"""Ground-truth test for the Chromium profile copy in the first-boot agent.

Ground truth: the Debian run of 2026-08-21, where Brave came up completely
empty. Two separate causes, both covered here.

  1. Only passwords were ever migrated. Bookmarks, history and cookies are not
     protected by the DPAPI master key at all - Bookmarks is plain JSON and
     History is a plain SQLite file - so there was never a reason not to copy
     them. Verified against the reporter's own Brave profile: 93 readable urls
     in History, readable bookmark titles, 94 cookies whose encrypted_value
     starts with "v10" while the plain "value" column is empty.

  2. The passwords that did migrate landed in a database we built ourselves and
     labelled schema version 8 while it carried columns Chromium added at 19,
     25, 26 and 30. See the chromium-crypto harness for that half.

The cookie jar is copied verbatim rather than rebuilt, so its schema is a real
Chromium's; only the values are rewritten, by the login hook.
"""
import importlib.util
import sqlite3
import sys
import types
from pathlib import Path

MOD_PATH = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/agent.py"
spec = importlib.util.spec_from_file_location("ag", MOD_PATH)
ag = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ag)

BRAVE_ID = "com.brave.Browser"
BRAVE_REL = "BraveSoftware/Brave-Browser"

failures: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        print(f"  PASS  {name}")
    else:
        failures.append(name)
        print(f"  FAIL  {name}  {detail}")


class Stub:
    """Pins which browser builds exist, so the destination is predictable."""

    def __init__(self, flatpak: bool = True, native: bool = False) -> None:
        self.flatpak, self.native = flatpak, native

    def __enter__(self):
        self.saved = {n: getattr(ag, n) for n in ("shutil", "subprocess")}
        real_shutil = self.saved["shutil"]
        which = real_shutil.which

        shim = types.SimpleNamespace(
            copyfile=real_shutil.copyfile,
            copytree=real_shutil.copytree,
            rmtree=real_shutil.rmtree,
            which=lambda n: ("/usr/bin/flatpak" if n == "flatpak" and self.flatpak
                             else "/usr/bin/" + n if n.startswith("brave") and self.native
                             else None),
        )
        ag.shutil = shim
        ag.subprocess = types.SimpleNamespace(
            run=lambda *a, **k: types.SimpleNamespace(
                returncode=0 if self.flatpak else 1, stdout="", stderr=""))
        self._real_which = which
        return self

    def __exit__(self, *exc) -> None:
        for name, value in self.saved.items():
            setattr(ag, name, value)


def make_windows_profile(tmp: Path) -> Path:
    """The files a real Brave profile has, in the layout Windows uses."""
    root = tmp / "win" / "User Data"
    default = root / "Default"
    (default / "Network").mkdir(parents=True)
    (default / "Bookmarks").write_text('{"roots":{"bookmark_bar":{}}}', encoding="utf-8")
    (default / "Top Sites").write_bytes(b"sqlite-ish")
    (default / "Favicons").write_bytes(b"sqlite-ish")
    (default / "Sessions").mkdir()
    (default / "Sessions" / "Session_13431778520312982").write_bytes(b"session")
    (default / "Sessions" / "Tabs_13431778520454243").write_bytes(b"tabs")

    con = sqlite3.connect(default / "History")
    con.execute("CREATE TABLE urls (url TEXT, title TEXT)")
    con.execute("INSERT INTO urls VALUES ('https://example.com', 'Example')")
    con.commit()
    con.close()

    con = sqlite3.connect(default / "Network" / "Cookies")
    con.execute("CREATE TABLE cookies (host_key TEXT, name TEXT, path TEXT, "
                "encrypted_value BLOB)")
    con.execute("INSERT INTO cookies VALUES ('.example.com', 's', '/', ?)",
                (b"v10windows",))
    con.commit()
    con.close()

    # Never copied: machine state, and the master key in it is useless on Linux.
    (root / "Local State").write_text("{}", encoding="utf-8")
    (root / "Profile 1").mkdir()
    (root / "Profile 1" / "Bookmarks").write_text("{}", encoding="utf-8")
    return root


def test_flatpak_destination(tmp: Path) -> None:
    print("a Flatpak Brave gets the files in its private config home")
    src = make_windows_profile(tmp)
    with Stub(flatpak=True):
        ag._copy_chromium_profile("Brave", src, tmp)

    dst = tmp / ".var" / "app" / BRAVE_ID / "config" / BRAVE_REL / "Default"
    check("bookmarks copied", (dst / "Bookmarks").is_file())
    check("history copied", (dst / "History").is_file())
    check("favicons copied", (dst / "Favicons").is_file())
    check("top sites copied", (dst / "Top Sites").is_file())
    check("cookie jar copied into Network/", (dst / "Network" / "Cookies").is_file())
    check("open tabs copied", (dst / "Sessions" / "Tabs_13431778520454243").is_file())
    check("the whole Sessions directory came",
          sorted(p.name for p in (dst / "Sessions").iterdir())
          == ["Session_13431778520312982", "Tabs_13431778520454243"])
    check("nothing landed in ~/.config", not (tmp / ".config").exists())


def test_history_survives_the_copy(tmp: Path) -> None:
    print("the copied History is still a readable database")
    src = make_windows_profile(tmp)
    with Stub(flatpak=True):
        ag._copy_chromium_profile("Brave", src, tmp)
    dst = tmp / ".var" / "app" / BRAVE_ID / "config" / BRAVE_REL / "Default"
    con = sqlite3.connect(dst / "History")
    rows = list(con.execute("SELECT url, title FROM urls"))
    con.close()
    check("the row came across", rows == [("https://example.com", "Example")], str(rows))


def test_machine_state_is_left_behind(tmp: Path) -> None:
    print("Local State and secondary profiles stay on Windows")
    src = make_windows_profile(tmp)
    with Stub(flatpak=True):
        ag._copy_chromium_profile("Brave", src, tmp)
    root = tmp / ".var" / "app" / BRAVE_ID / "config" / BRAVE_REL
    check("no Local State", not (root / "Local State").exists())
    check("no Profile 1", not (root / "Profile 1").exists())


def test_native_destination(tmp: Path) -> None:
    print("a packaged Brave gets them in ~/.config")
    src = make_windows_profile(tmp)
    with Stub(flatpak=False, native=True):
        ag._copy_chromium_profile("Brave", src, tmp)
    dst = tmp / ".config" / BRAVE_REL / "Default"
    check("bookmarks copied", (dst / "Bookmarks").is_file())
    check("no Flatpak home created", not (tmp / ".var").exists())


def test_unmapped_browser_is_skipped(tmp: Path) -> None:
    print("a browser iGloo has no mapping for")
    src = make_windows_profile(tmp)
    with Stub(flatpak=True):
        ag._copy_chromium_profile("Some Other Browser", src, tmp)
    check("nothing written", not (tmp / ".var").exists() and not (tmp / ".config").exists())


def test_missing_default_profile(tmp: Path) -> None:
    print("a user-data root with no Default directory")
    root = tmp / "win" / "User Data"
    root.mkdir(parents=True)
    with Stub(flatpak=True):
        ag._copy_chromium_profile("Brave", root, tmp)
    check("nothing written", not (tmp / ".var").exists())


#   Fedora: the installer stages, the agent places

def stage(tmp: Path, name: str) -> Path:
    """What the Fedora kickstart leaves behind in /var/lib/igloo/chromium."""
    staged = tmp / "stage" / name
    (staged / "Network").mkdir(parents=True)
    (staged / "Bookmarks").write_text('{"roots":{}}', encoding="utf-8")
    (staged / "History").write_bytes(b"sqlite-ish")
    (staged / "Network" / "Cookies").write_bytes(b"sqlite-ish")
    (staged / "Sessions").mkdir()
    (staged / "Sessions" / "Tabs_1").write_bytes(b"tabs")
    return staged


def test_staged_files_are_placed_and_cleared(tmp: Path) -> None:
    print("Fedora: staged files land in the profile and the staging area goes")
    stage(tmp, "Brave")
    saved = ag._CHROMIUM_STAGE_DIR
    ag._CHROMIUM_STAGE_DIR = tmp / "stage"
    try:
        with Stub(flatpak=True):
            ag.place_staged_chromium_profiles(tmp)
    finally:
        ag._CHROMIUM_STAGE_DIR = saved

    dst = tmp / ".var" / "app" / BRAVE_ID / "config" / BRAVE_REL / "Default"
    check("bookmarks placed", (dst / "Bookmarks").is_file())
    check("cookie jar placed", (dst / "Network" / "Cookies").is_file())
    check("open tabs placed", (dst / "Sessions" / "Tabs_1").is_file())
    check("the staging directory is cleared",
          not (tmp / "stage" / "Brave").exists())


def test_staging_absent_is_a_no_op(tmp: Path) -> None:
    print("Debian: nothing staged, because its agent copies directly")
    saved = ag._CHROMIUM_STAGE_DIR
    ag._CHROMIUM_STAGE_DIR = tmp / "never-created"
    try:
        with Stub(flatpak=True):
            ag.place_staged_chromium_profiles(tmp)
    finally:
        ag._CHROMIUM_STAGE_DIR = saved
    check("nothing written", not (tmp / ".var").exists())


def test_staged_unmapped_browser_is_left_alone(tmp: Path) -> None:
    print("a staged browser iGloo has no mapping for")
    stage(tmp, "Some Other Browser")
    saved = ag._CHROMIUM_STAGE_DIR
    ag._CHROMIUM_STAGE_DIR = tmp / "stage"
    try:
        with Stub(flatpak=True):
            ag.place_staged_chromium_profiles(tmp)
    finally:
        ag._CHROMIUM_STAGE_DIR = saved
    check("nothing written", not (tmp / ".var").exists())


if __name__ == "__main__":
    import tempfile
    for test in (test_flatpak_destination, test_history_survives_the_copy,
                 test_machine_state_is_left_behind, test_native_destination,
                 test_unmapped_browser_is_skipped, test_missing_default_profile,
                 test_staged_files_are_placed_and_cleared,
                 test_staging_absent_is_a_no_op,
                 test_staged_unmapped_browser_is_left_alone):
        with tempfile.TemporaryDirectory(prefix="chromium-copy-test-") as d:
            test(Path(d))
    print()
    if failures:
        print(f"{len(failures)} FAILED: {', '.join(failures)}")
        sys.exit(1)
    print("all checks passed")
