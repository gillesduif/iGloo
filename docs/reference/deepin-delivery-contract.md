# Historical Deepin prototype delivery contract — superseded

> This document describes the rejected prototype, not the supported design.
> The 2026-09-08 independent review found its disk guard insufficient and removed
> its executable hooks, partition policy and core ISO extraction changes.
> Claimed historical tests below were not rerun or accepted as validation.
> Use [the current decision record](../../distros/deepin/STATUS.md) and
> [prototype review](../../distros/deepin/REVIEW.md) for implementation decisions.
> Deepin remains `coming-soon`; do not reinstate this delivery path.

What `DeepinPlugin` puts on the staged volume, what runs it, and why each choice is the
one that is safe on a disk that still holds Windows.

Companion to [deepin-installer-findings.md](deepin-installer-findings.md), which is the
reconnaissance. This file is the design, and it is the document to update when the
mechanism changes.

**Provenance.** Everything about the installer's own behaviour below was read out of
`live/filesystem0.squashfs` on `deepin-desktop-community-25.2.0-amd64.iso`
(`/usr/share/deepin-installer/` and `/usr/bin/deepin-installer`), 2026-09-07. Line
references are to that tree. The four `deepin-installer-parted` canary runs and the
completed-install probe come from the findings document.

> **Enforcement is not written yet.** Under `DI_PARTITION_TYPE=6` the installer computes the
> layout; the guard records it and lets it run. The rules for refusing an unacceptable one
> come from what those records show. The first VM run (2026-09-08) is in
> [The first VM run](#the-first-vm-run-2026-09-08).

## What lands on the volume

Igloo unpacks the ISO onto the installer volume (`ExtractIsoToVolume`), so the volume *is*
the live medium and `DI_LIVE_DIR_ENV` (`/usr/lib/live/mount/medium/`) resolves to it. On
top of the unpacked image the plugin writes:

```
/settings.ini                                       overrides, named on the kernel cmdline
/oem/hooks/before_install/10_igloo_prepare.job      measure the disk, render the policy
/oem/hooks/before_install/90_igloo_verify.job       verify, and install the interlock
/oem/hooks/before_install/igloo_partition_policy.json.template
/oem/hooks/before_install/igloo_settings.env        the same keys, replayed through the CLI
/oem/hooks/in_chroot/50_igloo_agent.job             the first-boot agent's bootstrap
/igloo-agent/…                                      the agent payload itself
/migration-manifest.json
```

Kernel command line, verbatim from deepin's own install entry in `/boot/grub/grub.cfg`
minus `splash`:

```
boot=live union=overlay livecd-installer locales=<locale> DI_SETTINGS_FILE_PATH=/settings.ini console=tty --
```

`boot=live` satisfies `is_livecd`, `livecd-installer` satisfies `is_livecd_install`;
without the second one the live desktop starts and no install happens at all.

## What runs, in order

`deepin-installer-preinit` is the whole of the install-side sequence that matters. On the
`is_livecd_install` branch:

| # | Step | What it means for Igloo |
|---|---|---|
| 1 | `setup_live_workdir`, `setup_oem_squashfs` | the medium is mounted |
| 2 | `init_config` | the conf is built from deepin's defaults |
| 3 | `pxe_update_oem` | **`settings.ini` is copied off the medium and merged** |
| 4 | `init_workspace` | **`oem/hooks/*` are copied into the installer's own hooks tree** |
| 5 | `before_install` | **Igloo's two hooks run here** |
| 6 | `init_hookslist` | `DI_HOOKS_LIST` is set from `DI_INSTALL_MODE` |
| 7 | installer core (lightdm greeter script) | partitioning, unsquashfs/ostree, chroot stages |

Three consequences, and they decide the whole design:

- **`oem/live_hooks` is dead on this path.** `live_hooks()` is called only from
  `deepin-installer-preinit`'s `else` branch - the "Try deepin" desktop. With
  `livecd-installer` on the command line it never runs, and `init_live_workspace` never
  copies the directory either. A plugin that ships its logic there installs on deepin's
  defaults: `DI_PARTITION_TYPE=0`, whole disk, `DI_ENABLE_AUTO_SELECT_DISK=true`.
- **`oem/hooks/<stage>` works for every stage.** `init_workspace` does
  `cp -vfr $DI_LIVE_DIR_ENV/oem/hooks/* $DI_INSTALL_TOOLS_DIR_ENV/hooks/`, merging into the
  shipped stage directories. deepin ships `oem/hooks/in_chroot` and `oem/hooks/first_boot`
  on its own ISO, so this is the supported route, not a trick.
- **`before_install` is the last stage before the partitioner.** It runs after the
  settings merge, so it can check what the merge actually produced, and before the
  installer core, so nothing has touched the disk yet.

For the chroot stages, `setup_chroot_hooks` (`before_chroot/15_…`) does
`rsync -aHAXi --delete $DI_INSTALL_TOOLS_DIR_ENV/ /target/$DI_INSTALL_TOOLS_DIR_ENV`, which
carries Igloo's `in_chroot` job into the target; `hook_manager.sh` then runs anything under
`*/in_chroot/*` as `chroot /target /bin/bash -e <job>`.

## Why two interlocks

`settings.ini` delivery fails soft. The copy in `pxe_update_oem` is
`cp … || warning`, not `|| error`: a wrong path or an unmounted medium leaves deepin's
shipped defaults in force, and those defaults take the whole disk. Nothing in the installer
reports this.

`before_install` gives no cooperative way to stop, either - it runs each job as
`bash hook_manager.sh <job>` and ignores the exit status, and `error()` in deepin's own
function library is `echo` plus `exit 1`, which only ends the hook.

So the plugin verifies twice:

1. **`90_igloo_verify.job`, before the installer core.** Reads the merged configuration
   back through `deepin-installer-config get` and requires `DI_PARTITION_TYPE=6`,
   `DI_ENABLE_AUTO_SELECT_DISK=false` and a `DI_DEVICE_LIST` naming the disk the hook
   measured. If any of that is wrong it powers the machine off. Nothing has been written to
   the disk at that point, and an unexplained power-off is a far better outcome than a
   silent whole-disk install.
2. **A wrapper around `deepin-installer-parted`.** The same hook moves the binary to
   `deepin-installer-parted.igloo-real` and puts a guard in its place, so the check happens
   at the moment of the call rather than being trusted from a minute earlier. `-m 4`
   (mounting) writes nothing to the partition table and passes straight through; every other
   call is recorded first - see below.

The wrapper never reaches the installed system: `setup_chroot_hooks` mirrors the tools
directory, not `/usr/bin`.

### The capture

Under `DI_PARTITION_TYPE=6` Igloo no longer supplies the layout, so the enforcement rules
have to be written from what the installer actually does. The guard therefore dumps, for
every partitioning call:

- the argv it was invoked with, and the mode and policy path parsed out of it;
- the contents of the file `-c` names - the layout the GUI generated;
- Igloo's own reference policy, rendered by `10_igloo_prepare.job` to
  `/tmp/igloo/igloo_reference_policy.json`, to diff against;
- every `DI_*` key in the runtime conf, which answers whether the GUI overwrote
  `DI_MOUNTS_POINTS` and `DI_DEVICE_LIST`;
- `sfdisk -d`, `sfdisk -F` and `lsblk` for the target disk, i.e. the table *before*.

It writes that to `/tmp/igloo/capture/parted-<stamp>.txt` and then appends it to
`/var/log/deepin-installer/deepin-installer-preinit.log`, because the live filesystem is a
tmpfs overlay and that log is one of the four files the installer's own **Save Log** button
exports to a USB stick. Then it `exec`s the real binary, so the install proceeds.

The open question the capture answers is the second-ESP one (#227): `autoCustomDisk` reads
`DI_EFI_SIZE_CONFIG` and `DI_EFI_PARTITION_FS_CONFIG`, so it may well create its own 300 MiB
ESP in the free region rather than reuse the one Windows already has.

## Decisions

### DI_PARTITION_TYPE=6, because 1 does not exist unattended

`PartitionAutoManager::start` is a `switch` on `DI_PARTITION_TYPE` with an eight-entry jump
table (`.rodata` at `0x233e34` in `/usr/bin/deepin-installer`). Read out of the binary:

| Value | Handler |
|---|---|
| 0 | `autoFullDisk` |
| **1** | default branch - `"Unknown unattended installation mode <%1>"` |
| 2 | `autoFullDiskEncryption` |
| 3 | `autoSaveData` |
| 4 | default branch - fails |
| 5 | default branch - fails |
| **6** | `autoCustomDisk` |
| 7 | `autoLVMFullDisk` |

So "manual partitioning" only exists when a human drives the GUI; with any `auto-*` install
mode it fails outright, which is what the first VM run hit. **6 is the only value that both
installs unattended and leaves the rest of the table alone**, and it settles findings open
item #6: the `default_settings.ini` comment ("single disk, multiple systems") and the
positional enum in `partition_funcs.sh` ("CustomMode") describe the same thing, while 5 is
supported by neither.

`autoCustomDisk` calls `findFreePartition()`, `PartitionCustomManager::findDev()`,
`autoCustomDiskNewPartition()` and `checkPartition()`. There is no delete or wipe call in
it, and `findFreePartition` only walks `getSystemPartitionInfo()` - it finds free space and
creates partitions there.

**This inverts the ADR-008 posture and that needs deciding, not just noting.** Igloo does
not hand over a layout any more; the installer computes one. What Igloo can still do is
refuse an unacceptable one at the call site, which is what the guard is for. Whether
"installer proposes, Igloo vetoes" satisfies ADR-008 as written is an open question for
that ADR, not something this document settles.

### The partition policy carries no disk entry

The task of describing the disk "as it already is" cannot be done safely, so the plugin
does not attempt it. A `type: "disk"` entry was measured to run `wipefs -a -f` on the whole
device followed by a fresh `parted mklabel`, **whatever `operate` says** - `"edit"` is not
an edit - and to exit 0 afterwards. That is true regardless of whether the `diskType` value
matches the disk. `deepin-installer-parted` also does not need the entry: runs 2-4 of the
canary left the table and the existing filesystem untouched without one.

`DeepinPartitionPolicy` therefore has no field for a disk label anywhere in its types. The
detected table is still read on the live side - `10_igloo_prepare.job` refuses anything but
`gpt` - but it is used to refuse, never to declare.

### The ESP is reused, never formatted

*(Under mode 6 this describes Igloo's reference policy and what the guard will be asked to
enforce - not what the installer generates, which is unknown until the capture run.)*

The Windows ESP is declared `operate: "edit"`, `isneedformat: false`, `mountPoint:
"/boot/efi"`, with its real `path`, `partUuid` and sector range. `isneedformat: false` was
measured to invoke no `mkfs` at all (a logging wrapper on `mkfs.vfat` saw no call, and the
filesystem UUID was unchanged).

Naming it in `DI_MOUNTS_POINTS` matters twice over. `setup_efi_parted` prefers whatever is
already mounted at `/target/boot/efi` and only otherwise searches the whole machine and
takes the first EFI partition `fdisk` reports - which is how a second ESP, or the wrong
one, gets picked. And `shrink_filesystem`, which resizes every declared ext4/btrfs
filesystem down by 35 MiB, skips `/boot/efi` and `/boot` by mount point.

Under `DI_IMMUTABLE_SYSTEM=true` this still holds: `before_chroot/13_mount_persistent.job`
calls `mount_partitions` (which mounts everything in `DI_MOUNTS_POINTS` except `swap`) and
then `setup_efi_parted`. The findings document scoped the ESP behaviour to the non-OSTree
branch; it applies to both.

### `DI_INSTALL_MODE=auto-no-first-boot`

`init_hookslist` only adds the `user_config` stage to `DI_HOOKS_LIST` when the mode
contains `no-first-boot`. That stage is what creates the account, the password and the
hostname (`user_config/02_setup_user_info.job`). Under plain `auto-install` those run at
first boot instead, from deepin's own first-boot service - competing with Igloo's agent,
which is ordered `Before=display-manager.service`.

### OSTree is left on

`DI_IMMUTABLE_SYSTEM=true` is deepin's default and is kept. Every path the agent writes to
(`/opt`, `/etc`, `/etc/grub.d`, `/etc/xdg/autostart`, `/var/lib`, `/boot`, `/boot/efi`) was
found writable on that branch and survived a reboot; only `/usr` is read-only. Turning it
off would take the `unsquashfs_filesystem` branch, which produces a layout deepin does not
ship and probably does not test.

The cost is that `ostree admin status` does not work on the result ("no /boot/loader
directory"), so nothing in Igloo may be built on stock ostree tooling, and that survival
across an ostree update that replaces the deployment is still unproven.

### The agent gets its own systemd unit

`oem/hooks/in_chroot/50_igloo_agent.job` writes `/opt/igloo/igloo-bootstrap.sh` and
`igloo-bootstrap.service` into the target and symlinks the unit into
`multi-user.target.wants`. It cannot copy the agent itself: the staged volume is not
reachable from inside the chroot. As on the Debian and Mint paths, the bootstrap mounts the
`OEMDRV` volume by label on the first boot of the installed system, copies `igloo-agent/`
and the manifest in, and executes `first-boot.sh`.

It has to be Igloo's own unit. deepin's first-boot chain removes its own service in
`first_boot_cleanup`, and `DI_FB_HOOHS_LIST` (deepin's typo, not a transcription error)
only lists `user_config` and `first_boot_cleanup`.

### Mount points are predicted, then corrected

`DI_MOUNTS_POINTS` has to be set before partitioning, but the partition *numbers* are
assigned by the partitioner. `10_igloo_prepare.job` predicts them (lowest free numbers, in
policy order) so the configuration is complete, and the guard wrapper rewrites the value
from the real partition table after phase 1 succeeds, matching each partition by the start
sector the policy asked for. A wrong prediction that the wrapper somehow failed to correct
costs a failed mount and a failed install - never the wrong partition.

### No swap partition

The layout is ESP + 4 GiB `/boot` + 23 GiB `/` + the remainder as `/persistent`, matching
`DI_BOOT_SIZE_CONFIG`, `DI_ROOTA_SIZE_CONFIG` and `DI_PERSISTENT_SIZE_CONFIG`. deepin's own
GUI install also creates swap; Igloo does not, which costs hibernation. `setup_swap` reuses
an existing swap partition if the machine has one and does nothing otherwise.

## Answers to the open items

**Open #4 - can `DI_MOUNTS_POINTS` be supplied through settings.ini?** Moot for Igloo, and
therefore not on the critical path either way. The value is a list of Linux device paths,
which cannot be known on the Windows side at render time: the disk has not been seen by a
Linux kernel yet, and the partitions do not exist. It is written from the live system by
`10_igloo_prepare.job` through `deepin-installer-config set` - the fallback the task
describes - and corrected by the guard wrapper once the partitions exist. Whether the
merge would also accept the key from `settings.ini` is untested and no longer matters.

**Open #5 - where does the agent go?** Resolved above: `oem/hooks/in_chroot/`, which
`init_workspace` copies into the installer's hooks tree and `setup_chroot_hooks` mirrors
into the target. The recon's conclusion that no medium-sourced path exists for stages other
than `live_hooks` was drawn from the live session; the ISO's own
`oem/hooks/{in_chroot,first_boot}` directories and `init_workspace` show otherwise.

## The first VM run (2026-09-08)

A Windows VM, iGloo's Windows side, then the generated GRUB entry. What it settled:

**The delivery chain works end to end.** From `deepin-installer-preinit.log`:

```
pxe_install_type is : none
setting_ini_path is : /settings.ini
'/usr/lib/live/mount/medium//settings.ini' -> '/tmp/oem_settings_from_grub/settings.ini'
'…/oem/hooks/before_install/10_igloo_prepare.job' -> '…/tools/hooks/before_install/…'
'…/oem/hooks/in_chroot/50_igloo_agent.job' -> '…/tools/hooks/in_chroot/…'
igloo-prepare: target disk /dev/nvme0n1 (from /dev/nvme0n1p4)
igloo-prepare: reusing ESP /dev/nvme0n1p1 (index 1, 200 MiB, never formatted)
igloo-prepare: free region 354899968s..501700607s (71680 MiB)
igloo-verify: guard installed at /usr/bin/deepin-installer-parted
```

So: the local `cp` branch is the one taken, `settings.ini` arrives, `init_workspace` copies
`oem/hooks/*` into every stage directory (Igloo's `50_igloo_agent.job` lands beside deepin's
own three in `in_chroot`), the target disk is identified from the OEMDRV volume's parent,
and the 200 MiB **Windows** ESP is found rather than a new one.

**And it failed at the GUI, before any partitioning.** `deepin-installer.log`:

```
DI_INSTALL_MODE: "auto-no-first-boot"
PartitionAutoManager::start  auto-installMode = 1
```

then the failure screen: `Unknown unattended installation mode <1>`. No
`deepin-installer-parted` call, no guard output, disk untouched. That is what sent
`DI_PARTITION_TYPE` from 1 to 6.

Two smaller observations from the same run: the installer rewrote the clock two hours back
when it applied `DI_TIMEZONE`, and `DeviceManager` reports the Windows ESP with
`"isEFIPartition": false` while still listing it as vfat - worth remembering if the
installer's own ESP detection ever has to be reasoned about.

## Before bare metal

In order:

1. **A full run in a VM.** Mode 6, guard records and continues, `Save Log` off the machine
   afterwards. It answers at once: what layout `autoCustomDisk` produced, whether it reused
   the Windows ESP or made a second one, whether the GUI overwrote `DI_MOUNTS_POINTS`, and
   whether the `in_chroot` hook put `igloo-bootstrap.service` in the target.
2. **Write the enforcement rules** from those records, before any run on a disk carrying
   real data. Whether "installer proposes, Igloo vetoes" satisfies ADR-008 has to be
   answered at the same time.
3. **Does phase 1 leave undeclared partitions alone?** Findings open #1. The canary disk
   carried one partition and it was declared. This must be tested on a multi-partition disk
   before any run on hardware carrying real data.
4. **`isneedformat: false` on a real disk**, not a loop device.
5. **Agent survival across an ostree update** that replaces the deployment.
6. `status` in `distro.json` reads `available`; it should not stay that way past a run on
   a machine with real data unless step 2 is done.
