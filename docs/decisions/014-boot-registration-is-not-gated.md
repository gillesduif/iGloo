# ADR-014: Boot registration uses the field-proven NVRAM + bcdedit path, not the recovery planner

**Status:** Accepted
**Date:** 2026-10-25

## Context

Between v0.2-alpha and the beta branch, `DirectInstallService.RegisterBootEntry`
was replaced by a call into `CommunityRecovery` (`CommunityBootRegistrationPlan`,
`CommunityBootRegistrationExecutor`, `CommunityRecoveryBoundary`). That planner
refuses to write anything unless it can *prove* the complete mutation footprint
first: every element of every object in the BCD store parsed and semantically
understood, all 256 `Boot####` slots read with the exact Win32 error for absent
slots, variable attributes equal to 7, and no pre-existing firmware entry for the
target partition.

Observed on 2026-10-25 in the disposable VirtualBox lab (Debian, reproduced for
Mint and Fedora): preparation succeeded completely (NTFS resize, OEMDRV, initrd
injection, shim/GRUB, ISO copy, staging), then registration stopped with
`BootPlanDeleteReferencesUnavailable` → `CommunityRecoveryBoundaryException
(ScopeUnresolved)`. The trigger was a leftover iGloo BCD object from an earlier
run; deleting it required `BcdMutationPlanning.InboundReferences` over the whole
store, which fails as soon as one element (e.g. the WinRE RAM-disk object already
known from issue #242) cannot be fully interpreted. A clean machine passes once;
every retry fails. The planner also fails with
`BootPlanExistingTargetEntryRequiresOwnership` when the firmware has created its
own entry for the installer partition - exactly the Gigabyte/AMI behaviour the
alpha path was built to survive.

The gate also runs *after* the destructive steps (resize, partition creation), so
its "no boot changes were started" guarantee does not protect the step that
actually carries risk, and its own blocker list is ignored by
`ExecuteWithoutRecovery`.

## Decision

`RegisterBootEntry` is the v0.2-alpha logic again, adapted to the current
`CompletedDirectInstallPreparation` contract (geometry from the pinned canonical
volume identity instead of a second WMI query):

1. Raw UEFI NVRAM: write `Boot####` (target `\EFI\BOOT\BOOTX64.EFI`), choose
   `BootNext` (own entry, or the firmware's own entry for the same partition when
   one exists), prepend `BootOrder`, promote sibling entries for the partition.
2. Windows BCD, best-effort: remove stale iGloo entries by description, then
   `bcdedit /copy {bootmgr}` + `device`/`path` + `{fwbootmgr} bootsequence`.
3. `RealTimeIsUniversal=1`, non-fatal.

Only the first NVRAM write is fatal. Everything after it logs and continues.

The `CommunityRecovery` code stays in the tree and in its tests. It is reachable
for diagnostics (`PlanBootRegistrationForDiagnostics`) and for the reviewed/
recovery workflow, but it is **not** a precondition for registration on the
Community product path. The Core → CLI → Community/Fleet call chain is unchanged;
only the body of one method in `Igloo.Preflight` differs.

## Consequences

- Retries after a failed or aborted run work again (stale BCD objects are
  deleted, not treated as unprovable).
- Firmware that prunes OS-registered entries is handled as in the alpha.
- Recovery proofs for boot registration are an *additional* feature to build on
  top of a working registration, not a gate in front of it. If they return, they
  must run before the resize, not after staging.
