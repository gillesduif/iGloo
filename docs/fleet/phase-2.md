# Fleet Phase 2: recoverable execution and read-only target identity

Status: **Phase 2A fake recovery, Phase 2B1 read-only identity, Phase 2B2 local authority/gating, and Phase 2B3.1 shared snapshot contracts/read-only capture composition are implemented. Real execution remains disabled; production recovery readiness remains unavailable.**

## Issue #242 — production capture blockers, 2026-09-26

**Partial implementation; issue #242 is not closed.** The shared snapshot schema,
scope and Exact rules remain intact. Core/Preflight own the changes; Fleet.Web,
PreCommitGate and production RecoveryReadiness were not changed by this task.
Existing Community/GUI/package work in the working tree was preserved.

The canonical firmware getter now uses GetFirmwareEnvironmentVariableExW and
retains native variable attributes with raw bytes. Failed calls never expose the
attributes out parameter as valid evidence. Native failure distinctions and legacy
providers' Unsupported attributes are preserved. This closes the missing native
attribute acquisition implementation, subject to elevated runtime validation.

WindowsWinReReader reads bounded, DTD-disabled ReAgent.xml version 2.0 as typed,
locale-independent source evidence, including raw bytes, loader GUID, location
disk GUID/offset and raw InstallState. A unique canonical storage correlation is
required before reading Winre.wim through its canonical volume GUID/path. XML
absence never becomes disabled WinRE; access/ambiguity/unsupported failures remain
typed. **InstallState's enabled-state semantics are not sufficiently established,
so Enabled remains Unsupported/WinReInstallStateSemanticsUnproven.** This improves
acquisition but does not close the configured-state blocker or extend the snapshot
schema with a guessed restoration contract. A WIM hash establishes content identity
only, not bootability.

The existing BCD reader now has fixture-testable typed nested-device projection.
Fully qualified GPT parents retain their identifiers and AdditionalOptions;
provider class/device-kind mismatches, native parents and opaque parents cannot
become canonical GPT identity. A failed required GetElementWithFlags result is
still a failure. The active WinRE RAM-disk provider failure seen in Phase 2B3
remains unresolved; deterministic qualified-parent fixtures do not certify it.

Empty EFI optional data has supported dependency semantics. Nonempty optional
data remains losslessly retained and Unsupported, even when it contains a familiar
BCDOBJECT substring. No undocumented Windows-specific blob interpretation was
introduced. Missing optional evidence is Ambiguous; malformed EFI load-option
framing remains rejected. Recapture now preserves missing, denied, unsupported
and ambiguous required evidence instead of flattening all failures to Unavailable.

After deterministic tests passed, the actual tool-process identity checks returned
Administrator **False**, user `IGLOO-LAB\testuser`, medium integrity
`S-1-16-8192`, and Administrators `S-1-5-32-544` **deny-only**. Commands were the
WindowsPrincipal Administrator check, `whoami`, and `whoami /groups`. Therefore no
elevated/live recovery capture was attempted. Combined current-loader/Windows/
WinRE/EFI identity and independent real-host correlation are **not proven**.
No boot, BCD, firmware, registry, storage or filesystem configuration mutation was
performed. Runtime VM validation required by CONTRIBUTING remains outstanding.

Added **38 deterministic cases** in RecoveryAcquisitionTests: native attributes
and failures, locale-independent WinRE evidence and correlation failures, nested
qualified/opaque BCD parents, optional-data support limits, and independent exact,
changed, missing and unavailable recaptures. Fixtures do not read live Windows
state. Validation:

- `dotnet test .\tests\Igloo.Preflight.Tests\Igloo.Preflight.Tests.csproj --no-restore -m:1 -warnaserror`: **220 passed**.
- `dotnet test .\tests\Igloo.Core.Tests\Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror`: **134 passed**.
- `dotnet build -warnaserror`: **0 warnings, 0 errors**, restore up to date.
- `dotnet test .\Igloo.sln --no-build --no-restore -m:1`: **622 passed, 0 failed, 0 skipped**; no OOM/fallback.
- `dotnet list .\Igloo.sln package --vulnerable --include-transitive`: **no known vulnerable packages** from the configured NuGet source.
- `git diff --check`: passed (only Git's existing LF/CRLF conversion notices).

Remaining requirements for Exact: supported WinRE enabled/configured semantics,
qualified active RAM-disk dependency closure, supported required nonempty EFI
optional-data semantics, and elevated independent combined capture validation.
Community's dynamic mutation footprint remains separate issue #241. Production
RecoveryReadiness remains **ObservationUnavailable / NotImplemented**; issue #243
is not enabled. No restore, storage rollback, destructive adapter, commit or push.
See the [shared architecture](../architecture/recovery-snapshot-v1.md#current-capture-support-and-blockers)
for acquisition sources and the unchanged shared integration boundary.

### Elevated read-only continuation, 2026-09-26

This continuation changed documentation only. It loaded the freshly tested Core/
Preflight assemblies in disposable .NET 10 PowerShell tool processes and called
the existing canonical readers. No implementation was repeated, no snapshot or
gate semantics changed, and unrelated working-tree changes were preserved.

The sandbox process was `IGLOO-LAB\codexsandboxoffline`, Administrator=False,
medium integrity. The approved host tool process was independently verified as
`IGLOO-LAB\testuser`, **Administrator=True**, high integrity
`S-1-16-12288`, with Administrators `S-1-5-32-544` **enabled**, not deny-only.
The checks were WindowsPrincipal.IsInRole(Administrator), `whoami`, `whoami /groups`
and, for the first host probe, `whoami /priv`.

Read-only calls were WindowsFirmwareSnapshotCapture.Capture / WindowsFirmwareReader,
WindowsWinReReader.ReadConfiguration / Capture, WindowsStorageReader.ReadIdentitySnapshot,
WindowsBcdReader.ReadRecoveryGraph, WindowsRecoveryPathReader.ReadWindowsVolume /
ReadFileIdentity, CanonicalRecoveryIdentity.Bind, WindowsRecoverySnapshotCapture.Capture,
and the shared hash/assessment/comparison methods. No bcdedit, reagentc, firmware
setter, execution adapter, mount assignment or configuration write ran.

**Firmware attributes and privilege prerequisite.** In a high-integrity process
with SeSystemEnvironmentPrivilege disabled, BootOrder and BootNext returned native
**1314 / AccessDenied**, including their attribute observations. The probe then
used the existing FirmwareNative.EnablePrivilege helper through reflection with
a NullLogger, solely to enable that privilege in each disposable probe process.
No firmware setter was invoked. With that process prerequisite satisfied:

- BootOrder: Available, ordered `[0000, 0002]`, native variable attributes **7**.
- BootNext: **Absent**, native error **203**; its attributes remain Absent.
- Boot0000: Available, attributes **7**, 300 raw bytes; structural GPT HardDrive /
  FilePath / End parse points to `\EFI\Microsoft\Boot\bootmgfw.efi` on partition
  `a5cb2c6a-7662-487d-affd-1431285ae164`, start LBA 1116160, length 202752 sectors.
  Raw SHA-256: `2ADA70C4C3F9F0EDC09994A516DE7574CFC01DBF7DD3C2147DD2D61CDBE8BC7C`.
- Boot0000 retains **136 optional-data bytes**. The visible WINDOWS/BCDOBJECT data
  does not establish complete dependency semantics: `OpaqueOptionalData` remains
  **Unsupported**. No GUID substring was treated as structural proof.
- Boot0002: Available, attributes **7**, 268 raw bytes, unsupported device-path
  shape and four optional bytes `0000424F`. It is the observed Kingston USB entry,
  outside this observation scope's required entry set; its mere presence does not
  create a required dependency. BootOrder still retains its position.

Both fresh privileged processes reproduced these values and raw hashes. A later
fresh process without privilege adjustment again returned **1314**, confirming
that elevation alone still does not satisfy the production reader's prerequisite.
Native attribute acquisition is now runtime-validated on this host; establishing
the privilege at a future read-only orchestration boundary remains necessary.

**WinRE and active BCD evidence.** ReAgent.xml was Available, 1109 bytes, SHA-256
`5E586528E31BF274ACFC09EA431375DB005C22EE2139BFEDA08C546D56B06001`.
The typed source retained InstallState **1**, recovery loader
`4f9e620f-6f1a-11f0-b519-f7b20eb4bccb`, disk GUID
`c7c285c8-be60-40f9-b55f-487890bdf072`, offset **999129350144**, and directory
`\Recovery\WindowsRE`. Enabled remains
**Unsupported / WinReInstallStateSemanticsUnproven**; no enabled-state meaning was
inferred from that integer.

The BCD provider enumerated **21 objects**. Current loader
`4f9e620d-6f1a-11f0-b519-f7b20eb4bccb` has a recoverysequence pointing to that same
WinRE loader. Current loader device/osdevice and boot-manager device expose
qualified GPT identities. Active WinRE device `0x11000001` and osdevice
`0x21000001` retain RAM-disk kind 4, `\Recovery\WindowsRE\Winre.wim`, native parent
`\Device\HarddiskVolume4`, and AdditionalOptions
`4f9e6210-6f1a-11f0-b519-f7b20eb4bccb`. Both required qualified reads remain
**Unavailable / BcdQualifiedDeviceUnavailable**. This canonical observation does
not retain a native error number, so this run makes no new native-error claim.
The options object's SDI device (`0x31000003`) independently qualifies to recovery
partition `20a415c1-dcca-4e16-8eca-8fc165c602c2`; this does not replace the failed
required RAM-disk qualification. Historical opaque objects remain retained without
expanding the active scope solely because they exist.

**Additional production correlation blocker.** WindowsWinReReader returns
**Unavailable / WinReVolumeOwnerUnavailable** for both recovery volume and image.
Three other inventory volumes expose `PartitionGuid=Unavailable/VolumeOwnerNotProven`:
`f866f3be-0000-0000-0000-100000000000`,
`f866f3be-0000-0000-0000-f01f37000000`, and
`f866f3be-0000-0000-0000-00076b000000`. The reader's all-volume ownership guard
rejects that inventory before selecting the configured recovery volume. The guard
was not bypassed or weakened in any production snapshot.

A separate diagnostic joined the configured disk GUID/offset uniquely in each
fresh canonical inventory, then used CanonicalRecoveryIdentity.Bind and the existing
ReadFileIdentity getter. Two fresh processes independently reopened volume
`20a415c1-dcca-4e16-8eca-8fc165c602c2`, `\Recovery\WindowsRE\Winre.wim`:
**811706096 bytes**, SHA-256
`5E4BF8A33825E57F710CECF14C3CBF6E2D62BAB7AA48F7E00D94CF8083FE2D32`.
This proves the configured content is readable through a canonical handle path;
it neither proves bootability nor repairs production correlation/dependency closure.
Future correlation changes must prove which unknown inventory rows are irrelevant,
rather than silently dropping them.

**Combined host correlation.** Boot0000 was selected only after its structurally
parsed partition/path agreed with the qualified BCD Windows Boot Manager. The
observation-only WindowsBootConfiguration scope included RTC, no mutation slots,
and the observed Windows volume as its explicit target; it was not a Community
operation declaration or a Fleet target approval. Strong disk identity was
`eui.00000000000000006479A7A8F000009E` (format 8), GPT disk
`c7c285c8-be60-40f9-b55f-487890bdf072`, logical/physical sectors **512/4096**.
The ESP GUID above correlated to offset **571473920**, length **103809024**, FAT32;
Windows partition/volume GUID was `fb8bbd3d-f4b8-48da-ad88-13a1b49e5cb4`.
The boot-manager executable was Available, **3087200 bytes**, SHA-256
`25D8869E3C79083AE62C698A9DB9030748E3267BD53593357447F3F72761D1F2`.
RTC key was present and RealTimeIsUniversal was **Absent**. No RTC write occurred.

Each of two fresh privileged processes called the production capture twice; each
call itself performs two independent observations. All four returned canonical
hash `F6A8B10D7AE9809F3FE6EDEF527E46CED94A27FA21FB3A180B288E3B524C5E99`,
with valid hash verification. Every assessment remained **Unsupported**, comparison
**ObservationUnavailable**, and IndependentReadback
**Unavailable / RequiredEvidenceIncompleteDuringRecapture**. Equal hashes of these
incomplete observations do not establish ExactMatch or bootability.

The observed blocking issues were required active WinRE noncanonical/failed device
and osdevice qualification, Boot0000 opaque optional data, unsupported WinRE enabled
state, unavailable WinRE volume/image, and unavailable independent-readback proof.
Thus #242 remains open: supported WinRE state semantics, scoped canonical WinRE
correlation, active RAM-disk qualification, required EFI optional-data semantics,
and a complete Exact independent recapture remain outstanding. Firmware attribute
acquisition itself is now proved under the stated process privilege prerequisite.
RecoveryReadiness remains **ObservationUnavailable / NotImplemented** and
PreCommitGate is unchanged. No source, package, GUI or execution behavior changed
in this continuation; no commit or push was performed.

Continuation validation: the initial sandbox test attempt could not read the
installed Windows SDK directory; rerunning in the approved host process succeeded.
`dotnet test .\tests\Igloo.Preflight.Tests\Igloo.Preflight.Tests.csproj --no-restore -m:1 -warnaserror`:
**220 passed**. The equivalent Core command: **134 passed**. Both had zero failures
or skips. `dotnet build .\Igloo.sln -warnaserror -m:1`: **0 warnings, 0 errors**.
`git diff --check` passed; `git status --short` retained the existing mixed worktree,
with only the two recovery documents edited by this continuation.
No full-suite rerun, new tests, vulnerable-package audit, VM installation or reboot
was performed in this documentation/runtime-only continuation; the earlier 622-test
and package-audit results above are historical, not new runs.

### Six-blocker implementation continuation, 2026-09-26

The preceding runtime-only record is historical. This continuation changed only
shared Core/Preflight acquisition, deterministic recovery tests and these recovery
documents. RecoverySnapshotV1's schema and Exact assessment were not changed.

| #242 acceptance blocker | Current result | Evidence |
| --- | --- | --- |
| 1. Establish firmware-read privilege | **Closed** | The canonical reader enables the existing process-token privilege before every getter and rejects unsuccessful assignment. Two fresh processes transitioned Disabled -> Enabled without probe-side privilege setup; attributes 7 and BootNext Absent/203 were reproduced. |
| 2. Supported locale-independent WinRE enabled state | **Remaining** | XML source is typed and available, but raw InstallState=1 is not a sufficiently established enabled-state API contract. Enabled remains Unsupported/WinReInstallStateSemanticsUnproven. |
| 3. Canonical WinRE volume ownership/correlation | **Closed for the observed gap** | Configured-partition access paths prove the selected canonical volume; other fully identified volume GUIDs are outside that dependency. Production now returns Available recovery volume and WIM content identity in independent captures. Unknown required identity still fails closed. |
| 4. Active WinRE RAM-disk qualification | **Remaining** | Device/osdevice qualified reads still return Unavailable. Native parent, path and AdditionalOptions are preserved; the qualified SDI device is not substituted for the failed RAM-disk observations. |
| 5. Boot0000 optional-data semantics | **Remaining** | The same 136 bytes remain lossless/Unsupported. A deterministic fixture now uses this exact payload and verifies that it cannot become Exact. Recognizable WINDOWS/BCDOBJECT content does not prove complete dependency semantics. |
| 6. Complete independent Exact recapture | **Remaining** | Two fresh processes reproduced four snapshots with valid equal hashes, but assessment is Unsupported, comparison ObservationUnavailable, and IndependentReadback Unavailable due to blockers 2/4/5. |

FirmwareNative.EnableReadPrivilege reuses the existing token APIs with
TOKEN_QUERY/TOKEN_ADJUST_PRIVILEGES and SeSystemEnvironmentPrivilege. Both the
AdjustTokenPrivileges BOOL and native error must indicate success; error 1300
cannot be ignored. A failed call with native error zero becomes error 31, never
success. FirmwareVariableObservation preserves a separate pre-acquisition failure
state so error 203 from a token API cannot masquerade as absent BootNext. The native
getter still exclusively establishes variable absence and retains its raw error
and attributes. Process rights are enabled, never assigned or persisted; no firmware
setter runs. The existing write-oriented helper and destructive adapters were not
changed or invoked by validation.

WinRE correlation still first requires the configured GPT disk GUID and byte offset
to identify one canonical partition. If another volume's owner is unavailable, it
uses that configured partition's authoritative volume-GUID access path, requiring
complete partition paths, a single unique GUID, no duplicate owner, all volume GUIDs
known and unique, and agreement with the selected volume's owner before the existing
canonical binding validation. Missing/denied selected ownership, missing candidate,
malformed paths, duplicate identity, ambiguous access paths and letter-only locators
fail closed. Other rows retain their unknown owners; no source inventory is filtered
or rewritten. This resolves the observed dependency selection gap without asserting
that every unrelated volume's owner is now known.

Research did not establish a supported WinRE enabled-state ABI or a complete
Windows EFI optional-data dependency contract. Microsoft's ReAgent.xml example is
source evidence, not a versioned InstallState semantics specification. The documented
GetElementWithFlags flag qualifies partitions; it does not prove a successful nested
RAM-disk qualification here. No localized command text, private DLL ABI, guessed
binary offsets, BCD recovery-enabled flag, or SDI/native-parent substitution was
introduced to force completion. References and limits are recorded in the
[shared architecture](../architecture/recovery-snapshot-v1.md#current-capture-support-and-blockers).

Before native validation, actual tool elevation was verified again: Administrator
True, `IGLOO-LAB\testuser`, high integrity and Administrators enabled.
Fresh process IDs **28248** and **16188** each began with the firmware privilege
Disabled and acquired it through WindowsFirmwareReader. Both used the same
structurally correlated, observation-only WindowsBootConfiguration scope described
above; neither called EnablePrivilege through reflection or a probe helper.
Each called production Capture twice (each Capture independently reads twice).
All four returned canonical hash
`BEAB4CAA8D7EFEC957BEECA3EDE3D0126983D4182EC1787A3979C61B19724B77`.
The hash changed from the prior run because previously unavailable WinRE volume/image
facts are now captured. Equal hashes still do not prove Exact or bootability.

Production recovery volume is now **Available** with partition/volume GUID
`20a415c1-dcca-4e16-8eca-8fc165c602c2`; image is **Available**, 811706096 bytes,
SHA-256 `5E4BF8A33825E57F710CECF14C3CBF6E2D62BAB7AA48F7E00D94CF8083FE2D32`.
Windows/ESP/BCD identities, BootOrder `[0000, 0002]`, BootNext Absent/203 and the
Boot0000 raw hash remained as observed above. Seven assessment issues remain:
four active WinRE BCD qualification/noncanonical-device issues, unsupported
Boot0000 optional data, unsupported WinRE enabled state, and unavailable readback.

Added **26 deterministic cases** across RecoveryReadPrivilegeTests and
RecoveryAcquisitionTests, including privilege failures/no getter invocation,
per-read ordering, scoped ownership success and malformed/denied/duplicate failures,
the exact observed optional payload, and rejection of a qualified-SDI substitution
for failed active RAM-disk qualification. No test depends on live recovery state.

- `dotnet test .\tests\Igloo.Preflight.Tests\Igloo.Preflight.Tests.csproj --no-restore -m:1 -warnaserror`: **246 passed**.
- `dotnet test .\tests\Igloo.Core.Tests\Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror`: **134 passed**.
- `dotnet build .\Igloo.sln -warnaserror -m:1`: **0 warnings, 0 errors**.
- `dotnet test .\Igloo.sln --no-build --no-restore -m:1`: **648 passed, 0 failed, 0 skipped**; no OOM/fallback.
- `git diff --check`: passed; existing LF/CRLF notices only. `git status --short`
  retains the existing mixed worktree and adds RecoveryReadPrivilegeTests.cs.

No analyzer suppressions, package upgrades, Fleet.Web edits, machine configuration
mutations, restore or storage rollback were added. Runtime VM installation remains
outstanding. Production RecoveryReadiness remains ObservationUnavailable /
NotImplemented and PreCommitGate is unchanged. Issue #242 remains open; #243 has
not started. Unrelated working-tree changes were preserved. No commit or push.

### Four-blocker interpretation review, 2026-09-26

Reviewed the existing diff, recovery architecture, canonical acquisition paths and
deterministic success/failure fixtures before further work. The prior firmware
privilege and observed WinRE volume-correlation fixes remain intact. This pass
changed only RecoveryAcquisitionTests and the two recovery documents; production
schema, readers, assessment, readiness and gate semantics were not changed.

| Acceptance item | Status | Implementation/evidence | Runtime observation | Sufficiency for Exact |
| --- | --- | --- | --- | --- |
| WinRE enabled-state semantics | **Remaining** | Typed ReAgent.xml evidence retained; reviewed Microsoft REAgentC/XML documentation and installed SDK headers/IDL. No authoritative enabled-state enum/ABI was established. Additional uninterpreted integer values remain Unsupported in tests. | InstallState=1; configured loader, canonical volume and image Available; Enabled Unsupported/WinReInstallStateSemanticsUnproven. | A raw integer and a readable WIM do not establish configured enabled/disabled semantics. No text or BCD-flag inference was added. |
| Active WinRE RAM-disk qualification | **Remaining** | Existing provider-backed qualified-parent success and native/opaque-parent failure fixtures retained. Added regression for the observed COM HRESULT. | For both device/osdevice, GetElement and GetElementWithFlags(0) succeed with native parent; GetElementWithFlags(1) throws COMException **0xD000000D**, before projection. | The provider has not supplied qualified GPT identity for the required RAM-disk parent. Flags=0 and the qualified SDI entry cannot substitute for it. |
| EFI optional-data semantics | **Remaining** | UEFI specifies byte framing/pass-through, not the observed Windows payload's dependency interpretation. Strengthened exact-payload serialization/reopen and RelevantOpaque/Unsupported regression. | Boot0000 raw payload Available, 300 bytes; optional data 136 bytes, Unsupported; raw SHA-256 remains 2ADA70C4C3F9F0EDC09994A516DE7574CFC01DBF7DD3C2147DD2D61CDBE8BC7C. | Lossless bytes and a recognizable BCDOBJECT string do not establish complete dependencies. |
| Independent Exact recapture | **Remaining** | Not attempted again because the three interpretation/qualification prerequisites remain unresolved. | Prior production assessment Unsupported and comparison ObservationUnavailable remain the latest combined results; no new Exact result is claimed. | Equal incomplete hashes are insufficient. Current required Unsupported/Unavailable evidence prevents Exact. |

Actual tool elevation was again Administrator=True, high integrity, Administrators
enabled, user `IGLOO-LAB\testuser`. The read-only diagnostic loaded
the existing Core/Preflight assemblies, captured current WinRE evidence and
Boot0000, and called the same root\WMI BcdStore/BcdObject provider directly with
OpenStore/OpenObject and GetElement/GetElementWithFlags. It obtained the active
loader from WindowsWinReReader, not from a guessed GUID. For `0x11000001` and
`0x21000001`, ordinary/Flags=0 results were BcdDeviceFileData kind 4 with path
`\Recovery\WindowsRE\Winre.wim`, AdditionalOptions
`4f9e6210-6f1a-11f0-b519-f7b20eb4bccb`, and BcdDevicePartitionData kind 2 parent
`\Device\HarddiskVolume4`. Flags=1 returned the same COM HRESULT in both cases.
This is more precise native evidence than the earlier generic canonical failure
code; it does not reinterpret that failure as absence or supported qualification.

The reviewed primary sources are linked in the
[interpretation review](../architecture/recovery-snapshot-v1.md#interpretation-review-and-provider-limitation-242).
The local SDK search covered header/IDL declarations for WinReGetConfig,
WinREGetConfig, WINDOWS_OS_OPTIONS and REAGENT_CONFIG, with no matches. Microsoft
Q&A logs and community implementations were not treated as a supported ABI or
state specification. This establishes the limits of this review, not universal
nonexistence of undocumented Windows internals. No private ABI, binary-offset
guess, native-parent inference or localized command-output parser was introduced.

Validation completed with **three additional test cases** (two raw InstallState
values and the actual provider HRESULT), plus strengthened existing optional-data
round-trip assertions. Qualified RAM-disk projection success and failure tests both
ran. The first targeted run hit CA2201 in the new test's direct COMException
construction (tests themselves: 249 passed, zero failed/skipped); this was fixed
using the runtime HRESULT-to-exception conversion, without suppression. Final runs:

- `dotnet test .\tests\Igloo.Preflight.Tests\Igloo.Preflight.Tests.csproj --no-restore -m:1 -warnaserror`: **249 passed, 0 failed, 0 skipped**.
- `dotnet test .\tests\Igloo.Core.Tests\Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror`: **134 passed, 0 failed, 0 skipped**.
- `dotnet build -warnaserror`: **0 warnings, 0 errors**.
- `dotnet test .\Igloo.sln --no-build --no-restore -m:1`: **651 passed, 0 failed, 0 skipped**; no OOM/fallback.
- `git diff --check`: **exit 0**. Git emitted LF/CRLF conversion notices, not
  whitespace-check failures; these are separate from the zero-warning build.

Work stops at these unsupported contracts/provider failures as required. #242
remains open; #243 has not started. RecoveryReadiness.Production stays
ObservationUnavailable / NotImplemented. PreCommitGate, Exact rules and snapshot
schema are unchanged. No machine configuration mutation, Fleet.Web edit, commit
or push occurred; unrelated working-tree changes were preserved.

## Phase 2B3.2 — Community durable boot boundary

Community boot registration now passes through `CommunityRecoveryBoundary`
immediately before the existing privilege/Boot####/BootNext/BootOrder/BCD/RTC
stage. The mutation body is preserved in `RegisterBootConfiguration`; storage
preparation, shrink/create/format and staging behavior are unchanged. The service
is not wrapped wholesale. No boot mutation, restore or real-host capture was run
while implementing this milestone.

For a supported declared scope the boundary requires shared Exact assessment,
persists an immutable artifact, closes its write handle, reopens independently,
verifies full artifact integrity plus manifest/snapshot hash/structure and scope/
target binding, and recaptures current machine state. Only ExactMatch permits the
mutation callback. Cancellation and any failed prerequisite prevent the callback.
Mutation exceptions are not retried or relabelled as observation failures.

Community artifacts use `%LOCALAPPDATA%\iGloo\RecoverySnapshots\<guid>.recovery.json`.
The versioned envelope contains artifact identity, snapshot byte length, SHA-256,
canonical state hash and the unmodified shared snapshot serialization. A full
artifact digest is pinned in memory before persistence. The file store uses
CreateNew, WriteThrough and Flush(true), then fresh read handles; it refuses
overwrite and reparse ancestors and bounds artifact size. Evidence is retained
after failure. No cleanup, storage rollback or restore workflow is implemented.

**Production boot registration currently fails closed with ScopeUnresolved.**
The current installer still chooses free Boot#### indices, alternative entries,
stale description-matched BCD objects and new BCD identifiers dynamically. It
cannot supply a truthful exact declaration under existing Phase 2B3.1 rules.
No placeholder target/Boot0000 identity or generic scope is substituted, and the
snapshot model/rules were not weakened. Registration stops before privilege
enablement, capture or artifact creation. The UI's existing error handler prevents
its subsequent reboot. Preparation may already have completed; this milestone
does not undo it. Resolving the operation footprint and production capture gaps
remains necessary before this stage can proceed.

Added **14 deterministic tests** using synthetic snapshots and temporary artifact
files, including successful flush/fresh-store reopen/revalidation, non-Exact and
hash rejection, artifact corruption, persistence/reopen failure, changed or
unavailable machine state, wrong target, cancellation, no mutation on rejection,
preserved mutation exceptions and refusal to overwrite. The production registration
entry into the boundary is tested with a harmless callback and no machine readers.

Targeted validation used `--no-restore -m:1 -warnaserror`: **Preflight 182 passed**
and **Community.App 58 passed**, zero failures/skips, with no compiler warnings
or errors. `git diff --check` passed. The full solution was not rerun for this
limited integration. Fleet.Web, packages, shared RecoverySnapshotV1 APIs,
RecoveryReadiness and PreCommitGate were not changed. Production RecoveryReadiness
remains ObservationUnavailable / NotImplemented. No commit or push was performed.

## Phase 2B3.1 — shared canonical RecoverySnapshotV1, 2026-09-21

The [shared architecture and scope](../architecture/recovery-snapshot-v1.md)
define one Community + Fleet representation in `Igloo.Core/Recovery`, with
Windows acquisition in `Igloo.Preflight`. Fleet does not own the Windows
recovery engine. Local source and all existing uncommitted work were inspected;
the GUI/.NET/package work was preserved. No new live boot/registry probe or
machine configuration mutation was performed for this milestone.

Implemented contracts cover versioned scope, strong canonical Windows/target
binding, typed BCD object graph and role validation, raw/parsed firmware state,
exact ESP/Windows boot association, WinRE configuration/content identity, and
optional RTC registry before-state. RTC is mandatory for the future direct-install
boot scope because that flow writes RealTimeIsUniversal. Partition transitions
and staged file rollback remain separate future contracts.

Required BCD closure follows active/default/recovery/resume/inheritance/device
options and explicit mutation objects. Unmodified selection-list alternatives
are explicitly excluded from dependency expansion, while their ordered references
and raw observations are preserved. Assessment reports Required, RelevantOpaque,
ObservedUnrelated and UnsupportedRelevant evidence. Opaque relevant values never
silently disappear. Qualified GPT identity must agree with ordinary device structure
and correlate to captured canonical storage. Failed fields cannot become zeros,
absence or apparent changed associations.

The shared pure EFI parser validates EFI_LOAD_OPTION and the GPT HardDrive ->
FilePath -> End structure; BootOrder and BootNext preserve exact raw bytes and
native error semantics. Unsupported paths and optional bytes remain lossless.
Required opaque optional-data dependencies and unsupported attributes prevent Exact.
WinRE stores enabled/configured state and exact WIM location/content identity;
a SHA-256 read is not a bootability claim. RTC captures exact native type/raw bytes,
including value absence, with query-only independent reopen.

Canonical UTF-8 JSON uses stable set/property ordering, GUID formatting, invariant
numbers and base64 raw bytes. SHA-256 covers scoped semantic state and versions,
excluding timestamps and informational locators/diagnostics. The full artifact
retains unrelated evidence and must receive a separate durable manifest hash.
Structural/hash assessment is mandatory after deserialize; two partial snapshots
cannot validate each other. Pure comparison distinguishes ExactMatch, Changed,
Missing, ObservationUnavailable, Unsupported and Ambiguous.

`BootRecoverySupport.Exact` remains strict; typed issues supplement Partial and
Unsupported. Synthetic deterministic fixtures prove representability, **not a
real-host exact RecoverySnapshotV1**. Production capture composes canonical
readers but cannot be Exact: native firmware variable attributes are not exposed,
typed configured-WinRE capture is unavailable, required nested BCD qualification
still has unresolved provider failures, and Windows EFI optional-data dependency
semantics remain unproven. Combined capture/recapture needs later elevated-host
validation. The current direct-install footprint remains explicitly unresolved
even if a caller sets its resolution flag.

Future Community integration must capture Exact, persist, independently reopen,
verify artifact/hash/structure and revalidate identity before journaled mutation.
Future Fleet consumes the same artifact through protected state, manifest,
readiness, the existing gate, authorization and journal. Neither chain is wired
to mutation or restoration by this milestone; the monolithic DirectInstallService
was not wrapped in a fake recoverable adapter.

**RecoveryReadiness.Production remains ObservationUnavailable / NotImplemented.**
PreCommitGate and protected execution state are unchanged. No authorization is
consumed, no destructive execution/restore is added, and no commit or push occurs.
Igloo.Fleet.Web gains no Domain/Persistence/Server reference.

### Phase 2B3.1 validation

The requested `dotnet restore` succeeded. `dotnet list .\Igloo.sln package
--vulnerable --include-transitive` reported **zero known vulnerable packages**
for all 23 projects using NuGet.org. `dotnet build .\Igloo.sln -warnaserror`
succeeded with **zero warnings and zero errors**. The final
`dotnet test .\Igloo.sln --no-build --no-restore -m:1` completed normally with
**570 passed, 0 failed, 0 skipped**; no OutOfMemoryException or fallback occurred.

| Project | Passed |
| --- | ---: |
| Core | 134 |
| Preflight | 168 |
| Migration | 21 |
| Iso | 19 |
| Community.App | 58 |
| UsbWriter | 23 |
| Fleet | 147 |

This adds **118 deterministic test cases** over the 452-test baseline: 69 Core
and 49 Preflight cases. They cover serialization/hash/reopen, canonical identity,
BCD closure/roles/qualified devices and failed reads, raw/parsed EFI and explicit
unsupported state, WinRE association, RTC raw state, exact/partial/unsupported
assessment and comparison. None requires the host's current boot configuration.

`git diff --check` passed. The branch remains `refactor/community-fleet-foundation`.
Task changes comprise this document, the architecture index/new shared design,
the Core BCD-reader interface, the partial WindowsBcdReader extension, new Core
Recovery models/rules/parsers, five new Preflight read-only files, and six new test
files. PreCommitGate and protected execution state have no diff. Existing modified
GUI/project files and untracked portal/logo/polish scripts and Web UI remain in the
dirty worktree. Nothing was staged, committed or pushed.

## Phase 2B3 — elevated read-only feasibility, 2026-09-20

**Feasibility observations succeeded in several areas, but a complete exact
recovery snapshot was not proven. Production remains
ObservationUnavailable/NotImplemented. No recovery implementation, gate change,
destructive adapter, commit or push was introduced.**

### Elevation and repository baseline

The actual Codex tool process returned `True` for:

```powershell
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
whoami
whoami /groups
```

Effective user was `IGLOO-LAB\testuser`. Integrity SID
`S-1-16-12288` established high integrity. Built-in Administrators
(`S-1-5-32-544`) was enabled and a group owner, not deny-only.

Branch: `refactor/community-fleet-foundation`. The worktree was already dirty:
eight modified project/Web files and existing untracked Web UI/bootstrap/repair
work. Those changes were preserved. The baseline `dotnet build` passed with zero
warnings/errors; `dotnet test` passed **452 tests**, zero failures/skips;
`git diff --check` passed. Git's existing LF-to-CRLF notices are separate from
compiler warnings and whitespace errors. Web still references Contracts only.

### BCD: available text and typed evidence, incomplete exact snapshot

`bcdedit /enum all /v` and `bcdedit /enum firmware /v` both exited **0**.
Independent consecutive re-reads also exited 0 and returned identical text.
The listings exposed the standard Windows Boot Manager GUID, its
`\EFI\Microsoft\Boot\bootmgfw.efi` path, the Windows loader GUID, device/osdevice,
default/display-order links, recoverysequence, resumeobject, and firmware entries.
An additional installer boot-manager record and historical recovery/resume
records displayed `unknown` devices. These were observed existing state and
were not repaired or removed.

The historical English stale-entry parser is not a recovery parser. To avoid
mistaking lossy text for exact device identity, an additional read-only
`root\WMI` probe called `BcdStore.OpenStore("")`, `EnumerateObjects(Type=0)`,
`BcdObject.EnumerateElements()`, and
`GetElementWithFlags(Type=<observed device element type>, Flags=1)`.
The store opened and all **21 objects** enumerated their elements successfully.
Qualified direct-partition reads exposed GPT disk/partition GUIDs for the
Windows manager, current loader, resume and WinRE SDI device. They also recovered
GUIDs behind the text's unknown direct-partition devices; the historical and
installer partition GUIDs were absent from the current canonical inventory.
An unknown text label alone therefore does not prove unreadability.

Qualified reads of RAM-disk file devices failed, including both device and
osdevice of the **active WinRE loader**: CIM status/native error code **1**,
general provider failure. This is an **Unavailable observation**, not Win32
firmware error 1/Unsupported, not AccessDenied, and not Absent. Ordinary element
enumeration still exposed those devices' file paths, AdditionalOptions links,
and parent device data. The current WinRE parent could be correlated through
native volume mapping; an older RAM-disk parent remained an opaque 72-byte
unknown-device blob. No undocumented blob layout was guessed.

Microsoft documents the qualified-partition read and its distinction from
unknown ordinary device data in
[GetElementWithFlags](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/getelementwithflags-bcdobject)
and [BcdDeviceQualifiedPartitionData](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bcd/bcddevicequalifiedpartitiondata).
This BCD API uses GPT style **1**, unlike MSFT_Disk's GPT style **2**.
Typed observations are a viable next extension to the canonical BCD reader;
the mixed successful and failed probes are not a versioned restoration snapshot.
The dependency scope and handling of nested/opaque devices still need proof.

### WinRE, canonical storage and exact-volume BitLocker

`reagentc /info` exited **0**, reported **Enabled**, version `10.0.26100.9444`,
and a configured Windows RE directory beneath
`\\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE`.
Its BCD identifier matched the current Windows loader's recoverysequence.
The separate recovery/custom-image fields were blank with index 0; these were
not interpreted as absence of the configured WinRE image. Consecutive command
re-reads were identical and successful. Other locales and failed-command
classification were not validated by this successful English-output probe.

The built **canonical WindowsStorageReader.ReadIdentitySnapshot()** returned
Available. The current Windows disk exposed provider UniqueId/format 8, GPT disk
GUID and sector geometry; ESP, Windows and configured WinRE partitions exposed
partition GUIDs and unique GUID-based volume ownership. Other attached media
included non-GPT/reduced-identity storage; those facts were not promoted to exact
GPT identity.

In-memory read-only path probes used GetVolumePathName/GetVolumeNameForVolumeMountPoint
on the Windows system directory, and QueryDosDevice on canonical volume-GUID
names. OPEN_EXISTING metadata handles followed by
GetFinalPathNameByHandle(VOLUME_NAME_GUID) resolved the configured WinRE directory,
Winre.wim and the BCD manager's bootmgfw.efi path to their canonical volume GUIDs.
The native-device mappings agreed with the canonical partition observations and
typed BCD evidence. Disk/partition numbers and drive letters were only live
locators, never stable identity. No mount point or drive letter was assigned.
These probes follow Microsoft's
[volume mapping example](https://learn.microsoft.com/en-us/windows/win32/fileio/displaying-volume-paths)
and [handle path API](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew).

Both files were then opened with FileMode.Open/FileAccess.Read and fully read
for SHA-256: Winre.wim **811,706,096 bytes** with a WIM signature; bootmgfw.efi
**3,087,200 bytes** with an MZ signature. Successful reading and recognizable
headers do not establish image integrity against a trusted reference or prove
that WinRE will boot. No WinRE configuration command other than `/info` ran.

The built **WindowsBitLockerReader.ReadExactVolume(volume)** matched the
Windows volume's canonical GUID to Win32_EncryptableVolume.DeviceID. Conversion,
protection, lock and encryption-method observations were each Available/**0**
(fully decrypted, protection off, unlocked, no encryption). Drive letter was
informational. ESP/recovery volumes without matching encryption-provider rows
returned Unavailable/BitLockerVolumeNotUnique; non-GPT volumes lacking partition
GUIDs returned Unavailable/ExactVolumeRequired. No missing observation became
false/zero, and no key/protector methods or secrets were accessed.

### Firmware and ESP association

Only the canonical WindowsFirmwareReader/FirmwareNative read path ran.
The existing EnablePrivilege helper enabled SeSystemEnvironmentPrivilege in
the short-lived probe process token; `whoami /priv` confirmed it enabled.
No firmware writer was called and no machine privilege policy was changed.

| Fact | Elevated result |
| --- | --- |
| BootOrder | Available, native error 0, bytes `00000200`: Boot0000 then Boot0002 |
| BootNext | Absent, native error **203**, including canonical DecodeBootNext |
| Boot0000 | Available, **300 raw bytes**, repeated reads identical |
| Boot0002 | Available, **268 raw bytes**, repeated reads identical |

Raw Boot#### hex was retained in the local tool transcript. No recovery backup
or authoritative snapshot file was created. BootOrder/BootNext independent
re-reads agreed. The existing strict BootNext decoder and native error mappings
were unchanged; no absent value was converted to index zero.

A bounded in-memory structural probe validated Boot0000's load-option header,
terminated description, exact 116-byte device-path region, GPT HardDrive node
(42 bytes), file-path node and end-entire node. Its partition GUID uniquely
matched the canonical ESP; LBA start **1,116,160** and size **202,752**, multiplied
by the observed 512-byte logical sector size, matched ESP offset/size exactly.
The executable path was `\EFI\Microsoft\Boot\bootmgfw.efi`, agreeing with the
qualified BCD manager device and independently resolved executable handle.
The current loader device/osdevice matched the canonical Windows volume on that
same observed GPT disk. This establishes a conservative **primary Windows
boot-path association for these reads**, not overall recovery readiness.

Boot0000's remaining 136 optional bytes were retained. A BCDOBJECT string in
that OS-specific data was only corroboration. A GUID occurrence anywhere in a
load option is not structural identity proof. Boot0002 had an MBR HardDrive
node and multiple complete device paths; the minimal single-path structural
probe rejected that shape as unsupported rather than inventing an ESP match.
Its raw read remained Available. UEFI defines load-option/file-path-list and
device-node structure in the
[boot-manager specification](https://uefi.org/specs/UEFI/2.11/03_Boot_Manager.html)
and [device-path specification](https://uefi.org/specs/UEFI/2.10/10_Protocols_Device_Path_Protocol.html).

### ACL boundary and implementation decision

**The production WindowsProtectedDirectoryAcl policy passed the real elevated
Windows host probe. Cleanup of the entire temporary probe tree is complete.**
The dedicated root `C:\ProgramData\iGloo-Fleet-Execution-ACL-Probe` did not exist
beforehand. Following explicit scope clarification, it was created solely for
this test, with exactly one GUID child:

```text
C:\ProgramData\iGloo-Fleet-Execution-ACL-Probe\ac1b1edb-aa9b-4309-86af-5976c8c2022e
```

The built production adapter created the protected directories. Fresh Get-Acl
reads independently verified owner BUILTIN\Administrators (`S-1-5-32-544`), a
protected DACL, and exactly two explicit Allow/FullControl ACEs: SYSTEM
(`S-1-5-18`) and BUILTIN\Administrators. Both ACEs had
`ContainerInherit | ObjectInherit`, propagation `None`, and FullControl mask
`2032127`. The directory DACL was `D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)`.

| Real-host check | Result |
| --- | --- |
| Explicitly protected child directory | Accepted, with the same owner and exact explicit protected ACL |
| Ordinary inherited child directory | Correctly rejected as a protected directory: its DACL was unprotected and its ACEs inherited |
| Child files, including beneath the ordinary inherited directory | Accepted; Administrators owner, exactly inherited SYSTEM/Admin FullControl, no unexpected principal |
| Reopen from fresh tool processes and new adapter/ACL objects | Directory and file verification succeeded |
| Extra Users Modify directory ACE and its inherited child-file ACE | Both rejected; restored original ACLs and child inheritance passed again |
| Unexpected deny ACE on a child file | Rejected; original ACL restored and reverified |
| Administrators rights reduced to ReadAndExecute | Rejected after independent read-back confirmed the changed rights; original ACL restored and reverified |
| Unexpected current-user owner | Rejected; original Administrators owner restored and reverified |

One attempted rights edit initially retained FullControl on read-back and was
correctly accepted. A fresh explicit reduced-rights descriptor established the
negative case above; setter success alone was not treated as proof of a change.

The only reparse test was an internal directory junction, `inside-junction`,
targeting the same GUID child's `protected-child` directory. The existing
`ProtectedExecutionState.NoReparseAncestors` guard accepted the plain target
and rejected both the junction and a file path beneath it. ACL-only file
verification accepted that descendant, demonstrating why the separate ancestor
and reparse guard is necessary. This was a direct probe of the production guard,
not creation or reopening of real Fleet execution state.

After an earlier automatic approval rejection prevented cleanup, the explicitly
authorized cleanup continuation independently confirmed LinkType `Junction`,
the ReparsePoint attribute, and this exact target:

```text
C:\ProgramData\iGloo-Fleet-Execution-ACL-Probe\ac1b1edb-aa9b-4309-86af-5976c8c2022e\protected-child
```

`System.IO.Directory.Delete(<exact inside-junction path>, false)` removed only
the junction object, without recursion or traversal into its target. A fresh
process verified the junction absent and the target still present, with matching
ACLs, creation/last-write timestamps, child-file length and SHA-256. Only then
were the three remaining test files deleted individually and their three parent
directories removed non-recursively, followed by the empty GUID directory.
The dedicated root was independently verified empty and removed non-recursively
because this test had created it. A further fresh-process inspection confirmed
the junction, GUID directory and probe root all absent. The former target was
therefore preserved during junction deletion, then removed as ordinary scoped
test content during GUID-tree cleanup. No test artefact remains.

No access or changes to `C:\ProgramData\iGloo-Fleet-Execution` or real execution
state were part of this probe or cleanup. No BCD, WinRE, BitLocker, EFI/NVRAM,
storage or other machine-configuration operations ran in the ACL continuation.

Implementation stopped at feasibility because the complete recovery dependency
and serialization scope was not proven. The failed qualified RAM-disk reads,
opaque historical device evidence and unsupported additional firmware path
shape are limitations of the probed observation paths, not proof that current
WinRE identity is unavailable or that every historical/USB entry must be in
scope. Native-path correlation and typed BCD reads demonstrated useful
alternatives. Required dependencies must be established before excluding such
evidence or declaring a snapshot exact. This run has not proven every field
required for an exact snapshot or safe future recovery.
No RTC read/write was attempted; whether RTC state belongs in a future operation
scope remains undecided.

RecoveryReadiness.Production remains ObservationUnavailable with typed reason
NotImplemented. The pure PreCommitGate and all Phase 2B2 checks are unchanged:
fresh planning validity, explicit binding, ExactMatch target revalidation,
exact-volume BitLocker, verified/correlated protected state, Ready recovery,
unconsumed valid authorization and exact operation correlation. The gate does
not consume authorization. Even a future Ready observation will not authorize,
reserve or start mutation. No new production parser/evaluator or deterministic
tests were added on this blocked implementation path; host probes stayed
separate from the normal tests.

At the end of Phase 2B3, the remaining **Phase 2B3.1 blocker was the shared canonical RecoverySnapshotV1
design**; no RecoverySnapshotV1 has been proven. Define and prove its complete
versioned recovery dependency scope, typed nested BCD observation/error handling,
strict parsing of recovery-relevant EFI paths, WinRE image verification and
snapshot consistency; then add deterministic state/correlation/evaluator tests.
Unrelated firmware entries may be preserved raw only with a proven scope
exclusion; unsupported relevant paths must fail closed.
The existing explicit target/authorization workflow and real mutation-boundary,
verification/restoration prerequisites still apply. No configuration repair or
destructive test is authorized by these findings.

### Validation after the observations

`dotnet restore` succeeded. `dotnet list .\Igloo.sln package --vulnerable
--include-transitive` reported **no vulnerable packages** for all 23 projects
using the current NuGet source. `dotnet build .\Igloo.sln -warnaserror` succeeded
with **zero warnings/errors**. `dotnet test .\Igloo.sln` passed **452 tests**:
Core 65, Preflight 119, Iso 19, Migration 21, UsbWriter 23, Community.App 58,
Fleet 147; **zero failures/skips**. No new tests were added.
`git diff --check` passed. The only task-authored tracked change is this document;
the pre-existing eight modified files and untracked GUI/bootstrap/repair work
remain. No commit or push was performed. The later ACL probe and its cleanup
are complete as recorded above; final continuation validation is recorded below.

### Final ACL continuation validation

After successful cleanup and the documentation update, the requested commands
completed in sequence:

```powershell
dotnet restore
dotnet list .\Igloo.sln package --vulnerable --include-transitive
dotnet build .\Igloo.sln -warnaserror
dotnet test .\Igloo.sln --no-build --no-restore -m:1
git diff --check
git status --short
```

Restore succeeded. All **23 projects** reported no known vulnerable packages,
including transitive dependencies, using the current NuGet source. Build
completed with **zero warnings and zero errors**. The solution test run with
`-m:1` completed normally: **452 passed, 0 failed, 0 skipped**, with the project
counts recorded above. No OutOfMemoryException or sequential-project fallback
occurred. No tests or production source were added or changed for this ACL work.
Whitespace validation passed; Git's LF-to-CRLF notices are not whitespace errors.

The branch remains `refactor/community-fleet-foundation`. Final status contains
this modified document, eight pre-existing modified source/project files, and
the existing untracked Web UI/portal work (`Build-iGloo-Fleet-Portal.ps1`, Web
Components, FleetOperatorCli.cs, Properties, appsettings.json and wwwroot).
Only this document was edited by the ACL continuation. Nothing was staged,
committed or pushed. RecoveryReadiness.Production still returns
ObservationUnavailable/NotImplemented; PreCommitGate is unchanged. The shared
canonical RecoverySnapshotV1 design was then the Phase 2B3.1 blocker; the subsequent
shared milestone and its remaining production capture gaps are recorded above.

## Phase 2B2 — protected local state, authorization and read-only gate

This milestone adds local building blocks, not a production execution endpoint.
No partition/boot mutation, restoration, reboot, WinRE probe, complete boot
snapshot or Linux completion receipt is introduced. Community behavior and
references, canonical observation readers, and Phase 2A journal semantics remain
unchanged. The gate cannot invoke a mutation adapter.

### Protected-state layout and authority

`ProtectedExecutionState.ForMachine()` chooses one machine-local base, with no
user-profile or staging fallback:

```text
%ProgramData%/iGloo-Fleet-Execution/<AgentId N>/<ExecutionId N>/
  authority.json       schema-1 receipt: expected binding and manifest hash
  manifest.json        schema-1 immutable artifact manifest
  immutable/          explicitly named artifact bytes
  mutable/            authorization.db; future execution journal files
```

The endpoint and execution directory components come only from validated GUIDs.
Creation writes a protected pending sibling, flushes new files to disk, and uses
a same-volume rename to publish the canonical directory. Competing publication
has one winner; an existing execution is never overwritten or reused. Failed
pending directories are non-authoritative and retained for explicit operator
cleanup. There is no automatic deletion or reuse. Tests inject a temporary root;
production composition must use the machine factory and must not substitute a
weaker root after a protection failure.

The manifest binds execution, Prepared plan, endpoint, profile revision, evidence
hash, target-binding schema/fingerprint, operation set, and artifact name/size/hash.
Serialization and SHA-256 reuse EvidenceIntegrity; object keys, artifact ordering
and operation ordering are deterministic. Artifact names are deliberately flat,
restricted relative filenames: traversal, rooted paths, alternate streams,
reserved Windows device names, trailing dots and case aliases are rejected.
Reparse points are rejected throughout checked paths. Immutable bytes, names,
manifest identity and hashes are verified on every reopen; missing, changed or
unexpected immutable artifacts block use. Mutable files are outside that hash
set and still require protected ACLs and non-reparse paths.

Restart calls Open with the already-approved ExecutionBinding. The ACL-protected
authority receipt supplies the original manifest hash; it is not recomputed from
newly observed files as a replacement authority. Supplying a different plan,
execution, endpoint, target or operation set cannot reopen the original authority.

### ACL boundary and threat model

IProtectedDirectoryAcl separates protected creation from directory/file ACL
verification. WindowsProtectedDirectoryAcl creates a protected DACL with only
SYSTEM and built-in Administrators receiving FullControl, inherited by children;
directory ownership must be SYSTEM or Administrators. Reopen reads back owner,
inheritance, principals and rights. Unexpected or inherited directory ACEs,
unexpected file principals, denied ACL reads and reparse paths fail closed.
The intended Agent service identity for this policy is LocalSystem. Supporting
another service principal requires an explicit policy change and verification.

**At the end of Phase 2B2, this Windows ACL policy had not been verified on an
elevated real host.** The successful Phase 2B3 probe and cleanup are recorded
above. Phase 2B2 deterministic tests use a fake ACL adapter. No production ACL
writer was invoked for that milestone; success is not inferred from setting an
ACL alone.

The threat model excludes malicious Administrators/SYSTEM, compromised trusted
providers and whole-volume/VM rollback. Hashes detect changes against the
protected local receipt; they are not signatures or proof against an administrator
who replaces both artifacts and authority. Path checks rely on the protected
parent directories excluding untrusted concurrent writers. Phase 2C must resolve
privileged rollback, clock trust, freshness and mutation-boundary race handling
before treating these local checks as execution authority.

### Execution authorization lifecycle

ExecutionAuthorization schema 1 is separate from a Phase 1 planning approval and
from the execution journal. It binds a unique AuthorizationId (nonce), ExecutionId,
PlanId, endpoint, profile revision, evidence hash, exact-target schema/fingerprint,
and the exact set of typed operation IDs. Operations name permitted future storage
resize or boot-configuration steps; they contain no command or executable payload.
Issued/expires timestamps must be UTC, increasing, and at most ten minutes apart.
No code derives an execution authorization from an old planning approval.

SqliteExecutionAuthorizationStore is provisioned explicitly at a verified mutable
directory. Initialize exclusively creates a new database; it never repairs or
reinitializes an existing file. Database schema version 1 is checked when reading
authority. Issuance rows are immutable, hashed, and unique by authorization and
execution ID. Consumption is a separate immutable row. SQLite primary/unique
constraints, foreign keys, no-update/no-delete triggers and an immediate transaction
serialize competing consumers across store/process instances. Expiry and exact
correlation are checked after obtaining the transaction's write reservation.
Only one consumer succeeds, and committed consumption remains rejected after
store reconstruction. Unknown schema, missing/corrupt authority and database
errors fail closed. Expired authority cannot be replaced for the same execution;
a new explicit workflow needs a new execution identity.

Validate opens the database read-only, checks existence, lifetime, unused state
and exact correlation, and does not consume. Consume is a separate explicit API
for a future boundary immediately before the first authorized mutation. The gate
has neither a store dependency nor a consumption call. There is no production
provisioning/issuance route or destructive consumer in this milestone.

### Phase 1 validity reuse and gate semantics

The audit confirmed that Plans()/Approvals() refresh statuses and write audit
events. PlanningValidity now contains the shared pure rules for device trust,
certificate expiry, work/evidence expiry, newer profile revisions, latest accepted
assessment, approval state and Prepared plan state. Existing Phase 1 refresh
methods delegate to these rules and retain their externally visible behavior.
Gate composition uses IPlanningStore.Read plus PlanningValidity.Evaluate, not the
refreshing list accessors. The validity result records its evaluation instant;
the gate requires the same captured instant for evaluation rather than accepting
a stale validity result. Plan/approval identity and evidence correlation are also
checked before reporting a valid plan.

PreCommitGate evaluates trusted, freshly acquired inputs: expected execution and
operation set, pure plan validity, explicit approved target binding, shared storage
and exact-volume BitLocker observations, protected-state verification, recovery
readiness and read-only authorization validation. It calls the existing pure
TargetRevalidator and requires ExactMatch. Every applicable blocking category is
returned, with target, authorization, protected-state and recovery detail retained.
The result is Ready or Blocked; Ready is an observation result, not an execution
capability, reservation or permission to skip fresh checks at consumption.

RecoveryReadiness models Ready, NotReady, ObservationUnavailable, Unsupported and
Ambiguous, with typed reasons. **Production always supplies
ObservationUnavailable/NotImplemented.** Only deterministic tests supply Ready.
At the end of Phase 2B2, the previously blocked elevated read-only BCD, WinRE,
exact-volume BitLocker, EFI/NVRAM and ESP/Windows-boot association feasibility
checks remained required. The Phase 2B3 findings above record later observations
and remaining limitations.
No recovery partition detection or Linux marker substitutes for verified recovery.

### Validation and remaining Phase 2C prerequisites

Tests cover publication races, restart/reopen, identity reuse rejection, traversal,
artifact tampering/missing/unexpected files, ACL failure, authorization correlations,
lifetime, immutable issuance, restart and concurrent consumption, corrupt/missing
databases, pure validity reuse, every gate prerequisite, simultaneous blockers,
unchanged authorization after repeated gate evaluation, and architecture boundaries.

Verification completed with **452 passing tests (45 new)**, zero failures/skips,
and a Release build with zero warnings/errors under warnings-as-errors. Restore,
the Phase 1 and Phase 0 real Windows read-only demonstrations, and whitespace
checks passed. New untracked source files were checked separately for whitespace.
No production ACL write, elevated recovery probe, destructive operation, commit
or push was performed.

Files for this milestone:

- Agent: `Execution/ProtectedExecutionState.cs`, `Execution/WindowsProtectedDirectoryAcl.cs`,
  and `Execution/PreCommitGate.cs`.
- Domain: `ExecutionAuthorization.cs`, `PlanningValidity.cs`, and shared changes to
  `ApprovalService.cs`, `EnrollmentService.cs`, `EvidenceIntegrity.cs`.
- Persistence: `SqliteExecutionAuthorizationStore.cs`.
- Tests: `ExecutionBoundaryTests.cs`, with the existing target test fixture made
  reusable in `TargetRevalidationTests.cs`.
- Documentation: this file. Prior Phase 2B1 uncommitted work remains intact.

At the end of Phase 2B2, the prerequisites before Phase 2C were: verify the
production Windows ACL model on an elevated host;
complete elevated recovery feasibility and real RecoveryReadiness; design a trusted
explicit target-binding/authorization issuance workflow; integrate the protected
directory and journal with fresh execution-boundary checks; and prove real shared
mutation/verification/restoration semantics without changing Community selection
or sequencing. The later Phase 2B3 findings above record the completed ACL proof
and remaining recovery limitations. Production execution remains disabled.

## Phase 2B1 — completed identity milestone (historical scope)

Phase 2B1 extends the consolidated readers; **Phase 2B remains incomplete**.
There is no second Fleet Windows inspector. Core contains local, typed facts;
Preflight owns WMI and native reads; Fleet.Agent/Targets contains immutable
bindings and pure comparison. Community callers still use their existing
compatibility projections. Phase 2A StorageState, journal records and recovery
state transitions have not been reinterpreted or changed.

### Observation availability and compatibility

`Observation<T>` distinguishes Available, Unavailable, Unsupported, AccessDenied,
Ambiguous and Absent. A failed fact cannot expose a default zero/false value.
Missing WMI properties are Unsupported; present null or invalid properties are
Unavailable. Partial/failed inventory enumeration cannot become a successful
identity snapshot. Empty successful enumeration remains distinct from failure.
Native error numbers remain available on firmware observations; diagnostic codes
on typed observations contain no private device data.

Community still receives physical-drive-number DeviceId paths, the original
FreeBytes meaning, offset ordering, -1/null offsets/types and zero shrink fallback.
Resize candidate selection, mutation sequencing, Linux removal safeguards,
BitLocker's C: compatibility query and Unknown fallback remain unchanged.
Legacy raw rows and ValueOrThrow behavior remain available to these callers.
The strict supported-size projection preserves provider rejection separately
from successful SizeMin/SizeMax, including unsupported and access-denied results.

### Stable, structural and transient facts

WindowsStorageReader now captures MSFT_Disk UniqueId, UniqueIdFormat, SerialNumber,
BusType, FriendlyName, Size, PartitionStyle, GPT disk GUID, both sector sizes and
Number. Partition facts include GUID, GPT type, disk/partition number, offset,
size, system/boot/active flags, access paths and drive letter. Volume observations
include GUID path identity, owning partition GUID, filesystem, label, drive
letter, Status and DirtyBitSet when supplied by Windows. Volume ownership is
joined through a matching GUID access path; drive letters never establish it.

These fields follow Microsoft's [MSFT_Disk contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk).
The current conservative binding supports GPT with a nonempty provider UniqueId
in EUI64, FCPH or SCSI-name format (2, 3, 8), known nonvirtual bus, disk GUID,
partition GUID and volume GUID. Vendor-specific/missing identifiers and reported
virtual/file-backed buses have reduced or unavailable identity strength and
cannot produce ExactMatch. MBR is Unsupported; it is never promoted to GPT.
Provider identifiers are correlation evidence, not hardware attestation: cloned
identifiers, dishonest providers and observation races are not solved here.

`ExactTargetBinding` schema **1** binds PlanId, ApprovalId, endpoint, profile
revision and evidence hash to:

- Stable facts: disk UniqueId/format and GPT GUID, partition GUID, volume GUID.
- Structural facts: disk size/style/sector sizes, partition offset/size/type,
  filesystem and label. This version conservatively requires filesystem/label.
- Informational facts: disk number, partition number and drive letter.

TargetFingerprint hashes schema, stable and structural facts only. Locator changes
do not change equivalence. Within a fresh inventory, disk numbers only associate
partition rows with disk rows; duplicate associations are ambiguous. Old locators
can explain a replacement mismatch but can never prove a positive match.

### Exact-volume BitLocker and firmware reads

WindowsBitLockerReader adds an exact-volume path alongside the unchanged
Community query. It correlates Win32_EncryptableVolume DeviceID to the observed
volume GUID and reads GetConversionStatus, GetProtectionStatus and GetLockStatus.
EncryptionMethod and drive letter are optional metadata. Missing/denied provider
results and unknown status values block exact revalidation; evidence for another
volume is rejected. No protector or key material is read. DeviceID correlation
and fresh status methods follow the [Win32_EncryptableVolume contract](https://learn.microsoft.com/en-us/windows/win32/secprov/win32-encryptablevolume).

The canonical firmware reader adds BootNext using the existing native read path.
Decoding requires exactly one little-endian 16-bit entry index. Missing variable
(native 203), access/privilege denial (5/1300/1314), unsupported call (1/50), other
native failure, and a present value remain distinct; malformed BootNext is
Ambiguous. Buffer sizes, EfiBootEntries behavior and privilege/write sequencing
are unchanged. This does not constitute a complete boot recovery snapshot.

### Revalidation and approval boundary

TargetRevalidator takes an explicitly approved binding, its Prepared plan, a fresh
shared storage observation and volume-correlated BitLocker observation. It has
no Windows reader, process, native, journal or mutation dependency. Outcomes are:

| Outcome | Meaning |
| --- | --- |
| ExactMatch | Required stable/structural facts agree and BitLocker observation belongs to that volume. |
| Changed | Explicit plan/binding correlation or an identity/structural fact differs. |
| Missing | Successful inventory does not contain the required target. |
| Ambiguous | Duplicate identity or conflicting ownership prevents a unique match. |
| Unsupported | Binding schema, identity strength/style or provider capability is unsupported. |
| ObservationUnavailable | Required facts cannot be observed, including access denial or missing binding. |

Each blocked result includes a typed reason. Unknown facts cannot trigger a
heuristic fallback. An old Prepared plan without an explicitly approved binding
returns BindingRequired; no factory enriches old approvals from current state.
No binding is attached to existing plan persistence or network protocols.
ExactMatch is only an identity result: it does not validate approval freshness,
BitLocker execution safety, recovery capability or permission to execute.

### Validation and remaining work

Deterministic tests cover raw failure versus zero/absence, supported-size errors,
provider identity projection, GUID-based volume ownership, BootNext decoding,
locator changes, disk replacement, partition/volume replacement, geometry/type
and filesystem/label drift, missing and duplicate targets, reduced VM identity,
MBR rejection, wrong-volume/unknown BitLocker evidence, explicit approval
correlation, old plans, binding fingerprints and pure comparison architecture.
Existing Community compatibility and project-reference tests remain in place.
No elevated identity or boot probe is required by these tests.

Verification: 407 tests pass (55 added since the 352-test consolidation baseline),
with zero failures/skips. Restore and the Release build with warnings treated as
errors pass, with zero warnings/errors. Both existing Windows read-only Phase 0
and Phase 1 demonstrations pass. Whitespace validation includes new source files.
The demonstrations exercise Community-compatible preflight, not elevated proof
of the new exact-volume or complete boot/recovery capabilities.

At the end of Phase 2B1, the remaining work was recorded below. Phase 2B2 above
supersedes its local-state, authorization and gate TODOs; elevated recovery work
remains blocked.

Before RecoveryReadiness can be implemented, the previously blocked elevated
read-only BCD, WinRE, exact-volume BitLocker and EFI/NVRAM feasibility checks must
succeed, including exact ESP/Windows boot association. A future approval workflow
must explicitly approve schema-1 bindings; snapshot freshness/race handling and
provider identity limitations need execution-boundary treatment. Protected local
state/ACL verification, durable single-use authorization and a read-only gate are
still outstanding. No WinRE implementation, RecoveryReadiness, protected execution
directory, authorization, production endpoint, real partition/boot mutation or
restoration, reboot or Linux completion receipt is enabled by Phase 2B1.

## Historical shared observation extraction milestone — 2026-09-20

The reuse-audit extraction is implemented. This is consolidation of existing
Community observations, not completion of Phase 2B identity/readiness semantics.
The older feasibility assessments below remain historical records of host access.

Core now exposes narrow storage, BitLocker, firmware and BCD read interfaces.
WindowsStorageReader centralizes disk/partition/ESP enumeration, volume properties
and GetSupportedSize. Raw WMI property values and nulls are retained in local
property bags; failures remain explicit, including errors following partial
enumeration. They are not exact-target proofs or public Fleet evidence.

WindowsPreflightChecker, PartitionResizeService, DirectInstallService and
LinuxRemovalService consume the shared storage implementation. Their existing
compatibility conversions remain: physical-drive-number paths, unallocated
capacity, offset ordering, -1/zero/null fallbacks and per-caller selection rules.
Existing mutation services bind their selected WMI object path only within their
existing mutation flow; no write methods are exposed by the observation interface.
The shared size-query implementation retains the callers' null versus explicit
method-parameter behavior.

WindowsBitLockerReader owns the existing provider query and interpretation.
Community still queries C: and still maps missing/failed observations to Unknown.
No exact-volume BitLocker binding or lock-state claim was introduced.

FirmwareNative contains the single firmware read/write declarations and privilege
enablement implementation. Existing callers retain privilege-call placement and
their differing diagnostic policies. WindowsFirmwareReader exposes only reads
and retains native errors. DirectInstall's 256-byte BootOrder buffers and
EfiBootEntries' 4096-byte reads, scanning ranges, description matching and failure
fallbacks remain unchanged. Existing firmware writes were relocated to the common
native declaration without adding or invoking a write operation.

WindowsBcdReader exposes only fixed firmware enumeration. Raw results explicitly
remain Unparsed, CommandFailed or Unavailable. BcdListingParser contains the moved
stale-identifier parser; DirectInstall's compatibility method forwards to it.
Existing private BCD write execution remains in DirectInstall. The native
executable path resolver is shared; no generic command runner was introduced.

Community DI registers the shared readers, and existing constructor overloads
remain usable. Fleet's existing preflight composition reaches these same readers;
no alternative Fleet inspector or project reference was added. Phase 2A contracts,
journal and coordinator are unchanged. No WinRE, authorization, protected execution
state, target revalidation, recovery-readiness evaluator or gate was implemented.

Fourteen new characterization cases passed before observation extraction, followed
by fourteen shared-reader/compatibility cases. Coverage includes disk and partition
projections, BitLocker's C: query and failures, resize tie-breaking without an
NTFS/OS-volume filter, partial enumeration, firmware failure/scanning behavior and
unchanged BCD parser quirks. The solution now passes 352 tests with zero failures
or skips; Release build with warnings as errors passes with zero warnings/errors.
Restore, both real Windows read-only Fleet demonstrations, and git diff --check
also pass. New untracked source files were checked separately for whitespace.
No destructive demonstration, commit or push was performed.

Remaining overlap is outside this extraction: UsbWriter's removable-media
Win32_DiskDrive listing, existing private mutation plumbing, and unrelated GPU,
TPM/display/application observations. The next semantic milestone should enrich
these canonical providers and add strict Fleet projections around their results,
without replacing the Community compatibility mappings. Complete boot/recovery
feasibility remains subject to the documented read-access checks.

## Historical host feasibility findings — recovery inspection still blocked

### 2026-09-20 resumed feasibility inspection

The resumed task expected an elevated session, but the actual 64-bit tool process
reported `WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(Administrator)`
as **false**. No elevation was attempted. Read-only probes independently confirmed
that the required access is still unavailable:

- `bcdedit.exe /enum all /v`: the boot configuration data store could not be
  opened; access denied. Boot manager/loader identifiers, device references,
  bootsequence and display order could not be captured.
- `reagentc.exe /info`: requires an elevated command prompt; operation failed
  with error 5. WinRE status, configured location and recovery-partition binding
  could not be captured.
- Exact-volume BitLocker was not re-probed after the stop condition. The prior
  provider access denial remains unresolved; no volume-bound readiness is claimed.
- EFI/NVRAM reads were not attempted after the stop condition. BootOrder,
  BootNext, Boot#### contents and their ESP correlation remain unverified.
- The temporary directory/ACL check was not reached. No ACL test directory was
  created and protected-state feasibility remains unverified.

Implementation stopped immediately after these access failures. Phase 2B remains
unimplemented. This is an observed process-permission limitation, not evidence
that Windows or this firmware cannot provide the necessary read interfaces.
Successful storage inspection from the prior session does not substitute for
missing boot/recovery evidence.

The next session must verify elevation in the **actual command/tool process**,
then repeat read-only feasibility checks. Proposed BCD probes are
`bcdedit.exe /enum all /v` and `/enum firmware /v`; these enumeration commands
do not modify BCD, but successful enumeration must still be evaluated for complete
snapshot coverage. WinRE inspection uses `reagentc.exe /info`. Neither command's
output has yet established an exact recoverable snapshot in this task. Firmware
read access, exact-volume encryption correlation and ACL read-back still require
their own successful checks. No production-directory, authorization, gate or
adapter code was added, and no boot/storage mutation was performed.

The requested post-inspection verification was rerun on 2026-09-20: restore
passed; Release build passed with zero warnings/errors; all 324 tests passed
with zero failures/skips; Phase 1 enrollment/planning/restart persistence and
Phase 0 read-only assessment demonstrations passed; git diff --check passed.
Only this assessment document changed among tracked files. Verification produced
its normal ignored build/test artifacts; no feasibility-test directory or
production execution state was created. No commit or push was performed.

### 2026-09-19 initial assessment

Phase 2B was attempted on 2026-09-19. It is **not implemented**. The initial
read-only capability probes hit the requested stop conditions before any source
code changes: this process is not elevated and cannot read the required boot
configuration or obtain volume-bound BitLocker evidence. Phase 2A remains the
implemented baseline; the findings below are diagnostic observations, not a new
adapter, execution authorization or readiness result.

| Read-only probe | Observed result | Implication |
|---|---|---|
| Get-Disk / Get-Partition | GPT disk GUID, hardware unique ID/serial, partition GUIDs, numbers, geometry, GPT types, access paths and boot/system roles available on the Windows disk | Stable target identity appears representable; full volume binding and revalidation remain unimplemented |
| Additional attached disks | MBR layouts without GPT disk/partition GUIDs | Must be explicitly Unsupported by the proposed GPT identity adapter, never inferred from disk numbers or drive letters |
| bcdedit.exe /enum firmware /v | Exit 1: boot configuration store access denied | Exact BCD/Windows boot manager snapshot cannot be proven in this session |
| reagentc.exe /info | Exit 5: elevated command prompt required | WinRE configuration and resolvability remain Unknown |
| Win32_EncryptableVolume through root/CIMV2/Security/MicrosoftVolumeEncryption | Access denied | BitLocker state cannot be associated with the exact target volume in this session; remains blocking Unknown |

Hardware serials and volume identifiers are intentionally not copied into this
public document. The probes did not select or authorize a mutation target. No
partition changes, boot writes, firmware writes, BitLocker changes, elevation,
reboot or Linux installation were attempted.

The seven-step Phase 2B demonstration stopped during initial inspection. No exact
boot snapshot, authorization, fresh plan revalidation or pre-commit gate result
was fabricated. Protected-directory ACL verification and durable authorization
consumption have not been implemented or tested for Phase 2B.

The next prerequisite is an explicitly available elevated Windows inspection
session in which the same read-only BCD, WinRE and volume-specific BitLocker
queries succeed. Firmware-variable and EFI read access, complete snapshot scope,
and protected-state ACL semantics must then be verified before continuing; an
elevated token alone does not establish those properties. Community changes or
destructive host operations are not needed to resolve the current access blocker.

Phase 2C remains blocked by all unimplemented Phase 2B requirements: exact fresh
plan/target/evidence binding, proven recovery readiness, complete deterministic
boot capture, protected immutable local artifacts, durable single-use
authorization and a read-only pre-commit gate. Real partition mutation, boot
mutation/restoration, reboot execution, Linux completion receipts and production
execution endpoints remain disabled.

Baseline verification after these probes passed: solution restore; Release build
with warnings as errors (zero warnings/errors); all 324 tests (zero failures or
skips); Phase 1 enrollment/planning/restart-persistence demonstration; Phase 0
actual Windows assessment demonstration; and git diff --check. No tests were
added and no source/project dependencies changed. Verification logs are ignored
local `test-logs/phase2b-*` artifacts. No commit or push was performed.

## Architecture and scope

Core now defines immutable storage identities, exact before/after states, typed
inspect/apply/verify contracts, boot snapshots and restoration contracts. Storage
identity includes hardware identity, disk GUID, partition GUID and partition
number. Geometry, filesystem and label participate in exact value comparison;
a disk ordinal alone is rejected. Boot snapshots use a versioned, explicit scope
and immutable canonical content. A future adapter must define that format and
prove its completeness; the fake format does not describe real Windows state.

Fleet.Domain defines correlated, versioned journal records and the journal
interface. Fleet.Persistence implements a separate SQLite execution journal,
with append-only events, contiguous per-operation sequences and immutable intent.
UPDATE and DELETE are rejected by database triggers. Commits use synchronous FULL.
Records carry ExecutionId, Prepared PlanId, device/Agent identity, approval,
evidence ID/hash, profile revision, operation ID, exact input states, UTC time,
observed state and verified receipt or failure classification. These are local
records, not network execution authorization or signed success attestations.

Fleet.Agent implements the generic recovery coordinator. It has no host
registration, CLI switch or HTTP endpoint. The deterministic fake adapters live
in the Fleet test project; they cannot be selected by the production Agent.
Their machine state is independent of the SQLite journal and survives coordinator
and adapter reconstruction in tests.

All project references and Community behavior remain unchanged. Persistence uses
a generic intent type, so Domain and Server do not gain a Core execution dependency.
The existing planning database and protocol remain unchanged. Prepared plans
remain non-executable. No production adapter, real disk/boot action, generic
remote command, reboot or Linux installation is enabled.

## Durable lifecycle and recovery

A single canonical local journal path is required per endpoint. The coordinator
holds an OS-enforced exclusive file handle across inspection, durable intent,
apply, verification and receipt. A competing coordinator fails closed with an
I/O error; it does not wait on or steal an expiring lease. Process termination
releases the handle. The persisted intent prevents a later caller from applying
the same operation again. Operation IDs must be stable across retries. This is
at-most-one apply attempt per operation, not a claim of exactly-once hardware
execution. A future authorization layer must prevent minting a second operation
ID for the same approved mutation.

The first call validates immutable correlation and target facts, inspects actual
state, and commits Intent before invoking the adapter. A mismatched or uncertain
initial state records RecoveryRequired without applying. A successful apply is
followed by a separate inspection and independent VerifyAsync read-back. Only
an exact, uncertainty-free authorized after-state yields a VerifiedReceipt.
A rejected-before-mutation result plus independently observed unchanged state can
produce FailedWithoutMutation. An exception alone never establishes that result.
Ordinary apply errors record ApplyInterrupted and trigger inspection. Cancellation
or simulated process loss propagates, leaving the durable intent for recovery.

RecoverAsync loads authority from the journal, reacquires the exclusive guard,
and inspects actual state before interpreting completion:

| Actual evidence | Appended record | Returned outcome |
|---|---|---|
| Exact expected before-state | ObservedNotApplied | NotApplied |
| Exact authorized after-state and independent verification | VerifiedReceipt | AppliedAndVerified |
| Neither state provable, unavailable inspection or failed verification | RecoveryRequired | AmbiguousRecoveryRequired |

Recovery and repeated calls **never apply**, even when the state is NotApplied.
An old receipt cannot override current drift. After an external recovery, a later
inspection may establish an exact state; this appends new evidence without erasing
the prior ambiguity or retrying the mutation. A new authorization/resolution
workflow is deliberately not implemented. Intent-write failure prevents apply;
receipt-write failure leaves intent available for read-back recovery.

Boot restoration is a separate journaled operation using BootRestorationAdapter.
It requires an Exact captured snapshot matching the authorized destination and
scope. Partial/unsupported snapshots are rejected before restoration. Restoration
gets independent verification just like forward mutation; partial actual state
remains recovery-required. No automatic rollback is attempted.

## Safety invariants and limits

- Never authorize by disk number or rely on an adapter exception as proof of no effect.
- Persist intent before any attempt, and persist a receipt only after independent read-back.
- Never overwrite journal history or change correlation/geometry under an operation ID.
- Never replay an existing operation, including one interrupted before apply.
- Never trust a historical receipt over freshly observed actual state.
- Never use Community FileStagingService's disposable staging directory for this journal.
- Never map Linux `.done` markers or a zero exit code to a verified receipt.
- Never interpret the fake adapter's results as real host evidence.

The journal constructor requires an explicit path; no runtime path is registered.
Tests use isolated temporary directories. Phase 2B must select a protected,
non-staging local path, enforce endpoint ACLs and one canonical journal location,
and establish disk flush/filesystem assumptions. Database triggers are not a
security boundary against an administrator replacing or editing the database.
Missing/corrupt history requires recovery; callers must not recreate authority
under a fresh ID. Record versions and sequence/authority inconsistencies fail
closed. The generic API is an internal building block, not a permission boundary.

## Non-destructive test coverage

Deterministic tests cover durable ordering, successful receipts, process loss
before mutation, partial mutation, after mutation before receipt, and after
verification before journal completion. They reconstruct the journal and adapters
from the same SQLite file while retaining independently modeled machine state.
Additional cases cover ordinary exceptions before/after effects, intent/receipt
write failures, geometry drift, uncertain observations, immutable authority,
sequence rejection, exclusive coordination, repeated calls, ordinal-only target
rejection, boot mutation/restoration crashes, exact restoration and unsupported
or partial snapshots. Existing architecture tests enforce unchanged Community
and Fleet dependency boundaries.

## Verification

The baseline was 301 passing tests. Phase 2A adds 23 tests, for **324 passing,
zero failed and zero skipped** (Fleet: 64; all other project totals unchanged).
Restore and the Release warnings-as-errors build pass with zero warnings/errors.
The Phase 1 read-only Windows demonstration passes, including enrollment,
assessment/dry-run, explicit approval, Prepared plan and persistence after Server
restart. The Phase 0 demonstration also passes with actual Windows preflight
submission/retrieval. git diff --check passes; new untracked files were checked
separately with git diff --no-index --check and have no whitespace errors.
Logs remain under ignored test-logs/phase2a-* files.

## Remaining prerequisites for Phase 2B

1. Define and verify a real Windows identity/geometry observation format, including
   unambiguous target selection, filesystem facts, sector alignment and unsupported layouts.
2. Decompose canonical shared execution primitives into independently verifiable
   steps without silently changing Community. Do not wrap/replay the monolithic preparation call.
3. Implement complete BCD/EFI/NVRAM snapshot, verification and restoration support;
   explicitly reject unsupported or partial recovery. Prove this on disposable targets.
4. Add fresh exact-target BitLocker/recovery readiness checks, immutable artifacts,
   protected journal deployment and single-use execution authorization bound to
   the approved plan/evidence. Planning approval alone remains insufficient.
5. Establish authenticated execution-correlated Linux receipts and cleanup ordering.
6. Obtain explicit disposable VM/hardware authorization before any destructive tests.

## Historical real-adapter readiness findings

The following baseline findings still block wiring real execution. They do not
block the isolated Phase 2A foundation implemented above.

## Stop condition: interrupted partition preparation is ambiguous

[IDirectInstallService](../../src/Igloo.Core/Abstractions/IDirectInstallService.cs)
exposes one `PrepareAsync` operation followed by `RegisterBootEntryAsync`.
Inside [DirectInstallService](../../src/Igloo.Preflight/DirectInstallService.cs),
preparation can remove an old installer partition, resize a partition, create
several partitions and write installer artifacts in the same call.

The concrete interruption window is between the resize call at line 279 and
installer partition creation at line 284. Recovery currently detects a reusable
installer volume by label/size (line 228), not an execution receipt. If the process
dies in that window, retrying preparation re-enters the resize path without a
durable record proving the preceding mutation completed. This is a code-path
finding; no destructive retry was performed to reproduce it.

[PartitionResizeService](../../src/Igloo.Preflight/PartitionResizeService.cs)
accepts a disk number and allocation amount. It selects the candidate with the
largest reported shrink allowance (lines 107–136), calculates a new size from a
fresh probe (line 64), and invokes Resize (line 85). It does not accept an exact
partition identity, expected before-state, authorized after-state or execution
ID, and does not return a durable verified mutation receipt.

Phase 1's [planner](../../src/Igloo.Fleet.Agent/ReadOnlyPlanner.cs) observes the
boot partition, while the resizer may select a different candidate on the same
disk. A Fleet wrapper cannot treat those as the same authorized target.

An outer journal recording only "Prepare started/completed" cannot satisfy the
requirement to journal and verify every mutation or determine a safe retry after
a crash. This does not establish that the engine can never be decomposed; it
establishes that its present public contracts are insufficient for safe Fleet
resume. A shared-engine contract change and recovery tests must precede an adapter.

## Stop condition: boot recovery and restart cannot be proven

DirectInstallService keeps installer drive, disk and partition information in
in-memory fields (lines 94–103). Boot registration requires those fields and
fails without them (lines 1308–1311). There is no transaction-bound restore API.
Re-running all preparation just to rebuild those fields is not a safe recovery
strategy.

Boot registration writes Boot####, BootNext, BCD entries, BootOrder and RTC
configuration. Its BCD path is explicitly best effort; nonzero command exits are
logged rather than returned as a typed failed step (line 1568). The combined call
does not produce an exact before/after backup or verified restoration receipt
covering all these state changes. Successful return cannot stand in for verified
boot-state recovery.

[LinuxRemovalService](../../src/Igloo.Preflight/LinuxRemovalService.cs) is a
separate removal/reclamation workflow. It does not consume an execution journal
to restore the exact pre-migration partition and boot state. It must not be
advertised or invoked as automatic Fleet rollback.

## Additional execution prerequisites discovered

### Recovery and BitLocker observations

The inspected shared contracts contain no RecoveryReadiness result proving WinRE
availability, a known-good Windows boot entry, saved BCD/EFI state, verified
partition snapshot and recoverable local manifest. Recognizing a recovery
partition or having removal code does not prove recoverability.

[WindowsPreflightChecker](../../src/Igloo.Preflight/WindowsPreflightChecker.cs)
queries BitLocker for `C:` (lines 480–513) and returns Unknown on missing/failed
observations. It does not bind recovery evidence to the exact execution volume.
The real baseline returned Unknown. No suspension or decryption was attempted;
that observation must block execution. This is an endpoint observation, not a
claim that BitLocker can never be reliably checked on a configured test machine.

### Durable local artifacts

[FileStagingService](../../src/Igloo.Migration/FileStagingService.cs) uses a
per-distro staging directory and deletes a previous staging directory on entry
(lines 36–44). Fleet cannot store its recovery journal there and blindly reuse
the existing staging call on restart. Execution needs an explicit immutable
artifact set, a protected transaction directory and hash-aware resume behavior.

[IsoAcquisitionService](../../src/Igloo.Iso/IsoAcquisitionService.cs) already
provides canonical HTTPS/checksum/signature validation and should remain the
source of installer acquisition. Its success alone does not prove that the
later copied boot artifacts match the approved execution.

[UsbWriterService](../../src/Igloo.UsbWriter/UsbWriterService.cs) performs raw
media writes and partition-table work. It is not a safe fallback when direct
installation recovery is ambiguous and was not invoked.

### Linux correlation and completion

[MigrationManifest](../../src/Igloo.Core/Models/MigrationManifest.cs) remains
private local execution state. The inspected handoff has no Fleet ExecutionId,
PlanId, profile-revision and original-manifest-hash receipt linking a first boot
to a specific authorization.

The [Debian-family agent](../../distros/_debian-family/agent/agent.py) collects
step exceptions but returns zero after the loop (lines 2652–2663). Its
[first-boot launcher](../../distros/_debian-family/agent/first-boot.sh) writes
`.done` after invocation without requiring successful post-boot validation.
The [Fedora launcher](../../distros/fedora-kde/agent/first-boot.sh) also writes
its done marker after logging an Agent failure. These markers prevent reruns;
they are not proof that migration succeeded.

Fleet must not map a zero exit code, `.done`, or "Linux booted" to Completed.
An execution-correlated receipt must report required step and post-boot results,
retain failure/unknown states, and survive manifest redaction and seed cleanup.
That receipt also needs an explicit trusted reporting path without copying the
Windows Agent private key or operator credential into installer artifacts.
## Issue #241 — Community planning checkpoint (2026-09-26)

Priority returned to Community; #243 was not started. Issue #241 is **not closed**.
The new immutable read-only candidate planner uses the shared canonical readers,
pins the prepared target, chooses a proven-absent Boot#### and BCD GUID before any
recovery capture, and records the known firmware/BCD/RTC intents. Complete-sequence
checks reject extra/missing/substituted intents; structural revalidation includes
the entire stale/sibling candidate set as well as BootOrder/BootNext and identity.

Production still stops before capture, artifact creation and all boot writes.
The Windows BCD-to-firmware synchronization footprint has no supported binding to
the chosen Boot####; description-only stale ownership and `/delete /f` reference
effects are also unproven. The legacy dynamic writer remains unreachable, not
certified as a plan-bound executor. A resolved RecoveryScopeV1 is deliberately not
manufactured. The successful direct-install durable-path acceptance test remains
blocked; existing generic durable-boundary fixtures are not reclassified as proof.

The Community dependency review retains all three outstanding #242 interpretation
gaps: WinRE state, active RAM-disk qualification and Windows EFI optional data.
Their exclusion is not proven while BCD copy/delete/synchronization effects remain
unbounded. See the [shared architecture checkpoint](../architecture/recovery-snapshot-v1.md#issue-241-community-planning-checkpoint-2026-09-26)
for the operation-by-operation reasoning and Microsoft API contracts.

RecoverySnapshotV1 and Exact rules were not changed. Production RecoveryReadiness
remains ObservationUnavailable / NotImplemented; PreCommitGate and Fleet.Web were
not changed by this continuation. No boot/BCD/firmware/registry/storage mutation,
VM migration, commit or push was performed.

Validation for this checkpoint: 27 new deterministic planning cases passed.
Targeted Preflight: 276 passed; Core: 134 passed; Community.App: 58 passed, each
with `--no-restore -m:1 -warnaserror`. `dotnet build -warnaserror` completed with
zero warnings/errors. The full `dotnet test .\Igloo.sln --no-build --no-restore
-m:1` suite passed **678 tests, 0 failed, 0 skipped** (134 Core, 21 Migration,
19 Iso, 58 Community.App, 276 Preflight, 23 UsbWriter, 147 Fleet). `git diff --check`
passed; existing LF/CRLF conversion notices were separate warnings. These tests
do not validate a native mutation executor or an Exact Community recovery scope.

## Issue #241 — checked native executor continuation (2026-09-27)

The legacy Community writer was removed. Registration now invokes a plan-bound
executor owning the durable boundary; native access is private to the callback
after verification. The interpreter validates the complete immutable program and
each exact target/payload, and never generates identities or selects stale objects.

BCD creation now compiles explicit CreateObject with the planned GUID/type and
typed replication of retained boot-manager elements. Device/path/description are
replaced; locale/inherit are omitted; unsupported retained values reject planning.
Returned creation GUID/type are checked. GUID-qualified GPT setters replace the
drive-letter command. There is no CopyObject/BCDEdit mutation path.

Core inbound-reference analysis bounds deletion conservatively: any existing
reference or incomplete/opaque reference scan rejects the deletion rather than
performing a reference rewrite. Execution reopens the graph and pinned stale
object immediately before DeleteObject; no `/delete /f`, fresh description search,
force or automatic cleanup remains. Intermediate/final BCD graphs are checked.
Firmware writes use planned bytes/attributes; RTC requires an existing fixed key.

**#241 remains open, and is not code-complete pending only VM validation.** Exact
live-store BCD-to-NVRAM synchronization remains unsupported, as does stale ownership
where only a description identifies a candidate. No resolved scope is invented.
The shared Exact rules, RecoveryReadiness and PreCommitGate remain unchanged;
Community still cannot reach durable capture/reopen/ExactMatch. The successful
production-plan boundary acceptance test remains blocked, not simulated with a
generic scope or test-only override.

Graph fixtures demonstrate the copied default -> Windows loader -> recoverysequence
-> WinRE/RAM-disk dependency. Disconnected WinRE is excluded only from that local
graph traversal, not from the incomplete production recovery scope. Windows EFI
optional data remains relevant while provider firmware effects are unbounded.
See the [architecture continuation](../architecture/recovery-snapshot-v1.md#issue-241-checked-native-executor-continuation-2026-09-27)
for contracts, limitations and the future disposable-VM validation procedure.
No native mutation or destructive VM test was run, and #243 was not started.

Validation: 48 new deterministic cases (18 Core, 30 Preflight); targeted runs with
`--no-restore -m:1 -warnaserror` passed Core **152**, Preflight **306** and
Community.App **58** tests. `dotnet build -warnaserror` passed with zero warnings
and errors. Full serialized `dotnet test .\Igloo.sln --no-build --no-restore -m:1`
passed **726 tests, 0 failed, 0 skipped**: Core 152, Migration 21, Iso 19,
Community.App 58, Preflight 306, UsbWriter 23, Fleet 147. The focused interpreter
success tests use fakes; they do not certify native effects or production Exact.
`git diff --check` passed; LF/CRLF conversion notices are not check failures.

## Issue #241 — firmware-effect boundary audit (2026-09-27)

Continued from the existing immutable planner and native executor. **#241 remains
open and not code-complete; #243 was not started.** No production implementation
was changed in this continuation because the required supported BCD-to-firmware
effect mapping could not be established. New regression tests preserve the
existing rejection behavior; they do not certify implicit native effects.

The actual PowerShell tool process was Administrator=True, High integrity, user
`IGLOO-LAB\testuser`, with enabled Administrators membership.
Read-only provider metadata confirmed CreateObject's explicit BCD GUID input and
the signatures of DeleteObject and all seven typed setters. None exposes an EFI
index binding. `bcdedit /enum firmware /v` exited 0. Fresh-process canonical reads
observed BootOrder `[0000,0002]`/attributes 7, BootNext Absent/203, Boot0080
Absent/203, Boot0000 with 136 opaque optional bytes, and 21 BCD objects.
The installer-labelled stale candidate had an opaque DeviceType 5 device;
canonical incoming-reference analysis returned Unsupported, not an empty set.

The reviewed Microsoft contracts do not bound the complete EFI write set of the
live-store BCD create/set/delete calls. High-level fwbootmgr BootNext/BootOrder
associations do not provide a chosen Boot#### allocation contract. Explicit
firmware writes already name their exact variables, but dropping BCD-managed
registration would change intended boot behavior; no equivalence was established.
No undocumented GUID/index/optional-data interpretation was introduced.

Stale ownership remains separate from incoming-reference completeness. Failed or
nonempty reference scans reject deletion; even a successful empty scan plus a
matching description does not establish ownership or firmware effects. Known
instruction/scope coverage remains checked, but no complete RecoveryScopeV1 or
successful production-plan durable-boundary path is claimed. WinRE configured
state and active RAM-disk qualification remain required under the retained graph
closure; EFI optional-data dependencies remain unbounded. Exact rules,
RecoveryReadiness (ObservationUnavailable / NotImplemented), PreCommitGate and
Fleet.Web were not changed by this continuation.

The [architecture audit and conditional VMware checklist](../architecture/recovery-snapshot-v1.md#issue-241-firmware-effect-contract-audit-2026-09-27)
record every provider-call category, actual observations, ownership limits and
the required domain-joined WIN11-BASE-derived Fedora/Debian/Mint runs. They separate
VM checkpoint recovery from unimplemented application/storage/payload rollback.
Resolve the code/contract blockers before that destructive acceptance phase;
do not bypass production guards. No machine configuration mutation, VM migration,
commit or push was performed.

Validation: **14 added deterministic cases**, with focused executor tests **44
passed**. Targeted `--no-restore -m:1 -warnaserror` runs passed Preflight **320**,
Core **152**, Community.App **58**. `dotnet build -warnaserror` passed with
**0 warnings, 0 errors**. Full `dotnet test .\Igloo.sln --no-build --no-restore
-m:1` passed **740 tests, 0 failed, 0 skipped**: Core 152, Migration 21, Iso 19,
Community.App 58, Preflight 320, UsbWriter 23, Fleet 147. These fixtures test
explicit-call mapping/rejection, not hidden BCD provider effects. Successful full
resolved-scope integration remains an unmet acceptance test, not a skipped test.
`git diff --check` passed (exit 0); existing LF/CRLF conversion notices were
separate warnings. The pre-existing mixed working tree was preserved.

## Issue #241 — hybrid registration and existing-entry hardening (2026-09-27)

**Still open, not code-complete.** The proposed hybrid already retains the decisive
unsupported calls: preselected-GUID CreateObject and typed setters on the system
store. Removing only fwbootmgr bootsequence or stale deletion cannot prove those
calls firmware-independent. No supported no-synchronization/EFI-index-binding
contract was found. Explicit firmware writes cannot prevent a provider-created
duplicate without that binding. Legacy comments explain the intended BCD
workaround, but do not prove it works or establish equivalence to firmware-only
registration. No such replacement was introduced.

One concrete planner gap was hardened: any **observed** existing EFI entry on the
target partition now rejects planning with
`BootPlanExistingTargetEntryRequiresOwnership`. The planner no longer selects a
partition-matching sibling for BootNext while creating another entry. This applies
to different executable paths and opaque optional data, including observed entries
above 00ff. No reuse/deletion/ownership is inferred. For remaining candidates,
BootNext targets the new preselected index and BootOrder prepends it while keeping
the original sequence unchanged. The obsolete sibling-selection property was
removed. This does not certify uniqueness among unobserved variables or implicit
provider effects; the existing Unsupported declaration remains.

Stale deletion still requires complete empty incoming references and still cannot
proceed on description-based ownership. A leave-stale variant was considered, but
would leave the create/set synchronization and duplicate-identity problems intact;
it was not adopted as an executable workaround. No final RecoveryScopeV1 or full
synthetic production-plan durable success is claimed. #242 classifications remain
WinRE state **Required**, RAM-disk qualification **Required**, and EFI optional-data
dependencies **Still unbounded** under the retained operation/closure.

A fresh process verified Administrator=True/High and used canonical read-only
firmware/BCD acquisition plus provider metadata. It observed BootOrder `[0,2]`,
BootNext Absent/203, Boot0080 Absent/203, firmware attributes 7 on BootOrder and
the two present entries, and 21 BCD objects. The stale scan remains
Unsupported/BcdReferenceSemanticsUnsupported. No setter, registry write, reboot,
destructive operation, commit or push occurred. No #243/Fleet.Web work or changes
to Exact, RecoveryReadiness or PreCommitGate were made.

See the [hybrid-path analysis](../architecture/recovery-snapshot-v1.md#issue-241-hybrid-path-and-existing-entry-follow-up-2026-09-27)
for external-reference/fwbootmgr effects, exact raw firmware hashes and the
leave-stale decision. The existing conditional domain-joined WIN11-BASE clone
checklist remains the required future Fedora/Debian/Mint runtime validation.
Provider-effect binding, a justified ownership/additive design, full scope
derivation and deterministic durable success must precede those destructive runs.

Validation added **7** cases (existing same/different loader, opaque options,
high-index entry, exact BootOrder/BootNext plans, and a newly appearing target
entry). Focused Community boot-registration tests: **78 passed**. Targeted
`--no-restore -m:1 -warnaserror`: Preflight **327**, Core **152**, Community.App
**58**, all passed. `dotnet build -warnaserror`: **0 warnings, 0 errors**.
Full `dotnet test .\Igloo.sln --no-build --no-restore -m:1`: **747 passed,
0 failed, 0 skipped** (152 Core, 21 Migration, 19 Iso, 58 Community.App,
327 Preflight, 23 UsbWriter, 147 Fleet).

## Issue #241 — direct UEFI pivot blocked on dedicated ESP preparation (2026-09-27)

The Community product direction now supersedes the hybrid BCD-managed design:
use an exact planned Boot#### plus one-shot BootNext, without a Windows-side
BootOrder change, BCD/fwbootmgr mutation or stale cleanup. No manual UEFI step is
acceptable. **This is a design decision, not an enabled registration path.**

The required staging layout preserves the Windows ESP and places only boot-critical
EFI payload on a **separate iGloo FAT32 GPT ESP**, at
`\EFI\iGloo\shimx64.efi` and `\EFI\iGloo\grubx64.efi`. OEMDRV remains the installer
payload volume. The user explicitly rejected binding the production one-shot entry
to generic FAT32 OEMDRV or staging iGloo into Microsoft's ESP.

Source inspection found that current preparation cannot satisfy this contract:
it creates a primary/basic-data FAT32 OEMDRV, has no separately owned ESP allocation
or sizing allowance, pins that same payload volume as the boot target, and uses
label-based GRUB/configuration discovery. Distro templates also do not establish
an exact permanent Linux ESP destination with two ESPs present. Therefore
implementation stopped at the requested preparation dependency; no generic-FAT
fallback, speculative partition creation or snapshot-rule exception was added.

The checked-in Fedora, Debian and Mint flows intend to install their final
bootloader during the first installer boot, before the installer's completion
reboot. One-shot selection is consistent with that intended sequence, but the
new separate-ESP loader/configuration chain and final destination still need code
and disposable-VM proof. Early installer failure does not renew BootNext or undo
storage changes. Firmware-menu selection is not the recovery design.

The [dedicated ESP prerequisite and revised VM gate](../architecture/recovery-snapshot-v1.md#issue-241-direct-uefi-pivot-and-dedicated-staging-esp-prerequisite-2026-09-27)
specifies allocation/ownership, canonical disk and partition binding, stable payload
lookup, signed-loader configuration, retry/cleanup boundaries, and per-distro final
boot ownership work. The standard GPT EFI device path stores the partition GUID;
the disk GUID must be independently bound by the canonical plan, not invented as
an extra device-path field.

Only these two recovery documents changed in this checkpoint. Production remains
blocked; the existing hybrid planner/executor code is preserved and not enabled.
No resolved direct scope or durable ExactMatch-to-executor success is claimed.
#242 dependencies have not been globally or operationally excluded. Exact rules,
RecoveryReadiness.Production (`ObservationUnavailable / NotImplemented`) and
PreCommitGate remain unchanged. No host firmware/BCD/RTC/storage mutation, reboot,
commit or push occurred. #243 was not started. **#241 is not code-complete.**

Validation for this documentation-only checkpoint (no new tests): targeted
`dotnet test <project> --no-restore -m:1 -warnaserror` passed Preflight **327**,
Core **152**, and Community.App **58**, each with **0 failed / 0 skipped**.
`dotnet build -warnaserror` succeeded with **0 warnings / 0 errors**.
`dotnet test .\Igloo.sln --no-build --no-restore -m:1` passed **747**, with
**0 failed / 0 skipped**: Core 152, Migration 21, Iso 19, Community.App 58,
Preflight 327, UsbWriter 23, Fleet 147. These results validate the preserved
working tree; they do not validate the unimplemented dedicated ESP design.

## Issue #241 — permanent Linux ESP preparation contracts (2026-09-27)

The new iGloo ESP is now explicitly intended to remain the installed Linux ESP.
There is no planned third ESP and no post-install deletion of this ESP. The Windows
ESP remains preserved and excluded from staging.

Implemented the shared immutable `PreparationPlanV1` / `PreparedLayoutV1` model,
checked space accounting (1 GiB FAT32 ESP, 1 MiB alignment, payload/optional ISO/Linux
allocations), provider-result versus independent-readback identity checks, Windows
ESP alias/preservation checks, explicit ownership/interruption states and boot-file
identity comparison. Community checkpoint persistence reuses the existing durable
artifact store with fresh reopen, SHA-256, structure and generation verification.
The checkpoint carries canonical GUIDs/geometry/roles; labels, drive letters and
disk/partition numbers are not ownership inputs.

**This is not a completed native preparation implementation.** The requested
distro-specific stop condition was reached: Mint's upstream partman EFI path can
choose the first eligible ESP, and the current iGloo recipe cannot bind the new
ESP instead of Windows. Debian still specifies the default. Fedora's current
autopart recipe cannot be combined with an explicit existing-ESP partition command;
its replace-mode whole-disk clearing is also incompatible with preservation.
Supported exact per-installer binding must precede native ESP creation/staging.
No assertion is made that a supported integration is impossible; the missing
integration cannot be replaced by a default-selection assumption.

`DirectInstallService.Prepare` now rejects unsupported dedicated-ESP preparation
**before any preparation side effect**, including shell changes, ISO mounting,
legacy label-based leftover deletion and shrink/create/format/copy. This intentionally
blocks all current direct-install recipes. The old registration planner/executor
and `_preparedBootTarget` remain unchanged and unreachable through a successful new
preparation. No firmware writer, BCD change, RTC change, reboot or registration
enablement was added. Exact/RecoverySnapshotV1/RecoveryReadiness/PreCommitGate remain
unchanged; #243 and Fleet.Web were not touched.

The [preparation contract and supported-source audit](../architecture/recovery-snapshot-v1.md#issue-241-permanent-linux-esp-preparation-contract-2026-09-27)
records what is implemented and what is still missing: native ownership acquisition,
canonical free-extent/shrink selection, signed loader profiles/configuration
discovery, stable filesystem-identity acquisition and duplicate rejection,
downstream stage2/ISO/seed binding, and exact permanent-ESP installer recipes.
Existing label-based GRUB discovery was not relabeled as exact. No actual ISO
pair or developer-host partition was staged, mounted or modified.

Added **55 deterministic cases** (46 Core, 9 Preflight), including space boundaries,
alignment/overflow, canonical GUID mismatches, Windows ESP preservation, interrupted
creation, stale/unreceipted identities, file corruption, durable reopen/generation
checks, and rejection of each of the three current installer paths before side
effects. Successful exact distro binding and real ESP creation tests are still
absent because those implementations are blocked, not silently simulated.

Validation: `dotnet test <project> --no-restore -m:1 -warnaserror` passed Core
**198**, Preflight **336**, Community.App **58**, and Migration **21**, all with
**0 failed / 0 skipped**. `dotnet build -warnaserror`: **0 warnings / 0 errors**.
Full `dotnet test .\Igloo.sln --no-build --no-restore -m:1`: **802 passed,
0 failed, 0 skipped** (198 Core, 21 Migration, 19 Iso, 58 Community.App,
336 Preflight, 23 UsbWriter, 147 Fleet). No separate distro test project exists;
the solution build includes the distro plugins. No destructive VM validation was
performed. **#241 remains open and not code-complete.** No commit or push.

## Issue #241 — shipped installer ESP binding audit (2026-09-27)

Continued the existing preparation work without enabling it. Added
`Igloo.Core/Preparation/InstallerEspBinding.cs` and 31 deterministic Core cases.
The shared binding retains the prepared generation and exact Windows ESP,
Linux ESP and payload identities. Pure runtime translation rejects missing,
duplicate, substituted, overlapping and incomplete evidence, including reversed
enumeration, changed ordinals and another partition with the same filesystem UUID.
FAT32 must be qualified; generic vfat is not enough. A successful translation is
not an installer capability, machine authorization or a native collector result.

The [media/source audit](../architecture/installer-esp-binding.md) records actual
image versions, acquisition limits, hashes, selection/format/bootloader code paths,
payload gaps and investigated alternative strategies:

| Distro | Result |
| --- | --- |
| Fedora 44-1.7 / Anaconda 44.30 | Shipped onpart/noformat semantics support the nominated ESP. The pure generator emits only that verified directive. A complete recipe cannot be derived from the current root reservation: unformatted partition creation/readback needs a proper partition receipt, not invented formatted-volume identity. Existing autopart/whole-disk templates remain unwired to this helper and blocked. |
| Debian trixie hd-media 20250803+deb13u7 / partman-efi 110 | Actual EFI fstab generation selects the first qualifying ESP; default bootdev does not nominate it. No supported exact unattended two-ESP binding was established. An explicit debootstrap deployment is a documented lower-level alternative requiring a new complete integration, not implemented here. |
| Mint 22.3 / Ubiquity 24.04.3+mint19 | Actual first-ESP selection remains unacceptable. Skipping the final bootloader step does not solve partition/mount ownership; signed-GRUB multi-install can include the wrong mounted ESP. A nonzero success-command result is ignored before completion/reboot, so a proposed post-install loader hook is not a safe failure boundary. Requires redesigned installation strategy or a proven supported selector. |

No-format support in individual hooks does not prove the intended ESP is selected
or that Windows is preserved throughout installation. All three paths remain
blocked. Native Linux inventory acquisition, strong physical-identity translation,
complete storage recipes, stable payload/seed lookup, signed loader profiles and
interruption/recovery semantics remain requirements before native preparation.
No distro template, preparation support gate, registration writer, RecoverySnapshot
Exact rule, RecoveryReadiness or PreCommitGate was changed by this continuation.
Existing unrelated dirty changes, including Fleet.Web work, were preserved.

Validation in this continuation:

- `dotnet test tests/Igloo.Core.Tests/Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror`: **229 passed, 0 failed, 0 skipped**.
- Same targeted command for Preflight: **336 passed, 0 failed, 0 skipped**; Community.App: **58 passed, 0 failed, 0 skipped**; Migration: **21 passed, 0 failed, 0 skipped**.
- Core distro-specific filter `FullyQualifiedName~FedoraEspDirective|FullyQualifiedName~ValidTwoEspIdentity`: **5 passed, 0 failed, 0 skipped**, included in the 229 Core total. No separate distro test project exists.
- `dotnet build -warnaserror`: **0 warnings, 0 errors**; all distro projects build.
- `dotnet test .\Igloo.sln --no-build --no-restore -m:1`: **833 passed, 0 failed, 0 skipped** (Core 229, Migration 21, Iso 19, Community.App 58, Preflight 336, UsbWriter 23, Fleet 147).
- `git diff --check`: passed. Existing LF-to-CRLF conversion notices are separate from whitespace failures.

The initial Core run passed its 224 then-current tests but exited nonzero on
CA1308 in a test's casing expression. The test was corrected without suppression;
the final 229-test run and solution build above are clean. No destructive runtime
validation, native partition creation, firmware/BCD/RTC write or reboot occurred.
**#241 remains open and not code-complete.** No commit or push.

## Issue #241 — complete installation ownership continuation (2026-09-27)

Continued the existing prepared-layout and actual-media audit. The
[implementation and complete distro lifecycle findings](../architecture/community-installation-ownership.md)
record the current result. Production remains blocked before its first storage
side effect; there is no native create/format/staging, firmware/BCD/RTC write,
reboot or #243 work.

Implemented:

- Partition-only creation/readback receipts for the owned Linux ESP, unformatted
  root, payload and optional ISO, plus the preserved GPT partition set. These do
  not fabricate filesystem/volume identity for a raw root. Durable checkpoints
  distinguish partial creation receipts from complete installation ownership.
- Shared `InstallationOwnershipV1` resolution against exact generation, GUIDs,
  types, geometry, filesystem identities and parent disk. The read-only Linux
  collector/protocol supplies typed observations from two matching independent
  passes. Native packaging, mount transitions and strong physical-ID equivalence
  still need qualification; this is not installation authorization.
- The complete Fedora storage-section generator: pre-created root by PARTUUID,
  exact existing ESP by UUID with no format, no autopart, no new partition allocator
  and no unsafe clearpart. It removes the entire old storage section and retains
  Wi-Fi setup. The actual extracted F44 pykickstart parser accepted the section.
  Production renderer integration and safe post-install payload behavior are not
  enabled by this parser result.
- Pure generation/volume/partition/path/content payload verification and Fedora
  stage2/kickstart UUID transport generation, supported by the shipped initrd.
  Early-boot verification, native mounted-file readback and remaining agent/seed
  label scans are still unresolved production integration work.

| Path | Current result |
| --- | --- |
| Fedora KDE 44-1.7 | Complete pre-created root/ESP storage sub-contract implemented and fixture-tested. Full path remains **Blocked** on runtime/mount/payload integration, signed loader profiles and VM proof. |
| Debian Trixie GNOME | The current partman path remains unacceptable. Actual Trixie bootstrap/GRUB/shim package scripts were inspected without execution. A complete offline GNOME deployment, explicit mount/loader finalization, package-hook/MOK effects and durable failure/completion lifecycle are not implemented. **Blocked**, no accepted replacement yet. |
| Mint 22.3 Cinnamon | OS-only Ubiquity does not remove ambiguous partition/mount selection or signed-hook effects; its success hook is not a failure barrier. Rootfs or lower-level alternatives have no established complete Mint deployment contract. **Blocked**, requires a supported redesigned strategy. |

No common installer engine or capability boolean substitutes for these missing
contracts. The Windows ESP remains a preserved, distinct identity; no claim is
made that a complete installation has yet proven it untouched. All three complete
production paths remain blocked. RecoverySnapshotV1/Exact, RecoveryReadiness and
PreCommitGate are unchanged; unrelated dirty-tree changes are preserved.

Added **48 deterministic .NET cases** (36 Core, 2 Preflight, 10 Migration/Fedora)
and **12 Python collector cases** using mocked command output only. Coverage
includes raw root ownership, partial/durable receipts, Windows/root/ESP alias
rejection, two ESPs, reversed enumeration, duplicates, changed identities/geometry,
typed observation failures, full Fedora storage generation and payload integrity.
Unsupported Debian/Mint engines are rejected, not simulated as successful installs.

Validation for this continuation:

- `dotnet test tests/Igloo.Core.Tests/Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror`: **265 passed, 0 failed, 0 skipped**.
- Same targeted command for Preflight: **338 passed, 0 failed, 0 skipped**;
  Community.App: **58 passed, 0 failed, 0 skipped**; Migration: **31 passed,
  0 failed, 0 skipped**. Distro-specific deterministic cases are included in
  Core and Migration; no separate distro test project exists.
- `python -B -m unittest discover -s tests/installer -p 'test_*.py'`:
  **12 passed, 0 failed, 0 skipped** (separate from .NET totals).
- `dotnet build -warnaserror`: **0 warnings, 0 errors**.
- `dotnet test .\Igloo.sln --no-build --no-restore -m:1`: **881 passed,
  0 failed, 0 skipped** (265 Core, 31 Migration, 19 Iso, 58 Community.App,
  338 Preflight, 23 UsbWriter, 147 Fleet).
- `git diff --check`: **passed (exit 0)**. Existing LF/CRLF conversion notices
  are separate from whitespace errors.

No package maintainer script, installer, native inventory probe against developer
disks or destructive VM test was executed. The read-only media/package extraction
and Fedora parser check do not establish bootability. **#241 remains open and not
code-complete.** No commit or push.

### Community #241 — Debian-only deployment protocol continuation (2026-09-27)

Final status: **DEBIAN DEPLOYMENT ARCHITECTURE BLOCKED**. Debootstrap remains a
candidate, not an accepted production replacement. See the complete
[Debian deployment contract](../architecture/debian-target-root-deployment.md)
and [artifact evidence](../architecture/debian-deployment-evidence.json).

Added Debian-specific immutable source/product/configuration declarations,
43 explicit deployment stages, ownership and mount revalidation before every
operation (repeated after durable intent reopen), separate performer/observer
interfaces, and a generation-exclusive journal contract. Declared helpers and
partial mount/teardown states extend the existing shared mount verifier without
introducing another storage observation model. The candidate namespace denies
preserved/raw-disk/firmware writes, host service access and deployment networking;
effective native enforcement and observation still have no implementation.

The receipt producer maps detailed Debian outcomes onto the shared installation
receipt, retaining package/source policy, root/ESP/generation, file identities,
firmware witness and failure evidence. Independent reopen verifies bytes,
structure, generation and plan identity. Exceptions or unavailable readback after
entry remain OutcomeUnknown; nonzero commands with observable partial state stay
Failed. No blind retry or automatic cleanup/rollback is introduced. Synthetic
EvidenceComplete is not production support, bootability or reboot authorization.

Actual Trixie package inspection confirms GRUB/shim hooks may reinstall files and
some GRUB failures do not fail dpkg. Shim policy can invoke mokutil despite GRUB's
no-nvram setting. Additional read-only downloads/extraction inspected efibootmgr
18-2, mokutil 0.7.2-1 and tasksel 3.81; no maintainer script ran. Package SHA-256
matched the downloaded index, but this research did not independently verify its
InRelease signature. It is not a production authenticated offline bundle.

The agent remains a blocker: legacy behavior includes broad seed/OS discovery,
boot-order/cleanup changes and success exit after failed steps. The new contract
requires a separate content-bound deployment service/profile, not that launcher.
Debian's legacy preseed export now throws Unsupported instead of falling back to
biggest_free/atomic/default ESP selection. The template remains audit material.

Remaining implementation: authenticated complete offline GNOME/hardware package
closure; native isolated runtime/observers/operation adapters; target configuration
and signed-chain qualification; safe agent profile; complete native NVRAM inventory,
immutable permanent finalization intent and journaled writer; Linux durable sink.
Only after those exist can the documented two-ESP Secure Boot disposable-VM
installation, fault-injection, reboot and Windows-preservation matrix qualify the
architecture. This is not a claim that only VM testing remains.

Added **161 Debian deterministic cases**. Final validation:

- `dotnet test tests/Igloo.Core.Tests/Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror`: **330 passed / 0 failed / 0 skipped**.
- Same command for Preflight: **343 / 0 / 0**; Community.App: **58 / 0 / 0**;
  Migration: **192 / 0 / 0** (31 existing cases plus 161 Debian cases).
- Debian-specific Migration filter `--filter FullyQualifiedName~DebianDeploymentTests`
  with `--no-restore -m:1 -warnaserror`: **161 / 0 / 0**.
- `dotnet build -warnaserror`: **0 warnings, 0 errors**.
- `dotnet test .\Igloo.sln --no-build --no-restore -m:1`: **1,112 passed / 0 failed /
  0 skipped** (330 Core, 192 Migration, 19 Iso, 58 Community.App, 343 Preflight,
  23 UsbWriter, 147 Fleet).
- `git diff --check`: **exit 0**. LF/CRLF conversion notices are separate from
  whitespace errors. Initial analyzer/test findings were corrected before these
  final successful runs; no suppression was added.

No native preparation, storage/ESP/firmware/BCD/RTC mutation, reboot or destructive
VM validation occurred. No Fedora/Mint redesign, Fleet.Web edit or #243 work was
performed. RecoverySnapshotV1/Exact, RecoveryReadiness and PreCommitGate are
unchanged. All unrelated existing working-tree edits were preserved. No commit
or push. **#241 remains open.**

## Issue #241 — target-root deployment lifecycle and receipts (2026-09-27)

Continued the current working tree and froze Fedora's explicit storage generator.
The [complete target-root design](../architecture/target-root-deployment.md) now
specifies all 24 Debian candidate stages with inputs, effects, readback, failure
and retry limits. It compares Mint image/bootstrap/other primitive strategies
against the actual 22.3 release. Neither replacement engine is accepted or enabled.

Implemented shared evidence boundaries:

- An observed EXT4 root-format receipt and formatted-root resolver that preserve
  the existing fresh-root rejection rule and exact owned GPT identity.
- Pure kernel mountinfo parsing, device-number correlation, exact core root/ESP/
  read-only source mount verification, Windows ESP exclusion, substitution/bind/
  propagation rejection, and stable UUID fstab generation. Native mount/stat/path
  acquisition and restricted execution are still missing.
- Continued canonical payload verification after the verified root-format
  transition, without labels or default media discovery.
- Separate `InstallationReceiptV1`, typed stage outcomes, independent final
  layout/file/firmware evidence comparison and Community durable persist/reopen.
  `EvidenceComplete` does not mean supported installation, bootability, recovery,
  bounded package effects or reboot authorization. No native receipt producer,
  Linux durable sink, distro engine, rollback or reboot implementation was added.

New read-only media/package findings include:

- Mint's actual manifest-remove includes signed GRUB and shim. Ubiquity's EFI and
  dependency-aware keep rules mean a raw copy/remove recipe is unsafe. Kernel/
  hardware/initramfs, accounts, locale, repository pins, Mint adjustments and live
  state also require full installed-system finalization.
- Mint shim postinst's latest/previous signed-loader choice depends on Secure Boot
  and revocation observations. Hiding all EFI evidence is not a neutral way to
  prevent writes. grub-multi-install can mount its selected ESPs itself; an
  unmounted Windows ESP alone is not isolation.
- Debian's signed-policy triggers, tolerated grub-install errors, kernel hooks
  and os-prober behavior require bounded package execution plus explicit verified
  finalization. The documented bootstrap primitive does not supply the current
  offline GNOME package closure or a complete deployment/recovery lifecycle.

Fifteen additional extracted-source hashes were retained in the evidence manifest.
No package maintainer script was executed. Public chroot/mount namespace contracts
were reviewed; neither alone prevents raw-device/firmware access. No guessed
isolation capability or distro-support flag was added.

All complete architectures remain **Blocked**. Fedora's supported storage
sub-contract is unchanged; Debian debootstrap and Mint live-image deployment remain
research candidates. Native preparation is not the next enabled task. The exact
remaining work is release-qualified deployment/finalization and effective mount/
device/firmware isolation, authenticated payload/package transport, runtime
integration, pinned loader profiles and disposable-VM evidence.

Added **70 deterministic .NET cases** (65 Core and 5 Preflight). Tests exercise
formatted-root identity, same-disk ESP exclusion, mount order/identity/substitution,
payload changes, synthetic Debian/Mint evidence, stage failure/uncertain outcomes,
missing content, exact loader destination, opaque firmware evidence, and durable
partial/complete-evidence reopen. They do not simulate a successful real apt,
debootstrap, Mint cleanup or bootloader transaction. Those engines remain absent.

Validation:

- `dotnet test tests/Igloo.Core.Tests/Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror`: **330 passed, 0 failed, 0 skipped**.
- Same targeted command for Preflight: **343 passed, 0 failed, 0 skipped**;
  Community.App: **58 passed, 0 failed, 0 skipped**; Migration: **31 passed,
  0 failed, 0 skipped**. Core/Migration include the distro-specific cases.
- `python -B -m unittest discover -s tests/installer -p 'test_*.py'`: **12 passed,
  0 failed, 0 skipped**, separate from .NET totals.
- `dotnet build -warnaserror`: **0 warnings, 0 errors**.
- Full `dotnet test .\Igloo.sln --no-build --no-restore -m:1`: **951 passed,
  0 failed, 0 skipped** (330 Core, 31 Migration, 19 Iso, 58 Community.App,
  343 Preflight, 23 UsbWriter, 147 Fleet).
- `git diff --check`: **passed (exit 0)**; LF/CRLF conversion notices are not
  whitespace failures. Initial analyzer findings were corrected without suppression
  before these final successful runs.

No native partition/storage/ESP/firmware/BCD/RTC mutation, reboot or destructive
VM validation occurred. Production preparation, RecoverySnapshotV1 Exact,
RecoveryReadiness, and PreCommitGate are unchanged. No #243 or Fleet.Web change;
unrelated working-tree edits were preserved. **#241 remains open and not
code-complete.** No commit or push.


## Community #241 Debian content continuation — 2026-09-28

Status: **DEBIAN CONTENT DEPLOYMENT FOUNDATION BLOCKED**. Continued the existing
43-stage candidate without enabling preparation, registration, deployment, #243,
RecoveryReadiness or PreCommitGate. Fedora/Mint and Fleet.Web source were not changed.
The detailed contract and remaining gaps are in
[Debian target-root deployment](../architecture/debian-target-root-deployment.md#debian-content-continuation--2026-09-28).

Implemented in this continuation:

- Generation/build-bound OfflineDebianPackageSetV1, deterministic workstation policy,
  archive/index/package filename/version/hash retention and source binding.
- Read/download-only Linux bundle build/verify tool: externally pinned keyring and
  signing fingerprints, gpgv authentication, signed metadata freshness/identity,
  index and archive hashes, isolated APT dependency solution and independent rerun.
  Recommends are explicit; unresolved recommendations reject the solution. Separate
  debootstrap minbase archives and final security/updates archives are retained.
- Typed native stage dispatcher into an isolated-host interface, exact configuration
  operations, secret-stdin contract, package commands, kernel/modules/initramfs
  evidence, secret-free command results and finalizer input. Unsupported operations
  remain unsupported; there is no host command fallback or firmware writer.
- Dedicated additive agent installation profile and explicit first-boot-pending state.
  The replacement migration worker is still unqualified, so native agent installation
  is refused. Debian plugin configuration, boot-spec and agent export APIs cannot
  fall back to the old preseed, hd-media or broad agent installer.
- Linux durable journal: owner-only/no-symlink directory traversal, exclusive generation,
  create-new files, file/directory fsync, hash-bearing references and fresh-process
  readback. Corrupt history blocks append. No cleanup, overwrite, retry or rollback.
- PreBootStagesVerified checkpoint remains incomplete. The finalizer handoff retains
  unsupported hook/unavailable firmware evidence and grants no execution capability.

Remaining non-firmware blockers are explicit: no complete real authenticated
GNOME/hardware bundle rehearsal or qualified complete runtime toolchain/source observer;
no effective native namespace/device/firmware isolation broker and independent observer;
remaining native file/keyring/credential/configuration-cleanup implementations; no
qualified exact first-boot migration worker/agent receipt integration. Signed loader
qualification, permanent NVRAM journal/finalization and disposable-VM evidence also
remain. **It is not true that only firmware work remains. #241 stays open.**

Validation (all tests deterministic; no deployment or developer boot/storage mutation):

- Core `--no-restore -m:1 -warnaserror`: **330 passed, 0 failed, 0 skipped**.
- Preflight same flags: **343 passed, 0 failed, 0 skipped**.
- Community.App same flags: **58 passed, 0 failed, 0 skipped**.
- Migration same flags: **235 passed, 0 failed, 0 skipped**, including **204 Debian**
  cases. **43 .NET cases added** over the 1,112-test prior baseline.
- Debian-filtered Migration tests with the same flags: **204 passed, 0 failed,
  0 skipped** (subset, not additional tests).
- Linux `python3 -B -m unittest discover -s tests/installer -p 'test_*.py'`:
  **34 passed, 0 failed, 0 skipped**: 12 existing inventory and 22 new Debian tests.
  New tests use real gpgv/APT with an ephemeral signing key/synthetic repositories,
  and temporary Linux journal files with independent process readback. WSL APT 2.8.3
  is not qualification of a full Trixie APT 3.0.3 installation. No actual package
  install, maintainer-script execution, mount or firmware action occurred.
- `dotnet build -warnaserror`: **0 warnings, 0 errors**.
- Full `dotnet test Igloo.sln --no-build --no-restore -m:1`: **1,155 passed,
  0 failed, 0 skipped** (330 Core, 235 Migration, 19 Iso, 58 Community.App,
  343 Preflight, 23 UsbWriter, 147 Fleet).
- `git diff --check`: **exit 0**; existing LF/CRLF conversion notices are separate
  from whitespace errors. Initial compile/test issues were corrected before these
  successful final runs; no analyzer suppression was added.

No real Trixie workstation bundle or disposable VM installation was claimed.
Unrelated working-tree changes were preserved. No commit or push.

## Community #241 — Debian non-firmware continuation (2026-09-28)

**DEBIAN NON-FIRMWARE FOUNDATION BLOCKED.** The current Debian engine was extended,
not restarted. No Fleet #243, Fleet.Web, RecoverySnapshotV1/Exact,
RecoveryReadiness, PreCommitGate, Windows preparation, registration or permanent
firmware change was made by this continuation. Existing unrelated work remains.

The [Debian architecture checkpoint](../architecture/debian-target-root-deployment.md#debian-non-firmware-continuation--2026-09-28)
and [real acquisition evidence](../architecture/debian-nonfirmware-evidence.json)
record the result:

- Real Trixie/updates/security signed metadata, allowed-key gpgv verification and
  12 amd64 component indexes were inspected. The actual debootstrap 1.0.141
  **78-package minbase** archive set was downloaded/hash-verified and independently
  reread in another process.
- The workstation solver selected **1,594 packages but rejected the closure**:
  `gvfs-backends 1.57.2-2+deb13u1 -> Recommends: wsdd` has no candidate in those signed
  repositories. No recommendation was omitted and **no complete GNOME bundle or
  success manifest exists**. The attempted build is
  `2a35782f-a4f6-4bf2-9ddb-76e577147473`; hashes and package identities are retained.
- Fixed the real bootstrap transport mismatch: print/download now both use the
  same captured file metadata, rather than letting HTTPS change the package set.
  Added pinned runtime/helper-tree identity verification and durable profile hash.
  The observed Ubuntu/WSL build tools do not qualify a complete Trixie runtime.
- Implemented FD-relative/no-symlink/no-cross-mount configuration writes with exact
  before-state, atomic replacement, ownership/modes, fsync and fresh readback;
  sealed-memfd single-use credential stdin; and read-only dpkg, artifact and mount
  acquisition primitives. They do not replace the missing privileged broker.
- Implemented a restricted, hash-pinned first-boot **evidence consumer** and receipt
  states: pending/succeeded/failed/unknown, including systemd invocation/exit evidence.
  It cannot perform privileged user-data import, enrollment, boot cleanup or broad
  discovery. Those required producer/handoff implementations still block migration
  worker qualification and production InstallAgent dispatch.
- Changed declared machine identity to an empty machine-id file using documented
  systemd image semantics, and completed final temporary-source/policy cleanup
  declarations. Native application still needs the isolated host.

The full isolation/mount broker remains unimplemented. Actual debootstrap device
creation and mount fallbacks need a qualified supervisor/package-process split;
no plain chroot, full host `/dev`, container-detection override or weakened device
policy was accepted. There was **no real target-root rehearsal**, native partition
creation, package installation, firmware/BCD/RTC write or reboot. All 43 stages
remain guarded; no new end-to-end native stage is certified from primitive tests.

Remaining non-firmware blockers: complete authenticated GNOME policy/bundle and
runtime/source observer; exact native mount/isolation and full observer/credential
integration; privileged user-data completion and first-boot producer qualification.
Signed loader/package hooks, permanent firmware finalization and disposable VMware
evidence remain separate. It is **not** true that only firmware work remains.

Validation:

- Core **330**, Preflight **343**, Community.App **58**, Migration **257** passed
  using `--no-restore -m:1 -warnaserror`; **226 Debian** tests are a Migration subset.
- Linux/native fixtures: **66 passed**. Real signature/minbase/closure observations
  are recorded separately, not added to the deterministic test count.
- Full serialized solution: **1,177 passed, 0 failed, 0 skipped**.
- `dotnet build -warnaserror`: **0 warnings, 0 errors**.
- `git diff --check`: passes; preexisting LF/CRLF notices remain separate.
- **22 .NET and 32 Linux tests added.** No suppressions, commit or push.

## Community #241 — Debian isolation continuation (2026-09-28)

**DEBIAN ISOLATION FOUNDATION BLOCKED.** See the new
[native execution-boundary record](../architecture/debian-isolation-boundary.md)
for the threat model, enforcement, actual observations and remaining integration.
This continuation is Debian-only. Fleet #243, Fleet.Web, RecoverySnapshotV1/Exact,
RecoveryReadiness, PreCommitGate, Windows preparation and registration are unchanged.

Implemented a real candidate package/configuration child using authenticated
Trixie bubblewrap 0.12.0, a static trusted setup/gate, sealed libseccomp policy,
exact capability bounds and fresh external kernel readback before and after intent.
Private mount/PID/network/IPC/UTS views, minimal devices, nodev/nosuid, read-only
ESP/source, empty read-only sysfs, closed setup FDs and no host service sockets are
checked before release. No bootstrap or firmware profile is offered.

Added native FD-anchored filesystem mount mechanics and exact reverse unmount,
with intent/readback/delta checks and no force/lazy success. Actual tmpfs fixtures
exercise attachment, teardown and failure cases. GPT/FAT32 block-device mounts and
the shared canonical lease/session/journal composition remain unqualified; there
is still no complete production IDebianIsolatedStageHost and no newly certified
end-to-end stage. The documented debootstrap setup/maintainer-script privilege
split remains unresolved. The GNOME/wsdd and first-boot blockers are unchanged.

Readback initially caught broad capability bounds and imported propagation links;
trusted setup was corrected without weakening verification. A separate rejected
detached-FD experiment selected the wrong WSL source view. No target command ran,
but setup created an empty `/boot/efi` directory and zero-length `/usr/bin/probe`
in WSL. Both exact placeholders were inspected, individually removed and verified
absent. That source path was removed. No Windows ESP, physical partition, BCD,
firmware or RTC mutation occurred, and no reboot or real deployment was performed.

Validation: Core **330**, Preflight **343**, Community.App **58**, Migration **275**
passed with `--no-restore -m:1 -warnaserror`; Debian subset **244**. Linux ordinary
fixtures **109**, explicit native broker/tmpfs fixtures **36**. Full serialized
solution **1,195 passed, 0 failed, 0 skipped**. Build **0 warnings, 0 errors**.
`git diff --check` exits **0**; existing LF/CRLF notices are separate. Added **18
.NET, 43 Linux policy/primitive and 36 explicit native tests**. No suppressions,
package upgrades, commit or push. Unrelated working-tree changes were preserved.

## Community #241 — Debian canonical session continuation (2026-09-28)

**DEBIAN SESSION FOUNDATION BLOCKED.** See the
[canonical mount-session record](../architecture/debian-session-boundary.md) for
the exact implementation, runtime evidence and all 43 stage classifications.

Implemented shared canonical descriptor-acquisition declarations and a single-use
Debian session authority, persistent native supervisor transport, hash/protection
checks before tool execution, fresh resolver callbacks, and fsync/reopen session
journaling before effects. Exact mount/unmount primitives now receive canonical
witnesses through the private .NET pipe. Detached or path-expanding resource FDs
are rejected before bubblewrap, including a real kernel-clone negative test.

Production remains disabled. No complete IDebianIsolatedStageHost, root-only
package view, supported debootstrap privilege transition, real GPT mount
qualification or complete semantic observer exists yet. Read-only WSL inventory
returned `Unavailable / sfdiskExit1`; no fabricated witness or host block-device
rehearsal followed. Only disposable directory/private-tmpfs fixtures ran. Zero
stages are newly NativeSupported. The GNOME/wsdd and first-boot producer blockers
are retained; signed-loader/NVRAM and VM validation remain additional work.

Validation: Core **353**, Preflight **343**, Community.App **58**, Migration **293**,
Debian subset **262**, Linux deterministic **132**, explicit native **40** passed.
Full serialized solution **1,236 passed, 0 failed, 0 skipped**; solution build
**0 warnings, 0 errors**. Added **41 .NET, 23 Linux deterministic and 4 explicit
native tests**. Temporary fixture cleanup was independently checked. No physical
disk, Windows ESP, firmware, BCD or RTC writes, reboot, commit or push. No Fleet
#243/Fleet.Web work or change to RecoverySnapshotV1/Exact, RecoveryReadiness or
PreCommitGate. Unrelated working-tree changes are preserved.
`git diff --check` exits **0**; 28 existing LF/CRLF notices are separate. New/edited
untracked files also have no trailing whitespace.

## Community #241 — Debian bootstrap primitive decision (2026-09-28)

**BLOCKED: no complete replacement bootstrap is qualified.** The
[bootstrap decision and evidence](../architecture/debian-bootstrap-primitive.md)
compare actual authenticated Trixie debootstrap 1.0.141, mmdebstrap
1.5.7-1+deb13u1, cdebootstrap 0.7.9+b13, live-build and debuerreotype, plus
authenticated base-image deployment as an alternative architecture.

Stock debootstrap is no longer emitted as a candidate native command. Its setup
requirements conflict with the existing child restrictions. mmdebstrap has public
mount-skip controls, but its configured root/unshare modes still use chroot;
extract-only output is not a configured Debian system. Chrootless compatibility
is not proven for the workstation. An authenticated configured-root artifact
could remove target bootstrap setup, but its provenance, safe importer, complete
package state and restricted follow-on lifecycle remain unimplemented/unqualified.
No replacement or new schema was invented to claim support.

The isolation/session model and all 43 stage classifications remain unchanged:
**zero new NativeSupported stages**. Seven new .NET regression cases prevent
candidate dispatch or bootstrap replacement through existing execution profiles.
Four native tests verify chroot/character-device creation denial in both package
and configuration children. Existing raw-device, firmware, network, mount and
service-socket restrictions were retained. No real bootstrap/install ran.

Validation: Core **353**, Preflight **343**, Community.App **58**, Migration **300**,
Debian subset **269**, Linux deterministic **132**, explicit native **44** passed.
Full serialized solution **1,243 passed, 0 failed, 0 skipped**; solution build
**0 warnings, 0 errors**. Temporary fixture cleanup was independently verified.
The GNOME package closure, complete session/observer integration, credential and
first-boot producers, signed-loader/NVRAM qualification and disposable VM evidence
remain blockers. Native preparation, registration and Fleet #243 remain disabled.
No RecoverySnapshotV1/Exact, RecoveryReadiness or PreCommitGate changes. No Mint,
Fedora, Fleet.Web, commit or push; unrelated work was preserved.
`git diff --check`: **exit 0**, with 28 existing LF/CRLF notices kept separate.
Edited/new untracked files also pass the trailing-whitespace check.

## Community #241 — Debian configured-root artifact candidate (2026-09-28)

**DEBIAN CONFIGURED-ROOT ARTIFACT ARCHITECTURE BLOCKED.**
The [artifact lifecycle and responsibility matrix](../architecture/debian-configured-root-artifact.md)
continue the selected configured-filesystem direction. This is Debian-only work;
Fleet #243, RecoveryReadiness, PreCommitGate, RecoverySnapshotV1/Exact, native Windows
preparation and Community firmware registration are unchanged.

Added an immutable configured-root descriptor/build-attestation contract, strict
development-pin reopen, a separate candidate 43-stage responsibility map, and a
production session/lease import gate that remains Unsupported. No production
signing authority was found or invented. A separately isolated mmdebstrap factory
VM is the selected build candidate; no on-target bootstrap is restored.

The semantic stream/manifest importer uses FD-relative openat2 traversal, create-new
writes, explicit link/owner/mode/xattr policy, root/mount witnesses, durable reopened
intent and independent filesystem/dpkg readback. It has no production path-based
entry point. Real ordinary-directory fixtures exercise packing/import, corruption,
escapes, special files, partial/unknown outcomes and fresh-process readback. An
explicit root-only fixture also restored/read back the narrowly declared ping file
capability on inert data. This is not an actual configured Debian build, GPT session
or supported production import. The existing isolation broker is unchanged.

Fresh signed trixie/updates/security metadata was independently reopened and the
APT solve repeated. **1,594 selected candidates still fail** the unchanged Recommends
policy: `gvfs-backends 1.57.2-2+deb13u1 -> wsdd`, with zero wsdd candidates. No full
workstation artifact was produced. Source/closure evidence is retained with the
architecture. No wsdd2 substitution, release mixing or policy relaxation occurred.

New NativeSupported stages: **0**. Remaining non-firmware work includes the complete
authenticated closure, qualified factory/neutralization/metadata, release signing,
canonical root-only import session, target configuration/initramfs and exact
first-boot producers. Signed-loader/NVRAM and disposable VM validation also remain.

Validation: Core **353**, Preflight **343**, Community.App **58**, Migration **326**;
Debian subset **295** (including **26** new .NET cases). Linux discovery **189**
(including **57** artifact tests), explicit native broker **44**, and explicit
file-capability fixture **1**. All passed, with **0 failed / 0 skipped**.
Full serialized solution: **1,269 passed / 0 failed / 0 skipped**. Build with
`-warnaserror`: **0 warnings / 0 errors**. `git diff --check`: **exit 0**;
28 existing LF/CRLF notices are separate from whitespace failures. New/untracked
files were also checked directly. No physical disk, firmware, BCD or RTC mutation,
reboot, package upgrade, commit or push; unrelated working-tree changes preserved.

## Community #241 — real Debian configured-root factory (2026-09-28)

**DEBIAN CONFIGURED-ROOT FACTORY BLOCKED.** The [current factory report](../architecture/debian-configured-root-artifact.md)
and [observed evidence](../architecture/debian-configured-root-factory-evidence.json)
supersede the earlier unresolved-closure checkpoint, without changing the target
isolation/session, Exact, RecoveryReadiness or PreCommitGate contracts.

Fresh signed Trixie/updates/security inputs still lack wsdd. The explicitly reviewed
`trixie-gvfs-wsdd-2026-09-28-v1` exception covers only the exact unavailable
recommendation of gvfs-backends 1.57.2-2+deb13u1, expires on 2026-10-28, and records
loss of automatic WS-Discovery browsing. Recommends remains enabled; no wsdd2 or
other-release package is substituted. Every other Depends/Recommends resolves.

The complete 1,594-package solution, 78 minbase archives and 110 alternate signed
repository paths were downloaded and independently verified. APT independently
repeated the solution in both the host verifier and Trixie guest. Unmodified
mmdebstrap 1.5.7-1+deb13u1 built the real GNOME root offline in a newly installed,
BIOS-only QEMU/KVM factory VM with a fresh virtual disk and read-only input ISO,
no NIC, passthrough or shared folders. No migration/developer disk was exposed.

Fresh dpkg-query and dpkg --audit report all 1,594 exact versions configured, no
missing/extra/unconfigured packages and no pending/awaited triggers. The raw-tree
observer found legitimate Unicode/escaped systemd paths, a GStreamer capability,
journal ACLs, hardlinks and set-ID entries. Narrow metadata schema 2 support and
adversarial/native fixtures were added; the byte-stream format remains v1.

Publication correctly stops on factory TLS private key, Exim/debconf hostname,
hostname/mailname, temporary source/log and runtime device residue. The read-only
gate leaves the root unchanged. No neutralized manifest/stream, complete artifact
attestation, production authentication or real-root import is claimed. The next
step is an exact reviewed neutralization and target identity-regeneration lifecycle,
then full real-artifact verification/import. Signing, production session integration,
initramfs/target configuration and first-boot producers remain separate blockers.

NativeSupported stages remain **0**. Windows preparation/registration and firmware
finalization remain disabled. No Mint, Fedora, Fleet.Web or #243 implementation,
no developer physical disk/ESP/firmware/BCD/RTC mutation, no host reboot, commit or push.

Validation: Core **353**, Preflight **343**, Community.App **58**, Migration **351**;
Debian subset **320**, configured-root .NET subset **26**. Full serialized solution
**1,294 passed / 0 failed / 0 skipped**. Linux discovery **236**, native broker **44**,
native artifact capability/ACL fixtures **2**, all **0 failed / 0 skipped**.

## Community #241 — Debian neutral artifact and real import (2026-09-28)

**DEBIAN CONFIGURED-ROOT ARTIFACT QUALIFIED**, for development artifact/import
mechanics only. The [neutralization report](../architecture/debian-configured-root-neutralization.md)
supersedes the raw-root publication blocker above without rebuilding the factory.
The original disk still has SHA-256
`70DED1CA5E7211C99D4C4706D809B600CD7F97D3B07028BF463363862152C861`.
It was a read-only backing file for a separate disposable VM overlay. Neither the
raw root nor its forensic observations were overwritten.

Plan `debian-trixie-neutralization-2026-09-28-v2` transforms a fresh derivation,
with exact object before/after states, reopened intent, public debconf operations,
35 fresh package observations and an exact whole-tree delta. The initial candidate
and import remain superseded evidence after discovery of `99mmdebstrap`; the revised
candidate removes that exact build-policy object and was imported separately.
All 1,594 exact packages remain configured, with clean dpkg audit/no pending triggers.
Factory hostname/mailname, Exim/debconf identity, TLS key/certificate, sources,
runtime nodes/log residue and provisional initramfs are excluded or neutralized.
Two real helper instances produced different TLS/Exim identities; production target
helper qualification remains outstanding.

The real schema-2 manifest has **144,911 entries**. The regular-byte stream is
**4,641,457,180 bytes**, SHA-256
`EDE9ADCC24597B82389CE7C73D38A02A3AB077DA53C132EE9704FEC71BC705F5`.
Strict .NET reopen and separately staged fresh-process verification pass under an
out-of-band development pin. A newly created file-backed EXT4 filesystem inside
the VM imports it with durable intent/progress/result. Fresh full metadata/content
and dpkg readback exactly match; ordinary unmount and exact loop detach verify.
No developer physical partition/ESP, firmware, BCD or RTC was modified.

ProductionAuthentication and `ImportSupport(session, leases)` remain **Unsupported**;
NativeSupported stages remain **0**. The v1 43-stage history is unchanged.
ImportConfiguredRoot is a strategy/import-mechanics concept, not a successful
Bootstrap stage. Remaining work is release authority/eligibility, canonical session
and GPT/mount/journal integration, target machine/initramfs/agent/UserData/Enrollment
finalization, followed by signed boot/NVRAM and VMware migration/recovery validation.
Windows preparation and Community registration stay disabled. RecoverySnapshotV1,
Exact, RecoveryReadiness and PreCommitGate are unchanged. No Mint, Fedora redesign,
Fleet #243 or Fleet.Web work is included.

Validation: Core **353**, Preflight **343**, Community.App **58**, Migration **360**;
Debian filter **329**, configured-root .NET filter **35** (overlapping subsets);
full serialized solution **1,303**; Linux discovery **253**; explicit broker **44**;
capability/ACL **2**; real-metadata failures **18**; complete-source corruption
checks **3**. Final runs have **0 failed / 0 skipped**. Real TLS/Exim regeneration
checks cover two independent instances. Build has **0 warnings / 0 errors**.
`git diff --check` succeeds; LF/CRLF notices are recorded separately.
Build with `-warnaserror`: **0 warnings, 0 errors**. `git diff --check`: **exit 0**;
28 existing LF/CRLF conversion notices are separate. Changed/new files also receive
a direct whitespace check. The earlier publication rejection is retained as
historical evidence; the later qualified neutral derivation supersedes that
rejection only, not the remaining production acceptance gates.

### Community #241 — canonical Debian import integration continuation

The existing [canonical session](../architecture/debian-session-boundary.md#later-canonical-import-integration-2026-09-28)
now has a closed development import action, import-specific immutable plan,
connected root-only view, payload-FD source validation, fresh semantic observer,
separate reopened import/session records and persistent-journal preflight. Linux
ESP is read-only/unmounted during this phase. No 43-stage promotion, target
configuration, agent/first-boot, firmware, Windows preparation or #243 work occurs.

The retained descriptor/pin/export hashes match the previous qualification. No
artifact rebuild/neutralization or new real import occurred. The exact
4,641,457,180-byte stream cannot be a single FAT32 payload file; the current
transport correctly blocks before reservation. Explicit chunk transport preserving
the original stream identity is proposed, not implemented. No host-path/network/
extra-source-disk fallback exists. Linux/.NET/runtime, canonical GPT/physical-ID,
persistent-store placement and full-artifact execution qualification remain open.

Acceptance: **integration implemented in part and fixture-tested; canonical real
artifact import NOT qualified**. ProductionAuthentication and production import
remain Unsupported; NativeSupported **0**. Prior import-mechanics qualification
is unchanged. The opt-in harness composes the actual components and documents its
unmet prerequisites. It does not provision or fabricate a target.

Validation: Core **353**, Preflight **343**, Community.App **58**, Migration **381**;
full serialized solution **1,324**; overlapping Debian/configured-root/session
subsets **350/35/39**; Linux discovery **268**, explicit native fixtures **45**,
capability/ACL fixtures **2**. All final runs: **0 failed / 0 skipped**. Builds of
the solution and opt-in harness: **0 warnings / 0 errors**. Real GPT/full-artifact
canonical execution was not run; these counts do not certify it.

### Later Debian FAT32 chunk transport acceptance — 2026-09-28

The preceding single-file transport blocker is superseded by explicit versioned
chunk transport in the existing canonical import plan/authority/reader. The real
unchanged 4,641,457,180-byte artifact has five 1-GiB-policy chunks, verified from
ordinary output and then a new GPT/FAT32 virtual payload. Original descriptor,
manifest, pin, package policy and neutralization evidence remain unchanged.
See [transport evidence](../architecture/debian-root-chunk-transport-evidence.json)
and [current session status](../architecture/debian-session-boundary.md#later-chunk-transport-qualification-2026-09-28).

The complete real collector rejects the MBR factory-runtime disk with
`NonGptDiskVisible`; Linux .NET and canonical lab-provider receipt qualification
are absent. No inventory filtering, fabricated receipt, source fallback or resolver
relaxation was used. Full canonical import and session teardown were **not run**.
The delivery fixture's normal FAT32 unmount is independently verified; it does not
stand in for canonical import/teardown. Production authentication, preparation,
registration and all readiness gates remain closed. NativeSupported remains **0**.

Current checks: Core **353**, Preflight **343**, Community.App **58**, Migration
**399**; full serialized solution **1,342**; Linux discovery **286**. Explicit
native broker fixtures **45**, ACL/capability fixtures **2** (reported separately,
not physical-storage/canonical proof). Final failures/skips: **0/0**. Build has
**0 warnings/errors**. Exact commands and evidence limits are in the artifact/session
documentation and harness README. Issue #241 remains open; no commit or push.
