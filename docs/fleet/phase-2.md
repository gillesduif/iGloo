# Fleet Phase 2A: recoverable execution foundation

Status: **implemented for deterministic fake adapters only; real execution remains disabled**.

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
