# Installation target identity

The opt-in target API establishes this invariant: a receipt identifies the exact
new GPT partition returned by an explicit iGloo creation operation, on the exact
authorized GPT disk, with the complete resulting partition identity set unchanged
when Linux inspects it. Device paths are runtime outputs, never authorization.

This component performs no Linux installation, formatting, mounting, GPT repair,
bootloader work or migration. Deepin remains blocked. Existing distro recipes keep
their legacy behavior; their earlier VM validation is not evidence for this new
boundary.

## Identity and consistency

`MigrationManifest.installationTarget` contains version 1 of
`InstallationTargetClaim`. See the [schema](../schemas/installation-target.schema.json)
and [shared fixture](../../tests/fixtures/installation-target.json).

| Field | Meaning |
|---|---|
| `disk.diskGuid` | Primary GPT disk identity. Require exactly one visible match. |
| `rootPartitionGuid` | Primary GPT unique partition GUID (Linux PARTUUID) of the newly created root. |
| `espPartitionGuid` | Explicit existing ESP identity on the same disk; nullable only for a consumer that does not require an ESP. |
| `disk.logicalSectorSize` | Exact logical sector size, currently 512 or 4096 bytes. |
| `disk.diskSizeBytes` | Exact geometry consistency check, never disk selection. |
| `disk.partitions` | Complete unordered set of unique GUID, GPT type, byte offset and byte length for every allocated partition. Detects added, removed, replaced and changed partitions, including unrelated partitions. |
| `installationId` | Must equal the manifest ID and the independently supplied expected ID for the active installation run. |
| `ownership: created-by-igloo` | Creation receipt provenance. Only the post-operation capture path may publish it. A hand-written flag does not establish ownership. |

Model, size, type, labels, filesystem UUIDs, enumeration order, Windows disk
number, Linux `/dev/...` names and partition slot numbers cannot identify an
authorized target. GPT GUIDs survive device renaming. Start/length and sector
checks detect a stale extent even if somebody retains a GUID. The root must have
the Linux filesystem GPT type; the authorized ESP must have the ESP GPT type.
Other Linux partitions and multiple ESPs are permitted when present in the
approved layout. None is chosen by type or position.

Geometry is expressed in bytes to avoid implicit 512-byte LBA assumptions.
All extents must be sector aligned, positive, non-overlapping and inside the
disk. New roots additionally leave at least 1 MiB at both disk ends. The Linux
GPT reader enforces the actual usable-LBA bounds and verifies primary/backup
header and partition-array CRCs and agreement. Hybrid MBR, unsupported GPT
extensions and malformed GPT-looking media fail closed. Linux sysfs `start`
and `size` count 512-byte sectors even on 4Kn media; the collector converts them
to bytes before comparing them with GPT.

## Windows preparation

`IInstallationTargetPreparer` is implemented by
`WindowsInstallationTargetPreparer` and registered in the app. Discovery now
exposes optional GPT disk/partition GUIDs and logical sector size. These are
selection facts, not ownership. Missing facts do not receive invented defaults.

1. Select the disk GUID, obtain a fresh strict snapshot with `ReadLayoutAsync`,
   and explicitly authorize an already-free `[offset, offset + length)` extent
   and the exact existing ESP GUID. The caller owns that authorization decision.
2. Supply `InstallationTargetRequest` and the pending migration manifest. The
   manifest must explicitly contain schema version 1 and the same nonempty
   installation ID, and must not already contain a claim.
3. Freeze the approved snapshot. Resolve exactly one `MSFT_Disk.Guid`, refresh
   the storage cache and re-query through `MSFT_DiskToPartition`. Compare the
   entire current layout to the approved snapshot before creation.
4. Call `MSFT_Disk.CreatePartition` on the revalidated object with explicit byte
   offset, size, sector alignment and Linux GPT type. No maximum-size mode,
   disk initialization, formatting, drive letter, resizing or existing-root reuse.
5. Require the returned embedded partition object and its exact GUID/geometry.
   Refresh and re-query again. Require exactly one new partition, matching that
   returned GUID and the authorized extent, with all previous partitions and
   disk geometry unchanged. Only then construct the claim.
6. Publish into the existing `migration-manifest.json` using a flushed temporary
   file and atomic replacement. Compare the pending file's SHA-256 before
   publication and retain unknown migration fields. A changed file, cancellation,
   provider error or failed postcondition stops the operation. Never retry
   creation, delete a partial partition or infer its identity from the extent.

The Windows storage primitives and their units are documented in
[MSFT_Disk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk),
[MSFT_Partition](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition),
[CreatePartition](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/createpartition-msft-disk),
[Refresh](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk-refresh)
and [MSFT_DiskToPartition](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disktopartition).
`MSFT_Disk.UniqueId` is a different hardware/provider identifier; it is not the
GPT disk GUID. The local WMI class schema was inspected, but the creation method
was exercised only through fake providers in this task.

This first API deliberately accepts already-free space. It does not call the
legacy shrink, seed reuse, largest-gap or `EnsureRootPartition` paths. A future
Windows orchestration step must explicitly reserve space by exact partition
identity, finish any seed/ISO partition creation, then obtain and authorize the
final pre-root snapshot. It must not infer free-space ownership from the old
shrink request. Any later partition creation invalidates the finalized claim.
Interrupted preparation requires an explicit recovery decision; retry/adoption
is not implemented.

## Reboot and Linux resolution

The receipt travels inside the existing migration manifest; there is no second
target-state file. New manifests receive a random installation ID when generated.
Old manifests retain `null` IDs/claims when deserialized and remain usable by
legacy consumers. A strong consumer rejects them explicitly. The outer schema
stays at 1 because the additions are optional; the nested claim has its own
required version. Unknown claim fields/versions, duplicate JSON keys, missing
required fields, noncanonical GUIDs, invalid numeric values, oversized files and
excessive nesting are rejected by the strict readers. Unknown ordinary migration
fields are preserved. Schema validation cannot establish creation provenance or
layout equality; runtime checks remain mandatory.

`distros/_shared/installer/igloo_target.py` provides a stdlib-only resolver:

```text
python3 igloo_target.py --manifest /explicit/read-only/location/migration-manifest.json \
  --expected-installation-id <ID bound to this installation's boot handoff>
```

The future boot driver must deliver the expected ID independently from the
candidate manifest, for example in its staged boot configuration. Copying the ID
out of an arbitrary discovered manifest defeats the stale-run check. This task
does not implement a Deepin boot entry or select/mount seed/ISO media. Stable
seed/ISO identity and content validation belong to that later boot handoff;
they are not needed to resolve the root/ESP from an explicitly supplied receipt.

The CLI validates the receipt before opening any devices. Its collector opens
whole block devices read-only, checks both GPT copies, and binds each GPT slot
to the current kernel partition extent and block major/minor number. It re-reads
GPT after binding and checks for inventory changes. It does not use a partition
number as cross-reboot identity. Sparse-file tests use the same GPT parser.
Known non-GPT media can be excluded after inspection; unreadable or corrupt
GPT-looking devices cannot be silently skipped, since they could hide a clone.

`resolve` requires a complete trusted inventory, rejects duplicate disk or
partition GUIDs globally, requires the entire claimed layout to match, and only
then returns `diskDevice`, `rootDevice`, `espDevice` and `installationId`. A path
may change from NVMe to SATA naming, and array/slot order may change, while the
identity remains the same. The CLI prints no target output on failure. It never
formats the ESP, or any other partition.

## Limits of the boundary

The threat model is accidental misselection, stale manifests, changed layouts,
device reordering, interrupted preparation and visible cloned-GUID collisions.
GPT GUIDs and a nonce are not cryptographic attestation. An administrator who
rewrites the receipt, or substitutes an exact clone while removing the original,
can defeat them. Byte-identical restoration cannot reveal a historical write.
Partition labels, attributes and entry numbers are not part of the portable
identity set; the two actual GPT copies must still agree. This component does
not promise to detect every possible GPT byte change.

Preparation and manifest staging require a single coordinating writer. Atomic
replacement prevents publishing truncated JSON; the digest checks detect
intervening changes but are not a filesystem compare-and-swap against an
uncooperative process replacing the file after the last check.

Resolution proves an observation, not an exclusive lease on storage. A future
writer must revalidate immediately before mutation, establish appropriate
quiescence/exclusivity, reject mounts/holders and unexpected existing filesystems,
and never treat a receipt from a completed installation as permission to format
again. This task grants no formatting permission or installed-system recovery
workflow. No success here enables Deepin.

## Legacy findings, kept separate

The earlier concerns were confirmed directly in current code:

| Existing path | Finding |
|---|---|
| `EnsureRootPartition` | Reuses any Linux-type partition; otherwise fills the largest free extent without capturing a returned identity. |
| `BuildStoragePartitionList` | Assigns every Linux partition `root` and `wipe: superblock`; assigns every ESP `esp`. |
| Windows discovery / old manifest | Previously had no disk GUID, PARTUUID or logical-sector identity contract; model/size could not prove ownership. |
| `PartitionResizeService` | Chooses a lettered partition by maximum shrinkable capacity; computes the new size from `SizeMax`, not current `Size`. Pre-existing adjacent free space can invalidate the assumed freed extent. |
| Direct-install seed handling | Reuses/deletes seed candidates based on label and capacity, without this ownership receipt. |
| Fedora | First size match, then largest non-USB fallback; dual boot uses `clearpart --none`, while replace mode permits whole-disk initialization. |
| Debian / Mint | `biggest_free` recipes do not bind installation to the Windows-selected GPT disk. |
| Ubuntu | `size: largest` disk matching is disconnected from the selected Windows disk; the root/ESP classification above is ambiguous. Its STATUS confirms curtin still rewrites GPT despite comments claiming otherwise. |

These recipes and resize behavior were not rewritten. `PreCreateRootPartition`
is documented as legacy and cannot satisfy `IInstallationTargetConsumer`.
Deepin declares `CreatedRootAndEsp`; both existing app staging/preparation paths
refuse identity-requiring plugins before reaching their legacy disk preparation.
Deepin's own config, boot-spec and agent methods continue to throw even for a
valid claim. A future driver must use the new boundary deliberately.
