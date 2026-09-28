# Canonical configured-root import qualification

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
