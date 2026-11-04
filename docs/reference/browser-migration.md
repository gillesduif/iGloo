# Browser migration

What iGloo moves from the Windows browsers to the Linux ones, and the eight
places where a copy that looks correct still produces an empty browser.

## Windows staging before installation

The Community wizard supplies each selected browser's engine to the shared
staging service. For Chromium, Windows stages the same `Default` profile files
that the first-boot agents consume: Bookmarks, History, Favicons, Top Sites,
Network/Cookies and Sessions. Machine state such as `first_party_sets.db`,
Local State, caches and runtime locks is not part of that portable snapshot.
Password extraction remains a separate operation.

SQLite databases are captured with the SQLite backup API, including committed
WAL data, into standalone database files. Space measurement reserves the main
file plus its WAL and rollback journal; a snapshot that grows beyond that
reservation is rejected. A sidecar without its main database is rejected too.

SQLite PERSIST journals can remain nonempty after a successful commit. SQLite
also initializes a journal with zero magic and nonzero metadata before it can
write protected main-file pages. Both states are accepted under source locks;
the journal's length alone does not identify an active transaction. A hot
journal is validated, copied alongside an owned private main-file snapshot,
and recovered by SQLite only in that private copy. Validation checks page and
sector sizes, record bounds, checksums, duplicate page records, successive
headers and the reserved recovery size. Super-journal footer markers are
rejected before SQLite can follow an external pathname. The recovered copy
must pass `quick_check`. Browser main databases, rollback journals and WAL
files are opened read-only and never recovered in place. SQLite may update an
existing SHM coordination file on the normal WAL read path. Temporary cleanup
attempts every owned snapshot file even if an earlier deletion fails.

An idle open Windows browser handle can prevent the locked file copy. Only
that known sharing/locking failure enables a bounded read-only SQLite fallback
with exclusive locking mode selected before its first database read. This
avoids creating source WAL sidecars. Existing complete WAL sidecars are read
through SQLite's normal snapshot protocol; an incomplete pair is rejected.

Other selected files retain strict copy checks. Typed `BrowserInUse` failures
show which selected browsers and background processes to close, with an
explicit Task Manager action and retry that preserves the selection. Typed
`RecoveryRequired` failures offer open/recover/close/retry guidance when safe
private recovery is unavailable. Neither message displays raw source paths.
No browser process is stopped automatically. Failed staging removes its own
incomplete generation through the existing cleanup path.

This path has synthetic Windows file-lock and SQLite snapshot regression tests;
those checks do not establish a completed installation or a live-browser import
on the user's VM. The NTFS routes described below also remain for older manifests
without staged browser paths.

The standalone `tests/installer/BrowserStagingProbe` runner exercises the same
public staging service against the current Windows user's Edge profile. It
compares source main/journal/WAL hashes and removes only its own generated test
copies. It saves a result without browser contents beside the runner, then
tries the optional writable `\\VBoxSvr\igloo-repair-evidence` share and
`%LOCALAPPDATA%\Igloo\diagnostics\browser-probe` if necessary. The visible
staging result survives evidence-save failures. Unreadable or changing source
files produce an unverified result rather than a claim of unchanged data.
Journal diagnostics contain header metadata and, only when a super-journal
magic marker is present, footer metadata; page contents are not recorded.
This is a browser-staging check, not an installer, credential-import or
cross-reboot acceptance test. A live VM result requires the executed probe's
report; building this runner does not establish that result.

## What migrates

| Engine | Browsers | What moves |
|---|---|---|
| Gecko | Firefox, Zen, Waterfox | The whole profile root: bookmarks, history, cookies, saved passwords, open tabs, extensions, preferences |
| Chromium | Chrome, Edge, Brave, Vivaldi, Opera | Bookmarks, history, favicons, top sites, cookies, saved passwords, open tabs |

Open tabs are covered below - the two engines keep them in different places.

Gecko profiles are OS-portable, including `key4.db` and `logins.json`, so the
agent copies the profile root straight off the mounted NTFS partition.

Chromium needs two mechanisms, because DPAPI protects the master key and the
master key encrypts only three things. Measured on a real Brave profile:

| Data | File | Encrypted |
|---|---|---|
| Bookmarks | `Default/Bookmarks` | no — plain JSON |
| History, Favicons, Top Sites | `Default/…` | no — plain SQLite |
| Passwords | `Default/Login Data` | yes |
| Cookies | `Default/Network/Cookies` | yes, `v10` |
| Payment cards | `Default/Web Data` | yes |

So the plain files are simply copied off the NTFS partition by the first-boot
agent, the way a Gecko profile root is. Passwords are decrypted on Windows and
re-encrypted for transit (ADR-011), then written into `Login Data` at a login
after the installed browser has initialized its database. Cookies ride the same
envelope, but the jar itself is copied verbatim and
only its `encrypted_value` column is rewritten — the decrypted value travels as
opaque bytes, never as text, because current Chromium prefixes it with a hash of
the cookie's domain and copying the row wholesale keeps `host_key` matching
without iGloo having to know that format.

Which side reads the Windows partition differs per distribution, so the plain
files arrive by two routes. The Debian-family agent mounts the NTFS itself and
places the files directly. Fedora's agent cannot — all its NTFS reading happens
in the kickstart `%post`, which runs before any Flatpak exists and so cannot know
the destination. There the `%post` stages the files under
`/var/lib/igloo/chromium/<browser>/` and the agent's `browser-profiles` step
moves them once the browsers are installed, then clears the staging directory.
Both routes end in the same `_place_chromium_files()`.

Only the `Default` profile is migrated. A `Profile N` needs an entry in
`Local State` to be reachable, and `Local State` is machine state — it holds the
DPAPI-wrapped master key, which is meaningless on Linux.

Payment cards are deliberately left behind. They are encrypted like passwords
and could cross the same way, but a browser silently offering a card it cannot
decrypt is a worse failure than no card at all.

## Open tabs

Gecko keeps the session inside the profile root, in
`sessionstore-backups/recovery.jsonlz4`, so it moves with everything else and
needs no special handling. Chromium keeps it in a `Sessions/` directory of
`Session_*` and `Tabs_*` files, which is why that one directory is copied whole
rather than file by file.

Having the data is not the same as reopening it. Firefox only restores a session
when `browser.startup.page` is 3, and a typical Windows profile has 1 (open the
home page) - measured on the reporter's Zen profile. The agent therefore sets the
preference to 3 in `prefs.js`, but only in profiles whose `compatibility.ini`
still says `WINNT`, so it fires once and only on what actually came from Windows.

It goes in `prefs.js`, not `user.js`: `user.js` re-applies its value at every
start, which would leave the user unable to change this back from Settings.

Chromium has no equivalent here. Its restore setting lives in `Preferences`,
which is machine state iGloo does not carry, so the tabs arrive but the browser
opens on its new-tab page. They are one "Reopen closed window" away rather than
lost.

## Trap 1: profiles.ini names a profile Linux never picks

A Windows `profiles.ini` selects the active profile through an install section:

```ini
[Install308046B0AF4A39CB]
Default=Profiles/vjncdvaw.default-release
```

`308046B0AF4A39CB` is a hash of the installation directory — on Windows,
`C:\Program Files\Mozilla Firefox`. No Linux build ever hashes to it. Firefox
then falls back to the legacy `Default=1` flag, and on a Windows profiles.ini
that flag usually sits on a stale, empty profile left over from an earlier
install.

Measured on the Debian 13 run of 2026-08-19: the agent copied 580 MB into
`~/.mozilla/firefox` correctly, and Firefox opened the 1 KB profile next to it.

`normalise_gecko_profiles()` in both agents rewrites the file: the install
section's target gets `Default=1`, every other profile loses it, and the
`[Install…]` and `[BackgroundTasksProfiles]` sections — both keyed on Windows
paths — are dropped. A `profiles.ini` with no install section is left untouched,
because that one is already native.

## Trap 2: the profile is newer than the Linux browser

Firefox refuses to open a profile a newer build has touched, so it offers a new
empty profile instead. Windows runs the release channel; Debian and Fedora ship
ESR, which always trails it:

```ini
[Compatibility]
LastVersion=153.0.4_20260810162159/20260810162159
LastOSABI=WINNT_x86_64-msvc
```

The agent strips `LastPlatformDir` and `LastAppDir` — both Windows paths — and
leaves the rest of the file in place. It must not delete the file; see trap 8.
`LastOSABI` is the discriminator, so a genuine Linux profile is left alone.

That alone would not save `places.sqlite`: Places replaces a database whose
schema is newer than it understands, costing bookmarks and history. So the agent
also removes the version gap instead of only surviving it. `ensure_matching_firefox()`
compares the profile's `LastVersion` against `firefox --version` / `firefox-esr
--version`, and installs Flathub's `org.mozilla.firefox` — the same release
channel Windows runs — only when the distribution's build is the older one.

Once that build is really installed — checked, not assumed, because hiding the
packaged launcher before the download succeeded would leave the machine with no
browser — the agent also takes the distribution's Firefox out of the menu and
points `x-scheme-handler/http`, `https` and `text/html` at the Flathub build.
Two Firefoxes with nothing to say which one holds your data is a worse outcome
than the version gap was. The packaged entry is hidden with a `NoDisplay` copy in
`/usr/local/share/applications`, which `XDG_DATA_DIRS` ranks above
`/usr/share`: apt's own file is untouched, and deleting that one override brings
the entry straight back.

Which in practice means Debian only. Mint packages the release channel (its
Mozilla partnership rules out ESR) and Fedora ships release, so both compare
equal and no second Firefox is installed. The comparison, not the distro name, is
the condition — if Debian ever ships release Firefox this stops firing by itself.
When either version cannot be read, nothing is installed.

## Trap 3: Flatpak browsers do not read ~/.config

Every browser iGloo installs comes from Flathub (`WindowsAppScanner` sets no
native package for any of them). Flatpak always overrides `XDG_CONFIG_HOME`, so
a Flatpak Brave reads

```
~/.var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser/Default/Login Data
```

and never looks at `~/.config/BraveSoftware/`. The credential import writes to
every root that has a matching install — the Flatpak's when `flatpak info` finds
it, `XDG_CONFIG_HOME` when the packaged binary is on `PATH`, both when both are
there — because picking one means guessing and a wrong guess writes the passwords
where nothing reads them.

Gecko browsers hit the same wall from the other side. They keep their profile
under `$HOME`, and Flatpak hands an app without host filesystem access its own
`$HOME`, so a Flatpak Firefox reads
`~/.var/app/org.mozilla.firefox/.mozilla/firefox`. `relocate_gecko_profiles()`
moves the copied profile there when the Flatpak is installed and
`flatpak info --show-permissions` shows no `home` or `host` filesystem access.
Both directories sit in the user's home, so it is a rename rather than a second
copy of a profile that can run to hundreds of megabytes. An existing destination
is never overwritten.

## Trap 4: a Login Data schema that lies about its version

`Login Data` is a SQLite database with a `meta` table naming its schema version.
Chromium reads that number and migrates the `logins` table up to its own
`kCurrentVersionNumber`, one step at a time.

iGloo created the file with a table carrying `possible_username_pairs` (added at
version 19), `date_last_used` (25), an AUTOINCREMENT `id` (26) and
`date_password_modified` (30) — while labelling it version 8. Brave therefore ran
the version 19 step, `ALTER TABLE logins ADD COLUMN possible_username_pairs`,
against a column that was already there. The migration failed,
`LoginDatabase::Init` returned false, and the caller recreated the file from
scratch. Measured on the Debian run of 2026-08-21: five logins written to the
right path with the right ownership, and an empty password manager.

The later version-31 replacement was also incomplete. In a disposable Fedora
guest on 2026-10-18, Chrome 155 logged `Unable to migrate database from 31 to
43` and displayed no saved passwords. The iGloo-created file had only `logins`
and `meta`; Chrome's own version-43 file also had sync metadata, insecure
credential, password note and statistics tables. An independent read-only check
decrypted the synthetic iGloo row, confirming that valid SQLite rows and v10
ciphertext alone did not establish browser usability.

The current importers wait for the installed browser to create a complete
`Login Data` before inserting passwords. If it has not done so, they retain
the encrypted envelope and password-attempt budget without prompting. A copied
Cookies database can still be processed separately. WSL regression tests and
an import into a copy of Chrome's native version-43 database pass. In the
disposable Fedora guest, the next login imported one synthetic password after
Chrome had created that database; Chrome's password manager displayed its site
and username, revealed the expected synthetic value, and still listed it after
a clean guest reboot. Disposable Debian and Mint guests then repeated the
browser-created database retry with Flatpak Brave: each password manager
displayed the synthetic site and username, revealed the expected synthetic
password, and still listed it after reboot. A read-only v10 verifier matched
each guest's schema-43 database against its independently recorded synthetic
password digest. Debian's Google Chrome first launch required third-party
terms; those terms were not accepted, so its Chrome UI was not tested.

## Trap 5: App-Bound Encryption

Chrome 127+ and current Edge encrypt the master key with App-Bound Encryption
and emit `v20` password values. iGloo does not defeat it (ADR-011). Those
browsers are skipped, the manifest keeps `includesPasswords: false`, and the
Windows log records which browser and why. Brave was measured as still
migratable on 2026-08-20; that can change with any Brave release.

## Trap 6: a profile for a browser that was never installed

The setup page has two independent lists: which browsers to migrate, and which
apps to install. Nothing linked them, so unticking Zen under apps while leaving
it ticked under browsers copied 300 MB of profile onto a machine with no Zen.

`GetSelectedSuggestions()` now adds the Flatpak for every selected browser that
has one. Firefox and Edge have no mapping — Firefox ships with the distribution,
Edge is not offered — so they are skipped.

## Trap 7: the app list could not see 64-bit installs

Zen was not merely unticked on the Mint run of 2026-08-20 — it was never offered.
iGloo publishes `win-x86`, and WOW64 redirects a 32-bit process's HKLM reads to
`WOW6432Node`. Both entries in the scanner's path list therefore resolved to the
same key. Measured from a 32-bit build on the reporter's machine:

```
Registry.LocalMachine + …\Uninstall     : 360 subkeys
Registry.LocalMachine + WOW6432Node\…   : 360 subkeys   <- the same key
RegistryView.Registry64 + …\Uninstall   : 478 subkeys
```

118 installations were invisible: every 64-bit-only one, which on that machine
meant Zen Browser, Thunderbird and KeePassXC. The scanner now names the views
with `RegistryKey.OpenBaseKey`, the same way `FindNativeExe` names `Sysnative` to
defeat the matching System32 redirect.

## Trap 8: deleting compatibility.ini stops Firefox adopting the profile

Setting `Default=1` is not enough. Since Firefox 67 the `[InstallHASH]` mapping
decides, and `Default=1` is only consulted on the path where Firefox adopts a
legacy profile for an install it has not seen before. That path has two gates,
both in `nsToolkitProfileService`:

```cpp
compat->Append(COMPAT_FILE);
rv = compat->Exists(&exists);
if (exists) {
  rv = MaybeMakeDefaultDedicatedProfile(profile, &result);
```

and inside that call:

```cpp
rv = compatData.GetString("Compatibility", "LastPlatformDir", lastGreDirStr);
if (NS_FAILED(rv)) {
  return true;          // no LastPlatformDir: counts as the current install
}
```

So `compatibility.ini` must **exist** and must **not** name a foreign install
directory. Removing the file to dodge trap 2 satisfies neither, and Firefox falls
straight through to `CreateDefaultProfile()`.

Measured on the Debian run of 2026-08-21. Every gecko browser started empty while
its data sat next to it, and the leftover profiles.ini says why:

```ini
[Profile0] Path=Profiles/vjncdvaw.default-release   Default=1   ← 599 MB, ours
[Profile2] Path=8s0n05mp.default-release-1                      ← 57 MB, new
[InstallCF146F38BCAB2D21] Default=8s0n05mp.default-release-1    ← Firefox's own
```

## Why Firefox is not in the suggested apps list

Every distribution iGloo installs already ships Firefox, so offering it would put
a second one on the machine. `WindowsAppScanner` has no mapping for it and
`FlatpakFor("Mozilla Firefox")` returns null, which is what
`GetSelectedSuggestions()` relies on to leave it out when a browser profile is
ticked. Debian's second Firefox is installed by the agent instead, and only when
the version comparison in trap 2 calls for it.

## Open questions

- **Zen and Waterfox on Fedora.** The kickstart `%post` copies those profiles, and
  the agent relocates them afterwards, so first boot is covered. A profile copied
  by any later path would not be.

The [credential decision](../decisions/011-chromium-credential-migration.md)
records the separate Chromium policy. These local migration data are outside
Fleet's sanitized [evidence boundary](../fleet/security.md).
