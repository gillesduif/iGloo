Second alpha of iGloo: migrate from Windows 11 to Linux without touching a terminal.

Validated end-to-end on bare metal, dual-boot alongside Windows 11:

- Fedora KDE
- Linux Mint Cinnamon
- Debian (GNOME)

## Added

- Browser migration carries the whole profile. 0.1-alpha moved saved passwords only. A ticked browser now arrives with its bookmarks, history, favicons, cookies and the tabs that were open on Windows. Firefox and Zen keep their existing profile directory instead of being handed a new one, and the installed Firefox is matched to the profile's major version. The migrated browser is made the default.
- The Windows account picture is carried over as the Linux avatar.
- The boot menu is themed and lists one entry per operating system, each with its own logo, and names the Windows entry instead of numbering it. There is no *Advanced options* submenu.
- The hostname is derived from the Windows computer name where that is a valid hostname, instead of always from the user name.
- `igloo-boot-order.service` puts this system back at the front of the UEFI boot order on every boot, not only at install time.

## Fixed

- Choosing Windows from the boot menu once made Windows the permanent default. The machine then went straight to Windows on every following boot and looked like Linux had never been installed.
- A Linux installed earlier stopped booting once another distribution was installed next to it, hanging indefinitely on a device that did not exist. The boot entry for it named a partition by kernel name, which changes with probe order.
- The migrated display layout reverted to the distribution default after logging out.
- The application scan missed roughly a quarter of the installed programs.
- Brave and Chrome discarded the imported passwords and reset their password database.
- Every install left an orphaned entry behind in the Windows boot store.

The full list is in the [changelog](https://github.com/gillesduif/iGloo/blob/main/CHANGELOG.md).

## Known limitations

- Fedora: the KDE first-login setup screen still appears once ([#1](https://github.com/gillesduif/iGloo/issues/1))
- Mint: wallpaper scaling on landscape monitors uses zoom instead of fit ([#2](https://github.com/gillesduif/iGloo/issues/2))
- Debian: creates a second 1 GB EFI partition instead of reusing the Windows one ([#227](https://github.com/gillesduif/iGloo/issues/227))
- The boot menu labels the Windows entry "Windows 11" on Windows 10 machines ([#228](https://github.com/gillesduif/iGloo/issues/228))
- Boot can take around 100 seconds between the menu and the login screen on NVMe ([#221](https://github.com/gillesduif/iGloo/issues/221))
- Locales without a glibc equivalent, such as en-BE, fall back rather than map ([#220](https://github.com/gillesduif/iGloo/issues/220))
- Unsigned installer: SmartScreen will warn (click "More info" > "Run anyway")

SHA256 of `iGloo-Setup-0.2-alpha.exe`:

```
2f9b154f561398e468ff74760ca728c3e95ec8cf72b760157132618387e6556e
```

Requires Windows 10 (version 1809) or Windows 11. On systems with an NVIDIA GPU and Secure Boot enabled, the installation completes but the locally compiled NVIDIA module is rejected by the kernel; the pre-flight check warns about this in advance. Linux Mint is the exception: it ships pre-signed NVIDIA modules and works with Secure Boot enabled. Read the [README](https://github.com/gillesduif/iGloo#readme) for the full safety model before running.
