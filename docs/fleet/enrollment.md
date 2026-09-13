# Trusted enrollment (Phase 1)

Trusted enrollment is separate from Phase 0's --development-local mode.
The trusted protocol is v2.0; the Phase 0 protocol remains v1.0.

1. Initialize the Windows Server's private CA and TLS identity once.
2. Provision its public fleet-root.pem to the Agent through a trusted, independent
   channel. Never download and blindly trust a root from an enrollment response.
3. An authenticated operator creates a token, valid for 1–60 minutes.
4. The Agent generates a 2048-bit RSA key locally and submits a signed CSR over
   server-authenticated HTTPS with the token.
5. Server verifies proof of possession, consumes the token transactionally and
   assigns random DeviceId, AgentId and EnrollmentId values.
6. Server signs a 30-day client certificate and returns only that public
   certificate and identifiers. The Agent persists its own key locally.
7. Subsequent Agent APIs require the certificate at the TLS connection, not a
   self-reported thumbprint header.

Tokens contain 32 cryptographically random bytes encoded as 64 hex characters.
Only their SHA-256 hashes, creation/expiry time, creator, consumed/revoked state
and IDs are stored. Plaintext is returned only at creation; there is no retrieve
plaintext operation. Single-use consumption and device creation commit together.
Failed CSR issuance rolls back consumption. Revocation is available before use.
Expiry is based on authoritative Server UTC time.

## Local key protection

Server default directory:
%LOCALAPPDATA%/Igloo/FleetServer

Agent default directory:
%LOCALAPPDATA%/Igloo/Fleet/trusted

Both CLI hosts protect their metadata directory with a Windows ACL restricted to
the current Windows principal. ca-key.dpapi, server-key.dpapi and agent-key.dpapi
contain DPAPI CurrentUser-protected PFX data. No unprotected PEM private key is
written. The Agent sends only a CSR; its private key never reaches Server.

At runtime Schannel uses Windows user key containers imported without
PersistKeySet; disposal removes those temporary imported keys. The initialization
copy of the Server TLS certificate is exportable only so it can be saved under
DPAPI protection. Normal key loads are not exportable.

This is protected per-user software storage, not TPM/HSM-backed key protection
or a machine-wide Windows service identity. Run under a dedicated Windows
principal for a pilot and protect its profile/backups. The built-in CA command
requires Windows; the injected TLS/persistence host can otherwise use .NET 8.

## Loss, interrupted enrollment and re-enrollment

Existing or partial identity files cause enrollment to fail closed. They are
never silently overwritten. A crash after token consumption but before receiving
the certificate requires operator recovery: identify/revoke the orphaned device
using enrollment audit, issue a new token, and enroll into a deliberately new
Agent metadata directory.

**Re-enrollment creates a new DeviceId and AgentId.** It is not an identity
replacement on the old device. Revoke/retire the old record explicitly first.
Hostname and IP are not identity keys and are not collected by this phase.
The system cannot deduplicate physical machines through hostname reuse.

Client renewal and CA rotation are not automatic in Phase 1. Before expiration,
plan a controlled re-enrollment or a future renewal workflow. Existing historical
evidence/approvals remain attached to the original immutable identity.

See [commands](phase-1.md), [security](security.md) and [database design](persistence.md).
