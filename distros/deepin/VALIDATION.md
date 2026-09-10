# Validation

## Target identity phase — 2026-09-10

Scope: generic target creation receipts and read-only Linux identity resolution.
No Deepin installer, deployment, formatting, GRUB/EFI writer or migration agent
was run. No host block device was opened by the tests. The Windows mutation
adapter was tested through injected fake storage; it was not run against a real
disk or VHD. Deepin remains blocked and coming-soon.

| Check | Result |
|---|---|
| Full Release solution build, SDK 9.0.304, `--no-restore -warnaserror` | Passed, zero warnings/errors. Full repository CA/style analyzer policy enabled. |
| Full solution `dotnet test -c Release --no-build --verbosity minimal` | **506 passed**, zero failed/skipped: Core 185, Preflight 136, Deepin 64, App 59, Iso 19, Migration 20, UsbWriter 23. |
| `wsl -d Ubuntu-24.04 -- python3 tests/installer/target_identity_test.py` | **38 tests passed**, including many malformed/ambiguity subcases. A Linux CI job runs this suite. |
| Disposable GPT-file integration | Passed for 512-byte and 4096-byte logical sectors, fragmented extents and nonconsecutive slots, using the production raw GPT parser. Header/table corruption, duplicate GUIDs, independently valid but disagreeing GPT copies, and fake stale sysfs/device IDs fail closed. No loop attachment, mount, privileged utility or host disk access. |
| Target JSON Schema Draft 2020-12 | Schema and shared fixture passed. Final root-run validation rejected all 21 missing/invalid variants; the independent schema review also checked nullable ESP and additional malformed cases. |
| Catalog schema | 18/19 catalog entries pass; `_template` also passes. Ubuntu's existing `in-development` enum failure remains, confirmed against `main`. Deepin passes. |
| Existing `tests/agent/*_test.py` | All six scripts passed under WSL. |
| Protected work | All eight UNRELATED/UNCERTAIN files in REVIEW.md remain SHA256-identical to the initial snapshot. |
| CodeQL / VM / physical hardware | Not run. CodeQL remains a CI gate; no local CLI was available. |

New tests exercise before/after creation invariants, no mutation on rejected
requests, exact returned GUID capture, no retry on partial/error/cancelled
creation, snapshot freezing, pending-manifest digest checks, preservation of
unknown migration fields, serialization, strict version/field parsing, same
model/size decoys, stale geometry, renamed/reordered devices, unrelated Linux
partitions and explicit ESP selection. The app test proves a strong-identity
plugin cannot reach legacy disk preparation; Deepin tests prove a valid claim
still cannot enable config, boot-spec or agent output.

Test issues corrected during validation: two tests assumed MiB rather than
sector alignment, and a mutable-collection fixture assumed a compiler-generated
read-only collection was an array. New-code analyzer findings were corrected;
the deliberate synchronous `Flush(true)` durability barrier is justified locally.

The identity model, capture postconditions, manifest transfer and Linux resolver
are implemented and validated synthetically. The real cross-reboot guarantee
still requires a disposable Windows/Linux VM run: prove the actual CIM embedded
return binding and refreshed GUIDs, carry the expected installation ID in the
boot handoff, and resolve the same virtual disk through Linux with a different
controller/name. Preserve and compare every GPT partition; attempt stale/clone
cases and require refusal. No formatter or installer should be part of that run.

The live collector intentionally refuses corrupt/unsupported GPT-looking media,
including hybrid layouts. A booted ISO's loop/optical devices may therefore
require further observation during that VM experiment; do not fix a refusal by
blindly omitting devices that could contain a duplicate target identity.

Later Deepin work still needs exact Windows space reservation and seed/ISO
staging, safe medium/run-ID delivery, root quiescence and filesystem checks,
constrained immutable deployment, ESP-preserving boot integration, OOBE and
migration. Physical-hardware validation must cover real storage-provider
behavior, 512e/4Kn, NVMe/SATA, firmware/Windows boot preservation and BitLocker.
See the generic [identity contract](../../docs/reference/installation-target-identity.md)
for the single-writer assumption, observation-time limitation and clone threat
model. Passing tests do not authorize formatting or prove a Deepin install.

Research checkpoint: `6ae25ab` (`Deepin: document installer research and block
unsafe integration`). Target identity is a separate subsequent commit on
`feature/deepin`; `main` remains at `2f8f8cea148915042b137536d12e9cfcbcad1890`.
Protected work is intentionally left uncommitted. No pushes were made.

### Identity-phase file inventory

15 added files, 15 modified files, no deletions. Paths are repository-relative.

| Area | Added | Modified |
|---|---|---|
| Core contracts | `src/Igloo.Core/Abstractions/IInstallationTargetConsumer.cs`, `src/Igloo.Core/Abstractions/IInstallationTargetPreparer.cs`, `src/Igloo.Core/Models/InstallationTargetClaim.cs` | `src/Igloo.Core/Abstractions/DiskInfo.cs`, `src/Igloo.Core/Abstractions/IDistroPlugin.cs`, `src/Igloo.Core/Models/MigrationManifest.cs` |
| Core validation/transfer | `src/Igloo.Core/Services/InstallationTargetValidation.cs`, `src/Igloo.Core/Services/InstallationTargetManifest.cs` | `src/Igloo.Core/Services/ManifestGeneratorService.cs` |
| Windows | `src/Igloo.Preflight/WindowsInstallationTargetPreparer.cs` | `src/Igloo.Preflight/WindowsPreflightChecker.cs`, `src/Igloo.App/App.xaml.cs`, `src/Igloo.App/ViewModels/DirectInstallViewModel.cs`, `src/Igloo.App/ViewModels/FileStagingViewModel.cs` |
| Linux | `distros/_shared/installer/igloo_target.py` | — |
| Tests/schema | `tests/Igloo.Core.Tests/InstallationTargetIdentityTests.cs`, `tests/Igloo.Preflight.Tests/InstallationTargetPreparationTests.cs`, `tests/Igloo.App.Tests/InstallationTargetWorkflowTests.cs`, `tests/Igloo.Distro.Deepin.Tests/DeepinTargetIdentityTests.cs`, `tests/installer/target_identity_test.py`, `tests/fixtures/installation-target.json`, `docs/schemas/installation-target.schema.json` | `.github/workflows/ci.yml` |
| Deepin/docs | `docs/reference/installation-target-identity.md` | `distros/deepin/DeepinPlugin.cs`, `distros/deepin/STATUS.md`, `distros/deepin/VALIDATION.md`, `distros/README.md`, `docs/architecture.md`, `docs/reference/operation.md` |

## Research checkpoint validation — 2026-09-09

Scope: the blocked Deepin plugin and preservation of existing repository behavior.
These results do **not** validate Linux installation, partitioning or migration.

## Build and tests

Windows host; .NET SDK **9.0.304** selected with a temporary `global.json` outside
the repository, matching the .NET 9 CI toolchain. Commands below used the absolute
solution path while running from that temporary directory. No SDK configuration
was added to the repository.

| Check | Result |
|---|---|
| `dotnet restore Igloo.sln` | Passed, all 18 projects restored/up to date. |
| `dotnet build Igloo.sln -c Release --no-restore -warnaserror` | Passed, zero warnings/errors. Full CA rules and build style analyzers enabled by Directory.Build.props. |
| `dotnet test Igloo.sln -c Release --no-build --verbosity normal` | Passed, all seven test projects: Core, App, Preflight, Iso, Migration, UsbWriter, Deepin. |
| Focused Deepin Release build with `-warnaserror` | Passed, zero warnings/errors. |
| Focused Deepin tests | **56 passed**, zero failed. |
| Existing `tests/agent/*_test.py` scripts under WSL Ubuntu-24.04 | All six passed: boot_order, chromium_profile, gecko_profiles, gnome_applier, grub_root_uuid, monitors_xml. Each script was executed directly, not via empty unittest discovery. |
| Deepin JSON Schema Draft 2020-12 + URI format validation | Passed. |
| Full catalog schema validation | **18/19 passed**. Existing Ubuntu `status: in-development` is outside the schema enum; the same failure was verified on `main`. Left untouched. |
| `git diff --check` | Passed. |
| Packaged Deepin DLL versus source-folder discovery DLL | SHA256 identical; app output metadata also says coming-soon. |
| CodeQL | Not executed locally: CLI unavailable. Existing CI CodeQL workflow remains required. |

Deepin tests check real packaged metadata and its coming-soon status; permanent
blocking even if metadata says available or omits status; all executable
entrypoints refusing; RAM/Secure Boot/NVIDIA findings; null inputs and cancellation;
missing/malformed/invalid metadata; unknown/missing/ambiguous target data;
unresolved placeholders; and malformed stale installer/agent resources.

There is deliberately no successful template-rendering, boot-spec or payload
packaging test: those capabilities are not implemented. Tests require refusal,
so a future developer cannot mistake a passing suite for a validated installation.
There is no disk-layout generator left to test. The next deployment implementation
needs a separate aggressive identity/layout and disposable-disk test suite.

## Repository safety

- Branch: `feature/deepin`.
- Feature HEAD and `main` remain `2f8f8cea148915042b137536d12e9cfcbcad1890`.
- `test/extract-cookies` remains `9f1c924be09e1f68b60386239bb4f548393474c2`.
- Index is empty; nothing committed or pushed.
- All eight UNRELATED/UNCERTAIN files in REVIEW.md were compared to their initial
  snapshot using SHA256 and remain byte-for-byte unchanged.
- Reviewed Deepin-driven edits to the app, core, preflight, allocation tests,
  `.gitattributes` and third-party notice were removed through normal file edits;
  those files have no diff against `main`.
- No destructive Git command, host partition write or firmware write was used.

The initial snapshot remains at the temporary path recorded in REVIEW.md.
It contains the rejected prototype if historical investigation needs it.

## Outstanding validation

No VM was booted and no end-to-end install was attempted. No QEMU executable was
available in the checked Windows/WSL command paths. More fundamentally, the owned
target contract and constrained deployment driver still need implementation;
booting the stock ISO would not validate those missing components.

See STATUS.md for the required VM and physical-hardware matrices. Until those
pass, keep Deepin coming-soon and all executable entrypoints blocked. The
pre-existing shared-agent `--fix-boot-order` defect documented in REVIEW.md is
not covered by the six passing agent scripts and remains outside this change.
