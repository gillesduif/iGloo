# Fleet Phase 1 implementation report

Verified on Windows, 13 September 2026.

## Result

Fleet now supports trusted enrollment, durable read-only assessment and migration
planning, exact-evidence approval and a non-executable PreparedMigrationPlan.
The real Windows demonstration completed enrollment, assessment, dry-run, review,
approval and preparation, then restarted Server and verified the same stored
inputs. The full solution passes 301 tests and a warning-free Release build.
Community and the separate Phase 0 development demonstration remain operational.

## Repository changes

Phase 1 was implemented on the then-uncommitted Community/Fleet Phase 0 foundation;
it preserved that rename and the shared engine. The project-reference
graph remains the [baseline graph](phase-1-baseline.md):

```text
Community.App -> Core, Preflight, Iso, Migration, UsbWriter
Preflight, Iso, Migration, UsbWriter, distro plugins -> Core
Fleet.Agent -> Contracts, Domain, Core, Preflight
Fleet.Domain -> Contracts
Fleet.Persistence -> Domain
Fleet.Server -> Domain, Persistence
Fleet.Web -> Contracts
```

New components include PlanningProtocol v2, transactional PlanningService,
EvidenceIntegrity, SqlitePlanningStore, FleetCertificateAuthority, trusted HTTPS
Server/Agent modes, ReadOnlyPlanner, ResultSpool and the engineering operator CLI.
No Phase 1 changes were needed in Community or shared migration implementations.
Framework targets and existing package versions were retained; Fleet uses
Microsoft.Data.Sqlite 8.0.4 and ProtectedData 8.0.0, matching existing repository
versions. Operational documentation starts at [phase-1.md](phase-1.md).

## Trusted enrollment

An operator creates a cryptographically random 256-bit token with a 1–60 minute
lifetime. Only its SHA-256 hash and lifecycle metadata persist. It is shown once,
can be revoked, and is consumed atomically with enrollment. Concurrent/replayed,
expired or revoked use fails. The Agent creates its own RSA key and signed CSR;
the Server validates proof of possession and issues a fixed client-auth certificate
under its private CA, ignoring requested identities/extensions. Random Server
DeviceId/AgentId values and the full certificate hash establish durable identity.
Keys are protected by current-user Windows DPAPI and directory ACLs. Disabled
identities can be reenabled; Revoked identities cannot. Both invalidate approval
and preparation. See [enrollment.md](enrollment.md).

## Authentication

Agent and operator clients validate HTTPS hostname, validity, server EKU and chain
against an independently provisioned CA root. Agent API requests additionally
require a CA-issued client certificate and current database authorization.
Authorization scopes work/results/evidence to the authenticated identity, including
on existing connections after revocation. The separate operator authentication
interface initially uses a long shared engineering secret; Agent certificates
cannot authorize operator routes. Default binding is loopback, with explicit
matching hostname/listener configuration available for a pilot. There is no
production SSO, per-human RBAC, certificate renewal or CA rotation workflow.

## Durable persistence

SQLite schema 1 stores a JSON planning aggregate in a singleton row behind
IPlanningStore. PRAGMA user_version migration and writes are transactional; newer
unknown schemas fail rather than recreate the database. Immediate transactions
serialize token consumption, leases, evidence, approvals and invalidation.
Historical profile revisions/evidence are retained with audit events; there is no
automatic retention deletion. The real demo reopened the same database after
Server restart and compared profile revision, evidence hash and Prepared plan.
This adapter rewrites the aggregate on writes and is intentionally limited to a
small single-node pilot. See [persistence.md](persistence.md).

## Evidence model

Stored evidence includes device/Agent UUIDs, work/lease/correlation IDs, exact
profile revision, protocol/schema/rules versions, Agent/Core versions, capability
snapshot, timestamps, sanitized preflight categories, coarse inventory/capacity,
plugin/catalog hash and the versioned eligibility decision. Raw preflight text,
hostnames, user paths/files, disk labels, browser/Wi-Fi credentials, private keys
and MigrationManifest are excluded. SHA-256 covers the result, exact profile,
capabilities and rules version using ordinal-key canonical .NET JSON. Identical
retries return existing evidence; conflicting replacements fail. Audit records
actors, targets, correlation, safe action/error codes and invalidations. Hashes
are integrity identifiers, not protection against a malicious database owner.

## Migration Profiles

Schema 1 contains a name, supported distro ID, minimum available bytes and optional
Secure Boot requirement. Updates append numbered immutable revisions. Work embeds
the exact revision, and approval requires the current revision. Debian, Fedora KDE
and Mint Cinnamon use existing plugins; the unfinished Ubuntu pipeline is refused.
The binary/catalog hash pins the actual evaluated plugin because existing distro
metadata supplies no independent release-version contract.

## DryRun

Only RunAssessment and RunMigrationDryRun are accepted. The Agent pulls leased
work, invokes the existing WindowsPreflightChecker and, for dry-run, the existing
IDistroPlugin.CheckCompatibility hook. It evaluates sanitized hardware/preflight,
target compatibility, Secure Boot and conservative capacity observations. It does
not render installer artifacts, stage files or change partitions, boot state or
encryption. A bounded durable spool precedes submission; retries are idempotent.
Safety tests inspect project/assembly dependencies and forbidden execution call
sites, and planner tests exercise real distro compatibility headlessly. This is
code-boundary evidence, not OS-level tracing or a sandbox for malicious plugins.

## Eligibility

Eligible requires complete passing evidence. NeedsReview captures warnings or
specific uncertain probes and requires an explicit review reason. Blocked covers
known failed prechecks, unsupported target/hardware, measured insufficient space
or unmet Secure Boot requirements. Unknown covers failed local reads or missing
planning evidence. Neither Blocked nor Unknown can be approved. Unknown probes
never produce Eligible. Fixed reasons include PrecheckFailed, BitLockerUnknown,
InsufficientSpace, UnsupportedDistro, SecureBootRequirementNotMet,
HardwareUnsupported, CompatibilityReview, MissingEvidence, LocalReadFailure and
DiskSelectionUnknown.

The real Windows Debian dry-run returned NeedsReview with BitLockerUnknown,
CompatibilityReview and DiskSelectionUnknown. RAM was about 31 GiB with three
disks. Capacity remained null because shrink sizing was inconclusive; a small
unallocated region was not treated as the total possible capacity. The operator
explicitly approved planning with future revalidation required.

## Approval

Approval binds identity, exact dry-run ID, decision ID, evidence hash and profile
revision. It requires fresh current evidence and unexpired work. New evidence or
profile revision, changed Agent/Core versions or capabilities, disable/revoke,
explicit approval revocation or expiration invalidates preparation. Approval lasts
at most four hours; work/evidence freshness can invalidate it sooner. Historic
inputs remain intact with audited validity transitions.

## PreparedMigrationPlan

The final real demonstration produced these conceptual inputs:

```text
PlanId: fd9c3d24-5098-41f5-8747-6f05a00ac43e
DeviceId: c37cd20c-c478-4ba3-b847-b41479b359ae
ProfileRevisionId: 5b84c9e1-0bd6-461a-acf0-741d2ee0f797
DryRunId: 571b1abb-20e4-4159-bfe6-88b229a037ef
DecisionId: 0ed770b1-9487-491f-af21-235a90221686
ApprovalId: 20fa0319-0630-4429-879c-1e3327b8fb24
EvidenceHash: 5BFA0842753A4F1E64F07C805F3C73937795392C4FB82E3F4641658B9AD345D1
DistroId: debian
IglooVersion: 1.0.0.0 (shared Core assembly version)
Status at verification: Prepared
Safety: Non-executable planning artifact. Recovery readiness and fresh
        execution authorization required in a future phase.
```

The actual record also retains AgentId, plugin hash and creation/expiry times.
It has no execution payload or consumer in Phase 1. Recovery readiness is a future
prerequisite, not a verified property of this machine. Validity is time bounded;
the example is historical evidence of preparation, not current authorization.

## Security/privacy

Protections include TLS/mTLS, independent Server trust, single-use hashed tokens,
CSR validation, per-request revocation, separate operator authorization, protected
keys/directories, strict bounded JSON, ownership checks, sanitized evidence,
bounded spooling and audited exact-input approval. No generic command endpoint
exists. TLS handshake failures remain transport diagnostics; application-level
denials are audited. Database contents are ACL-protected but not encrypted.
Operator profile names/review reasons are deliberately stored and must not contain
secrets. See [security.md](security.md) for threat handling and limitations.

## Tests

| Project | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Igloo.Core.Tests | 65 | 0 | 0 |
| Igloo.Preflight.Tests | 74 | 0 | 0 |
| Igloo.Iso.Tests | 19 | 0 | 0 |
| Igloo.UsbWriter.Tests | 23 | 0 | 0 |
| Igloo.Migration.Tests | 21 | 0 | 0 |
| Igloo.Community.App.Tests | 58 | 0 | 0 |
| Igloo.Fleet.Tests | 41 | 0 | 0 |
| **Total** | **301** | **0** | **0** |

Baseline: 278 passed. Final Release build with -warnaserror: zero warnings/errors.
Both real-machine demo scripts passed. git diff --check passed; Git emitted only
its existing LF-to-CRLF conversion notices. Logs are local ignored files:
test-logs/phase1-final-build.log, phase1-final-tests.log, phase1-final-demo.log and
phase1-final-phase0.log.

## Demonstration

scripts/test-fleet-phase1.ps1 first passes the execution-boundary tests, initializes
isolated protected CA/TLS keys and SQLite, starts HTTPS Server, creates/consumes a
token and enrolls the real Agent. It runs a real assessment and Debian dry-run,
records an explicit NeedsReview approval, prepares the plan above, stops/restarts
Server and compares persisted inputs. Agent child processes do not inherit the
operator secret. Processes are stopped and environment settings restored.
Artifacts are under test-logs/phase1-demo-8b77229dc0cb42c8b80255361ca2e331,
including verified-plan.json. Phase 0 independently submitted/retrieved real
assessment 3baeb41d-559b-4c5a-b38c-661bf247406f.

Intermediate failures were resolved before final verification: Windows Schannel
needed user-key-container certificate imports rather than ephemeral keys; initial
Server certificate serialization needed an exportable initialization import;
public PEM loading needed the public-only API; three nullable-long theory inputs
needed explicit long constants. The first planning demo exposed incorrect capacity
classification when shrink sizing returned zero. The planner now preserves that
ambiguity as null/NeedsReview while measured insufficient capacity still blocks,
with focused regression coverage. No final checks are failing.

## Deviations

The operational Web surface is an engineering CLI/API, not a browser frontend.
SQLite uses a transactional aggregate rather than normalized entity tables.
Agent execution is one polling cycle per invocation, not a Windows service;
persisted spooling survives invocations. Local trusted plugin binaries/catalog
hashes stand in for a distro release-version contract. Recovery prerequisites are
descriptive and no executable migration recipe is generated. These choices keep
the phase bounded; they are not claims of production deployment readiness.

## Remaining risks

The shared engineering operator principal has no per-human attribution/RBAC.
There is no attestation, HSM, tenant isolation, automatic renewal/CA rotation,
CRL/OCSP service, HA or distributed scheduler. SQLite aggregate rewrite cost and
unbounded retained audit require normalization and an explicit retention policy
before scale. DPAPI recovery depends on the original Windows principal/profile.
Compromised trusted local plugins or database/CA administrators are outside the
protection boundary. Inconclusive BitLocker/shrink probes still require review;
no execution readiness is established by accepting this planning artifact.

## Recommended Phase 2

Consider Controlled Single-Endpoint Execution only after a separate safety review
and pilot validation of trust/approval reliability. Specify fresh execution
authorization and revalidation, BitLocker handling, verified recovery readiness,
transaction logging, boot/reboot and installer handoff, post-boot verification and
failure recovery before adding any execution capability. None is implemented here.
