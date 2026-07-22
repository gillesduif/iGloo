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

VALIDATED 2026-07-21 on the Mint VM (staged by the new build): firmware entry
"Boot0080* iGloo distribution installer" was matched case-insensitively and
deleted by the agent (log: "Removed stale UEFI boot entry Boot0080"); Windows
Boot Manager and the distro's own entry survived; agent finished with
0 failures. Post-reboot checks confirmed: OEMDRV partition gone from lsblk,
manifest password redacted, no agent re-run (.done guard), Windows boots
normally from GRUB. Validation gate CLOSED for the Mint/Debian-family path.
Checklist gotchas found while validating, for the docs backlog: on Mint the
unit is igloo-bootstrap.service (not igloo-first-boot.service) and the logs
are bootstrap.log + agent.log (not first-boot.log) - the README's
"Verifying an install" snippet documents only the Debian d-i names.

### 3. UEFI fallback-path loader (bare-metal Gigabyte/AMI boot failure) - FIXED
Bare-metal Mint attempt 2026-07-21 on a Gigabyte B650 AORUS ELITE AX V2
(AMI BIOS F33a, Secure Boot off): the Windows pipeline completed flawlessly
(shrink, OEMDRV carve, kernel/initrd extract, preseed inject, full ISO copy,
grub.cfg to all prefixes, Boot0080 + BootNext written) but the machine rebooted
straight into Windows. `bcdedit /enum firmware` afterward showed Boot0080 GONE
and displayorder reset to `{bootmgr}` only: this firmware class silently PURGES
OS-registered Boot#### entries across a reboot. The one-shot BootNext safety
worked perfectly (clean fallback to Windows, no boot loop, staging intact).

Fix: `DirectInstallService.ConfigureBootFiles` now also stages the shim + grub
to the UEFI fallback path `\EFI\BOOT\BOOTX64.EFI` (+ grubx64.efi) on the OEMDRV
volume, which such firmware boots via its fallback scan / one-time boot menu
without needing an NVRAM entry. The change is ADDITIVE: the `\igloo-boot\` copy
and the NVRAM Boot#### entry are unchanged, so firmware that honours entries
(VMware, the Fedora reference hardware) boots exactly as before and the fallback
loader stays dormant. No boot-loop risk: the fallback path is never in BootOrder,
so a failed install still lands in Windows (BootOrder[0]).

Corrected a false code comment in the process: it claimed Windows write-protects
`\EFI\BOOT\` on FAT32 even for Administrator (the stated reason for staging to
`\igloo-boot\`). Verified untrue for a normal data partition we create and letter
by writing BOOTX64.EFI there directly; the real value of `\igloo-boot\` is just
that it is the NVRAM entry's target.

VALIDATION: Mint installed automatically on the Gigabyte board with this patch
(no F12) - the firmware auto-discovered \EFI\BOOT\BOOTX64.EFI on OEMDRV and
created a generic "UEFI OS" entry that booted it. Confirmed distro-agnostic
(shared pipeline).

### 3b. Boot entry now targets the standard fallback path - FIXED (follow-up)
Second field run: installing Fedora as a SECOND distro (Windows + an existing
Mint) on the same Gigabyte board booted straight back to Windows. `bcdedit
/enum firmware` showed why:
  - displayorder: Windows Boot Manager (1st), "ubuntu" (2nd, Mint's efibootmgr
    entry, which SURVIVES), "UEFI OS" -> J:\EFI\BOOT\BOOTX64.EFI (3rd, our
    fallback loader, auto-created by the firmware).
  - iGloo's own Boot0080 was gone again, and BootNext with it.
Two facts stand out: (a) a Linux-side efibootmgr entry survives while iGloo's
Windows-written Boot#### is pruned; (b) the pruned entry pointed at the private
\igloo-boot\shimx64.efi, whereas the target the firmware KEPT and even auto-
registered is the standard \EFI\BOOT\BOOTX64.EFI. Conclusion: this AMI firmware
prunes boot options whose device path it considers non-standard.

Fix: RegisterBootEntry now builds the EFI_LOAD_OPTION pointing at
\EFI\BOOT\BOOTX64.EFI (FallbackBootDir/FallbackBootFile) instead of
\igloo-boot\shimx64.efi. Same shim binary (ConfigureBootFiles stages it to both
paths), so no regression on firmware that already worked; but on pruning
firmware the entry now survives, so the BootNext to it is honoured and the
installer boots first instead of falling through to Windows. The BCD-firmware-
object route (bcdedit) was rejected: Windows cannot cleanly add a firmware app
entry to an arbitrary .efi the way Linux's efibootmgr can.

VALIDATION OPEN: must be tested in the FAILING scenario (Windows + an existing
Linux/boot entry present), not a fully wiped clean disk - a clean disk already
booted via the auto-"UEFI OS" path and would pass regardless, proving nothing
about this fix.

## Skipped for behavior risk (known issues, deliberately not fixed)

### 4. Sync-over-async inside the direct-install worker
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

The loose `Igloo.Distro.*.dll` files and `bin/`/`obj/` under `distros/` are
git-ignored build outputs, not tracked files. EXCEPTION discovered late: the
`distros/*/agent/__pycache__/*.pyc` files ARE tracked in git (predating the
ignore rules) and were re-committed in sync with their sources; the hygiene
pass should `git rm --cached` them and extend `.gitignore`. The App csproj's
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
