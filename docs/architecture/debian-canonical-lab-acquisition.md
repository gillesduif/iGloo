# Debian GPT lab acquisition continuation

Latest successor: [post-import configuration attempt](debian-post-import-configuration.md) is blocked; the earlier successful import remains a separate verified checkpoint.

Current milestone: the [full canonical lab import](debian-canonical-lab-import-evidence.json)
completed on 2026-09-29, with independent readback, reopened results and exact teardown.
Production and downstream configuration remain blocked. Earlier sections are historical.

The original report below is retained as a historical checkpoint. The later
[storage-session milestone](#later-storage-session-milestone--2026-09-28) supersedes
its ownership/lease, journal-placement and storage-smoke blockers only.

Baseline: `25794d402aa5d22ca7849d33a4f06078e99818b0`, branch
`refactor/community-fleet-foundation`, initially clean. This continuation is
**incomplete**: runtime and read-only acquisition progressed; canonical session
smoke and full import did not execute. It does not close issue #241.

The [scoped runtime evidence](debian-canonical-gpt-runtime-evidence.json) retains
actual probe outputs, device identities, source readback and references to protected
raw evidence. Final source hashes are explicitly separate from executed binary hashes.

## Exact-byte checkpoint

The rebuilt transport test initially failed (15 passed, 1 failed). Binary reads
of HEAD, its parent and checkout proved that the transport envelope acquired one
terminal LF: 1121 bytes / `A093432AE281F4620959B56E35774DC0E5D01C74443DCD33F248DA723B364757`
became 1122 bytes / `43BE8D1FC68F29273BA13E80D892674A0DA53258C5B27F72BDF34D1976AAA75B`.
A read-only `mtype` read from the retained FAT32 image independently reproduced
the original envelope. Only those original bytes were restored, without parsing
or reserialization. The rebuilt embedded-resource suite then passed all 16 tests,
including rejection of LF, CRLF, BOM and JSON-equivalent reformatting.

The neutralization export was also restored to its separately retained 55,916-byte
`qualification.json`, SHA-256 `D38FAE0A29E58190EB00D7F163478D35CB5711E42C1A7105F480408E1AC23FD3`.
That hash binds the complete documentation export. No privacy change was reversed.
Only these two files have scoped `-text` attributes and no-final-newline EditorConfig
rules. The transport-evidence and final-delivery JSON summaries were inspected but
not changed: their hashes identify embedded transport bytes or separate retained
observations, not the containing formatted JSON export. No digest was replaced.

## Actual runtime execution

Lab run `28df6134-bff5-4f94-a07e-3ed442caac64` is retained under
`/var/lib/igloo/canonical-labs/28df6134bff54f94a07e3ed442caac64/` in Ubuntu-24.04.
The new construction overlay used the old factory only as a read-only backing.
Fresh host `/proc` FD observations checked launch arguments, regular-file backing
inodes/sizes/access modes and absence of physical block FDs before guest mutations.

A new 24-GiB GPT/EXT4 runtime was built from the runtime OS directories, excluding
factory/artifact workspaces and home/root contents. Construction shut down normally.
The qualification runtime then booted directly with the retained Debian runtime
kernel/initrd copied from the **new** filesystem, `root=PARTUUID=...`, BIOS/no EFI,
no NIC, shares, passthrough or imported-target boot. It had only the new runtime,
40-GiB target and 8-GiB journal disks. A later runtime-dependency provisioning boot
also attached a read-only tools CD; the whole collector included it as ISO9660.
No disk was omitted. This is a specific QEMU/KVM runtime, not hardware equivalence.

Microsoft .NET **8.0.20 linux-x64** was acquired from its fixed official release
URL and verified against the release metadata SHA-512:
`4b801177e009ec710d5e2511500aa60bec95811add50fdc59b00a5c486840c66f00c48f272cf7a2f8129ed158ec5b887d1dd60594e612f749ba48fcf48af9cd2`.
No SDK/dependency upgrade or host installation occurred. The first actual harness
invocation failed because ICU was missing. The retained authenticated
`libicu76 76.1-4 amd64` archive (SHA-256
`C1BF762996DE9ECBA9B9D871E4928A8090F023A3E9E7FA3240B3D90F892A01DC`)
was installed in the runtime only. Two serial delivery experiments timed out;
their partial files were retained and never installed. Read-only CD delivery
succeeded. A subsequent probe correctly rejected checkout-derived mode 0777 on
the collector; runtime provisioning set that exact tool to root-owned 0644.
The source now stages protected modes and ICU up front; that revised one-pass
construction sequence has not itself been rerun end-to-end.

The actual Linux `CanonicalImportQualification` executable then ran
`--observe-inventory` and `--observe-lab`. The shared collector/protocol reported
Available for all three GPT disks, six partitions and the tooling CD. A changed
backing-inode negative case returned exit 2 / Ambiguous / LabBackingOrLaunchChanged.
An earlier diagnostic serializer error on unavailable `Observation.Value` was
fixed and the negative case rerun. This negative case is **read-only acquisition**,
not the requested negative through a canonical import session.

## Acquisition and receipt field mapping

| Required fact / owner | Normal producer | Actual lab observation | Binding and remaining limit |
| --- | --- | --- | --- |
| New backing ownership | Windows preparation authority for real storage | Exclusive host create-new file, inode/length, QEMU open FD and launch | Trusted host/hypervisor/exclusive runtime; not a physical disk identity |
| `CanonicalDiskIdentityV1.UniqueId/UniqueIdFormat/BusType` | Windows storage provider | No Windows provider observation | **Not supplied**; serial is never substituted |
| GPT disk GUID, size, sector sizes | Native inventory | Shared collector plus fresh sysfs sector read | Joined to host backing/serial declaration; full inventory retained |
| Partition GUID/type/geometry | Create result plus independent GPT readback | Actual sfdisk intents and fresh tables | Lab transition evidence only; no retrospective before-state |
| `CanonicalVolumeIdentityV1.VolumeGuid` | Windows volume manager | No Windows volume GUID | **Not supplied**; PARTUUID/FAT UUID/EXT4 UUID are distinct |
| Filesystem identity | Independent format readback | Actual blkid EXT4/FAT32 UUID and type | Tagged lab format facts; not a Windows format receipt |
| Generation | Durable preparation intent | `eee69ac3-52eb-4054-ba4a-292193ac8bd2` before owned partition creation | Actual new lab disks; never resets an import reservation |
| `CreateInWindows`, `WindowsEsp`, boot-file receipts | Windows preparation/handoff | Lab provisioning and preserved empty test ESP | Cannot truthfully claim Windows creation or boot-chain evidence |
| Session/import store | Nominated runtime storage and native observer | Separate root-owned 0700 directories on journal EXT4 | Provisioned/read back; **DebianImportJournalStorage.Verify not invoked** |

`InstallerLabAcquisition.Correlate` joins host creation/reopen bindings with the
complete shared `InstallerEspBinding.ValidateInventory` result and fresh guest
serial/sector observations. Its distinct versioned lab record contains no Windows
provider/volume fields and has no conversion to Windows identities.
`VerifyCreation` and `VerifyFormat` implement structural transition validation
and retain lab provenance, generation and before/intent/readback references.
Those transition methods are fixture-tested, not yet exercised against a complete
independently collected before/after sequence in this VM. The current provisioning
script records native tables and filesystem readbacks, but does not yet feed these
methods with full collector snapshots at each transition. It is not an authorized
preparation engine or a lease-producing provider.

The read-only probe's protected host declaration is supplied by the trusted lab
controller. Its hash is a reference to host evidence, not a signature or self-issued
authorization. Correlation cannot establish truth against a compromised host root.

## Exact remaining call-chain break

Connected here:
host file/launch/FD observations → new GPT guest → protected collector → Linux .NET
`InventoryProbe` → `LinuxInstallerInventoryProtocol.Parse` →
`InstallerLabAcquisition.Correlate` → independently reopened diagnostic result.

**Not connected:** tagged lab acquisition/creation/format receipts →
`PreparedLayoutV1` / `InstallerEspBindingV1` → `InstallerBlockLeases`.
The v1 validators require native Windows volume/provider fields and
`CreateInWindows`; their meaning was not broadened. A provider-tagged installation
ownership representation and shared structural validation across those contracts
are still implementation work. Passing serials/UUIDs as Windows fields, weakening
`CanonicalRecoveryIdentity`, or hand-writing a successful preparation receipt is
not a supported bridge. The current lab records therefore cannot authorize the
existing persistent session, journal verifier, mount actions or importer.

`--smoke-development` now expresses acquisition/mount/Inspect/reverse-unmount/Close
through existing authority ordering, omitting ImportConfiguredRoot. It has been
built, **not executed** against canonical lab leases. It does not bypass the missing
ownership bridge or create an import success record.

## Evidence limits and handoff

All original 4,641,457,180 FAT32-delivered chunk bytes were read again, with every
chunk digest and the original aggregate SHA-256 matching. Descriptor, stream,
manifest, chunks, external pin and prior evidence were not regenerated. The pin
matched its retained digest and remained within its original October 5 expiry.
No new full semantic verification or canonical payload import occurred.

Target and journal provisioning are real native operations on newly created lab
files. The target root remains fresh filesystem scaffolding. A before/after hash
of the preserved **lab** ESP and root directory readback matched around the native
negative acquisition case. This does not prove actual Windows preservation.

There is no session reservation, import reservation, semantic import result,
independent imported-root/package/neutral-state result, handoff, or canonical
teardown receipt. Provisioning mount unmounts and normal VM shutdown are separate
events. Persistent journal directory creation is not journal-placement qualification.
Source transport on the new target payload also remains to be staged/verified.

ProductionAuthentication remains Unsupported, NativeSupported remains 0, and all
preparation/registration/recovery/readiness gates are unchanged. Target configuration,
initramfs, agent/UserData/Enrollment and boot/firmware work remain outside this task.
The mission is not complete; the blocker is unfinished tagged ownership/session
integration, not an expired pin or missing permission.

## Validation in this continuation

Windows checkout, rebuilt current sources:

```text
dotnet test tests/Igloo.Core.Tests/Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Preflight.Tests/Igloo.Preflight.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Community.App.Tests/Igloo.Community.App.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Migration.Tests/Igloo.Migration.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Migration.Tests/Igloo.Migration.Tests.csproj --no-restore -m:1 -warnaserror --filter FullyQualifiedName~DebianRootTransportTests
dotnet build Igloo.sln -warnaserror
dotnet build tests/installer/CanonicalImportQualification/CanonicalImportQualification.csproj --no-restore -m:1 -warnaserror
dotnet test Igloo.sln --no-build --no-restore -m:1
git diff --check
```

Final counts: Core **363**, Preflight **343**, Community.App **58**, Migration
**399**, Fleet **153**, ISO **19**, USB **23**. Serialized solution: **1,358 passed,
0 failed, 0 skipped**. The 16 transport cases and 10 lab-acquisition cases are
subsets, not additional totals. Both builds passed with zero warnings/errors.
The initial transport failure was the committed newline regression, then repaired.

Ubuntu-24.04, unprivileged fixture suite against this checkout:

```text
python3 -B -m unittest discover -s tests/installer -p 'test_*.py'
```

**293 passed**, including the existing import/transport/metadata fixtures, five
new lab-runtime failure fixtures and two exact-byte evidence tests. This count
does not include the separately executed VM probes. Privileged broker/isolation
fixture scripts were not run in this continuation. Full canonical source rejection,
session smoke, full import, result/journal reopen and canonical teardown were not
executed. No power-loss or reboot-durability claim is made.

The native VM used the published .NET harness, with actual Available inventory and
correlation, then exit 2/Ambiguous on changed host backing evidence. Provisioning
and acquisition diagnostics are retained separately from nonexistent session/import
records. A first publish attempt to a temporary Windows path failed with MSB3094;
publishing to a new simple lab staging path succeeded without dependency changes.
All modified/untracked task files were separately checked for trailing whitespace,
BOMs, private machine identifiers and accidental large artifacts. Git emitted only
existing LF/CRLF conversion warnings. No analyzer suppression was introduced.

## Later storage-session milestone — 2026-09-28

**Executed and qualified for this isolated lab storage scope. No artifact import.**
[New evidence](debian-storage-session-smoke-evidence.json) records the actual
starting HEAD `25794d402aa5d22ca7849d33a4f06078e99818b0` and the initial 12 modified /
10 untracked files with byte hashes. Those changes were preserved; no Git history
or branch operation occurred. The exact-byte repairs remained unchanged.

### Provider boundary and shared checks

`InstallationStorage.VerifyLab` accepts tagged `InstallerLabStorageEvidenceV1`
inputs only after full host/guest correlation and the complete ordered
before/create/format/readback sequence. It checks the actual creation and format
intents against run, generation, role, geometry and preceding observation hashes.
Each transition also passes `InstallerLabAcquisition.VerifyCreation/VerifyFormat`.
The resulting `ValidatedInstallationStorage` has no public/deserialization
constructor. It retains `IsolatedFileBackedLab`, version 1, `StorageSmoke`, run,
generation and the evidence digest. Caller JSON is not a verified context.

`InstallationStorage.VerifyClosure` is shared with the existing Windows
`InstallationOwnership.ResolveCore`: complete inventory validation, uniqueness,
parent disk/GPT/sector/geometry correspondence, and exact protected-disk partition
closure. Windows producer validation, v1 records and fingerprint inputs retain
their original meaning. The lab does not produce `UniqueId`, `BusType`, Windows
volume GUIDs, `CreateInWindows`, Windows ESP boot files or recovery identities.

`InstallerBlockLeases` uses the validated context and fresh guest serial/sector,
whole-inventory and device-number observations. Lab bindings carry a separate
`StoragePartition` and explicit provenance; their Windows `Partition` is null.
Windows lease JSON retains the original exact shape (regression tested). No lab
binding is converted to a Windows record. Native acquisition rejects mixed
representations and compares provenance to the private declaration. Locator or
canonical disagreement invalidates the session; no descriptor replacement occurs.

`DebianMountSessionAuthority.ForLabStorageSmoke` has no artifact/import plan or
authenticator. Its closed scope permits only the storage actions. Production
import support still returns Unsupported for its real lease set. The native
protocol and schema-2 session records retain provider/scope/run/generation and
plan/evidence bindings. Existing Windows session-record serialization is unchanged.

### Native execution

New lab run: `49ae7708-8113-4d53-9dea-7fdcb3bf7b81`.
Preparation: `143faa07-7012-4e54-b5c4-04e349552248`.
Session: `9ddbe1d4-98d4-4d35-be23-c271dff2f8fa`.

The powered-off retained 24-GiB GPT runtime was copied to a new file. Full source
SHA-256 before/after remained
`AEA60285A9362B01C11E9BC110F8CF30CC38F1083EB936482D6DE5787DD13519`.
Fresh 40-GiB target and 8-GiB journal files were created. Host launch/FD readback
verified their inodes, lengths and access modes, plus the read-only tools CD.
The final guest exposed all three GPT disks and the optical device to the shared
collector. No disk filtering, physical passthrough, NIC, shared folder, firmware
interface or imported-target boot was used.

The copied .NET 8.0.20 runtime's UID-1000 placement was corrected **inside the new
runtime**. Dotnet, runtime libraries, current harness/Core/Debian assemblies and
native modules were independently inventoried as root-owned/protected. System
interpreter/tools, dependent-library ownership and Python library placement were
checked before privileged session execution. The launch environment is explicit.
Executed hashes are separate from current source hashes in the new evidence.

Platform GPT initialization and preserved lab ESP/journal formatting preceded the
new preparation generation. The bounded provisioner then created and formatted
Linux ESP, payload and root one at a time, with six native .NET transition
validation invocations and independently reopened provisioning records. No
retrospective before-state was used. Provisioning evidence lives on runtime EXT4
before session/import journal bootstrap; it does not self-authorize journal creation.

Actual `ObserveImportJournalsAsync` and `DebianImportJournalStorage.Verify` accepted
two separate root-owned 0700 directories on journal EXT4 UUID
`0a040944-4f8f-40d9-aa1a-def96571a2ee`, outside the target/protected disk. Both stores
were placement-validated; the import store remained entirely empty.

The SAME native authority/private pipe/supervisor executed:

1. AcquireLeases (root RW, Linux ESP RO, payload RO).
2. PrepareImportMountpoints (generation-specific scaffolding outside target content).
3. MountRoot and MountPayload (sibling connected mounts; payload RO).
4. Inspect (fresh canonical witnesses and `ConnectedImportView`, independent namespace,
   mount/path/identity readback, no child/stacked/detached/alias topology).
5. UnmountPayload, UnmountRoot, Close (ordinary exact teardown; fresh absence readback).

Linux ESP remained unmounted. The preserved lab ESP whole-device digest was
unchanged: `7FD44E194D3DBB9F465F855D77B00A4044E9E66EA64DA53650866286C1893C1F`.
A read-only post-teardown filesystem observation showed only `lost+found` beneath
root. This does not claim that mounting EXT4 caused no filesystem-journal writes.

All 17 session records were independently reopened again after completion; their
hash chain, generation, session, plan and provider matched. Exact unmount results
are records 12 (payload) and 14 (root); Close is record 16, SHA-256
`A8B10F697BDD4BECEE7CA6760A6725B57BD18142AF8B6C890B1E93DD7384B7C4`.
The journal/tools bootstrap mounts were subsequently normally unmounted. Normal VM
power-down and fresh host process absence were separately observed. Namespace or
process disappearance was not used as canonical teardown evidence.

### Failures and negative case

An oversized serial code-staging command did not execute. A fresh guest check
observed no staging/evidence directory and a blank target. Smaller bounded code
transfers succeeded. These were runtime code bytes, never workstation artifacts.

The first positive smoke preflight rejected tool-manifest ordering before any
session reservation. `ImmutableSortedDictionary`'s comparer order differed from
ordinal path order; the verifier now explicitly orders keys ordinally before the
same exact-set comparison. Missing or extra tools are still rejected. The successful
session used rebuilt v4 assemblies. No reserved or poisoned generation was replayed.

A separate negative declaration/generation with a changed reopened backing inode
went through the **same `--smoke-development` entrypoint**, returning exit 2,
`Ambiguous / LabBackingOrLaunchChanged`, before journal reservation, block acquisition
or mounting. Read-only collection did occur. It is not the old correlation-only probe.

### Validation and next boundary

Current rebuilt Windows checks: Core **375**, Preflight **343**, Community.App
**58**, Migration **406**. Serialized solution **1,377 passed**, zero failed/skipped;
Linux unprivileged discovery **297 passed**. Solution and harness builds: zero
warnings/errors. The 16 exact-byte transport tests remain within Migration; focused
lease and smoke tests are subsets, not extra totals. Final focused reruns also
verify unchanged Windows lease JSON and production rejection of lab leases.

Commands:

```text
dotnet test tests/Igloo.Core.Tests/Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Preflight.Tests/Igloo.Preflight.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Community.App.Tests/Igloo.Community.App.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Migration.Tests/Igloo.Migration.Tests.csproj --no-restore -m:1 -warnaserror
dotnet build Igloo.sln -warnaserror
dotnet build tests/installer/CanonicalImportQualification/CanonicalImportQualification.csproj --no-restore -m:1 -warnaserror
dotnet test Igloo.sln --no-build --no-restore -m:1
python3 -B -m unittest discover -s tests/installer -p 'test_*.py'
git diff --check
```

The fixture suites are not native GPT proof; the separately recorded VM run is.
No power-loss, recovery, full-artifact import or general physical-hardware test was
performed. Full artifact bytes were neither staged nor read for this milestone.

The follow-up import task requires a **new preparation generation and fresh target**,
an explicitly authenticated lab import scope (this smoke context intentionally
cannot import), the existing unchanged descriptor/pin/chunks on canonical FAT32,
and separate import reservation/readback/result evidence. The completed smoke
generation cannot be reused. Target configuration and all regeneration contracts
remain unsatisfied. ProductionAuthentication is Unsupported, NativeSupported is 0,
production preparation/registration and downstream readiness remain closed.


## Later authenticated lab import composition — 2026-09-29

This continuation starts from the existing 23 modified / 20 untracked files at
`25794d402aa5d22ca7849d33a4f06078e99818b0`; it preserves the storage milestone above.
StorageSmoke remains version 1 and cannot import. New import preparation uses
schema 2 with `ConfiguredRootImport` bound into **both** creation and format
intents before their effects. Retagging old smoke evidence fails intent validation.
`InstallationStorage.VerifyLab` still invokes the same transition, whole-inventory,
protected-set and host/guest correlation checks; no Windows identity is fabricated.

`DebianLabImportPlanV1` is a declaration, not a verified capability.
`ForLabDevelopmentImport` requires a validated storage context and the external
pin; the immutable fingerprint binds the complete plan, provenance and pin validity.
The source-only interface shares artifact/transport verification with the original
Windows plan. Windows v1 records and fingerprint serialization remain unchanged.
The explicit harness entrypoint verifies the protected external envelope and
actual descriptor before starting a session, then authenticates the actual canonical
payload descriptor again through `IDebianConfiguredRootAuthenticator`.

The native private declaration carries distinct `LabImport` provenance and the
artifact plan. It rejects mixed smoke/import declarations and lease provenance
changes. Root-only topology, fresh shared canonical witnesses and protected journal
placement are unchanged. Lab import journal records retain their top-level
plan/generation/session fields and add schema-2 provenance; existing Windows journal
records retain schema 1. Session records link the independently reopened import result.

Delivery uses file-level create-new copies of the retained exact objects, first
through a new read-only provisioning ISO, then to the freshly formatted FAT32
payload. The ISO is inventoried throughout provisioning. It is not an import source:
only canonical payload FDs supply descriptor, manifest, transport and chunk bytes.
External trust is a separate protected input. No stream reconstruction, workstation
rebuild, metadata relaxation or replacement pin is involved.

The first native attempt exposed a new envelope bug before content writes: the lab
record nested `PlanSha256`, while the existing native journal requires it at the top
level. Its import generation reservation remains retained, with no import intent.
Independent readback found only `lost+found`; the preserved ESP digest matched.
The session became OutcomeUnknown and has **no verified canonical teardown**.
Normal VM shutdown is separate. The corrected envelope is covered by the shared
session fixture's lab import case; subsequent attempts use new disks/generations.

This composition does not open production support or authorize target configuration.
The actual execution outcomes are recorded separately in the new import evidence.

### Executed full import and retained handoff — 2026-09-29

The fresh `daa72a8f-914a-4574-92ef-4d39ffb0c1e1` run completed the **full canonical
development import**, not another storage smoke. Generation
`ff104c9d-505c-4d88-8620-19b1153bd91e` imported the unchanged 4,641,457,180-byte stream
from its own read-only FAT32 payload. The independent observer matched all 144,911
manifest entries, 1,594 configured packages and the neutral-state contract.
The preserved lab ESP's before/after digest matched.

Eighteen session records and 1,823 import records were independently reopened and
chain-verified. Import result `A18E09649CD6E97EE8257EBF46F74CD0FFBF3A19E6EB49EDEF358133B2CEDDE9`
is linked from session completion. Payload then root were ordinarily unmounted;
fresh mount deltas proved exact absence. Close and normal VM shutdown were separately
verified. This is fresh-process/fsync evidence, **not a power-loss test**.

The target, payload and journal disks remain powered off under
`/var/lib/igloo/canonical-labs/daa72a8f914a457492ef4d39ffb0c1e1/`.
The protected raw records are in `guest-evidence/` and on the retained journal disk.
These locations are evidence references, not new execution authority. The completed
generation cannot import again. Later target configuration must establish its own
bound handoff and authorization; no configuration or imported-target boot occurred.

A separate fresh `ef80d64a-5da6-4799-877c-6f452ffb2964` run changed one byte in its own
payload chunk. The **same canonical entrypoint** acquired/mounted the source and
rejected `TransportChunkHashMismatch` before import reservation, intent or content
writer invocation. Its root retained only `lost+found`, and its preserved ESP hash
matched. Eleven session records reopened. The poisoned session has **no canonical
teardown receipt**; normal shutdown and namespace disappearance are not substituted
for one. This negative outcome does not invalidate the separately verified positive
run's teardown, and it does not authorize replay of either generation.

The positive run executed revision v2; revision v3 only adds the explicit pre-intent
source-rejection diagnostic and its durable session record. Executed assembly/module
hashes for both are retained separately from final source hashes. The failed initial
envelope attempt and the earlier generic source rejection remain preserved too.

See [current native import evidence](debian-canonical-lab-import-evidence.json) for
exact identities, observations, result references, test commands and limitations.
ProductionAuthentication remains Unsupported; NativeSupported remains zero.

## Later post-import configuration attempt — 2026-09-29

The [configuration continuation](debian-post-import-configuration.md) now consumes
the verified successful import without replay or new format receipts. The actual
native attempt revalidated lineage, same-backing ownership, journals and the full
neutral baseline. It stopped before the Files intent because the helper adapter
assumed an artifact `/boot/efi` directory that does not exist. The operation is
consumed: OutcomeUnknown, no configured-root success, no canonical teardown.

The corrected private helper view passed five isolated native fixtures; it was
not retried on the target. The user requested implementation completion and a
blocked native report. [New evidence](debian-post-import-configuration-evidence.json)
separates executed code from later fixes and fixture results. The powered-off import
checkpoint and failed target/journals are retained. No initramfs handoff is ready.
