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
reviewed_commit: 71053e91a935f0d382dafe900f9b251a9bd40804
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
- **Installer recipe (observed).** The publish command of `installer/build-setup.bat` failed on a clean copy of the tracked files: `NETSDK1004` for `Igloo.Cli` without a restore, and `NETSDK1047` when only a restore without a runtime existed, because `PackagedCli.targets` restores and publishes the CLI inside the same build. The script now restores the CLI for `win-x64` first; with that step a fresh copy published with exit code 0. The script itself was not run, because it replaces `installer\publish`, `installer\output` and `C:\Temp\igloo-build`.

After `6b3a0ef`: `dotnet build Igloo.sln -c Release --no-restore -warnaserror` 0 warnings, 0 errors; `dotnet test Igloo.sln -c Release --no-build` 2,085 tests, 2,084 passed, 0 failed, 1 skipped.

## Protected host (`71053e9`)

**[Unit tests, L1]** `HostProtection` in `Igloo.Core` reads `HKLM\SOFTWARE\Policies\iGloo`, `ProtectedHost`, and every write primitive calls `RequireWritable` first. Refusal tests cover installer preparation, boot registration, Windows shrink, Linux removal, volume extension, firmware variable writes, `diskpart`, `bcdedit`, the BCD writer, both USB routes, raw write handles and `WindowsMaintenance`, each for a set and for an unreadable marker. `HostProtectionGuardTests` scans `src/` and fails on a raw write primitive outside the guard, and on product code that writes or overrides the marker. Test assemblies pin the marker to "not protected" through a module initializer, so the suite does not read the registry. After the commit: `dotnet build Igloo.sln -c Release --no-restore -warnaserror` 0 warnings, 0 errors; 2,127 tests, 2,126 passed, 0 failed, 1 skipped.

**[Read in source, three independent sweeps]** A read-only search by mechanism, by entry point and by file-system target found no write primitive under `src/` without a guard. It also found `WindowsMaintenance` (`manage-bde -off`, `shutdown /r`, `shutdown /r /fw`), which is guarded as well. No command of the CLI reaches a write primitive. Not guarded by decision: `Mount-DiskImage` / `Dismount-DiskImage` on the Target Disk page (the dismount also detaches a mount of the same ISO the user made) and the per-user AutoPlay policy value.

## Layout refusal before the shrink

**[Unit tests, L1]** `PlanLinuxRegion` is a pure function over the partition offsets of the target disk. It removes the seeds this run deletes, shrinks the Windows partition in the model when the largest existing extent is smaller than the plan, and requires exactly one unallocated region of the provider minimum or more. `RequireSingleLinuxRegion` reads the layout and runs it twice: before the first write, and on the live layout immediately before `IPartitionResizeService.ShrinkAsync`. `LinuxRegionAdmissionTests` cover a clean disk, a leftover region (refused, resizer not called), fitting existing space (no shrink), a region of exactly the minimum, a reused installer partition with and without space left, two ESPs, a missing or too small Windows partition, a rival region on another disk, a file-backed virtual disk (ignored), an unreadable layout and a stale seed. The refusals are `InstallationRefusedException`, which `DirectInstallViewModel` shows as the error text (`A_refusal_before_the_resize_is_shown_in_its_own_words`). Core.Host derives the rules from the plugin's `InstallerType` and `MinDiskBytes`; `Installer_target_rules_follow_the_shipped_plugins` ties Debian, Fedora KDE and Linux Mint to their rule, and an installer type without its own rule gets the strictest one. After the change: build `-warnaserror` 0 warnings, 0 errors; 2,151 tests, 2,150 passed, 0 failed, 1 skipped. No VM run.

**Audit per provider (CONTRIBUTING rule 3, read in source).**

- **Debian** (`DebianInstaller`). `igloo-bound-free-setup.sh` binds the disk by GPT GUID and size and quarantines every other automatic partman choice; in fallback mode `igloo-bound-free-do-option.sh` exits 99 unless exactly one free region on that disk is at least `MinDiskBytes`. The new check uses the same number, on the same disk. No ESP rule: without an `igloo.linuxesppartuuid` token the `atomic` recipe decides (#227).
- **Linux Mint** (`Ubiquity`). `partman-auto/init_automatically_partition select biggest_free`, no disk bound. Refused: a second region on the target disk, a region on any other disk at least as large as the planned one (file-backed virtual disks excepted; a disk without partitions counts as one region), more than one ESP on the target disk. An ESP on another disk is not part of the rule and not verified.
- **Fedora KDE** (`Anaconda`). The kickstart matches the disk by GPT GUID and size, then `ignoredisk --only-use`, `clearpart --none`, `autopart --type=plain --nohome`, `bootloader --boot-drive`. No line binds an ESP, so more than one ESP on the target disk is refused. Whether `autopart` spreads over several small unallocated regions of that disk is not verified.
- **Ubuntu** (`Subiquity`, parked; `distro.json` status `in-development`, so Core.Host refuses before the service). The root partition is created from Windows, so the region rule is skipped. Not changed, and to fix before Ubuntu returns: `EnsureRootPartition` reuses any existing Linux-filesystem partition, and `BuildStoragePartitionList` marks every such partition `root, wipe: superblock` and every ESP `esp`.
- **`distros/_debian-family`, `distros/_shared`.** First-boot agents and GRUB handling; neither chooses a partition or a region.

## Seed leftovers in the Linux removal

**[Unit tests, L1]** `InstallerSeedOwnership.FindOwnedSeed` accepts a partition only on a disk with the Windows boot partition, at the reported offset and size, of the basic-data GPT type, not flagged boot or system, with a reachable volume that carries `igloo-seed-identity.json`, `igloo-seed-reuse.json` or `igloo-boot`. `WindowsPreflightChecker.BuildLinuxInventory` uses it to decide what System Check lists; `LinuxRemovalService.FindOwnedSeed` uses it again on the live layout before `DeleteObject`. `InstallerSeedOwnershipTests`: a label without iGloo files, a seed on a second disk, a moved or resized partition, an ESP-typed or unmounted partition and an unreadable layout are all left alone. 2,165 tests, 2,164 passed, 0 failed, 1 skipped; build `-warnaserror` clean. The delete itself (WMI) is not exercised by a test.

**Audit per provider (read in source).** Debian, Linux Mint and Fedora KDE use the label `OEMDRV`, and `Prepare` writes the three marker entries on every seed. The Debian-family agent redacts `migration-manifest.json`, `preseed.cfg` and `igloo-boot/initrd` in place at first boot; the Fedora agent replaces `ks.cfg` and the manifest. Neither removes the identity file or `igloo-boot`, so a seed stays recognisable after an installation. Ubuntu (parked) puts an ISO above 4 GiB on a separate `IGLOOISO` NTFS partition without a marker file; that partition is no longer listed. `distros/_shared` does not touch the seed.

## Linux removal limited to the selected disks (issue #160)

**[Unit tests, L1]** `LinuxRemovalPlanner.Plan` takes the selected installations, the owned seeds, one partition layout and the firmware entries with the GPT partition their device path names (`EfiBootEntries.ParsePartitionGuid`). `LinuxRemovalPlanTests` (10 cases): removing the installation on disk 1 plans nothing on disk 0; a paired entry goes only when its device path points at the installation's disk; removing every installation deletes the entries on those disks and leaves an `ubuntu` entry that points at a third disk; a partition of an unknown type, or a Linux partition outside the removal, keeps the loader folders and the unpaired Linux entries; a partition that moved or changed type since System Check is not deleted and counts as a failure; an iGloo entry goes with its seed or when its partition no longer exists. 2,175 tests, 2,174 passed, 0 failed, 1 skipped; build `-warnaserror` clean.

**[Read in source]** `LinuxRemovalService.Remove` now refuses when the layout cannot be read, executes the plan, cleans loader folders only on the planned disks and restores the local-time clock only when that cleanup ran. `DeleteObject`, `SetFirmwareEnvironmentVariable` and the folder deletes are unchanged and still not covered by a test.

**Audit per provider (read in source).** Debian registers `debian` on the ESP of the installation's disk, its own ESP inside the iGloo region included (#227); `RemoveRedundantEfiPartitions` keeps its rules. Linux Mint registers `ubuntu` on the first ESP Ubiquity finds; when that is an ESP on another disk, the entry and its folder now stay. Fedora registers on the disk the kickstart binds. MBR Linux partitions have no GPT type and count as unknown, which also keeps the boot files in place.

## Read in source, not changed

- **Browser locks (B-03).** `CopyLockedChromiumDatabase` covers `SQLITE_BUSY`/`SQLITE_LOCKED`. A database Windows refuses to open (`SQLITE_CANTOPEN` → `BrowserInUse` in `LoginDataReader.ReadConnectionWithoutSharedMemory`), a non-database profile file held open for writing (`CopyFileAsync` opens the source with `FileShare.Read`) and Firefox profile files in the same position still raise `BrowserProfileStagingException`, which removes the generation and ends staging. Whether a running Edge or Chrome produces these today is not verified.
- **First-boot cleanup.** `cleanup_installer_partitions` in both agents only logs; neither removes the installer partition or the iGloo firmware entry. Fedora's seed keeps the copied user data.
- **Linux removal.** At `68670ad`, `WindowsPreflightChecker.BuildLinuxInventory` listed every partition labelled `OEMDRV`, `CIDATA` or `IGLOOISO` on any disk as a seed leftover, and `LinuxRemovalService.Remove` deleted the listed partitions by number; the label was the only criterion. Changed on this branch, see the sections on seed leftovers and on the Linux removal.
- **System Check.** `PreflightViewModel.RunCheckAsync` extends the largest lettered partition on the Windows disk into adjacent unallocated space of 64 MiB or more on every run, without a prompt (`LinuxRemovalService.TryReclaimFreedSpace`).
- **Debian free-region choice.** On the Community path the preseed runs in `fallback` mode: `igloo-bound-free-do-option.sh` exits 99 unless exactly one free region on the bound disk meets the provider minimum, and it has no ESP binding (no `igloo.linuxesppartuuid` token), so the `atomic` recipe decides about the ESP as in issue #227.
- **Linux Mint.** The preseed uses stock `biggest_free`; the Debian template states that this choice scans every disk.
- **Suppressions.** `#pragma warning disable` and `[SuppressMessage]` occur in `src/` and `tests/` (for example `SeedIdentityV1.cs`, `PartitionResizeService.cs`), which CONTRIBUTING rule 1 excludes. None was added.

## Not verified

Every installer, first-boot and boot-registration statement above is L0 or L1. The r6 runs in [[00 - Home/Beta Track]] remain the acceptance test; see also [[13 - Testing and Validation/Evidence Register]].
