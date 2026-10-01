# Canonical configured-root import qualification

Latest: [complete v5 qualification](../../../docs/architecture/debian-core-configuration-v5.md)
passed one fresh retained-helper sequence, then one canonical configuration attempt.
Independent reopen verified all configuration steps, whole-tree/package readback,
preservation, 20 effect records and 18 session records including exact teardown.
The powered-off target and journals are retained for a separately authorized next
phase. Both completed operations are consumed; these resource IDs are not commands
to replay. No initramfs, target boot or production enablement occurred.

Previous: [v5 qualification provisioning](../../../docs/architecture/debian-core-v5-capacity-block.md)
stopped before native execution: WSL virtual free space did not reflect the full
underlying Windows volume. No new helper or canonical attempt was dispatched.

Before provisioning on the existing WSL lab host, run the read-only backing-volume
check from Windows (180 GiB is this qualification's declared resource reserve):

```powershell
powershell.exe -NoProfile -NonInteractive -File tests/installer/Test-CanonicalLabHostCapacity.ps1 -DistributionName Ubuntu-24.04 -RequiredAdditionalBytes 193273528320
```

Exit 2 means insufficient space; exit 3 means placement/capacity could not be
observed. Neither permits provisioning. This does not start WSL or grant storage
authority. The Python copy/create paths also require this observation on WSL,
in addition to their existing guest-filesystem capacity and ownership checks.
Do not resume incomplete copies, remove retained evidence, or automatically repair
or restart WSL after a capacity failure. Revalidate retained bytes before a new
independent preparation. At that earlier stop the budget was unused (0/3 helpers,
0/1 canonical attempts). The later successful v5 run used one helper pass and its
one canonical attempt. Canonical dispatch always requires a complete passing helper
sequence on the intended revision.

Previous: [retained helper-contract tests](../../../docs/architecture/debian-core-helper-contracts.md)
passed Exim/TLS/locale in real isolated fixtures, then stopped at useradd. Policy v5
corrects the evidenced defaults declaration but has no native qualification. The
two authorized fixture passes are consumed; no new canonical attempt was invoked.

Previous configuration successor: [reviewed checkpoint-derived attempt](../../../docs/architecture/debian-core-configuration-compatibility.md).
Files and Debconf passed; Exim readback failed on the observer's incorrect group
expectation. Final policy v3 corrects that expectation but has not run natively.
Do not reexecute any consumed configuration operation or reuse its copied stores.

## Authenticated lab import continuation

Executed 2026-09-29: [full native import evidence](../../../docs/architecture/debian-canonical-lab-import-evidence.json).
The positive run verified 144,911 entries / 1,594 packages and reopened 18 session /
1,823 import records, then ordinarily unmounted and closed. The target remains
powered off. The distinct corrupted-source run rejected before import reservation;
its poisoned session has no canonical teardown receipt. Do not replay either run.

The storage-only milestone below remains historical evidence for its own generation.
A new import operation uses fresh version-2 lab preparation, never a retagged smoke
receipt or a reused session. The explicit entrypoint is:

```text
/opt/dotnet/dotnet CanonicalImportQualification.dll --import-lab-development storage.json descriptor.json external-pin.json transport.json runtime.json journals.json
```

The diagnostic metadata must be protected and match the existing external pin.
The actual import reopens all artifact objects only beneath the canonical FAT32
payload. The plan and session bind storage provenance, artifact identities, exact
transport bytes and external pin validity. Production composition is unchanged.

Use `canonical_import_lab.prepare(image, pin, NEW_DIRECTORY)` to copy the nominated
retained FAT32 objects read-only, verify each object and the original aggregate,
and retain input evidence. Pass that delivery directory to
`canonical_storage_lab.create(previous, repo, publish, delivery)` so a new read-only
ISO supplies provisioning bytes. Verify the launch/backing FDs with `observe(run)`
after boot, before any guest provisioning. Temporary boot-time kernel/initrd FDs
are not an accepted steady-state storage declaration.

Stage current code under a new protected `/opt` directory, excluding the delivery
objects from runtime copies. Call `canonical_storage_guest.provision(...,
'ConfiguredRootImport')`, then `canonical_import_guest.stage_payload(...)` and
`execute(...)`. Staging checks the freshly receipted FAT UUID, capacity and reserve;
it independently reopens every FAT32 file and normally unmounts before the session.
The full source verification is repeated inside the real canonical import operation.

For the native rejection, use another fresh run/target/generation and
`stage_payload(..., corrupt=True)`. That closed test operation changes one byte in
only the new payload copy after recording its original binding. It does not change
the expected descriptor, transport or pin. Failure is not permission to retry,
reset reservations or claim namespace disappearance as teardown.

Retain all journal chains and independent observations, then request normal runtime
shutdown only after recording the actual teardown outcome. Never boot the imported
target. See the latest acquisition continuation and import evidence for executed
results and remaining limits; a building harness is not native qualification.

After successful canonical Close, run the read-only `canonical_import_readback.verify`
in a fresh process against the provisioning evidence and mounted journal stores.
It checks the record hashes/chains, provider/scope/plan/source/lease bindings, linked
import result and exact reverse-unmount deltas. It neither imports nor authorizes
recovery. Retain the complete returned record references and observer evidence before
normally unmounting provisioning resources and shutting down. Separately record
console power-down, host process absence and retained backing identities/hashes.

## Current storage-only qualification

The [later executed storage milestone](../../../docs/architecture/debian-canonical-lab-acquisition.md#later-storage-session-milestone--2026-09-28)
supersedes the historical acquisition/ownership gap below for StorageSmoke only.
The real canonical smoke and exact teardown succeeded. Full import was not invoked.

```text
/opt/dotnet/dotnet CanonicalImportQualification.dll --smoke-development storage.json runtime.json journals.json
```

This is a separate closed scope: no descriptor, external artifact pin, artifact files,
user/agent placeholders or import permission. `storage.json` is the protected
`InstallerLabStorageEvidenceV1` produced from real before/create/format snapshots and
intent bytes. The harness re-collects all inventory and guest identities, validates
the tagged context, invokes the SAME journal-placement verifier and session authority,
and completes ordinary reverse teardown. Production import remains Unsupported.

For reproducible provisioning, publish the current framework-dependent net8.0 harness:

```text
dotnet publish tests/installer/CanonicalImportQualification/CanonicalImportQualification.csproj --no-restore -m:1 -warnaserror --no-self-contained -p:UseAppHost=false -o C:/igloo-lab-runtime/NEW-STAGING-DIRECTORY
```

In the authorized isolated host environment, `canonical_storage_lab.create(previous,
repo, publish)` makes a new sparse copy of the powered-off GPT runtime and NEW
target/journal backing files. It refuses an open original backing, verifies the full
copy digest, records create/launch intent and boots with no NIC/passthrough/shares.
`canonical_storage_lab.observe(run)` must independently verify all actual QEMU FDs
before any guest disk mutation. Its protected `host-binding.json` is delivered by
the exclusive controller over the console, never generated from a guest claim.

The read-only tooling CD delivers the current harness/native code, collector,
compiled static gate and previously authenticated bubblewrap. Stage them under a
NEW root-owned `/opt` directory. Deliver only bounded code fragments over the
console when necessary; large Python `-c` commands exceed the shell/argument limit.
Verify every delivered executable hash. No shared folder or network fallback exists.

Inside that new guest, call `canonical_storage_guest.provision(host_path, stage)`
after host backing observation. It protects the copied dotnet/runtime and staged
code; records blank disks separately; initializes platform GPT/preserved ESP/journal;
then records complete collector snapshots and explicit intents for each owned
creation/format. It invokes this SAME harness with:

```text
--verify-lab-transition protected-request.json protected-runtime.json
```

The .NET transition probe independently recollects before returning a receipt. The
final smoke revalidates the entire sequence and exact intent bindings. Provisioning
evidence is create-new and fsynced on runtime EXT4, before the session stores exist.
Errors stop the sequence; partial roots/records are not removed, reformatted or resumed.
Before privileged session use also check system interpreter/tool/library placement
and clear environment search overrides, as recorded in the native run evidence.

The provisioner creates two distinct root-owned 0700 directories on a separately
owned journal EXT4 disk. The smoke actually calls `ObserveImportJournalsAsync` and
`DebianImportJournalStorage.Verify`; directory creation alone is never qualification.
The payload may be empty because StorageSmoke only verifies storage. The full-import
source contract is unchanged.

Retain stdout/exit status as diagnostics beside the durable native journal. After
success, independently reopen every session record and verify its hash chain and
provider/plan/session/generation, compare preserved lab-ESP bytes, verify no target
mount remains and no import-store reservation exists. The native session itself
records ordinary payload/root unmount observations. Only afterward normally unmount
provisioning journal/tool mounts and shut down the disposable guest. Confirm power-down
and host process absence separately; neither replaces canonical teardown.

Actual run: `49ae7708-8113-4d53-9dea-7fdcb3bf7b81`, .NET 8.0.20, QEMU/KVM, three GPT
virtual disks plus a read-only tools CD. The initial preflight caught culture-dependent
tool-manifest ordering before reservation; rebuilt v4 execution passed. The final
provisioner adds explicit target/journal role checks; those checks were reapplied
read-only to this run's retained blank-state evidence, not by repeating preparation.
A future one-pass construction/provisioning invocation is not claimed as already run.

The successful generation is reserved and cannot be reused for subsequent import.
A follow-up must provision a fresh target/generation and add an explicitly authorized
lab import scope with the existing external pin, canonical FAT32 source and separate
import journal. This smoke scope cannot be upgraded by changing a JSON provider flag.

## Historical preceding GPT runtime checkpoint

## Committed-baseline GPT runtime continuation

See [actual execution and remaining ownership break](../../../docs/architecture/debian-canonical-lab-acquisition.md).
The new GPT runtime has executed this executable on Linux .NET 8.0.20. Whole
inventory and lab correlation pass; canonical session/import have **not** run.

Read-only actions (no plan, reservation, mounts or import):

```text
dotnet CanonicalImportQualification.dll --observe-inventory /protected/collector.py EXPECTED_SHA256 /new/probe.json
dotnet CanonicalImportQualification.dll --observe-lab /protected/collector.py EXPECTED_SHA256 /protected/host-binding.json /new/probe.json
```

`host-binding.json` is controller-provided protected lab evidence, not arbitrary
JSON authorization. It carries LabRunId, HostObservationSha256, Created and
ReopenedHost arrays of InstallerLabBackingV1. The controller must independently
verify launch and FD ancestry and correlate creation inode/length before delivery.
The probe freshly reads all guest disk serials/physical sectors and uses the shared
inventory validator. It cannot create a Windows identity or obtain an import lease.
Outputs are create-new diagnostics, explicitly **not** session journal receipts.

`--smoke-development` uses the same six arguments and authority as
`--run-development`, but omits ImportConfiguredRoot. Inspect verifies acquired
leases/mounts; it does not claim imported content. All journal and ownership
preconditions still apply. This action is built, not natively qualified.

Runtime construction tooling is `../canonical_lab_runtime.py` (host) and
`../canonical_lab_guest_build.py` (construction guest). Host `create(repo, publish)`
acquires fixed .NET 8.0.20 bytes, verifies their Microsoft SHA-512, stages authenticated
retained libicu76, and creates only new file-backed storage. Run
`observe_construction(run)` before the guest builder. The builder copies runtime OS
trees only, creates a new GPT runtime, and unmounts normally. Request normal guest
poweroff and exit its console shell; verify process exit before `start_gpt(run)`.
`observe_gpt(run)` must pass before `../canonical_lab_guest_storage.py` provisions
new target/journal disks. These scripts have no migration/production entrypoint.

The actual first run required a separate readonly tools CD to install missing ICU
and a protected-mode correction for the collector; those findings are incorporated
into the construction sources. The revised single-pass construction source has not
been rerun. Source hashes for subsequent revisions must not replace historical tool
hashes. Two failed serial package-transfer files remain retained outside Git.

Storage provisioning records blank state, GPT intentions/readbacks and filesystem
readbacks on the persistent runtime before journal directories exist. It creates
separate root-owned 0700 directories on a separate EXT4 disk. It does **not** yet
produce a lease-authorizing complete collector-based receipt chain or invoke
DebianImportJournalStorage.Verify. The remaining tagged ownership seam must be
implemented before claiming the smoke test, source staging or full import.

The previous sections below remain historical evidence for the earlier transport
lab; its MBR runtime must not be reused as canonical qualification evidence.

This executable composes the existing resolver, authority, persistent supervisor,
semantic importer and Linux journals. Development only: no provisioning, synthetic
inventory, fabricated format receipt, package execution or firmware operations.

```text
dotnet build tests/installer/CanonicalImportQualification/CanonicalImportQualification.csproj -warnaserror
dotnet run --no-build --project tests/installer/CanonicalImportQualification -- --check plan.json descriptor.json external-pin.json runtime.json journals.json
```

The plan is `DebianConfiguredRootImportPlanV1` with actual ownership/format evidence,
not fictitious user/agent fields. The descriptor argument is diagnostic only;
execution rereads source from the canonical payload. The external pin is not
renewed. The retained artifact's pin expires **2026-10-05T09:32:27.464Z**.

For that artifact `SingleFile` still exits **2** with
`ConfiguredRootContentExceedsFat32SingleFile`. Explicit `Transport: "Chunked"` uses
`TransportManifestSha256: "A093432AE281F4620959B56E35774DC0E5D01C74443DCD33F248DA723B364757"`.
`OtherPayloadBytes` budgets other required payload files; allocation rounding and
a 64-MiB reserve are additional. Changed transport changes the plan fingerprint;
it never authorizes replay of an already reserved generation.

An eventual `--run-development` requires all of:

- Disposable isolated Linux VM without passthrough/shared folders/NIC/host sockets/
  EFI; newly provisioned virtual storage; retained actual create/format/readback
  receipts; separate preserved ESP/sentinel object. This harness does not provision
  storage or manufacture Windows receipts.
- Linux .NET 8 and qualified collector/runtime tools. Whole inventory must pass the
  shared resolver. No WSL disk filtering or loop-as-physical-ID shortcut.
- `runtime.json`: `DebianSessionRuntimeV1` with protected root-owned tools and exact
  hashes, including `root_transport.py`, session/import/observer/journal modules and `/usr/bin/dpkg-query`.
- Source files on leased read-only payload at `configured-root/<BuildId>/<DerivationId>/`.
  Descriptor includes the attestation. Chunked selects `descriptor.json`,
  `root.manifest.json`, `transport.json`, `root.content.0000` through `.0004`.
  SingleFile selects `root.content`. No wildcard discovery or reconstruction exists.
- Two distinct existing root-owned 0700 directories on a nominated persistent EXT4
  installer-runtime filesystem outside all protected/target disks. `journals.json`
  provides `SessionStore`, `ImportStore`, `ToolPath`, `ToolSha256`,
  `RuntimeFileSystemUuid`. Fresh native readback and the same canonical resolver
  reject tmpfs, aliases, wrong UUID and protected placement before reservation.
  Journal calls recheck directory device/inode/mount ID. No production location
  has been qualified here.

Actions: AcquireLeases, PrepareImportMountpoints, MountRoot, MountPayload,
ImportConfiguredRoot, Inspect, UnmountPayload, UnmountRoot, Close. ESP stays an
acquired read-only lease, **unmounted**. Scaffolding stays outside the empty root.

Session/import stores separately reserve the same generation/plan. Records bind
session, leases/protected set and artifact build/derivation. The session result
links the reopened import reference/hash. Console output is diagnostic, not durable
evidence. Content success and exact teardown are separate. Cancellation/supervisor
disappearance is not teardown proof. No retry, resume, cleanup or reformat exists.

No canonical native run occurred here. Fixture success does not qualify real GPT/
EXT4/FAT32/full-artifact execution. Prior standalone EXT4 evidence remains unchanged.

## Reproducing the bounded transport lab

`../chunk_lab_vm.py` creates a NEW qcow2 derivative of the retained powered-off
overlay plus NEW 40-GiB target and 8-GiB journal virtual disks. It records launch
intent/arguments and starts BIOS QEMU/KVM without NIC, shares or passthrough.
Before guest provisioning independently inspect `/proc/<qemu-pid>/fd` and `fdinfo`:
only the new regular files may be writable; both retained backings must be
read-only; no physical block FD may exist. Keep that host evidence.

`../chunk_lab_console.py <serial-socket> <script-file>` transfers trusted scripts
over serial only. Stage a new tool directory and record hashes, preserving old
verifiers/attestations. The guest shell uses `stty -echo -icanon -ixon`. No artifact
content, credentials, shared folder or network source is sent through this channel.

`../configured_root_transport_qualification.py --produce <retained-artifact-dir>
<new-output-dir> <existing-external-pin>` verifies original source and creates
chunks using the native producer. A **separate process** with `--verify
<chunk-directory> <new-result-prefix> <existing-external-pin>` reopens all chunks
and runs the full original semantic verifier. Its retained development anchors
are source trust, not target authorization or release authentication.

After host ancestry proof, run `../chunk_lab_payload.py` **inside this new VM**.
It requires exact nominated virtual serial/size and blank signatures/partition
before-state; creates GPT test partitions/filesystems; records before/intent/
readback under `/chunk-lab-evidence`; checks capacity; copies create-new; normally
unmounts and remounts FAT32 read-only. Run `--verify` against that mounted payload.
Failed provisioning is not retried. Observations retain lab provenance and are not
converted into synthetic native Windows preparation or provider-identity receipts.

Actual host workspace: Ubuntu-24.04
`/var/lib/igloo/chunk-labs/cac91c4b60f946a2ae46ef78e7f42ae1/`.
Guest source `/chunks-original`; delivery
`/chunk-payload/configured-root/99695826-82bc-4077-b4cf-a22cebc706da/6072529e-1484-4fc7-a68c-d05c970bb393/`.
All eight physical objects fit. Full verification passed from FAT32 and ordinary
output. A separate full-sized corrupt derivative was rejected before import.
Normal delivery unmount was followed by fresh mountinfo absence verification.

**Canonical harness execution is blocked:** the actual complete collector reports
`Unsupported / NonGptDiskVisible` because the inherited runtime bootdisk is MBR.
Linux .NET 8 is absent. Required next: separately qualified GPT runtime for ALL
visible disks, authenticated matching runtime/tools, a supported observed lab
provider/preparation receipt contract, and persistent EXT4 journal directories
validated by `ObserveImportJournalsAsync`. Do not invent Windows UniqueIdFormat,
BusType or volume GUID equivalence from serials, or filter out the bootdisk.

There is no canonical reservation/import result, post-import readback or session
teardown receipt from this run. FAT32 delivery teardown is a separate narrower
result. A formatted journal disk alone does not qualify persistent journal placement.

## Post-import configuration continuation (blocked native milestone)

See the [configuration contract](../../../docs/architecture/debian-post-import-configuration.md)
and [attempt evidence](../../../docs/architecture/debian-post-import-configuration-evidence.json).
The current attempted operation is consumed. Do not rerun its command, replace its
operation ID, delete reservations or treat its checkpoint as automatic retry authority.
The user selected implementation completion and a blocked report after the failure.

The implemented entrypoint is:

```text
dotnet CanonicalImportQualification.dll --configure-lab <protected-predecessor-directory> <continuation.json> <runtime.json> <journals.json> <external-pin.json>
```

This is a protected development composition, not a general destination CLI.
`canonical_configuration_lab.preserve` independently verifies the powered-off
successful import/Close, checks open descriptors and backing identities, then
create-new copies, hashes, fsyncs and reopens a read-only evidence checkpoint.
`canonical_storage_lab` constructs a new runtime launch while binding the same
target/journal inodes; it does not format or import again. Its configuration branch
stages only protected current code, prior records and external trust input.
`canonical_configuration_guest` verifies protected runtime/tool placement, collects
the complete inventory, checks the original EXT4 journal UUID, bootstraps separate
0700 stores under the predecessor hash and dispatches the actual harness. The
canonical journal-placement verifier independently validates those stores.

`LabConfiguration` reopens predecessor chains, validates fresh continuation storage,
derives canonical leases and runs AcquireLeases, PrepareImportMountpoints, MountRoot,
MountPayload, ConfigureCore, Inspect, ordinary reverse unmount and Close. The latter
steps require successful configuration; the failed native run did not reach them.
`canonical_configuration_readback.verify` independently checks raw session/effect
chains and observer bindings. Missing results are OutcomeUnknown and never authorize
replay. Retained prior import records are byte-identical; working backing hardlinks
are not the preserved independent checkpoint copies.

The native failure was the missing artifact `/boot/efi` directory. The corrected
configuration view privately masks `/boot` read-only and maps the verified empty
root `/mnt` to child `/boot/efi`, without changing the imported root or mounting
the actual ESP. `configuration_broker_rehearsal.py` exercised this exact view/gate
in five ordinary-directory native fixtures. They do not exercise Debian's actual
Exim/TLS/locale/account helpers or establish a configured filesystem result.

Rebuilt regression commands and results are recorded in the attempt evidence.
The fixed source needs a separately authorized native qualification; no retry,
checkpoint restoration or fresh-derivative workflow is implied by these instructions.

## Explicit checkpoint-derived experiment

--configure-derived-lab takes the same five protected inputs as --configure-lab
plus a separately protected authorization file. canonical_configuration_derivation
verifies the checkpoint and copies, reserves the host authorization outside the
copied journals, and rejects aliasing or incomplete publication. The guest stages
that exact envelope and the current code, then uses the shared canonical verifier.
Stores are configuration-derived/<attempt-id>/session and effects. This is not
a new format or preparation generation. The actual executed authorization is now
consumed; copying the journal or changing IDs does not authorize another attempt.

The actual corrected view passed current native fixtures, but configuration stopped
before Files because /etc/default/locale is a symlink. Final source instead writes
/etc/locale.conf and keeps the link. That final correction has not had a native
configuration attempt. See the new evidence for executed hashes, full test commands,
independent record readback and the distinction between provisioning unmount, normal
shutdown and the absent canonical teardown receipt.

## Retained helper-contract prerequisite (2026-09-29)

`configuration_broker_rehearsal.retained_helpers(stage, source, manifest, destination,
parameters)` is an opt-in isolated-VM fixture API, not an installer entrypoint. It
requires a fresh ordinary directory destination and the read-only, noload checkpoint
copy. Use the authenticated manifest and canonical public parameters. It copies the
full retained dependency tree, rechecks the baseline, then uses the shared closed
commands, real broker/gate, sealed inputs and fresh independent delta observers.
Failed roots/records must be retained. It has no retry or cleanup operation and grants
no canonical ownership. Keep its explicitly authorized pass budget outside the API.

The two recorded passes used policies v3 and v4. Final v5 is a different revision;
do not relabel those records or rerun their roots. Future qualification requires
fresh explicitly authorized resources, protected current staging, successful full
helper sequence and then a separately reserved canonical checkpoint derivative.
The unchanged ContinueLabCheckpoint and --configure-derived-lab path remains the
only canonical route. No configured target handoff is currently available.
# Historical initramfs inspection status (2026-09-29)

The latest successor investigation is recorded in
[debian-initramfs-continuation.md](../../../docs/architecture/debian-initramfs-continuation.md).
The configured v5 predecessor was independently reopened and fully reverified
read-only in an isolated runtime. This does not qualify initramfs generation.
The new candidate view/parser and predecessor readback are incomplete foundations;
there is no canonical initramfs command to invoke yet. No helper pass or canonical
attempt was consumed. Do not run the incomplete rehearsal as a qualified recipe.

## Initramfs canonical successor (2026-09-30)

**Latest:** the corrected-wire canonical attempt passed generation, publication, full
installed-image/delta verification and exact teardown. See
[the new evidence](../../../docs/architecture/debian-initramfs-canonical-wire-evidence.json).
The following first-attempt description remains historical. Retained success is not
replay authorization or target-boot readiness.

The helper candidate is qualified for its recorded lab scope. The canonical
successor is implemented but its one positive attempt stopped after Baseline and
before generation; publication, full result and canonical teardown remain
unqualified. See [the current handoff](../../../docs/architecture/debian-initramfs-continuation.md)
and [canonical evidence](../../../docs/architecture/debian-initramfs-canonical-evidence.json).

The explicit lab entrypoint is:

```text
--initramfs-derived-lab configured-predecessor declaration.json authorization.json runtime.json journals.json external-pin.json candidate-plan.json
```

`--reopen-configured-predecessor <directory>` is read-only and grants no effects.
The effect entrypoint requires protected raw import/configuration/Close chains,
the exact retained configuration plan, validated independent-copy authorization,
fresh complete inventory, current external development authentication, and real
journal placement. `canonical_initramfs_lab` extends the existing host copy flow;
`canonical_initramfs_guest` bootstraps separate stores and an executable runtime
workspace without formatting or manually mounting the target. Check both Windows
backing-volume capacity and guest capacity before provisioning. The retained
candidate declaration is rederived from current authenticated target inputs.

Do not rerun the consumed attempt. The source immediately after that failed run fixed its numeric lease-role
adapter defect and added stopped-operation recording; at that checkpoint it had only
regression coverage. A future separately authorized run must rebuild/stage it,
repeat canonical preconditions, and retain new results. Success must pass the
full candidate observer, create-new publisher, installed-image/configured-delta
observer, raw chain verifier and ordinary exact teardown. `canonical_initramfs_readback.verify`
rejects the incomplete retained attempt. No image handoff or target-boot readiness
is available from this canonical run.


### Numeric initramfs wire regression (2026-09-30 continuation)

The startup request and lease response are emitted by the production .NET
serializers. `DebianConfigurationContinuationTests` can export its explicitly
synthetic validated fixture by setting `IGLOO_INITRAMFS_WIRE_FIXTURE` to an external
directory before rebuilding/running that test. Set the same variable to the
Linux-visible directory when running `test_initramfs_wire`. The Python regression
runs the actual adapter, numeric-role/UUID parsing and candidate launch construction;
only native acquisition, generation and independent process observations are doubled.
The ordinary-file publisher is real. This is contract coverage, not lab authority.

```powershell
$env:IGLOO_INITRAMFS_WIRE_FIXTURE = 'C:/igloo-lab-runtime/wire-fixture'
dotnet test tests/Igloo.Migration.Tests/Igloo.Migration.Tests.csproj --no-restore -m:1 -warnaserror --filter FullyQualifiedName~DebianConfigurationContinuationTests
```

```sh
IGLOO_INITRAMFS_WIRE_FIXTURE=/mnt/c/igloo-lab-runtime/wire-fixture python3 -B -m unittest discover -s tests/installer -p 'test_initramfs_*.py'
```

The fixed wire representation remains numeric roles `[0,1,2]`, accesses `[1,0,0]`,
and filesystems `[EXT4,FAT32,FAT32]`. Canonical lease UUIDs use uppercase text;
the candidate uses the same UUID in lowercase canonical text. No string/numeric
role coercion is accepted. Inter-step failure records retain a closed location code
and the authority's completed-step count; they confer no replay or teardown authority.

## UserData producer fixture (separate from canonical import)

The selected-document producer now has a real nonempty Linux fixture qualification.
See the current [UserData handoff](../../../docs/architecture/debian-target-root-deployment.md#selected-document-userdata-producer-fixture--2026-09-30)
and [evidence](../../../docs/architecture/debian-userdata-producer-evidence.json).
No canonical reservation, agent installation or first-boot execution is part of it.

Export the shared contract using the actual rebuilt .NET serializer:

```powershell
$env:IGLOO_USERDATA_WIRE_FIXTURE = '<new external fixture-input directory>'
$env:IGLOO_USERDATA_FIXTURE_OPERATION = '<fresh separately authorized fixture operation GUID>'
dotnet test tests/Igloo.Migration.Tests/Igloo.Migration.Tests.csproj --no-restore -m:1 -warnaserror --filter FullyQualifiedName~DebianUserDataTests
```

The generated `plan.json` and non-personal `data.json` are fixture inputs, not source
acquisition authority. Without the operation variable the test uses its fixed regression
identity. Native acceptance requires a fresh declared operation and isolated fixture
runtime. Preserve consumed resources; do not rerun a fixture under the same
store or replace its files. For another authorized acceptance pass, export a fresh
operation through the real producer and record its binding before execution.

Stage the current producer, observer, existing dependency modules, rehearsal and wire
files under protected root-owned directories in the disconnected disposable runtime.
Check current host/guest capacity and the actual staged hashes/permissions. No target
or journal disk copy is needed. Only the independent runtime copy and read-only tools
media were attached for this qualification. Run the explicit small-fixture tests there:

```sh
IGLOO_USERDATA_WIRE=/opt/igloo-userdata-tools/wire \
IGLOO_USERDATA_TOOLS=/opt/igloo-userdata-tools \
IGLOO_USERDATA_NATIVE_UNIT=1 \
python3 -B /opt/igloo-userdata-tools/test_userdata_producer.py -v
```

After a separately recorded acceptance reservation, the protected
`userdata_rehearsal.py --fixture` creates the synthetic selected source/account/home
and runs the real producer and independent observer. It accepts no arbitrary source
or destination argument. The fixture records remain under
`/var/lib/igloo/userdata-fixtures/<operation>/`. `userdata_entry --reopen` validates
all three v2 terminal records, their intent/context hash-links and the unchanged v1
completion envelope independently. The .NET test's optional
`IGLOO_USERDATA_NATIVE_RESULT` directory supplies the exact reopened `receipt.json`
and `result.json` to the fixture decoder. This validates only UserData's envelope and
bound fixture result; it does not waive any mandatory first-boot receipt or authorize
installation of that receipt. Production and all canonical scopes remain closed.


### Canonical selected-document continuation (implementation only)

Current: [corrected-input attempt](../../../docs/architecture/debian-userdata-canonical-corrected-attempt-evidence.json)
qualified the native wrong-operation rejection and reached verified Baseline, then
failed before transfer. Its one positive/negative allocation is consumed.
Post-failure constructor/journal-namespace corrections are fixture-tested, not
native-qualified. Exact canonical teardown remains unverified. See the latest
[handoff](../../../docs/architecture/debian-target-root-deployment.md#corrected-input-userdata-canonical-attempt--2026-10-01).
The paragraphs below describe earlier attempts and grant no replay authority.

Latest: [2026-10-01 execution](../../../docs/architecture/debian-userdata-canonical-execution-evidence.json)
passed capacity, ten isolated admission fixtures and bounded payload staging, but
the single positive dispatch stopped before session creation. Both positive and
negative dispatch allocations are consumed; the historical instructions below
are not permission to replay them. The final strict external-pin schema correction
is rebuilt/tested but has not been natively dispatched. See the
[current handoff](../../../docs/architecture/debian-target-root-deployment.md#canonical-userdata-execution---2026-10-01).

The new `--userdata-derived-lab` arguments are, in order:
`<initramfs-predecessor> <current-declaration> <protected-authorization>
<runtime> <journals> <external-pin> <transfer-plan> <delivery-record>`.
All are protected references; no arbitrary source/destination path is accepted.
Use `DebianUserData.Plan/Serialize`; regression operation IDs and fixture receipts
are never canonical authority. Read the current
[handoff](../../../docs/architecture/debian-target-root-deployment.md#canonical-selected-document-successor-implementation---2026-09-30)
and [evidence](../../../docs/architecture/debian-userdata-canonical-evidence.json).

Capacity blocked this run before provisioning/reservation. Native staging, receipt
publication, positive transfer and negative rejection remain unqualified. Reuse the
existing isolated launch/runtime-copy tooling and current backing correlation.
The bounded functions are `canonical_userdata_lab.derive`, guest `stage_documents`,
host `finalize_staging`, and `stage_predecessor`. Stage only the copied payload,
shut down that runtime and bind its changed backing before canonical launch.
Guest `prepare` bootstraps separate journals; canonical verification must accept
placement independently. Run `execute(..., negative=True)` before the one positive
`execute`, after capacity/trust checks. These functions grant no retry authority.

Independently reopen exported records with `canonical_userdata_readback.verify`.
Harness exit zero alone is not the final handoff. Preserved-object verification,
exact canonical teardown and normal shutdown remain separate requirements.

Actual-serializer regression export (synthetic effects only):

```powershell
$env:IGLOO_USERDATA_SESSION_WIRE = '<external regression directory>/session-wire'
$env:IGLOO_USERDATA_ADMISSION_EXPORT = '<external regression directory>/wire'
$env:IGLOO_USERDATA_WIRE_FIXTURE = '<external regression directory>/fixture-wire'
dotnet test tests/Igloo.Migration.Tests/Igloo.Migration.Tests.csproj --no-restore -m:1 -warnaserror --filter 'FullyQualifiedName~DebianUserData|FullyQualifiedName~DebianConfigurationContinuationTests'
```

In Linux set `IGLOO_USERDATA_SESSION_WIRE`, `IGLOO_USERDATA_ADMISSION_WIRE` and
`IGLOO_USERDATA_WIRE` to those actual exports. Load `test_userdata_session`,
`test_userdata_admission.WireTests`, `test_userdata_producer.WireTests` and
`test_configured_successor` with unittest and `tests/installer` on `sys.path`.
Set `IGLOO_USERDATA_ADMISSION_RETURN=1` there to export synthetic bundle/results.
In Windows set that variable to the wire directory and run the rebuilt test
`PythonTerminalAndAdmissionReturnToRealDotNetConsumer`.
`IGLOO_USERDATA_ADMISSION_NATIVE_UNIT=1` is reserved for ordinary-file admission
fixtures inside the isolated runtime; it was not enabled on WSL/Windows.

### Read-only UserData readiness

Before allocating a new derivative, invoke the current harness with:

```text
--userdata-readiness <protected-predecessor> <protected-external-pin>
    [<protected-declaration> <protected-authorization>]
```

This calls the same predecessor, complete-pin and optional operation readers as
the canonical entrypoint. It does not acquire blocks, mount filesystems, create a
session, reserve operations or authorize effects. Historical continuations use
their recorded guest observations; active continuation still collects fresh
inventory and guest witnesses. Exact enclosing journal hashes remain distinct
from the native observation's authenticated representation. Duplicate JSON fields
are rejected before chain decoding.

The pin's descriptor, semantic manifest and logical content must match the
verified import ancestry; current validity is checked again at dispatch. A
byte-identical protected parser copy does not establish protection of its original
path. In the native negative, the unmodified request must first pass readiness;
changing only its operation must then reach
`OperationBinding / UserDataOperationBindingRejected` before any session effects.
An earlier rejection is not qualification of that guard.