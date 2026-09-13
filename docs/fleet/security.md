# Fleet Phase 1 security and safety

## Authentication versus authorization

Trusted mode uses HTTPS with a private CA. Agent/Operator clients validate the
Server's hostname, certificate chain, validity interval and server-auth EKU against
the independently provisioned root. Redirects/proxies are disabled; HTTPS never
falls back to HTTP. Certificate validation is not bypassed.

Kestrel requests client certificates. Enrollment and minimal health are bootstrap
routes without a client certificate. Every /v2/agent route requires a valid,
non-CA, client-auth certificate and a unique durable certificate-hash mapping.
The authenticated identity scopes work claiming, result submission and evidence
reads. Payload identity never overrides TLS identity.

Every request checks Fleet status and expiry. Active may become Disabled or
Revoked; Disabled can be reenabled. Revoked is terminal. A valid TLS certificate
does not override a Fleet revocation, including on an already-established
connection. Revocation/disable invalidates approvals and plans.

Operator routes use IFleetOperatorAuthenticator. The initial engineering
implementation uses a separate, at-least-32-character operator secret over TLS
and the explicit actor engineering-operator. It never authorizes Agent routes.
A request carrying an Agent certificate cannot use operator APIs even with an
operator header. Agents are not given the operator secret; the demo explicitly
removes it from their process environment.

The default bind is loopback. An explicit IGLOO_FLEET_LISTEN_ADDRESS and matching
IGLOO_FLEET_TLS_NAME can configure a pilot network listener. Kestrel endpoint
configuration reload is disabled; an environment URL cannot silently add an HTTP
listener. Use a reachable HTTPS name matching the issued SAN and provision the CA
public certificate independently. No root is installed into the machine trust
store by the demo.

This follows the chain/custom-root and transport/authorization separation described
in [ASP.NET certificate authentication documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/certauth?view=aspnetcore-8.0).
The private CA has no CRL/OCSP service: chain validation uses NoCheck for external
revocation, while Agent revocation is enforced from Fleet's database on every
authorized request.

## Threat handling

| Threat | Phase 1 behavior |
|---|---|
| Stolen unused enrollment token | Short lifetime, one use, operator revocation and audit; possession still permits enrollment |
| Token replay/concurrent use | Transactional consumption permits exactly one enrollment |
| CSR spoofing/privilege escalation | Verify CSR signature; discard requested subject/extensions; issue fixed client-only usages |
| Stolen Agent key | Revoke identity; no hardware attestation protects a compromised principal |
| Hostname reuse | No identity or authorization decision uses hostname |
| Agent impersonating another device | TLS mapping and work/payload ownership checked |
| Agent accessing operator API | Separate auth branch rejects client-certificate requests |
| Tampered profile/work references | Exact leased work, correlation and immutable revision checked |
| Result replay | Identical result returns existing evidence; conflicting replacement fails |
| Stale approval | New revision/evidence, identity/version/capability changes, disable/revoke or expiry invalidates it |
| Network outage | Bounded sanitized spool, retry/backoff and idempotent Server submission |
| Malformed payload | Strict unknown-field rejection, enum/schema/size/identity validation |
| Server restart | Transactional SQLite history and protected CA/TLS keys persist |

TLS-handshake rejections are transport diagnostics. Application-level authorization,
enrollment and planning failures are safe audit entries with fixed error codes.
Neither request bodies nor credentials are logged by the implementation.

## Hard execution boundary

The only Agent work enum values are RunAssessment and RunMigrationDryRun.
There are no arbitrary commands, scripts, file destinations, ISO URLs or executable
payloads in work messages. Agent references only Contracts, Domain, Core and
Preflight. It registers/calls only WindowsPreflightChecker plus read-only plugin
compatibility. It never calls installer rendering, partition modification, file
staging, USB writing, boot APIs or reboot.

Phase1SafetyTests checks project/assembly references and forbidden service/call
sites before the real demo runs. Planner tests invoke actual distro compatibility
headlessly. This is a code-boundary regression check, not OS-level I/O tracing or
a sandbox against malicious local plugins. Distro binaries/configuration must
remain administrator-controlled trusted local code.

A PreparedMigrationPlan is an immutable-input planning artifact with an audited
validity status. No execution endpoint consumes it. Its recovery requirement is
a future prerequisite, not a claim that recovery has been verified.

## Privacy and known limits

Uploaded evidence contains UUIDs, timestamps, protocol/schema/rule versions,
capabilities, fixed preflight statuses/reason codes, RAM/disk count, a coarse
capacity observation and a hash of the selected plugin binary/catalog. It excludes
hostnames, user files, paths, raw finding messages, disk labels, GPU names, browser
or Wi-Fi secrets, private keys and MigrationManifest. Operator-authored profile
names/review reasons are persisted deliberately; do not put secrets in them.

No enterprise SSO/RBAC, tenant isolation, HSM, CRL distribution, OCSP, automatic
certificate renewal/CA rotation, HA, distributed workers or attestation is claimed.
Operator identity is a shared engineering principal. The database is ACL-protected,
not encrypted. A principal controlling the database/CA can alter trust/history;
hashes detect ordinary mismatches, not malicious administrator rewriting.
Do not treat this phase as permission to execute a migration.
