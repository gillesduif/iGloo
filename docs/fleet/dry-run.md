# Read-only dry-run, eligibility and preparation

The Agent pulls one strongly typed work item per invocation. Work includes ID,
assigned DeviceId/AgentId, type, Server creation/not-before/expiry, status, attempt
count, correlation ID, payload version and exact profile revision snapshot.
Assignment requires explicitly advertised capabilities; Agent version alone is
not sufficient.

Claims are transactional leases: five minutes, at most five attempts, one owner
identity. Another Agent cannot claim or submit it. An expired lease may be
reclaimed; an old lease's conflicting response is rejected. Work lifetimes are
1–1,440 minutes, at most 20 outstanding items per device. Completed work is never
reissued. Late evidence under the still-current lease can remain durable history,
but expired work is not approvable.

## What actually runs

RunAssessment calls the existing IPreflightChecker.
RunMigrationDryRun calls that same checker and IDistroPlugin.CheckCompatibility,
reads local plugin/catalog bytes for provenance, and compares read-only disk
capacity observations to profile/plugin requirements.

No rendering, ISO download/staging, partition modification, BitLocker change,
filesystem/user-data writes, boot modification or reboot occurs. The only writes
are Fleet identity, protected keys, logs and sanitized spool metadata.

The system disk must be unambiguous. Shared Preflight returns zero both on failed
GetSupportedSize queries and zero shrink capacity. When there is insufficient
unallocated space and a zero shrink observation, capacity is Unknown. A measured
nonzero capacity below requirements, or a physical disk smaller than required,
is Blocked. Disk sizing remains a best-effort planning observation, not final
geometry validation.

The real-machine demo found BitLockerUnknown and DiskSelectionUnknown (sizing
could not establish capacity). It produced NeedsReview, and its recorded manual
approval explicitly limits the artifact to non-executable planning.

## Explainable rules v1

| Status | Meaning |
|---|---|
| Eligible | Required collected checks passed; no modeled review reasons |
| NeedsReview | Known uncertainty/warnings need an explicit operator reason |
| Blocked | A known preflight, target, hardware, Secure Boot or capacity blocker |
| Unknown | Required planning evidence absent or local observation failed |

Reasons are fixed enum codes: PrecheckFailed, BitLockerUnknown, InsufficientSpace,
UnsupportedDistro, SecureBootRequirementNotMet, HardwareUnsupported,
CompatibilityReview, MissingEvidence, LocalReadFailure, DiskSelectionUnknown.
Unknown never becomes Eligible. The rule version and original decision are stored.

Raw local findings are mapped to categories; their free-form text is not uploaded.
Malformed/inconsistent evidence is rejected, not treated as a negative-but-valid
assessment. Local read exceptions become sanitized Unknown evidence with no
exception text. Cancellation propagates.

## Offline results

ResultSpool writes each completed result before transmission. Up to 100 files,
32 KiB each, are retained under Agent data/outbox; a full spool fails closed.
Atomic publish, flush and crash recovery preserve completed records. Submission
retries temporary HTTP failures with one- then two-second backoff; after that the
process exits and the next invocation retries the spool before claiming new work.

Only acknowledged results are removed. Conflicts/expired leases remain for
operator recovery and may block later work; no automatic evidence destruction or
identity reassignment occurs. A local exclusive Agent lock prevents concurrent
processes from racing the same identity/spool. Phase 1 is one-shot/pull-based, not
a Windows service or an indefinitely retrying daemon.

## Approval and staleness

An approval requires exact DryRunId, ProfileRevisionId, EvidenceHash and DecisionId.
The Server records actor, reason, UTC creation and expiry (four hours).
NeedsReview requires a nonempty reason (up to 500 characters); original reasons
remain visible. Unknown and Blocked cannot be normally approved.

Any newer accepted assessment/dry-run for that device supersedes earlier approvals
conservatively. A new profile revision, changed Agent/Core versions/capabilities,
disabled/revoked identity, work/evidence expiry or approval revocation also
invalidates preparation. Retrying identical evidence does not invalidate approval.
A later operator cannot accidentally prepare against stale latest-device state.

## PreparedMigrationPlan

Preparation is transactional and idempotent for an approval. It contains PlanId,
DeviceId/AgentId, profile revision, dry-run, decision, approval, evidence hash,
Server times, Core version, exact distro/plugin hash and a fixed non-executable
safety boundary. Its stored inputs never change. Validity status can become
Expired, Superseded or Invalidated with audit history.

There is no executable command, migration manifest, partition recipe or execution
authorization. Creating or reading a plan performs no endpoint action.
Phase 1 ends at Prepared. Recovery verification, fresh disk/BitLocker checks and
a separately reviewed execution protocol are prerequisites for any future phase.
