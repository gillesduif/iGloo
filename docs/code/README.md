# Code guide - for newcomers

How to find your way around the iGloo codebase without a guide dog. This
document tells you **what lives where, which files carry the weight, and how the
three big flows run through the code**. Read
[architecture.md](../architecture.md) first for the diagrams; this is the
file-level companion.

> Naming note: the product is **iGloo**, the code is `Igloo` (C# identifiers
> cannot start lowercase). Same convention Apple uses for iPhone/`IPhone`.

## Solution layout

```
src/
├── Igloo.App/         WPF wizard (UI + ViewModels + DI wiring)      - the shell
├── Igloo.Core/        contracts, models, plugin loading             - the vocabulary
├── Igloo.Preflight/   hardware detection + ALL disk work            - the muscle
├── Igloo.Iso/         download + cryptographic verification        - the gatekeeper
├── Igloo.Migration/   user-file staging on the Windows side
└── Igloo.UsbWriter/   fallback USB path
distros/               one folder per distro (plugin + templates + agent)
tests/                 xUnit suites
```

Two dependency rules keep this sane (enforced by review, stated in
[BR-08](../business/business-rules.md)): **Core references nothing**, and
**plugins never touch disk/network/firmware** - they only declare and render.

## Per-project tour

### Igloo.App - the wizard shell
| File | Why it matters |
|---|---|
| [`App.xaml.cs`](../../src/Igloo.App/App.xaml.cs) | Entry point: Serilog (file log in `%LOCALAPPDATA%\Igloo\logs`), DI container (`RegisterServices` - every service + ViewModel is registered here), plugin discovery at startup, crash logging |
| [`ViewModels/MainWindowViewModel.cs`](../../src/Igloo.App/ViewModels/MainWindowViewModel.cs) | The wizard conductor: ordered `_steps` list, `NextAsync()` drives navigation, branches dual-boot → `DirectInstallViewModel` vs replace → `UsbWriterViewModel`, and calls each page's `Prepare(...)` on entry |
| [`ViewModels/PreflightViewModel.cs`](../../src/Igloo.App/ViewModels/PreflightViewModel.cs) | Runs the hardware report; `CanProceed => HasReport && !HasBlockers` is the wizard's first safety gate |
| [`ViewModels/DistroSelectionViewModel.cs`](../../src/Igloo.App/ViewModels/DistroSelectionViewModel.cs) | Catalog: merges manifest data with **plugin `CheckCompatibility` findings** - a Blocker greys the distro out with its reason (this is where BR-06 lives) |
| [`ViewModels/IsoAcquisitionViewModel.cs`](../../src/Igloo.App/ViewModels/IsoAcquisitionViewModel.cs) | Drives download+verify; loads the bundled GPG key from the distro folder; wraps progress in `ThrottledProgress` |
| [`ViewModels/DirectInstallViewModel.cs`](../../src/Igloo.App/ViewModels/DirectInstallViewModel.cs) | Resolves the plugin's `InstallerBootSpec`, calls `PrepareAsync`, then `RegisterBootEntryAsync` + 10-second countdown reboot |

### Igloo.Core - the vocabulary
| File | Why it matters |
|---|---|
| [`Abstractions/IDistroPlugin.cs`](../../src/Igloo.Core/Abstractions/IDistroPlugin.cs) | **The** contract. `IDistroPlugin` (metadata, `CheckCompatibility`, `RenderInstallerConfigAsync`, `GetAgentPayloadAsync`, `GetInstallerBootSpec`) and `InstallerBootSpec` - the ~12 declarative fields that capture a distro's *entire* boot delta (cmdline, artifact paths, config delivery, full-ISO copy, pre-created root…). Read the XML docs here first; they encode the field lessons |
| [`Abstractions/CoreServices.cs`](../../src/Igloo.Core/Abstractions/CoreServices.cs) | Service interfaces (`IIsoAcquisitionService`, `IDirectInstallService`, …) + `IsoSpecification` |
| [`Models/MigrationManifest.cs`](../../src/Igloo.Core/Models/MigrationManifest.cs) | The single source of truth the wizard writes and everything downstream reads (user, hardware, files, Wi-Fi, apps) |
| [`Models/DistroManifest.cs`](../../src/Igloo.Core/Models/DistroManifest.cs) | Parsed `distro.json` (ISO/GPG spec, tags, requirements, `status` → catalog availability) |
| [`Plugins/`](../../src/Igloo.Core/Plugins/) | `DistroLoader` (manifests) + `DistroRegistry` (plugin DLLs, isolated `AssemblyLoadContext` each) |
| [`Services/ThrottledProgress.cs`](../../src/Igloo.Core/Services/ThrottledProgress.cs) | Rate-limits progress→UI (~10 Hz); read its doc comment before touching any progress plumbing |

### Igloo.Preflight - the muscle (all disk work lives here)
| File | Why it matters |
|---|---|
| [`DirectInstallService.cs`](../../src/Igloo.Preflight/DirectInstallService.cs) | The no-USB pipeline, ~1300 lines, the most consequential file in the repo. Key methods in execution order: `Prepare` (orchestrates), `CreateOemDrvPartition` / `CreateIsoPartition` (diskpart; FAT32 seed + NTFS for ≥4 GiB ISOs) / `EnsureRootPartition` (pre-created root for subiquity), `ConfigureBootFiles` (kernel/initrd extract-or-download, **initrd config injection** via `AppendFileToInitrd`/`BuildNewcCpio` - a from-scratch gzip'd cpio appender), `BuildStoragePartitionList` (full-fidelity curtin config generation), `SubstituteGeometryTokens` (fail-loud `{{IGLOO_*}}` guard), `RegisterBootEntryAsync` (UEFI `BootNext` via `SetFirmwareEnvironmentVariableW`) |
| [`PartitionResizeService.cs`](../../src/Igloo.Preflight/PartitionResizeService.cs) | NTFS shrink via Windows' own `Resize-Partition` WMI - deliberately no custom logic (BR-01) |
| `PreflightService` / `WindowsAppScanner` | Hardware report (BitLocker, Secure Boot, UEFI, GPU, RAM, disks); installed-app scan → Linux app suggestions |

### Igloo.Iso - the gatekeeper
| File | Why it matters |
|---|---|
| [`IsoAcquisitionService.cs`](../../src/Igloo.Iso/IsoAcquisitionService.cs) | Resumable download (Range requests), SHA-256 streaming, and the **fail-closed policy block** at the top of `AcquireAsync` - read that comment before touching anything (BR-02) |
| [`PgpCleartextVerifier.cs`](../../src/Igloo.Iso/PgpCleartextVerifier.cs) / [`PgpDetachedVerifier.cs`](../../src/Igloo.Iso/PgpDetachedVerifier.cs) | Fedora-style clear-signed CHECKSUM vs Debian/Ubuntu-style detached signatures; both enforce the full-fingerprint pin |

### distros/ - the plugins
| Path | Why it matters |
|---|---|
| [`_template/`](../../distros/_template/) | Documentation-by-example for a new distro |
| [`_schema/`](../../distros/_schema/) | JSON Schema for `distro.json` (CI-validated) |
| [`_debian-family/agent/`](../../distros/_debian-family/agent/) | The shared first-boot agent: `agent.py` (ordered best-effort steps: password, keyboard, apt, GPU, codecs, os-prober, Flathub, **user-file migration via ntfs-3g + rsync**, Wi-Fi, redaction), `first-boot.sh` (runner + `.done` marker), `igloo-first-boot.service` (`Before=display-manager.service`) |
| `fedora-kde/` | Reference plugin: Anaconda/kickstart, `%post` file copy, RPM Fusion agent |
| `debian/`, `linuxmint-cinnamon/` | d-i and Ubiquity variants - study their template comments; every guard comment marks a real field failure |
| `ubuntu/` | In development - read [`STATUS.md`](../../distros/ubuntu/STATUS.md) before touching |

## The three flows, traced through files

**Flow 1 - startup → catalog.** `App.OnStartup` builds the host → `DistroLoader.Load`
(manifests) + `DistroRegistry.LoadAsync` (DLLs) → `MainWindowViewModel` steps through
`PreflightViewModel` (report) → `DistroSelectionViewModel.RefreshCompatibility(report)`
merges Secure-Boot tags + plugin findings into the catalog items.

**Flow 2 - "Install" → reboot.** `DirectInstallViewModel.Prepare` resolves
`plugin.GetInstallerBootSpec()` → `DirectInstallService.Prepare` runs the sequence in
[architecture §4](../architecture.md#4-the-no-usb-direct-install-pipeline) (shrink →
partitions → boot files → config injection/staging → ISO copy → grub.cfg → staging
artefacts) → `RegisterBootEntryAsync` sets `BootNext` → `shutdown.exe /r /t 10`.

**Flow 3 - first boot on Linux.** Installer late-hook wrote only a bootstrap
(two-phase pattern, [architecture §5](../architecture.md#5-two-phase-first-boot-bootstrap-the-pattern-mint-proved)) →
oneshot unit runs before the display manager → bootstrap mounts the seed partition,
copies `agent.py` + `manifest.json` → agent applies the manifest step by step, logs to
`/var/log/igloo/`, marks `.done`, reboots first if a driver requires it.

## Building, publishing, debugging

```powershell
dotnet build                                   # whole solution
dotnet run --project src/Igloo.App             # run (UAC prompt is by design)

# Publish + installer (what testers and releases run):
installer\build-setup.bat
# Copies src/ + distros/ to C:\Temp\igloo-build first: the SDK's publish step
# fails from paths containing an apostrophe (MSB3094), and this checkout lives
# under "Gilles D'huyvetter". The script then publishes win-x64 self-contained
# and compiles installer\output\iGloo-Setup-<version>.exe with Inno Setup.
```

If `dotnet restore` or `dotnet publish` fails with
`NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')`, the
shell is missing standard Windows variables (`ProgramData` and friends), or a
lingering build server is still running with such an environment. Run
`dotnet build-server shutdown` and retry from a normal cmd or PowerShell
window; `build-setup.bat` sets the variables itself and shuts the servers down
first, so it is immune to this.

Logs: Windows side `%LOCALAPPDATA%\Igloo\logs\igloo-*.log` (Serilog, daily).
Linux side `/var/log/igloo/` + `/var/log/installer/` - every unattended phase
traces itself (BR-07); start any diagnosis from the logs, never from a guess.

## Conventions & sharp edges

- **Line endings are load-bearing.** Everything that crosses to Linux (kickstart,
  preseed, user-data, agent files) is normalized to LF at render/copy time - a CRLF
  once broke Fedora's kickstart include. Do not "fix" the normalizers.
- **Template guard comments are law.** Comments like "do NOT add partman-auto/method
  back" mark real disk-wiping incidents; treat them as tests without a runner.
- **Subiquity early-commands**: single-quoted payloads only; never `pkill -f`
  (see the warning blocks in `user-data.template` - both were live failures).
- **Progress reporting** goes through `ThrottledProgress`; report freely from
  services, never worry about UI pressure.
- **Every distro-facing byte gets verified or rendered** - nothing from the network
  is used unverified (BR-02), nothing user-specific is hardcoded (templates + tokens).

## Where to go next

- Add a distro → [distros/README.md](../../distros/README.md) + architecture §6/§9
- Why is X built this way → [decisions/](../decisions/) (ADRs)
- What must never break → [business rules](../business/business-rules.md)
- The research story → [white paper](../whitepaper/igloo-whitepaper.md)
