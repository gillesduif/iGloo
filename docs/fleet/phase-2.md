# Fleet Phase 2: recoverable execution and read-only target identity

Status: **Phase 2A fake recovery, Phase 2B1 read-only identity, Phase 2B2 local authority/gating, and Phase 2B3.1 shared snapshot contracts/read-only capture composition are implemented. Real execution remains disabled; production recovery readiness remains unavailable.**

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

Effective user was `desktop-living\gilles d'huyvetter`. Integrity SID
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
