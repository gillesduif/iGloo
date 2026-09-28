# Debian bootstrap primitive decision — Community #241

The subsequent [configured-root artifact continuation](debian-configured-root-artifact.md)
selects a separate mmdebstrap factory VM, development artifact contracts and semantic
import fixtures. A reviewed wsdd exception now permits the real 1,594-package GNOME
build; independent package state verifies, but factory neutralization/publication
remains blocked. This does not reopen any on-target approach below. Earlier closure
rejections in this decision remain historical evidence.

2026-09-28: **BLOCKED — no replacement bootstrap primitive is qualified.**

Stop adapting stock debootstrap to the package sandbox. The comparison below
does not select a new production primitive or grant bootstrap a package profile.
An authenticated, configured base-filesystem artifact is a candidate for a
separate implementation; its missing contracts prevent acceptance today.

This decision preserves the [canonical session](debian-session-boundary.md),
[isolation boundary](debian-isolation-boundary.md), PreparedLayout, exact leases,
RecoverySnapshotV1, Exact, RecoveryReadiness and PreCommitGate. No preparation,
registration, firmware finalization or Debian deployment is enabled.

## Evidence inspected

The current source, tests, uncommitted Debian implementation and session documents
were inspected before this decision. The following actual Trixie packages were
downloaded into a fresh WSL temporary audit directory and extracted **without
installation or maintainer-script execution**:

| Package | Version inspected |
| --- | --- |
| debootstrap | 1.0.141 |
| mmdebstrap | 1.5.7-1+deb13u1 |
| cdebootstrap | 0.7.9+b13 |
| live-build | 1:20250505+deb13u1 |
| debuerreotype | 0.15-1.1 |

Acquisition independently repeated gpgv verification using the separately trusted
archive keyring and allowed fingerprints, checked the retained InRelease's
codename/architecture/date policy, verified the signed Packages.xz length/hash,
and then checked each downloaded .deb length/SHA-256. This is the existing
authenticated Trixie snapshot, not a newly solved workstation bundle. Exact
repository, package and extracted-script identities are retained in
[debian-bootstrap-evidence.json](debian-bootstrap-evidence.json).

The mmdebstrap script hash is
`C8A55D9731E81C00A78DB8824854A099ADBDB516F83F88B6D546D4D17B03EA1A`.
The binary package's script/POD was inspected, not a guessed newer upstream
implementation. cdebootstrap's matching 0.7.9 source was also read from Debian
Sources; the evidence file distinguishes HTTPS source provenance from the signed
binary-package chain. No bootstrap binary, extraction of a target root, dpkg
installation, APT installation, disk image, loop mapping or physical block mount
was executed in this comparison.

## Required execution boundary

Only the trusted supervisor holds canonical root/ESP/payload block descriptors
and performs exact mounts. Package and configuration children have no raw block
FDs/nodes, no mount/chroot/namespace creation, no MKNOD/SYS_ADMIN/SYS_CHROOT,
no firmware interface, no external network and no host service sockets. Their
ESP/payload views are read-only. The declared root is the only installed-system
write target. Fresh external observation, reopened durable intent, restricted
execution, semantic readback and a durable result are all required.

A bootstrap is not qualified by running UID 0, returning zero, successfully
extracting files, having a Debian package signature, or matching a tar hash.
Its setup-to-maintainer-script transition must preserve the same constraints.

## Comparison

| Primitive | Result for the current session | Reason |
| --- | --- | --- |
| Stock debootstrap, including foreign/second-stage | Rejected as deployment primitive | Both phases require the audited mknod/bind sanity check; second stage performs proc/device setup before dpkg. No accepted public privilege transition. |
| mmdebstrap root/sudo | Not qualified | Supported mount skipping exists, but configured installation still constructs a `chroot` command and invokes dpkg through it. The current package profile denies that syscall and capability. |
| mmdebstrap unshare | Rejected as a drop-in | Its own namespace setup and UID/GID mapping are not the canonical session. Target processes cannot create namespaces or receive their setup privileges. |
| mmdebstrap fakechroot/fakeroot | Rejected | Library interposition is not kernel containment, and correct installed ownership/package behavior is not established. |
| mmdebstrap chrootless | Not qualified | Scripts run outside the destination using DPKG_ROOT conventions. Complete essential/workstation compatibility and correct target-only effects are not proven. |
| mmdebstrap extract-only | Insufficient alone | Produces extracted files, not configured packages. A separately supported configuration transition and complete dpkg readback are still required. |
| Authenticated configured root-filesystem artifact | Candidate, not accepted | Avoids running bootstrap helpers on the migration target, but no qualified artifact, builder attestation, safe importer or package-state transition exists here. |
| Official cloud/live/container image as a direct substitute | Not accepted | Image authenticity and product/deployment equivalence are separate. No reviewed image-to-owned-root conversion satisfies the current receipt, package and identity contracts. Whole-disk deployment would violate PreparedLayout. |
| cdebootstrap | Rejected as a drop-in | The inspected implementation performs namespace/mount setup and executes target commands through chroot; changing frontend does not supply the missing restricted execution transition. |
| live-build / debuerreotype | Rejected as on-target replacements | The inspected implementations invoke debootstrap and additional chroot/mount operations. They are build tools, not adapters to iGloo's package executor. |

### Stock debootstrap

The [documented foreign/second-stage split](https://manpages.debian.org/trixie/debootstrap/debootstrap.8.en.html)
does not suppress device/proc setup. Authenticated 1.0.141 source locations are
recorded in the previous session document: main lines 618–629,
`functions` 1845–1894 and 1256–1305, and `scripts/debian-common` 132–187.
Preparing helpers beforehand does not remove the sanity check. No fabricated
container flag, helper patch, fake mount success, tolerated setup failure or
privilege grant to maintainer scripts is accepted.

The old candidate `BootstrapCommand` generator has been removed.
`DebianNativeInstructions.Command(Bootstrap)` now explicitly rejects with
`DebianBootstrapPrimitiveUnqualified`. Offline `--print-debs`/download-only
acquisition remains separate; retaining its package-selection evidence does not
authorize executing the bootstrap helper against a target.

### mmdebstrap: supported controls, but no complete transition

The [Trixie public interface](https://manpages.debian.org/trixie/mmdebstrap/mmdebstrap.1.en.html)
supports mount skipping, APT-based selection, explicit local mirrors and archive
output. Recommends must be enabled explicitly for iGloo; Suggests remain disabled.
Neither defaults nor extraction success establish our package policy.

The authenticated 1.5.7-1+deb13u1 source provides more precise evidence than a
blanket claim that mmdebstrap always requires SYS_ADMIN:

- Lines 6066–6119 test mounting capability and can set `canmount=0`.
- Lines 2244–2306, 2411–2415 and 2486–2490 honor mount-skip choices. Supplying
  supervisor-owned pseudo-filesystems can therefore address that part of setup.
- Lines 3824–3826 still construct `env ... chroot <target>` for root/unshare/
  fakechroot. Lines 3963–3984 use it for dpkg installation. Public mount skipping
  does not replace that command with a broker callback or drop privilege at each
  maintainer-script boundary.
- Lines 2970–2985 bypass prepare/essential/install for the extract variant. Its
  output is deliberately unconfigured; it is not a successful Bootstrap receipt.
- Lines 3916–3962 use `--force-script-chrootless` with an alternate dpkg root.
  Running that mode does not prove every script honors that root. The upstream
  package's own POD warns about incomplete support and unintended outer-root
  effects. No `--skip=check/chrootless` or forged environment is accepted.

No PATH replacement of `chroot`, upstream patch, copy of mmdebstrap's private
essential-package loop, blanket `--force-depends` installation, or target `/`
special case is implemented. A future extract-plus-configuration proposal would
need its own complete supported package-order/state proof; it is not implicit in
the extract interface. The existing kernel filter continues to reject chroot and
character-device mknod in both package and configuration profiles.

### Authenticated base-image route

This route can remove bootstrap-time setup from the physical migration runtime,
but has **not** met the user's acceptance rule. The following are concrete missing
pieces, not implied properties of tar/SquashFS:

1. A bounded build environment using an unmodified Debian-supported builder;
   package code must not gain access to build-host disks, sockets or firmware.
   Builder privileges cannot simply be inherited by deployment package children.
2. A complete authenticated offline solution, pinned tool/runtime identities,
   package versions and configured dpkg state, including triggers/diversions and
   the selected workstation policy. Mutable mirror access during deployment is
   forbidden. The retained GNOME `gvfs-backends -> wsdd` blocker is unchanged.
3. Independently trusted artifact provenance/signature and a manifest binding
   release, architecture, package policy, source metadata, package closure,
   builder/tool versions and content. Debian signs repository inputs; that does
   not sign an iGloo-produced filesystem. An embedded self-declared hash is not
   the required trust anchor. No new signing key/policy is silently introduced.
4. A semantic filesystem importer operating only beneath the pinned owned root:
   reject escapes, aliasing hardlinks, device nodes, sockets, unexpected mounts,
   unsafe xattrs and prohibited destinations. Handle Debian's legitimate merged
   `/usr` links, owners/modes/capabilities and configuration files deliberately.
   The current small-file openat2 writer intentionally refuses symlink traversal;
   it is not an implemented OS-image extractor. ESP and payload stay read-only.
5. Empty/expected before-state, durable import intent, independent reopen and full
   filesystem/package readback. Partial extraction remains partial/unknown, never
   retryable just because no final marker exists. Bind artifact evidence to the
   exact PreparedLayout generation without substituting filesystem UUIDs.
6. Reset installer machine identity, configure fstab/accounts/source policy, and
   validate later restricted APT operations and semantic observers. A base image
   does not resolve user-data/agent producers or signed-loader/firmware stages.

Official alternatives do not fill these gaps automatically. The inspected
[cloud catalog](https://cloud.debian.org/images/cloud/trixie/latest/) offers
generic, genericcloud and nocloud disk artifacts and a checksum list; it did not
list a detached checksum signature. The corresponding
[cloud README](https://cloud.debian.org/images/cloud/) distinguishes current
TLS/checksum distribution from historical signed images and documents different
cloud-init/nocloud behavior. This is a catalog observation, not a claim that all
Debian images lack signatures. No cloud image was downloaded or authenticated.

[Debian installation/live image verification](https://www.debian.org/CD/verify)
does provide a signature workflow. A verified live image would still require its
[live-system behavior](https://live-team.pages.debian.net/live-manual/html/live-manual/the-basics.en.html)
to be converted to the intended installed workstation and independently verified.
It is not equivalent to the existing OfflineDebianPackageSet or a configured-root
receipt. We did not import a cloud disk, container rootfs or live SquashFS.

### Other Debian mechanisms

The inspected [cdebootstrap 0.7.9 source](https://sources.debian.org/src/cdebootstrap/0.7.9/src/)
has `install_newns`/`install_mount` in `install.c` (330–370) and target execution
via `di_exec_prepare_chroot` in `execute.c` (230–249). Its documented foreign mode
is not an iGloo restricted-execution callback. No alternate helper/configuration
recipe was invented to bypass those operations.

The authenticated live-build `bootstrap_debootstrap` calls debootstrap at lines
94–122. Debuerreotype's `debuerreotype-init` uses unshare/debootstrap at 134–140;
`debuerreotype-chroot` uses recursive host binds and chroot at 26–37. Both may be
inputs to a separately qualified artifact factory; neither is an accepted
on-target bootstrap. `dpkg-deb --extract` is also not an installer: its
[public contract](https://manpages.debian.org/trixie/dpkg/dpkg-deb.1.en.html)
distinguishes archive extraction from package installation/configuration.

## Integration and remaining blockers

There is no selected production replacement, no new source schema and no new
execution profile. The existing isolation/session model and failure semantics
are unchanged. No caller can obtain bootstrap execution by presenting a signed
bundle, a qualified tool hash, relabeling a bootstrap command as configuration,
or substituting an archive importer as a desktop operation.

**New NativeSupported stages: zero.** All 43 classifications in the canonical
session document remain unchanged, including Bootstrap = Unsupported. In addition
to this primitive decision, production still requires:

- Qualified root-only connected child view, real GPT/EXT4/FAT32 mount/session
  integration, helper-contract reconciliation and complete runtime identities.
- A complete IDebianIsolatedStageHost, semantic observers and stage-journal
  composition, protected credential transport and first-boot receipt producers.
- Complete authenticated offline workstation closure; no Recommends relaxation.
- Signed-loader/package-hook/NVRAM qualification and disposable VM lifecycle
  evidence, both outside this continuation.

The next bootstrap-specific prerequisite is to qualify an authenticated configured
root artifact and its supported safe import/package transition, or obtain a
supported upstream primitive that delegates target execution to the existing
broker. Neither direction may change the containment contract to obtain success.

## Validation

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 353 | 0 | 0 |
| Preflight | 343 | 0 | 0 |
| Community.App | 58 | 0 | 0 |
| Migration | 300 | 0 | 0 |
| Debian filter (Migration subset) | 269 | 0 | 0 |
| Linux deterministic/native primitives | 132 | 0 | 0 |
| Explicit native broker/session/tmpfs fixtures | 44 | 0 | 0 |
| Full serialized solution | 1,243 | 0 | 0 |

Targeted .NET runs used `--no-restore -m:1 -warnaserror`; the full solution used
`--no-build --no-restore -m:1`. `dotnet build -warnaserror`: **0 warnings, 0 errors**.
Linux deterministic discovery used `python3 -B -m unittest discover -s
tests/installer -p test_*.py`; the explicit `broker_rehearsal.py` run used the
previously authenticated bubblewrap binary and fresh disposable directory/tmpfs
fixtures. These counts add **7 .NET and 4 explicit native tests**, with one old
candidate-command test corrected to assert rejection rather than an executable
debootstrap declaration.

New native probes independently verified containment before target release, then
received EPERM for chroot and character-device mknod in both profiles. This is
syscall-policy evidence, not a run of debootstrap/mmdebstrap or real package
configuration. All temporary broker fixture paths were independently verified
absent afterward; the two prior WSL placeholders also remain absent. Retained
package-audit downloads are evidence artifacts, not mounted/deployed roots.

No analyzer suppression, privilege change, upstream patch, package installation,
physical-disk operation, firmware/BCD/RTC mutation, reboot, commit or push.
`git diff --check`: **exit 0**; 28 existing LF/CRLF notices are separate from
whitespace failures. All files edited in this continuation, including untracked
source/tests/documents, also pass a trailing-whitespace check.
