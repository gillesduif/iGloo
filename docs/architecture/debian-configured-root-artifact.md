# Debian configured-root artifact — Community #241

Status, 2026-09-28: **DEBIAN CONFIGURED-ROOT ARTIFACT QUALIFIED** for development
publication/import mechanics. Production deployment remains blocked.

The later [canonical import integration](debian-session-boundary.md#later-canonical-import-integration-2026-09-28)
adds a closed session action, connected root-only view, payload-FD source binding,
fresh verifier and separate reopened journals. No new real import is qualified:
the subsequent [chunk transport continuation](debian-session-boundary.md#later-chunk-transport-qualification-2026-09-28)
verifies real FAT32 delivery, while canonical runtime/persistent-store qualification
is open and production authentication remains Unsupported. Artifact bytes and
previous evidence were not rebuilt or changed.

## Identity-preserving FAT32 transport v1

This is a separate physical envelope, **not a semantic artifact schema change**.
The exact retained schema-2 descriptor/attestation/manifest/content remain unchanged.
`SingleFile` remains compatible up to 4,294,967,295 bytes per physical file. Explicit
`Chunked` uses 1,073,741,824-byte chunks, only the final chunk shorter. No fallback,
compression, separators, headers or reconstructed content file is introduced.

`DebianRootTransportV1` binds version/type, original build/derivation, descriptor and
semantic-manifest hashes, original logical length/hash, fixed chunk policy and an
ordered array of index/name/length/hash. Names are exactly `root.content.0000` etc.
Strict .NET/Python parsers reject duplicate/unknown fields, noninteger/overflow
lengths, gaps, aliases, paths and noncanonical UUID/hash forms. Limits: 32-KiB
envelope, 64 chunks / 64 GiB logical content, 64-KiB read/copy buffers, at most 64
retained chunk FDs plus the three descriptor/manifest/transport FDs. Offsets are
64-bit. Equal hashes are legal; indices, physical identity and aggregate bytes bind
the ordered source. The shared real envelope is tested by both codecs.

`root_transport.produce` first hashes the existing read-only FD, creates only a new
directory/files, copies bounded buffers, fsyncs, independently reopens and verifies
all chunks, and publishes/reopens the manifest. It reports success only after all
durability/readback calls succeed. Failed output remains forensic partial state;
there is no resume/overwrite/delete operation. The qualification producer also runs
the existing complete semantic verifier against the original source first.

The source reader opens all chunks FD-relative beneath the canonical payload,
checks regular-file/unique-inode/length/mount identity, retains handles and supports
reads crossing chunk and semantic-file boundaries. `verify_source` and `import_files`
share that reader, including the existing narrow ACL/capability/hardlink policy.
An FD alone is not immutable: retained identity, per-file hashes, full stream
rehashing and independent target readback remain required. The existing exclusive
runtime threat model does not claim containment of a compromised host root.

Capacity uses allocation-rounded physical objects plus descriptor, semantic and
transport manifests, `OtherPayloadBytes` and an explicit 64-MiB reserve. Import checks
total filesystem capacity and remaining reserve before import reservation. No resize
or PreparedLayout mutation exists. The lab needed **4,740,030,464 bytes** including
reserve and observed **8,573,145,088 available bytes**, with 4096-byte allocation.
Production planning still must supply all other required payload bytes and account
for this budget before preparing a generation; no production planner is qualified.

Real content: **4,641,457,180 bytes**, original SHA-256
`EDE9ADCC24597B82389CE7C73D38A02A3AB077DA53C132EE9704FEC71BC705F5`.
The [exact transport manifest](debian-root-chunk-transport-manifest.json) is 1121 bytes,
SHA-256 `A093432AE281F4620959B56E35774DC0E5D01C74443DCD33F248DA723B364757`.

| Index | Bytes | SHA-256 |
| --- | ---: | --- |
| 0 | 1073741824 | `4A12222BB8993E2A687CCC8721837CD80D307B9DCB022FCEBE443A5560AC5C41` |
| 1 | 1073741824 | `B2851BBB5A55E3179A1647FEFD9D26F6F0F12518D3B29D0ED8148C0C4F9A04F0` |
| 2 | 1073741824 | `D7C53DBFCE5788C2930BA2E07D3B4AE8465DE09E13920909A3FC0847C1CC713B` |
| 3 | 1073741824 | `B046F45ED9DD2BF7D7B09A8433C22115F265BFC57C7EB3E1EDB29D7EB458329E` |
| 4 | 346489884 | `B8DA049C3E6ABE4DA9361B64CDF55818DD76B682672D90E931DE805FBE917D03` |

Fresh verifier processes read every chunk and the complete original semantic stream
from ordinary output and then from a read-only FAT32 mount. A separate full-sized
corrupt derivative is rejected before invoking import. That rejection is a source
verifier test, not a canonical session/generation execution. Development authority
still comes only from the original externally pinned descriptor and its existing
validity interval, not from a transport hash. ProductionAuthentication is Unsupported.
New verifier identities are in [transport evidence](debian-root-chunk-transport-evidence.json);
old attestations and evidence were not rewritten.

The [final readback and normal delivery teardown](debian-root-chunk-final-delivery-evidence.json)
use the final checked-in reader hashes, and are separate from canonical teardown.
[Runtime evidence](debian-root-chunk-lab-runtime-evidence.json) retains QEMU arguments,
actual host FD access modes and shutdown observation. The VM powered off normally;
the process is absent, with an inert serial-socket pathname retained (not falsely
reported absent). The original raw disk and retained overlay size/mtime are unchanged.

Validation commands for this transport continuation:

```text
dotnet test tests/Igloo.Core.Tests/Igloo.Core.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Preflight.Tests/Igloo.Preflight.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Community.App.Tests/Igloo.Community.App.Tests.csproj --no-restore -m:1 -warnaserror
dotnet test tests/Igloo.Migration.Tests/Igloo.Migration.Tests.csproj --no-restore -m:1 -warnaserror
dotnet build -warnaserror
dotnet build tests/installer/CanonicalImportQualification/CanonicalImportQualification.csproj --no-restore -m:1 -warnaserror
dotnet test Igloo.sln --no-build --no-restore -m:1
python3 -B -m unittest discover -s tests/installer -p 'test_*.py'
python3 -B tests/installer/broker_rehearsal.py
python3 -B tests/installer/artifact_native_rehearsal.py
git diff --check
```

Core 353, Preflight 343, App 58, Migration 399; full solution 1,342, all 0 failed /
0 skipped. Debian 368, configured-root 35, session 41 and transport 16 are
overlapping Migration subsets, **not additional totals**. Linux discovery 286;
explicit broker native fixtures 45 and ACL/capability fixtures 2 pass separately.
Linux commands used Ubuntu-24.04; broker used the retained hash-pinned bubblewrap
`573236E5328AC2EBB08F59AE3A9805B4F8D12BDEF14BE8AF4450D5463294985F`.
Both builds: 0 warnings/errors. Diff-check exits 0; 28 LF/CRLF notices are separate.
Changed untracked source/docs were also checked for trailing whitespace: none.
Early test/launch failures were corrected before these final runs; they were not
ignored or converted to skips. Full canonical import, power-loss testing and
production journal placement were **not run**.

Configured-root deployment remains the selected **candidate direction**, not an
accepted production installer. The existing offline Trixie GNOME root was preserved
unchanged and a separate derivation was neutralized with exact package-aware
operations. All 1,594 packages remain configured. The real manifest, content stream,
attestation and external development pin pass fresh-process verification. Real
EXT4 import, full filesystem/dpkg equality and ordinary teardown also pass. See the
[neutralization and import report](debian-configured-root-neutralization.md) for
the exact policy, evidence and remaining target regeneration requirements. Release
signing and the production import session remain unqualified. Preparation and registration stay
disabled. RecoverySnapshotV1, Exact, RecoveryReadiness and PreCommitGate are unchanged.

## Evidence and qualification levels

| Component | Evidence in this continuation | Limit |
| --- | --- | --- |
| Trixie inputs | Fresh InRelease and 12 Packages.xz indexes; signature, primary signer, suite/codename, architecture, Date/Valid-Until/90-day age and hashes verified | Frozen inputs; no metadata refresh during download/build |
| GNOME closure | 1,594 packages, 104 standard-priority packages, 78 minbase archives, 110 alternate repository paths; archives independently hashed; APT solution repeated on host and Trixie guest | Exactly one reviewed wsdd recommendation exception; no other unresolved relation |
| Factory | Actual Debian 13.7.0 BIOS guest, QEMU/KVM, new 80-GiB virtual disk, read-only input ISO, no NIC/passthrough/shared folders | Factory evidence, not the production target sandbox or VMware migration qualification |
| Configured root | Exact neutralization plan v2; whole-tree delta and fresh dpkg audit after each operation; all 1,594 versions/states retained | Target machine/TLS/Exim/initramfs finalization remains mandatory |
| Descriptor/attestation | Real schema-2 descriptor binds original source, derived root, exact plan, package/neutralization readbacks, manifest/stream; fresh independent verification and external development pin | Development only; no release authority |
| Semantic filesystem | Actual 144,911-entry neutral manifest, 4,641,457,180-byte stream; existing narrow schema 2 covers all observed metadata | Production canonical session integration remains Unsupported |
| Publication authentication | Explicit interface; caller-supplied development pin only | No production artifact signing authority or key-management workflow |
| Production import | Typed session/lease support query returns Unsupported | No arbitrary destination or fixture bypass into native dispatch |

Original factory evidence is [debian-configured-root-factory-evidence.json](debian-configured-root-factory-evidence.json).
It is preserved unchanged; current neutralization/import evidence is linked from
the [continuation report](debian-configured-root-neutralization.md).
The earlier [rejected solve](debian-configured-root-evidence.json) remains historical evidence.
Qualification labels: **Designed, Implemented, FixtureTested, RealFactoryBuilt,
IndependentlyVerified**. ProductionAuthenticated and VMware migration VMValidated
remain false. **ImportRehearsed** is true for the revised development artifact;
canonical production import still returns Unsupported.

Retained evidence is in Ubuntu-24.04 at
`/var/lib/igloo/factory-evidence/99695826-82bc-4077-b4cf-a22cebc706da/`:
`signed-inputs`, `packages/bundle`, and `factory` (powered-off raw VM disk, read-only
input ISO, logs, exported package observations and hash-verified compressed full
filesystem audit). Original absolute paths in provenance describe execution before
retention; moving these new experiment directories did not change their contents.
The raw guest root is `/factory/root-99695826-82bc-4077-b4cf-a22cebc706da`.
It contains a generated test TLS private key and is **not a distributable artifact**.
The first shutdown wait timed out; a later independent check confirmed QEMU exit
and console-socket removal. The host was not rebooted or powered off.

## Factory choice and boundary

The [on-target rejection](debian-bootstrap-primitive.md) remains in force. Nothing
in this design lets mmdebstrap, debootstrap, tar or a chroot command borrow a target
package profile. The existing broker, capability sets, syscall policy, leases and
mount/session authority are unchanged.

| Build-time primitive | Decision |
| --- | --- |
| mmdebstrap 1.5.7-1+deb13u1 | Selected candidate for a separate disposable factory VM: Debian APT dependency resolution, multiple explicit mirrors, fully configured package output, public file-mirror hooks and directory output fit the input/evidence model |
| debootstrap 1.0.141 plus controlled VM | Supported base-system alternative, but needs an additional pinned APT workstation phase and equivalent qualification; no reason to maintain two factories now |
| live-build 1:20250505+deb13u1 | Appropriate for live media; extra live/session cleanup and product transformation are unnecessary for a configured installed root |
| debuerreotype / official cloud or live root | No demonstrated advantage or intended-workstation equivalence; not accepted as a prequalified substitute |

These versions were inspected from authenticated Trixie packages in the prior
[source audit](debian-bootstrap-evidence.json). The public
[mmdebstrap interface](https://manpages.debian.org/trixie/mmdebstrap/mmdebstrap.1.en.html)
supports explicit APT Recommends, versioned includes, file mirrors and configured
directory output. The real build used that interface without patching helpers.

Required factory: a disposable **VM**, with only newly created file-backed virtual
build disks, a read-only authenticated repository input image and a bounded output
channel. No physical/USB/PCI block passthrough, shared host directories, guest
integration service sockets, clipboard/credentials, host firmware or migration
target access. Disable its NIC for the build; use no UEFI firmware/ESP in the guest
for this artifact phase. Record VM image/runtime/tool hashes and the exported VM
device configuration. Guest root and Debian maintainer scripts are trusted only
inside this disposable factory. The hypervisor, source verifier and release
authority remain trust assumptions. A namespace/chroot on WSL is not an equivalent
factory. The actual guest ran under QEMU 8.2.2/KVM hosted by WSL; package scripts
executed inside the VM, not on the host.

Observed factory lifecycle:

1. Verify the separately trusted archive keyring, allowed signers, suite/arch/date,
   retained InRelease, indexes and every package archive; independently repeat APT
   resolution with Recommends on and Suggests off. Bind all three suites and actual
   versions, plus required/important/standard priority expansion. `standard` is not
   an invented package name. Only the exact reviewed wsdd relation may be excepted.
2. Use unmodified mmdebstrap in the VM, with explicit file-only sources, amd64,
   exact resolved `name=version` includes, configured directory output and its
   authenticated public file-mirror hook tree. Never accept its default online
   mirror or default Recommends setting. No deployment-time network.
3. Independent package readback and raw filesystem inventory succeeded. The raw
   root initially failed publication and remains untouched. Plan v2 neutralizes a
   separate derivation; its full semantic manifest binds alternatives/diversions
   and all other retained package state.
4. Produce the semantic manifest and byte stream. Independently reopen/reverify
   both in a separate verifier, retain the build attestation, then obtain external
   release approval/signing. An unsigned or unsupported artifact cannot deploy.

`tests/installer/factory_guest_rehearsal.py` records reopened build intent, runs the
bounded experiment and records process outcome. `factory_root_observer.py` performs
fresh package/filesystem inspection. `factory_publication_gate.py` does not alter
the tree or filter away rejected entries. None is production migration dispatch.

The guest has only its new virtual disk and read-only optical input, no EFI/ESP,
and only loopback networking. QEMU runs as uid 1000 with zero effective/permitted/
ambient capabilities, NoNewPrivs=1 and seccomp mode 2. Independent host FD readback
found no physical block device. The hypervisor and host kernel remain trusted.
Factory-only setup privilege does not change target broker capabilities/seccomp.

Runtime: APT 3.0.3, dpkg 1.22.22, gpgv 2.4.7, Python 3.13.5/python3-apt 3.0.0.
mmdebstrap executable SHA-256:
`C8A55D9731E81C00A78DB8824854A099ADBDB516F83F88B6D546D4D17B03EA1A`.
Helper-tree SHA-256:
`FB6B55CBEFDB69ED5B137806BD4EDB161ABF2F305C1E32966C40A29E84C6596D`.
Full tool/VM/input identities and argv are retained with the evidence.

Invocation: `--mode=root --variant=custom --format=directory --architectures=amd64`,
all four components, explicit keyring, Recommends true, Suggests false, retry zero,
translations disabled, unmodified public file-mirror-automount hooks, all 1,594
exact name/architecture/version includes, and three explicit file-only repositories.
There is no default Internet source. APT 2.8.3 and guest APT 3.0.3 reproduced the
same frozen solution. The full configured build took approximately 301 seconds.

## Product policy, signed packages and hardware

The existing `DebianWorkstationPolicy.Trixie` remains authoritative: standard/base
priorities; linux-image-amd64/initramfs-tools; task-gnome-desktop and its GNOME
recommendations, gdm3, NetworkManager, sudo, systemd-sysv/dbus; locale/timezone and
keyboard/console tools; CA/archive trust; Python/GI, rsync and ntfs-3g; the declared
firmware and signed-boot support packages. One versioned exception was reviewed:
**trixie/debian, gvfs-backends 1.57.2-2+deb13u1 amd64, Recommends: wsdd**, only while
no wsdd candidate/provider exists. It expires at 2026-10-28 00:00 UTC and requires
review on any relation/version/source change. The manifest retains the original
unresolved relation, applied exception and empty remaining-unresolved list. Any
second failure blocks; Recommends stays enabled and Suggests disabled.

The authenticated GVfs source separates WS-Discovery from libsmbclient access.
Absence loses automatic discovery of some Windows/Samba shares, not direct
`smb://server/share` access, local files or offline migration. This discovery feature
is explicitly non-mandatory for the baseline. Debian [bug 1110689](https://bugs.debian.org/1110689)
confirms wsdd2 has a different interface; the later unstable Suggests change is not
imported into Trixie. No wsdd2 substitution or cross-release package is used. This
is a product policy decision, not a claim that Trixie's metadata changed.

The desired factory outcome includes configured signed-package **contents**, with
no installed ESP or claimed NVRAM registration. Configured package state and empty
`/boot/efi` were observed in the no-EFI factory. This is not loader qualification. The
existing [actual Trixie hook audit](debian-target-root-deployment.md#actual-trixie-package-observations)
shows GRUB/shim hooks may call grub-install/update-secureboot-policy and can swallow
failure. A non-EFI factory and public debconf options alone cannot certify their
final boot state. Real shim hooks return early without `/sys/firmware/efi`; signed
package files are present and no machine-specific grub.cfg was generated. GRUB
also logged a tolerated update failure: configured dpkg state must never be
presented as successful bootloader installation.
If this cannot be supported, a separately versioned deferred-package policy and
complete target finalization are required. This continuation does **not** silently
omit those packages, mark unpacked packages configured, or change the package policy.

The real root contains kernel/modules `6.12.107+deb13-amd64` from package `6.12.107-1`,
modules.dep and a 133,708,032-byte provisional factory initramfs.
Target regeneration for the installed kernel, followed by independent module/image
correlation, is still mandatory and **not qualified under the restricted broker**.
The provisional image and aliases are excluded by the versioned neutralization
plan and rejected by the artifact verifier. They remain only in raw evidence.
The target create/regenerate/readback sequence is explicit; kernel package files
and modules remain in the artifact.
Never use the builder's running kernel as authority. Hardware baseline remains
firmware-linux, firmware-iwlwifi and firmware-realtek plus their authenticated
closure. This is not universal hardware support: storage/network hardware must be
qualified before migration. DKMS/NVIDIA/MOK are excluded from supported baseline
claims; optional online applications/codecs do not repair missing boot hardware.
The actual dependency closure also contains Intel/AMD microcode and AMD/Intel/
NVIDIA/MediaTek firmware blobs. Firmware blobs do not qualify proprietary NVIDIA,
DKMS or MOK enrollment; none of those driver lifecycles is accepted by this artifact.

## Source contract, trust and obsolescence

`DebianConfiguredRootArtifactV1` supports historical schema 1 and real-artifact
schema 2, binding trixie/amd64, independent build ID,
creation/support times, the exact `OfflineDebianPackageSetV1` and its hash, configured
package states, workstation policy, builder/helper/runtime identities, neutralization
policy, stream/manifest filenames/lengths/hashes, logical byte count and attestation.
The build ID is not the migration's PreparedLayout generation. A later import intent
must bind both identities without baking target identifiers into a reusable image.

`DebianRootBuildAttestationV1` binds the same build, package set, policy, builder,
manifest and content to retained dpkg/dependency/neutral-state readback hashes,
independent verifier identity and verification time. Schema 2 additionally requires
`DebianRootNeutralizationEvidenceV1`, binding preserved source, plan/result,
independent neutralization observations and target regeneration responsibilities.
Merely supplying those fields
is not proof the readback happened. The publisher must authenticate the observations.

Trust chain: externally trusted Debian archive keyring -> signed InRelease -> index
hashes -> exact deb bytes/solution -> qualified isolated builder -> independently
verified configured filesystem -> manifest/content/attestation -> **external iGloo
release trust anchor** -> migration verifier. Debian signatures cover Debian inputs,
not the filesystem produced by iGloo.

Repository inspection found distro PGP verifiers in Igloo.Iso and CI/DCO/CodeQL
workflows, not an iGloo configured-root release signer, signing service or rotation
policy. `IDebianConfiguredRootAuthenticator` defines the required verification
boundary. `ProductionAuthentication` stays Unsupported. No key was generated.
`ReopenDevelopmentPinned` requires an out-of-band descriptor hash, build/policy ID
and UTC validity interval; no embedded pin/self-signature is trusted. Native fixture
pins independently bind manifest and stream hashes. Both are explicitly development
mechanisms and cannot enable a production stage.

The candidate descriptor limits its support window to 30 days after creation and
requires current external approval within that window. This is a conservative
schema bound, not an implemented release revocation service. Re-signing old bytes
does not prove security currency. Repository snapshot expiry, package-policy changes,
builder/schema changes and revocation/release eligibility still need operational
policy. Authentic, structurally valid and supported-for-migration are distinct.

## Machine-neutral filesystem

The verifier requires empty `/etc/machine-id` and `/etc/fstab`, a D-Bus machine-id
link to `/etc/machine-id`, and resolver link to `/run/NetworkManager/resolv.conf`.
An empty machine-id permits generation without implying the missing-file first-boot
condition; see [systemd's documented distinction](https://manpages.debian.org/trixie/systemd/machine-id.5.en.html).
The dedicated worker uses explicit generation/receipt state, not ConditionFirstBoot.

Reject persistent hostname, SSH host keys, reusable password hashes (even locked
hashes), NetworkManager connection/state secrets, root SSH material, user home
content, random seed, migration generation/agent receipts, build policy-rc.d, temporary
os-prober policy, populated logs, factory apt sources and machine-specific grub.cfg.
Runtime `/dev`, `/proc`, `/sys`, `/run`, `/tmp` and `/boot/efi` have no children.
Package locks, triggers, alternatives/diversions and dpkg info are retained in the
exact manifest; nonempty dpkg updates/Unincorp or pending/awaited triggers reject.
The reviewed versioned neutralizer operates only on a derived factory root. No
blanket cleanup command was added. The original raw audit found:

- `/etc/machine-id` already empty, but fstab still has the base-system placeholder;
  the required D-Bus and resolver links have not been created.
- `/etc/hostname`, `/etc/mailname`, Exim configuration, generated Exim state and
  both current/backup debconf databases contain `igloo-factory`.
- `ssl-cert` generated a private snakeoil TLS key/certificate. Its authenticated
  postinst calls the supported `make-ssl-cert generate-default-snakeoil` primitive.
  A generic artifact must not reuse this key. Removing it alone is insufficient:
  target-time generation and dependent service readiness need an explicit contract.
- Eight character devices and four `/dev` links, `/dev/pts`, `/dev/shm` and `/run/lock`
  scaffolding remain from bootstrap setup. No device/FIFO/socket is importable.
- A file-only factory apt source and populated fontconfig log remain.

The original read-only publication gate reported `SpecialFileRejected`,
`ReadbackSpecialFile`, and `MachineIdentityResidue`. The source is retained unchanged.
The derived v2 root now passes the gate, whole-tree private-key/identity audit and
structured account/debconf checks. Its exact delta and regeneration lifecycle are
documented in the [neutralization report](debian-configured-root-neutralization.md).
The verifier additionally rejects snakeoil/private keys, mailname and the observed
hidden factory hostname in Exim/debconf state. This is detection, not a qualified
generic cleanup implementation. No stream or successful attestation is produced.

Next neutralization work must use an exact observed plan, durable intent, supported
package configuration interfaces and independent readback. It must define Exim/
mailname and TLS identity regeneration on the target; package state must still
verify afterward. No direct editing of dpkg status or fabricated configured state.

Host name, locale/timezone, actual users/credentials, stable fstab, network profiles,
target identity, initramfs, exact worker payload and user-data/enrollment evidence
remain migration-specific. The legacy broad agent is never selected.

## Semantic format and importer mechanics

Candidate format `igloo-semantic-root-stream-v1` is uncompressed:
`IGLOO-SEMANTIC-ROOT-V1\n`, then the bytes of regular files in manifest path order.
There are no archive filenames, implicit metadata, compression bombs, offsets or
tar extension headers. The independent manifest describes all metadata and exact
lengths/hashes; stream size, each file and entire stream are checked before intent.
This costs transfer/storage space but keeps v1 parsing bounded and inspectable.
No identical-build-byte reproducibility claim is made.

`native/configured_root.py` implements:

- Strict JSON duplicate/unknown fields, canonical unique entry order, build ID,
  bounds, normalized absolute POSIX manifest paths. Stream data never selects a
  destination. Metadata schema 1 keeps its original ASCII restrictions. Schema 2
  permits only the two observed NetLock certificate Unicode paths and two literal
  systemd `system-systemd\x2d{cryptsetup,veritysetup}.slice` paths, bound to inspected
  ca-certificates/systemd-cryptsetup versions. No Unicode normalization, backslash
  decoding or general path-encoding relaxation is performed.
- Explicit directories/files/symlinks/hardlinks with UID/GID/mode, content hashes
  and xattrs. All parents must be directories; no entry may be placed below a link.
  Merged-/usr links are created last. Links can point within semantic root, including
  intentional runtime resolver targets; escaping, cyclic and magic device/proc/sys/
  ESP links reject. Schema 2 explicitly supports the seven inspected systemd
  service masks to `/dev/null` and the exact `/etc/mtab -> ../proc/self/mounts` link.
  They are link objects only; no imported link is traversed during extraction.
- Hardlinks only to declared regular files, both inside the root and newly imported;
  matching metadata, no xattr-bearing hardlink groups in v1, exact final inode/link
  count. No link to existing scaffolding or another filesystem.
- Explicit `user.*` xattrs, plus only the reviewed revision-2 effective CAP_NET_RAW
  value on root-owned mode-0755 `/usr/bin/ping` in schema 1. The actual tree instead
  has one capability on GStreamer's PTP helper: NET_BIND_SERVICE, NET_ADMIN and
  SYS_NICE, effective/permitted, matching `libgstreamer1.0-0 1.26.2-2` postinst.
  Schema 2 requires that exact path, root owner/group, 0755, length, file hash,
  capability bytes and package version. Unknown capabilities remain blocked.
- Schema 2 permits only the observed `/var/log/journal` access/default ACL pair,
  mode 02755, root:systemd-journal (gid 997), adm gid 4, matching systemd 257.13's
  tmpfiles policy. Exact ACL bytes and group database identities are checked.
  Directories receive ACLs last, so imported children cannot inherit undeclared
  ACLs. Arbitrary ACLs, security.* and trusted.* remain rejected. Native fixture
  readback verifies both ACLs and rejection after changing the adm entry.
  An explicit root-only temporary-file
  fixture successfully restored and independently read the exact ping capability;
  its file contained inert fixture data and was never executed. Declared set-ID
  modes are retained and require publisher review. The real audit found 32 such
  files/directories and 10 hardlink groups, no sparse files, FIFOs, sockets or block
  devices. The complete neutral real tree validates under schema 2 without adding
  broader metadata permissions.
- Block/character devices, FIFOs and sockets reject entirely. Runtime device nodes
  belong to the existing broker, not the artifact. Mountpoints cannot carry payload.
- FD-relative openat2 BENEATH/NO_SYMLINKS/NO_XDEV for children; create-new files only;
  root inode/device/mount-ID/connected-ancestry checks; reject child mounts and
  replacement. No arbitrary destination CLI, no shell extraction or target scripts.
- Before-state must be empty or exactly empty root-owned mode-0700 `lost+found`
  with no xattrs, separately authorized by a future formatted-root/session receipt.
  No existing application file is overwritten. Capacity check includes conservative
  data/metadata reserve; it is not an ENOSPC guarantee or filesystem ownership proof.
- File/directory fsync, then fresh FD tree readback: exact file set/type/owners/modes,
  hashes/links/link counts/xattrs; independent dpkg-query against the imported admin
  directory without chroot or scripts; no pending triggers; machine-neutral checks.
  Package database alternatives/diversions are byte-bound by the filesystem manifest.
  Factory dpkg audit and dependency verification remain separate requirements.

`pack_verified_fixture` independently validates a tree and packs its regular bytes
to a new external file. Tests independently reopen the stream and inspect the
import in a fresh Python/dpkg-query process. The latest continuation also packs and
independently verifies the real neutralized root. The untouched raw root is still
not publishable; the revised derived artifact is the separate import-rehearsal input.

## Session integration and durable failure states

The production entry must ultimately derive a root FD exclusively from the active
canonical `InstallerBlockLeaseSet`/`DebianNativeMountSession`, revalidate the whole
layout and formatted-root receipt, supply an empty connected root-only view, and
keep ESP/payload read-only. It must compare preserved Windows state before/after.
Those session-view/real-GPT qualification gaps predate this importer and remain.
`DebianConfiguredRootStages.ImportSupport(session, leases)` always returns Unsupported.
There is no path overload, manual resolution flag or fixture enable switch.

`ImportJournal` uses the existing Linux deployment journal primitive in a separate
store. Fixture sequence: source verified -> root before-state -> create-new generation
reservation -> durable intent -> independently reopened intent -> bounded progress
checkpoints -> file/directory fsync -> full readback -> durable/reopened result.
Evidence binds target generation, plan hash, artifact build and content hashes.
Only `AppliedAndVerified` after all checks can describe fixture mechanics success;
it is neither Debian installation completion nor production authorization.

Failure with independently observed partial state is Failed. Observer failure is
OutcomeUnknown. A crash, fsync/result-checkpoint failure or supervisor loss leaves
the durable intent without a verified result. Exceptions never mean no bytes were
written. No cleanup erases evidence and no re-import resumes a reserved generation
or nonempty root. Recovery requires explicit review and later authorized recreation/
reformat of the **owned** root under a new preparation receipt/generation. This task
implements neither rollback nor that destructive recovery operation.

## Revised responsibility/support matrix

The v1 43-stage enum and old receipts are unchanged. Candidate responsibility mapping
is implemented separately; Bootstrap's future replacement is ImportConfiguredRoot,
not a successful debootstrap stage. Artifact readback does not auto-promote any stage.
NativeSupported = **0**; existing implementation status remains 32 PartiallyImplemented,
2 Unsupported, 9 FirmwareDeferred.

| # | Existing stage | Candidate responsibility | Current support |
| --- | --- | --- | --- |
| 1 | ValidateOwnership | Target | Partial |
| 2 | ResolveRuntimeDevices | Target | Partial |
| 3 | PrepareNamespace | Target | Partial |
| 4 | MountRoot | Target | Partial |
| 5 | MountLinuxEsp | Target | Partial |
| 6 | MountPayload | Target | Partial |
| 7 | ExcludeWindowsEsp | Target | Partial |
| 8 | VerifySourceTrust | Target artifact authentication/source qualification | Partial |
| 9 | ObserveFirmwareBeforePackages | Firmware boundary | FirmwareDeferred |
| 10 | Bootstrap | Replace with ImportConfiguredRoot in a new strategy | Unsupported |
| 11 | ConfigureApt | Target installed-system sources; no deployment network | Partial |
| 12 | ConfigureArchiveKeyring | Artifact readback | Unsupported |
| 13 | MountHelpers | Target | Partial |
| 14 | ConfigurePackagePolicy | Artifact readback | Partial |
| 15 | ConfigureHostname | Target | Partial |
| 16 | ConfigureLocale | Target | Partial |
| 17 | ConfigureTimezoneKeyboard | Target | Partial |
| 18 | InitializeMachineIdentity | Target | Partial |
| 19 | ConfigureNetwork | Target | Partial |
| 20 | GenerateFstab | Target | Partial |
| 21 | ConfigureUser | Target protected credentials | Partial |
| 22 | ConfigureSudo | Target | Partial |
| 23 | InstallKernel | Artifact package/kernel readback | Partial |
| 24 | InstallFirmware | Artifact package/hardware-policy readback | Partial |
| 25 | InstallDesktop | Artifact configured-product readback | Partial |
| 26 | InstallAgent | Target exact payload/generation | Partial |
| 27 | ConfigureSignedPackages | Signed package qualification/finalization boundary | FirmwareDeferred |
| 28 | DrainPackageTriggers | Signed-trigger boundary; generic artifact triggers must already be clean | FirmwareDeferred |
| 29 | FinalizeLoaderFiles | Exact ESP finalization | FirmwareDeferred |
| 30 | GenerateGrubConfiguration | Exact target boot references | FirmwareDeferred |
| 31 | FinalizeMachineIdentity | Target | Partial |
| 32 | GenerateInitramfs | Target hardware/kernel qualification | Partial |
| 33 | InspectBootFiles | Signed boot boundary | FirmwareDeferred |
| 34 | ObserveFirmwareBeforeFinalization | Firmware boundary | FirmwareDeferred |
| 35 | FinalizeFirmware | Firmware boundary | FirmwareDeferred |
| 36 | InspectFirmwareAfterFinalization | Firmware boundary | FirmwareDeferred |
| 37 | VerifyWindowsPreservation | Target | Partial |
| 38 | PersistDeploymentEvidence | Target | Partial |
| 39 | UnmountHelpers | Target | Partial |
| 40 | UnmountLinuxEsp | Target | Partial |
| 41 | UnmountPayload | Target | Partial |
| 42 | UnmountRoot | Target | Partial |
| 43 | PersistCompletionEvidence | Target | Partial |

## Remaining acceptance work

1. Provision external release signing/pinning, revocation/expiry and publisher/verifier
   separation. No private key belongs in this repository.
2. Join authenticated artifacts to the canonical production root-only session view,
   leases, mount observers and stage journal; qualify real GPT/EXT4/FAT32 behavior.
3. Qualify target configuration/initramfs, exact agent/user-data/enrollment producers
   and full semantic observers. Fixtures do not close these existing gaps.
4. Only then qualify signed-loader/ESP/NVRAM finalization and the disposable VMware
   lifecycle. No firmware writes or bootability claims occur in this continuation.

Secure Boot/NVRAM and VM validation are **not** the only remaining blockers.

## Validation for this continuation

Each targeted .NET project ran with `--no-restore -m:1 -warnaserror`:

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 353 | 0 | 0 |
| Preflight | 343 | 0 | 0 |
| Community.App | 58 | 0 | 0 |
| Migration | 360 | 0 | 0 |
| Debian filter (subset of Migration) | 329 | 0 | 0 |
| Configured-root .NET filter (subset) | 35 | 0 | 0 |
| Full `Igloo.sln --no-build --no-restore -m:1` | 1,303 | 0 | 0 |
| Linux unittest discovery | 253 | 0 | 0 |
| Explicit native broker fixtures | 44 | 0 | 0 |
| Explicit native artifact capability/ACL fixtures | 2 | 0 | 0 |
| Revised real-metadata failure fixtures | 18 | 0 | 0 |
| Complete real stream/manifest rejection checks | 3 | 0 | 0 |

This continuation adds 9 .NET derivation-binding cases and 17 deterministic
neutralization cases. Existing policy/source/metadata/adversarial cases remain
included. Counts for filters overlap and must not be added to their parent suites.
`dotnet build -warnaserror`: zero warnings/errors.
`git diff --check`: exit 0; 28 LF/CRLF conversion notices are separate from failures.
Changed/new files also receive a direct trailing-whitespace check because Git's
diff check does not inspect untracked files.

Runtime fixture coverage includes deterministic stream generation/reopen, real
file/xattr/capability writes, merged-/usr objects, hardlink identity/counts, a fresh
Python/dpkg-query observer, no writes to an outside sentinel, archive/manifest
corruption, traversal/magic/cyclic links, device/FIFO/socket rejection, root/mount
substitution, stale generation, package/credential residue, preexisting/unexpected
files, interrupted file/import, observer failure and journal fsync/result failure.
Mount substitution is an injected fixture here; real canonical GPT mount/import
integration is still outstanding. The existing real configured root was reused;
no factory rebuild occurred. No developer storage, ESP, firmware, BCD or RTC was
modified, and the developer machine was not rebooted. The factory guest is separate
from the required disposable VMware migration/boot/recovery acceptance tests.
