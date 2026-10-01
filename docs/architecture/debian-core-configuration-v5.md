# Qualified core configuration in the isolated development lab (2026-09-29)

The complete **debian-trixie-core-configuration-v5** profile passed on one fresh
retained-root helper fixture, then on one fresh canonical checkpoint derivative.
Independent readback verified configuration, the unchanged filesystem complement,
package state and preserved lab objects. Configuration and exact canonical teardown
have separate durable, independently reopened results. See the
[new evidence](debian-core-configuration-v5-evidence.json) for hashes and commands.
This supersedes the current-status statements in the earlier capacity and helper
reports; their original observations and byte-bound JSON remain unchanged.

## Executed sequence and scope

The first of the newly authorized three helper passes succeeded; no second or third
pass was dispatched. Fixture run `868f7520c39e4919bd356c3a5f474057` used an independently
verified checkpoint copy, attached read-only and mounted without journal replay.
The real retained helpers ran through `configuration_broker_rehearsal.retained_helpers`,
shared `helper_commands`, `TargetFiles`, `PackageBroker`, its stopped gate and effective
isolation observer. Fresh-process `verify_delta` accepted every step and final state.
Its 29 independently reopened records are fixture evidence, not canonical receipts.
Ordinary fixture unmount and normal VM shutdown were recorded separately.

The same native modules, gate and bubblewrap were checked against canonical staging.
The rebuilt harness then executed:

`--configure-derived-lab` → `InstallationStorage.ContinueLabCheckpoint` → canonical
leases and journal-placement verification → `ForLabConfiguration` →
`session_configuration.perform` → restricted broker and independent observers →
durable results → Inspect → ordinary UnmountPayload / UnmountRoot → Close.

| Step | Fresh helper fixture | Canonical derivative |
| --- | --- | --- |
| Baseline | Full neutral filesystem/package verification | AppliedAndVerified |
| Files / Debconf / Exim | Independently verified | AppliedAndVerified |
| TLS / Locale | Independently verified | AppliedAndVerified |
| User / Credential / Sudo | Independently verified | AppliedAndVerified |
| Final configured state / whole-tree delta | Verified | AppliedAndVerified |

V5 required no further account, helper, isolation or observer correction. Files
performed the existing exact-hash-bound `/etc/default/useradd` edit; the invalid
`-K CREATE_MAIL_SPOOL=no` argument remained absent. The denied audit socket stayed
denied, with the existing narrow errno behavior. The authenticated locale symlink,
private read-only `/boot` view and actual ESP exclusion remained intact.

The fixed public profile is hostname/mailname `igloo-lab-config`, username
`iglootest`, UID/GID 1000, locale `en_US.UTF-8`, timezone `Etc/UTC`, keyboard `us`,
and an explicitly empty migrated network-profile set. The observer verified
Exim UID 0 / authenticated GID 104 / mode 0644; new target-local TLS identity,
key/certificate relationship and key UID 0 / authenticated GID 105 / mode 0640;
locale output; account/database backups; protected credential correspondence;
home/skel links and metadata; locked root; sudo membership and authenticated policy.
Private keys, encrypted passwords and raw shadow contents were not exported.

Final readback verified deterministic network/resolver/APT settings and canonical
root/ESP UUIDs in fstab, the package closure and the unchanged complement including
kernel/modules, signed-package contents, links, ACLs and capabilities. Machine-id
remains empty with its D-Bus link; first-boot identity is still pending. The configured
tree is a verified transformation of the imported baseline, not a redefinition of
the original neutral artifact or an `ArtifactValid` claim for configured content.

## Canonical results and retained handoff

- Run: `60068939-1509-4b7c-99da-560446fbbc53`.
- Operation: `de073c82-3d6e-4f83-bb9e-5e56658c3873`.
- Session: `bc6b764a-1c99-4552-ad30-532ae8c32a57`.
- Original preparation lineage: `ff104c9d-505c-4d88-8620-19b1153bd91e`.
- Plan SHA-256: `EAE587970C148977E8A32BC16CB611D118E92759D32DBB5EA3407F5D8064F18F`.
- Configuration result: `00000019-54EC121627C7D45BCCB47621CFD6C05F1C22C91E0C17241ED2329F9D0DD6443D.json`.
- Close result: `00000017-D21FE8A94119D47270A91CED0962F6CF6637ADAE0C0825F453CC9AFAEF964001.json`.
- Protected raw resources: `/var/lib/igloo/canonical-labs/6006893915094b7c99da560446fbbc53/`.

Independent guest and host reopen verified **20 effect records and 18 session
records**, their chains, predecessor/operation/plan bindings and final result
reference. Exact mount-delta observations support ordinary payload/root unmount;
Close was independently reopened. Normal runtime poweroff and process absence are
separate lifecycle evidence. `configured-retention.json` binds the powered-off
target, payload-containing disk, journal and runtime backings for later revalidation.

A deliberately mismatched predecessor was rejected through the same canonical
entrypoint before block acquisition or configuration writes. Earlier journal
bootstrap/provisioning effects are separate; the negative is not a claim that the
whole VM performed no writes. None of the three historical failed configuration
operations was replayed, repaired or relabelled. Their missing failure-path canonical
teardown qualification remains an acceptance backlog item.

The next phase needs its own authorization and continuation boundary referencing
this configuration result and Close, freshly verified ownership, preserved set,
journals and configured-state evidence. This completed operation cannot replay or
automatically authorize initramfs work. The imported target was never booted.

## Provisioning corrections and limitations

The resumed Windows backing-volume preflight passed, and WSL became available.
Full backing hashes/identities matched the records preceding the capacity failure.
The old partial helper preparation was preserved and not reused.

Fresh provisioning initially stopped because an authenticated bubblewrap binary
was referenced through vanished `/tmp` acquisition scratch. `canonical_storage_lab`
now uses the predecessor's retained `input/bwrap`, still requiring the same fixed
SHA-256. The explicit fixture staging adapter also needed its local validation
import/signature corrected. These stops occurred before VM launch or helper
reservation; their records remain retained. No helper privilege or policy changed.

This qualifies only the stated retained Debian artifact, fixed configuration
profile, explicit checkpoint-derived lab provider and observed isolated runtime.
It does not prove real Windows preservation, user-data migration, target boot or
power-loss durability. ProductionAuthentication remains Unsupported; NativeSupported
remains 0. Production preparation/registration, recovery/readiness gates and Windows
v1 semantics are unchanged. Initramfs, agent/UserData/Enrollment, first boot and
boot/firmware finalization remain outside this milestone.
