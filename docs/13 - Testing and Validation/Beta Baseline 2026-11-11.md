---
title: Beta Baseline 2026-11-11
type: evidence-register
product: community
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: delivery
last_reviewed: 2026-11-11
reviewed_commit: 6b3a0efb154e957af6388aafb6bbe5f1d5bd298a
sources: [src/Igloo.Preflight/DirectInstallService.cs, src/Igloo.Core/Preparation/SeedIdentityV1.cs, src/Igloo.Migration/FileStagingService.cs, src/Igloo.Migration/FileStagingService.Chromium.cs, src/Igloo.Migration/Chromium/LoginDataReader.cs, src/Igloo.Preflight/LinuxRemovalService.cs, src/Igloo.Preflight/WindowsPreflightChecker.cs, distros/_debian-family/agent/agent.py, distros/fedora-kde/agent/agent.py, distros/debian/preseed/igloo-bound-free-do-option.sh, tests/Igloo.Preflight.Tests/SeedStagingCopyTests.cs, tests/Igloo.Preflight.Tests/DirectInstallReadinessTests.cs, tools/vm-lab/Check-BootRegistration.ps1]
aliases: []
tags: [igloo]
---

# Beta Baseline 2026-11-11

Host-only record for branch `refactor/community-fleet-foundation`: build, unit tests, Linux fixture scripts and source reading. No VM or hardware run is part of it. Evidence levels: **L0** source read, **L1** .NET tests executed, **L2** fakes and fixtures executed, **L3** disposable VM, **L4** hardware. Nothing here is L3 or L4.

## Baseline at `68670ad`

| Check | Command | Result | Level |
| --- | --- | --- | --- |
| Build | `dotnet build Igloo.sln -c Release --no-restore --no-incremental` | 0 warnings, 0 errors | L1 |
| .NET tests | `dotnet test Igloo.sln -c Release --no-build` | 2,079 tests: 2,076 passed, 2 failed, 1 skipped | L1 |
| Agent regressions | `python3 scripts/test_agent_regressions.py` in WSL `Ubuntu` (26.04.1 LTS, Python 3.14.4) | 31/31 scripts | L2 |
| Installer fixtures | `python3 -B -m unittest discover -s tests/installer -p "test_*.py"`, same WSL, not root | 547 tests: 414 passed, 128 skipped, 5 errors | L2 |

The two failures were `FileStagingViewModelTests.Background_browser_lock_offers_explicit_Task_Manager_action_and_retains_retry` (both rows): the assertion expected the label of the removed Open Task Manager button. The skip is the opt-in `PinnedCurrentDebianLiveIsoExposesTheRequiredEfiMembers`. The five installer errors are two import failures (`test_agent_offline`, `test_agent_offline_successor` need `distros/debian/native` on `PYTHONPATH`; both load with it), two `PermissionError` on `/root` and one missing `/var/lib/igloo`; all belong to the reviewed-native lab track. The earlier lab distribution `Ubuntu-24.04` did not start (`ext4.vhdx` not found), so these runs used a different WSL distribution than the October records.

## Seed copy for providers without user data on the seed

**[Observed: scratch probe against the `68670ad` Release build]** A console program outside the repository called the built `DirectInstallService.CopyStagingArtefacts` (file copies into a temporary directory only) with the file subset `Prepare` selects when `MigrationDataOnSeed` is false, then `SeedIdentityWriter.WriteAsync`:

- staged `files/Documents/a.txt`: the seed received the empty directories `files` and `files\Documents`; the identity was written without user files;
- staged `files/Documents/Sub/b.txt` as well: the seed also received `files\Documents\Sub`, and `SeedIdentityWriter` threw `InvalidDataException: Selected user data contains an unbound empty directory.`

**[Read in source]** In `Prepare` that exception comes after the NTFS shrink, the partition creation and the ISO copy. In the flat case the Debian-family agent takes the seed as its source as soon as `/run/igloo-seed/files` is a directory (`migrate_user_files`, `staged = _STAGED_FILES_DIR.is_dir()`), so the empty folder would have been copied instead of the Windows data. The first-boot half was not executed.

**Fix `e881f5b` (L1).** `SeedDirectories` leaves both user-data trees off the seed and out of the reuse comparison when the provider reads user data from the Windows volume; a seed written by the earlier build is therefore classified stale and replaced. `SeedStagingCopyTests` (3 tests) cover the copy with and without user data and the identity that follows. Audit: Debian and Linux Mint set `MigrationDataOnSeed = false` and are the affected providers; Fedora KDE and the parked Ubuntu keep the default and get the same directories as before (Fedora's kickstart still copies from `OEMDRV/files`); `distros/_debian-family` binds user data only when `files` exists on the seed and its agent tests already cover the Windows-volume source; `distros/_shared/agent` does not read either tree; the USB writer refuses Debian and Mint.

## Other changes

- **`6b3a0ef` (L1).** `SeedIdentityWriter.RequireSelectionBindableAsync` applies the identity's selected-image and selected-data rules to the staged tree before the boot checks. Two order tests in `DirectInstallReadinessTests`; `PlannedSelectionIsCheckedWithoutHashingOrWritingAnIdentity` covers a decomposed file name and a missing image.
- **`cb84450` (L0).** `tools/vm-lab/Check-BootRegistration.ps1` matches the seed by FAT32 volume label; the earlier 5–8 GB window does not contain a seed without user data. More than one `OEMDRV` volume is a failure. Parsed for syntax, not run in a guest.
- **`a8e9ad7` (L1).** The stale Task Manager assertion follows the current message.

After `6b3a0ef`: `dotnet build Igloo.sln -c Release --no-restore -warnaserror` 0 warnings, 0 errors; `dotnet test Igloo.sln -c Release --no-build` 2,085 tests, 2,084 passed, 0 failed, 1 skipped.

## Read in source, not changed

- **Browser locks (B-03).** `CopyLockedChromiumDatabase` covers `SQLITE_BUSY`/`SQLITE_LOCKED`. A database Windows refuses to open (`SQLITE_CANTOPEN` → `BrowserInUse` in `LoginDataReader.ReadConnectionWithoutSharedMemory`), a non-database profile file held open for writing (`CopyFileAsync` opens the source with `FileShare.Read`) and Firefox profile files in the same position still raise `BrowserProfileStagingException`, which removes the generation and ends staging. Whether a running Edge or Chrome produces these today is not verified.
- **First-boot cleanup.** `cleanup_installer_partitions` in both agents only logs; neither removes the installer partition or the iGloo firmware entry. Fedora's seed keeps the copied user data.
- **Linux removal.** `WindowsPreflightChecker.BuildLinuxInventory` lists every partition labelled `OEMDRV`, `CIDATA` or `IGLOOISO` on any disk as a seed leftover, and `LinuxRemovalService.Remove` deletes the listed partitions. The label is the only criterion.
- **System Check.** `PreflightViewModel.RunCheckAsync` extends the largest lettered partition on the Windows disk into adjacent unallocated space of 64 MiB or more on every run, without a prompt (`LinuxRemovalService.TryReclaimFreedSpace`).
- **Debian free-region choice.** On the Community path the preseed runs in `fallback` mode: `igloo-bound-free-do-option.sh` exits 99 unless exactly one free region on the bound disk meets the provider minimum, and it has no ESP binding (no `igloo.linuxesppartuuid` token), so the `atomic` recipe decides about the ESP as in issue #227.
- **Linux Mint.** The preseed uses stock `biggest_free`; the Debian template states that this choice scans every disk.
- **Suppressions.** `#pragma warning disable` and `[SuppressMessage]` occur in `src/` and `tests/` (for example `SeedIdentityV1.cs`, `PartitionResizeService.cs`), which CONTRIBUTING rule 1 excludes. None was added.

## Not verified

Every installer, first-boot and boot-registration statement above is L0 or L1. The r6 runs in [[00 - Home/Beta Track]] remain the acceptance test; see also [[13 - Testing and Validation/Evidence Register]].
