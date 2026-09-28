# Community installation ownership — issue #241 continuation

Latest integration: the [closed canonical Debian import action](debian-session-boundary.md#later-canonical-import-integration-2026-09-28)
connects existing ownership/lease/session code to the importer. Fake-boundary tests
remain distinct from native GPT/full-artifact qualification. The subsequent
[chunk transport continuation](debian-session-boundary.md#later-chunk-transport-qualification-2026-09-28)
verifies the unchanged full stream from FAT32, while the whole lab collector rejects
its MBR runtime disk. Canonical import remains unqualified; no readiness gate opens.

Latest Debian-only checkpoint: [configured-root artifacts](debian-configured-root-artifact.md).
The canonical ownership/isolation model is unchanged. A real offline 1,594-package
GNOME root verifies in a disposable factory VM. Its preserved raw source remains
non-publishable; the [neutralization continuation](debian-configured-root-neutralization.md)
produces a separate machine-neutral development artifact with independent readback.
Production authentication and the canonical import session remain Unsupported.
No Fedora/Mint implementation or native preparation enablement is included.

This builds on the [shipped-installer audit](installer-esp-binding.md), not a new
installer stack. **No production preparation, registration or distro replacement
is enabled.** RecoverySnapshotV1, Exact, RecoveryReadiness and PreCommitGate are
unchanged. The complete product paths remain Blocked; Fedora's *storage recipe*
now has a supported contract and deterministic implementation.

The [target-root deployment continuation](target-root-deployment.md) adds verified
post-format root/mount contracts and durable installation evidence, with a full
Debian stage design and deeper Mint image/package audit. It does not accept or
enable either replacement engine. The historical checkpoint below remains useful
for the storage and shipped-media evidence it established.

The [Debian-only engine continuation](debian-target-root-deployment.md) now adds
43 explicit stage contracts, target-bound configuration, independent readback and
durable intent/receipt rules. Debian remains blocked; its legacy configuration
export cannot fall back to partman. Fedora and Mint were not redesigned in that run.
The subsequent Debian content continuation adds source verification/solver tooling,
bounded executor interfaces, agent installation restrictions and native Linux journal
fixtures. Full bundle qualification, isolation/runtime implementation and the new
agent worker remain blockers alongside signed-loader/firmware work; native Windows
preparation is still not the next enabled step.
An earlier Debian non-firmware continuation recorded a real signed repository
blocker (`gvfs-backends` recommends unavailable `wsdd`), 78 independently verified
minbase archives, native file/credential/readback primitives and a restricted
first-boot evidence worker. The privileged mount/isolation host, complete GNOME
bundle/runtime and mandatory user-data producer are still unqualified. See the
Debian document for the supported-versus-fixture-tested distinction.

The [native isolation continuation](debian-isolation-boundary.md) now adds a real
restricted package broker and independently observed native mount mechanics.
Directory/tmpfs fixtures pass; the canonical block-session adapter and bootstrap
profile are unqualified. No production stage or preparation capability was enabled.

The subsequent [canonical session checkpoint](debian-session-boundary.md) implements
the lease/authority/private-pipe mount-session connection. Qualification remains
blocked: no real GPT deployment mounts, no complete package-stage host, and no
supported debootstrap privilege transition. Existing ownership and Exact rules
were not relaxed to accommodate the WSL inventory's unavailable observation.

## Common storage ownership implemented

The selected Fedora storage lifecycle is **pre-create the ESP and root partition**.
The future Windows orchestrator will create the exact planned GPT allocations;
Linux root remains unformatted until Anaconda formats that specific partition.
Kickstart's current size/disk allocator cannot be constrained to the one planned
free extent, so Anaconda-created root was rejected for this design.

`PreparedLayoutV1.StorageOwnership` adds `PreparedStorageOwnershipV1`:

- preparation generation;
- complete preserved GPT partition identities, including Windows ESP and Windows
  data/recovery partitions, matching the plan's before-partition set;
- creation receipts for Linux ESP, root, payload and optional ISO partition;
- each receipt's canonical disk identity, GPT disk/partition/type GUIDs and exact
  byte geometry.

`PreparedGptPartitionV1` deliberately has **no filesystem or volume GUID**.
`VerifyCreation` requires provider-created identity equal to independent readback,
the exact planned allocation, and a GUID absent from the before-state. Formatted
ESP/payload/ISO receipts still use the existing canonical volume contract and must
agree with their GPT receipts. Root is never represented by an invented FAT32/NTFS
volume, and canonical recovery volume validation was not weakened.

The existing durable checkpoint store now preserves this evidence. A
`CreationInProgress` checkpoint can retain each successful raw creation receipt
before formatting or the next create operation. It cannot be reopened as complete.
Complete/staged checkpoints require every planned partition and formatted-volume
receipt. Reopen still verifies pinned hash, generation and structure. This is
evidence persistence, **not** a complete creation journal, rollback or automatic
resume implementation. A crash before a receipt is durable remains ambiguous.

`InstallationOwnershipV1` joins that same layout to `InstallerEspBindingV1` and an
optional ISO filesystem UUID. The pure resolver verifies all preserved and owned
partitions, rejects extra partitions on the protected disks, and returns transient
Linux paths. It rejects changed generations, parent disks, geometry, GUIDs, types,
filesystem UUIDs, duplicates and Windows/root/ESP aliases. A recognized filesystem
on the expected fresh root rejects a fresh installation attempt; retry needs its
own supported transition, not reuse of this before-state contract.

These are enforceable validation methods, not UI capability booleans. No adapter
can obtain an Available complete recipe from a reserved extent or partial receipt.
Mount execution, post-format root UUID acquisition, post-install verification and
reboot authorization are **not** implemented by this common layer.

## Fedora: complete explicit storage recipe

Actual media remains Fedora Everything netinst 44-1.7, Anaconda 44.30,
pykickstart 3.69 and Blivet 3.13.2. The new distro-owned
`FedoraOwnedStorageRecipe` derives the entire storage section after common
ownership revalidation:

```text
ignoredisk --only-use=<freshly resolved target disk>
clearpart --none
bootloader --boot-drive=<freshly resolved target disk>
part /boot/efi --onpart=UUID=<unique Linux ESP UUID> --noformat
part / --onpart=PARTUUID=<owned root partition GUID> --fstype=ext4
```

No autopart, allocation, grow, resize, new ESP, unsafe clearpart or Windows format
is emitted. Root contains `/boot`; no separate boot/swap partition is implicitly
requested by this storage recipe. The existing KDE package selection is retained.
The disk name is freshly resolved output, not a persisted `/dev/sdX` identity.
Layered disk paths are rejected.

The replacement helper removes the whole legacy largest-disk/size-fallback storage
script and its storage include. It preserves the separate Wi-Fi work formerly
inside that same `%pre` block, and checks that top-level storage instructions
correspond exactly to the five generated commands. It does not append directives
to autopart. Neither this helper nor the collector is wired into the frozen
`IDistroPlugin` path; the existing legacy renderer remains behind the Community
preparation block. Do not use the helper's output as a complete boot-ready recipe:
the retained post-install scripts still have payload/agent discovery work below.

Read-only validation additionally ran the **actual extracted pykickstart 3.69 F44
parser** on the complete storage section. It accepted exactly two partitions:
`/boot/efi` with format false, and `/` by PARTUUID with format true/ext4. No script,
partitioner or installer was executed. This establishes parser/contract support;
it is not an Anaconda transaction or a VM installation test.

Storage sub-result: **SUPPORTED BY CONTRACT** for the explicit pre-created,
unencrypted root/ESP layout. Full Fedora installation: **BLOCKED** on runtime
integration, verified mounts/payload, pinned signed loader acquisition, resulting
bootloader verification and disposable-VM evidence. All Windows partition
identities are preserved by this storage recipe; preservation through every
package/post-install stage has not been certified.

## Linux runtime acquisition boundary

`distros/_shared/installer/collect_inventory.py` is read-only acquisition using
explicit machine-readable outputs of `lsblk`, `sfdisk --json`,
`wipefs --no-act --json`, and `blkid --probe --output export`. It performs two
independent passes, requiring identical results, and issues no mount, format,
partition, firmware or udev mutation. `LinuxInstallerInventoryProtocol` parses
its versioned JSON into the existing shared inventory/resolver, rejects duplicate
JSON properties and malformed values, and preserves typed failure states.

The collector correlates lsblk's parent and byte size with the GPT table's GUIDs,
sector size and geometry. FAT32 must be identified as FAT32, not assumed from
`vfat`. A successful signature scan with no recognized signatures is explicitly
Absent; a failed command is never converted to absence. This does not prove the
partition is zero-filled or exclude every unsupported format. Its use for fresh
root formatting also requires the independent owned-create receipt above.

Visible loop/optical media participate in filesystem UUID uniqueness checking.
SquashFS has no UUID field and is represented as identified SquashFS with structural
UUID absence; it cannot become the nominated ESP/payload. Unknown layered topology,
non-GPT disks, incomplete probes, missing tools or ambiguous signatures fail closed.
Native exit codes without a reliable richer classification remain Unavailable.
The relevant util-linux contracts are [lsblk](https://man7.org/linux/man-pages/man8/lsblk.8.html),
[sfdisk](https://man7.org/linux/man-pages/man8/sfdisk.8.html),
[wipefs](https://man7.org/linux/man-pages/man8/wipefs.8.html) and the
[SquashFS probe](https://raw.githubusercontent.com/util-linux/util-linux/v2.41/libblkid/src/superblocks/squashfs.c).

The collector and protocol are fixture-tested, not run against developer storage.
They still need packaging and qualification inside each pinned installer environment,
a verifier invoked before any installer consumption, and revalidation around the
actual mount/write transitions. Linux equivalence of the Windows hardware unique-ID
is not established merely by matching GPT GUID/geometry. Visible clones are
rejected, but a substituted single clone is not authenticated by these observations.
No claim of full production native identity support is made.

## Debian replacement architecture review

The current product is Debian Trixie GNOME, installed offline by hd-media d-i plus
live-installer from the live ISO. That first/default-ESP path is not accepted for
the new layout. It remains disabled; **no replacement is enabled**.

[Debian's documented debootstrap procedure](https://www.debian.org/releases/trixie/amd64/apds03.en.html)
supports installing into an explicitly mounted root and configuring a system
without partman. This could use the same prepared root and ESP identities. It is
not automatically equivalent to iGloo's current offline GNOME product.

The audit downloaded actual **Trixie APT candidate packages** on 2026-09-27,
verified their SHA-256 against the downloaded Packages index, and extracted data
and control scripts without executing them. These are candidate package versions,
not a claim they are preinstalled in the live ISO; index signatures were not
independently authenticated in this audit. Versions: debootstrap **1.0.141**,
grub-common/grub2-common/grub-efi-amd64 **2.12-9+deb13u2**, signed GRUB
**1+2.12+9+deb13u2**, shim-signed **1.51~1+deb13u1+16.1-2~deb13u1**.
Exact package URLs/hashes and decisive script hashes are in the audit manifest.

| Stage | Supported primitive / required ownership | Remaining implementation and failure consequence |
| --- | --- | --- |
| Root creation/format | Owned GPT root receipt; explicit format of that partition only | Windows native create/readback and Linux format transition are unimplemented. An uncertain result requires observation, not another create or guessed partition. |
| Target mounts | Resolve root and Linux ESP; mount root at a controlled target and that ESP at target `/boot/efi`; Windows unmounted or explicitly read-only | A verified mount executor must compare mountinfo/source identity and reject symlink/mount substitution before every package/loader phase. Do not expose Windows ESP as target or an alternate ESP. |
| Base deployment | debootstrap into that exact root with authenticated trixie source/keyring | Pin the complete dependency/media closure. Current live squashfs is not an APT mirror; offline parity is unresolved. Failure leaves a partial root. |
| Sources and system configuration | Deterministic trixie, updates and security sources; stable fstab, hostname, network configuration, locale/timezone, user and sudo group | Product configuration/migration manifests need a complete generator. No password fallback, missing-user success or boot-time configuration assumption. Windows need not be writable. |
| Desktop and packages | Intended product is GNOME; task-gnome-desktop plus required desktop/firmware/network/user packages must be specified | A base bootstrap alone is not Debian GNOME support. Version pinning, licensing/source choice and offline availability must be resolved. Interrupted dpkg state blocks completion. |
| Kernel/initramfs | linux-image-amd64, matching modules and initramfs generation inside the exact root | Independently verify installed kernel/modules/initramfs and boot configuration. Package exit success alone is insufficient. |
| Signed loader packages | shim-signed, grub-efi-amd64-signed and their actual dependencies, including shim helpers/common | Constrain target mounts, package hooks and Secure Boot policy. Validate the complete signed chain/SBAT/revocation compatibility. No unsigned substitute or disabling Secure Boot. |
| Final GRUB | Explicit target root and nominated ESP; signed loader and deterministic root reference in configuration | Run a supported explicit finalizer only after checking actual mounts. `GRUB_DISABLE_OS_PROBER=true` is the conservative candidate policy; do not inherit broad Windows discovery from the current template. Finalizer and independent readback remain missing. |
| Permanent NVRAM | Installer finalization owns permanent Linux boot, before completion reboot | `grub2/update_nvram=false` can suppress the inspected GRUB/shim hook updates; that is not a complete policy for all dependencies or an exact final Boot#### plan. Slot selection, payload/attributes, BootOrder effects, journal and fresh verification remain required. |
| Agent/configuration | Copy only generation/hash-verified payload into the exact target and enable the intended service | A broad label/directory search is not ownership. Missing/corrupt agent/config blocks completion, not a successful reboot. |
| Cleanup and reboot | Verify all required stages and persistent boot first; unmount only owned mounts | There is no implemented stage journal, recovery or reboot gate. Retain diagnostic evidence and the handoff payload on failure. VMware rollback is separate from product recovery. |

Actual `grub-efi-amd64` and `shim-signed` postinst helpers log grub-install failure
without necessarily failing the package transaction. They use the mounted ESP and
can invoke grub-install when prior core/EFI files exist. They inspect
`grub2/update_nvram` and removable-path settings; shim also invokes Secure Boot
policy tooling. A fresh root plus explicit mounts may constrain these supported
tools, but **APT exit zero is not bootloader readiness**. The complete hook/mount/
NVRAM/interruption lifecycle is not implemented or proven. Debian remains
**BLOCKED**; no partial debootstrap script is advertised as a replacement.

The inspected `shim-signed-common` helper, `update-secureboot-policy`, can request
MOK validation changes through `mokutil` when its configured policy/action and
confirmation inputs call for it. Its command failure is not always propagated.
Thus `grub2/update_nvram=false` alone does not bound all firmware effects of the
signed-package stack. This is a conditional source finding, not an observed
firmware change. The replacement must preserve Secure Boot, establish the complete
package-hook policy and reject an unsupported required action; disabling validation
or relying on an unattended prompt is not an accepted completion path.

## Mint replacement architecture review

Actual target remains Mint 22.3 Cinnamon / Ubiquity 24.04.3+mint19 / casper 1.498,
signed GRUB 1.202.5+2.12-1ubuntu7.3 and shim-signed 1.58+15.8-0ubuntu1. The prior
audit includes bundled partman EFI selection/format hooks, grub-multi-install,
signed package postinst scripts, Ubiquity deployment/configuration, and both GTK
and noninteractive success-hook handling.

| Option | What is supported | Why it is not an accepted complete strategy |
| --- | --- | --- |
| A: Ubiquity OS-only, explicit final loader | Public no-bootloader switch skips the final GrubInstaller call; shipped grub-install supports an explicit EFI directory and signed mode | It does not give exact root/ESP ownership to partman, does not remove mounted-ESP/package-hook effects, and the success hook ignores command failure before completion/reboot. Signed-GRUB multi-install can add the mounted Windows ESP to its proposed targets. There is no supported complete exclusion boundary here. |
| B: deploy Mint rootfs/image, configure and finalize explicitly | The ISO contains a squashfs and public extraction tools | A live filesystem is not a completed installed Mint system. Actual Ubiquity install.py excludes/preserves installer-generated fstab/crypttab/user state and applies manifest removal/configuration work. No supported Mint unattended deployment contract was established for recreating all users, package state, kernel/initramfs, hardware setup and signed boot lifecycle outside Ubiquity. Calling its internal stages is not a new supported public API. |
| C: lower-level Ubuntu/Mint primitive | Debian/Ubuntu bootstrapping and package tools exist | Bootstrapping Ubuntu and adding Mint packages is not demonstrated equivalent to this Mint Cinnamon release. No shipped primitive inspected provides the required exact target + complete Mint configuration lifecycle. LMDE or another release changes the product and is not silently substituted. |

Explicit finalization would need the same verified root/ESP mounts as Debian,
exact installed signed shim/GRUB packages, known distributor/configuration paths,
controlled package-update destination state, a separately planned permanent NVRAM
transition, independent content/configuration readback and a failure-propagating
completion gate. It must occur before reboot. None may rely on Ubiquity's success
hook as an error barrier. Mounting the wrong ESP and later correcting it cannot
satisfy Windows preservation. No option is implemented as an installer engine.

Mint sub-status: **BLOCKED BY SPECIFIC UNSUPPORTED CONTRACT** — complete exact
deployment/mount ownership and failure-controlled signed-boot finalization.
Ubiquity is not retained as a supported path, and no replacement is certified.
Mint remains a Community beta blocker. There is no manual UEFI fallback product
flow and no proposed Windows ESP hiding/type/flag/order workaround.

## Payload identity and transport

`InstallerPayloadVerification` adds pure generation/partition/volume/path/length/
SHA-256 comparison for payload and optional ISO files after independently verified
mount/readback. Wrong ISO content, wrong volume/partition, missing files, changed
generation and failed observations cannot pass. A hash proves content identity,
not bootability. Manifest persistence/binding to the final installation artifact
and the native mounted-file reader remain required; a caller-supplied hash is not
an independent read.

The Fedora 44-1.7 initrd was additionally streamed/extracted read-only. Its actual
`anaconda-lib.sh::disk_to_devpath` supports UUID selectors for stage2/kickstart.
The new generator emits `inst.stage2=hd:UUID=...:` and
`inst.ks=hd:UUID=...:/ks.cfg` only after complete inventory uniqueness and ownership
checks. This replaces labels in the **new generated transport**, not the blocked
legacy plugin. Early-boot duplicate checking and content verification must be
integrated before consuming those files.

| Remaining consumer | Actual behavior / required completion |
| --- | --- |
| Fedora stage2/kickstart | UUID syntax proven in shipped initrd; new helper tested. Early transport, integrity checks and prepared renderer integration still missing. |
| Fedora payload/agent/config | Existing `%post` uses `blkid -L OEMDRV`, broad NTFS source scans, and later enables os-prober. These retained scripts are outside the storage generator's preservation proof; replace with verified generation-bound mounts/files before enabling this full template. |
| Debian hd-media/ISO | Actual iso-scan exposes `shared/ask_device` / `shared/enter_device` and mounts selected paths, but current filename-only integration scans broadly. A stable device path needs prior verified translation and file hash/generation checks. A future bootstrap engine needs its own pinned package/payload source instead; neither is integrated. |
| Mint casper/ISO | Actual 20iso_scan/find_path scans for the filename. A later live-media argument does not prove this earlier lookup was constrained. A verified early-boot mechanism remains missing. |
| Debian/Mint seed and agents | Current generated services scan label and device lists for an igloo-agent directory. This is not exact. Resolve the payload partition and validate the durable manifest before copy/execute. |

No inspected limitation was converted into a claim that labels are inherently the
only available transport. No label-only ownership is accepted. If a future pinned
tool truly requires a label, a complete visible-inventory uniqueness check plus
strong identity/content checks is still required. This implementation does not
provide such a label fallback.

## Enablement decision

`DedicatedEspPreparationSupport.Production` remains
`Unsupported / DedicatedEspInstallerBindingNotImplemented`, before the first
Community preparation side effect. The common receipt/resolver/collector and
Fedora storage contract are implemented building blocks, not all production
capabilities. Native storage creation must wait for complete Debian/Mint strategies,
packaged runtime/mount verification, exact payload discovery, pinned loader profiles
and a supported failure/recovery/completion lifecycle. No third ESP is planned.

Tests and source inspections do not certify Windows ESP preservation through a
complete installation. No developer disk/ESP was mounted, created, formatted or
written; no firmware, BCD or RTC change or reboot occurred. No destructive VM test
was performed. Validation totals are recorded in the Fleet phase log.
