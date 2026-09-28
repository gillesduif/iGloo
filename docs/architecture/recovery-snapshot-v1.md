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
No acquisition path invokes destructive adapters or firmware setters. The canonical
firmware reader now establishes its required process-local privilege before each
read. Issue #242's elevated continuations exercised the composed capture in fresh
processes. They reproduced non-Exact results; the observations below distinguish
successful acquisition from unresolved dependency semantics.

Production capture intentionally cannot produce Exact today:

1. The native attribute acquisition gap is addressed by issue #242's extension
   of WindowsFirmwareReader to GetFirmwareEnvironmentVariableExW. Actual native
   attributes accompany successful raw reads; failed reads never publish an out
   parameter, and legacy providers still report Unsupported. Elevated readback now
   confirms attributes 7 for BootOrder, Boot0000 and Boot0002. A high-integrity token
   alone initially returned AccessDenied/1314. WindowsFirmwareReader now calls
   FirmwareNative.EnableReadPrivilege before acquisition and proceeds only when
   AdjustTokenPrivileges succeeds with native error zero. Failed token access,
   lookup, assignment (including successful BOOL with error 1300), or unknown error
   prevents the getter. Pre-acquisition failures cannot report variable absence.
   This enables an existing process right, never assigns rights or writes firmware.
   Two fresh elevated processes verified automatic Disabled -> Enabled transitions
   and successful native reads without reflection/manual privilege setup.
2. WindowsWinReReader now reads bounded, DTD-disabled ReAgent.xml version 2.0 as
   locale-independent source evidence, retaining raw XML and typed configuration
   identifiers. Its GPT disk GUID and byte offset must join uniquely to canonical
   storage before the canonical Winre.wim path/content can be observed. The raw
   InstallState integer does **not** establish a supported enabled-state contract:
   Enabled remains Unsupported/WinReInstallStateSemanticsUnproven. Missing XML is
   not interpreted as disabled WinRE. Neither reagentc text nor BCD flags substitute
   for the missing contract. Raw XML is available through ReadConfiguration; it is
   not added to the unchanged V1 snapshot schema or treated as restoration authority.
   The observed all-volume ownership blocker is now resolved using the configured
   partition's MSFT_Partition.AccessPaths. When other owners are unknown, capture
   requires complete, well-formed partition access-path observations, exactly one
   canonical volume GUID for the configured partition, no duplicate access-path
   owner, complete unique volume GUID observations, and agreement with the selected
   volume's partition GUID before CanonicalRecoveryIdentity.Bind. Distinct volume
   GUIDs outside that partition's paths are excluded from this location dependency;
   their owner failures remain failures, not invented identities. Drive letters
   cannot establish this proof. Production now captures the host's recovery volume
   and WIM content identity without filtering or replacing the storage inventory.
3. Phase 2B3's qualified active WinRE RAM-disk read failed. Required failed
   qualification and opaque/native parents remain explicit blockers. No
   undocumented RAM-disk blob parser was added.
4. Nonempty Windows EFI optional data is retained but its dependency semantics
   are not proven. Required unsupported device paths or additional storage
   dependencies outside V1's captured volumes also block Exact.
5. Four composed captures across two elevated processes reproduced the same
   canonical hash and correlated the Windows/ESP/BCD identities. Assessment is
   Unsupported, comparison is ObservationUnavailable, and IndependentReadback is
   Unavailable because the required WinRE/BCD/EFI gaps remain. Complete structural
   ExactMatch still needs real-host validation after those gaps close; identical
   hashes of incomplete observations are insufficient.
6. The current Community workflow has no validated durable mutation footprint or
   storage/payload recovery boundary. A resolved-scope boolean cannot bridge it.

Issue #242 adds deterministic projection of nested BCD provider devices through
the existing observation rows. Qualified GPT parents are accepted only with the
expected provider class, device kind and GPT style. AdditionalOptions and nested
parents are retained; native and opaque parents do not acquire invented canonical
identity. Failed GetElementWithFlags qualification remains a failure. This does
not close the active WinRE provider failure observed in Phase 2B3.

EFI optional data has application-defined semantics. Only empty optional data has
supported dependency semantics here. Nonempty bytes, including a recognizable
BCDOBJECT substring, remain opaque/Unsupported and are retained losslessly. Missing
optional-data evidence is Ambiguous; malformed load-option framing is rejected by
the structural parser. No parser claims to distinguish valid from malformed opaque
Windows-specific optional data without an established contract. Independent capture
now preserves typed missing/denied/unsupported/ambiguous recapture failures rather
than flattening them into Unavailable. None of these changes relax Exact assessment.

The initial 2026-09-26 attempt was not elevated. The later approved host process
was verified Administrator=True, high integrity, with Administrators enabled.
Canonical getters ran without machine configuration writes. The reader now adjusts
its own process token for firmware reads and fails closed if that is unsuccessful.
The remaining WinRE state, active RAM-disk and optional-data gaps and the repository's
runtime VM validation are still outstanding. See the
[issue #242 validation record](../fleet/phase-2.md#issue-242--production-capture-blockers-2026-09-26).
Primary references for the supported acquisition and its limits are Microsoft's
[firmware getter contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfirmwareenvironmentvariableexw),
[ReAgent.xml evidence](https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/winre-cannot-built-deploy-image-captured-non-uefi-computer),
and the [UEFI boot-manager specification](https://uefi.org/specs/UEFI/2.11/03_Boot_Manager.html).
The privilege success/error checks follow
[AdjustTokenPrivileges](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-adjusttokenprivileges);
volume ownership uses the existing
[MSFT_Partition access paths](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition).
[GetElementWithFlags](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/getelementwithflags-bcdobject)
documents qualified partitions but does not establish a successful nested RAM-disk
qualification on this host. No native parent/SDI substitution erases its failure.

### Interpretation review and provider limitation (#242)

The final 2026-09-26 interpretation review found no adequate contract to close the
remaining semantics. This is a limit of the evidence reviewed, not a claim that
no private Windows implementation exists. Microsoft's
[REAgentC contract](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/reagentc-command-line-options?view=windows-11)
documents `/info` as status display, without a typed output schema, InstallState
enum, or WinReGetConfig ABI. Its XML troubleshooting example does not specify all
enabled/disabled/transition semantics for WindowsRE version 2.0. Searches of the
installed Windows SDK headers/IDL and Microsoft source/documentation did not
establish a supported WinReGetConfig or WINDOWS_OS_OPTIONS contract. Community
scripts, forum logs and reverse-engineered struct layouts were not accepted as
authority. No new interpretation or private DLL call was added.

Direct calls to the same BCD provider localized the failure before projection:
for active WinRE device `0x11000001` and osdevice `0x21000001`, GetElement and
GetElementWithFlags(0) return BcdDeviceFileData with a BcdDevicePartitionData native
parent; GetElementWithFlags(1) throws COMException with HRESULT **0xD000000D** for
both elements. The HRESULT is recorded verbatim, without guessing a recovery
meaning. The canonical reader correctly reports Unavailable. The documented
[BcdDeviceFileData](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/bcddevicefiledata)
parent/path fields support retaining this evidence; they do not supply the missing
qualified GPT identity. Changing flags to zero would return the already retained
native locator, not resolve qualification. Existing success fixtures for qualified
parents and failure fixtures remain applicable; a new regression covers the actual
provider HRESULT.

UEFI 2.11 section 3.1.3 specifies the OptionalData boundary and passing its bytes to
the loaded image. It does not define the Windows interpretation of the observed
136-byte payload. Consequently its exact bytes survive snapshot serialization and
reopen, and it remains RelevantOpaque/Unsupported. Full-byte preservation and a
valid canonical hash do not prove that dependency closure is known. Tests now
verify that distinction using the observed payload through serialization/reopen.

No complete Exact recapture was attempted in this review: the prerequisite WinRE,
RAM-disk and optional-data blockers remain. Prior equal incomplete hashes are not
promoted to Exact. RecoverySnapshotV1, RecoverySnapshotRules, production readiness
and gate semantics are unchanged. See the four-row acceptance record in
[Fleet Phase 2](../fleet/phase-2.md#four-blocker-interpretation-review-2026-09-26).

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

### Phase 2B3.2: durable Community boundary

`Igloo.Preflight.CommunityRecovery` now supplies the Community-local orchestration
and artifact store without changing the shared snapshot representation. For a
declared boot/RTC request it performs Exact assessment -> durable CreateNew/write-
through/Flush(true) persistence -> independent file reopen -> artifact manifest,
full-byte SHA-256, canonical hash, structure and scope/target verification -> fresh
capture and shared ExactMatch comparison -> mutation callback. The in-memory
artifact digest is computed before the store receives the bytes. No returned
success flag or cached snapshot object substitutes for independently read bytes.

The envelope is versioned separately from RecoverySnapshotV1 and retains its
exact serialized bytes, length, content SHA-256, canonical hash and artifact GUID.
Artifacts live at `%LOCALAPPDATA%\iGloo\RecoverySnapshots\<guid>.recovery.json`,
are bounded to 16 MiB, refuse overwrite/reparse ancestors, and remain on disk
after a later failure. Flush/reopen checks exercise the filesystem durability
contract; they are not a simulated power-loss test. Durable discovery, journaled
mutation outcome tracking, restoration and storage/payload rollback remain future
work. This does not cover erasure of the Windows volume containing local evidence.

`DirectInstallService.RegisterBootEntry` enters this boundary before enabling
firmware privileges. Only its boot-registration body was separated; preparation
is unchanged. **The current direct installer cannot provide the required resolved
scope, so its production declaration remains unavailable and registration stops
before capture, persistence or mutation.** Its existing UI exception path prevents
reboot. The generic durable sequence is tested with synthetic Exact configuration
snapshots; those fixtures do not certify the dynamic installer. No generic scope
or guessed canonical identity is used to make registration pass. The existing
mutation behavior remains behind the gate for a future correctly bound integration.

Future Fleet integration uses **the same snapshot**: canonical capture -> protected
Fleet state -> manifest/artifact hash and independent reopen -> RecoveryReadiness
evaluator -> existing PreCommitGate -> execution authorization boundary -> journal
-> mutation. The gate must retain all Phase 2B2 checks and never consume authorization.
No part of either future mutation/restore chain is implemented by this milestone.
Igloo.Fleet.Web continues to reference Contracts only, with no Domain/Persistence/
Server reference added.

## Deterministic verification

### Issue #241: Community planning checkpoint (2026-09-26)

**Issue #241 remains open.** The current continuation adds a read-only immutable
candidate plan and prerequisite checks; it does not certify an executable
Community mutation footprint or enable registration. This distinction is part
of the API: candidate acquisition may be Available, but its recovery declaration
is Unsupported (`BcdFirmwareSynchronizationFootprintUnproven`). There is no public
setter or constructor that upgrades the candidate to a resolved scope.

`CommunityBootRegistrationPlan` pins the prepared canonical target, a chosen
Boot#### with an observed absent before-state, a proposed absent BCD GUID, typed
description-matched stale-object candidates, structurally parsed sibling entries,
the proposed BootNext/BootOrder bytes, BCD object/element slots, and the fixed
64-bit RTC registry slot/REG_QWORD payload. It retains immutable before-evidence
using the shared BCD, firmware, storage and RTC models. No second Windows reader
or recovery snapshot format was introduced.

Preparation now records a read-only canonical identity after staging, without
changing shrink/create/format decisions. Registration rebinds that exact identity;
disk/partition ordinals do not select its replacement. Ordinals are used once to
locate the prepared result within one inventory and for the EFI node's captured
partition-number field. Sector geometry comes from canonical identity rather
than the old fixed-512-byte/zero-on-failure fallback.

The candidate chooses from the existing 0080..00fe allocation range only after
native error 203 proves absence; an absent slot referenced by BootOrder or
BootNext is not allocated. Failed reads never mean free space, and exhausted
slots never fall back to overwriting Boot0090. Sibling search retains the explicit
0000..00ff policy, using structural GPT identity/geometry rather than a GUID byte
substring. Unsupported paths leave a blocker. Order/Next references outside that
scan are also observed. This is not a claim to enumerate all firmware variables.

Revalidation compares the entire canonical-reader planning evidence, including
the complete enumerated BCD candidate set and firmware scan. Consequently changes
to stale candidates, occupied slots, sibling entries, BCD identities, BootOrder,
BootNext, RTC or the bound target require replanning even when an object is outside
the recovery snapshot's semantic hash. Collection order is normalized. This uses
the shared observation serializer's structural equality, not a new semantic hash
or an Exact assessment. The complete proposed operation sequence is checked before
any callback, rejecting extra, missing, reordered, substituted or changed intents.

The production entry point passes this candidate through `ExecutePlan`. It cannot
reach snapshot capture, persistence or the legacy mutation callback. **The legacy
dynamic writer has not been converted into a plan-bound native executor.** Merely
changing the candidate's declaration in the future would be unsafe: that callback
must first be replaced, each native write must consume a checked planned intent,
and all implicit provider effects must be included. No successful direct-install
plan -> Exact capture -> durable reopen -> execution test is claimed. The existing
successful durable-boundary fixture is a generic configuration scope only.

The specific remaining footprint problems are:

1. Microsoft's [CopyObject contract](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/copyobject-bcdstore)
   can generate a new ID but cannot accept the proposed destination GUID.
   [CreateObject](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/createobject-bcdstore)
   accepts a GUID and type, but does not specify a caller-selected Boot#### slot
   for the firmware effects of a live firmware-application object. These contracts
   are insufficient to bind the existing copy/update/fwbootmgr sequence to the
   candidate's one explicitly chosen Boot####. No undocumented GUID/index mapping,
   guessed slot or catch-all firmware scope was added.
2. Description matching identifies candidates, not ownership. The current `/delete
   /f` path also requires a supported account of incoming-reference cleanup and
   firmware synchronization. Pinning a candidate GUID alone does not prove its
   complete deletion footprint. A stale set therefore adds an explicit blocker;
   no candidates are deleted in this continuation.
3. The proposed create intent records its boot-manager source and destination,
   but the supported typed creation/element-copy executor and complete native
   effects remain unimplemented. No speculative BCDEdit command replaces `/copy`.

#### Community dependency-closure review

| #242 evidence | Decision for the current Community operation |
| --- | --- |
| WinRE configured/enabled state | Retained. The current operation includes bootmgr copying and potentially forced deletion of description-selected objects. Their complete reference effects are not proven bounded. There is no proof that restoring this footprint can exclude the active Windows recovery chain. This does not claim registration explicitly toggles WinRE. |
| Active WinRE device/osdevice and RAM-disk dependencies | Retained with their failed qualified observations. Before excluding them, a supported executor and reference-closure analysis must prove the cloned/deleted object effects cannot involve that chain. SDI identity cannot substitute for failed RAM-disk qualification. |
| Windows Boot#### optional data | Retained as RelevantOpaque/Unsupported. Windows firmware-application synchronization is part of the intended operation, and its affected EFI identities/dependencies remain unproven. The BCDOBJECT substring is not a supported mapping contract. |

This is a conservative non-exclusion decision, not proof that each WinRE value is
directly written. A future smaller operation could justify a smaller closure,
but silently dropping the BCD path or narrowing V1 now would change the operation
without proving recovery equivalence. RecoverySnapshotV1, its Exact rules, Fleet
RecoveryReadiness and PreCommitGate are unchanged. Storage and staged-payload
rollback remain separate. No destructive host/VM migration was run; a complete
footprint, supported capture, checked executor and the repository-required VM
validation remain prerequisites for claiming the new registration path works.

### Issue #241: checked native executor continuation (2026-09-27)

This supersedes the previous checkpoint's statement that the legacy writer is
still present. **The legacy writer has been removed, but #241 is not complete and
is not yet code-complete pending only VM validation.** The unresolved native
firmware effects still prevent a resolved production recovery declaration.

`ICommunityBootRegistrationExecutor.Execute` accepts the immutable plan and a
cancellation token. Its production implementation owns the durable boundary and
constructs its private Windows backend only inside the successful boundary
callback. Callers supply neither a scope override nor new native mutation targets.
`BootRegistrationProgram` validates the complete instruction list before its first
native call and checks each identity/payload against the immutable plan. It stops
on every failure; there is no best-effort continuation, retry or automatic restore.
The program is tested with an in-memory backend, not by exposing a native bypass.

The Community registration path no longer contains BCDEdit writes, `/copy`,
description-based execution-time deletion, `/delete /f`, a free-index search, a
GUID-substring sibling search, or the zero-geometry/fallback-overwrite paths.
The old pure text parser remains only for compatibility tests; the canonical
reader's separate read-only `/enum firmware` API is unchanged. Linux removal and
its firmware helpers were not changed.

#### Deterministic BCD replication

Shared Core `BcdMutationPlanning.ReplicateBootManager` produces the desired new
object before capture, with the exact caller-selected GUID and observed Windows
boot-manager object type. Its final-content transform is:

| Source element | Planned result |
| --- | --- |
| Application device (11000001) | Replace with the target's qualified GPT disk/partition identity. |
| Application path (12000002) | Replace with `\EFI\BOOT\BOOTX64.EFI`. |
| Description (12000004) | Replace with the installer description. |
| Locale (12000005), inherited objects (14000006) | Omit from the newly created object; no transient copy followed by deletion. |
| Every other enumerated element | Replicate its supported typed value unchanged. Failed, opaque, malformed or unsupported retained values reject planning. |

The Windows writer uses documented [CreateObject](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/createobject-bcdstore)
with explicit `Id` and `Type`, verifies the returned identity/type, and then calls
the appropriate [typed setters](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/bcdobject).
There is no generated-ID fallback. Strings, integers, booleans, ordered object and
integer lists, individual object references and qualified GPT devices are
supported. The [qualified partition setter](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/setqualifiedpartitiondeviceelement-bcdobject)
takes disk and partition GUIDs, not drive letters. It cannot encode AdditionalOptions;
such retained devices and RAM-disk/file-device setters are rejected instead of
losing state. This is deterministic final-content equivalence in fixtures, not
a claim that live firmware side effects equal those of the old `/copy` sequence.

#### Deletion and revalidation

Inbound-reference analysis scans the canonical BCD graph for individual/list
references and device AdditionalOptions recursively through file/RAM-disk parents.
Unavailable, duplicate or opaque reference structure cannot prove absence. Ordinary
native partition parents expose known AdditionalOptions structure, so this analysis
can identify their BCD GUID references without pretending their storage identity is
qualified. This does not resolve #242's failed RAM-disk qualification.

A stale candidate with any inbound reference is rejected at planning time:
`BootPlanDeleteRequiresReferenceRewrite`. This implementation deliberately does
not perform incoming-reference cleanup or add implicit write targets. It also
rejects a copied object that would reintroduce a reference to a deleted candidate.
Opaque reference scans reject deletion even when that opaque object was unrelated
to the active boot recovery closure: proving no incoming references is a different
question from capturing an active dependency closure.

Immediately before each deletion the executor independently reads the canonical
graph, verifies the exact pinned GUID/type/elements against its original before-state,
and proves the incoming-reference set is still empty. It invokes `DeleteObject`
only for that GUID, with no force/cleanup command. Added, removed or changed objects
or references abort rather than expanding the plan. Each BCD instruction also
checks the expected intermediate graph, accounting only for earlier planned
instructions, and final graph readback checks the result. A changed graph after
earlier writes stops further execution; those earlier writes are not claimed
rolled back. Observations and writes are not a machine-wide atomic transaction.

Immediately before native entry the durable boundary still revalidates the entire
planning evidence: canonical target, chosen slot, BootOrder/BootNext, scanned
entries, BCD object/reference graph, fwbootmgr bootsequence and RTC. Native firmware
writes use explicit planned attributes through SetFirmwareEnvironmentVariableExW.
RTC uses only the fixed Registry64 slot and an existing key. A missing key rejects
planning; the executor never broadens the operation into registry-key creation.

Known BCD scope slots are derived from the exact create/delete/set instructions.
The only Boot#### write slot is the new entry; siblings remain read prerequisites.
BootOrder/BootNext are fixed firmware-section slots and RTC requires IncludeRtc.
`RequireScopeSlots` rejects differing workflow or slot sets, and each instruction
must also exactly match its declared element/payload, not just its containing
object. **Coverage is not resolution:** BCD-to-NVRAM side effects are still not
represented, so Declaration remains Unsupported. There is no boolean override and
no newly Exact RecoveryScopeV1. RecoverySnapshotRules were not changed.

#### Remaining #242 dependencies and acceptance gaps

| Evidence | Operation-specific graph result |
| --- | --- |
| WinRE configured/enabled state | Retained. Copying the boot manager's supported default reference can connect the new object to the current Windows loader, whose recoverysequence reaches WinRE. No blanket exclusion was introduced. |
| Active WinRE RAM-disk qualification | Retained when reachable through that chain. A deterministic graph test follows new object -> default loader -> recoverysequence -> WinRE RAM-disk and preserves the noncanonical-parent issue. |
| EFI optional data | Retained. The Windows live-store firmware-application create/delete/fwbootmgr effects are still not mapped to exact EFI variables, so the executor's explicit calls cannot prove that the opaque Windows entry is outside all provider effects. |

A disconnected synthetic WinRE object is outside a clone's graph traversal, and
that exclusion is tested. It is **not** excluded from the complete production
RecoverySnapshotV1 scope: the native footprint remains incomplete, and the shared
Windows recovery assessment remains unchanged. A GUID substring inside optional
data is still not a supported BCD/Boot#### mapping.

The full successful Community plan -> Exact capture -> durable reopen -> fresh
ExactMatch -> native executor acceptance case remains blocked, including in tests
using this production declaration. No generic configuration scope, test-only
resolved flag or forged capability was substituted. Existing successful generic
durable-boundary tests and successful fake native-program tests are separate
proofs. Production fails before capture/persistence/native writes.

Before disposable-VM destructive validation can become the final criterion, a
supported exact firmware-effect binding and any required stale ownership evidence
must be established, the complete scope derived, and required #242 observations
resolved (or operation-scoped exclusions actually proven). VM observations alone
must not be promoted into an undocumented universal mapping contract.

For the later explicitly authorized disposable VMware validation: preserve
WIN11-BASE, clone a disposable UEFI Windows 11 client and take a powered-off
checkpoint; record canonical BCD/NVRAM/storage/RTC before-state; exercise explicit
GUID creation and typed replication under a separate instrumented test harness;
compare every resulting BCD element and observed Boot####/BootOrder/BootNext changes
against the proposed footprint, including create failure, reference races and
delete/recreate cases. Once the production guards legitimately pass, require
durable artifact reopen/ExactMatch, inspect after-state and perform the full
boot/Windows-preservation test. Record VM/firmware/Windows versions and distro.
Do not disable the production guards to perform this test or use the developer
machine. No such native mutation or VM run occurred in this continuation.

Fixtures cover canonical identity and ordinal/letter independence, deterministic
serialization/hashing/reopen, raw firmware retention, structural EFI paths and
unsupported options, BootOrder/BootNext states, BCD roles and dependency graphs,
qualified GPT association, failure propagation, WinRE identity/content assurance,
RTC raw types/absence/reopen, scope rejection and all comparison outcomes. Tests
never read the developer machine's BCD, firmware, WinRE or registry. Final solution
validation and counts are recorded in [Fleet Phase 2](../fleet/phase-2.md).

### Issue #241: firmware-effect contract audit (2026-09-27)

**Still open; not code-complete.** This continuation inspected the existing
planner/executor without replacing them. No supported pre-mutation mapping from
their live-store BCD operations to exact EFI identities was established. Only
regression tests and documentation changed. No new scope, native mutation,
firmware-only fallback, or relaxation of Exact was introduced.

#### Per-call effect boundary

All BCD writer calls open the system store with `OpenStore(File="")`. They do not
use an isolated offline store. The documented [object identity/type contract](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/bcdobject)
identifies BCD objects; it does not identify an EFI variable. Type `10100002` is
a firmware-image Windows boot-manager application, so treating these writes as
ordinary file-only BCD edits would be unjustified.

In the table, **unbounded** means the reviewed contracts do not establish the
complete affected variable set, including absence of effects on other variables.
It does not assert that every call actually changes every listed variable.

| Native call in this executor | Predetermined direct target | Boot#### / BootOrder / BootNext / other firmware effects |
| --- | --- | --- |
| `CreateObject(Id, Type)` | New BCD GUID and type, with returned GUID/type checked. | Unbounded for all four categories; no EFI index input or complete effect manifest. |
| `SetQualifiedPartitionDeviceElement` | Planned BCD GUID/element and exact GPT disk/partition GUIDs. | Unbounded; qualified storage identity is not firmware-variable ownership. |
| `SetStringElement` | Planned GUID/type/string, including application path and description. | Unbounded; no contract binding that firmware-image object to the planned Boot####. |
| `SetIntegerElement`, `SetBooleanElement`, `SetIntegerListElement` | Planned GUID/type/typed value. Each method was inspected. | Unbounded; setter signatures do not certify absence of synchronization effects. |
| `SetObjectElement`, `SetObjectListElement` on the new object | Planned GUID/type and referenced GUID(s). | Unbounded; typed reference fidelity does not bound provider firmware effects. |
| `SetObjectListElement` on fwbootmgr `24000002` | Exact fwbootmgr bootsequence element and new BCD GUID. | BootNext interaction is relevant; the referenced GUID's EFI index and a complete effect set are unproven. Boot####/BootOrder/other effects cannot be ruled out. |
| `DeleteObject(Id)` | Pinned stale BCD GUID, after full reference scan. | Unbounded; no supported EFI ownership/deletion-effect manifest. Empty incoming BCD references alone cannot certify this call. |
| `SetFirmwareEnvironmentVariableExW` | Exact name, global namespace `8be4df61-93ca-11d2-aa0d-00e098032b8c`, bytes and attributes. | Explicit calls target the chosen Boot####, BootNext and BootOrder separately. This does not certify effects of interleaved BCD calls or firmware behavior across reboot. |
| Registry64 `SetValue` | Existing RTC key, `RealTimeIsUniversal`, REG_QWORD 1. | No firmware API is invoked by this instruction. Its registry before-state remains required. |

The [CreateObject contract](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/createobject-bcdstore)
accepts the preselected BCD GUID. The [object-list setter](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/setobjectlistelement-bcdobject)
accepts BCD GUIDs, not EFI indices. The [BcdStore method inventory](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/bcdstore)
and live provider signatures provide no additional mapping parameter for the
operations above. Microsoft's [UEFI settings guidance](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/bcd-system-store-settings-for-uefi)
describes firmware ordering through fwbootmgr. The indexed **BCD store and NVRAM**
slide in [Windows Boot Environment](https://uefi.org/sites/default/files/resources/UEFI-Plugfest-WindowsBootEnvironment.pdf)
also associates displayorder/bootsequence with BootOrder/BootNext. That high-level
association is not an API contract for allocation of a chosen Boot#### or an
exhaustive per-setter effect specification. The PDF download returned HTTP 403;
only the indexed slide text was accessible in this audit.

#### Explicit ownership and why it does not resolve the whole operation

The existing explicit path already owns one proven-absent Boot####. Its index,
load-option bytes, attributes, BootNext target and ordered BootOrder payload are
fixed before capture. [SetFirmwareEnvironmentVariableExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setfirmwareenvironmentvariableexw)
provides the necessary name/namespace arguments, and the [UEFI boot-manager specification](https://uefi.org/specs/UEFI/2.10/03_Boot_Manager.html)
defines the separate load-option and order variables. This establishes explicit
call targets, not equivalence to the Windows BCD-managed path.

The legacy `RegisterBootEntryViaBcdedit` comments (in HEAD, removed from the working
tree) describe the BCD path as a workaround for firmware that discards a raw entry.
They assert Windows-managed re-synchronization, but are not a supported API
contract or proof of that behavior. The surviving staging comment describes
fallback EFI files, not proof of the BCD workaround. Removing the BCD
create/set/bootsequence operations would remove that intended mechanism; neither
its effectiveness nor equivalence to explicit firmware-only registration has
been established by this audit. Neither rewriting opaque
BCDOBJECT optional data nor adopting a GUID-to-index algorithm is supported by
the reviewed contracts. Therefore no such redesign was made. Native post-write
comparison could detect some damage after a call, but cannot replace the required
complete pre-mutation footprint or make an unplanned write safe.
The inverse interaction also needs accounting: direct firmware writes may alter
the provider's firmware-object view. The fake interpreter currently changes its
BCD graph only for BCD calls; its intermediate graph checks are not evidence that
live BCD enumeration stays unchanged after a direct Boot#### or BootNext write.

#### Elevated read-only observations

The actual tool process was Administrator=True, High integrity (S-1-16-12288),
user `IGLOO-LAB\testuser`, with enabled Administrators membership.
Read-only commands were `whoami`, `whoami /groups`, `Get-CimClass` for
`root/WMI:BcdStore` and `BcdObject`, and `bcdedit /enum firmware /v` (exit 0).
Fresh PowerShell processes loaded the built Core/Preflight assemblies and invoked
only `WindowsFirmwareSnapshotCapture.Capture`, `WindowsBcdReader.ReadRecoveryGraph`
and pure reference analysis. The canonical reader enabled its process-local read
privilege. No setter, create/delete method, alternate store or export was invoked.

- Live provider metadata confirmed the documented parameters for CreateObject,
  DeleteObject and all seven typed setters; none accepts an EFI index.
- BootOrder was `[0000, 0002]`, attributes 7. BootNext was Absent/native 203.
  Boot0080 was Absent/native 203; it was inspected, not reserved or written.
- Boot0000 retained 300 bytes, attributes 7, a supported GPT/file path and
  136 optional-data bytes. Boot0002 retained 268 bytes, attributes 7,
  4 optional-data bytes and an Unsupported path shape. Those bytes were not
  interpreted as BCD identity mappings.
- Canonical BCD enumeration returned 21 objects. The current loader was
  `4f9e620d-6f1a-11f0-b519-f7b20eb4bccb`; fwbootmgr had displayorder and timeout,
  with no bootsequence element in the successful enumeration.
- The installer-labelled object `4f9e625c-6f1a-11f0-b519-f7b20eb4bccb` had type
  `10100002`, an opaque `BcdDeviceUnknownData` device (DeviceType 5), and a default
  reference to the current loader. Whole-graph incoming-reference analysis was
  **Unsupported / BcdReferenceSemanticsUnsupported**, not an empty reference set.

The initial reporting script dereferenced the absent Boot0080 parsed value and
failed after printing the present entries. A fresh process reran with availability
checks and completed successfully; no failed observation was converted to absence.
These reads cannot establish the counterfactual effects of a future provider write.
No full Exact recapture or native mutation was attempted.

#### Ownership, scope and dependency decisions

Description matching remains only candidate discovery. Complete BCD reference
enumeration, provenance establishing Community ownership, and a complete firmware
effect binding are separate obligations. The planner rejects nonempty incoming
references instead of rewriting them; failed/opaque scans reject deletion. Even
a fully enumerated empty reference set leaves the candidate's ownership and
firmware effects Unsupported. The production boundary rejects it before capture
or any write. There is no positive ownership fixture that can substitute a
description for missing provenance.

Known direct BCD slots are derived from instruction GUIDs, the single explicit
Boot#### slot is pinned, and RTC is required. BootOrder/BootNext are fixed firmware
section state. Complete program equality rejects extra/missing/substituted calls;
scope checks reject extra/missing/duplicate known slots. **A recovery slot may cover
several typed writes to one object**: a 1:1 proof must map each executable
instruction to its covered state, not equate instruction count with object count.
That complete proof remains unavailable because implicit provider effects have no
supported mapping. Declaration stays Unsupported and MutationFootprintResolved
cannot be set true by a caller. No final RecoveryScopeV1 is derived.

| #242 evidence | Decision for the current operation | Evidence/limit |
| --- | --- | --- |
| WinRE configured state | **Required for this operation** under the current shared closure. | The retained default reference connects the clone to the current Windows loader and active recovery chain. No executor/restoration proof permits removing the configured-state requirement. |
| Active WinRE RAM-disk qualification | **Required for this operation**. | The clone -> default loader -> recoverysequence -> WinRE path is covered by graph tests. A disconnected synthetic WinRE object is outside that local traversal, not proof of production exclusion. |
| EFI optional data | **Still unbounded**. | Provider effects may reach firmware applications whose optional-data dependencies are unsupported. No exact effect set proves those entries unrelated. |

The successful synthetic **production plan** -> resolved scope -> durable reopen
-> fresh ExactMatch -> executor path therefore remains blocked. Generic durable
boundary success fixtures and fake interpreter success fixtures remain separate;
they are not relabelled as that acceptance test. RecoverySnapshotV1, Exact rules,
RecoveryReadiness and PreCommitGate were not changed.

#### Conditional disposable VMware validation checklist

This is a future acceptance checklist, **not authorization to bypass the current
blockers**. Contract/effect binding, stale ownership, scope derivation and the
successful full deterministic path must be completed first. #241 is not yet at
the stage where only VM execution remains.

1. Preserve WIN11-BASE. Create separate disposable UEFI clones for Fedora, Debian
   and Mint, with distinct lab computer identities and confirmed domain-joined
   Windows state. Use the intended migration mode and record it explicitly.
   Record Windows build, VMware version, virtual hardware/firmware, Secure Boot,
   disk topology, distro/plugin version and verified ISO identity. Retain a
   powered-off checkpoint of each joined clone and its virtual NVRAM.
2. In each clone, verify elevation in the validation process. Retain independent
   canonical before-state captures of storage/target identities, BCD graph,
   required firmware variables with attributes/raw bytes, Windows/ESP association,
   WinRE/RAM-disk evidence and exact RTC type/value/absence. Record domain trust
   and successful Windows boot. Do not include credentials or BitLocker secrets
   in the evidence bundle.
3. Retain the immutable plan and a reviewed instruction-to-scope/effect map. Record
   the actual chosen Boot#### (do not assume 0080), proof of its absence, new BCD
   GUID/type, all replicated/replaced/omitted elements, fwbootmgr bootsequence,
   BootNext target, ordered BootOrder payload and RTC REG_QWORD 1. Deletion must
   have proven ownership and complete references; any unsupported candidate stops
   the run. Account for every provider effect as well as every direct call.
4. Exercise deterministic fault cases before the destructive run: occupied slot,
   changed target/order/next/RTC, changed stale/reference graph, corrupt artifact,
   failed reopen and unavailable recapture must all prevent first mutation.
   Retain actual Exact assessment, canonical hash, independent artifact reopen,
   fresh ExactMatch and final planning-evidence revalidation for the success run.
   If production rejects the scope, stop; do not patch a flag or invoke its private
   native backend to continue the migration.
5. Once those gates legitimately pass, execute the normal Community migration in
   the disposable clone with instrumentation retaining each planned native call,
   result and before/after evidence. Stop on unexpected or unknown effects. Do not
   retry into a different GUID/Boot#### or use description-based cleanup. Before
   reboot, independently inspect complete affected BCD/firmware/RTC state and
   verify that all changes belong to the declared footprint. An observation gap
   is a failed acceptance criterion, not a zero-change result.
6. Reboot into the intended Fedora/Debian/Mint installer, complete that clone's
   migration, and test subsequent Linux and Windows boots. Independently inspect
   firmware and BCD again; account for documented one-shot BootNext consumption
   separately from registration writes. Verify Windows data/ESP/recovery identity
   preservation, Windows domain trust where applicable, and required WinRE boot
   behavior. Hash equality alone is not boot or recovery validation.
7. On separate checkpoint-derived failure clones, retain evidence of interrupted
   execution and partial writes before reverting the lab checkpoint. A powered-off
   VMware disk/NVRAM checkpoint restore can validate lab recoverability; it is
   **not application rollback**. Product journaled restoration, storage rollback
   and payload/file rollback are not implemented by this milestone and must not
   be reported as passed. Test product recovery separately when that workflow
   exists; retain the unsupported status in the meantime.
8. Keep a per-clone bundle: source revision plus dirty-diff identity, build/test
   results, immutable plan/effect map, raw and canonical captures, artifact and
   hashes, independent readback/comparison, native-call log, before/after diffs,
   reboot/installer logs, domain/Windows/Linux checks, interruption evidence and
   checkpoint restore outcome. Record failures and missing evidence explicitly.

### Issue #241: hybrid-path and existing-entry follow-up (2026-09-27)

The proposed combination of preselected BCD GUID creation, typed BCD setters and
explicit firmware writes is already substantially the candidate program. Removing
fwbootmgr bootsequence and deletion does **not** make its remaining live-store
CreateObject/SetElement operations firmware-independent. The contracts reviewed
above do not provide a suppress-firmware-synchronization flag, chosen EFI index,
or complete effects list for those calls. The reader model has BCD GUIDs, typed
elements and qualified storage identities, but no authoritative BCD GUID -> EFI
index association. Matching path/description/optional-data substrings cannot fill
that gap. Reading before/after an unperformed mutation cannot establish its effects.

The audit also covers fwbootmgr and references outside the direct BCD target:

| Operation | Direct BCD/reference effect | Remaining provider effect proof |
| --- | --- | --- |
| CreateObject | Exact new GUID/type. | Other BCD objects, fwbootmgr projection and firmware effects unbounded. |
| All seven typed setters | Exact GUID/element/value. Object/list/device values preserve explicit references. | No complete guarantee that the system provider changes only that object; fwbootmgr and other object/reference effects are unbounded. |
| fwbootmgr bootsequence setter | Exact `24000002` list on fwbootmgr. | Replaces that reference slot; cannot bind resulting BootNext/Boot#### or certify absence of additional effects. |
| DeleteObject | Exact candidate GUID, with a required complete empty incoming-reference scan. | Reference scan is not a native side-effect manifest or ownership receipt; firmware and other provider effects remain unbounded. |
| Explicit firmware writer | No direct BCD setter. | Windows' reflected firmware-object graph may change; the reader cannot predict that reflection from a new slot before creation. |
| RTC writer | No direct BCD/reference or firmware call. | Fixed existing registry slot only; no new unresolved BCD operation introduced. |

No second effect/snapshot schema was added merely to encode unknown behavior.
The existing typed Unsupported blocker remains the production decision. No Exact
assessment, production recovery declaration or successful full boundary fixture
can be obtained by removing only one of the unsupported calls.

**Existing EFI target entries now reject candidate planning.** Previously the
planner could choose a partition-matching entry for BootNext and simultaneously
create another entry. Partition identity alone does not establish equivalent
executable/optional data or ownership. `BootPlanExistingTargetEntryRequiresOwnership`
now rejects that case before capture, including a matching observed entry above
00ff. It does not silently reuse, delete, prioritize or duplicate the existing
entry. A geometry mismatch remains Ambiguous. Unknown path shapes retain their
existing blocking observation; no description becomes an ownership marker.

For the remaining candidates, BootNext is exactly the preselected absent new index;
BootOrder is that index followed by the original ordered sequence, unchanged.
The entry was already proven absent from BootOrder/BootNext before selection.
The obsolete sibling-selection property was removed. This is a conservative
rejection of previously uncertified cases, not a replacement registration path.
It proves neither global deduplication of unobserved EFI variables nor absence of
provider-created duplicates. Those remain part of the firmware-effect blocker.

**Stale decision:** no new leave-stale execution path was adopted. Avoiding
DeleteObject would remove that direct delete and its ownership obligation, but
would not bound CreateObject, setters or firmware re-synchronization, or establish
the no-duplicate requirement. Nor does an empty BCD incoming-reference set prove
an old object's absent EFI representation. A future explicitly bounded additive
design can choose no stale deletion; this continuation does not authorize the
current hybrid on that assumption. Current incomplete/nonempty scans reject
deletion, and description-only candidates remain blocked even with empty scans.

No final scope exists, so #242 dependencies were not newly excluded: configured
WinRE state and active RAM-disk qualification remain Required under the current
closure; EFI optional-data dependencies remain Still unbounded. The full synthetic
production-plan durable success test remains an unmet criterion. The preceding
VMware checklist still applies only after these code/contract blockers close.

A fresh elevated PowerShell process independently verified Administrator/High
integrity and reused the built canonical firmware and BCD readers. No mutation
methods were invoked. Observations: BootOrder `[0000,0002]`/attributes 7, BootNext
Absent/203, Boot0080 Absent/203; 21 BCD objects and the same current loader. Stale
incoming-reference analysis remained Unsupported/BcdReferenceSemanticsUnsupported.
Provider metadata again exposed no EFI-index argument on CreateObject, DeleteObject
or the seven setters. Raw firmware evidence (SHA-256 is content identity only):

| Variable | Bytes / attributes | Optional bytes / path state | SHA-256 |
| --- | --- | --- | --- |
| Boot0000 | 300 / 7 | 136 / Available | `2ADA70C4C3F9F0EDC09994A516DE7574CFC01DBF7DD3C2147DD2D61CDBE8BC7C` |
| Boot0002 | 268 / 7 | 4 / Unsupported | `BC584438352FBFA3D4269827C8A78FC2D2AF8D1450DD6F61CA56DB3295FB2C24` |

These are component observations, not independent Exact snapshot comparison or
bootability evidence. #241 remains open and not code-complete. No destructive VM
validation, machine configuration change, reboot, commit or push occurred.

### Issue #241: direct UEFI pivot and dedicated staging ESP prerequisite (2026-09-27)

**Decision: stop implementation at preparation; registration remains blocked.**
The product direction supersedes the preceding hybrid BCD registration proposal
and its VM mutation expectations. Do not continue trying to establish undocumented
BCD-to-NVRAM equivalence. Do not bind a production one-shot entry to the existing
basic-data OEMDRV partition, and do not stage iGloo files into the Windows ESP.
This checkpoint changes documentation only: the existing blocked planner/executor
has not been converted into the proposed direct writer, and its BCD mutation code
has not been removed or enabled.

#### Actual handoff and reboot analysis

`DirectInstallService.Prepare` currently creates/reuses OEMDRV and puts both the
EFI loaders and installer payload there. `CreateOemDrvPartition` uses `create
partition primary` followed by FAT32 formatting, not creation of a GPT EFI System
Partition. `ConfigureBootFiles` copies shim/GRUB into `igloo-boot` and `EFI/BOOT`,
with shim named `BOOTX64.EFI` in the latter. The current candidate planner targets
`\EFI\BOOT\BOOTX64.EFI` on that OEMDRV volume. This is not the requested dedicated
iGloo ESP layout.

The current GRUB configuration supplies kernel arguments and initrd paths;
`BuildGrubConfig` finds its root by the payload label. It does not obtain them from
a BCD object or EFI OptionalData. The intended new entry has empty OptionalData;
it must not copy Windows Boot Manager's opaque optional bytes. Upstream
[shim's build contract](https://github.com/rhboot/shim/blob/main/BUILDING) provides
a default next loader, but the actual signed distro shim/GRUB pair, compiled GRUB
prefix and Secure Boot behavior must be validated at the new location. Moving
the two binaries alone is not a complete staging implementation.

`DirectInstallViewModel.RebootToInstallAsync` calls registration and only on success
runs a normal `shutdown.exe /r`. No firmware-menu interaction is part of the desired
flow. Under [UEFI load-option processing](https://uefi.org/specs/UEFI/2.10/03_Boot_Manager.html#load-option-processing),
BootNext selects the next boot once, before normal BootOrder processing, and is
removed before control transfers to the selected image. The Windows handoff does
not need to prepend the new entry to BootOrder.

The checked-in distro flows intend one Windows reboot into an installer, which
installs the permanent bootloader before its completion reboot:

| Distro | Current code responsible for final boot setup | Reboot boundary and outstanding evidence |
| --- | --- | --- |
| Fedora KDE | `distros/fedora-kde/kickstart/ks.cfg.template` emits `bootloader --boot-drive=${TARGET_DISK}`; Anaconda owns bootloader installation. The post script runs `grub2-mkconfig`. | Kickstart's `reboot` is the completion action. No intervening reboot is requested before installation. Selection of the permanent ESP with an additional temporary ESP is not bound by this template. |
| Debian | `distros/debian/preseed/preseed.cfg.template` enables `grub-installer` with `bootdev default`; the late command runs `update-grub`. | `finish-install/reboot_in_progress` belongs to installer completion. Default ESP selection is not an exact permanent-volume binding. |
| Linux Mint Cinnamon | `distros/linuxmint-cinnamon/preseed/preseed.cfg.template` enables GRUB installation through Ubiquity. | `ubiquity/reboot=true` requests the completion reboot. The template does not explicitly bind the final EFI destination in the proposed multiple-ESP layout. |

This is source-level control-flow evidence, not proof that any of the three new
layouts boots successfully. First-installed-boot agent/driver work can request a
later reboot, after the permanent bootloader should already exist. There is no
implemented automatic continuation that renews BootNext if an installer fails
before that point. On a later boot the unchanged normal order is available, but
Windows fallback is conditional on its boot/storage state remaining intact;
neither BootNext nor this recovery boundary provides storage rollback. No manual
UEFI selection is an acceptable substitute for the missing automatic transition.

#### Required preparation change before direct registration

The approved target layout is:

```text
Windows ESP (existing, preserved; no iGloo staging)
iGloo temporary ESP (separate GPT EFI System Partition, FAT32)
  \EFI\iGloo\shimx64.efi
  \EFI\iGloo\grubx64.efi
  minimal boot-critical configuration required by the supported loader chain
OEMDRV / migration staging
  kernel, initrd, installer ISO/stage2, migration configuration and payload
```

Preparation must establish the following before a resolved registration plan can
be produced:

1. Plan a separate owned ESP allocation alongside OEMDRV and any ISO/root
   allocations, including checked size/alignment/free-space accounting. The
   current shrink calculation only includes Linux, OEMDRV and optional ISO space.
   Do not repurpose, format or write the Windows ESP. Select the target by strong
   canonical disk identity, not a disk number or a label. Partition creation is a
   separate preparation/storage contract, not recovery supplied by this boot
   snapshot.
2. Establish and persist ownership of the newly created ESP and staging volume:
   GPT disk GUID, distinct partition GUIDs, geometry, canonical volume identities,
   ESP type `c12a7328-f81f-11d2-ba4b-00a0c93ec93b`, FAT32 and verified contents.
   Reopen and independently verify these facts. Current label-based OEMDRV reuse
   and deletion do not constitute ownership proof for a new ESP. Interrupted
   preparation, retries and leftover cleanup must never select another ESP by
   type, label or ordinal. Do not turn the existing OEMDRV partition into an ESP.
3. Replace the single `_preparedBootTarget`/OEMDRV association with an immutable
   prepared-layout binding that distinguishes Windows ESP, iGloo ESP and payload
   partition. Registration must consume the verified iGloo ESP and staged shim
   identity/hash, with the rest of the required loader/configuration chain bound
   as prerequisites. A manually supplied ESP flag or a successful FAT32 format is
   not sufficient.
4. Make the signed shim/GRUB chain find its configuration from the new path and
   locate OEMDRV by a supported stable filesystem/partition identity, checking
   uniqueness and exact ownership. Today GRUB uses `search --label`, and config is
   copied to several guessed distro prefixes on OEMDRV. A bootstrap/configuration
   contract must replace that guesswork without embedding opaque OptionalData or
   losing Secure Boot compatibility. Audit downstream kernel arguments,
   iso-scan/stage2 and agent seed lookup too; changing only GRUB's initial search
   does not make later label-based payload selection exact.
5. Bind each installer's permanent bootloader destination and lifecycle with both
   ESPs present. Decide explicitly whether Linux retains/reuses the iGloo ESP or
   obtains a separate permanent Linux ESP. Never delete an ESP that the installed
   system now depends on. The Windows ESP remains excluded from iGloo staging;
   do not silently use it to solve installer destination ambiguity. Audit Fedora,
   Debian and Mint separately, including dual-boot/replace behavior. Fedora's
   current post-install cleanup removes `OEMDRV/EFI`; it does not cover the new
   ESP and must not be repointed by label or filesystem type.

The canonical plan must bind both disk GUID and partition GUID. The standard
[GPT Hard Drive device-path node](https://uefi.org/specs/UEFI/2.10/10_Protocols_Device_Path_Protocol.html#hard-drive)
contains the partition signature, partition number and geometry, **not a GPT disk
GUID field**. Do not invent such a field in EFI_LOAD_OPTION. Retain the disk GUID
in the canonical binding and independently verify the partition's ownership and
uniqueness before writing the standard HardDrive/FilePath/End path.

These requirements are not implemented by the existing preparation model.
Accordingly no storage commands, new partition creation, ESP writes, firmware
writes, RTC changes, BCD mutations or reboot were performed for this checkpoint.
No production scope was marked resolved. RecoverySnapshotV1, Exact rules,
RecoveryReadiness and PreCommitGate remain unchanged.

#### Direct registration work queued behind preparation

Once the dedicated prepared ESP is proven, the proposed writer owns only a
preselected absent Boot#### and BootNext, with exact payload/attributes persisted
before mutation. BootOrder is an observed prerequisite, not a write slot. No BCD
object creation, fwbootmgr update, stale cleanup or GUID allocation belongs in
that writer. RTC inclusion still needs an explicit clock-policy decision; it is
not necessary merely to select the EFI image.

Existing BootNext must be captured exactly and its replacement policy decided;
do not assume absence or overwrite a pending recovery/update boot implicitly.
Final revalidation must cover the prepared layout/files, candidate absence,
BootNext/BootOrder prerequisites and any RTC slot actually included. A later
direct-writer implementation must mechanically prove plan/scope correspondence,
persist the plan, pass durable reopen and fresh ExactMatch, and only then enter
the writer. None of those success claims follows from this design checkpoint.

The #242 WinRE state, RAM-disk and Windows EFI OptionalData blockers are candidates
for operation-scoped exclusion after the BCD-free executor and dependency proof
exist. They are **not excluded now**: the retained executable model is still the
blocked hybrid. Generic Windows recovery rules must remain strict. Partial writes
(entry succeeds/BootNext fails), uncertain write results, failed reboot and failed
installation still need journal/recovery handling; durable before-state alone is
not rollback.

#### Revised disposable-VM gate

The earlier hybrid checklist is superseded; do not execute it to validate this
design. First finish preparation, the BCD-free writer, exact scoped capture and
the synthetic durable success/failure tests. Then, separately for Fedora KDE,
Debian and Mint:

1. Clone/checkpoint WIN11-BASE, domain join, record the exact ISO/build and firmware
   settings. Retain baseline canonical disks/partitions/volumes, Windows ESP file
   identities, BCD and all relevant raw firmware variables/attributes.
2. Prepare the new layout. Independently verify the dedicated ESP type/ownership,
   FAT32 and signed loader/configuration hashes; verify Windows ESP preservation,
   payload identity and the exact permanent Linux bootloader destination policy.
3. Review the planned absent Boot####, standard parsed path to
   `\EFI\iGloo\shimx64.efi`, empty OptionalData, BootNext before/after bytes and
   attributes. Verify no BootOrder/BCD/fwbootmgr instruction, capture and durable
   artifact readback, fresh ExactMatch and final prerequisite revalidation.
4. Execute only on the disposable clone. Independently read Boot####/BootNext,
   compare BootOrder and BCD against baseline, retain artifact and execution
   evidence, then reboot normally. Require automatic installer entry without
   firmware-menu interaction.
5. Complete installation. Record which partition receives the permanent distro
   loader, its final EFI entry/order and installer logs before/after its completion
   reboot. Require installed Linux to boot on that reboot and another normal reboot
   without BootNext or a manual firmware selection. Verify the Windows ESP remains
   preserved according to the approved install mode/policy.
6. Exercise duplicate identities, changed staging contents, occupied entry and
   changed BootNext before mutation. Exercise partial-write/installer failures only
   with a separately approved VM failure-injection procedure; record applied/unknown
   state honestly. Product restore/rollback is not implemented by this milestone.
   Retain snapshots, plans, hashes, logs and independent captures. Restore the
   VMware checkpoint separately; it is not proof of product rollback.

**#241 remains open and is not code-complete.** Dedicated ESP preparation and
cross-distro boot ownership are prerequisites, not merely outstanding VM tests.

### Issue #241: permanent Linux ESP preparation contract (2026-09-27)

The product decision now fixes the lifecycle: the new iGloo ESP becomes the
installed Linux system's permanent ESP. It is not disposable after installation.
No third ESP is planned. Windows ESP preservation remains mandatory. This
supersedes the earlier open choice between retaining and replacing the iGloo ESP.

**Implementation is partial; native preparation is blocked before its first side
effect.** The stop condition was reached at supported per-distro installer binding.
No new partition writer, staging fallback or installer-default exception was added.
The existing `_preparedBootTarget` registration plumbing is not replaced or enabled
in this preparation-only checkpoint. Registration and recovery Exact rules are
unchanged.

#### Implemented shared contracts and defensive preparation boundary

`Igloo.Core/Preparation/PreparedLayoutV1.cs` adds immutable, independently versioned
preparation contracts, using the existing canonical disk/volume/file identities:

- `PreparationPlanV1`: generation GUID, strong target disk, preserved Windows ESP,
  complete before-partition GUID set and explicit space allocations. It contains
  no persisted disk number, partition number, drive letter or label authority.
- `PreparationSpacePlanV1`: a **1 GiB** FAT32 GPT Linux ESP, payload allocation,
  optional NTFS ISO allocation and Linux space reservation. All offsets/sizes use
  checked **1 MiB** alignment compatible with physical sectors. Padding counts
  toward capacity, overflow and insufficient space fail closed. The input is one
  verified contiguous post-shrink extent, not a sum of arbitrary free regions.
  Existing allocations receive no credit without separately proven ownership.
  This is a space plan, not a shrink authorization. The old heuristic shrink
  selection is not connected to it.
- `PreparedPartitionV1` records the intended role and canonical provider-created
  identity. Pure verification requires the provider result to equal independent
  readback, match the exact allocation/disk/type/filesystem/geometry and be absent
  from the before-partition set. Windows ESP aliases are rejected. A format result
  or a newly found partition cannot replace the provider creation result.
- Ownership assessment distinguishes Planned, CreatedAndVerified,
  AmbiguousLeftover and OwnershipUnavailable. A lost creation receipt with a
  partition now occupying the allocation is ambiguous; a different generation,
  duplicate GUID, substituted payload or changed Windows ESP cannot be reused.
  ContentStagedAndVerified is a checkpoint state with additional file evidence,
  not an installer-support or bootability assertion.
- `PreparedLayoutV1` retains owned partitions, a distinct FAT filesystem UUID
  field for payload lookup and boot-critical canonical paths/lengths/SHA-256.
  The expected entry point is `\EFI\iGloo\shimx64.efi`, with
  `\EFI\iGloo\grubx64.efi` and configuration evidence. File comparisons reject
  missing configuration, changed shim/GRUB/configuration, and files on the Windows
  ESP. File hashes certify content only; they do not prove GRUB will discover that
  configuration or that a filesystem UUID is unique on attached media.

`Igloo.Preflight/CommunityPreparation/PreparedLayoutStore.cs` reuses the existing
Community CreateNew/write-through/flush/reparse-checked artifact store. Immutable
checkpoints reopen through fresh handles, verify a pinned SHA-256 and generation,
and reject incomplete state/receipt structures. No directory scan discovers
ownership. A future orchestrator must durably retain checkpoint references and
acquire creation/readback evidence; this store alone is not a crash-recovery
journal, tamper-proof authority or permission to resume mutation.

`DirectInstallService.Prepare` now checks
`DedicatedEspPreparationSupport` before shell suppression, ISO mounting, legacy
leftover deletion, shrink, partition creation, format or copy. Production support
is `Unsupported / DedicatedEspInstallerBindingNotImplemented`. It deliberately
blocks the old OEMDRV-as-boot-partition pipeline for every recipe. No boolean
override enables a partially implemented preparation path. Cancelled calls still
report cancellation first. This is an intentional user-visible preparation block,
not just a later registration failure after storage has already changed.

#### Supported contracts and the exact remaining installer blockers

| Component | Evidence | Required next implementation; not claimed complete |
| --- | --- | --- |
| Windows partition creation | Microsoft's [MSFT_Disk.CreatePartition](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/createpartition-msft-disk) accepts explicit Size/Offset/Alignment/GptType and returns CreatedPartition. It does not accept a caller-selected partition GUID; that GUID must be bound from the result and independently re-read. An access-path error can occur after creation. | Extend shared preparation around the canonical reader and returned object; correlate the complete before/after inventory, independently verify formatting and persist the creation receipt. Never interpret an exception/nonzero code as proof no partition exists. Native create/format/reopen acquisition is not implemented here. |
| Fedora KDE | The current template emits `autopart --type=plain --nohome`. The supported [Kickstart contract](https://pykickstart.readthedocs.io/en/latest/kickstart-docs.html#part-or-partition) offers an existing partition with `--onpart`/`--noformat`, but explicit `part` directives conflict with `autopart`. | Replace the whole-disk/largest-disk heuristic and automatic recipe with a complete explicitly bound storage recipe, including the existing Linux ESP at `/boot/efi`. Do not merely append an ESP directive. Replace-mode `clearpart --all --initlabel` would erase both ESPs and is incompatible with this preservation contract. |
| Debian | Current `biggest_free`/`atomic` plus `grub-installer/bootdev default` contains no exact ESP binding. [Debian's preseeding contract](https://d-i.debian.org/doc/installation-guide/en.amd64/apbs04.html#preseed-partman) describes recipe-based partitioning, not proof that this default selects a nominated ESP among several. | Establish a supported recipe/hook for the actual trixie installer components that preserves Windows and binds only the nominated ESP. Safely translate pinned disk/partition/filesystem identities to the current installer device and reject duplicates. No tested no-format binding exists in this tree. |
| Linux Mint Cinnamon | Upstream [partman-efi initialization](https://raw.githubusercontent.com/linuxmint/ubiquity/master/d-i/source/partman-efi/init.d/efi) marks eligible ESPs with method `efi`. Its [fstab generation](https://raw.githubusercontent.com/linuxmint/ubiquity/master/d-i/source/partman-efi/fstab.d/efi) selects the first qualifying ESP, optionally on an auto-partition disk. Disk selection does not disambiguate two ESPs on the same disk. The current iGloo template supplies no stronger binding. | This is the immediate multiple-ESP stop condition. Prove an installer-supported exact selection mechanism in the actual shipped Mint/Ubiquity version before enabling preparation. Do not change Windows ESP flags, reorder partitions, hide it, or patch private partman state merely to influence default selection. No third ESP or Windows-ESP fallback is introduced. |

These are limitations of the **current integration**, not a claim that Linux can
never reuse an existing ESP. Upstream source was read on this date; it is not proof
of the exact binaries in a user's ISO. The Mint
[format step](https://raw.githubusercontent.com/linuxmint/ubiquity/master/d-i/source/partman-efi/commit.d/format_efi)
skips an existing detected filesystem, but this does not solve destination
selection or prove every install stage leaves Windows untouched. The desired
policy is to reuse the prepared FAT32 filesystem without formatting. Formatting
it after handoff is not asserted safe: loader/configuration access and failure
recovery still need distro-specific validation. Permanent loader installation,
NVRAM updates and subsequent reboot behavior have not been validated on the new
layout for any of the three distros.

#### Loader and payload acquisition still required

`FindEfiFiles` currently chooses the first existing shim and GRUB candidates
independently. It does not establish a signed pair/profile, compiled prefix or
required auxiliary binaries from a pinned ISO. The current scatter of configuration
files across guessed prefixes on OEMDRV cannot certify the new
`\EFI\iGloo` chain. The default upstream next-loader filename is useful design
evidence, not a per-distro Secure Boot/configuration certification. No actual ISO
was mounted or modified during this checkpoint.

[GRUB's documented search](https://www.gnu.org/software/grub/manual/grub/grub.pdf)
supports filesystem UUID lookup and selects the first match. Do not invent generic
`search --part-uuid` support for every shipped signed GRUB. The model distinguishes
a FAT filesystem UUID from a Windows volume GUID, but native filesystem UUID
acquisition, uniqueness checking, generated boot configuration and downstream
installer binding remain unimplemented. Required downstream work includes Fedora
stage2/kickstart label arguments, Debian hd-media iso-scan, Mint casper ISO lookup,
and the agents' broad seed scans. No current label search is represented as exact.

Future registration may consume a fully verified generation plus Linux ESP
identity, exact shim identity and canonical GPT path inputs only after these
acquisition/installer contracts are implemented. It must never consume a merely
well-formed checkpoint as a boot-ready layout. RecoveryScopeV1 remains unresolved.
The Windows ESP was not mounted, staged into or formatted; no storage/firmware/
BCD/RTC mutation or reboot occurred. **#241 remains open; same-ESP permanent Linux
installation is not yet proven for all three distros.** The preceding VM checklist
must use the new ESP permanently, never delete it as temporary cleanup, and retain
the Windows ESP unchanged throughout both preparation and installation.

### Issue #241 shipped-installer ESP audit continuation (2026-09-27)

The [actual-media binding audit](installer-esp-binding.md) and
[extracted-source hash manifest](installer-esp-evidence.json) now supersede the
earlier upstream-only findings for the inspected versions: Fedora 44-1.7
(Anaconda 44.30 / pykickstart 3.69 / Blivet 3.13.2), Debian 13.7.0 live with
trixie hd-media build 20250803+deb13u7 (partman-efi 110), and Mint 22.3
(Ubiquity 24.04.3+mint19 / casper 1.498). Fedora/Mint full-image SHA-256 matched
the downloaded checksums; Debian initrd and selected ISO udebs were checked,
not the complete Debian ISO. This is content evidence, not bootability or a
signature-authentication assertion.

Added shared `InstallerEspBindingV1` declaration and pure runtime translation:
prepared generation, canonical disk/partition/volume identities, geometry and
distinct FAT filesystem UUIDs. Missing, duplicate, changed, overlapping or failed
observations cannot produce a device mapping. Linux paths are transient outputs.
No collector, installer authorization or RecoverySnapshot schema change was added.
The Fedora generator emits an exact no-format ESP directive only; it is not
combined with the old automatic partitioning template.

The complete Fedora recipe remains blocked by the lack of a truthful root
partition receipt. `PreCreateRoot` is not sufficient: the existing receipt uses
formatted-volume identity and rejects the unformatted root allocation. Introduce
proper partition-only creation/readback evidence in the future preparation
contract instead of inventing filesystem/volume identifiers or weakening canonical
recovery identity. Debian and Mint remain Unsupported for exact unattended ESP
selection with two eligible ESPs on the same disk.

The audit also evaluated Debian debootstrap and Mint OS-only plus explicit signed
bootloader installation. Supported individual tools do not complete the required
ownership/failure lifecycle. Mint's package hooks can reintroduce the currently
mounted ESP, and its success-command handler does not propagate nonzero exit as a
completion/reboot failure. These are retained blockers, not bypasses. Labels and
broad scans still occur in downstream stage2/ISO/seed/agent discovery. All native
preparation and registration remain blocked; Exact, RecoveryReadiness and
PreCommitGate semantics are unchanged. No developer-host mutation or VM install
was performed. The phase log records validation totals.

### Issue #241 complete installation ownership continuation (2026-09-27)

The [common ownership and distro lifecycle review](community-installation-ownership.md)
extends preparation evidence, not RecoverySnapshotV1. New partition-only receipts
truthfully represent an owned, unformatted root. They include generation,
canonical disk identity, GPT partition/type GUID and exact geometry; no synthetic
volume identity or filesystem is introduced. Complete installation ownership
requires every planned partition, all preserved Windows identities, matching
formatted receipts and fresh runtime correlation. Partial creation checkpoints
persist known receipts but cannot qualify a completed layout or authorize retry.

The chosen Fedora contract pre-creates both root and Linux ESP. Its full storage
section uses exact PARTUUID root selection, exact UUID ESP selection with
`--noformat`, and no autopart/all-disk clearing. The actual shipped F44 parser
accepted the generated section. This does not enable the retained template's
remaining payload/post-install behavior or claim an end-to-end installation.

A versioned Linux acquisition protocol and read-only collector now feed the same
pure resolver. Two independent inventories must agree; missing/duplicate/changed
identity, unsupported topology or observation failure cannot become permission to
install. Payload content comparison additionally binds generation, partition,
canonical volume/path and hash. Mount execution, native content readback,
physical-ID equivalence and production early-boot integration remain future work.

Debian's debootstrap primitive is a candidate, not a complete offline GNOME
replacement. Actual signed-package scripts need bounded mount, GRUB/NVRAM and MOK
policy effects plus independent failure/readback handling. Mint's current Ubiquity
partitioning and success-hook contracts still cannot enforce the required exact
ownership and completion boundary. No supported replacement engine is asserted.

`DedicatedEspPreparationSupport` remains Unsupported before the first preparation
side effect. There is no resolved direct-registration scope, firmware enablement,
storage rollback or mutation/restore implementation from this continuation.
RecoverySnapshotV1 Exact semantics, RecoveryReadiness.Production
`ObservationUnavailable / NotImplemented`, and PreCommitGate remain unchanged.
**#241 remains open; #243 is not started.** No developer-host mutation or VM
installation was performed.

### Issue #241 target-root deployment and installation receipts (2026-09-27)

The [deployment lifecycle design](target-root-deployment.md) retains the previous
Fedora storage contract and documents all stages of a candidate Debian bootstrap
plus the actual Mint filesystem/package transformation and signed-loader effects.
Neither replacement is accepted as complete. Native preparation and registration
remain blocked.

New shared pure code verifies a root filesystem format receipt against the same
owned partition, correlates core root/ESP/source mounts to fresh runtime device
identities, rejects Windows ESP exposure and mount substitutions, and generates
stable UUID fstab contents. The fresh-root resolver still rejects a pre-existing
filesystem; a separate observed format receipt is mandatory after that transition.
Mount/stat/path acquisition and restricted native execution remain unimplemented.

`InstallationReceiptV1` records deployment evidence, separately from boot recovery
before-state. It retains source/distro/strategy, root/ESP ownership, stage outcomes,
file identities and a raw permanent boot-entry/BootOrder witness. It reuses the
canonical EFI parser. Independent readback and complete stage evidence can yield
`EvidenceComplete`; that is expressly not bootability, a supported distro profile,
recovery readiness or reboot authorization. Other firmware side effects are not
proved absent by one matching witness. Partial or uncertain evidence never becomes
complete by durable reopen. Community storage reuses flushed create-new artifacts;
the installer-runtime durable writer remains required.

No RecoverySnapshotV1 schema, Exact rule, RecoveryReadiness or PreCommitGate behavior
was changed. No storage, firmware, BCD, RTC or machine configuration mutation was
performed. #241 remains open, #243 remains out of scope, and no commit or push was
made. Validation is recorded in the phase log.
