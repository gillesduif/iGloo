# Fleet Web boundary

This .NET 8 executable provides an engineering operator CLI and typed status
client. It references only Contracts, with no access to repositories or endpoint
services. The CLI accepts GET/POST requests within `/v2/operator/`, validates the
Server certificate against an independently provisioned root and sends the separate
operator credential. It supports profiles, typed work, evidence, approvals, plans,
trust and audit inspection. Enrollment token creation prints its one-time secret;
protect that output.

See [Phase 1 operations](../../docs/fleet/phase-1.md) for commands. Phase 0 status
methods remain available. There is no browser frontend or separate web host yet;
a future UI can consume the same API with operator authentication.
