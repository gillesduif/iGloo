# Phase 1 persistence and evidence

Trusted mode uses Microsoft.Data.Sqlite 8.0.4, already used by the repository's
migration project. No framework or existing dependency version was upgraded.

Default database:
%LOCALAPPDATA%/Igloo/FleetServer/fleet.db

IGLOO_FLEET_SERVER_DATA selects a different private directory. Initialization and
normal startup run deterministic schema migration. Schema 1 creates fleet_state
and sets PRAGMA user_version=1 inside a transaction. Unknown newer schema versions
fail startup; no delete/recreate fallback exists.

## Storage boundary

IPlanningStore is a Domain transaction/snapshot abstraction, independent of SQLite.
SqlitePlanningStore implements it. Schema 1 stores the transaction-scoped
PlanningState aggregate as JSON in a singleton row. This deliberately favors
atomicity and a small implementation for a single-node pilot over premature
relational normalization. All reads deserialize isolated state.

The aggregate includes token metadata, device/certificate identity, heartbeat,
immutable profile revisions, leased work, historical assessment/dry-run evidence,
decisions, approvals, plans and append-oriented audit. Evidence transport payloads
are embedded inside historical evidence records, not mutable current-device blobs.
Normal service methods never overwrite old evidence or profile revisions.

Immediate SQLite transactions serialize token consumption, claims, idempotency,
approval and invalidation across connections/processes. Exceptions roll back.
Readers see committed snapshots. No distributed lock or message broker exists.
The connection timeout is 30 seconds and connections are not pooled.

This format rewrites the aggregate for a write, so throughput and audit volume
are limited. Normalize/index records behind IPlanningStore before larger pilots;
do not mistake this initial adapter for a production high-volume database.
There is no automatic retention deletion in Phase 1. Plan backup/retention policy
before long-running deployment. Back up the stopped Server directory and Windows
principal/profile required to decrypt its DPAPI keys.

Phase 0's in-memory stores remain only in the explicit development mode; they
contained no durable database needing migration. Trusted mode never falls back to
them when SQLite fails.

## Evidence provenance and hash

Each DryRunEvidence retains the exact ReadOnlyWorkResult, Server receipt time,
capability snapshot and versioned decision. The referenced WorkItem contains the
full immutable profile revision and correlation ID. Nested assessment records
retain DeviceId/AgentId, agent/Core versions, preflight schema and generated times.
Control-plane times are Server UTC; generated assessment timestamps must be UTC
and plausible relative to the work request.

EvidenceIntegrity schema 1 sorts JSON object property names ordinally, preserves
array order, writes .NET JSON primitive representations without whitespace, then
computes SHA-256. The evidence hash covers:

```text
{ Result, Profile, Capabilities, RulesVersion: 1 }
```

Those inputs remain available in evidence/work history. This is a specified .NET
canonical format, not an assertion of RFC 8785 compatibility. A new encoding or
ruleset needs a new version. An identical retry returns the existing record and
hash; a changed payload for the same work ID conflicts.

Approvals and plans bind exact evidence hash, decision ID, identity and profile
revision. Changes never rewrite old decisions using new rules. Plan status may
change through audited invalidation; the original planning inputs remain fixed.

## Audit and restart verification

Audit records event ID, Server UTC time, actor type/ID, action, target and correlation
ID. Tokens/keys, raw manifests and raw exceptions are excluded. Significant
security/planning actions and input-driven invalidation are recorded.

Tests reopen isolated databases and verify devices, evidence/hash, profiles,
approvals, plans and audit. The real demo stops/restarts the Server and compares
the same evidence hash, revision and Prepared plan. The initial migration is
idempotent and never wipes existing history.
