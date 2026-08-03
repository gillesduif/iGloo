First public alpha of iGloo: migrate from Windows 11 to Linux without touching a terminal.

Validated end-to-end on bare metal, dual-boot alongside Windows 11:

- Fedora KDE
- Linux Mint Cinnamon
- Debian (KDE)

Known limitations:

- Fedora: the KDE first-login setup screen still appears once (settings are pre-filled, but you click through it)
- Mint: wallpaper scaling on landscape monitors uses zoom instead of fit
- Unsigned installer: SmartScreen will warn (click "More info" > "Run anyway")

SHA256 of `iGloo-Setup-0.1-alpha.exe`:

```
c563d7ed2144f5ab2d8abd3503e695502c4034ec61022afe09140309edc9c0aa
```

Requires Windows 10 (version 1809) or Windows 11. On systems with an NVIDIA GPU and Secure Boot enabled, the installation completes but the locally compiled NVIDIA module is rejected by the kernel; the pre-flight check warns about this in advance. Linux Mint is the exception: it ships pre-signed NVIDIA modules and works with Secure Boot enabled. Read the [README](https://github.com/gillesduif/iGloo#readme) for the full safety model before running.
