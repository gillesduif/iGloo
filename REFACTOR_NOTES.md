# Refactor Notes

Senior-level code-quality pass, July 2026. Ground rules: zero functional changes,
frozen plugin contract, cleanup-only on safety-critical paths. Everything below is
either something deliberately NOT fixed (with the reason), or a decision that needs
the maintainer.

## Verification summary

- Formatting commit (`dotnet format whitespace`): every touched `.cs` file proven
  token-identical to its predecessor (whitespace-stripped SHA-256 comparison).
- Python agents: layout-only changes proven behavior-identical by comparing the
  parsed AST against HEAD (`ast.dump` equality) for both agents.
- Test suite grew from 1 test to 156 across six projects (Core 27, Migration 5,
  Iso 15, App 51, Preflight 43, UsbWriter 15). All green, `dotnet build` clean.
- Agent files changed only in layout (AST-identical), so the Debian/Mint VM
  end-to-end validation required by the ground rules should be a formality, but
  it has NOT been run in this pass.

## Skipped for behavior risk (known issues, deliberately not fixed)

### 1. `ThrottledProgress` interval-arithmetic overflow (latent bug)
`src/Igloo.Core/Services/ThrottledProgress.cs` seeds `_lastForwardedTicks` with
`long.MinValue`; `now - _lastForwardedTicks` overflows negative, so WITHOUT a
force predicate no report is ever forwarded. Both production call sites
(DirectInstallViewModel, IsoAcquisitionViewModel) mask it with
`forceWhen: (cur, prev) => prev is null || ...`, which forces the first report
through and initializes the timestamp; behavior after that is correct.
The correct fix (seed with `DateTime.UtcNow.Ticks - _intervalTicks`, or guard the
subtraction) changes observable behavior for any caller that omits `forceWhen`,
so it is a bug fix, not a refactor. Current behavior is pinned in
`ThrottledProgressTests`; fix at will, then update those tests.

### 2. Hardcoded UEFI boot-entry description
`DirectInstallService.RegisterBootEntry` writes the NVRAM description
`"Igloo Fedora KDE Installer"` for EVERY distro. Debian/Mint installs show a
Fedora-named entry in the firmware menu. The natural fix is to derive it from
`InstallerBootSpec.MenuTitle`, but the string is written to NVRAM (user-visible,
and matched by `EfiBootEntries.IsIglooDescription` via the "igloo" substring, so
cleanup keeps working either way). Changing it is a behavior change; needs a
product decision.

### 3. Sync-over-async inside the direct-install worker
`DirectInstallService.Prepare` runs synchronously on a thread-pool thread
(`Task.Run`) and calls `_resizer.ShrinkAsync(...).GetAwaiter().GetResult()` plus
blocking reads in `RunDiskpart`. Deadlock-safe in context (no sync-context), but
unusual. Making the whole pipeline truly async restructures a safety-critical
sequence; left alone.

### 4. `DistroRegistry.LoadAsync` is synchronous
Named `*Async`, returns `Task.CompletedTask`, performs sync I/O and assembly
loading. Changing the signature ripples into App startup; harmless as is.

### 5. `Result` property naming on step view-models
`IsoAcquisitionViewModel.Result` / `FileStagingViewModel.Result` read like
`Task.Result` at call sites (`_isoAcquisition.Result!`). Renaming would touch
XAML bindings; skipped as churn.

## Removed dead code (knowledge preserved elsewhere)

- `HttpIsoAcquisitionService` (Igloo.Iso): an unregistered M3-era stub superseded
  by `IsoAcquisitionService`.
- `DirectInstallService.FindLargestFreeGap` + `GetDiskSize`: unreferenced. The
  mid-disk-gap insight it documented (Windows Recovery at the disk tail means the
  shrink gap is in the middle) survives in ADR-008 and in the
  `BuildStoragePartitionList` comments, which is the code path that replaced it.

## Documentation discrepancy to resolve

`docs/architecture.md` (section 2) says plugins load into "isolated
`AssemblyLoadContext`s". The code (`DistroRegistry`) deliberately loads into
`AssemblyLoadContext.Default` and its doc comment explains why (shared
`Igloo.Core` identity, no duplicate-type issues). ADR-004 also promises
isolation. One of the two should be corrected; the code comment appears to
describe reality.

## DistroLoader / DistroRegistry load paths (for the parked artifact-hygiene pass)

Two independent loaders scan the same directory tree at startup
(`App.FindDistrosDirectory`: `distros/` adjacent to the exe, else a parent walk
from the bin directory up to the repo root):

- `DistroLoader.Load(dir)` reads `distros/<folder>/distro.json` for every
  non-underscore folder (catalog metadata only, no code).
- `DistroRegistry.LoadAsync(dir)` loads
  `distros/<folder>/Igloo.Distro.{PascalCase(folder)}.dll` (the LOOSE dll at the
  folder root, not `bin/`) into `AssemblyLoadContext.Default` and instantiates
  the single `IDistroPlugin` implementation.

The loose `Igloo.Distro.*.dll` files and `bin/`/`obj/`/`__pycache__` under
`distros/` are git-ignored build outputs, not tracked files. The App csproj's
`CopyDistrosToOutput` target copies the tree (excluding sources and `bin`/`obj`)
next to the exe, which is how the published app finds both the manifests and the
loose plugin DLLs. Any hygiene pass that relocates the loose DLLs must update the
`DistroRegistry` naming convention and that MSBuild target together.

## Style notes

- The `.editorconfig` formatting pass removed the column-aligned house style;
  style rules are suggestions so existing code is not warned on.
- Em dashes were removed from comments in files this pass edited; untouched
  files may still contain them.
- The `TODO v1.1` blocks in `UsbWriterService` (VDS/WMI instead of diskpart,
  SetupAPI enumeration) were kept deliberately: they document researched
  alternatives with rationale, not stale reminders.
- Per-plugin and per-agent duplication (manifest loading in each `*Plugin.cs`,
  overlapping helpers in the two `agent.py` files) is intentional isolation per
  ADR-004: plugins are separate assemblies, agents are separate payloads.
  Same-assembly extraction does not apply.

## Test infrastructure added

Six test projects (one per production assembly) with characterization tests
written BEFORE each refactor. Notable pins:

- Manifest wire property names (the exact camelCase strings the Linux agents
  parse) in `MigrationManifestTests`.
- The fail-closed ISO acquisition guards, proven to fire before any HTTP client
  is created; PGP detached-signature verification exercised with a generated RSA
  key, including fingerprint-pin rejection of a valid signature from the wrong key.
- The cpio `newc` member layout and the `EFI_LOAD_OPTION` byte format emitted by
  the direct-install pipeline.
- The GRUB kernel-line patch semantics (`rd.live.check` presence removal,
  `nomodeset` idempotence), the GPT CRC-32 check value, and boot-entry
  classification (never matches Windows entries).

`InternalsVisibleTo` was added to Igloo.Iso, Igloo.App, Igloo.Preflight and
Igloo.UsbWriter for their test assemblies; a handful of pure static helpers
changed from `private` to `internal` for direct testing (no other visibility or
signature changes anywhere).
