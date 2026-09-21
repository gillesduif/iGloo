# Shared canonical recovery snapshot V1

Phase 2B3.1, 2026-09-21. `RecoverySnapshotV1` belongs to Community and Fleet
together. Core owns its contracts, pure parsing, dependency analysis, validation,
comparison and serialization. Preflight owns Windows acquisition through the
existing canonical readers. Fleet does not own the Windows recovery engine.

This milestone implements read-only representation and capture composition. It
does not implement durable product integration, restore, destructive execution,
or a Fleet readiness evaluator. `RecoveryReadiness.Production` remains
`ObservationUnavailable / NotImplemented`. PreCommitGate semantics are unchanged.
Synthetic fixtures demonstrate representable Exact scopes; this is **not** a
claim that an Exact production Windows recovery snapshot has been captured.

## Declared scope and dependency closure

`RecoveryScopeV1` declares the workflow, whether its mutation footprint has been
resolved, the observed Windows Boot#### identity, explicit BCD object and firmware
variable mutation slots, and whether RTC registry state is included. Slots may
have an observed absent before-state. Successful BCD enumeration is required to
establish an absent BCD slot; a failed enumeration cannot do so.

`WindowsBootConfiguration` covers the selected Windows boot configuration and
its active recovery dependencies, including the before-state of explicitly named
configuration mutation slots. It does not cover staged payload file writes or
storage transitions. A caller's scope declaration is not an authorization or a
proof that an arbitrary operation fits it. Future orchestration must derive and
validate that relationship before accepting the snapshot for a mutation.

`CommunityDirectInstallBootRegistration` records the intended future boundary,
requires the RTC section, and currently always reports an unresolved footprint.
Setting `MutationFootprintResolved=true` cannot make that workflow Exact. The
current implementation chooses identifiers dynamically, and no complete durable
operation-to-scope integration has been implemented.

The assessment derives four explicit evidence classes; callers cannot exclude
an inconvenient required object by labelling it unrelated:

| Class | Meaning in V1 |
| --- | --- |
| Required | An active dependency or explicit mutation slot, with typed evidence needed for exact representation. |
| RelevantOpaque | Relevant bytes/text whose dependency semantics cannot be established, such as opaque BCD elements or nonempty uninterpreted EFI optional data. Retained; blocks Exact. |
| ObservedUnrelated | Captured evidence outside the declared dependency closure. Retained in the artifact with a typed exclusion reason. |
| UnsupportedRelevant | A required format, schema, role or device-path shape without supported interpretation. Retained; blocks Exact. |

The fixed roots are Windows Boot Manager, firmware boot manager, the independently
resolved current Windows loader, configured WinRE loader when present, and each
present explicit BCD mutation object. Closure follows object references, ordered
inheritance lists, recoverysequence, resume references, bootsequence, and device
AdditionalOptions recursively through file/RAM-disk parents. Object role checks
distinguish managers, Windows loaders, resume applications, inherited settings
and device-option objects. A default reference and a loader resume reference
share an element number but have different containing object roles.

The managers' displayorder/toolsdisplayorder lists are retained exactly and in
order, but their alternative entries do not expand closure unless independently
required. Likewise BootOrder is captured in full without requiring interpretation
of every unchanged USB/historical Boot#### entry it names. This exclusion is
limited to restoring the ordered selection list while leaving those alternative
objects unchanged. Explicit mutation slots, the selected Windows entry and a
present BootNext target are always required. A future workflow that mutates an
alternative must include it, making unsupported or opaque state block Exact.

## Model and authority

`src/Igloo.Core/Recovery/` contains immutable records for the aggregate and its
versioned scope, BCD, firmware, ESP, WinRE and optional RTC sections. The aggregate
also contains UTC capture time, target binding, independent readback evidence,
diagnostic metadata and `CanonicalHash`. Unsupported section versions are
distinct from observation failures. Existing `BootState`, `BootSnapshot`,
`IRecoverableBootAdapter` and the Phase 2A fake adapters remain unchanged; this
milestone does not wrap the Community installer in those adapters.

Every observation retains `Available`, `Unavailable`, `Unsupported`,
`AccessDenied`, `Ambiguous` or `Absent`. Failed facts have no fabricated value.
Present zero, false and empty data remain distinguishable from failed reads.

Canonical volume binding combines provider physical identity/format/bus, GPT
disk GUID, disk geometry and sector sizes, partition GUID/type/geometry, volume
GUID and filesystem. The binder requires unique inventory joins and rejects
reduced identity, non-GPT storage, duplicate ownership and inconsistent facts.
Disk/partition numbers may join rows within one successful inventory; they never
enter the persisted canonical identity. Drive letters are informational only.
V1 deliberately supports a conservative subset of physical identity providers.

BCD objects retain numeric object/element types, GUIDs, typed scalar/list/device
values and each read's availability. Qualified GPT partition devices preserve
both disk and partition GUIDs. File/RAM-disk devices preserve kind, path, parent
and AdditionalOptions. Ordinary and qualified observations coexist: qualification
can replace a native partition locator in the semantic representation only when
the remaining structure agrees. Disagreement is Ambiguous. Unknown provider
data retains raw bytes when exposed and diagnostic MOF otherwise; MOF/localized
text is never canonical identity, and required opaque state is not Exact.
Every required qualified device must correlate to captured canonical storage.

The Preflight BCD extension uses only `OpenStore`, `EnumerateObjects`,
`OpenObject`, `EnumerateElements` and `GetElementWithFlags`. It extends
`IWindowsBcdReader` without changing the legacy firmware-listing API or using
`BcdListingParser` as recovery authority. Null provider values fail observation
instead of converting to zero. The published current-entry alias GUID is used
for OpenObject, and an unresolved alias is rejected. Microsoft's
[OpenObject contract](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/openobject-bcdstore),
[qualified device read](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/getelementwithflags-bcdobject)
and [BCD sample constants](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/Win7Samples/winbase/bootconfigurationdata/bcdsamplelib/Constants.cs)
define these read operations and identities.

Firmware representation retains native variable status, raw bytes, native
variable attributes as a separate observation, parsed BootOrder/BootNext and
EFI_LOAD_OPTION fields: load-option attributes, description, raw device-path
list, raw nodes, optional data and parsed GPT/file identity. The pure parser
accepts structural GPT HardDrive -> FilePath -> End as the supported Windows
path. It verifies lengths, UTF-16, GUID and geometry; a GUID byte occurrence is
never identity. Other shapes remain raw with Unsupported interpretation.
Required nonempty optional data and unsupported load-option attribute semantics
currently block Exact: a BCDOBJECT substring does not prove their dependencies.
The binary framing follows the [UEFI boot manager specification](https://uefi.org/specs/UEFI/2.11/03_Boot_Manager.html).

BootOrder is an ordered sequence of little-endian ushorts; malformed lengths and
duplicates are rejected. BootNext is exactly two bytes when present. Native 203
means absent, 5/1300/1314 access denied, 1/50 unsupported; other failures remain
unavailable and malformed payloads ambiguous. Contradictory native status cannot
be certified as absence or available data. EFI load-option attributes are not
native variable attributes. V1 supports restoration representation only for
observed standard native attributes 7; it never infers them from a payload.

The ESP association joins strong disk identity, GPT disk/ESP partition GUIDs,
partition geometry, canonical ESP volume, Boot#### GPT path and EFI executable
path to BCD Windows Boot Manager and the canonical Windows system volume.
`bootmgfw.efi` is identified by canonical volume/path, byte length and SHA-256.
The read-only file reader verifies the final handle's canonical volume path.
Required ESP identity, FAT32/type/geometry, BCD device/path and firmware path
must agree. A hash proves captured content identity, not successful execution.

WinRE records configured enabled state, BCD recovery loader, canonical recovery
volume and Winre.wim canonical path/content identity. Enabled state requires
these facts and agreement with recoverysequence and both RAM-disk devices.
Disabled state can retain configured information or independently observed
absence; a stale required BCD dependency still needs capture. Merely finding a
GPT recovery partition establishes neither configuration nor readiness.
`WinReImageAssurance.ContentIdentityOnly` explicitly excludes a bootability claim.

RTC is included because DirectInstallService writes `RealTimeIsUniversal`.
V1 captures the fixed HKLM 64-bit registry path, key existence, value absence or
the exact native registry type and raw bytes. No string expansion, terminator
repair or conversion to a boolean occurs. Capture uses query-only handles and
independent reopen; access errors and a changing value remain explicit. This is
the Windows RTC interpretation setting, not capture or alteration of the clock.
The raw read follows [RegQueryValueExW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regqueryvalueexw).

## Exactness, hashing and comparison

Existing `BootRecoverySupport` remains Exact / Partial / Unsupported, accompanied
by typed issues and observation availability. Exact requires supported schema,
resolved supported scope, all required observations and canonical correlations,
complete typed dependency closure, supported retained raw state, a valid hash,
and successful independent readback. Unsupported relevant state yields
Unsupported; other incomplete evidence yields Partial. Neither authorizes a
mutation. Even Exact describes captured state; it is not a Ready decision or a
guarantee that Windows/WinRE will boot.

Canonical serialization uses UTF-8 JSON with ordinal property ordering, sorted
object/element/entry sets, lowercase D-format GUIDs, invariant numeric encoding,
and base64 raw bytes. Semantic lists such as BootOrder and BCD object lists keep
their order. Schema and section versions participate in the state hash.
`CanonicalHash` is uppercase SHA-256 over that canonical semantic projection.

Capture time, transient disk/partition/letter locators, diagnostic codes, native
error diagnostics, provider text and independent-readback metadata do not affect
the state hash. Unrelated observations remain in the full serialized artifact
but do not affect semantic comparison. Required raw firmware payload bytes do
affect the hash: a partition-number field actually stored inside EFI data is
retained machine state, unlike a transient storage-enumeration ordinal. A changed
payload is conservatively Changed even if its canonical GPT identity still
matches. Qualified native BCD locator changes alone do not affect semantic hash.

The state hash is **not a hash of every byte of the artifact**. Durable storage
must additionally bind the complete serialized artifact in its local manifest,
including diagnostics and readback evidence. Deserialize only parses;
`Assess` verifies structure and state hash before use. A matching/recomputed
hash cannot make malformed or incomplete evidence Exact. A future durable reopen
must verify both artifact integrity and this structural assessment.

`Compare` is pure. It validates both snapshots before returning ExactMatch or
Changed. Otherwise it returns Missing, ObservationUnavailable, Unsupported or
Ambiguous with typed issues. AccessDenied is retained in issues and grouped as
ObservationUnavailable in comparison. Ambiguity takes precedence, then failed
observation, unsupported state and absence. Exceptions/read failures are not
interpreted as a changed or missing machine state. Two incomplete snapshots do
not verify each other merely because their hashes match.

## Current capture support and blockers

`WindowsRecoverySnapshotCapture` composes the existing WindowsStorageReader,
WindowsBcdReader and WindowsFirmwareReader, plus fixed read-only canonical-path
and RTC queries. It takes two independent captures for a bounded consistency
check; this is not an atomic machine snapshot and does not reserve the machine.
No new acquisition path invokes the existing destructive adapters or enables
firmware write privileges. No new live-machine probe was run in this milestone;
the composed capture API itself has not yet been proven on the elevated host.

Production capture intentionally cannot produce Exact today:

1. The canonical native firmware getter does not expose variable attributes.
   Those remain Unsupported, rather than guessed as 7.
2. A locale-independent typed configured-WinRE reader is not implemented. All
   configuration facts remain Unavailable/TypedWinReConfigurationNotImplemented;
   neither reagentc text nor BCD flags substitute for that contract.
3. Phase 2B3's qualified active WinRE RAM-disk read failed. Required failed
   qualification and opaque/native parents remain explicit blockers. No
   undocumented RAM-disk blob parser was added.
4. Nonempty Windows EFI optional data is retained but its dependency semantics
   are not proven. Required unsupported device paths or additional storage
   dependencies outside V1's captured volumes also block Exact.
5. The combined typed reader, current-loader resolution, identity correlation
   and independent recapture need real-host validation after these gaps close.
6. The current Community workflow has no validated durable mutation footprint or
   storage/payload recovery boundary. A resolved-scope boolean cannot bridge it.

## Community mutation audit and future product boundaries

The local uncommitted source was inspected, including DirectInstallService,
DirectInstallViewModel, EfiBootEntries, LinuxRemovalService, the existing
recoverable adapter contracts, Fleet protected state and PreCommitGate.

| Existing Community operation | Recovery boundary needed |
| --- | --- |
| Prepare: shrink/delete/create/reuse partitions; format and assign letters | Separate recoverable-storage transition contract; excluded from this snapshot. |
| Prepare: stage ISO/kernel/initrd/GRUB, EFI boot files, configs and installer/seed files | Explicit file/payload before-state and journal contract; excluded from configuration-only V1. |
| Register: select/write Boot####, set BootNext, prepend/reorder BootOrder including matching installer entries | Shared firmware snapshot plus a resolved exact variable footprint and future journal. |
| Register: delete description-matched stale BCD objects, copy bootmgr to a new GUID, set device/path, delete locale/inherit, set fwbootmgr bootsequence | Shared typed BCD closure plus explicit existing/new object slots; current dynamic text selection must be replaced. |
| Register: write RealTimeIsUniversal as REG_QWORD 1 | Exact raw registry before-state in the shared RTC section. |
| UI: prepare, later register, then reboot | Durable verified boundary before the corresponding mutation stage; currently absent. |
| Linux removal: remove firmware entries/references, whitelisted Linux EFI/config files, partitions/reclaim space, and RTC value | Separate declared removal footprint, file/storage recovery and journal; not covered wholesale. |

Boot/recovery configuration and storage-transition rollback have different
restore semantics. Binding a snapshot to a partition does not provide shrink,
partition creation, formatting or file-content rollback. BitLocker is not changed
by this milestone and exact-volume BitLocker gating remains an independent check.
The bootmgfw.efi and Winre.wim hashes identify referenced existing content; this
snapshot does not contain replacement file bytes and cannot cover overwriting
those files. Such an operation needs a separate durable content recovery contract.

Future Community integration must follow: capture Exact for the declared operation
-> persist durably -> reopen independently -> verify artifact/hash/structure
-> revalidate machine/target identity -> perform journaled boot mutation -> inspect
result -> recover only through a journaled restoration workflow. DirectInstallService
must move behind that boundary in stages; wrapping its monolith would give a false
recovery guarantee.

Future Fleet integration uses **the same snapshot**: canonical capture -> protected
Fleet state -> manifest/artifact hash and independent reopen -> RecoveryReadiness
evaluator -> existing PreCommitGate -> execution authorization boundary -> journal
-> mutation. The gate must retain all Phase 2B2 checks and never consume authorization.
No part of either future mutation/restore chain is implemented by this milestone.
Igloo.Fleet.Web continues to reference Contracts only, with no Domain/Persistence/
Server reference added.

## Deterministic verification

Fixtures cover canonical identity and ordinal/letter independence, deterministic
serialization/hashing/reopen, raw firmware retention, structural EFI paths and
unsupported options, BootOrder/BootNext states, BCD roles and dependency graphs,
qualified GPT association, failure propagation, WinRE identity/content assurance,
RTC raw types/absence/reopen, scope rejection and all comparison outcomes. Tests
never read the developer machine's BCD, firmware, WinRE or registry. Final solution
validation and counts are recorded in [Fleet Phase 2](../fleet/phase-2.md).
