# Fleet Web boundary

This .NET 10 executable provides an engineering browser shell, operator CLI and
typed status client. It references only Contracts, with no access to repositories
or endpoint services. The CLI accepts GET/POST requests within `/v2/operator/`, validates the
Server certificate against an independently provisioned root and sends the separate
operator credential. It supports profiles, typed work, evidence, approvals, plans,
trust and audit inspection. Enrollment token creation prints its one-time secret;
protect that output.

See [Phase 1 operations](../../docs/fleet/phase-1.md) for commands. Phase 0 status
methods remain available. Running without CLI arguments starts the browser host.
The AdminLTE shell uses secure cookies and antiforgery-protected setup, login and
logout forms. Operator password hashes are stored under the current user's local
application data, or the explicitly configured `FleetAuthentication:DataDirectory`.
Keep that directory outside source control and protect it as authentication data.

This is an engineering preview, not a production operator authorization boundary.
Restrict the host to a trusted development environment; the first administrator
setup endpoint is unauthenticated until an account exists. Sample device data is
allowed only in Development. Browser-to-Server operator credential provisioning
is not connected yet; the live status client cannot replace the CLI's independent
certificate and operator credential policy. UI pages do not authorize endpoint
execution, recovery or migration readiness.
