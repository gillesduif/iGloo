# Deepin 25 — BLOCKED / coming-soon

Decision recorded 2026-09-08, before replacement implementation. No installation
has been executed during this review. The previous prototype is unsafe to ship.

## Image and observed stack

The [official download page](https://www.deepin.org/en/download/) still selects
25.2.0 AMD64; later 25.2.x package updates are not replacement installation ISOs.
The [release directory](https://cdimage.deepin.com/releases/25.2.0/amd64/) publishes
`deepin-desktop-community-25.2.0-amd64.iso`, 6,976,131,072 bytes, and
[SHA256SUMS](https://cdimage.deepin.com/releases/25.2.0/amd64/SHA256SUMS).
The local ISO hashes to
`f875c9a605bfe6a8425d1d353a3c1ec755bf37f5b0a3231ca19e2145da0ff450`.
No signature is listed in that directory; this is a pinned HTTPS checksum, not
a verified publisher signature. Acquisition verification must not be weakened.

The ISO ships `deepin-installer` **7.0.60**, its Qt frontend and installer tools,
`live-boot` **1:20210208-deepin**, and immutable boot/control packages **1.0.31**.
It does not use Calamares, Debian Installer, Ubiquity or Subiquity. The archived
`deepin-installer-reborn` source is not authority for these binaries. Official
7.0.50.1 source is useful supplementary evidence but is not a source match.

## Boot facts, not an iGloo boot recipe

`boot/grub/grub.cfg` uses GRUB for UEFI and pairs:

| Kernel choice | Kernel | Initrd |
|---|---|---|
| 6.6 | `/live/vmlinuz-6.6` | `/live/initrd-6.6` |
| 6.18 | `/live/vmlinuz.efi` | `/live/initrd` |

The stock install command line is
`boot=live union=overlay livecd-installer locales=zh_CN.UTF-8 console=tty splash --`.
`live/filesystem.module` lists **both** `filesystem.squashfs` and
`filesystem-extra.squashfs`. A single squashfs is not the complete installer.
The ISO label is `deepin`; the normal command line does not reference it. UEFI
uses `EFI/BOOT/bootx64.efi` and GRUB; ISOLINUX BIOS entries also exist. The actual
initrd accepts `findiso=`, `fromiso=` and `toram`, loop-mounts ISO files and can
copy the entire medium into RAM. Full-ISO staging is therefore a supported
boot-layer candidate; it does not require Ubuntu's `iso-scan/filename` argument.
The installer later reads the medium again to deploy OSTree contents. NTFS and
installer-medium discovery, successful RAM copying and iGloo config delivery
still need VM validation. There is no approved `InstallerBootSpec`, extraction
recipe or RAM budget yet. See [ISO-EVIDENCE.md](ISO-EVIDENCE.md).

## Automation and chosen architecture

Shipped `configs/settings/default_settings.ini` documents `DI_INSTALL_MODE`,
`DI_PARTITION_TYPE`, and `DI_PARTITION_CONFIG`. Tools include
`deepin-installer-config`, `deepin-installer-parted`, a command agent, preinit,
and staged shell hooks. These are real interfaces, but not a demonstrated safe
unattended root-only integration:

- The shipped preinit `before_install()` does not propagate a failed guard hook.
- Configuration copied from the medium can fail with only a warning.
- Stock hooks include disk-level authorized-data writes, a first-ESP fallback,
  removable-path GRUB installation and overwrites of existing `EFI/ubuntu/grub*`.
- The prototype's custom mode lets the GUI choose and generate partition layout.
- Supplementary 7.0.50.1 source shows automatic custom disk selection falling back
  to other eligible disks and permissive argument/schema defaults. Current 7.0.60
  binary inspection independently confirms disk-object wipe/label calls and
  partition resolution by sector rather than GUID. No runtime safety claim is
  based solely on the older source; isolated current-binary tests remain needed.

**Decision:** ship a blocked plugin implementing the existing `IDistroPlugin`
interface. Compatibility always reports a blocker; config rendering, boot spec
and agent payload requests refuse with a clear error. No deployable partition
policy, unattended config or migration agent is emitted, even if catalog status
is accidentally changed. Keep the verified ISO pin and logo. Remove the unsafe
prototype and its unneeded core changes. Do not add a generic capability without
an implemented, verified consumer.

The preferred next architecture is a small Deepin-specific driver of the shipped
immutable deployment tools, bypassing the GUI, auto partitioning and generic hook
pipeline. Hooks `09_ostree_sys_init.job` and `10_ostree_persistent_init.job`
extract the repositories. `tools/functions/ostree_funcs.sh` provides checkout,
target mounting with `deepin-immutable-mount-root`, deployment with
`deepin-immutable-ctl admin deploy`, and backup finalization.
Separate `/boot` and `/persistent` partitions are optional in that script. This
makes installation into an explicitly designated root plausible, **not proven**.
Do not invoke the entire function library or chroot hook chain without auditing
its disk writes. The manual parted CLI is a research alternative, not selected
for production. Neither path has been exercised here.

## Disk safety prerequisite

**Target identity phase (2026-09-10):** the generic
[creation and resolution boundary](../../docs/reference/installation-target-identity.md)
is implemented separately from installation. Windows can create one explicitly
authorized root in an already-free extent, verify the returned PARTUUID against
the refreshed table, and publish a versioned receipt in the migration manifest.
The read-only Linux resolver checks GPT disk GUID, root/ESP PARTUUIDs, exact
sector geometry and the complete partition identity set before returning paths.
It rejects ambiguity and changed layouts. This resolves the missing identity
model and resolver; it does not validate the full Windows-to-Deepin boot workflow.

Deepin now declares that it requires an owned root and explicit ESP. It still
emits no boot spec, install config or agent, even with valid identity. The legacy
app preparation path refuses this requirement. The new preparer does not use
the old shrink or seed heuristics. Actual Windows CIM creation, boot-bound run-ID
delivery, seed/ISO identification, cross-reboot resolution and deployment still
need disposable VM validation. See VALIDATION.md for executed checks.

Before formatting anything, Windows must record the disk GPT GUID and exact
iGloo-created root PARTUUID, offset, length and logical sector size, plus the
existing ESP PARTUUID and seed/ISO identities. The live driver must find exactly
one matching disk and partition set, prove root ownership and absence of existing
filesystems/holders, and reject missing, stale, duplicate or changed identities.
No disk selection by label alone, model/size, largest gap, first ESP or enumeration
order. No fallback target. Only the owned root may be formatted; the existing ESP
may be mounted without formatting. Preserve GPT, Windows, MSR, recovery, other
Linux/data partitions and Microsoft EFI files. Refuse unknown layouts.

The legacy generic `PreCreateRootPartition` cannot provide that guarantee:
`EnsureRootPartition` reuses any Linux-type GPT partition, and that legacy path
does not produce the new manifest claim. `BuildStoragePartitionList` also marks
every Linux-type partition as root. Do not turn that flag on for Deepin; use the
explicit creation/identity API. Existing supported distro paths are
outside this change; their behavior is not evidence that Deepin is safe.

## First boot and hardware

The [official installation guide](https://www.deepin.org/en/deepin-25-installation/)
recommends Secure Boot disabled, UEFI, 8 GB RAM and 64 GB SSD; its VM floor is
4 GB RAM. It describes account creation at first boot and cautions that the
proprietary NVIDIA option can cause failures on newer cards. Do not promise the
Debian NVIDIA workflow for Deepin. BIOS media exist, but iGloo's integration is
UEFI-only. The default immutable system must remain enabled.

The ISO mounts `/opt`, `/etc` and `/var` writable, maps `/usr/local` to
`/var/usrlocal`, and routes APT/dpkg through `deepin-immutable-ctl admin exec`.
Its OEM settings select Deepin's `crimson` repository, not Debian or Ubuntu.
The future agent should use writable persistent state for logs, progress and
payload, wait for the actual user/OOBE completion, and only mark successful steps
complete. Reuse independently validated shared migration functions through a
Deepin adapter. Do not run the complete Debian-family agent: package/repository,
GRUB, display and cleanup assumptions differ, and it writes under `/usr`.
Do not globally disable immutable protection. No agent is packaged until its
write locations, activation and update persistence have been demonstrated.

## Validation needed before enabling

Verified so far: Git baseline and inventory; official release/checksum; local ISO
hash; stock boot config and layer list; shipped package versions, configuration,
hook failure handling and immutable deployment script structure. See
[REVIEW.md](REVIEW.md) for prototype decisions and protected user work, and
[VALIDATION.md](VALIDATION.md) for completed build/test/schema checks and their
limits. The full .NET analyzer build and all .NET tests pass, including 56 Deepin
refusal/metadata tests. Full-catalog schema validation has one existing Ubuntu
status-enum failure; Deepin passes.

Still required in disposable VMs: isolated current-binary backend experiments;
ISO-file boot/config delivery; missing/corrupt resource refusal before any disk
write; root-only immutable deployment; OOBE and migration; repeat/retry and power
failure; GPT/partition/ESP before-after comparisons; independent Windows boot;
multiple disks, duplicate labels, other Linux partitions, fragmented free space,
512e/4Kn, NVMe/SATA naming, and existing ESP capacity. Never attach host disks.

Still required on physical hardware: firmware BootNext/fallback behavior,
Windows/BitLocker preflight and boot preservation, NVMe/SATA/4Kn coverage, NVIDIA
and hybrid graphics, networking and display migration, and immutable updates
preserving the agent. Secure Boot remains blocked unless the complete chain is
validated. Deepin must stay `coming-soon` until the repository install matrix is
met. Build and unit-test success cannot satisfy that matrix.
