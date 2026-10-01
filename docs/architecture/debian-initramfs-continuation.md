# Initramfs successor qualification

**Latest result (2026-09-30): canonical generation, publication, independent installed-image/delta
verification and exact teardown passed** for the recorded development lab only. See
[the corrected-wire attempt evidence](debian-initramfs-canonical-wire-evidence.json) and
"Corrected canonical integration qualified" below. Earlier failed attempts and their
missing failure-path teardown remain unchanged. No target boot or production promotion occurred.

**Historical first canonical attempt (2026-09-30):** the separately authorized successor
reopened v5, acquired canonical leases, verified journal placement and independently
verified the configured baseline. It then stopped before generation on a wire-role
adapter defect. The operation remains `OutcomeUnknown`, without canonical teardown.
See [the new canonical evidence](debian-initramfs-canonical-evidence.json) and the
earlier canonical-attempt section below. At that checkpoint the correction had not been native-executed.

**Current helper milestone:** a real retained-tool candidate passed independent
image and unchanged-configured-root verification. See the [candidate evidence](debian-initramfs-candidate-evidence.json)
and the continuation below. Canonical successor dispatch, target publication and
canonical teardown were unimplemented/unqualified at that helper checkpoint. The next section records the
earlier inspection checkpoint; its zero-pass budget and missing-observer statements
are historical, not the current helper result.

## Historical inspection checkpoint (2026-09-29)

**At this inspection checkpoint the initramfs milestone was not qualified.** The successful predecessor remains
[v5 core configuration](debian-core-configuration-v5.md). This continuation added
readback and candidate-inspection primitives and independently reverified that
configured state in an isolated runtime. It did not invoke an initramfs generator,
publish an image, or dispatch a canonical successor operation. The authorized
budget remains **0/3 helper passes and 0/1 canonical attempts**.

See [new evidence](debian-initramfs-continuation-evidence.json). Historical JSON,
the original semantic manifest and the v5 transformation policy are unchanged.

## What was actually verified

The starting checkout was `25794d402aa5d22ca7849d33a4f06078e99818b0` on
`refactor/community-fleet-foundation`, with 32 modified tracked files, 62 untracked
files and an empty index. A separate starting snapshot records their exact hashes.

The protected v5 configuration/Close chains reopened: 20 effect records and 18
session records. Their terminal hashes are respectively
`54EC121627C7D45BCCB47621CFD6C05F1C22C91E0C17241ED2329F9D0DD6443D` and
`D21FE8A94119D47270A91CED0962F6CF6637ADAE0C0825F453CC9AFAEF964001`.
Full target, journal and runtime backing hashes matched the retained handoff.

The Windows backing-volume gate passed its 180 GiB requirement, independently of
the WSL capacity check. Existing `copy_verified` created independent runtime and
configured-source copies. Fresh QEMU command-line/descriptor inspection established
the writable disposable runtime, read-only source copy and read-only tools medium.
There was no NIC, share, physical storage passthrough or target boot.

Inspection resources are retained, powered off, at:
`/var/lib/igloo/initramfs-helper-labs/9617f6cb5805492d8915c94d56de0c7e/`.
The source root was mounted `ro,noload`; its FAT32 payload was read-only. The new
`configured_successor.configured_manifest` reproduced the durably observed v5
replacement-entry digest
`4C8CFA39471BA1202D9329BE0CC8897928C60DC1B428458A4BB699112D5FAADC`,
then invoked the existing complete filesystem and package validators. It did not
regenerate TLS material, passwords, accounts or any other configuration output.

The expected digest comes from the independently reopened predecessor observation.
It is not learned from the new copy. Original entries protect the unchanged
complement; the predecessor digest protects the exact configured outputs, including
secret-bearing files through their hashes. Secret contents are not exported.

Ordinary payload, root and tools-medium unmounts each had fresh exact mount-delta
readback. Normal poweroff and process absence were separately observed. These are
**inspection-fixture** teardown results, not a canonical initramfs teardown receipt.
Full post-inspection hashes reconfirmed the protected v5 backings unchanged and the
source copy equal to the configured target. Earlier failed runs were not opened for
writing, replayed, repaired or reclassified; they were not all rehashed in this run.

## Candidate strategy and retained-source findings

The retained authenticated stream was independently rehashed before extracting
45 non-secret generation-related source files for inspection. Kernel package
`linux-image-6.12.107+deb13-amd64` is `6.12.107-1`; initramfs-tools is `0.148.4` and
kmod is `34.2-2`. The native configured-tree/package readback covers those inputs.

The proposed closed candidate strategy is:

```text
mkinitramfs -d /var/tmp/config -m most -r UUID=<bound-root-uuid> -c gzip
            -o /var/tmp/candidate.img 6.12.107+deb13-amd64
```

The versioned candidate policy is `debian-trixie-lab-initramfs-candidate-v1`.
It selects explicit EXT4/root, `MODULES=most`, `RESUME=none`, gzip and private
workspace configuration. The entire target, including `/boot`, is read-only to
the child; `/var/tmp` is a separately leased writable workspace. The actual ESP
is not exposed. The existing CoreConfiguration view retains its original meaning.
Candidate generation must be followed by separate trusted create-new publication;
that publication path is **not implemented yet**. No managed update-initramfs state,
bootloader aliases or bootloader completion is implied.

`update-initramfs` contains a conditional `/etc/initramfs/post-update.d` dispatch.
That directory has no entries in this authenticated artifact. The separate kernel
post-install GRUB hook exists, but is not called by the selected low-level strategy.
The choice avoids combining candidate generation with direct target publication;
it is not a claim that GRUB was actually invoked or reachable through an existing
post-update entry in this artifact.

The bound build-hook set is amd64_microcode, dmsetup, fsck, fuse, intel_microcode,
keymap, klibc-utils, kmod, ntfs_3g, plymouth, resume, thermal, udev and zz-busybox.
Configuration hooks, sourced functions and interpreter/library/tool dependencies
also belong to the execution closure. `cache_run_scripts` invokes boot scripts with
`prereqs` to generate ordering; those metadata invocations need explicit review and
binding, and must not be confused with permission to execute their boot actions.

Important unresolved generation details include the retained `ldconfig -r` call
(the current sandbox denies chroot), generated binary dependency indices, microcode
selection/container semantics and Plymouth font-cache output. The upstream helper
may tolerate some failures; exit zero therefore cannot establish image correctness.
No syscall or capability restriction was relaxed to accommodate these operations.

## Implemented boundaries and remaining work

- `DebianVerifiedConfiguration.Reopen` validates the v5 plan, complete ordered
  effect chain, target/preparation/preserved-set bindings, independent observations,
  result reference and exact teardown. It is a readback capability, not successor
  execution authority. The shared existing chain parser is reused.
- `configured_successor.configured_manifest` revalidates configured content without
  weakening the neutral artifact verifier or replaying configuration.
- `InitramfsCandidate` is a separate broker view with a closed command shape,
  read-only target, independent writable workspace and effective mount/namespace/
  capability observation. It has deterministic tests but **no native helper test**.
- `initramfs_archive` parses bounded newc/gzip segments without extracting paths or
  devices. It rejects corruption, truncation, unsafe paths, special/set-ID objects,
  duplicate/aliased destinations and inconsistent hardlink groups.
- `initramfs_candidate` binds retained kernel/tool/hook inputs and begins independent
  image, library, module and copied-content checks. It rejects unsupported generated
  members. This is **not a complete image observer**.
- `initramfs_broker_rehearsal` requires a fresh configured fixture and records intents
  and results. It cannot report a qualified helper pass while complete image
  observation is missing. It was not dispatched in this continuation.

Still required: complete generated-content/firmware/dependency-index observation;
native candidate-view and helper qualification; a separately authorized configured
checkpoint successor wired through shared storage, leases, journal placement and
session dispatch; exact journaled target publication; canonical positive and native
precondition-negative runs; final delta/result reopen and canonical teardown.
These are **unfinished implementation/qualification work**, not an unavailable
external authorization, pin or capacity prerequisite. The external development pin
was protected and valid at inspection time; it must be checked again at execution.

ProductionAuthentication remains Unsupported and NativeSupported remains 0.
Windows v1, recovery/readiness and preparation/registration gates are unchanged.
No generated initramfs handoff exists. Later kernel/module/hook/input changes must
invalidate any future image result. No target-boot or power-loss claim is made.

## Retained candidate helper qualification (later continuation)

This continuation started at the same HEAD with 32 modified tracked files,
71 untracked files and an empty index. It used two of the three authorized helper
passes and no canonical attempt. Pass 1 generated an explicitly unqualified
diagnostic candidate. Read-only observer development used that retained image;
its diagnostic result was not rewritten. Pass 2 used a fresh independently
verified configured-root copy and workspace and passed the complete acceptance
path on execution identity
`F16BA7CD6B9A97B3F2BF50A8AF88D24C3F0AB009B30C410A0688ACCA1A8D1DDF`.

The accepted candidate is `/var/lib/igloo/initramfs-fixture-2/workspace/candidate.img`
inside the retained fixture runtime. It is 150,835,944 bytes, SHA-256
`DBCD976F4E5BD2B52C6859A3046043BDF604FF6EEEFAD055D5F8DB80187A7302`.
The evidence names the powered-off runtime backing, source copy and raw records.
The original configured target was never attached writable. Both complete
configured-state/metadata checks and package-state verification passed; no v5
helper, import or configuration operation was replayed.

The actual call chain was `initramfs_broker_rehearsal.run` →
`configured_successor.configured_manifest` → `initramfs_candidate.declaration` /
`verify_execution_inputs` → `PackageBroker.execute` with `InitramfsCandidate` →
the stopped command gate and independent effective-isolation observer → retained
`mkinitramfs`. A fresh observer process then ran `observe_image`,
`observe_generated` and `verify_payload`, followed by another configured-root
comparison. Fixture intents/results were fsynced and independently reopened.

The target, including `/boot`, stayed read-only; only the leased workspace was
writable. The selected kernel was the authenticated installed
`6.12.107+deb13-amd64`, not runtime `uname`. The strategy remains MODULES=most,
explicit root UUID, RESUME=none and gzip. The CPU policy is the observed
AuthenticAMD virtual CPU; required storage support is the declared virtio-block /
EXT4 lab topology. The real ESP was unexposed. No `update-initramfs`, bootloader,
managed-state publication or image boot occurred.

The observer now covers 3,310 archive entries and 874 source-bound modules:

- Eleven generated module indices are checked against authenticated source
  mappings, filtered to the selected module set. Retained kmod read-only lookups
  independently match the source, including soft dependencies. Virtio and
  virtio-pci are built in; virtio-blk and EXT4 dependency closures are present.
- AMD early microcode is the exact declared source concatenation. Intel records
  are source-bound and independently checked with retained iucode-tool strict
  validation and selection, including extended signatures. Early/raw/gzip
  archive composition remains bounded and non-extracting.
- All four font caches are queried by retained fontconfig through the restricted
  read-only broker. Their directory bindings and complete patterns match
  independent queries of authenticated fonts; retained `fc-match` verifies both
  Plymouth selections. Its exact no-writable-cache diagnostic is recorded:
  source selection does not require persisting a source font cache.
- The generated glibc cache has complete bounded entry/string/extension coverage,
  independent retained `ldconfig -p` readback and source-bound ELF/library closure.
  The retained glibc has a supported path-prefix fallback when `chroot` fails;
  no capability or syscall permission was added. See the retained-version
  [ldconfig implementation](https://sources.debian.org/src/glibc/2.41-12%2Bdeb13u4/elf/ldconfig.c/).

Narrow representation corrections include authenticated ntfs-3g set-ID bytes
as archive data, early-microcode directory modes, hook-specific copies under the
declared umask, systemd's authenticated private library search path, and libgio's
PEM-parser delimiter literals. They do not allow execution of archive data,
arbitrary set-ID files, substituted libraries or generated secret material.
Unknown payload data, unsafe paths/links, wrong metadata and missing dependencies
still fail. The source-bound boot-script `prereqs` branches were reviewed as
metadata calls; their boot actions were not executed.

Six read-only negative cases on the retained candidate rejected wrong kernel,
gzip corruption, authenticated module substitution, prohibited account material,
extra dependency-index bytes and changed microcode. The candidate stayed unchanged.
Ordinary unmount of the fixture payload, source and tools mounts produced exact
fresh absence/delta observations and reopened records. This is **fixture teardown**,
not canonical teardown; normal VM shutdown is recorded separately.

The final Linux discovery run passed 357 tests. Focused initramfs (15), configured
successor (3) and isolation (44) tests are overlapping subsets, not extra totals.
No .NET source changed, so no new .NET build/solution result is claimed. No additional
generic privileged probe pass was run; the actual generation and retained font
queries exercised the current broker/gate/observer path. `git diff --check` and
separate changed-untracked whitespace/BOM/artifact checks passed.

The remaining canonical work is explicit: consume `DebianVerifiedConfiguration.Reopen`
through a separately authorized configured-state successor, integrate the candidate
plan/view with shared leases and persistent journal placement in
`DebianMountSessionAuthority` / `DebianNativeMountSession`, then implement exact
create-new target publication and independent published-image/delta/result checks.
`DebianKernelEvidence` alone is not that archive observer or execution authority.
Canonical positive/rejection dispatch and exact canonical teardown are still
unqualified. Managed update-initramfs state, bootloader integration, other hardware,
target boot, power-loss durability and production enablement remain outside this
helper result. Changed kernel/modules, hooks, inputs or relevant early-boot policy
invalidate reuse of this candidate without separate revalidation.

## Canonical successor attempt (2026-09-30)

The new closed `InitramfsImage` storage provenance (provider version 4) consumes
`DebianVerifiedConfiguration.Reopen` through `ContinueLabConfiguredCheckpoint`.
It preserves the original creation/format lineage and validates new independent
backing copies. `OneConfiguredCheckpointInitramfs` has an external create-new host
reservation; neither the old configuration reservation nor its scope is reused.

`LabInitramfs` / `--initramfs-derived-lab` connects the validated context to
`InstallerBlockLeases`, `ObserveImportJournalsAsync`, `ForLabInitramfs`, and the
existing private-pipe mount supervisor. The immutable plan binds configuration/Close,
configured entries, derivation, exact candidate input/hook policy, root UUID, and
current runtime module hashes. Smoke, import and configuration authorities retain
their earlier action sets. This is a development composition only.

`session_initramfs.perform` separates Baseline, Generation, Candidate, Publication
and Verify intents/results. Generation uses the qualified `InitramfsCandidate`
broker with a read-only target. The generated-content query function was moved
unchanged into `initramfs_observer`; its syntax tree was independently compared
with the retained qualified implementation. No extra helper pass was consumed.
The executable workspace is a new protected directory on runtime EXT4; session
and effect journals stay on their independently verified persistent `noexec` EXT4
store. This distinction is necessary for the retained copied scripts' `prereqs`
invocation, and does not authorize executing their boot actions.

`initramfs_publication.publish` is a trusted FD-relative, create-new copy to
`/boot/initrd.img-6.12.107+deb13-amd64`, root:root mode 0600. It binds the candidate
and parent identities, rechecks the connected session, hashes during copying,
flushes the file/directory and independently reopens the installed file. A failed
copy retains its exact partial destination. There is no overwrite, cleanup,
bootloader alias or managed update-initramfs state. Final observation composes the
verified configured manifest with exactly that image addition; the neutral and v5
validators are unchanged. This publication path is fixture-tested, **not yet
qualified with a canonical generated image**.

Run `1ed3a65c-15a0-4cc9-afd2-a5308eba8f1c`, operation
`3d38bbf6-7e31-4934-bb76-11196a36b6fb`, session
`e9c1d5ba-79e3-4f2e-971e-3ee29fe7b4ae` consumed the single positive attempt.
The separately bound native negative rejected before session reservation and block
acquisition. The positive run durably reopened Baseline, but created no Generation
intent and invoked no generator or publisher. Its workspace remained empty.

The executed adapter compared the shared numeric lease role with the string
`Root`. Replaying that expression **read-only against the retained binding data**
raises `StopIteration`; this is a confirmed deterministic defect consistent with
the observed stop boundary. The original supervisor did not retain a Python
traceback, so that reproduction is not claimed to be an original exception record.
Current source uses `bound_root_uuid` with exact numeric role/order/access checks,
and records a stopped operation between effect checkpoints. These post-failure
corrections were not staged into or replayed on the consumed session.

The attempt remains `OutcomeUnknown`. Namespace disappearance and the later normal
VM shutdown are not canonical teardown. Only the separate provisioning journal/tools
mounts received ordinary unmount observations. The preserved lab partition hash
matched; no post-failure whole-target content verification is claimed. The failed
target, journals, stage revisions and raw records remain retained under the new run.
The protected configured predecessor and qualified helper fixture remain the
successful inputs. Another positive native attempt needs separate authorization
and a fresh derivative; no reservation may be reset. The existing failure-path
teardown gap remains an acceptance item, not a recovery capability implemented here.


## Corrected canonical integration qualified (2026-09-30)

The continuation started at the same committed HEAD with 32 modified tracked files,
84 untracked files and an empty index. All preceding work was preserved. The current
numeric-role correction already existed. This run additionally enforced exact role/access
integer types, all three filesystem roles and distinct UUIDs. The actual shared lease
serializer emits uppercase UUID text; the adapter now compares its canonical lowercase
UUID identity with the unchanged candidate declaration. Windows wire encoding is unchanged.

`DebianNativeMountSession.SerializeRequest` and the authority's `LeaseResponse` are the
production serialization paths exercised by the exported .NET fixture. The Python test
runs `session_initramfs.perform`, real role parsing, candidate launch construction and
ordinary-file publication with explicitly synthetic acquisition/generation/process
observations. It covers Baseline through handoff, contradictory UUIDs, forbidden coercion,
intent failure, final observer failure and single-use rejection. Inter-step failures
record a closed location and completed-step count without exception/private data.
The independent chain reader also rejects contradictory generation, publication, package
and image records even when their outer hashes are recomputed.

Fresh run `c5dc04e9-18f6-4b78-b60a-2346e2d70000`, operation
`5263e27d-3903-4221-a2a9-c169766a3f32`, session
`883ff871-f9bb-4c2f-a300-21d82dc8df79` used the existing one-use authorization and
independent-copy mechanism. It reopened the successful configured predecessor/Close,
revalidated complete inventory, shared ownership/leases, persistent journal placement,
external development authentication and the complete configured baseline. A separately
bound native negative rejected before session reservation/acquisition. The positive
command was invoked once. No additional generator helper pass was needed: the qualified
generator, isolation and image-observer contracts were unchanged.

Baseline, Generation, Candidate, Publication and Verify all completed `AppliedAndVerified`.
The new image at `/boot/initrd.img-6.12.107+deb13-amd64` is root:root mode 0600,
150,835,389 bytes, SHA-256
`E99B6539000237AB7779628FE1B2E17723ACF0F2256780822B5A7FB3A96423F2`.
It was generated in the runtime workspace with the target read-only, independently
qualified, then published create-new by the trusted publisher. Fresh installed-image
observation verified 3,310 archive entries, 874 modules, dependency indices, required
virtio/EXT4 support including built-ins, declared microcode, fonts and library closure.
The configured baseline plus exactly this image addition, package state and preserved
lab partition passed independently. No old fixture image was installed.

`canonical_initramfs_readback.verify` independently reopened 10 effect records and 18
session records, including ordinary payload/root unmounts with exact absence/delta
observations and Close. The effect result is
`00000009-9F380583A49B1A6F44E42B2D40692745C477D97A959E3C82CE842C317927F0B8.json`;
Close is `00000017-F0CB1447C24AF09EBC1A178E29E692AAB36C8AED0AAE130CE2A16CD23F8446F3.json`.
The durable independently reopened host handoff hash is
`FE6F41BF76BF65D2B06FD97E8E638B4C53E2CF239B19734D991A17BB57930BAF`.
Normal runtime power-down is separate evidence. Protected raw records and retained
backings are under `/var/lib/igloo/initramfs-helper-labs/c5dc04e918f64b78b60a2346e2d70000/`;
the inherited parent directory name does not make this canonical attempt a fixture.

Executed runtime-module identity:
`084441C3A73B5B4F7325700C3DE3A455BB097D13718196D0D8F6F8F9BD7BEDE2`.
Executed adapter hash:
`1650D3D664CC725488C4B93703AF2CCF8DDE4F5BB2F1080B134EB65B26ED0A10`.
No effect, isolation or observer source changed during the transaction.

The handoff qualifies only the declared AuthenticAMD/virtio-block/EXT4 lab and exact
installed kernel/input policy. Changes to kernel, modules, hooks or relevant early-boot
inputs require separately authorized revalidation/regeneration. Managed update-initramfs
state, aliases, bootloader command line/finalization, ESP/NVRAM, agent/UserData/Enrollment
and boot testing remain deferred. Earlier poisoned sessions still lack qualified
failure-path teardown. ProductionAuthentication remains Unsupported; NativeSupported
remains 0; Windows and recovery/readiness semantics are unchanged.


Post-shutdown full-byte checks matched the original configured target/journal/runtime,
the qualified helper runtime, and the previous failed canonical run's three backings.
The failed run's record hashes and file identities also remained unchanged. Other
historical failures were neither attached nor written; no new full-byte audit of all
older runs is claimed. The successful retained target hash is
`55B7F3E69A742C1DC5F470D67207F415B847ADFC296D4B93BA619D809A8E942C` (40 GiB);
the journal hash is `ED36D552AD275F96945A7D52870A039C0F1B097E9FA4853212E719DF9399109C`
(8 GiB). `initramfs-retention.json` binds those backings to the effect, Close and
independently reopened handoff. This is retained evidence, not successor execution
authority or permission to replay this completed operation.

Validation: solution and qualification-harness builds passed with warnings as errors;
the rebuilt serialized solution suite passed 1,442 tests (Core 398, Migration 448,
Iso 19, Community.App 58, Preflight 343, UsbWriter 23, Fleet 153). Final Linux discovery
passed 375 tests with the real serializer export enabled. The focused 18-test
wire/publication/readback result is an overlapping subset, not added to that total.
The new canonical evidence records exact commands and execution identities. No new
retained-helper campaign, target-boot test or power-loss test was performed.
