#!/usr/bin/env python3
"""Ground-truth test for normalise_gecko_profiles() in the first-boot agents.

Ground truth: the Debian 13 run of 2026-08-19 (igloo-logs-desktop-living), where
the agent copied AppData/Roaming/Mozilla/Firefox to ~/.mozilla/firefox correctly
(580 MB, bootstrap.log line 949) and Firefox still opened with no data at all.

The copied profiles.ini was:
    [Install308046B0AF4A39CB]      <- hashed from C:\\Program Files\\Mozilla Firefox
    Default=Profiles/vjncdvaw.default-release   <- 580 MB, the real profile
    [Profile1] Path=Profiles/i2p1dypv.default   <- 1 KB, empty
    Default=1                                   <- the legacy fallback

No Linux build hashes to 308046B0AF4A39CB, so Firefox ignored the install
section and fell back to Default=1: the empty profile. Passwords, bookmarks and
history were on disk the whole time, in a profile nothing ever opened.
"""
import importlib.util
import types
import sys
from pathlib import Path

MOD_PATH = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/agent.py"
spec = importlib.util.spec_from_file_location("ag", MOD_PATH)
ag = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ag)

# Verbatim from C:\Users\<user>\AppData\Roaming\Mozilla\Firefox\profiles.ini.
REAL_WINDOWS_INI = """[Install308046B0AF4A39CB]
Default=Profiles/vjncdvaw.default-release
Locked=1

[Profile1]
Name=default
IsRelative=1
Path=Profiles/i2p1dypv.default
Default=1

[Profile0]
Name=default-release
IsRelative=1
Path=Profiles/vjncdvaw.default-release

[General]
StartWithLastProfile=1
Version=2

[BackgroundTasksProfiles]
MozillaBackgroundTask-308046B0AF4A39CB-defaultagent=pneykqdj.MozillaBackgroundTask-308046B0AF4A39CB-defaultagent
"""

failures: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        print(f"  PASS  {name}")
    else:
        failures.append(name)
        print(f"  FAIL  {name}  {detail}")


# Verbatim compatibility.ini from the migrated profile, and the native Linux form.
WINDOWS_STAMP = ("[Compatibility]\n"
                 "LastVersion=153.0.4_20260810162159/20260810162159\n"
                 "LastOSABI=WINNT_x86_64-msvc\n"
                 "LastPlatformDir=C:\\Program Files\\Mozilla Firefox\n"
                 "LastAppDir=C:\\Program Files\\Mozilla Firefox\\browser\n")
LINUX_STAMP = ("[Compatibility]\n"
               "LastVersion=140.3.0esr_20260805120000/20260805120000\n"
               "LastOSABI=Linux_x86_64-gcc3\n"
               "LastPlatformDir=/usr/lib/firefox-esr\n")


def build(tmp: Path, ini_text: str, profiles: tuple[str, ...],
          stamp: str = WINDOWS_STAMP) -> Path:
    root = tmp / ".mozilla" / "firefox"
    root.mkdir(parents=True)
    (root / "profiles.ini").write_text(ini_text, encoding="utf-8")
    for rel in profiles:
        d = root / rel
        d.mkdir(parents=True)
        (d / "compatibility.ini").write_text(stamp, encoding="utf-8")
    return root


def section_of(text: str, header: str) -> list[str]:
    lines, out, inside = text.splitlines(), [], False
    for line in lines:
        if line.strip().startswith("["):
            inside = line.strip() == header
            continue
        if inside and line.strip():
            out.append(line.strip())
    return out


def test_real_windows_profile(tmp: Path) -> None:
    print("real Windows profiles.ini")
    root = build(tmp, REAL_WINDOWS_INI,
                 ("Profiles/i2p1dypv.default", "Profiles/vjncdvaw.default-release"))
    ag.normalise_gecko_profiles(tmp)
    text = (root / "profiles.ini").read_text(encoding="utf-8")

    check("the 580 MB profile becomes the default",
          "Default=1" in section_of(text, "[Profile0]"), text)
    check("the empty profile loses its default flag",
          "Default=1" not in section_of(text, "[Profile1]"), text)
    check("the Windows-path install section is gone",
          "Install308046B0AF4A39CB" not in text, text)
    check("the Windows-only background task section is gone",
          "BackgroundTasksProfiles" not in text, text)
    check("[General] survives untouched",
          section_of(text, "[General]") == ["StartWithLastProfile=1", "Version=2"], text)
    check("both profiles are still listed",
          text.count("IsRelative=1") == 2, text)
    # Firefox only adopts a profile whose compatibility.ini exists and names no
    # foreign install directory. Deleting the file is what made it start fresh.
    stamp = root / "Profiles" / "vjncdvaw.default-release" / "compatibility.ini"
    check("compatibility.ini survives", stamp.is_file())
    text = stamp.read_text(encoding="utf-8")
    check("the Windows install paths are gone",
          "LastPlatformDir" not in text and "LastAppDir" not in text, text)
    check("the version and ABI are kept",
          "LastVersion=153.0.4" in text and "WINNT" in text, text)


def test_idempotent(tmp: Path) -> None:
    print("running twice changes nothing the second time")
    root = build(tmp, REAL_WINDOWS_INI,
                 ("Profiles/i2p1dypv.default", "Profiles/vjncdvaw.default-release"))
    ag.normalise_gecko_profiles(tmp)
    once = (root / "profiles.ini").read_text(encoding="utf-8")
    ag.normalise_gecko_profiles(tmp)
    twice = (root / "profiles.ini").read_text(encoding="utf-8")
    check("second pass is a no-op", once == twice, twice)


def test_native_linux_ini_untouched(tmp: Path) -> None:
    print("a profiles.ini with no install section")
    native = """[Profile0]
Name=default-release
IsRelative=1
Path=xyz123.default-release
Default=1

[General]
StartWithLastProfile=1
Version=2
"""
    root = build(tmp, native, ("xyz123.default-release",), stamp=LINUX_STAMP)
    ag.normalise_gecko_profiles(tmp)
    check("left byte-for-byte alone",
          (root / "profiles.ini").read_text(encoding="utf-8") == native)
    check("a Linux profile keeps its stamp byte-for-byte",
          (root / "xyz123.default-release" / "compatibility.ini")
          .read_text(encoding="utf-8") == LINUX_STAMP)


def test_backslash_path(tmp: Path) -> None:
    print("install section written with a backslash path")
    ini = """[Install308046B0AF4A39CB]
Default=Profiles\\abc.default-release

[Profile0]
Name=default-release
IsRelative=1
Path=Profiles/abc.default-release

[Profile1]
Name=default
IsRelative=1
Path=Profiles/old.default
Default=1
"""
    root = build(tmp, ini, ("Profiles/abc.default-release", "Profiles/old.default"))
    ag.normalise_gecko_profiles(tmp)
    text = (root / "profiles.ini").read_text(encoding="utf-8")
    check("matched across the separator difference",
          "Default=1" in section_of(text, "[Profile0]"), text)


def test_install_names_a_missing_profile(tmp: Path) -> None:
    print("install section pointing at a profile that was not copied")
    ini = """[Install308046B0AF4A39CB]
Default=Profiles/gone.default-release

[Profile0]
Name=default
IsRelative=1
Path=Profiles/old.default
Default=1
"""
    root = build(tmp, ini, ("Profiles/old.default",))
    ag.normalise_gecko_profiles(tmp)
    check("left alone rather than leaving no default at all",
          (root / "profiles.ini").read_text(encoding="utf-8") == ini)
    check("the Windows paths still go, so whichever profile opens is adoptable",
          "LastPlatformDir" not in (root / "Profiles" / "old.default" /
                                    "compatibility.ini").read_text(encoding="utf-8"))


def test_zen_root(tmp: Path) -> None:
    print("Zen Browser uses the same layout under ~/.zen")
    root = tmp / ".zen"
    root.mkdir(parents=True)
    (root / "profiles.ini").write_text(REAL_WINDOWS_INI, encoding="utf-8")
    (root / "Profiles" / "vjncdvaw.default-release").mkdir(parents=True)
    (root / "Profiles" / "i2p1dypv.default").mkdir(parents=True)
    ag.normalise_gecko_profiles(tmp)
    text = (root / "profiles.ini").read_text(encoding="utf-8")
    check("normalised too", "Default=1" in section_of(text, "[Profile0]"), text)


#   Flatpak placement and the Firefox version match

class Stub:
    """Replaces the agent's flatpak calls, and records what would have been run."""

    def __init__(self, installed: tuple[str, ...] = (), private_home: bool = True,
                 firefox_major: int | None = None) -> None:
        self.installed = set(installed)
        self.private_home = private_home
        self.firefox_major = firefox_major
        self.commands: list[list[str]] = []

    def __enter__(self):
        self.saved = {n: getattr(ag, n) for n in
                      ("_flatpak_installed", "_flatpak_home",
                       "_installed_firefox_major", "run_cmd")}
        ag._flatpak_installed = lambda app_id: app_id in self.installed
        ag._flatpak_home = lambda app_id, home: (
            home / ".var" / "app" / app_id if self.private_home else None)
        ag._installed_firefox_major = lambda: self.firefox_major
        ag.run_cmd = self._run
        return self

    def __exit__(self, *exc) -> None:
        for name, value in self.saved.items():
            setattr(ag, name, value)

    def _run(self, cmd, **kw):
        self.commands.append(list(cmd))
        if "install" in cmd:
            self.installed.add(cmd[-1])
        return types.SimpleNamespace(returncode=0, stdout="", stderr="")


FIREFOX_ID = "org.mozilla.firefox"


def make_firefox_profile(tmp: Path, last_version: str) -> Path:
    root = tmp / ".mozilla" / "firefox"
    (root / "Profiles" / "vjncdvaw.default-release").mkdir(parents=True)
    (root / "profiles.ini").write_text(REAL_WINDOWS_INI, encoding="utf-8")
    (root / "Profiles" / "vjncdvaw.default-release" / "compatibility.ini").write_text(
        f"[Compatibility]\nLastVersion={last_version}\nLastOSABI=WINNT_x86_64-msvc\n",
        encoding="utf-8")
    return root


def test_version_reading(tmp: Path) -> None:
    print("reading versions out of the real strings")
    check("release channel", ag._version_major("153.0.4_20260810162159") == 153)
    check("esr suffix", ag._version_major("Mozilla Firefox 140.3.0esr") == 140)
    check("nothing to read", ag._version_major("Mozilla Firefox") is None)

    root = make_firefox_profile(tmp, "153.0.4_20260810162159/20260810162159")
    check("profile version from compatibility.ini",
          ag._profile_firefox_major(root) == 153)


def test_esr_older_than_profile_installs_flathub_firefox(tmp: Path) -> None:
    print("Debian: ESR 140 against a profile from 153")
    make_firefox_profile(tmp, "153.0.4_20260810162159/20260810162159")
    with Stub(firefox_major=140) as stub:
        ag.ensure_matching_firefox(tmp)
    check("the Flathub build is installed",
          any(c[:2] == ["flatpak", "install"] and c[-1] == FIREFOX_ID
              for c in stub.commands), stub.commands)


def test_release_channel_installs_nothing(tmp: Path) -> None:
    print("Mint/Fedora: release 153 against a profile from 153")
    make_firefox_profile(tmp, "153.0.4_20260810162159/20260810162159")
    with Stub(firefox_major=153) as stub:
        ag.ensure_matching_firefox(tmp)
    check("no second Firefox", stub.commands == [], stub.commands)


def test_unknown_versions_change_nothing(tmp: Path) -> None:
    print("no version to compare")
    make_firefox_profile(tmp, "153.0.4_20260810162159/20260810162159")
    with Stub(firefox_major=None) as stub:
        ag.ensure_matching_firefox(tmp)
    check("left alone rather than guessing", stub.commands == [], stub.commands)


def test_profile_moves_into_the_flatpak_home(tmp: Path) -> None:
    print("the profile follows the browser into its private home")
    make_firefox_profile(tmp, "153.0.4_20260810162159/20260810162159")
    with Stub(installed=(FIREFOX_ID,), private_home=True):
        ag.relocate_gecko_profiles(tmp)
    moved = tmp / ".var" / "app" / FIREFOX_ID / ".mozilla" / "firefox"
    check("moved", moved.is_dir(), str(moved))
    check("nothing left behind", not (tmp / ".mozilla" / "firefox").exists())
    check("the profile came along",
          (moved / "Profiles" / "vjncdvaw.default-release").is_dir())


def test_profile_stays_when_the_flatpak_sees_the_real_home(tmp: Path) -> None:
    print("a Flatpak granted --filesystem=home reads ~/.mozilla itself")
    make_firefox_profile(tmp, "153.0.4_20260810162159/20260810162159")
    with Stub(installed=(FIREFOX_ID,), private_home=False):
        ag.relocate_gecko_profiles(tmp)
    check("left in place", (tmp / ".mozilla" / "firefox").is_dir())
    check("no private home created", not (tmp / ".var").exists())


def test_profile_stays_when_there_is_no_flatpak(tmp: Path) -> None:
    print("a packaged browser keeps the native path")
    make_firefox_profile(tmp, "153.0.4_20260810162159/20260810162159")
    with Stub(installed=()):
        ag.relocate_gecko_profiles(tmp)
    check("left in place", (tmp / ".mozilla" / "firefox").is_dir())


def test_existing_destination_is_not_overwritten(tmp: Path) -> None:
    print("the Flatpak already has a profile of its own")
    make_firefox_profile(tmp, "153.0.4_20260810162159/20260810162159")
    existing = tmp / ".var" / "app" / FIREFOX_ID / ".mozilla" / "firefox"
    existing.mkdir(parents=True)
    (existing / "marker").write_text("keep me", encoding="utf-8")
    with Stub(installed=(FIREFOX_ID,)):
        ag.relocate_gecko_profiles(tmp)
    check("the existing profile survives",
          (existing / "marker").read_text(encoding="utf-8") == "keep me")
    check("the copy is left where it was", (tmp / ".mozilla" / "firefox").is_dir())


def test_normalise_reaches_into_the_flatpak_home(tmp: Path) -> None:
    print("profiles.ini inside a Flatpak home is normalised too")
    root = tmp / ".var" / "app" / FIREFOX_ID / ".mozilla" / "firefox"
    (root / "Profiles" / "vjncdvaw.default-release").mkdir(parents=True)
    (root / "Profiles" / "i2p1dypv.default").mkdir(parents=True)
    (root / "profiles.ini").write_text(REAL_WINDOWS_INI, encoding="utf-8")
    ag.normalise_gecko_profiles(tmp)
    text = (root / "profiles.ini").read_text(encoding="utf-8")
    check("normalised", "Default=1" in section_of(text, "[Profile0]"), text)


#   Making the Flathub build the one the user sees

# Verbatim head of Debian's /usr/share/applications/firefox-esr.desktop. The
# action groups matter: NoDisplay in one of those hides a right-click entry
# instead of the launcher.
FIREFOX_ESR_DESKTOP = """[Desktop Entry]
Version=1.0
Name=Firefox ESR
Exec=/usr/lib/firefox-esr/firefox-esr %u
Terminal=false
Type=Application
Categories=Network;WebBrowser;
MimeType=text/html;x-scheme-handler/http;x-scheme-handler/https;
Actions=new-window;new-private-window;

[Desktop Action new-window]
Name=Open a New Window
Exec=/usr/lib/firefox-esr/firefox-esr --new-window %u

[Desktop Action new-private-window]
Name=Open a New Private Window
Exec=/usr/lib/firefox-esr/firefox-esr --private-window %u
"""


def test_hiding_the_packaged_launcher(tmp: Path) -> None:
    print("the packaged Firefox is hidden, its action groups are not")
    src_dir = tmp / "usr" / "share" / "applications"
    dst_dir = tmp / "usr" / "local" / "share" / "applications"
    src_dir.mkdir(parents=True)
    (src_dir / "firefox-esr.desktop").write_text(FIREFOX_ESR_DESKTOP, encoding="utf-8")

    ag._hide_packaged_firefox(src_dir, dst_dir)

    result = (dst_dir / "firefox-esr.desktop").read_text(encoding="utf-8")
    entry = section_of(result, "[Desktop Entry]")
    check("NoDisplay is in [Desktop Entry]", "NoDisplay=true" in entry, str(entry))
    check("the action groups keep their own keys",
          "NoDisplay=true" not in section_of(result, "[Desktop Action new-window]"),
          result)
    check("Exec survives", any(ln.startswith("Exec=") for ln in entry), str(entry))
    check("MimeType survives", any(ln.startswith("MimeType=") for ln in entry))
    check("the package's own file is untouched",
          (src_dir / "firefox-esr.desktop").read_text(encoding="utf-8")
          == FIREFOX_ESR_DESKTOP)


def test_hiding_is_idempotent(tmp: Path) -> None:
    print("hiding twice does not stack NoDisplay lines")
    src_dir = tmp / "usr" / "share" / "applications"
    dst_dir = tmp / "usr" / "local" / "share" / "applications"
    src_dir.mkdir(parents=True)
    (src_dir / "firefox-esr.desktop").write_text(FIREFOX_ESR_DESKTOP, encoding="utf-8")

    ag._hide_packaged_firefox(src_dir, dst_dir)
    once = (dst_dir / "firefox-esr.desktop").read_text(encoding="utf-8")
    # Second pass reads the pristine system file again, so this really checks
    # the NoDisplay filter rather than the input happening to be clean.
    ag._hide_packaged_firefox(dst_dir, dst_dir)
    twice = (dst_dir / "firefox-esr.desktop").read_text(encoding="utf-8")
    check("still exactly one NoDisplay", twice.count("NoDisplay=true") == 1, twice)
    check("otherwise unchanged", once == twice, twice)


def test_no_packaged_firefox_to_hide(tmp: Path) -> None:
    print("a distribution that ships no Firefox launcher")
    src_dir = tmp / "usr" / "share" / "applications"
    dst_dir = tmp / "usr" / "local" / "share" / "applications"
    src_dir.mkdir(parents=True)
    ag._hide_packaged_firefox(src_dir, dst_dir)
    check("no override written", not dst_dir.exists())


def test_default_browser_on_a_fresh_home(tmp: Path) -> None:
    print("mimeapps.list written from nothing")
    ag._set_default_browser(tmp, "org.mozilla.firefox.desktop")
    text = (tmp / ".config" / "mimeapps.list").read_text(encoding="utf-8")
    entries = section_of(text, "[Default Applications]")
    check("https points at the Flatpak",
          "x-scheme-handler/https=org.mozilla.firefox.desktop" in entries, text)
    check("http points at the Flatpak",
          "x-scheme-handler/http=org.mozilla.firefox.desktop" in entries, text)


def test_default_browser_keeps_other_handlers(tmp: Path) -> None:
    print("an existing mimeapps.list keeps everything else")
    cfg = tmp / ".config"
    cfg.mkdir(parents=True)
    (cfg / "mimeapps.list").write_text(
        "[Default Applications]\n"
        "x-scheme-handler/http=firefox-esr.desktop\n"
        "application/pdf=org.gnome.Evince.desktop\n"
        "\n"
        "[Added Associations]\n"
        "image/png=org.gnome.Loupe.desktop\n", encoding="utf-8")

    ag._set_default_browser(tmp, "org.mozilla.firefox.desktop")
    text = (cfg / "mimeapps.list").read_text(encoding="utf-8")
    entries = section_of(text, "[Default Applications]")
    check("the stale handler is replaced, not duplicated",
          entries.count("x-scheme-handler/http=org.mozilla.firefox.desktop") == 1
          and "x-scheme-handler/http=firefox-esr.desktop" not in entries, text)
    check("the pdf handler survives",
          "application/pdf=org.gnome.Evince.desktop" in entries, text)
    check("[Added Associations] survives",
          "image/png=org.gnome.Loupe.desktop" in section_of(text, "[Added Associations]"),
          text)


#   Reopening the tabs that were open on Windows

REAL_PREFS_JS = """// Mozilla User Preferences

// DO NOT EDIT THIS FILE.

user_pref("browser.contentblocking.category", "standard");
user_pref("browser.startup.page", 1);
user_pref("browser.urlbar.placeholderName", "Google");
"""


def test_migrated_profile_restores_its_session(tmp: Path) -> None:
    print("a profile that came from Windows reopens its tabs")
    root = build(tmp, REAL_WINDOWS_INI,
                 ("Profiles/i2p1dypv.default", "Profiles/vjncdvaw.default-release"))
    prefs = root / "Profiles" / "vjncdvaw.default-release" / "prefs.js"
    prefs.write_text(REAL_PREFS_JS, encoding="utf-8")

    ag.normalise_gecko_profiles(tmp)
    text = prefs.read_text(encoding="utf-8")

    check("startup.page is 3",
          'user_pref("browser.startup.page", 3);' in text, text)
    check("the old value is gone, not just shadowed",
          'user_pref("browser.startup.page", 1);' not in text, text)
    check("set exactly once", text.count("browser.startup.page") == 1, text)
    check("the other preferences survive",
          'user_pref("browser.urlbar.placeholderName", "Google");' in text
          and 'user_pref("browser.contentblocking.category", "standard");' in text, text)
    check("the header survives", text.startswith("// Mozilla User Preferences"), text[:60])


def test_profile_without_prefs_js(tmp: Path) -> None:
    print("a Windows profile that has no prefs.js yet")
    root = build(tmp, REAL_WINDOWS_INI, ("Profiles/vjncdvaw.default-release",))
    ag.normalise_gecko_profiles(tmp)
    prefs = root / "Profiles" / "vjncdvaw.default-release" / "prefs.js"
    check("prefs.js is created with the one line",
          prefs.is_file()
          and prefs.read_text(encoding="utf-8").strip()
          == 'user_pref("browser.startup.page", 3);')


def test_linux_profile_keeps_its_own_startup_page(tmp: Path) -> None:
    print("a profile that did not come from Windows is not touched")
    native = """[Profile0]
Name=default-release
IsRelative=1
Path=xyz123.default-release
Default=1
"""
    root = build(tmp, native, ("xyz123.default-release",), stamp=LINUX_STAMP)
    prefs = root / "xyz123.default-release" / "prefs.js"
    prefs.write_text(REAL_PREFS_JS, encoding="utf-8")
    ag.normalise_gecko_profiles(tmp)
    check("left byte-for-byte alone",
          prefs.read_text(encoding="utf-8") == REAL_PREFS_JS)


if __name__ == "__main__":
    import tempfile
    for test in (test_real_windows_profile, test_idempotent,
                 test_native_linux_ini_untouched, test_backslash_path,
                 test_install_names_a_missing_profile, test_zen_root,
                 test_version_reading,
                 test_esr_older_than_profile_installs_flathub_firefox,
                 test_release_channel_installs_nothing,
                 test_unknown_versions_change_nothing,
                 test_profile_moves_into_the_flatpak_home,
                 test_profile_stays_when_the_flatpak_sees_the_real_home,
                 test_profile_stays_when_there_is_no_flatpak,
                 test_existing_destination_is_not_overwritten,
                 test_normalise_reaches_into_the_flatpak_home,
                 test_hiding_the_packaged_launcher, test_hiding_is_idempotent,
                 test_no_packaged_firefox_to_hide,
                 test_default_browser_on_a_fresh_home,
                 test_default_browser_keeps_other_handlers,
                 test_migrated_profile_restores_its_session,
                 test_profile_without_prefs_js,
                 test_linux_profile_keeps_its_own_startup_page):
        with tempfile.TemporaryDirectory(prefix="gecko-test-") as d:
            test(Path(d))
    print()
    if failures:
        print(f"{len(failures)} FAILED: {', '.join(failures)}")
        sys.exit(1)
    print("all checks passed")
