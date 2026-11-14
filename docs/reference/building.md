# Building from source

## Requirements

- Windows 10 version 1809 or later / Windows 11
- .NET 10 SDK (Community targets `net9.0-windows`, the libraries `net8.0`, Fleet
  Web and the CLI `net10.0`; one SDK builds all of them, and `dotnet test` also
  needs the .NET 8 and 9 runtimes)
- Administrator privileges (partition resize and UEFI NVRAM writes require
  elevation)

## Build and run

```powershell
git clone https://github.com/gillesduif/iGloo.git
cd iGloo
dotnet restore
dotnet build
dotnet run --project src/Igloo.Community.App
```

The application requests UAC elevation at startup because partition resize,
UEFI NVRAM entry registration and EFI partition writes require a
high-integrity token.

## Tests

```powershell
dotnet test
```

Eight xUnit test projects cover the safety-critical logic: manifest handling,
ISO verification, partition calculations, installer configuration rendering
and progress reporting. CI executes the build and the test suite on every
push and pull request. Fleet builds and tests in its own repository,
[gillesduif/iGloo-Fleet](https://github.com/gillesduif/iGloo-Fleet); it consumes the
`Igloo.Command.Contracts` and `Igloo.Cli.Client` packages that `dotnet pack`
produces from this one.

## Verifying an installation

After the first boot of the installed system:

```bash
systemctl status igloo-first-boot          # agent service state
sudo cat /var/log/igloo/first-boot.log     # full agent output
ls -la ~/Documents ~/Downloads             # migrated files
sudo grep linuxPassword /var/lib/igloo/manifest.json   # expected: null (redacted)
```

The agent pass runs once per installation. A completed pass redacts its inputs and removes
the installer partition, so it cannot be repeated: to run the agent again, install again.
Do not delete `/var/lib/igloo/.done`. Without it the first-boot unit looks for the removed
partition and fails on every boot; if it was deleted, recreate it:

```bash
sudo touch /var/lib/igloo/.done
```

> **Naming note:** the C# namespaces use `Igloo` (PascalCase) because C#
> identifiers cannot start with a lowercase letter. The product name is
> "iGloo" and the code identifier is `Igloo`.
