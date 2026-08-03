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

Requires Windows 11; NVIDIA systems currently require Secure Boot to be disabled. Read the [README](https://github.com/gillesduif/iGloo#readme) for the full safety model before running.
