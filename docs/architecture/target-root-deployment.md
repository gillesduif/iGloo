# Community target-root deployment design — issue #241

This continues the [installation ownership checkpoint](community-installation-ownership.md).
**No complete Debian or Mint replacement engine is accepted or enabled.** Fedora's
storage recipe is frozen. Native Windows preparation, firmware registration and
automatic completion/reboot remain disabled. The new code verifies evidence; it
does not execute the deployment sequence below.

The subsequent [Debian-only deployment checkpoint](debian-target-root-deployment.md)
implements a candidate stage runner, configuration/policy contracts, ownership and
mount revalidation, detailed receipt production and failure/reopen tests. It has
no qualified native Linux deployment/isolation backend and remains **BLOCKED**.
The Debian content continuation adds an authenticated offline-bundle tool, typed
stage dispatcher, kernel evidence and Linux fsync journal; it does not qualify a
complete workstation bundle, isolation broker or replacement migration agent.
That checkpoint supersedes the
Debian candidate stage table below; the earlier cross-distro audit is retained.
The latest Debian-only non-firmware checkpoint additionally implements file and
credential primitives, independent readback pieces and the first-boot evidence
worker. Real archive acquisition found an unresolved GNOME recommendation and did
not produce a complete workstation bundle. No isolation/mount backend or real
deployment rehearsal is claimed; the production gate remains closed.

The subsequent [native isolation continuation](debian-isolation-boundary.md)
implements and fixture-tests a real restricted package child and exact native
mount mechanics. Canonical block-session integration, debootstrap privilege
separation and production host composition remain blocked. Ordinary directory and
tmpfs results are not actual GPT/ESP or Debian lifecycle qualification.

The [canonical mount-session continuation](debian-session-boundary.md) now joins
the existing resolver to descriptor-acquisition declarations and persistent native
mount RPC, with reopened session journal checkpoints. Real GPT mounts, the root-only
child view, complete stage host and bootstrap privilege split remain unqualified.

## Implemented common boundary

`InstallationOwnership.Resolve` still requires an unformatted fresh root.
`VerifyRootFormat` additionally requires that before-state, the explicit format
provider's partition/filesystem result and independent inventory readback.
`RootFileSystemReceiptV1` retains the same owned GPT partition, generation and
observed EXT4 filesystem UUID. `ResolveFormattedRoot` requires that receipt and
fresh whole-layout correlation. It does not reinterpret an existing arbitrary
filesystem as a successful format or implement an automatic retry.

`TargetRootMounts` declares generation-specific root, ESP and read-only payload
mount destinations. Its pure verifier joins fresh block-device major/minor numbers
to documented `/proc/self/mountinfo`, then to the existing canonical resolver. It
rejects wrong devices, wrong filesystem roots, stacked mounts, alternate binds,
unexpected target submounts, wrong read/write modes, parent mismatch, propagation
and symlink/resolved-path substitution. Windows ESP must be unmounted in this
namespace; other preserved partitions cannot be writable there. Enumeration and
mount-list order are not authority. The deterministic fstab uses root filesystem
UUID and the nominated ESP's FAT UUID; it mounts neither Windows nor OEMDRV.

This verifies the **core mounts before chroot helper mounts**. There is no native
mount executor, mount/stat/path acquisition adapter or capability sandbox yet.
The existing read-only GPT/filesystem collector remains separate from these new
mount observations. Its production packaging and physical hardware-ID equivalence
still need qualification. No passed pure check is a substitute for that work.

[Mount namespaces](https://man7.org/linux/man-pages/man7/mount_namespaces.7.html)
separate mount views, subject to propagation rules. They do not themselves prevent
a process opening raw block devices or accessing firmware. Likewise,
[chroot](https://man7.org/linux/man-pages/man2/chroot.2.html) is not a security
boundary. A future executor must restrict raw-device/firmware writes and remount
capabilities, supply only verified helper mounts, and verify that effective policy.
Simply hiding Windows' mountpoint while exposing all of `/dev`, `/sys` and `/run`
does not establish Windows preservation. No such shortcut was implemented.

## Debian: complete candidate sequence and explicit stop points

Debian's [documented bootstrap procedure](https://www.debian.org/releases/trixie/amd64/apds03.en.html)
supports creating a base system in an explicitly mounted target. It is the chosen
**candidate for further engineering**, not an accepted production replacement.
The current product's offline Trixie GNOME installation cannot silently become an
unbounded network bootstrap. A live squashfs is not a complete authenticated APT
repository. The pinned runtime, package/dependency closure, signed Release metadata
and offline transport must exist before deployment can start.

The intended lifecycle below specifies all requested stages. Commands name public
primitives; this table is not an executable script or claim of implementation.
`R`, `E`, and `P` mean independently verified owned root, Linux ESP and payload
mounts. Windows ESP is never E. All stages bind the reopened preparation generation.

| Stage | Inputs, target, contract and expected effects | Readback / failure boundary / rerun policy |
| --- | --- | --- |
| 1 Resolve layout | Durable prepared generation plus complete fresh inventory; shared resolver; no writes | Exact preserved/owned identities required. Missing/duplicate/changed data stops. Read-only retry is safe. |
| 2 Root filesystem and mount | Created root receipt, explicit format transition, independent EXT4 UUID receipt; mount that device as R | Format is a future native action, not implemented here. Unknown outcome stays uncertain; never reformat because a call failed. Verify mount source/root/path/mode. |
| 3 ESP mount | Existing FAT32 E by exact prepared GUID/UUID at R/boot/efi; mount only, no format | Verify parent, major/minor, full filesystem root, type and path. Missing or changed mount stops. Reobserve before a controlled mount retry. |
| 4 Exclude Windows | Preserved identities plus complete mounts and effective device/firmware access policy | Windows ESP unmounted; Windows filesystems need not be writable for any stage. Mount namespace alone is insufficient. Unknown exposure blocks package execution. |
| 5 Bootstrap | Authenticated pinned trixie source/keyring and verified P; debootstrap into R | Verify target dpkg/base state. Failure leaves partial R. No blind bootstrap rerun; resume requires package-state diagnosis and a recorded decision. |
| 6 APT sources | Signed, pinned trixie/updates/security source plan and archive keyring; write R/etc/apt | Verify exact files, signature checks and package closure; no unauthenticated fallback. Repeat only after comparing desired files and dpkg state. |
| 7 Kernel | Declared amd64 kernel/modules and firmware packages; apt/dpkg in R | Check package status, installed kernel and matching modules. Hooks may run; nonzero or uncertain outcome blocks progress. Repair is explicit, not rollback. |
| 8 Initramfs | Exact installed kernel version, target module tree and initramfs-tools | Generate and independently inspect/hash the target initramfs; reject running-installer kernel substitution. Retry only after validating modules/package state. |
| 9 fstab | Verified root UUID and E FAT UUID; deterministic common generator | Exact readback, no Windows/OEMDRV mount entries. A successful write without reopen is incomplete. |
| 10 Hostname | Validated intended hostname and hosts policy; target files only | Readback and syntax checks; preserve errors as incomplete. Deterministic rewrite requires target/mount revalidation. |
| 11 Network | Intended NetworkManager configuration and migrated profiles; R/etc with correct permissions | Check declared profile identities/permissions; never copy the installer host's resolver or machine credentials blindly. Live connectivity is not required for the offline product. |
| 12 Locale/timezone | Validated supported locale, keyboard and zone; locales/tzdata/console configuration in R | Verify generated locale and configuration. Interactive debconf prompts are not unattended success. Resolve incomplete package configuration before retry. |
| 13 Accounts/admin | Intended username, UID policy, password hash and groups; adduser/usermod/chpasswd interfaces in R | Verify account/shadow/group state without logging secrets. No default password or first-boot repair assumption. Partial account creation needs explicit reconciliation. |
| 14 Sudo | Declared admin membership and sudo policy in R | Validate sudoers syntax and intended access. Failure blocks completion; no unconditional root login fallback. |
| 15 Desktop | Pinned GNOME task/meta and required product packages in R | Compare actual dpkg package/version/configuration closure. A bootable base console is not the GNOME product. Package failure leaves partial deployment. |
| 16 Firmware/drivers | Approved target-hardware package set with complete offline availability | Kernel modules and driver signing must match the installed kernel. Unplanned DKMS/MOK enrollment cannot become an automatic success or disable verification. |
| 17 Signed packages | Audited shim-signed, shim-signed-common, signed GRUB and dependencies; constrained package environment | Check package state, alternatives, signatures and policy effects. GRUB no-nvram does not constrain all MOK hooks. Unbounded effects block this stage. |
| 18 Final loader files | Exact R and E mounts, fixed distributor directory, supported signed-mode grub-install with explicit EFI directory and no-nvram | Reopen shim/GRUB/config on E, compare content and compatibility. No removable-path fallback, force, unsigned chain or alternate ESP. Partial writes require recovery diagnosis. |
| 19 GRUB configuration | Declared installed kernel/initramfs/root UUID; controlled target /etc/grub.d and defaults | Disable os-prober for this isolation boundary; generate in R and inspect references. A hash alone does not establish working GRUB or bootability. |
| 20 Permanent firmware | Separate future explicit NVRAM plan after loader readiness; not a package-hook side effect | Record before/after raw variables, exact ESP/path and all permitted changes; verify independently. Error does not prove no write. No writer implemented in this task. |
| 21 Community agent | Verified generation-bound agent, migration manifest, configuration and service payload from P | Install and hash/read back exact R paths and service configuration before completion. No first-boot label scan or silent Fleet provisioning. Current agent package/boot side effects need further qualification. |
| 22 Evidence | Source, root/ESP, distro/strategy, stages, kernel/initramfs/loader/config/agent files and firmware witnesses | Persist durably, reopen, then fresh state verification. Failed/unreadable/unknown stage stays incomplete. Receipt integrity does not certify a distro engine. |
| 23 Unmount | Exact recorded mount IDs, in reverse dependency order; flush owned filesystems | Verify owned helper/ESP/source/root mounts are gone. Busy/unavailable/uncertain unmount blocks completion; do not use lazy/forced unmount as proof. |
| 24 Completion reboot | Supported profile, all preceding results, fresh permanent boot validation and durable evidence | No reboot API is supplied here. A present file/variable is not a bootability test. Failed or uncertain stages require product/manual recovery, never a successful completion screen. |

There is no transaction across these stages. Root files, packages, ESP files and
NVRAM can each be partly changed. An interruption cannot be represented as "nothing
happened". Automatic retry/rollback remains unimplemented. A failed preparation
format uses its preparation checkpoint; installation receipts start only once an
owned formatted root identity can be represented truthfully.

### Signed-package findings from actual Trixie artifacts

The previous audit's debootstrap 1.0.141, GRUB 2.12-9+deb13u2, signed GRUB
1+2.12+9+deb13u2 and shim 1.51~1+deb13u1+16.1-2~deb13u1 remain the inspected
candidates, not a pinned full package closure. Additional script hashes are in
[the evidence manifest](installer-esp-evidence.json). No maintainer script ran.

- The EFI branch of grub-efi-amd64 postinst uses GRUB_DISTRIBUTOR and existing
  `/boot/grub/x86_64-efi/core.efi` to decide whether to invoke grub-install. It reads
  update-nvram and extra-removable configuration. Its wrapper can log a failed
  installation without failing dpkg. The script also invokes Secure Boot policy.
- shim-signed postinst uses an existing `/boot/efi/EFI/<id>` and likewise can
  reinstall there. shim-signed-common configures/triggers `update-secureboot-policy`;
  shim declares an interest-noawait policy trigger. That policy can request MOK
  validation changes. Trigger draining and policy-state readback are mandatory.
- Signed GRUB in the inspected Debian package does not supply the Mint-style
  postinst. Do not copy Mint's hook assumptions into Debian.
- `update-grub` executes grub-mkconfig. The kernel postinst hook can invoke it if
  grub.cfg exists and the environment is not identified as a container. Neither
  "chroot" nor a skipped kernel hook proves that final configuration exists.
- The shipped `30_os-prober` exits on GRUB_DISABLE_OS_PROBER=true; otherwise its
  discovery is outside the intended root/ESP scope. The new candidate explicitly
  rejects inheriting the old template's broad OS discovery.

[Trixie's grub-install](https://manpages.debian.org/trixie/grub2-common/grub-install.8.en.html)
documents an explicit EFI directory, signed mode, and suppression of Boot-variable
updates. These support a separate finalizer, but do not prove arbitrary package
hooks harmless. [efibootmgr](https://manpages.debian.org/trixie/efibootmgr/efibootmgr.8.en.html)
distinguishes creation with BootOrder update from create-only and explicit order
updates. A future finalizer must bind all arguments to fresh identities and audit
raw results; default disk/partition values and implicit package registration are
not allowed. No efibootmgr command was executed or generated by this task.

A possible supported isolation direction is package installation without firmware
write access followed by an explicit finalizer with narrowly scoped access. It is
**not yet a proven Debian production sequence**: package behavior when access is
restricted, authenticated offline closure, trigger completion, signed-chain policy
and interruption handling still need implementation and qualification. Debian
remains **BLOCKED**, rather than enabling a partial bootstrap script.

## Mint: select no unsupported replacement

Actual source is the previously checksum-verified Mint 22.3 Cinnamon ISO. The audit
additionally read these ISO directory records (no mounts):

- `casper/filesystem.squashfs`: 2,633,056,256 bytes, ISO LBA 8486;
- `casper/vmlinuz`: 15,571,336 bytes, LBA 1334451;
- `casper/initrd.lz`: 82,518,477 bytes, LBA 1294158;
- `filesystem.manifest-remove`: SHA-256
  `82f08134d51095444fdcf061447e12c9d1203fa30e7f3c8004ee886b89418f0d`.

The shipped manifest identifies kernel 6.14.0-37-generic, initramfs-tools
0.142ubuntu25.5, mint-meta-cinnamon/core 2025.12.15+mint22.3, mintsystem 8.6.5,
ubuntu-system-adjustments 2025.12.16-zena, NetworkManager 1.46.0-1ubuntu2.4,
linuxmint-keyring 2022.06.21, efibootmgr 18-1build2 and mokutil 0.6.0-2build3.
Extracted repository configuration combines zena main/upstream/import/backport
with noble/updates/backports/security and a Mint upstream priority of 700. That
is source evidence, not a complete reproducible package specification.

| Concern | A: deploy actual Mint squashfs | B: bootstrap Ubuntu + Mint | C: other public primitive |
| --- | --- | --- | --- |
| OS fidelity/content | Closest source bytes to this release, but live state is not installed state | Matching a few meta packages does not establish Mint build/configuration equivalence | No inspected shipped public target-root deployment interface establishes equivalent Mint output |
| Package database/cleanup | Requires actual Ubiquity dependency-aware keep/remove reconciliation, not deleting every manifest-remove item | Requires complete release package/version/pin/seed and configuration closure | An image builder or different distro/release is not automatically the Mint product |
| Users/groups | Reproduce intended account/sudo/group state; remove only proven live-user state | Explicit account policy required; Ubuntu defaults are not automatically Mint defaults | No complete release-specific contract established |
| Kernel/initramfs/drivers | Reconfigure hardware/kernel and regenerate target initramfs; do not copy the live initrd as installed initramfs | Install exact supported kernel/firmware and configure target hardware | Still requires the same finalization proof |
| Cinnamon/Mint tools | Preserve meta/core, tools, Mint adjustments and their configuration | Meta packages/repositories alone do not prove all release customization | No supported substitution identified |
| Repositories/keyrings | Preserve and verify the actual release's sources/pins/keyrings | Must reconstruct and authenticate the entire release graph | No alternative was accepted |
| Locale/timezone/network | Reproduce target locale.conf, timezone, NetworkManager and intended profiles | Must specify and validate these explicitly | Same requirement |
| Live/OEM residue | Apply semantic cleanup of casper/live/installer state with dpkg consistency; OEM mode is separate | Avoid live residue but still prove Mint-specific installed state | No private Ubiquity stage API is adopted |
| fstab/machine ID/keys | Generate stable fstab and a new machine identity; do not inherit image/installer identity or SSH host keys | Fresh target identity and key lifecycle required | Same requirement |
| Signed chain/NVRAM | Control inherited core.efi/alternatives/package hooks and final loader separately | New root may avoid some update branches, not all signing/policy effects | No tool combination automatically bounds all effects |
| Failure/maintenance | New engine must own every finalization stage and release regression; no proven completion barrier yet | Larger product-equivalence and package-policy burden | No supported end-to-end primitive demonstrated |

**Decision:** A is the most faithful *research candidate*, but no option is selected
as a supported production strategy. B is not accepted as "Mint because Cinnamon
packages install". C did not reveal a complete supported target-root primitive.
The [official Mint guide](https://linuxmint-installation-guide.readthedocs.io/en/latest/install.html)
describes the live-session installer flow; it does not provide a contract for a
replacement squashfs/bootstrap deployment. Absence of such a contract in inspected
material is not a claim that a new correct engine is impossible.

### Essential transformations observed in the shipped installer

`install.py::generate_blacklist` reads manifest removal inputs but then keeps
architecture/EFI-dependent packages and dependencies and considers maintainer
scripts. Critically, **the raw removal manifest includes shim-signed and signed
GRUB**. Applying it directly can remove the permanent Secure Boot chain.
`copy_all` preserves installer-created fstab/crypttab rather than replacing
everything with squashfs contents. `plugininstall.py` configures
network/locale/APT/plugins, target hooks, hardware, extras, loader, removals and
logs. It carries a Mint-specific locale.conf fix and final GRUB-title adjustment.

Hardware finalization uses hw-detect, module registration and target kernel
reconfiguration, temporarily diverts update-initramfs, then regenerates the exact
kernel's initramfs and links. Reproducing only file copy misses this lifecycle.
The actual user-setup helper creates accounts/groups and applies credentials;
the candidate must verify results rather than inherit helper error tolerance.
Complete live-user, machine-ID and SSH-key finalization outside Ubiquity is still
unqualified. [systemd's machine-ID contract](https://manpages.debian.org/trixie/systemd/machine-id.5.en.html)
also distinguishes a missing ID from an empty one for first-boot processing, and
can reuse a D-Bus ID. Clearing one file is not sufficient evidence of fresh identity.

`install_misc.py::chroot_setup` binds the live `/dev` and `/run`, mounts proc/sys,
and uses service-start suppression/diversions. It is an internal installer helper,
not a safe public replacement engine. It does not provide the required constrained
device/firmware boundary merely because its target path is explicit.

### Mint signed-boot finalization remains bounded only in parts

The inspected signed-GRUB postinst can call grub-multi-install when core.efi
exists. That helper enumerates usable ESPs, processes configured device choices,
adds a mounted `/boot/efi` outside that list, and mounts selected devices itself.
An otherwise unmounted Windows ESP is not protected by mountpoint absence alone.
No change to Windows flags/types/order is proposed.

The shipped shim postinst also selects **latest versus previous signed shim**
using SecureBoot/MokSBStateRT and kernel revocation checks. Hiding all EFI evidence
to suppress writes can change that selection; it is not a neutral security fix.
Kernel choice partly consults the running kernel, which may differ from the
installed kernel. The replacement needs an audited signed-profile decision with
read-only policy evidence and denied unauthorized writes, not unconditional copying
of a convenient shim binary. MOK/DKMS actions must not silently become interactive
firmware requirements or disable Secure Boot.

An explicit finalizer would use the exact R/E mounts and supported signed-mode
tools, independently verify loader/configuration destinations, and record a
separate permanent NVRAM transition before completion. No complete package-hook
isolation, release-equivalent finalization or journaled interruption lifecycle is
implemented. Ubiquity's success hook still cannot supply the failure barrier.
Mint remains **BLOCKED**; legacy Ubiquity code is retained behind the production
guard, not removed or certified as a supported migration path.

## Receipts and payload evidence implemented

`InstallationReceiptV1` is separate from RecoverySnapshotV1. It binds the prepared
ownership, formatted-root identity, distro/version/strategy, source identity,
stage results, required file identities and raw permanent entry/BootOrder witness.
Stages distinguish not started, failed, outcome unknown and readback verified.
The common evaluator requires all stages, independent current layout/file/firmware
readback, exact ESP destination, compatible kernel/initramfs names, an active
entry included in BootOrder, supported raw parsing/attributes and no opaque
optional data. Missing or failed evidence cannot become complete.

The maximum result is deliberately named **EvidenceComplete**, not installation
Ready, bootable, recoverable or authorized. Stage digests refer to retained audit
evidence; this evaluator does not validate arbitrary producer assertions about
accounts, packages, signatures or a complete firmware mutation set. The witness
does not prove the entry will be tried before every other bootable entry. No
qualified Debian/Mint receipt producer or reboot gate exists. Synthetic complete
fixtures test the evidence format, not nonexistent deployment engines.

`InstallationReceiptStore` reuses Community's create-new/flushed artifact store
and fresh-handle reopen with pinned hash/generation and structure checks. Partial
receipts stay partial. This Windows-side store is not yet an installer-runtime
durable writer or a method for finding receipts by scanning media. The Linux
durable sink and references to stage audit records remain required.

Payload verification now also accepts a **verified formatted-root receipt** while
retaining the same canonical payload volume/partition/generation/path/content
checks. This allows content verification to continue after formatting without
loosening the original fresh-root resolver. Native verified mounts and file readers
are still required. Fedora stage2/kickstart UUID generation remains unchanged;
retained post-install scans, Debian package/config transport and Mint's early ISO
scan are not repaired by these pure comparisons. No label fallback was added.

## Decision and next work

All complete product architectures remain **BLOCKED**. Fedora's unchanged exact
storage contract still needs native create/readback, runtime integration, pinned
loader profiles, payload/agent verification and disposable-VM validation. Debian
needs a qualified complete offline GNOME bootstrap plus package/finalizer isolation.
Mint needs a demonstrated release-equivalent filesystem/package finalization and
signed-boot lifecycle. Mount/device/firmware isolation must be implemented and
qualified in the pinned Linux environments before calling either design complete.

The next task is **not native Windows preparation**. Do not turn candidate stage
tables or synthetic receipts into capability flags. No native partition operation,
mount on developer storage, firmware/BCD/RTC write, reboot or destructive VM test
occurred. Fedora's generator and the global preparation guard are unchanged.
The phase log records exact deterministic validation counts.
