# iGloo: Unattended In-Place Migration from Windows to Linux Without External Boot Media

**Technical White Paper - Draft 0.1 (August 2026)**

Gilles D'huyvetter
Independent researcher/developer, Flanders, Belgium

---

## Abstract

Desktop Linux adoption has historically been gated not by the quality of the target
operating system but by the migration procedure itself: preparing external boot media,
reconfiguring firmware, manual disk partitioning, and post-install driver and data
setup. We present iGloo, a Windows-native application that performs a complete,
unattended migration to Linux - including dual-boot partitioning, installer boot
without any external media, cryptographically verified OS acquisition, and automated
migration of user files, wireless credentials, and hardware enablement - with no user
interaction beyond an initial questionnaire. We describe (1) a *direct-install
pipeline* that stages a Linux installer from within a running Windows system using
only disk partitioning and UEFI boot-order primitives; (2) a *distro plugin
abstraction* that reduces per-distribution integration to a declarative boot
specification and an installer-configuration renderer; (3) a *first-boot agent*
that completes hardware enablement and user-data migration inside the installed
system, where the full OS environment is available; and (4) the security and
data-safety model required to make a disk-repartitioning tool trustworthy to
non-technical users. We report validation results across four distributions
(Fedora, Debian, Linux Mint, Ubuntu) and derive design rules from failure modes
encountered on real firmware. The work is timely: the end of Windows 10 support
(October 2025) left an estimated 240 million PCs unable to upgrade, and European
digital-sovereignty policy actively seeks credible desktop migration paths.

---

## 1. Introduction

### 1.1 The migration gap
<!-- TODO: expand. Core argument: every prior "year of the Linux desktop" analysis
focuses on the desktop; the actual funnel drop-off is the installation procedure.
Enumerate the steps a Windows user must perform today (ISO download, USB flashing
tool, BIOS boot menu, Secure Boot, partitioning, post-install drivers/codecs,
manual data copy) and cite abandonment causes. -->

### 1.2 Why now
- End of Windows 10 support (14 Oct 2025); ~240M devices excluded from Windows 11
  by hardware requirements (Canalys). <!-- verify citation -->
- EU digital-sovereignty initiatives (public-sector migrations, e.g.
  Schleswig-Holstein; Sovereign Tech Fund; NGI programme).
- UEFI ubiquity: the boot environment is now uniform enough that media-less
  installer staging is tractable, which was not true in the BIOS era (§8, prior art).

### 1.3 Contributions
1. A media-less installer staging technique using only Windows-side primitives
   (volume shrink, FAT32 partition creation, UEFI BootNext registration) - §4.
2. A declarative per-distribution abstraction (`InstallerBootSpec`) that captures
   the *entire* distro-specific boot delta in ~10 fields - §5.
3. An initrd configuration-injection method (gzip-concatenated newc cpio) that
   delivers rendered installer configs into arbitrary initramfs images without
   unpacking them - §4.3.
4. A two-phase bootstrap design rule: *never perform environment-sensitive work in
   installer late-hooks; defer to first boot of the installed system* - derived from
   documented failure modes - §6, §9.
5. A trust architecture for consumer disk-repartitioning software: pinned-fingerprint
   GPG verification, offline key bundling, and a data-loss safety analysis - §7.

## 2. Requirements and threat model

### 2.1 Functional requirements
- R1: No external boot media, no firmware settings changed by the user.
- R2: Windows remains bootable and untouched (dual-boot) unless replacement is chosen.
- R3: Zero interaction between "Install" click and usable Linux desktop.
- R4: User data (folders, browser profiles, Wi-Fi credentials) present on first login.
- R5: Hardware fully enabled on first login (GPU drivers incl. NVIDIA, codecs,
  keyboard layout, locale).

### 2.2 Threat model
<!-- TODO: expand into table. Adversaries: (a) network attacker serving tampered
ISO/checksums (mitigated: TLS + SHA-256 + detached GPG sig verified against key
pinned by full 160-bit fingerprint, bundled offline - §7); (b) keyserver
substitution attack (mitigated: fingerprint pinning; 64-bit key IDs are forgeable);
(c) compromised mirror (same as a); (d) local tampering with staged artifacts
(out of scope pre-boot: Windows admin = game over; discuss). -->

### 2.3 Safety requirements (data loss)
- S1: The partitioner must never touch partitions outside the freed space.
- S2: Every destructive step must be preceded by a verified precondition, not an
  assumption about installer defaults (lesson: §9.1).
- S3: All unattended phases must produce persistent, on-disk execution traces
  (lesson: §9.3 - "never blind").

## 3. System overview

<!-- Figure 1: pipeline diagram. Windows app (wizard → manifest) → preflight →
ISO acquisition+verification → direct-install staging → reboot into installer →
unattended install → first-boot agent → finished desktop. -->

Component summary:
- **Wizard / manifest.** User answers (folders, browser, dual-boot vs replace,
  identity, locale/keymap) are serialized into a migration manifest - the single
  source of truth consumed by every later phase.
- **Preflight.** Hardware/firmware compatibility findings per distro plugin
  (BitLocker state, RAM, UEFI, GPU vendor; Secure Boot guidance for DKMS distros).
- **ISO acquisition.** Resumable download + SHA-256 + GPG (§7).
- **Direct-install pipeline** (§4). Stages installer artifacts on a dedicated
  FAT32 partition; registers one-shot UEFI boot entry.
- **Unattended install.** Distro-native automation (kickstart / preseed /
  autoinstall) rendered from the manifest (§5).
- **First-boot agent** (§6). Distro-family Python agent completing migration
  inside the installed OS.

## 4. The direct-install pipeline

### 4.1 Partition staging
Shrink NTFS volume → create FAT32 staging partition labelled per distro
(OEMDRV for Anaconda auto-pickup; CIDATA for cloud-init NoCloud) → sized as
extracted-artifact bytes + optional full ISO + overhead, rounded to MiB.
<!-- TODO: include sizing formula and why label choice is load-bearing per distro. -->

### 4.2 Kernel/initrd acquisition
Two paths, declared per distro: extraction from the ISO (Fedora, casper distros)
or direct download of alternate installer images (Debian: hd-media kernel+initrd,
because the standard installer initrd's cdrom-detect cannot consume an ISO *file* - 
these boot the offline install from the Debian Live image via iso-scan; §9.2).

### 4.3 Initrd configuration injection
Rendered installer configs are appended to the distro initrd as an additional
gzip-compressed newc cpio archive; Linux initramfs semantics concatenate members,
so the config appears at a known path (e.g. `/preseed.cfg`) without unpacking or
rebuilding the original image. Implementation is from scratch (no external cpio
on Windows). <!-- TODO: format details, 4-byte alignment, trailer handling. -->

### 4.4 Boot handoff
grub.cfg is written to every prefix the distro's signed grubx64.efi may search
(`EFI/BOOT`, `EFI/<distro>`, `boot/grub` - empirically distro-specific, §9.4);
UEFI BootNext is registered for a one-shot boot into the staged installer, so a
failed install falls back to Windows on the next boot rather than bricking boot.

## 5. The distro plugin abstraction

Each distribution is an isolated plugin: metadata, compatibility checks, an
installer-config renderer (kickstart/preseed/autoinstall from the shared manifest),
an agent payload, and a declarative `InstallerBootSpec`:

| Field | Meaning |
|---|---|
| KernelCmdline | Installer boot arguments (config path, iso-scan, automation flags) |
| Kernel/InitrdIsoPaths | Where artifacts live inside the ISO |
| Kernel/InitrdUrl | Alternate-image download path (Debian hd-media case) |
| ConfigDelivery | Staged on labelled volume vs injected into initrd |
| CopyFullIsoToVolume / IsoVolumeFileName | Whole-ISO staging for iso-scan/casper |
| VolumeLabel / MenuTitle | Staging label; GRUB entry title |

The claim defended here: this table is the *complete* per-distro boot delta - 
four distributions with three unrelated installer stacks (Anaconda, debian-installer,
Ubiquity/casper, subiquity/cloud-init) fit without pipeline changes.
<!-- TODO: honest limits: Secure Boot/MOK for DKMS modules; distros whose installers
have no unattended mode. -->

## 6. The first-boot agent

Shared per family (one apt-based agent serves Debian, Mint, Ubuntu with runtime
`/etc/os-release` detection). Runs as a systemd oneshot ordered
`Before=display-manager.service`: setup completes before any session exists.

Steps: password (crypt-hash limitations on Windows make install-time hashes
impractical → chpasswd in target), keyboard (installer preseeds are unreliable
for desktop keymaps - §9.5), GPU drivers (vendor detection, no version pinning),
codecs (per-distro package differences), os-prober/GRUB (Windows menu entry),
Flathub, user-file migration (ntfs-3g mount of the Windows volume; rsync with
`--no-links` to skip NTFS junctions; browser-profile mapping), Wi-Fi (NetworkManager
keyfiles, 0600 root), manifest redaction (password scrubbed from disk post-use).

**Two-phase bootstrap rule.** Installer late-hooks (Ubiquity `success_command`,
curtin `late-commands`) execute in constrained environments - busybox tooling,
no udev labels, kernel/module mismatches that make even `mount -t vfat` impossible
(§9.3). iGloo therefore minimizes late-hook work to writing a self-contained
bootstrap script + unit into the target with `echo`/`ln` only; all environment-
sensitive work (mounting the staging partition, copying the agent) happens on
first boot of the installed system, where the OS is complete and self-consistent.

## 7. Security architecture

- **Acquisition integrity:** TLS + SHA-256 + detached/cleartext GPG signature.
- **Key trust:** signing keys are bundled with the application or, when fetched,
  verified against a **pinned full 160-bit fingerprint** - 64-bit key IDs are
  spoofable on keyservers and are never used as trust anchors.
- **At-rest hygiene:** the migration manifest carries the initial password;
  the agent redacts it after use; Wi-Fi keyfiles land 0600 root-owned.
- **Boot integrity limits:** honest discussion of Secure Boot: distro shims are
  signed; DKMS-built modules (NVIDIA on Debian) trigger MOK enrollment, which
  breaks unattendedness → preflight guidance instead of silent failure.
<!-- TODO: full verification flow diagram; residual-risk table; disclosure policy. -->

## 8. Prior art

| System | Era | Approach | Why it ended / limits |
|---|---|---|---|
| Wubi (Ubuntu) | 2008-2013 | Loopback install into NTFS file, Windows bootloader | BIOS-era fragility, loopback I/O penalty, hibernation corruption, UEFI transition |
| mint4win | ~2012 | Wubi derivative | Same class |
| UNetbootin frugal | 2010s | Boot-media replacement, not migration | No data migration, manual partitioning |
| Vendor dual-boot tools | various | OEM-specific | Not general, not unattended |

Differentiators to defend: native partitions (no loopback), UEFI-native handoff,
data/hardware migration as first-class scope, per-distro plugin containment.
<!-- TODO: add academic literature on OS migration & unattended provisioning
(enterprise imaging: FOG, MDT, Clonezilla) and position against fleet tooling. -->

## 9. Failure modes and derived design rules (field notes)

Documented from real development on physical + virtual hardware. Each generalizes.

1. **The default-override trap.** debian-installer's `partman-auto/method`,
   when set alongside `init_automatically_partition select biggest_free`,
   silently escalates to whole-disk wipe. *Rule:* for destructive operations,
   installer automation must be tested against the *absence* of every adjacent key,
   not just the presence of the desired one. (S1/S2.)
2. **Installer ≠ one thing per distro.** Debian's standard installer initrd only
   supports whole-device CDs (cdrom-detect); booting from an ISO *file* requires the
   hd-media images, which then drive an offline install off the Debian Live image.
   *Rule:* the artifact that boots is a per-distro decision, not derivable from
   the ISO alone (`KernelUrl` in the boot spec).
3. **Late-hook environments are hostile.** busybox `mount` without LABEL=
   resolution; Ubiquity hooks that cannot mount vfat at all due to kernel/module
   mismatch; no `/proc` in chroots. *Rule:* two-phase bootstrap (§6); persistent
   `set -x` traces on disk for every unattended phase (S3).
4. **Signed GRUB prefixes are compiled in and undocumented.** Fedora searches
   `/EFI/fedora`, Debian `/EFI/debian`, casper images `/boot/grub`. *Rule:* write
   config to all candidate prefixes; cost is bytes.
5. **Installer preseeds under-deliver on desktops.** Ubiquity ignores d-i keymap
   keys (needs `layoutcode`); Mint 21+ added an unpreseedable-by-default codecs
   page (`use_nonfree`). *Rule:* treat installer automation as best-effort UX and
   the first-boot agent as the guarantee.

## 10. Evaluation

### 10.1 Method
<!-- TODO: define matrix: {distro} × {VM, ≥N physical machines} × {dual-boot,
replace} × {Secure Boot on/off} × {GPU vendor}; success criteria = R1-R5 checklist;
data-safety criterion = Windows partition bit-identical outside freed space. -->

### 10.2 Results to date (honest snapshot, August 2026)

All on the same physical machine (UEFI, NVIDIA RTX 5070, two 4K monitors,
no Secure Boot), dual-boot beside Windows 11 unless noted:

- Fedora KDE 44: end-to-end pass. NVIDIA driver built for the running kernel
  (the agent pre-installs the matching `kernel-devel-matched`, which prevents
  the akmods dependency chain from pulling a newer kernel mid-install), GRUB
  default pinned to the verified kernel, 4K at 144 Hz confirmed, wallpaper
  and file migration verified.
- Linux Mint 22 Cinnamon: end-to-end pass. NVIDIA driver install, display
  layout including rotation, keyboard layout (azerty), wallpaper verified.
- Debian 13 (GNOME): end-to-end pass. Offline squashfs install with no
  network until first boot; display layout including rotation, keyboard
  layout (azerty), wallpaper verified.
- Ubuntu LTS: pipeline complete, parked on the curtin disk-release defect
  documented in `distros/ubuntu/STATUS.md`.

Two failure modes found during this round generalize beyond iGloo and are
recorded here as additional field notes for section 9: the
`kernel-devel-matched` chain that silently upgrades kernels during akmods
driver installs (hard rich dependency, not a weak one), and EDID serial
collisions between identical monitor models that break EDID-based display
matching (both monitors reported the same serial `H1AK500000`; matching had
to fall back to connector identity).

## 11. Limitations and future work
- Single-developer bus factor; hardware matrix breadth; BitLocker-locked volumes
  unsupported (by policy); Secure Boot + DKMS distros need MOK story or preflight
  opt-out; rollback/undo (staged partition removal is manual today);
  accessibility and localization of the wizard itself.
<!-- TODO: pre-install disk-state snapshot for rollback; failure telemetry
(privacy-preserving); reproducible builds + signed releases for supply-chain
trust in iGloo itself. -->

## 12. Conclusion
<!-- TODO: restate the defensible claim: migration friction, not desktop quality,
gates adoption; iGloo removes it with a generalizable architecture; the moment
(W10 EOL + EU sovereignty) makes deployment impact plausible at policy scale. -->

## References
<!-- TODO: Canalys W11 estimate; Schleswig-Holstein migration; NGI/STF programme
docs; Wubi retrospective sources; debian-installer/preseed, kickstart, subiquity,
casper documentation; UEFI spec (BootNext); initramfs buffer-format kernel doc. -->
