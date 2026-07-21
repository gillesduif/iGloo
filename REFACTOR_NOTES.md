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

## Fixed after maintainer sign-off (were "skipped for behavior risk")

### 1. `ThrottledProgress` interval-arithmetic overflow - FIXED
Originally the `long.MinValue` seed made `now - _lastForwardedTicks` overflow
negative, so without a force predicate no report was ever forwarded (production
call sites masked it via `prev is null` in their predicates). Fixed with a
`_hasForwarded` flag: the first report is now always forwarded, the force
predicate is still evaluated on every report, and the two production call sites
observe identical behavior. Tests updated to pin the fixed contract, including
a regression test for the production `prev is null` convention.

### 2. Hardcoded UEFI boot-entry description - FIXED
The NVRAM description is now the distro-neutral constant
`DirectInstallService.BootEntryDescription = "iGloo distribution installer"`
(was "Igloo Fedora KDE Installer" for every distro). Because the new string no
longer contains "Igloo" case-sensitively, both first-boot agents'
`cleanup_installer_partitions` were switched from a case-sensitive `"Igloo" in`
match to a case-insensitive one; `EfiBootEntries.IsIglooDescription` and
`LinuxRemovalService` were already case-insensitive. NOTE: this agent change is
runtime-relevant (unlike the earlier AST-identical layout pass), so the
Debian/Mint VM end-to-end validation is REQUIRED before merge, and any machine
carrying a boot entry written by an older build still cleans up correctly (the
old description also matches case-insensitively).

## Skipped for behavior risk (known issues, deliberately not fixed)

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
