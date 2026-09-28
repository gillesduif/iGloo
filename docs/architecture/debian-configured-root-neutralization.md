# Debian configured-root neutralization and import — Community #241

This continuation uses the existing real 1,594-package Trixie GNOME root. It does
not rebuild Debian or run a bootstrap tool on the migration target. Production
authentication and `DebianConfiguredRootStages.ImportSupport` remain Unsupported.
NativeSupported remains **0**. Windows preparation, registration, firmware writes,
RecoverySnapshotV1, Exact, RecoveryReadiness and PreCommitGate are unchanged.

Status: **DEBIAN CONFIGURED-ROOT ARTIFACT QUALIFIED** — development artifact and
import mechanics only. Current derivation: `6072529e-1484-4fc7-a68c-d05c970bb393`.
Neutralization, packing, strict descriptor reopen, fresh independent verification,
real disposable EXT4 import, filesystem/dpkg equality and exact teardown passed.
This is not production authentication, a NativeSupported stage or boot validation.
The [retained observation export](debian-configured-root-neutralization-evidence.json)
contains the actual plan, source preservation, verification, import, teardown,
failure-injection and regeneration evidence. Its SHA-256 is
`D38FAE0A29E58190EB00D7F163478D35CB5711E42C1A7105F480408E1AC23FD3`.

| Evidence | Observed identity |
| --- | --- |
| Plan | `debian-trixie-neutralization-2026-09-28-v2`; 33 nominated object operations plus the supported debconf change and exact backup removal |
| Plan SHA-256 | `FEADA00CF8AAC4995B70EC9E86C42FCBDB411CEC78C8CDF3B738A0FAECA42653` |
| Manifest | `artifact/root.manifest.json`; 144,911 entries; 30,779,391 bytes |
| Manifest SHA-256 | `5BE0814960F7D87B3FB3379688D60873028921D4D8F0BF504DD4C0EB6AE3B9B2` |
| Content stream | `artifact/root.content`; 4,641,457,180 bytes |
| Stream SHA-256 | `EDE9ADCC24597B82389CE7C73D38A02A3AB077DA53C132EE9704FEC71BC705F5` |
| Descriptor SHA-256 | `4936F2FBC669253A93CB9004D3EF4E268B995AC30479CBEF1C1E83F5B8234A20` |
| Semantic filesystem SHA-256 | `1F842DC9D53C5C57B2CD9D77FE5731C29E777C4599C445CC8A3892222687BF13` |
| Package-state SHA-256 | `FE0F441041764FEF253382BCD6BD93933CF4EF63E8F086B10D93363B2B44719B` |
| Fresh verifier | `3CB7999F7E1B0EEBDD61653B9894BCC6AADA6CE63016E0CBBF21C57BE5EE2BF3`; ArtifactValid, DevelopmentImportOnly |
| Import generation | `fe0e0666-8d14-4816-b4b8-b72ba8628010` |
| Import plan SHA-256 | `648D25478EA1E26467115045C76EF962E5FBCF0C50F02D4D0F26C0DCF0C1899A` |
| Disposable EXT4 UUID | `bd992bee-11dd-475a-a08d-70b10578a2d8` |
| Import/teardown | AppliedAndVerified; exact filesystem/package equality; ordinary unmount; mount absent and exact loop detached |

Guest paths above are relative to
`/factory-neutral/6072529e-1484-4fc7-a68c-d05c970bb393/` in the retained derived
VM. Its host overlay is
`/var/lib/igloo/factory-evidence/99695826-82bc-4077-b4cf-a22cebc706da/neutralization-9f5101de-4b25-44d5-8ade-a18c4e371408/derived.qcow2`
in Ubuntu-24.04. The backing raw evidence is required to reopen this overlay;
do not mount or boot the raw backing writable.
The derived guest was powered off normally after verified import teardown. The
first 55-second shutdown observation timed out; a later fresh observation confirmed
QEMU exit, retained overlay and unchanged raw-source identity. No force termination
or developer-host reboot occurred. The host result is retained as
`guest-shutdown-result-v2.json` beside the overlay.

## Source preservation and derivation

The raw build is `99695826-82bc-4077-b4cf-a22cebc706da`. Its retained 80-GiB sparse
factory disk has SHA-256
`70DED1CA5E7211C99D4C4706D809B600CD7F97D3B07028BF463363862152C861`.
The complete raw filesystem/package observation has SHA-256
`A76733DB00ECD9FB0703B4F8D31B4ECE1D3274ADA07B7F89791039AFF2C6A579`.
The raw package-set file has SHA-256
`BE86797B73250B19569BC6A229EB1C13D22A651DEAEF315CE8C10406A5AD1272`.
These are evidence identities, not production authorization.

A new qcow2 overlay uses the original raw disk as a read-only backing file.
Independent host FD inspection confirms read-only backing access and no physical
block-device FD. The guest remains BIOS-only, offline, without a target ESP,
shared folders, storage passthrough or host service sockets. Only the guest's
disposable virtual disk and read-only authenticated input ISO are attached.
No developer partition, ESP, firmware, BCD or RTC operation is involved.

Before any root transformation, an exclusive derived directory is created by
`cp --archive --reflink=auto`. Reopened derivation intent binds the source build,
audit, package set and destination. Full source/derived inventories must agree on
all entries, hashes, ownership, modes, xattrs and hardlink groups. The original
root and raw observation remain available. Failure does not erase either copy.

The first neutralization candidate is retained as superseded evidence. A later
ownership audit found `/etc/apt/apt.conf.d/99mmdebstrap`, so the final plan is
**`debian-trixie-neutralization-2026-09-28-v2`**. A second fresh derivation removes
that exact observed build-policy file. The earlier candidate, descriptor, stream,
verification and rehearsal are not overwritten or promoted to final qualification.

## Exact neutralization

`configured_root_neutralization.py` produces a schema-1 immutable plan containing
each object's exact before/after state, operation, reason and target regeneration
responsibility. Runtime children are enumerated from the retained observation;
there is no recursive cleanup command, wildcard delete, package-state edit or
arbitrary destination. Files are FD-relative, no-follow/no-cross-mount, fsynced and
independently read back. Directory removal requires an empty nominated directory.

Every operation has a create-new durable intent, independent reopen, effect,
fresh package observation and result. The entire resulting tree must equal the
planned delta. All other entries and hardlink relationships must remain unchanged.

| Observed state | Exact neutral state / operation |
| --- | --- |
| `/etc/hostname`, `/etc/mailname` | Absent; no persistent factory hostname |
| Exim inputs | Empty `dc_other_hostnames`, false `dc_mailname_in_oh`; retain other local-delivery defaults |
| Exim generated configuration | Remove the exact observed generated object; regenerate for the target |
| Debconf current database | Supported `debconf-communicate SET` for the two Exim identity values and `FSET` for their mailname flag; all other stanzas must be identical |
| Debconf old database | Remove only the independently verified pre-change backup created by that supported operation; do not wipe debconf |
| Snakeoil key, certificate and certificate hash link | All absent; never clone machine-private TLS identity |
| `/etc/machine-id`, `/etc/fstab` | Empty regular files with observed ownership/modes |
| D-Bus machine-id | Symlink to `/etc/machine-id` |
| Resolver | Symlink to `/run/NetworkManager/resolv.conf`; no factory DNS contents |
| Factory APT sources and `99mmdebstrap` | Exact objects absent; archive keyring and package database retained |
| Fontconfig log | Exact observed log emptied; directory/default ownership retained |
| `/dev`, `/proc`, `/sys`, `/run`, `/tmp` children | Enumerated runtime nodes/links/scaffolding removed; no special file enters the artifact |
| Provisional initramfs and its aliases | Excluded; retained only in raw forensic evidence |

NetworkManager profiles, DHCP identity, SSH host keys, random seeds, target users,
enrollment and migration-generation state are prohibited. The observed root has
37 pinned system accounts. Root is exactly locked with `*`; each system account
has its reviewed marker, rather than accepting arbitrary locked password hashes.
Passwd/shadow backups, group credentials and debconf's password database receive
separate structured checks. Package-created defaults, generated Python/font/icon/
MIME caches, alternatives/diversions and empty package locks remain when unchanged
and manifest-bound. They are not removed merely because they were generated.

`dpkg-query` and `dpkg --audit` run in a fresh process after every transformation.
Exact expected versions and `install ok installed`, no pending/awaited triggers,
and no unexpected/missing package are required. Dpkg metadata is never edited to
conceal excluded machine-generated files. The full-tree delta check also protects
alternatives, diversions, authenticated signed-package contents and other state.

## Identity regeneration

Debian's supported [make-ssl-cert helper](https://manpages.debian.org/trixie/ssl-cert/make-ssl-cert.8.en.html)
and [Exim configuration generator](https://manpages.debian.org/trixie/exim4-config/update-exim4.conf.8.en.html)
were inspected from the installed packages. A factory-only rehearsal creates two
independent configuration instances under separate UTS/PID/network/IPC namespaces,
minimal `/dev`, private `/run` and `/tmp`, and a read-only neutral root. Only each
instance's configuration is writable. No user namespace is used: Debian system
UID/GID ownership must remain representable. The initial one-UID experiment failed
closed on ownership and is retained; it did not alter the target broker profile.

The successful helper test uses only CHOWN, DAC_OVERRIDE, FOWNER, SETUID and SETGID.
It writes each target hostname/mailname, uses supported debconf operations,
regenerates Exim configuration and runs `make-ssl-cert generate-default-snakeoil`.
Fresh OpenSSL/Exim observations require distinct keys/certificates, matching public
keys, correct subject/SAN, key root:ssl-cert 0640 and certificate root:root 0644.
Instance keys stay outside the artifact. This proves helper semantics in the
factory fixture, **not** a NativeSupported target configuration stage.

An [empty systemd machine-id](https://manpages.debian.org/trixie/systemd/machine-id.5.en.html)
permits a new per-instance ID but does not imply `ConditionFirstBoot`. The existing
restricted evidence worker retains its explicit generation/receipt semantics.

| Target responsibility | Operation and independent evidence | Completion rule |
| --- | --- | --- |
| Hostname/mailname/Exim | Validated target hostname/hosts and UTS; target mailname, debconf values, Exim generator; fresh file/debconf/Exim identity | Required before dependent services |
| TLS identity | Supported helper after target hostname; fresh key/cert relationship, subject, owner/mode and unique instance identity | Required before dependent TLS service readiness |
| Machine-id/D-Bus | Installed systemd creates instance ID; fresh ID/link equality | First-boot evidence pending until observed |
| Fstab | Exact root/ESP UUIDs from fresh canonical session; file/hash and mount relationship | No raw device-name authority |
| Resolver/network | Approved profiles and secret-safe mode/owner validation; NetworkManager supplies runtime resolver | No factory profiles or online deployment fallback |
| Final APT sources | Approved final Debian deb822/keyring configuration | Never a factory file URL or migration-time network dependency |
| User/sudo | Protected credential FD, exact user/group/sudo observations | No default password or credential in receipts |
| Initramfs | `update-initramfs -c -k 6.12.107+deb13-amd64` after target configuration; fresh kernel/modules/image correlation | Still unqualified under target broker; factory initramfs cannot satisfy evidence |
| Agent | Exact restricted worker/config/unit payload and enablement | InstalledFirstBootPending, not managed migration complete |
| UserData | Exact generation-bound producer, no broad disk/label discovery | Execution Unsupported |
| Enrollment | Explicit generation-bound product handoff | Execution Unsupported |

Every failure is Failed or OutcomeUnknown according to independent observation,
blocks target completion and permits no blind retry. These contracts neither add
firmware operations nor claim atomic rollback.

## Whole-tree and metadata verification

The fresh whole-tree audit scans regular files, compressed payloads and bounded
ASN.1 candidates for the exact factory identities, paths and known private-key
formats. It also checks structured account/debconf state. Evidence includes hashes
and classifications, never key/password bytes. This is not a claim to detect
arbitrary steganography. Signed input/package ownership and the exact generated
state delta complement the content scan.

Two public upstream test constants occur in authenticated package contents:
`libgnutls.so.30.40.3` and the IO::Socket::SSL `simulate_proxy.pl` example. Each
exception requires its exact whole-file hash and independent extraction from the
authenticated `.deb`; neither is an installed machine identity. Changed content
or an unknown private key blocks publication. The large fwupd compressed UI
resource has its own exact hash-bound scan limit, not a general size bypass.

A supplemental read-only structured PEM audit inspected 305 certificate instances.
None matches the original factory certificate's DER fingerprint or carries the
factory hostname in its decoded certificate. Exact imported-content equality also
binds this exclusion to the imported root; it is not a filename-only assertion.

Metadata schema 2 remains narrow: the observed Unicode CA paths, literal systemd
escaped names, `/dev/null` masks, merged-/usr links, hardlink groups, declared set-ID
entries, exact GStreamer capability and exact journal access/default ACL pair.
No new capability, arbitrary ACL, `security.*`, `trusted.*` or special-file policy
is added to make neutralization pass. All symlink objects are deliberate; no
archive-created symlink is traversed during file creation.

Observed neutral tree: 11,046 directories (excluding root), 116,571 regular-file
paths, 17,293 symlinks, 10 hardlink groups/13 aliases and 32 set-ID entries.
The manifest represents hardlink aliases separately, yielding 116,558 primary
regular files. It retains the two observed Unicode CA paths and literal systemd
escaped names. Exactly one file capability remains on
`/usr/lib/x86_64-linux-gnu/gstreamer1.0/gstreamer-1.0/gst-ptp-helper`:
`AQAAAgAUgAAAAAAAAAAAAAAAAAA=` (NET_BIND_SERVICE, NET_ADMIN, SYS_NICE).
The only other xattrs are the exact `/var/log/journal` access/default ACL pair.
There are no special nodes or sparse files, and no new schema allowance was needed.

## Artifact and import evidence

The real manifest includes every semantic entry in canonical order. The content
stream contains regular-file bytes only, in manifest order, with no archive paths
or hidden metadata. Reopened full stream hash/length and individual file hashes
are mandatory. Schema-2 attestation now requires neutralization derivation evidence:
source disk/observation, package set, plan/result, post-observation, whole-tree audit
and regeneration-contract hashes. Schema-1 history remains readable.

The .NET strict codec independently reopens the descriptor. A separately staged
fresh Python verifier checks the external development pin, validity, descriptor,
package set, attestation bindings, manifest, stream, tree, dpkg and neutral state.
Its frozen tool identity is recorded. This independent process shares the trusted
factory VM/kernel; it is not an external release-signing authority.

Development pins are supplied from outside the artifact and bind build, policy,
descriptor, manifest/content and a seven-day interval. **DEVELOPMENT / NON-PRODUCTION**.
ProductionAuthentication remains Unsupported; no private signing key is generated.

The import harness creates a new 12-GiB regular sparse file inside the disposable
VM. Formatting receives only the verified regular-file FD. A newly allocated loop
device must read back the exact file inode/device/length before use. A private
mount namespace mounts EXT4 `rw,nosuid,nodev,noexec`; UUID, major/minor, mount ID,
source, filesystem root, propagation and absence of stacked mounts are checked.
No caller-supplied device or developer physical partition is accepted.

Import requires the exact fresh-root state, reopened durable intent, FD-relative
create-new writes, per-file hashes/metadata and fsync, bounded progress checkpoints,
then independent filesystem/package/neutral readback. A fresh verifier compares
all paths, types, contents, UID/GID, modes, links/link counts, xattrs, ACLs and
capabilities. Only filesystem-created empty `lost+found` is excluded by policy.
Fresh dpkg inspection does not execute maintainer scripts. Unmount is exact,
ordinary (never lazy/force), followed by absence and loop-detach readback.

Failure-injection derivatives retain exact real status/capability/ACL/hardlink
metadata and bytes but are explicitly **incomplete test derivatives**, not another
workstation artifact. They cover corruption, stale pins/generations, insufficient
capacity, fsync/observer/interruption, nonempty root, target/mount substitution,
xattr/capability/ACL restoration and hardlink creation. Failed targets/journals stay
available. Observed partial import is Failed; unavailable readback is OutcomeUnknown.
No partial root is automatically reused or deleted.

The 18 real-metadata cases pass. Three additional tests use a separately copied,
hash-verified **complete** 4,641,457,180-byte stream and full manifest: byte
corruption, one-byte truncation and manifest corruption all reject before import
intent, leave their separate targets empty, and preserve the original stream.
This distinguishes complete-source rejection from the smaller metadata derivatives.

## Remaining production boundary

ImportMechanicsQualified is distinct from NativeSupported. The production root FD
must still originate from the real canonical session/lease, fresh whole-layout
identity and formatted-root receipt, with qualified GPT/EXT4/FAT32 mount evidence,
independent observer composition and the deployment stage journal. The fixture
cannot satisfy or bypass `ImportSupport(session, leases) == Unsupported`.

The real artifact can satisfy generic base/desktop/kernel/module/firmware/keyring
and signed-package **content** evidence after successful imported-root readback.
Target ownership/mounts/authentication/import, machine configuration, initramfs,
agent/UserData/Enrollment, Windows preservation, receipts and teardown remain
TargetRequired. Signed loader/ESP/GRUB/NVRAM remain FirmwareDeferred. The v1
43-stage history stays compatible; Bootstrap is not renamed into a successful
stage. The configured-root strategy uses the separate ImportConfiguredRoot concept.

Release signing, production session integration, target-specific finalization and
disposable VMware migration/boot/recovery validation remain later gates. The
generic Debian kernel/firmware baseline does not qualify arbitrary hardware,
proprietary NVIDIA/DKMS or MOK-requiring drivers.

## Validation and retained unsuccessful attempts

| Final validation run | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core, `--no-restore -m:1 -warnaserror` | 353 | 0 | 0 |
| Preflight, same flags | 343 | 0 | 0 |
| Community.App, same flags | 58 | 0 | 0 |
| Migration, same flags | 360 | 0 | 0 |
| Debian filter (overlapping subset) | 329 | 0 | 0 |
| Configured-root .NET filter (overlapping subset) | 35 | 0 | 0 |
| Full serialized solution, `--no-build --no-restore -m:1` | 1,303 | 0 | 0 |
| Linux deterministic discovery (includes 17 neutralization cases) | 253 | 0 | 0 |
| Explicit native broker | 44 | 0 | 0 |
| Explicit capability/ACL fixtures | 2 | 0 | 0 |
| Real metadata derivatives | 18 | 0 | 0 |
| Complete real source rejection checks | 3 | 0 | 0 |

The final real import and both independent TLS/Exim instances passed. Build has
zero warnings/errors. `git diff --check` exits 0; 28 LF/CRLF conversion notices are
separate. Changed/new files also pass direct trailing-whitespace inspection.

These are final-run counts, not a claim that every exploratory harness invocation
succeeded. Retained earlier attempts include the one-UID helper ownership failure,
an incorrect fixture assumption that `/boot/efi` exists in a no-ESP factory,
transport framing/path-length errors, a strict descriptor rejection of a local
timezone offset during export, and a native-suite invocation rejected before tests
because its explicit authenticated-bwrap environment was missing (0 tests, 1 setup
error). Corrected invocations used fresh fixtures or read-only revalidation. The
original source, superseded candidate and failed/unknown evidence were preserved.
