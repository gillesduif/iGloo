# Migration profiles

A profile revision describes a **read-only system-disk migration feasibility
assessment**, not a MigrationManifest or execution recipe.

Schema 1 fields:
- Name: 1–80 characters.
- DistroId: exact existing plugin identity: debian, fedora-kde or linuxmint-cinnamon.
- MinimumAvailableBytes: 20 GiB to 1 TiB. The planner also applies the plugin's
  declared minimum disk requirement, taking the greater value.
- RequireSecureBoot: if true, a false preflight observation blocks the plan.

The planner identifies the single disk containing Windows' boot partition.
No drive path, remote script, ISO staging destination or secret migration setting
can be supplied. A candidate is not final execution disk authorization.

Ubuntu is still marked in-development upstream and is explicitly rejected for
Phase 1 profiles. Missing plugins on an Agent produce UnsupportedDistro rather
than falling back. There is no invented distro version field: current plugin
metadata does not expose a dependable version contract. Evidence instead records
a SHA-256 of the actual local plugin binary plus catalog JSON.

User-data/browser/Wi-Fi/application migration and recovery policies are not
configurable because a complete safe planning representation is not yet available.
Nothing claims those migration features have been evaluated. Full recovery
readiness and target geometry require a future execution review.

## Revisions

Creating a profile assigns ProfileId, RevisionId, Revision=1, SchemaVersion=1 and
Server UTC creation time. Revision updates append a new RevisionId with the next
number. Every revision is immutable from creation, even before first use.

Work stores the exact revision snapshot. Old revisions remain queryable. Creating
a revision supersedes approvals/plans for earlier revisions and older work can
never produce a currently approvable plan. A new dry-run is required.

See [commands](phase-1.md) and [dry-run/approval semantics](dry-run.md).
