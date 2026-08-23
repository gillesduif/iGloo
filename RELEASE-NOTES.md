Second alpha of iGloo: migrate from Windows 11 to Linux without touching a terminal.

Validated end-to-end on bare metal, dual-boot alongside Windows 11:

- Fedora KDE
- Linux Mint Cinnamon
- Debian (GNOME)

## New in 0.2-alpha

- **Your browsers come with you.** 0.1-alpha carried saved passwords. A ticked browser now arrives with its bookmarks, history, favicons, cookies and the tabs that were open on Windows. Firefox and Zen keep their profile rather than getting a fresh one; Chrome, Edge, Brave, Vivaldi and Opera keep theirs. The migrated browser becomes the default.
- **Your Windows account picture becomes your Linux avatar.**
- **A boot menu that reads like one.** One entry per operating system, each with its own logo, the Windows entry named rather than numbered, and no *Advanced options* submenu.
- **The machine keeps coming back to Linux.** Picking Windows from the menu once no longer makes Windows the permanent default, and this system re-asserts its place in the UEFI boot order on every boot instead of only at install time.

The full list, including the fixes, is in the [changelog](https://github.com/gillesduif/iGloo/blob/main/CHANGELOG.md).

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
<vul in na het bouwen van de installer>
```

Requires Windows 10 (version 1809) or Windows 11. On systems with an NVIDIA GPU and Secure Boot enabled, the installation completes but the locally compiled NVIDIA module is rejected by the kernel; the pre-flight check warns about this in advance. Linux Mint is the exception: it ships pre-signed NVIDIA modules and works with Secure Boot enabled. Read the [README](https://github.com/gillesduif/iGloo#readme) for the full safety model before running.
