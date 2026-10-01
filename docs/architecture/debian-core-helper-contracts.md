# Retained core-configuration helper contracts (2026-09-29)

Latest: [v5 helper and canonical configuration qualification](debian-core-configuration-v5.md)
passed the complete sequence on fresh resources, including accounts, credentials,
sudo, independent delta verification and exact canonical teardown. The report below
is the unchanged interpretation of the earlier v3/v4 experiments, not the new result.

Historical outcome: **configuration remained blocked before a new canonical attempt**.
The authorized initial helper pass and one corrected pass both stopped at User.
Neither Credential, Sudo nor final configured-state verification ran. No new
checkpoint-derived authorization, reservation or canonical session was created.
See [new evidence](debian-core-helper-contracts-evidence.json) for exact identities
and records. Earlier v2 compatibility and consumed-attempt reports are unchanged.

## Executed scope

The isolated helper VM is retained at
`/var/lib/igloo/helper-contract-labs/7ada455b3d5148478ee5a82d6a7f1fad/`.
Its runtime is an independent writable copy. Its source is an independently
hashed copy of the protected import checkpoint, attached read-only and mounted
`ro,noload`. Actual QEMU descriptors and launch arguments were checked. No NIC,
host share, physical device, service socket or original evidence backing was
attached. The actual Linux ESP was not mounted.

Each pass copied the complete retained root into a fresh ordinary directory,
preserving numeric ownership, links, metadata and helper dependencies. A fresh
process verified the original complete filesystem, package state and neutrality
before any fixture mutation. These are helper fixtures, not canonical GPT targets;
they receive no fabricated preparation receipts. The checkpoint payload supplied
the unchanged authenticated manifest. Five generic broker-view cases also passed
on each executed revision; they are distinct from the real helper tests.

`configuration_broker_rehearsal.retained_helpers` uses the canonical executor's
`session_configuration.helper_commands`, `TargetFiles`, `PackageBroker`, stopped
command gate, effective-isolation observer and fresh-process `verify_delta`.
Each completed step has independently reopened fixture intent/result files. These
fixture records are not canonical session/effect journals. The canonical machinery
and its authority gates were not bypassed or invoked by the fixture.

| Step | Initial policy v3 | Corrected policy v4 |
| --- | --- | --- |
| Baseline | Full filesystem/package/neutral checks passed | Passed on a new root |
| Files / Debconf | Independently verified | Independently verified |
| Exim | Independently verified | Independently verified |
| TLS | Independently verified | Independently verified |
| Locale | Independently verified | Independently verified |
| User | Helper exit 1; no result | Helper exit 3; no result |
| Credential / Sudo / Verify | Not invoked | Not invoked |

Exim's retained `gentmpconf` sets `root:Debian-exim` before rename. The authenticated
target group database binds Debian-exim to GID 104 for this artifact. Both passes
verified UID 0, GID 104, mode 0644, intended identity, no temporary output and the
unchanged complement. No runtime group lookup, helper patch or opportunistic
root-group acceptance was used. Negative metadata/path regressions remain strict.

Both passes generated a new TLS key and certificate through retained
`make-ssl-cert`; independent checks verified key/certificate correspondence,
hostname/SAN, key UID 0/GID 105/mode 0640, certificate mode 0644 and the certificate
hash link. No private bytes were exported. Retained `locale-gen` and the matching
read-only archive query verified only `en_US.utf8`; the authenticated locale links
remained intact. Every completed step checked the complete unchanged complement
and exact package state. These successes do not establish account/sudo correctness.

## Evidenced corrections and remaining qualification

The first User exit is consistent with the retained shadow audit initialization:
it runs before argument processing, and EPERM from the denied audit socket is
fatal. Authenticated useradd/libaudit disassembly and upstream source were reviewed;
no syscall trace or stderr capture is claimed. Policy v4 keeps that exact
`socket(AF_NETLINK, SOCK_RAW|SOCK_CLOEXEC, NETLINK_AUDIT)` **denied**, returning
EAFNOSUPPORT for the three closed account helpers in the CoreConfiguration view.
All other non-UNIX sockets remain denied with EPERM. No socket access, audit
capability or other privilege was added. Exported BPF regression tests cover both
the narrow errno distinction and continued denials. See the retained-version
[shadow audit initialization](https://raw.githubusercontent.com/shadow-maint/shadow/4.17.4/lib/audit_help.c)
and [libaudit probe](https://raw.githubusercontent.com/linux-audit/audit-userspace/v4.0.2/lib/netlink.c).

The corrected native pass progressed to exit 3. The command incorrectly used
`-K CREATE_MAIL_SPOOL=no`: this is a useradd defaults key, not a login.defs override.
The retained binary, authenticated defaults and upstream
[useradd implementation](https://raw.githubusercontent.com/shadow-maint/shadow/4.17.4/src/useradd.c)
and [login-definitions table](https://raw.githubusercontent.com/shadow-maint/shadow/4.17.4/lib/getdef.c)
support that diagnosis. The broker intentionally does not export helper stderr.
Read-only observation found no intended account, group or home. A fresh full
post-failure observer exactly matched the previously verified Locale-stage result,
including its package and filesystem-delta identities.

Final source policy **v5** removes that invalid argument and declares one bounded
Files edit: enable `CREATE_MAIL_SPOOL=no` in `/etc/default/useradd`, preserving all
other bytes. The original defaults must match authenticated SHA-256
`6702E1CB0D7035A0D7A2BEEDED69F69ACD8AC2B2778913B5AFE60595936448F5`.
Planner, FD-relative writer, compatibility review and delta observer share this
declaration. Metadata/substitution/changed-input tests remain fail-closed. The
.NET plan and native policy both bind v5; old v2/v3/v4 records are not reinterpreted.

V5 passed deterministic tests, builds and the non-mutating retained-input review
(63 checks, 81 dependency objects). **V5 has not executed native helpers or a
canonical transaction.** The fixture authorization budget is exhausted; no third
fixture or canonical attempt was dispatched against the unqualified account
prerequisite. New explicit authorization is needed for fresh fixture resources and,
only after the entire helper sequence passes, a fresh canonical derivation.

## Retention and evidence limits

Both failed fixture roots and their records remain in the powered-off runtime.
Read-only source/payload/tools mounts were ordinarily unmounted and fresh mount
absence was independently verified. This is fixture provisioning teardown, not
canonical teardown. One normal poweroff request was issued. The initial 55-second
process-absence check failed; a later fresh check observed QEMU absent. The final
power-down console line was not captured, and no forced termination occurred.

Checkpoint and all three earlier failed target/journal backing pairs are compared
to fresh full before/after hashes and device/inode/link-count evidence. Prior
reservations and outcomes remain untouched. This run produces no configured
canonical target, configuration handoff, canonical teardown or initramfs authority.
No power-loss, imported-target boot or real Windows preservation is claimed.

ProductionAuthentication remains Unsupported; NativeSupported remains 0.
Production preparation/registration and downstream readiness remain closed.
RecoverySnapshotV1, Exact, RecoveryReadiness, PreCommitGate and Windows v1 contracts
are unchanged. Initramfs, agent, user-data, enrollment, boot and firmware remain
outside scope.
