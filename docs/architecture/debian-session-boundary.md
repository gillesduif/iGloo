# Debian canonical mount session — Community #241

Status, 2026-09-28: **DEBIAN SESSION FOUNDATION BLOCKED**.

## Later chunk transport qualification (2026-09-28)

The following continuation supersedes the single-file transport blocker in the
previous checkpoint below. It does **not** supersede canonical-session qualification.

`DebianConfiguredRootImportPlanV1.Transport` explicitly selects `SingleFile` or
`Chunked`; `TransportManifestSha256` binds the exact separate v1 transport envelope
into the plan fingerprint. `DebianRootTransports.Reopen` checks its strict bounded
contract during source authentication. `DebianMountSessionAuthority` includes the
transport identity in independently reopened import records and checks the handoff.
The native runtime hashes the additional `root_transport.py` module before loading it.

The existing call chain is unchanged after physical source opening:
`DebianNativeMountSession` → `session_import.perform_import` → FD-relative chunk
opens beneath the canonical payload → `root_transport.LogicalStream` → existing
`configured_root.verify_source` / `import_files` → fresh semantic observer → separate
reopened import/session records. The verifier checks every chunk, the concatenated
original stream and every semantic file **before import reservation**. Import reads
the same retained FDs, checks identities during reads, hashes each imported file,
and rehashes the complete stream before final readback/publication. No reconstructed
stream, imported code, target package operation or new execution profile exists.

**Transport bytes and FAT32 delivery are verified; canonical import is not run.**
The [new evidence](debian-root-chunk-transport-evidence.json) records the full real
4,641,457,180-byte artifact, five fixed chunks, independent directory and read-only
FAT32 verification, unchanged descriptor/manifest/pin, and normal delivery unmount.
Its physical source is an 8-GiB payload on a newly created GPT virtual test disk.
This proves delivery mechanics, not acquisition through `InstallerBlockLeases`.

The actual whole collector in the new VM returns `Unsupported / NonGptDiskVisible`:
the retained factory runtime uses an MBR bootdisk. No disk was filtered out. Linux
.NET is absent. New GPT target and journal disks were genuinely created/formatted
with retained before/intents/readbacks, but there is no qualified Linux lab provider
for the Windows-origin `CanonicalDiskIdentityV1` provider fields / volume identities
and preparation receipts. Those values were not invented from serials or labels.
Canonical acquisition, session/import journals, full import and session teardown
were therefore **not invoked**. Provisioning observations remain explicitly lab
evidence, not native Windows preparation receipts. A fresh qualified GPT runtime,
authenticated .NET 8/runtime tools, and the lab identity/receipt adapter are next
prerequisites. Formatting a journal disk alone does not qualify journal placement.

Fixture coverage connects chunked dispatch through the existing authority/journals
and existing importer. It is distinct from the full source/FAT32 tests. Production
authentication, `ImportSupport`, preparation/registration and downstream readiness
remain closed; NativeSupported remains 0. The original neutralization/regeneration
handoff is unchanged. No full-installation success or target configuration is inferred.

## Later canonical import integration (2026-09-28)

Historical implementation checkpoint: its missing chunk transport is superseded
above; its canonical qualification limits and original validation counts are retained.

The closed `ImportConfiguredRoot` action is implemented behind explicit development
composition. `DebianConfiguredRootImportPlanV1` binds ownership/root-format receipt,
target generation and artifact build/derivation/descriptor/policy/manifest/content.
There are no fictitious user/agent/completion fields. The authority uses
`IDebianConfiguredRootAuthenticator` with an explicit external pin; production gets
no development authenticator by default.

Connected code: `InstallationOwnership.ResolveFormattedRoot` via `InstallerBlockLeases`,
`DebianMountSessionAuthority`, `DebianNativeMountSession`, `block_session.MountSession`,
`session_import.ConnectedImportView`, existing `configured_root.verify_source` and
`import_files`. A fresh hash-checked `session_entry` process performs full tree,
read-only dpkg and neutral-state verification. Its actual observation is retained;
no target code executes.

Import is trusted supervisor work, not a package child. Metadata restoration needs
CHOWN, DAC_OVERRIDE, FOWNER/FSETID and SETFCAP for the already reviewed owners,
set-ID modes, ACLs and GStreamer capability. The mount supervisor retains its setup
privilege; it exposes no arbitrary command and executes no imported code. Fresh
readback receives only directory/manifest FDs, never raw block descriptors. The
package/configuration capability/seccomp profiles are unchanged.

Import topology is root EXT4/rw plus sibling payload FAT32/ro. ESP remains acquired
read-only and **unmounted**. Runtime mountpoints are create-new outside the root
under protected parents. Connected ancestry, inode/device, mount ID, filesystem
root, propagation, aliases, stacked and child mounts are checked. No detached clone
or bubblewrap source expansion is used; DirectoryLease and metadata checks remain.

Source files are opened FD-relative under payload without symlink/mount crossing.
Strict descriptor/attestation/pin validation and full stream/per-file verification
precede import reservation. The same content FD supplies import bytes. Identity
checks and manifest-bound post-readback reject substitution. Build, derivation and
target generation remain distinct.

Import checkpoints use a separate `DebianLinuxDeploymentJournal` through the private
pipe: reserve once, append/fsync, independently reopen, acknowledge. Session effect
intents stay separate. Result records include leases/protected-set identity; the
session record and handoff link the reopened import reference/hash. Progress never
authorizes replay. Missing observer, cancellation, supervisor loss, fsync or result
publication failure cannot become success. Normal exact teardown is separate.

`ObserveImportJournalsAsync` uses the protected entrypoint/shared collector.
`DebianImportJournalStorage.Verify` requires nominated persistent EXT4 runtime
storage and separate private directories outside protected/target disks. Journal
calls pin directory device/inode/mount ID. No actual production location is claimed.

**Qualification remains blocked.** The real 4,641,457,180-byte stream exceeds
FAT32's 4,294,967,295-byte single-file limit. It is rejected before session reservation.
No host path, network, shared folder, alternate filesystem or extra source disk
fallback was added. Smallest proposed follow-up: explicit plan-bound FAT32 chunk
transport with bounded lengths, ordered hashes, original aggregate stream identity,
FD-bound reads, total payload-capacity checks and full verification before intent.
The semantic format/schema stays unchanged. Chunk transport is **not implemented**.

The [opt-in harness](../../tests/installer/CanonicalImportQualification/README.md)
builds but was not run against canonical GPT storage. WSL has no Linux dotnet on
PATH. No new VM/provisioning/mount/real import occurred. Runtime/physical identity,
source delivery, persistent journal placement and full native execution remain
prerequisites. Fake mount/authority tests are not canonical device evidence.
NativeSupported remains **0**; production authentication/import/preparation and
registration remain Unsupported/disabled. Older missing-view/dispatch statements
below are historical; this section supersedes implementation gaps, not qualification.

The handoff is the reopened content-result reference/hash plus session/plan,
generation, root/source leases, protected set and artifact identities. A stored FD
number is not durable authority. Hostname/mailname/Exim/TLS, users, network/fstab,
machine identity, final initramfs, agent/UserData/Enrollment and all signed-loader/
firmware work remain required follow-ups. Content verification does not satisfy
any of those target-regeneration contracts.

Validation for this later integration (not the historical counts below):

| Check | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 353 | 0 | 0 |
| Preflight | 343 | 0 | 0 |
| Community.App | 58 | 0 | 0 |
| Migration | 381 | 0 | 0 |
| Full serialized solution | 1,324 | 0 | 0 |
| Debian / configured-root / session filters (overlapping Migration subsets) | 350 / 35 / 39 | 0 | 0 |
| Linux unittest discovery | 268 | 0 | 0 |
| Explicit native broker/session/tmpfs/observer fixtures | 45 | 0 | 0 |
| Explicit file-capability/ACL fixtures | 2 | 0 | 0 |

Commands: `dotnet test tests/Igloo.<name>.Tests/Igloo.<name>.Tests.csproj --no-restore -m:1 -warnaserror`
for the four projects; Migration filters `--filter FullyQualifiedName~Debian`,
`~DebianConfiguredRoot`, `~DebianMountSession`; `dotnet test Igloo.sln --no-build --no-restore -m:1`.
`dotnet build -warnaserror` and the separate qualification-harness build both have
0 warnings/errors. Linux commands: `python3 -B -m unittest discover -s tests/installer -p 'test_*.py'`,
`python3 -B tests/installer/broker_rehearsal.py` (root, pinned bubblewrap environment),
`python3 -B tests/installer/artifact_native_rehearsal.py` (root). The 15 new import
dispatch cases are inside discovery, not additional to its count.

The new native observer fixture uses real FD-relative files and fresh trusted dpkg
readback, but a small synthetic package tree. The session/lease and dispatch tests
use fake canonical/mount boundaries. None uses the full retained GNOME stream.
Full-artifact corruption/rehearsal and real GPT/session qualification were **not
rerun**. Earlier evidence remains historical and unchanged. No failed check is
silently skipped or promoted to native qualification.

The later [configured-root artifact continuation](debian-configured-root-artifact.md)
adds a candidate source format and FD-relative fixture importer. Production import
still returns Unsupported: canonical root-only session view, real mount qualification
and complete observer/journal integration are not supplied by a fixture root FD.
Its latest factory experiment resolves the package closure with one reviewed wsdd
exception and builds a real GNOME root. The subsequent
[neutralization/import report](debian-configured-root-neutralization.md) records
the verified development artifact and separate import-mechanics qualification.
Neither supplies the missing production session integration; the older package
policy rejection below is historical, not the current solver result.

The subsequent [bootstrap primitive decision](debian-bootstrap-primitive.md)
rejects adapting stock debootstrap to this boundary, compares authenticated
Trixie alternatives, and removes the candidate native bootstrap command.
No replacement is qualified and the stage matrix below remains unchanged.

This continues the [isolation boundary](debian-isolation-boundary.md), without
replacing its package policy or restarting the deployment design. Native Windows
preparation, Community registration and the 43-stage production runner remain
disabled. No change to RecoverySnapshotV1, Exact, RecoveryReadiness or PreCommitGate.

## What is implemented in this continuation

`InstallerBlockLeases` in Core derives exactly three descriptor-acquisition
declarations from `InstallationOwnership.ResolveFormattedRoot`. It uses the
existing PreparedLayout, creation/format receipts, ESP binding and whole-inventory
validation. There is no second GPT or filesystem ownership resolver in Python.

Each declaration binds preparation generation, session identity, UTC acquisition
time, canonical physical-disk receipt, GPT disk/partition/type, byte geometry,
filesystem type/UUID, role, access mode and current device path/major/minor. Root
is EXT4/rw; ESP and FAT32 payload are ro. Preserved partition/filesystem observations
are fingerprinted as an additional session invariant. No Windows partition is an
acquirable role. Complete unique block stat coverage is required, including devices
outside the nominated disk. The canonical hash excludes transient locators.

An acquisition declaration is **not an open descriptor or exclusive kernel lease**.
`CanonicalBlocks` in `block_session.py` opens only the three authorized devices,
with no-follow/CLOEXEC, checks block type, major/minor, read-only size ioctl and
access mode, and takes cooperating-process flock locks. These locks do not exclude
an unrelated privileged actor. The trusted exclusive installer runtime remains a
precondition; competing host root is outside the package threat model.

Every fresh witness comes from the existing collector, two matching inventories
around whole-device stat, and another .NET canonical validation. There is no empty
inventory fallback. GUID/type/geometry/UUID/protected-set disagreement invalidates
the session. Locator/major-minor changes are **not called canonical identity
changes**; they require session restart instead of silently replacing an open FD.
No stale lease is reacquired during a stage.

`DebianMountSessionAuthority` binds one immutable deployment plan to one generation
and one use. It rejects concurrent actions, replayed challenges, stale observation
sequence numbers, skipped intent/readback, out-of-order mounts/unmounts and remount
after teardown. Observation failure availability/code are durably retained, including
AccessDenied versus Absent. There is a bounded request count.

`DebianNativeMountSession` implements the Linux private-pipe transport to a persistent
trusted supervisor. It verifies the declared tool/module hashes, reserves a separate
`DebianLinuxDeploymentJournal` store for session records, reopens namespace intent
before spawning the supervisor, and awaits fresh external namespace readback.
`session_entry.py` independently verifies root ownership/modes, protected parent
paths and hashes before importing the local module tree. No inherited PYTHONPATH,
shell string, arbitrary device operation or credential message is accepted.

The trusted supervisor retains a recursively private mount namespace. PID/network/
IPC/UTS isolation still belongs to the existing **per-command** package broker;
this mount-session work does not falsely report those namespaces as established
for a package that has not been launched. Exact mount actions are selected from a
closed enum and derive paths from the generation. Their canonical witness callback
now crosses the private pipe to the shared .NET resolver. Descriptor open, mount,
unmount and close intents must be fsynced/reopened before acknowledgement. Mount
results retain the fresh-process mountinfo/stat evidence. Checkpoints include a
sequence and prior-content hash. They are effect evidence, not installation success.

Unmount revalidates canonical identity before and after durable intent and after
the effect, as well as the previously implemented mount-ID/inode/parent/mode checks.
The order remains ESP, payload, root (helpers must precede these when integrated).
There is no lazy/force unmount. Failure poisons the session. Killing an abandoned
supervisor makes its private namespace disappear but **does not produce a verified
teardown receipt** or erase the prior intent.

## Root-only view and the previous detached-FD finding

`DirectoryLease` now additionally requires its descriptor to name the exact connected
path and its mount ID to appear in the current mount table. Equal inode/device alone
cannot qualify a detached clone. There are deterministic regression tests and a real
kernel open_tree-clone negative test. The latter closes the rejected detached FD;
it never passes that FD to bubblewrap or attaches it to any path.

The production root-only view is **still missing**. An assembled supervisor root
with `/boot/efi` or helpers beneath it cannot be recursively imported as if it were
an unmounted directory. The package broker continues to reject child mounts. This
continuation did not reintroduce the rejected detached-FD expansion route.

## What is not yet a production deployment session

The new transport composes canonical decisions, native mount mechanics and durable
session records in code. It is **not yet a qualified IDebianIsolatedStageHost**.
No caller can request package execution through the mount-session protocol.

Remaining composition requirements:

- Qualified native GPT/EXT4/FAT32 device acquisition and mounts; safe creation of
  generation-specific mountpoints beneath verified root/runtime directories.
  The mount session currently requires existing protected empty mountpoints.
- A verified connected root-only package view and its controlled ESP/payload
  aliases; package-child evidence joined to the supervisor's canonical leases.
- Reconcile the old target/helper contract (rw ESP, proc rw, sysfs ro) with the
  actual restricted child (ro ESP, proc ro, empty ro sysfs). Never synthesize the
  old observation from the new flags merely to make the verifier pass.
- Full runtime/library/helper qualification, including actual collector tools,
  not only Python/module/executable hashes. Windows hardware-ID to Linux physical
  identity qualification remains distinct from matching GPT/size/geometry.
- Native 43-stage host, complete semantic observers and stage-journal composition.
  Session effect checkpoints do not satisfy account/kernel/agent/package checks.
- Protected credential producer/FD transport into the active package child. The
  existing single-use sealed-memfd consumer remains separate; this session protocol
  cannot carry secrets in JSON, argv or environment.

Resource limits currently include message size, request count, process/command
deadlines and discarded/bounded diagnostics. A qualified process/memory/CPU limit
profile remains open. This is not a DoS-proof sandbox claim.

## Debootstrap privilege decision

The retained authenticated **debootstrap 1.0.141** binary/helper tree was reread;
no helper was patched. Its public [foreign/second-stage contract](https://manpages.debian.org/trixie/debootstrap/debootstrap.8.en.html)
splits initial unpacking from completion, but does not provide a switch to omit
only device/proc setup. In this version:

- `/usr/sbin/debootstrap:618–629` invokes `check_sane_mount` for both install phases.
- `functions:1845–1894` creates/writes `test-dev-null` with mknod, then tries a bind
  mount if that fails. A pre-existing `/dev/null` does not skip this test.
- `scripts/debian-common:132` runs setup_devices during the first stage.
- `scripts/debian-common:139–187` enters second_stage_install, runs setup_proc,
  then invokes dpkg. `functions:1256–1305` includes tolerated mount failures;
  cleanup includes lazy unmount. There is no supported capability-drop hook at
  this boundary in the inspected public interface/helper tree.

The package profile denies MKNOD and mount operations and retains nodev roots.
Therefore it cannot truthfully complete those checks. Granting SYS_ADMIN to the
whole second stage would also grant it to maintainer scripts. Preparing helpers
in the supervisor does not eliminate check_sane_mount. Fabricated CONTAINER flags,
ignored failures, helper patches and an unqualified fakechroot route remain rejected.

**Bootstrap remains Unsupported. No real debootstrap or dpkg/apt configuration ran
under this broker.** A supported upstream setup/configuration split, or a separately
reviewed authenticated base-image/alternative bootstrap architecture, is required.
The mount-session work does not make that architecture decision on its own.

## Runtime evidence and limits

The WSL Linux 6.6.87.2 read-only canonical collector returned:

```json
{"availability":"Unavailable","code":"sfdiskExit1","schemaVersion":1}
```

No ownership witness was manufactured. The current collector also treats loop
devices as external filesystems, rather than GPT parent disks; a disposable GPT
rehearsal requires explicit supported collector/runtime handling. Filtering out
unobserved host disks or reclassifying a loop by a label is not accepted.

No GPT image, loop mapping, partition creation, formatting or actual block-device
mount was performed. There is no root EXT4, ESP FAT UUID, GPT GUID or real mount
receipt to report from this run. Real mount/unmount evidence is **tmpfs fixture
evidence only**, not physical-hardware or full-deployment equivalence.

Explicit privileged fixtures used disposable directories/private tmpfs. They ran
the persistent supervisor, authenticated its modules, observed its private namespace
in a fresh process, fsynced and independently reopened startup/close checkpoints,
and rejected canonical authorization before any block open. .NET authority tests
exercise the canonical protocol and journal contract using fixtures; a complete
.NET-to-Linux-to-GPT integration run has **not** occurred. No Linux `dotnet`
executable was available on the WSL qualification PATH.

The package-profile negative syscall/device/network/socket tests continue to pass.
All old raw-device/capability/firmware restrictions remain; none was widened for
bootstrap. No new empty host-root setup placeholders were created by this run.

## Other retained blockers

The last authenticated GNOME solution still rejects `gvfs-backends -> wsdd`.
This continuation did not rebuild the signed repository solution or change its
Recommends policy, substitute wsdd2, or import a package from another release.
The 78 verified minbase archives remain acquisition evidence, not a workstation
bundle or an installed target root.

DeploymentContent/UserData/Enrollment are still the generation/hash-bound receipt
requirements consumed by the existing first-boot worker. Privileged producers are
not implemented/qualified here. UserData execution remains Unsupported; there is
no partition scan, label discovery, boot cleanup or enrollment-success fallback.
The worker and its responsibilities were not redesigned.

Existing dpkg/file/mount readback primitives remain partial. Account/sudo,
locale/timezone/NetworkManager, service/agent, initramfs contents and Windows file
preservation observations are not complete native session observers. No command
exit or matching hash is promoted to proof of installation or bootability.

## All 43 stages after this continuation

`NativeSupported` requires the production session, reopened intent, effect,
effective isolation, independent semantic readback and durable result. **Zero
stages meet that complete definition.** PartiallyImplemented does not enable one.

| # | Stage | Classification |
| ---: | --- | --- |
| 1 | ValidateOwnership | PartiallyImplemented |
| 2 | ResolveRuntimeDevices | PartiallyImplemented |
| 3 | PrepareNamespace | PartiallyImplemented |
| 4 | MountRoot | PartiallyImplemented |
| 5 | MountLinuxEsp | PartiallyImplemented |
| 6 | MountPayload | PartiallyImplemented |
| 7 | ExcludeWindowsEsp | PartiallyImplemented |
| 8 | VerifySourceTrust | PartiallyImplemented |
| 9 | ObserveFirmwareBeforePackages | FirmwareDeferred |
| 10 | Bootstrap | Unsupported |
| 11 | ConfigureApt | PartiallyImplemented |
| 12 | ConfigureArchiveKeyring | Unsupported |
| 13 | MountHelpers | PartiallyImplemented |
| 14 | ConfigurePackagePolicy | PartiallyImplemented |
| 15 | ConfigureHostname | PartiallyImplemented |
| 16 | ConfigureLocale | PartiallyImplemented |
| 17 | ConfigureTimezoneKeyboard | PartiallyImplemented |
| 18 | InitializeMachineIdentity | PartiallyImplemented |
| 19 | ConfigureNetwork | PartiallyImplemented |
| 20 | GenerateFstab | PartiallyImplemented |
| 21 | ConfigureUser | PartiallyImplemented |
| 22 | ConfigureSudo | PartiallyImplemented |
| 23 | InstallKernel | PartiallyImplemented |
| 24 | InstallFirmware | PartiallyImplemented |
| 25 | InstallDesktop | PartiallyImplemented |
| 26 | InstallAgent | PartiallyImplemented |
| 27 | ConfigureSignedPackages | FirmwareDeferred |
| 28 | DrainPackageTriggers | FirmwareDeferred |
| 29 | FinalizeLoaderFiles | FirmwareDeferred |
| 30 | GenerateGrubConfiguration | FirmwareDeferred |
| 31 | FinalizeMachineIdentity | PartiallyImplemented |
| 32 | GenerateInitramfs | PartiallyImplemented |
| 33 | InspectBootFiles | FirmwareDeferred |
| 34 | ObserveFirmwareBeforeFinalization | FirmwareDeferred |
| 35 | FinalizeFirmware | FirmwareDeferred |
| 36 | InspectFirmwareAfterFinalization | FirmwareDeferred |
| 37 | VerifyWindowsPreservation | PartiallyImplemented |
| 38 | PersistDeploymentEvidence | PartiallyImplemented |
| 39 | UnmountHelpers | PartiallyImplemented |
| 40 | UnmountLinuxEsp | PartiallyImplemented |
| 41 | UnmountPayload | PartiallyImplemented |
| 42 | UnmountRoot | PartiallyImplemented |
| 43 | PersistCompletionEvidence | PartiallyImplemented |

Signed-loader/package-hook/NVRAM finalization and disposable VMware lifecycle
validation remain additional blockers. It is **not true that only firmware remains**.

## Validation

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 353 | 0 | 0 |
| Preflight | 343 | 0 | 0 |
| Community.App | 58 | 0 | 0 |
| Migration | 293 | 0 | 0 |
| Debian filter (Migration subset) | 262 | 0 | 0 |
| Linux deterministic/native primitives | 132 | 0 | 0 |
| Explicit native broker/session/tmpfs fixtures | 40 | 0 | 0 |
| Full serialized solution | 1,236 | 0 | 0 |

Targeted project runs: `dotnet test <project> --no-restore -m:1 -warnaserror`.
Debian filter: `--filter FullyQualifiedName~Debian`.
Full solution: `dotnet test Igloo.sln --no-build --no-restore -m:1`.
Linux deterministic: `python3 -B -m unittest discover -s tests/installer -p test_*.py`.
Explicit native: `python3 -B tests/installer/broker_rehearsal.py`, root in WSL with
the previously authenticated bubblewrap path/SHA environment bindings.

`dotnet build -warnaserror`: **0 warnings, 0 errors**. No analyzer suppressions.
`git diff --check`: **exit 0**; 28 existing LF/CRLF notices are separate from
whitespace failures. New/edited untracked files also pass the trailing-whitespace check.
Added **41 .NET**, **23 Linux deterministic** and **4 explicit native** tests.
There was no real GPT, debootstrap, apt or VM lifecycle qualification. No commit/push.
