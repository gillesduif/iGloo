# Community installer ESP binding audit — 2026-09-27

Issue #241 remains open. **Preparation and registration remain blocked.** This
checkpoint inspects the actual configured installer media and adds pure identity
translation plus a Fedora ESP-only directive. It does not implement a complete
installer adapter, Linux inventory collector, native Windows storage writer or
permission to install. RecoverySnapshotV1 and its Exact rules are unchanged.

The subsequent [complete installation ownership continuation](community-installation-ownership.md)
adds raw root-partition receipts, the complete Fedora storage-section generator,
and a read-only Linux inventory collector/protocol. It supersedes the earlier
"ESP-only directive / collector missing" implementation checkpoint below, while
retaining its shipped-media evidence and Debian/Mint selection blockers. All
production installer paths remain blocked; a complete Fedora storage section is
not a complete enabled installer.

See the later [Debian-only deployment protocol](debian-target-root-deployment.md)
for the candidate debootstrap boundary and actual Trixie package-hook findings.
It keeps Debian blocked and prevents legacy preseed export; it does not change
the multiple-ESP findings below or enable any native preparation.

The later [target-root deployment audit](target-root-deployment.md) records the
complete Debian candidate lifecycle, actual Mint image transformations and signed
package side effects. New evidence includes Mint's removal manifest containing
signed boot packages and shim selection depending on Secure Boot/revocation state.
Exact mount and receipt contracts are implemented; distro replacement engines are
still not qualified. These findings do not reopen the rejected partman selectors.

The invariant is two ESPs: preserve the Windows ESP and reuse the prepared
iGloo/Linux FAT32 ESP without formatting it. OEMDRV remains payload only. The
Windows ESP must remain eligible and present; changing its flags/type, hiding it,
changing enumeration order, selecting the first ESP or adding a third ESP is not
an acceptable solution.

## Shipped artifacts inspected

The user confirmed no local images existed. The audit downloaded Fedora and Mint
images, Debian hd-media initrd, and selected Debian ISO byte ranges. Extraction
used ISO directory records, `unsquashfs` and `dpkg-deb --extract`; no installer,
package maintainer script, partitioning operation or firmware operation was run.
No ISO was mounted. SELinux xattrs were not needed for source inspection.

Artifacts are retained under `%TEMP%\igloo-installer-audit-241` on the audit host.
They are audit downloads, not installed into the application's ISO cache. The
[source-byte manifest](installer-esp-evidence.json) records extracted paths,
lengths and SHA-256 for the decisive scripts. Full-image hashes establish content
identity, not installer safety, bootability or signature authenticity. Published
checksum signatures were **not** independently verified in this audit.

| Integration / artifact | Actual shipped implementation | Acquisition evidence |
| --- | --- | --- |
| Fedora KDE: [Fedora-Everything-netinst-x86_64-44-1.7.iso](https://download.fedoraproject.org/pub/fedora/linux/releases/44/Everything/x86_64/iso/Fedora-Everything-netinst-x86_64-44-1.7.iso) | `.buildstamp`: Fedora 44, final, Everything, build 202604221336.x86_64, Lorax 44.6-1. Extracted Anaconda **44.30**, pykickstart **3.69**, Blivet **3.13.2**. KDE is selected by the iGloo package recipe, not a different live installer. | Complete 1,217,329,152-byte ISO SHA-256 `bd285201494dd0ba09b54d05ac707de1401668b8512a573edb5922dcf9d7067e`, matching the downloaded published CHECKSUM. `images/install.img` extracted read-only. |
| Debian configured `current-live` + trixie `current/images/hd-media` | On this date the live URL resolves to **13.7.0 GNOME**. Actual hd-media `/etc/lsb-release`: **13 (trixie), installer build 20250803+deb13u7**; anna 1.99, iso-scan 1.98. Actual live ISO udeb pool: **partman-efi 110**, **partman-auto 177**, **grub-installer 1.212**, live-installer 58. iGloo boots d-i hd-media, not the live image's Calamares 3.3.14-1. | Complete initrd SHA-256 `586d75b66f63c2f11e68f7c01f0ccb4e7c0a36608f86bce085bba20a68c09b7f`, matching images/SHA256SUMS. Selected udebs read from ISO ranges and checked against that ISO's `sha256sum.txt`. Full Debian ISO was **not** downloaded or hash-verified. |
| [Linux Mint 22.3 Cinnamon amd64](https://mirrors.edge.kernel.org/linuxmint/stable/22.3/linuxmint-22.3-cinnamon-64bit.iso) | `.disk/info`: Zena, release amd64 20260108. Manifest and installed dpkg status: **Ubiquity 24.04.3+mint19**, bundled partman, **casper 1.498**; GRUB EFI 2.12-1ubuntu7.3, signed GRUB 1.202.5+2.12-1ubuntu7.3, shim-signed 1.58+15.8-0ubuntu1. | Complete 3,091,660,800-byte ISO SHA-256 `a081ab202cfda17f6924128dbd2de8b63518ac0531bcfe3f1a1b88097c459bd4`, matching the repository pin and published checksum. Actual `casper/filesystem.squashfs` inspected. |

Debian input URLs are rolling; these findings must not silently certify a later
`current` resolution. The live ISO's published full-image SHA-256 was
`e94859a83b305cae5125dc9c6080ced040c59d099ea7381e7f300fd1dba87a17`.
Downloaded udeb hashes matched the ISO's internal list:

- partman-efi 110: `ca596467e15ce55a9ef7351bda33d49d08b384129f7bd2bb60d46c08b6abdc9e`
- partman-auto 177: `6336439b8349df6b2f0b2ecf3ca19091e12da56d5793df3dab189c36d4a90e1f`
- grub-installer 1.212: `f563ab6bf4f079de5d2ecef83c80aa4c5ae109498f4534296661722784d8eb43`
- live-installer 58: `b60df57cd03c6a13b853ef78360ea563020cf7fe04d4de999e1465d38a954b72`

## Shared declaration and pure runtime translation

`Igloo.Core/Preparation/InstallerEspBinding.cs` adds `InstallerEspBindingV1`:
prepared generation, canonical target disk, distinct Windows/Linux ESP and payload
volume identities, plus their filesystem UUIDs. A Windows volume GUID is not a FAT
filesystem UUID. Declaration checks the existing prepared-layout structure and
content evidence; it does not turn supplied file hashes into independent readback.
The caller must reopen and revalidate the preparation checkpoint first.

`Resolve` takes a typed, complete runtime inventory observation. It matches GPT
partition GUID to parent GPT disk GUID, size/sectors, geometry, partition type,
FAT32 and filesystem UUID. It rejects missing, duplicate, overlapping, changed or
incomplete evidence, including another partition with the same filesystem UUID.
Windows/Linux aliases are rejected. `/dev/...` names are ephemeral output only;
changing enumeration order or Windows/Linux ordinals does not change ownership.
Generic `vfat` is insufficient evidence of FAT32 rather than FAT16.

This is a **pure translator**, not a production collector. It cannot authenticate
cloned disk contents or independently acquire Windows hardware unique-ID
equivalence on Linux. The binding retains the canonical physical identity; a
future collector must establish its supported translation and complete inventory,
including honest states for unformatted/unsupported partitions, before using the
result to authorize installation. Unsupported acquisition must not be filled with
synthetic defaults. No runtime device path is persisted as ownership authority.

## Fedora: supported ESP directive, incomplete complete-disk recipe

In the actual image, pykickstart `commands/partition.py` accepts an existing
partition and no-format. Anaconda
`modules/storage/partitioning/custom/custom_partitioning.py` resolves `onPart`
through Blivet, sets the mountpoint, and returns from the no-format branch before
creating a format. Blivet `devicetree.py` resolves both UUID and PARTUUID.
Anaconda's EFI platform requires the stage-one device mounted at `/boot/efi`.
These agree with the supported [Kickstart partition contract](https://pykickstart.readthedocs.io/en/latest/kickstart-docs.html#part-or-partition).

After the complete synthetic inventory passes, the new pure generator emits:

```text
part /boot/efi --onpart=UUID=<unique prepared Linux ESP filesystem UUID> --noformat
```

It emits no resize, format, clearing or automatic-partition instruction. Windows
is never the nominated ESP. However this is **only an ESP directive**. It is not
wired into the current `autopart` recipe: explicit partitions and autopart conflict.
The existing replace-mode `clearpart --all --initlabel` is also incompatible with
Windows preservation. Both remain behind the preparation guard.

The root allocation is currently a reserved extent, not an independently observed
owned Linux root partition. A complete recipe must bind that exact intended
allocation, preserve all Windows partitions and reject additional allocations;
simply adding `--ondisk`/`--grow` or the directive above does not prove this. That
recipe and runtime collector remain missing. No claim of end-to-end Fedora Windows
preservation or installed-system boot success is made.

The continuation inspected this gap down to the current contracts: the space plan
can set `PreCreateRoot`, but its root allocation has an empty filesystem and the
receipt still requires `CanonicalVolumeIdentityV1`. `VerifyCreation` correctly
rejects an empty filesystem through canonical **volume** validation. An unformatted
GPT partition is not an observed formatted volume; inventing a filesystem or volume
GUID is prohibited. Thus toggling `PreCreateRoot` cannot supply a truthful owned
root receipt. The next storage-contract change must represent partition creation
identity independently of formatted-volume identity, without weakening recovery
volume validation. A future explicit root `--onpart=PARTUUID=...` recipe can then
be based on a real, independently reopened receipt. The shipped parser removed
`--start`/`--end` in Fedora 14; current `--size`/`--ondisk` cannot express the exact
reserved extent instead. No complete recipe was emitted from insufficient inputs.

The actual Anaconda EFI backend installs its persistent boot target during the
bootloader installation stage. `bootloader/efi.py` invokes `efibootmgr` with the
chosen stage-one disk/partition and loader path; ordinary creation uses `-c`, while
the keep-order path uses `-C`. Its normal replacement path can remove existing
entries with the Fedora product label. This is installer-owned NVRAM behavior,
not permission for Windows-side registration to modify BootOrder, and not proof
that every firmware entry is preserved. Disposable-VM inspection remains required.

## Debian: nominated ESP unsupported in the current unattended integration

Actual `partman-efi` `init.d/50efi` marks qualifying existing ESPs as method `efi`.
`fstab.d/efi` walks them and emits **the first** such partition as `/boot/efi`
(`seen_efi` prevents later candidates). `partman-auto` `lib/recipes.sh` implements
`$reusemethod{}` through a method search and internal partman partition IDs; it is
not a demonstrated public GPT-GUID selector. The existing `biggest_free`/`atomic`
recipe provides no stronger selection proof.

`commit.d/50format_efi` deliberately skips an existing detected filesystem, so
no-format reuse is possible. That does not select the correct one of two ESPs.
`grub-installer` uses `bootdev=dummy` for EFI and installs into the target's already
mounted ESP. Setting `grub-installer/bootdev` to a stable partition path does **not**
repair selection. Its bootloader installation stage follows target installation;
it exposes update-nvram and extra-removable choices and invokes target grub-install.
No exact resulting Boot####/BootOrder is asserted without runtime inspection.

No supported unattended exact-ESP mechanism was established in these shipped
components or the documented preseed recipe contract. `PartmanExistingEspDirective`
therefore returns `Unsupported / DebianNominatedEspSelectionUnproven`. This is a
limitation of the demonstrated integration, not a theorem that Debian cannot reuse
an ESP. Private method/ID file edits are not introduced. Windows preservation is
**not proven**, so installation remains unreachable.

An alternative with an official contract does exist at a lower level:
[Debian's trixie Unix/Linux installation procedure](https://www.debian.org/releases/trixie/amd64/apds03.en.html)
uses debootstrap into an explicitly mounted target, followed by system/kernel and
bootloader setup. That could remove partman's selection algorithm entirely. It is
not the current offline live-installer flow and does not automatically reproduce
its desktop/user/data configuration. Before adopting it, iGloo would need a pinned
bootstrap environment/package source, exact root and ESP mount verification,
configuration and migration ownership, signed bootloader installation, package-hook
effect control, and a durable interruption/recovery lifecycle before reboot. This
is a concrete candidate redesign, not a supported completed iGloo installer. No
bootstrap or bootloader command was executed during this investigation.

## Mint: critical stop condition confirmed in the shipped installer

Actual Ubiquity-bundled `usr/lib/partman/init.d/50efi` marks eligible ESPs. Its
`fstab.d/efi` collects them and chooses the first on `partman-auto/disk`, or the
first overall. A disk filter cannot distinguish two ESPs on that same disk.
Reversing enumeration can change the result. The existing Windows ESP must not be
made ineligible to influence this algorithm.

The format hook skips existing detected filesystems, but no-format alone is not
exact destination selection. The bundled grub-installer also uses the mounted
`/target/boot/efi` with a dummy EFI bootdev. Ubiquity's `plugininstall.py`
`configure_bootloader` invokes GrubInstaller after target deployment/configuration,
with the target mounts and efivars available. The final loader therefore depends
on the earlier partitioning/mount selection. Its installer can update NVRAM; exact
post-install variable identities/order have not been exercised here.

The shipped command interface supports `--no-bootloader`, and templates expose
`ubiquity/install_bootloader`, `ubiquity/success_command` and `partman/early_command`.
These do not provide an exact nominated-ESP partition input. Skipping the final
bootloader call and adding a success hook does not prove that earlier mount,
partition-commit or package stages leave Windows untouched. `--only` changes UI
mode, not partitioner ownership. No private partman-state patch is an acceptable
substitute.

`PartmanExistingEspDirective` returns
`Unsupported / MintNominatedEspSelectionUnproven`, even when both ESP identities
translate successfully. **Mint remains a blocker.** A different Mint strategy
would need a supported deployment/partitioning interface that preserves both ESPs
and nominates one explicitly, plus a separate supported bootloader step if needed.
Replacing Ubiquity with an independently implemented deployment engine, substituting
LMDE or another installer is not an established Cinnamon integration and is a
separate architecture/product decision. No silent switch or Windows compromise
was made.

### Mint no-bootloader alternative: supported pieces, incomplete lifecycle

Additional files were extracted from the same checksum-verified Mint image:
the grub-install manual, `usr/lib/grub/grub-multi-install`, and the signed GRUB/shim
post-install scripts. They establish the following limited conclusions:

1. `ubiquity --no-bootloader` / `ubiquity/install_bootloader=false` skips
   `configure_bootloader`'s GrubInstaller invocation. It does **not** skip partman,
   identify root storage, or redefine the ESP mounted by partman.
2. The shipped grub-install manual supports `--efi-directory` for an explicitly
   mounted ESP, `--bootloader-id`, `--no-nvram`, and `--uefi-secure-boot` when
   grub-efi-amd64-signed is installed. Those are supported tools for a prospective
   explicit final boot step; they are not a signed-chain or firmware acceptance
   proof by themselves. No `--force`, unsigned replacement or Secure Boot disabling
   workaround is proposed.
3. The current `ubiquity/success_command` hook executes before the completion reboot
   and could host an explicit final step. But correctness requires more than moving
   the final grub-install call there. A failed hook must reliably prevent completion
   and reboot, preserve the handoff chain, record uncertain writes, and permit
   recovery from a partially installed root or EFI directory. None of those
   journal/recovery guarantees is implemented in the current templates.
   More specifically, the shipped `frontend/base.py::run_success_cmd` discards
   `execute_root`'s boolean result. `misc.py::execute` converts a nonzero command
   exit to `false`; `frontend/noninteractive.py` then reports completion and can
   reboot without testing that result. The GTK path also ignores the hook result.
   Thus a nonzero exit from a proposed bootloader success hook is **not** a proven
   abort-before-reboot mechanism.
4. Actual signed GRUB/shim postinst scripts can invoke `grub-multi-install` when
   `/boot/grub/x86_64-efi/core.efi` exists. That helper uses
   `grub-efi/install_devices`, but if `/boot/efi` is mounted from a device outside
   the configured list, it **adds that mounted device to the proposed list** and
   raises the changed-devices prompt. Preseeding only the Linux ESP while partman
   mounts Windows therefore is not a sufficient ownership proof. Subsequent
   package upgrades must retain the correct nominated ESP too.

The missing transition is a supported, deterministic **OS deployment and mount
setup** that never nominates Windows before the explicit final loader step, with
bounded package-hook effects and failure handling. The examined Ubiquity public
interface does not establish it. A root-only deployment strategy plus explicit
signed loader installation remains an architectural candidate, not an implemented
workaround. Secure Boot/SBAT/revocation compatibility and the final automatic
persistent boot entry would additionally require disposable-VM validation. No
manual UEFI intervention is accepted as the fallback product flow.

## Payload discovery remains incomplete

No production template was changed: emitting a partial recipe would suggest a
complete binding that does not exist. The existing unsafe/default paths remain
unreachable through the production preparation guard.

- Fedora plugin kernel arguments still use `inst.stage2=hd:LABEL=...` and
  `inst.ks=hd:LABEL=...`. Stable UUID support must be connected to a pinned runtime
  collector and checked across the full stage2/seed pipeline; it is not yet wired.
- Debian hd-media `iso-scan.postinst` enumerates devices. Its filename setting does
  not establish ownership of the containing partition. Current `/debian.iso`
  selection is not an exact OEMDRV binding.
- Mint casper `casper-premount/20iso_scan` passes `/mint.iso` to `find_path`;
  `casper-helpers` scans devices. A matching filename or `live-media=` argument
  alone does not prove that this earlier scan is constrained to the nominated
  OEMDRV. The current copy-to-RAM arguments are not ownership evidence.
- Existing seed/agent/log lookup still includes label-based or broad mounted-media
  discovery. No complete duplicate-label pre-reboot validation exists. None of
  these searches is classified as exact by the new model.

## Next gate and validation

All three distros cannot yet be certified to reuse the nominated ESP. The immediate
gate is supported exact Debian/Mint partition selection, a complete Fedora root
recipe, and exact runtime/payload identity acquisition. Signed shim/GRUB pairing,
prefix/configuration discovery and independent content validation remain required
before native Windows create/format/readback orchestration. The latter is a later
continuation, not enabled by these pure helpers.

Added 31 deterministic cases for declaration, two ESPs on one disk, ordinal/order
changes, identity mismatches, duplicates, overlapping/invalid geometry, filesystem
qualification, no-format Fedora output and explicit Debian/Mint rejection. They
do not simulate a successful unsupported installer. See the Fleet phase log for
exact build/suite totals. Real end-to-end Fedora/Debian/Mint installation on
disposable Windows clones remains mandatory before any support claim. It was not
performed, and no developer-host storage, firmware, BCD or registry was changed.

## Complete ownership continuation (2026-09-27)

The Fedora model now requires **pre-created, independently verified ESP and root
partitions**, not a free root extent. `PreparedStorageOwnershipV1` retains raw GPT
creation receipts separately from formatted-volume identity. The complete recipe
binds root by PARTUUID and the no-format ESP by filesystem UUID, preserves the
before-partition set, and emits no autopart or destructive clearpart. The actual
extracted pykickstart 3.69 F44 parser accepted the entire five-command storage
section. This is syntax/contract evidence, not an executed Anaconda transaction.

The Linux collector obtains typed GPT/filesystem observations using read-only
util-linux commands and two matching independent passes. The pure common resolver
checks generation, all owned/preserved partition identities, parent disk and
geometry, filesystem identities and global visible UUID uniqueness. Linux device
paths are results. Native packaging, mount verification, physical hardware-ID
equivalence, payload integration and post-install checks remain incomplete.

Additional shipped Fedora initrd extraction proves UUID transport syntax for
stage2/kickstart, now emitted by the new pure generator. The legacy post-install
label scans are still blocked and must be replaced. Debian Trixie package-script
inspection and Mint's complete deployment/bootloader-hook review are recorded in
the [continuation](community-installation-ownership.md), including source hashes
in the manifest. Neither debootstrap nor a Mint rootfs/OS-only alternative is
implemented or certified as a complete iGloo installation engine.

Final full-path status is **Blocked for Fedora, Debian and Mint**. Fedora's explicit
storage sub-contract is implemented; Debian and Mint still require complete
supported replacement strategies. Native preparation cannot be enabled from
Fedora's result alone. The Fleet phase log records this continuation's exact tests.
