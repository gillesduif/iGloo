# Fleet Phase 2A: recoverable execution foundation

Status: **implemented for deterministic fake adapters only; real execution remains disabled**.

## Shared observation extraction milestone — 2026-09-20

The reuse-audit extraction is implemented. This is consolidation of existing
Community observations, not completion of Phase 2B identity/readiness semantics.
The older feasibility assessments below remain historical records of host access.

Core now exposes narrow storage, BitLocker, firmware and BCD read interfaces.
WindowsStorageReader centralizes disk/partition/ESP enumeration, volume properties
and GetSupportedSize. Raw WMI property values and nulls are retained in local
property bags; failures remain explicit, including errors following partial
enumeration. They are not exact-target proofs or public Fleet evidence.

WindowsPreflightChecker, PartitionResizeService, DirectInstallService and
LinuxRemovalService consume the shared storage implementation. Their existing
compatibility conversions remain: physical-drive-number paths, unallocated
capacity, offset ordering, -1/zero/null fallbacks and per-caller selection rules.
Existing mutation services bind their selected WMI object path only within their
existing mutation flow; no write methods are exposed by the observation interface.
The shared size-query implementation retains the callers' null versus explicit
method-parameter behavior.

WindowsBitLockerReader owns the existing provider query and interpretation.
Community still queries C: and still maps missing/failed observations to Unknown.
No exact-volume BitLocker binding or lock-state claim was introduced.

FirmwareNative contains the single firmware read/write declarations and privilege
enablement implementation. Existing callers retain privilege-call placement and
their differing diagnostic policies. WindowsFirmwareReader exposes only reads
and retains native errors. DirectInstall's 256-byte BootOrder buffers and
EfiBootEntries' 4096-byte reads, scanning ranges, description matching and failure
fallbacks remain unchanged. Existing firmware writes were relocated to the common
native declaration without adding or invoking a write operation.

WindowsBcdReader exposes only fixed firmware enumeration. Raw results explicitly
remain Unparsed, CommandFailed or Unavailable. BcdListingParser contains the moved
stale-identifier parser; DirectInstall's compatibility method forwards to it.
Existing private BCD write execution remains in DirectInstall. The native
executable path resolver is shared; no generic command runner was introduced.

Community DI registers the shared readers, and existing constructor overloads
remain usable. Fleet's existing preflight composition reaches these same readers;
no alternative Fleet inspector or project reference was added. Phase 2A contracts,
journal and coordinator are unchanged. No WinRE, authorization, protected execution
state, target revalidation, recovery-readiness evaluator or gate was implemented.

Fourteen new characterization cases passed before observation extraction, followed
by fourteen shared-reader/compatibility cases. Coverage includes disk and partition
projections, BitLocker's C: query and failures, resize tie-breaking without an
NTFS/OS-volume filter, partial enumeration, firmware failure/scanning behavior and
unchanged BCD parser quirks. The solution now passes 352 tests with zero failures
or skips; Release build with warnings as errors passes with zero warnings/errors.
Restore, both real Windows read-only Fleet demonstrations, and git diff --check
also pass. New untracked source files were checked separately for whitespace.
No destructive demonstration, commit or push was performed.

Remaining overlap is outside this extraction: UsbWriter's removable-media
Win32_DiskDrive listing, existing private mutation plumbing, and unrelated GPU,
TPM/display/application observations. The next semantic milestone should enrich
these canonical providers and add strict Fleet projections around their results,
without replacing the Community compatibility mappings. Complete boot/recovery
feasibility remains subject to the documented read-access checks.

## Phase 2B: read-only host inspection blocked

### 2026-09-20 resumed feasibility inspection

The resumed task expected an elevated session, but the actual 64-bit tool process
reported `WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(Administrator)`
as **false**. No elevation was attempted. Read-only probes independently confirmed
that the required access is still unavailable:

- `bcdedit.exe /enum all /v`: the boot configuration data store could not be
  opened; access denied. Boot manager/loader identifiers, device references,
  bootsequence and display order could not be captured.
- `reagentc.exe /info`: requires an elevated command prompt; operation failed
  with error 5. WinRE status, configured location and recovery-partition binding
  could not be captured.
- Exact-volume BitLocker was not re-probed after the stop condition. The prior
  provider access denial remains unresolved; no volume-bound readiness is claimed.
- EFI/NVRAM reads were not attempted after the stop condition. BootOrder,
  BootNext, Boot#### contents and their ESP correlation remain unverified.
- The temporary directory/ACL check was not reached. No ACL test directory was
  created and protected-state feasibility remains unverified.

Implementation stopped immediately after these access failures. Phase 2B remains
unimplemented. This is an observed process-permission limitation, not evidence
that Windows or this firmware cannot provide the necessary read interfaces.
Successful storage inspection from the prior session does not substitute for
missing boot/recovery evidence.

The next session must verify elevation in the **actual command/tool process**,
then repeat read-only feasibility checks. Proposed BCD probes are
`bcdedit.exe /enum all /v` and `/enum firmware /v`; these enumeration commands
do not modify BCD, but successful enumeration must still be evaluated for complete
snapshot coverage. WinRE inspection uses `reagentc.exe /info`. Neither command's
output has yet established an exact recoverable snapshot in this task. Firmware
read access, exact-volume encryption correlation and ACL read-back still require
their own successful checks. No production-directory, authorization, gate or
adapter code was added, and no boot/storage mutation was performed.

The requested post-inspection verification was rerun on 2026-09-20: restore
passed; Release build passed with zero warnings/errors; all 324 tests passed
with zero failures/skips; Phase 1 enrollment/planning/restart persistence and
Phase 0 read-only assessment demonstrations passed; git diff --check passed.
Only this assessment document changed among tracked files. Verification produced
its normal ignored build/test artifacts; no feasibility-test directory or
production execution state was created. No commit or push was performed.

### 2026-09-19 initial assessment

Phase 2B was attempted on 2026-09-19. It is **not implemented**. The initial
read-only capability probes hit the requested stop conditions before any source
code changes: this process is not elevated and cannot read the required boot
configuration or obtain volume-bound BitLocker evidence. Phase 2A remains the
implemented baseline; the findings below are diagnostic observations, not a new
adapter, execution authorization or readiness result.

| Read-only probe | Observed result | Implication |
|---|---|---|
| Get-Disk / Get-Partition | GPT disk GUID, hardware unique ID/serial, partition GUIDs, numbers, geometry, GPT types, access paths and boot/system roles available on the Windows disk | Stable target identity appears representable; full volume binding and revalidation remain unimplemented |
| Additional attached disks | MBR layouts without GPT disk/partition GUIDs | Must be explicitly Unsupported by the proposed GPT identity adapter, never inferred from disk numbers or drive letters |
| bcdedit.exe /enum firmware /v | Exit 1: boot configuration store access denied | Exact BCD/Windows boot manager snapshot cannot be proven in this session |
| reagentc.exe /info | Exit 5: elevated command prompt required | WinRE configuration and resolvability remain Unknown |
| Win32_EncryptableVolume through root/CIMV2/Security/MicrosoftVolumeEncryption | Access denied | BitLocker state cannot be associated with the exact target volume in this session; remains blocking Unknown |

Hardware serials and volume identifiers are intentionally not copied into this
public document. The probes did not select or authorize a mutation target. No
partition changes, boot writes, firmware writes, BitLocker changes, elevation,
reboot or Linux installation were attempted.

The seven-step Phase 2B demonstration stopped during initial inspection. No exact
boot snapshot, authorization, fresh plan revalidation or pre-commit gate result
was fabricated. Protected-directory ACL verification and durable authorization
consumption have not been implemented or tested for Phase 2B.

The next prerequisite is an explicitly available elevated Windows inspection
session in which the same read-only BCD, WinRE and volume-specific BitLocker
queries succeed. Firmware-variable and EFI read access, complete snapshot scope,
and protected-state ACL semantics must then be verified before continuing; an
elevated token alone does not establish those properties. Community changes or
destructive host operations are not needed to resolve the current access blocker.

Phase 2C remains blocked by all unimplemented Phase 2B requirements: exact fresh
plan/target/evidence binding, proven recovery readiness, complete deterministic
boot capture, protected immutable local artifacts, durable single-use
authorization and a read-only pre-commit gate. Real partition mutation, boot
mutation/restoration, reboot execution, Linux completion receipts and production
execution endpoints remain disabled.

Baseline verification after these probes passed: solution restore; Release build
with warnings as errors (zero warnings/errors); all 324 tests (zero failures or
skips); Phase 1 enrollment/planning/restart-persistence demonstration; Phase 0
actual Windows assessment demonstration; and git diff --check. No tests were
added and no source/project dependencies changed. Verification logs are ignored
local `test-logs/phase2b-*` artifacts. No commit or push was performed.

## Architecture and scope

Core now defines immutable storage identities, exact before/after states, typed
inspect/apply/verify contracts, boot snapshots and restoration contracts. Storage
identity includes hardware identity, disk GUID, partition GUID and partition
number. Geometry, filesystem and label participate in exact value comparison;
a disk ordinal alone is rejected. Boot snapshots use a versioned, explicit scope
and immutable canonical content. A future adapter must define that format and
prove its completeness; the fake format does not describe real Windows state.

Fleet.Domain defines correlated, versioned journal records and the journal
interface. Fleet.Persistence implements a separate SQLite execution journal,
with append-only events, contiguous per-operation sequences and immutable intent.
UPDATE and DELETE are rejected by database triggers. Commits use synchronous FULL.
Records carry ExecutionId, Prepared PlanId, device/Agent identity, approval,
evidence ID/hash, profile revision, operation ID, exact input states, UTC time,
observed state and verified receipt or failure classification. These are local
records, not network execution authorization or signed success attestations.

Fleet.Agent implements the generic recovery coordinator. It has no host
registration, CLI switch or HTTP endpoint. The deterministic fake adapters live
in the Fleet test project; they cannot be selected by the production Agent.
Their machine state is independent of the SQLite journal and survives coordinator
and adapter reconstruction in tests.

All project references and Community behavior remain unchanged. Persistence uses
a generic intent type, so Domain and Server do not gain a Core execution dependency.
The existing planning database and protocol remain unchanged. Prepared plans
remain non-executable. No production adapter, real disk/boot action, generic
remote command, reboot or Linux installation is enabled.

## Durable lifecycle and recovery

A single canonical local journal path is required per endpoint. The coordinator
holds an OS-enforced exclusive file handle across inspection, durable intent,
apply, verification and receipt. A competing coordinator fails closed with an
I/O error; it does not wait on or steal an expiring lease. Process termination
releases the handle. The persisted intent prevents a later caller from applying
the same operation again. Operation IDs must be stable across retries. This is
at-most-one apply attempt per operation, not a claim of exactly-once hardware
execution. A future authorization layer must prevent minting a second operation
ID for the same approved mutation.

The first call validates immutable correlation and target facts, inspects actual
state, and commits Intent before invoking the adapter. A mismatched or uncertain
initial state records RecoveryRequired without applying. A successful apply is
followed by a separate inspection and independent VerifyAsync read-back. Only
an exact, uncertainty-free authorized after-state yields a VerifiedReceipt.
A rejected-before-mutation result plus independently observed unchanged state can
produce FailedWithoutMutation. An exception alone never establishes that result.
Ordinary apply errors record ApplyInterrupted and trigger inspection. Cancellation
or simulated process loss propagates, leaving the durable intent for recovery.

RecoverAsync loads authority from the journal, reacquires the exclusive guard,
and inspects actual state before interpreting completion:

| Actual evidence | Appended record | Returned outcome |
|---|---|---|
| Exact expected before-state | ObservedNotApplied | NotApplied |
| Exact authorized after-state and independent verification | VerifiedReceipt | AppliedAndVerified |
| Neither state provable, unavailable inspection or failed verification | RecoveryRequired | AmbiguousRecoveryRequired |

Recovery and repeated calls **never apply**, even when the state is NotApplied.
An old receipt cannot override current drift. After an external recovery, a later
inspection may establish an exact state; this appends new evidence without erasing
the prior ambiguity or retrying the mutation. A new authorization/resolution
workflow is deliberately not implemented. Intent-write failure prevents apply;
receipt-write failure leaves intent available for read-back recovery.

Boot restoration is a separate journaled operation using BootRestorationAdapter.
It requires an Exact captured snapshot matching the authorized destination and
scope. Partial/unsupported snapshots are rejected before restoration. Restoration
gets independent verification just like forward mutation; partial actual state
remains recovery-required. No automatic rollback is attempted.

## Safety invariants and limits

- Never authorize by disk number or rely on an adapter exception as proof of no effect.
- Persist intent before any attempt, and persist a receipt only after independent read-back.
- Never overwrite journal history or change correlation/geometry under an operation ID.
- Never replay an existing operation, including one interrupted before apply.
- Never trust a historical receipt over freshly observed actual state.
- Never use Community FileStagingService's disposable staging directory for this journal.
- Never map Linux `.done` markers or a zero exit code to a verified receipt.
- Never interpret the fake adapter's results as real host evidence.

The journal constructor requires an explicit path; no runtime path is registered.
Tests use isolated temporary directories. Phase 2B must select a protected,
non-staging local path, enforce endpoint ACLs and one canonical journal location,
and establish disk flush/filesystem assumptions. Database triggers are not a
security boundary against an administrator replacing or editing the database.
Missing/corrupt history requires recovery; callers must not recreate authority
under a fresh ID. Record versions and sequence/authority inconsistencies fail
closed. The generic API is an internal building block, not a permission boundary.

## Non-destructive test coverage

Deterministic tests cover durable ordering, successful receipts, process loss
before mutation, partial mutation, after mutation before receipt, and after
verification before journal completion. They reconstruct the journal and adapters
from the same SQLite file while retaining independently modeled machine state.
Additional cases cover ordinary exceptions before/after effects, intent/receipt
write failures, geometry drift, uncertain observations, immutable authority,
sequence rejection, exclusive coordination, repeated calls, ordinal-only target
rejection, boot mutation/restoration crashes, exact restoration and unsupported
or partial snapshots. Existing architecture tests enforce unchanged Community
and Fleet dependency boundaries.

## Verification

The baseline was 301 passing tests. Phase 2A adds 23 tests, for **324 passing,
zero failed and zero skipped** (Fleet: 64; all other project totals unchanged).
Restore and the Release warnings-as-errors build pass with zero warnings/errors.
The Phase 1 read-only Windows demonstration passes, including enrollment,
assessment/dry-run, explicit approval, Prepared plan and persistence after Server
restart. The Phase 0 demonstration also passes with actual Windows preflight
submission/retrieval. git diff --check passes; new untracked files were checked
separately with git diff --no-index --check and have no whitespace errors.
Logs remain under ignored test-logs/phase2a-* files.

## Remaining prerequisites for Phase 2B

1. Define and verify a real Windows identity/geometry observation format, including
   unambiguous target selection, filesystem facts, sector alignment and unsupported layouts.
2. Decompose canonical shared execution primitives into independently verifiable
   steps without silently changing Community. Do not wrap/replay the monolithic preparation call.
3. Implement complete BCD/EFI/NVRAM snapshot, verification and restoration support;
   explicitly reject unsupported or partial recovery. Prove this on disposable targets.
4. Add fresh exact-target BitLocker/recovery readiness checks, immutable artifacts,
   protected journal deployment and single-use execution authorization bound to
   the approved plan/evidence. Planning approval alone remains insufficient.
5. Establish authenticated execution-correlated Linux receipts and cleanup ordering.
6. Obtain explicit disposable VM/hardware authorization before any destructive tests.

## Historical real-adapter readiness findings

The following baseline findings still block wiring real execution. They do not
block the isolated Phase 2A foundation implemented above.

## Stop condition: interrupted partition preparation is ambiguous

[IDirectInstallService](../../src/Igloo.Core/Abstractions/IDirectInstallService.cs)
exposes one `PrepareAsync` operation followed by `RegisterBootEntryAsync`.
Inside [DirectInstallService](../../src/Igloo.Preflight/DirectInstallService.cs),
preparation can remove an old installer partition, resize a partition, create
several partitions and write installer artifacts in the same call.

The concrete interruption window is between the resize call at line 279 and
installer partition creation at line 284. Recovery currently detects a reusable
installer volume by label/size (line 228), not an execution receipt. If the process
dies in that window, retrying preparation re-enters the resize path without a
durable record proving the preceding mutation completed. This is a code-path
finding; no destructive retry was performed to reproduce it.

[PartitionResizeService](../../src/Igloo.Preflight/PartitionResizeService.cs)
accepts a disk number and allocation amount. It selects the candidate with the
largest reported shrink allowance (lines 107–136), calculates a new size from a
fresh probe (line 64), and invokes Resize (line 85). It does not accept an exact
partition identity, expected before-state, authorized after-state or execution
ID, and does not return a durable verified mutation receipt.

Phase 1's [planner](../../src/Igloo.Fleet.Agent/ReadOnlyPlanner.cs) observes the
boot partition, while the resizer may select a different candidate on the same
disk. A Fleet wrapper cannot treat those as the same authorized target.

An outer journal recording only "Prepare started/completed" cannot satisfy the
requirement to journal and verify every mutation or determine a safe retry after
a crash. This does not establish that the engine can never be decomposed; it
establishes that its present public contracts are insufficient for safe Fleet
resume. A shared-engine contract change and recovery tests must precede an adapter.

## Stop condition: boot recovery and restart cannot be proven

DirectInstallService keeps installer drive, disk and partition information in
in-memory fields (lines 94–103). Boot registration requires those fields and
fails without them (lines 1308–1311). There is no transaction-bound restore API.
Re-running all preparation just to rebuild those fields is not a safe recovery
strategy.

Boot registration writes Boot####, BootNext, BCD entries, BootOrder and RTC
configuration. Its BCD path is explicitly best effort; nonzero command exits are
logged rather than returned as a typed failed step (line 1568). The combined call
does not produce an exact before/after backup or verified restoration receipt
covering all these state changes. Successful return cannot stand in for verified
boot-state recovery.

[LinuxRemovalService](../../src/Igloo.Preflight/LinuxRemovalService.cs) is a
separate removal/reclamation workflow. It does not consume an execution journal
to restore the exact pre-migration partition and boot state. It must not be
advertised or invoked as automatic Fleet rollback.

## Additional execution prerequisites discovered

### Recovery and BitLocker observations

The inspected shared contracts contain no RecoveryReadiness result proving WinRE
availability, a known-good Windows boot entry, saved BCD/EFI state, verified
partition snapshot and recoverable local manifest. Recognizing a recovery
partition or having removal code does not prove recoverability.

[WindowsPreflightChecker](../../src/Igloo.Preflight/WindowsPreflightChecker.cs)
queries BitLocker for `C:` (lines 480–513) and returns Unknown on missing/failed
observations. It does not bind recovery evidence to the exact execution volume.
The real baseline returned Unknown. No suspension or decryption was attempted;
that observation must block execution. This is an endpoint observation, not a
claim that BitLocker can never be reliably checked on a configured test machine.

### Durable local artifacts

[FileStagingService](../../src/Igloo.Migration/FileStagingService.cs) uses a
per-distro staging directory and deletes a previous staging directory on entry
(lines 36–44). Fleet cannot store its recovery journal there and blindly reuse
the existing staging call on restart. Execution needs an explicit immutable
artifact set, a protected transaction directory and hash-aware resume behavior.

[IsoAcquisitionService](../../src/Igloo.Iso/IsoAcquisitionService.cs) already
provides canonical HTTPS/checksum/signature validation and should remain the
source of installer acquisition. Its success alone does not prove that the
later copied boot artifacts match the approved execution.

[UsbWriterService](../../src/Igloo.UsbWriter/UsbWriterService.cs) performs raw
media writes and partition-table work. It is not a safe fallback when direct
installation recovery is ambiguous and was not invoked.

### Linux correlation and completion

[MigrationManifest](../../src/Igloo.Core/Models/MigrationManifest.cs) remains
private local execution state. The inspected handoff has no Fleet ExecutionId,
PlanId, profile-revision and original-manifest-hash receipt linking a first boot
to a specific authorization.

The [Debian-family agent](../../distros/_debian-family/agent/agent.py) collects
step exceptions but returns zero after the loop (lines 2652–2663). Its
[first-boot launcher](../../distros/_debian-family/agent/first-boot.sh) writes
`.done` after invocation without requiring successful post-boot validation.
The [Fedora launcher](../../distros/fedora-kde/agent/first-boot.sh) also writes
its done marker after logging an Agent failure. These markers prevent reruns;
they are not proof that migration succeeded.

Fleet must not map a zero exit code, `.done`, or "Linux booted" to Completed.
An execution-correlated receipt must report required step and post-boot results,
retain failure/unknown states, and survive manifest redaction and seed cleanup.
That receipt also needs an explicit trusted reporting path without copying the
Windows Agent private key or operator credential into installer artifacts.
