# Deepin 25.2.0 ISO evidence

Read-only inspection on 2026-09-08; report completed 2026-09-09. No installer,
partition writer, host disk attachment or installed-system boot was executed.
This record supersedes prototype assertions where they conflict.

## Reproducible inputs

- Official [download selector](https://www.deepin.org/en/download/),
  [release directory](https://cdimage.deepin.com/releases/25.2.0/amd64/) and
  [checksum file](https://cdimage.deepin.com/releases/25.2.0/amd64/SHA256SUMS).
- Local `docs/reference/deepin-desktop-community-25.2.0-amd64.iso`:
  6,976,131,072 bytes; SHA256
  `f875c9a605bfe6a8425d1d353a3c1ec755bf37f5b0a3231ca19e2145da0ff450`.
- ISO9660 primary-volume label: `deepin`. Rock Ridge filenames can be read using
  Windows `tar`; do not infer Linux paths from a Windows mounted-ISO 8.3 view.
- Supplementary official source [7.0.50.1 tarball](https://community-packages.deepin.com/deepin/beige/pool/main/d/deepin-installer-reborn/deepin-installer-reborn_7.0.50.1.tar.gz),
  checked against its [.dsc](https://community-packages.deepin.com/deepin/beige/pool/main/d/deepin-installer-reborn/deepin-installer-reborn_7.0.50.1.dsc):
  `1c8daf240a52dd4ec2b1ef4f4c2defde5f544981eeb75ee182eff5498e1271b9`.
  This is **not** source for the shipped 7.0.60 executable. The historical GitHub
  `linuxdeepin/deepin-installer-reborn` redirects to a 2021 archive.

Safe initial inspection commands from the repository, without mounting media:

```powershell
$deepinIso = 'docs/reference/deepin-desktop-community-25.2.0-amd64.iso'
Get-FileHash -LiteralPath $deepinIso -Algorithm SHA256
tar -tf $deepinIso
tar -xOf $deepinIso boot/grub/grub.cfg
tar -xOf $deepinIso live/filesystem.module
tar -xOf $deepinIso live/filesystem.manifest
tar -xOf $deepinIso oem/settings.ini
```

Extract both squashfs files into a new temporary directory, then inspect with
`unsquashfs -ll` or extract to another new directory. They are data archives;
do not run their hooks or executables on a host. The initrd contains an initial
microcode CPIO followed by a legacy-LZ4 archive; inspect the decoded main archive,
not only the first CPIO.

Preserved extraction directories on the investigation machine:

- Windows `%TEMP%/igloo-deepin25-evidence`: ISO files.
- WSL Ubuntu-24.04 `/home/gillesduif/igloo-deepin25-extra-evidence`: installer layer.
- WSL `/home/gillesduif/igloo-deepin25-base-evidence`: base/immutable tools.
- WSL `/home/gillesduif/igloo-deepin25-initrd-evidence`: actual decoded initrd.
- Windows `%TEMP%/igloo-deepin-official-research/BUILD`: supplementary source.

These caches are disposable investigation inputs, not build dependencies.

## ISO and boot

| Path / object | Observed content |
|---|---|
| `boot/grub/grub.cfg` | Stock installer entries listed in STATUS.md; `nomodeset` only in explicit graphics fallback entries. |
| `EFI/BOOT/` | `bootx64.efi`, `grubx64.efi`, `grub.efi`, `mmx64.efi`; presence does not prove Secure Boot trust. |
| `isolinux/` | BIOS boot configs; no iGloo legacy validation. |
| `live/filesystem.module` | `filesystem.squashfs`, then `filesystem-extra.squashfs`. |
| `live/filesystem.squashfs` | 3,524,956,160 bytes; base OSTree content. |
| `live/filesystem-extra.squashfs` | 2,674,847,744 bytes; installer binaries, configs, hooks and persistent/extension OSTree content. |
| `live/initrd` | 217,233,554 bytes; paired with `live/vmlinuz.efi` (6.18). |
| `live/initrd-6.6` | 157,930,013 bytes; paired with `live/vmlinuz-6.6`. |
| `live/filesystem.manifest` | deepin-installer 7.0.60; immutable boot/ctl 1.0.31; live-boot 1:20210208-deepin. |
| `oem/settings.ini` | `DI_INSTALL_MODE="default"`; Deepin beige/crimson APT source; boot size 4096 MiB. |

The actual initrd's `usr/lib/live/boot/9990-cmdline-old` parses `findiso=`
(line 56), `fromiso=` (84), and `toram` (221).
`9990-misc-helpers.sh:246–253` mounts the containing partition read-only and
loop-mounts the ISO. `9990-toram-todisk.sh` copies the entire medium for plain
`toram`; `9990-main.sh` releases findiso/fromiso holders after that copy.
This proves code support, not successful no-USB boot on iGloo's NTFS partition.
Budgeting only the base squashfs for RAM is insufficient.

## Current installer and backend

Paths below are under `usr/share/deepin-installer/` in the extra layer unless
specified otherwise.

| Evidence | Consequence |
|---|---|
| `configs/settings/default_settings.ini` | Real INI keys include install mode, partition type/config, device list, locale, user/password, hooks, immutable mode and authorized-data preservation. Key existence is not proof of a safe combination. |
| `tools/deepin-installer-preinit`, `tools/functions/default_funcs.sh` | `pxe_update_oem()` handles `DI_SETTINGS_FILE_PATH` with a warning-only copy at line 1627. Ordinary configuration initialization uses the config tool to merge settings. OEM hooks are merged; `before_install()` does not propagate a hook failure. |
| `tools/scripts/init_environment.sh` | Runtime config `/etc/deepin-installer/deepin-installer.conf`; medium `/usr/lib/live/mount/medium/`. |
| `tools/functions/partition_funcs.sh` | Calls `deepin-installer-parted` using the partition mode and JSON policy. `setup_efi_parted` contains a first-ESP fallback. |
| `tools/hooks/before_chroot/02_parted_manager.job` | Can preserve authorization data by writing directly to the whole disk near sector 64; later cleanup can zero that region. Not a permitted root-only operation. |
| `tools/functions/bootloader_funcs.sh:95,107` | Uses `grub-install --force-extra-removable` and overwrites `grub*` in existing `EFI/ubuntu`. Do not reuse this bootloader hook wholesale. |
| `deepin-installer-command-agent` strings | Password-processing utility (`[-d] -p password`); not evidence of an installation RPC. |

Disassembly of the **shipped** binaries using `nm`/`objdump`:

- `libDeepinInstallerPartition.so`, `ConventionalPartition::diskTask` at `0xe590`
  iterates disk objects, calling `clearDeviceStatus` (`0xea81`) and
  `setDisklabelType` (`0xea60`) without testing the object's operation. A
  `type: disk` row must never be passed for a preserved disk.
- `CommonFunc::editPartition` at `0x579e0` calls `getPartitionPath` (`0x57aaa`)
  before its format decision (`0x57b26`). The resolver at `0xa3100` calls
  `ped_disk_get_partition_by_sector` (`0xa31a9`), not a GUID/ownership comparison.
  Supplying an apparent partition edit is not an exact target guarantee.

Supplementary 7.0.50.1 source gives readable context: automatic `CustomMode`
falls back to other eligible devices (`PartitionAutoManager.cpp:351`); automatic
manual type 1 is unsupported in its dispatch; missing/noninteger CLI mode becomes
zero; policy parsing permits absent fields; edit resolution uses the supplied
extent midpoint and can return success when the initial path is absent.
These remaining details are **not assumed identical in 7.0.60** without further
binary or isolated runtime evidence.

## Deployment and mutable paths

`tools/functions/ostree_funcs.sh` and hooks `09_ostree_sys_init.job`,
`10_ostree_persistent_init.job`, `13_mount_persistent.job`, and
`ostree/02_setup_ostree.job` show base checkout, extension checkout from
`persistent/ostree`, `deepin-immutable-mount-root`, and
`deepin-immutable-ctl admin deploy` followed by backup/finalization.
Empty boot/data-device branches use directories inside root. This supports
investigating one owned root plus an explicitly selected existing ESP.
It does not prove a complete install or justify executing the whole hook chain.

The shipped base `usr/bin/deepin-immutable-mount-root` mounts `usr:ro`,
`opt:rw`, `etc:rw` (line 296) and writable `var` (357–358).
The persistent hook maps `/usr/local` to `/var/usrlocal` (82–84).
APT, apt-get and dpkg route through `immutable-adapter.sh` to
`deepin-immutable-ctl admin exec --`. Preserve these mechanisms.
OEM default mode keeps Deepin account/OOBE setup; agent activation must follow
actual account creation. Locale/timezone/user configuration has its own hook
stages and must be audited before being reused.

## File fingerprints

SHA256 values allow a future reviewer to distinguish this exact image from an
updated binary with the same command names. Library basenames below identify the
ELF objects extracted from the extra layer.

| File | SHA256 |
|---|---|
| `usr/bin/deepin-installer` | `0607f05e7d6fde5a00c6016b16258d0c6c9e9099b3c271d5967c1a2a14f8e496` |
| `usr/bin/deepin-installer-parted` | `410eb2ae66524b8a8885ea013cd808007b6b5944bcd955b2b07edff2a360f05f` |
| `libDeepinInstallerPartition.so` | `7da4ff11b5c4fbc019e32ac740ce85dfb107fb1d99c97ce5e442c68a55db0a05` |
| `partition_funcs.sh` | `2d72101c72c8000eed80a3332e649031c78ba7ceb7f026f76821bd2c61e8de39` |
| `ostree_funcs.sh` | `ec418e09b5ed8c57e552b2e9c28f36df876c6f203a2c361d9256386aa085aee5` |
| Initrd `9990-cmdline-old` | `7b9ce4cc3b5be151d793f46f25276011e7bbafcb49417ae80cdf4e9830a5c0dd` |
| Initrd `9990-misc-helpers.sh` | `6db3c7c8dc99b00d1a11c8e705995211f059e42e168aa243412fac7449b7da57` |
