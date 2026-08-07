# Building from source

## Requirements

- Windows 10 version 1809 or later / Windows 11
- .NET 9 SDK (the application targets `net9.0-windows`; the libraries target
  `net8.0` and are built by the same SDK)
- Administrator privileges (partition resize and UEFI NVRAM writes require
  elevation)

## Build and run

```powershell
git clone https://github.com/gillesduif/iGloo.git
cd iGloo
dotnet restore
dotnet build
dotnet run --project src/Igloo.App
```

The application requests UAC elevation at startup because partition resize,
UEFI NVRAM entry registration and EFI partition writes require a
high-integrity token.

## Tests

```powershell
dotnet test
```

Six xUnit test projects cover the safety-critical logic: manifest handling,
ISO verification, partition calculations, installer configuration rendering
and progress reporting. CI executes the build and the test suite on every
push and pull request.

## Verifying an installation

After the first boot of the installed system:

```bash
systemctl status igloo-first-boot          # agent service state
sudo cat /var/log/igloo/first-boot.log     # full agent output
ls -la ~/Documents ~/Downloads             # migrated files
sudo grep linuxPassword /var/lib/igloo/manifest.json   # expected: null (redacted)
```

To re-run the agent during development:

```bash
sudo rm /var/lib/igloo/.done
sudo python3 /opt/igloo/agent.py --manifest /var/lib/igloo/manifest.json --log-dir /var/log/igloo
```

> **Naming note:** the C# namespaces use `Igloo` (PascalCase) because C#
> identifiers cannot start with a lowercase letter. The product name is
> "iGloo" and the code identifier is `Igloo`.
