# Validation

## Experimental VM phase — 2026-09-12 (in progress)

The user requested an experimental installation path. The app integration is
not enabled yet. All eight protected files from REVIEW.md still match their
initial SHA256 snapshots. No new commit or push has been made in this phase.

| Check | Result |
|---|---|
| Full Release build, SDK 9.0.304, warnings as errors, one build worker | Passed, zero warnings/errors. An initial parallel attempt exhausted host memory. Two analyzer findings in the target preparer's WMI cleanup were addressed with explicit ownership transfer and IDisposable cleanup; selection semantics are unchanged. |
| Full .NET suite | 505 passed, 1 failed. The existing catalog assertion requires `coming-soon`; the user's pre-existing local `distro.json` edit says `available`. That edit is preserved, and the plugin's execution blockers remain enforced. |
| Python installer suite | 67 passed. Includes optical pin validation and embedded GPT GUID collision checks, real collector with fake optical sysfs/device I/O, mount postconditions, ISO OEM handoff checks, preservation sampling, VM attachment constraints, and refusal to overwrite an existing experiment directory. CI now discovers all installer test files. |
| Actual pinned-ISO console boot | Passed in QEMU/KVM, stock 6.6 kernel/initrd, both live layers, no writable disks/network. This is direct kernel boot, not UEFI validation. |
| Collector inside Deepin | Initial tests correctly failed on an unreadable emulated floppy and then on the hybrid optical ISO. The final probe removes unused default emulated devices and verifies the complete pinned read-only optical image; collection passed with an empty target inventory. No blanket unreadable-device exception was introduced. |
| Immutable extraction/mount experiment | **Passed** against a newly created synthetic GPT disk file. Exact root resolution, empty-root check, ext4 creation, both repository checkouts and structured immutable mount postconditions succeeded. Raw GPT/reserved regions and sampled non-root data were unchanged. No host physical disk was exposed. |
| Immutable deployment experiment | **Passed** in `igloo-deepin-deploy-mount2-20260912`. Target initramfs generation and `deepin-immutable-ctl admin deploy -v` completed; status JSON reports the new deployment. GPT identity and reserved/non-root samples remained unchanged. No ESP or EFI writes occurred. Installed-system boot remains unverified. |
| Installed root direct boot | **Passed to serial login**, with the generated 6.18 kernel/initrd and generated `ostree=auto` command line, no ISO/network and a fresh qcow2 overlay over the disposable fixture. Immutable status reports the new deployment booted; `/usr` remains read-only. This bypasses EFI/GRUB. |
| Native first-boot UI | Account-creation screen observed after stopping the fixture's failing GPT-generated ESP automount and generating the missing config in the running guest. No account was submitted. The fresh-deployment fix must generate that config while the ISO OEM settings are present; the post-boot generator alone produces incorrect upstream UOS defaults. |
| Fresh deployment with handoff changes | `igloo-deepin-handoff-20260912` completed deployment with GPT identity and non-root/reserved samples unchanged. Fresh installed boot of this revision is pending. The later explicit OEM-setting validator was added after that VM uploaded its script. |
| EFI file and entry experiment | In a fresh qcow2 overlay, a new synthetic FAT ESP carried Microsoft/fallback-file canaries. Exact claim resolution preceded GRUB installation with a unique vendor directory and `--no-nvram --no-uefi-secure-boot`. Existing file hashes and ESP filesystem UUID remained unchanged; GPT resolution passed afterward. An explicit `efibootmgr --create-only` entry and BootNext preserved existing BootOrder and Boot entries. This is not proof of Windows bootability. |
| Complete OVMF → GRUB → installed kernel boot | Reached serial login without an ISO or QEMU-supplied kernel/initrd. Firmware log identifies the exact ESP GUID and `EFI/IglooDeepinVm/grubx64.efi`. **Post-boot bounded preservation passed:** all reserved/non-target samples and both existing EFI file canaries match. The initial apparent mismatch was an empty extraction, not changed GPT bytes. This still does not prove preservation of an actual Windows installation. |

Reproduction: [VM probes](../../tools/deepin-vm/README.md). Evidence logs are
retained under `/home/gillesduif/igloo-deepin-probe-pinned-20260912` and the
`igloo-deepin-immutable-*-20260912` directories in the Ubuntu-24.04 WSL instance.
Failed runs are retained too. The initial immutable run refused before formatting
because virtio's 20-byte serial limit truncated the fixture's extra serial fence;
the full GPT GUID resolution was not weakened to address that harness issue.
Some earlier runs were interrupted by WSL shutdown after their Windows launcher
exited; this was not evidence of installer failure or necessarily memory exhaustion.
The completed run kept a Windows WSL client alive, with a temporary 5 GiB memory
scope and direct I/O. The shipped mount helper's `LIBMOUNT_FORCE_MOUNT2=always`
setting was also necessary for the temporary writable overlay remount. None of
these tests proves EFI boot, Windows preservation, completed OOBE or migration.
Direct boot logs and the account-screen image are retained in
`igloo-deepin-deploy-mount2-20260912/boot-inspection/`. Both boot tests used fresh
qcow2 overlays; the original deployment image was not modified. A debug serial
root shell was enabled only in the second diagnostic VM, not in staged config.

EFI scripts, logs, private firmware variables, overlays and preservation samples
are retained in `igloo-deepin-deploy-mount2-20260912/efi-inspection/`. The host
sample extraction uses `qemu-img dd`; its output length/content must be checked
before interpreting the mismatch. Follow-up inspection was interrupted by WSL
startup errors (`E_UNEXPECTED`, then `0x80072746`). At that time VMware was using
about 17 GB and Windows reported about 1.4 GB free commit space. The user's VM
was not stopped or modified. The requested test platform is VMware with Windows
already installed. The app integration is still disabled.

After the user closed the VMware guest, inspection resumed. QEMU 8.2's `dd`
count includes skipped input blocks: using the sample length alone produced a
zero-byte file at the backup-GPT offset. Corrected extraction with an explicit
byte-count assertion passed every reserved/non-target sample and the Microsoft
and fallback EFI canaries. Results are in `efi-inspection/postboot-corrected/`.
`extract_image_range` now encapsulates this behavior and rejects short output;
focused tests cover the end-position calculation, empty output and invalid ranges.

The fresh handoff revision subsequently booted from its own generated kernel and
initrd in another new qcow2 overlay, without an ISO or debug shell. The native
account-creation screen appeared without repair commands. Its deployed config
also passed the explicit OEM repository/mode validator. Screenshot and console
evidence are in `igloo-deepin-handoff-20260912/boot-check/`. The screen uses the
ISO's default Chinese locale; mapping the user's selected locale remains part
of app integration. No account creation or cleanup hooks were submitted.

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
