# Historical Deepin prototype research — unverified unless corroborated

> Preserved as research history. These notes mix old source analysis, claimed
> live-session observations and prototype assumptions. They are not evidence
> of a safe iGloo installation. The independent 2026-09-08 review corroborated
> specific facts against the original ISO and rejected the prototype design.
> See [STATUS.md](../../distros/deepin/STATUS.md) and
> [REVIEW.md](../../distros/deepin/REVIEW.md). In particular, do not assume
> custom auto-partitioning preserves all partitions, a guard hook failure stops
> preinit, or the complete Debian-family agent works unchanged on Deepin.

Reconnaissance for a Deepin plugin. Everything below was read off the ISO and a
live session on 2026-09-05, not from upstream documentation — the public
`deepin-installer-reborn` docs describe a previous generation and no longer
match what ships.

| | |
|---|---|
| Image | `deepin-desktop-community-25.2.0-amd64.iso` (6,976,131,072 bytes) |
| SHA-256 | `f875c9a605bfe6a8425d1d353a3c1ec755bf37f5b0a3231ca19e2145da0ff450` |
| Installer | `deepin-installer` 7.0.60 |
| Spec file | `/usr/share/deepin-installer/configs/settings/default_settings.ini` |
| Runtime config | `/etc/deepin-installer/deepin-installer.conf` |

Two provenances are mixed below and are marked where they differ. Most claims
come from the live session and are reproducible from the attached terminal
capture. The image size, the SHA-256, the release-directory listing and
`preseed/deepin.seed` come from the download side and are **not** in that
capture; they need re-checking against the mirror, not against the session.

One property of the **live session** shapes several sections: it booted in
**legacy BIOS** mode. `ls /sys/firmware/efi` reports BIOS and the runtime conf
carries `DI_BOOTLOADER_IS_EFI=false`. No EFI code path was executed there, so
ESP claims sourced from that session are code reading, not observation.

A later **installed-system probe (2026-09-07) did run UEFI** —
`DI_BOOTLOADER_IS_EFI=true`, `efivarfs` mounted, `/boot/efi` on a vfat
partition, and `efibootmgr` showing
`Boot0004* deepin … \EFI\deepin\shimx64.efi`. Sections carrying a 2026-09-07
provenance note are from that run and are observation. The two are different
sessions on different firmware; do not read one as confirming the other.

## The delivery mechanism

The installer accepts a settings file named on the kernel command line. This is
the whole reason a Deepin plugin is feasible: iGloo does not repack ISOs, and
this needs no repacking.

```
kernel cmdline:  boot=live … DI_SETTINGS_FILE_PATH=/igloo/settings.ini
                     ↓
installer reads: /usr/lib/live/mount/medium/igloo/settings.ini
                     ↓
copies to:       /tmp/oem_settings_from_grub/settings.ini
                     ↓
                 rebuild_installer_config
```

From `tools/functions/default_funcs.sh:1613`. Two routes, chosen by
`pxe_install_type`:

- `http` / `iso` → `wget` the value as a URL
- anything else (local, USB, NFS) → `cp ${DI_LIVE_DIR_ENV}${path}`, so the value
  is a **path relative to the mounted install medium**

`DI_LIVE_DIR_ENV` is `/usr/lib/live/mount/medium/` (`tools/scripts/init_environment.sh:18`).
The local route is the one iGloo wants; the ISO is already staged on a volume,
so this is one extra file plus one kernel parameter.

Both map onto the existing contract with no change to `IDistroPlugin`:
`RenderInstallerConfigAsync` produces the settings.ini, `GetInstallerBootSpec`
carries `DI_SETTINGS_FILE_PATH=` in `KernelCmdline` and the file in
`ExtraIsoFiles`.

**The copy fails soft.** The `cp` is `|| warning "copy … failed"`, not `|| error`.
A wrong path, an unmounted medium or a typo does not stop the install — it
proceeds silently on the shipped defaults, which are `DI_PARTITION_TYPE=0`
(whole disk) and `DI_INSTALL_MODE=no-first-boot`. For a plugin whose entire
safety story rests on this file being read, that is the failure mode to design
against: the plugin should verify from the live side that the file arrived,
rather than assume delivery.

Two other cmdline flags gate live-installer mode: `boot=live` (`is_livecd`) and
`livecd-installer` / `live-config.livecd-installer` (`is_livecd_install`).

`DI_CONFIG_FILE_ENV` is not a delivery mechanism — it is hardcoded to
`/etc/deepin-installer/deepin-installer.conf` in `init_environment.sh:11`.

## Install modes

`DI_INSTALL_MODE`, documented in `default_settings.ini`:

| Value | Meaning |
|---|---|
| `default` | installs, then shows the post-configuration screens |
| `auto-install` | unattended |
| `no-first-boot` | no post-configuration stage |
| `auto-no-first-boot` | production: unattended and no post-configuration |

Any mode name containing `no-first-boot` skips the post-config stage; the check
lives in `is_have_first_boot` in `default_funcs.sh`.

The shipped spec file defaults to `no-first-boot`; the runtime conf on the live
session read `default`, so the value is rewritten during the session and the
plugin has to set it explicitly rather than rely on either.

## Partitioning

`DI_PARTITION_TYPE`, values enumerated in the shipped comment in
`default_settings.ini`:

| Value | Meaning |
|---|---|
| 0 | whole disk |
| 1 | manual partitioning |
| 2 | whole disk, encrypted |
| 3 | keep user data |
| 5 | reserved |
| **6** | **single disk, multiple systems** |
| 7 | whole disk, LVM |

**The shipped source contains two enumerations and they disagree.** The comment
above the `is_full_disk_mode()` body in `partition_funcs.sh` lists the same enum
positionally:

```
#FullDiskMode = 0 … #MountMode = 4, #GhostMode, #CustomMode, #LvmFullDisk
```

Read sequentially that gives 5 = Ghost, 6 = **Custom**, 7 = LVM. Both sources
agree on 0–4 and on 7 and disagree on exactly 5 and 6. The value iGloo would
care about most is the one that is least pinned down.

Type 6 is the dual-boot case, and it is the least evidenced of the set:

- `DI_PARTITION_CONFIG` points at `/etc/deepin-installer/partition_policy.json`,
  which **does not exist** in a live session. Generated later, or optional.
- Deepin's own `after_chroot/99_gen_experience.job` branches on types 0, 1, 2, 3
  and 7. There is no branch for 6.
- A grep across all of `tools/` for `"6"`, `== 6`, `-eq 6`, `MultiSystem`,
  `multi_system` and `multisystem` returns **zero hits**. Not proof that it is
  broken, but nothing in the shell layer treats 6 as a distinct case at all.

`DI_PARTITION_TYPE` is consumed in `tools/functions/partition_funcs.sh` at lines
4, 612 and 686 via `installer_get`.

Two configuration surfaces around phase 1 remain unexplored and should be before
any run: `DI_PART_POLICY_FILE` (present in the installer binary's string table,
no consumer found in the shell layer) and
`configs/settings/full_disk_policy.json` (present on disk, unreferenced by the
scripts). The absence of the file `DI_PARTITION_CONFIG` names makes it worth
knowing whether either of these is the real input.

### Partitioning runs in two phases

```bash
parted_manager() {                                   # phase 1: lay out the disk
    local parted_type=$(installer_get "DI_PARTITION_TYPE")
    local parted_conf=$(installer_get "DI_PARTITION_CONFIG")
    deepin-installer-parted -m $parted_type -c $parted_conf
}

parted_mount() {                                     # phase 2: mount
    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
    if ! is_ostree_system; then
        deepin-installer-parted -m 4 -c $mount_points
        bind_data_mount
        setup_data_info
        setup_efi_parted
    else
        # only / is mounted here; the rest waits for ostree
        …
        delete_data_without_home
    fi
    setup_swap
}
```

**Phase 2 has two shapes, and the default is the second one.** The
`-m 4` call, `bind_data_mount` and `setup_efi_parted` all live in the
non-OSTree branch. `DI_IMMUTABLE_SYSTEM=true` is the shipped default in both
`default_settings.ini` and the runtime conf, so out of the box `parted_mount`
mounts only `/` by hand and hands the rest to the OSTree path
(`ostree_funcs.sh`), which this reconnaissance did not follow. Every claim below
about mounting, ESP handling and bind mounts is a claim about the non-OSTree
branch.

`DI_MOUNTS_POINTS` format, from their own comment in
`hooks/before_chroot/08_setup_system.job:11`:

```
/dev/nvme0n1p3=/;/dev/nvme0n1p2=/boot;/dev/nvme0n1p1=/boot/efi
```

`device=mountpoint`, semicolon-separated, and the ESP is addressed as
`/boot/efi`. There is **no format flag** — this variable says where things
mount, not whether they are formatted. Format decisions live in phase 1.

`DI_MOUNTS_POINTS` appears in neither `default_settings.ini` nor the live
runtime conf, and not in the string table of `/usr/bin/deepin-installer` either.
It is read by the shell layer through `installer_get` and written by something
else — presumably `libDeepinInstallerPartition.so` or the GUI's partition
plugin.

*(2026-09-07 probe.)* It **is** an ordinary key in the conf once an install has
run, and its real value confirms the format, including `swap` as a mount point:

```
DI_MOUNTS_POINTS="/dev/nvme0n1p1=/boot/efi;/dev/nvme0n1p2=/boot;/dev/nvme0n1p3=swap;/dev/nvme0n1p4=/;/dev/nvme0n1p5=/persistent"
```

That shows the key persists and what it looks like. It does **not** show that a
value supplied through settings.ini is read rather than overwritten — here the
GUI wrote it. Still load-bearing for the mode-1 plan, still unproven.

`is_full_disk_mode()` (`partition_funcs.sh:602`) splits the modes:

```bash
local install_mode="02367"
if [[ "${install_mode}" =~ ${parted_type} ]]; then
```

Types 0, 2, 3, 6 and 7 are modes where the installer owns the layout. Types 1
(Advanced), 4 (Mount) and 5 are the ones where it does not. Given ADR-008
("installers never partition"), 1 and 4 are the interesting pair for iGloo, not
6 — iGloo already shrinks Windows and creates the partitions itself.

Note the test is a substring regex, not an equality check. An **empty**
`DI_PARTITION_TYPE` matches and `is_full_disk_mode` returns true. The plugin
must always emit the key explicitly.

### The ESP is reused, not created

This is #227 in Deepin form, and the answer is favourable — under two conditions
neither of which the live session could confirm.

`setup_efi_parted()` (`partition_funcs.sh:159`) sits entirely inside
`if is_uefi; then`, and it is called from `parted_mount` only on the non-OSTree
branch. The session booted BIOS, so none of this ran. What follows is read from
the source.

> **Amended 2026-09-07** (installer sources from the ISO): it applies on the
> OSTree branch too, by a different route.
> `hooks/before_chroot/13_mount_persistent.job` calls `mount_partitions` — which
> mounts every entry in `DI_MOUNTS_POINTS` except `swap`, so `/boot/efi` included
> — and then `setup_efi_parted` directly. The branch below is therefore reachable
> whichever value `DI_IMMUTABLE_SYSTEM` takes.

It picks an ESP in this order:

1. whatever is already mounted at `/target/boot/efi` — i.e. what the caller
   listed in `DI_MOUNTS_POINTS`. Takes `setup_esp_parted` and sets
   `DI_BOOTLOADER`.
2. otherwise the first EFI partition `fdisk -l -o Device,Type` reports
   (`sed -n '1p'`, across all disks, `DI_START_DEV` **not** excluded)
3. and if `fdisk` finds none, a further `lsblk` fallback matching vfat plus an
   `EFI` label, this one excluding `DI_START_DEV`

On 2 and 3 the partition is not only flagged: if it is not already mounted the
installer creates `/target/boot/efi`, mounts it there and sets `DI_BOOTLOADER`
to it (their comment: 复用磁盘中的efi分区, *"reuse the EFI partition on the
disk"*).

So passing the existing Windows ESP as `/boot/efi` takes branch 1 and no second
ESP is created. Debian's failure mode is avoidable here by construction. And
because branches 2 and 3 search the whole machine and take the first match,
iGloo should always populate branch 1 explicitly rather than let it search.

`setup_esp_parted()` (`partition_funcs.sh:152`) is five lines and is the only
thing branch 1 does to that partition:

```bash
setup_esp_parted() {
    local part=$1
    local dev=$(part_to_device $1)
    local num=$(part_num $1)
    parted -s "$dev" set "$num" esp on
}
```

No `mkfs`, no `mklabel`. It sets the ESP flag and nothing else.

It is still a GPT write on the shared disk. On a Windows ESP the flag is already
set, so the write is idempotent — the same "byte-identical rewrite is harmless
by construction" reasoning ADR-008 relies on. Within the existing risk model,
but not a no-op, and worth naming in the plugin's validation run.

One downstream effect worth knowing about for a dual-boot target:
`setup_udisks_rules()` writes `/target/etc/udev/rules.d/80-udisks-installer.rules`
with `UDISKS_IGNORE=1` for the ESP, `/boot` and swap. On a shared disk that
hides the Windows ESP from the file manager on the installed system. Cosmetic,
reversible, but surprising if nobody expects it.

### The phase-1 input format

`configs/settings/partition_policy.json` is not a policy — its header calls it
`deepin-installer-parted输入模板`, an **input template**, and it is not valid
JSON (trailing commas, a `/* */` header). It documents the schema by example:

```json
{ "type": "disk",      "operate": "edit",   "device": "/dev/sda", "diskType": "msdos" }
{ "type": "partition", "operate": "new",    "device": "", "filesystem": "ext4",
  "mountPoint": "/data2", "label": "_dde_data",
  "startPoint": "", "endPoint": "", "index": -1, "isstartpoint": true }
```

`type` is `disk` | `partition` | `VG` | `LVM`; `operate` is `edit` | `new` |
`delete`. So phase 1 takes **a list of operations**, not a desired end state.

That distinction is the whole of ADR-008. curtin treats its storage config as
authoritative and *deletes what is not declared*; an operation list does not
work that way. If that holds, Deepin is structurally safer than the Ubuntu path
that forced ADR-008 in the first place.

**It is not yet established that it holds.** The template shows what can be
asked for; it does not say what happens to partitions nobody mentions. That is
exactly the assumption curtin violated, so it needs proving on a disk with real
data before any bare-metal run, not assuming from a schema.

#### The real policy, from a completed install

*(Provenance: `/etc/deepin-installer/partition_policy.json` on an installed
system, probe of 2026-09-07. **UEFI**, `DI_PARTITION_TYPE=0` — whole disk — on a
**blank** 100 GB VM disk. Everything below describes that case and only that
case.)*

The template omits fields the real file carries. One entry, complete:

```json
{
  "type": "partition",  "operate": "new",
  "device": "/dev/nvme0n1",  "path": "",  "partUuid": "",
  "id": "{414a7e52-1c78-4e82-88be-0144ab6daa2d}",
  "startPoint": 2048,  "endPoint": 616447,  "sectorSize": 512,
  "size": 300,  "deviceSize": 102400,  "index": -1,
  "filesystem": "vfat",  "label": "EFI",  "mountPoint": "/boot/efi",
  "partType": "primary",  "isstartpoint": true,
  "isneedformat": true,
  "isCrypt": false,  "isCryptHeader": false,  "isNeedCryptHeader": false,
  "cryptPassword": ""
}
```

Two fields matter more than the rest:

- **`isneedformat`** — per-partition format control. This is the field ADR-008
  needs: an existing Windows ESP declared with `isneedformat: false` would be
  mounted and not formatted.
- **`ptuuid`** on the `type: "disk"` entry, alongside full per-partition
  geometry in sectors and `partUuid`. Between them these allow a declaration
  that describes the disk exactly as it already is — ADR-008's "byte-identical
  rewrite is harmless by construction" lever.

The disk entry from that run:

```json
{ "type": "disk", "operate": "edit", "device": "/dev/nvme0n1",
  "diskType": "gpt", "ptuuid": "" }
```

`operate: "edit"`, not `new`, and `gpt` rather than the template's `msdos`.

**What that install does not establish.** Every partition entry was
`operate: "new"` with `isneedformat: true`, because the disk was blank. It shows
nothing about `edit` on an existing partition or about `isneedformat: false`.

#### Phase 1, run directly against a canary

*(Provenance: four runs of `deepin-installer-parted -m 1 -c <policy>` in the
live session, 2026-09-07, against a 200 MB loop device carrying one pre-existing
formatted ESP with a known file on it. `wipefs`, `parted` and `mkfs` are not
loop-specific; partition-node re-creation is, and is called out below.)*

**1. A `type: "disk"` entry destroys the disk, whatever `operate` says.**

```
CommonFunc::setDisklabelType  "Command: wipefs -a -f /dev/loop2 succeed."
CommonFunc::setDisklabelType  "Command: parted -s /dev/loop2 mklabel gpt succeed."
CommonFunc::checkDeviceExist  "/dev/loop2p1 not found, retry..."   ×5
exit=0
```

`operate: "edit"` on the disk entry is not an edit. It wipes every signature and
lays a fresh label. The declared partition was gone before any partition entry
was looked at. **The plugin must never emit a `type: "disk"` entry for a shared
disk** — this is now measured, not inferred.

Note the exit code. It destroyed the target, failed to find the partition it was
asked to work on, and returned **0**. Phase 1's exit status cannot be used as a
success signal.

**2. Without a disk entry, nothing is wiped.** Runs 2–4 left the partition table
and the filesystem untouched except where explicitly asked.

**3. `isneedformat` controls formatting, and `false` is honoured.** Same policy,
one field flipped:

Silence in the installer's own log is weak evidence, so the decisive run watched
the invocation instead: `mkfs.vfat` was replaced with a logging wrapper (itself
verified to fire on a `--help` call) and the pair re-run on a fresh canary.

| `isneedformat` | `mkfs` actually invoked? | fs UUID |
|---|---|---|
| `false` | **no call at all** | `7FFE-14DF` — unchanged |
| `true` | `CALLED: -F32 -n CANESP /dev/loop2p1` | `81A1-5E6A` — reformatted |

So under `false` no format command is executed. That is observed at the point of
invocation, not inferred from a quiet log.

**An ADR-008-shaped policy is therefore viable: hand over the existing Windows
ESP with `isneedformat: false` and it is mounted, not formatted.**

**4. Missing sector geometry crashes it; `index` does not.** With `startPoint`
and `endPoint` absent:

```
CommonFunc::editPartition  "Partition [0s ~ 0s] path be changed from /dev/loop2p1 to /dev/loop2p-1"
Segmentation fault  (exit=139)
```

Isolated by changing one field at a time:

| run | `index` | geometry | result |
|---|---|---|---|
| 2 | `-1` | absent | `[0s ~ 0s]`, path `/dev/loop2p-1`, **segfault** |
| 5 | `-1` | present | no log, `exit=0`, no crash |
| 3 | `1` | present | no log, `exit=0`, no crash |

So `index: -1` is harmless as long as the geometry is real. Read as zeros, the
boundaries look like they need changing, `editPartition` runs, composes
`${device}p${index}` — hence `/dev/loop2p-1` — and dies. Supply real sectors and
that path is never taken.

That also explains the silence under `isneedformat: false`: with geometry that
already matches, there is nothing to change and nothing is logged — and the
wrapper run above confirms nothing is executed either.

Malformed input segfaults rather than erroring, which is worth knowing when
debugging a rendered policy.

**Still to confirm on a real disk.** All four runs used a loop device. The
`wipefs`/`mklabel`/`mkfs` calls are device-agnostic, but the `not found, retry`
loop in run 1 is characteristic of loop devices not re-creating partition nodes
after a table rewrite; on a real disk that path may behave differently.

Two things the plugin must never emit for the shared disk:

- a `type: "disk"` entry — it carries `diskType`, i.e. the partition table format. Sending `msdos` for a GPT disk holding Windows is the whole-disk-loss case in one line.
- `operate: "new"` or `"delete"` for anything iGloo did not create itself.

The intended shape is `operate: "edit"` entries naming partitions iGloo already
made, plus the existing ESP, and nothing else — with real sector geometry on
each and no `type: "disk"` entry at all. Both are measured requirements, not
style preferences; see the canary runs below. (`index` turned out not to
matter once the geometry is right.)

`deepin-installer-parted` has no `--help` and no usage strings — invoking it
outside the installer fails to load `libDeepinInstallerPartition.so` and exits.
The complete set of option-shaped strings in the binary is:

```
--auto  --bind  --force  --key-slot  --label  --mode  --output
--primary  --same-as  --timeout  --wipesignatures  --yes
```

A string dump cannot say which are its own and which it forwards to `parted`,
`sgdisk` or `cryptsetup`. `--wipesignatures`, `--same-as` and `--output` are the
three to establish before it is pointed at a disk holding Windows.

Sizes are configurable, which matters for the second-ESP class of bug (#227):

```ini
DI_EFI_SIZE_CONFIG   = 300     ; MiB
DI_BOOT_SIZE_CONFIG  = 4096    ; MiB — Deepin wants a 4 GiB /boot
DI_EFI_PARTITION_FS_CONFIG  = "vfat"
DI_BOOT_PARTITION_FS_CONFIG = "ext4;ext3"
```

Note the 4 GiB `/boot`: far larger than the other distros in the catalog, and it
has to come out of the space iGloo shrinks off Windows.

## Keys that map onto the migration manifest

```ini
DI_USERNAME       DI_PASSWORD       DI_HOSTNAME       DI_ROOT_PASSWORD
DI_AVATAR         DI_LOCALE         DI_TIMEZONE
DI_LAYOUT         DI_LAYOUT_VARIANT
DI_START_DEV      DI_DEVICE_LIST    DI_ENABLE_AUTO_SELECT_DISK
DI_HOOKS_LIST     DI_APT_SOURCE_DEB
```

`DI_PASSWORD_ENCRYPTION_CONFIG = true` — the password is supplied hashed, which
matches what the manifest already carries after the sha512-crypt work.

Locale and layout round-trip: the session had `DI_REGION=Belgium`,
`DI_LOCALE=nl_NL` and `DI_LAYOUT=nl` written into the runtime conf from the
live-session choices, along with a full `DI_REGION_FORMAT_*` block reading
`nl_BE`.

**Time did not.** The same file still read `DI_TIMEZONE=Asia/Beijing` and
`DI_LOCALTIME=Asia/Shanghai` after the region was set to Belgium. Either the
timezone is written at a later stage than region and locale, or it is not
written from the region at all. `DI_LAYOUT_VARIANT=us` alongside `DI_LAYOUT=nl`
is similarly unexplained. The plugin should set `DI_TIMEZONE` itself and verify
it on the installed system rather than assume the region carries it.

`deepin-installer-config get|set <file> <section> <key> [value]` is a CLI for
reading and writing that conf. Deepin's own hooks use it against
`/target/...` to configure the installed system.

## Hooks

Deepin ships hook directories and uses them itself. Plain bash, run in sorted
order, one `.job` per script:

| Stage | Runs | Evidenced |
|---|---|---|
| `before_chroot` | on the live system before the target is populated | yes |
| `after_chroot` | on the live system after the target is populated | yes |
| `before_install` | on the live system before installing | yes (`default_funcs.sh:98`) |
| `live_config` | on the live system at boot | yes |
| `ghost` | ghost/recovery install path | yes (`hooks/ghost/05_init_recovery.job`) |
| `in_chroot` | inside the target rootfs during install | yes (installed conf) |
| `ostree` | ostree deployment stage | yes (installed conf) |
| `cleanup` | after the install stages | yes (installed conf) |
| `first_boot` | on the installed system at first boot | as an `oem/hooks/` directory on the ISO, yes (three `.job` files); not as a `DI_FB_HOOHS_LIST` stage |

*(Provenance for the last three rows: `DI_HOOKS_LIST` in
`/etc/deepin-installer/deepin-installer.conf` on an installed system, probe of
2026-09-07.)*

```
DI_HOOKS_LIST="…/hooks//before_chroot;…/hooks//in_chroot;…/hooks//after_chroot;…/hooks//ostree;…/hooks//cleanup"
```

**First-boot hooks run off a different key**, and it is misspelled in Deepin's
own config:

```
DI_FB_HOOHS_LIST="…/hooks//user_config;…/hooks//first_boot_cleanup"
```

So the first-boot stages on this build are `user_config` and
`first_boot_cleanup` — no stage literally called `first_boot`.

**And the mechanism removes itself.** On the installed system
`deepin-installer-first-boot.service` does not exist
(`systemctl cat` → "No files found"), while
`/var/log/deepin-installer/deepin-installer-first-boot.log` is 70 KB. It ran and
was then cleaned up, which is what `first_boot_cleanup` is for. An iGloo agent
cannot be hung off this: it needs its own systemd unit, exactly as on the other
four distros.

`DI_HOOKS_LIST` selects stages (semicolon-separated). `live_config` is the
exception: it is driven by a separate key, `DI_LC_HOOKS_LIST`, which the runtime
conf had pre-populated with
`/usr/share/deepin-installer/tools/hooks//live_config`.

One stage comes off the medium rather than the squashfs. `live_hooks()`
(`default_funcs.sh:87`) copies `${DI_LIVE_DIR_ENV}/oem/live_hooks` into the
installer's tools directory and runs every `.job` in it:

```bash
local OEM_DIR_CONFIG=$DI_LIVE_DIR_ENV/oem
is_empty_dir "$OEM_DIR_CONFIG/live_hooks" || cp -vfr $OEM_DIR_CONFIG/live_hooks $DI_INSTALL_TOOLS_DIR_ENV/live_hooks
```

> **Corrected 2026-09-07** — provenance: the installer sources read out of
> `live/filesystem0.squashfs` on the ISO, not the live session.
>
> **`live_hooks` does not run on the install path**, so a plugin cannot use it.
> `deepin-installer-preinit` calls it only in the `else` branch of
> `if is_livecd_install`, i.e. for the "Try Deepin" desktop entry;
> `init_live_workspace`, which copies the directory in, is in the same branch. With
> `livecd-installer` on the command line neither runs.
>
> **Hooks for the ordinary stages *are* picked off the medium**, by
> `init_workspace()` (`default_funcs.sh:193`), which is the install-path
> counterpart:
>
> ```bash
> is_empty_dir "$OEM_DIR_CONFIG/hooks" || cp -vfr $OEM_DIR_CONFIG/hooks/* $DI_INSTALL_TOOLS_DIR_ENV/hooks/
> ```
>
> It merges `oem/hooks/<stage>/` into the shipped stage directories, and the ISO
> ships `oem/hooks/in_chroot/` and `oem/hooks/first_boot/` with three `.job` files
> each of its own. `before_install` runs immediately after it, still inside
> preinit — after the settings.ini merge and before the installer core, so before
> any partitioning. `setup_chroot_hooks` (`:496`) then
> `rsync -aHAXi --delete`s the whole tools tree into `/target`, which is how an
> `in_chroot` job reaches the target.
>
> Note also that `before_install` **ignores each job's exit status**, and deepin's
> own `error()` is `echo` plus `exit 1`. A hook cannot abort the install
> cooperatively. See [deepin-delivery-contract.md](deepin-delivery-contract.md).

`DI_OEM_DIR` is exported as `$DI_LIVE_DIR_ENV/oem/`
(`init_environment.sh:19`). The ISO's own `oem/` carries `settings.ini` plus the
`hooks/` tree described above.

## The base is debian-installer

*(From the ISO, not the live session.)* `preseed/deepin.seed` on the ISO carries
real `d-i` directives alongside `ubiquity` ones, and a commented-out block for
`passwd/username`, `passwd/user-password`, `netcfg/get_hostname` and
`oem-config/enable`. Deepin is closer to the Debian family than
`installerType: "Custom"` in the catalog entry suggests.

## No signed checksums

*(From the mirror, not the live session — re-verify at pin time.)* The release
directory contains exactly three files:

```
MD5SUMS       76 bytes
SHA256SUMS   108 bytes      (one line: hash + filename, no PGP block)
deepin-desktop-community-25.2.0-amd64.iso
```

No `.asc`, `.sig`, `.gpg` or `SHA256SUMS.sign`. Deepin publishes unsigned
checksums.

`IsoAcquisitionService` has two verification paths — detached signature and
clear-signed — and Deepin fits neither.

This needs no ADR. BR-02 already carves out the case: *"SHA-256 **and** (when the
distro publishes one) a GPG signature."* Deepin publishes none, so SHA-256 alone
is the rule as written.

What follows is not optional, though. `ResolveTrustedSha256` fails closed when
there is neither a pinned hash nor a signed one, so for Deepin the pinned
`sha256` in `distro.json` is **mandatory** — the plugin cannot work without it.
Unlike Mint, where the pin is a second lock on top of GPG, here it is the only
one, and its correctness rests on the value being reviewed in a pull request.

For 25.2.0 that value is:

```
f875c9a605bfe6a8425d1d353a3c1ec755bf37f5b0a3231ca19e2145da0ff450
```

*(2026-09-07: recomputed over the local copy of the image — hash and the
6,976,131,072-byte length both match what is pinned in `distros/deepin/distro.json`.
That confirms the pin describes this file; it does not re-verify the mirror, which
is still a check to make at every release bump.)*

BR-10 is satisfied so far as observed: both apt sources in the shipped configs
are HTTPS, and every Deepin artefact URL seen is HTTPS.

One inconsistency to fix while here: `SECURITY.md` promises "ISO acquisition and
verification (SHA-256 + pinned GPG fingerprints)" without BR-02's conditional.
Shipping a distro with no signature makes that sentence wrong.

## It merges, so render only overrides

`rebuild_installer_config()` (`default_funcs.sh:66`) is a one-liner onto
`init_config` (`:37`), which calls `deepin-installer-config init <file>`. The
merge itself happens inside that binary, but Deepin's own comment states the
contract:

> 重新生成安装器配置文件，使新下载的覆盖配置按既定优先级重新合并。
> *(regenerate the installer config so newly downloaded overrides are re-merged
> according to the established priority)*

The plugin therefore renders only the keys it wants to change, not a full copy
of `default_settings.ini`. The established priority itself is inside the binary
and has not been established from the outside.

Two things `init_config` does besides merging, both of which the plugin has to
work around rather than through:

- it re-derives state on **every** rebuild: `init_bootloader_type`,
  `init_tpm_check`, `init_nvidia_check`. `DI_BOOTLOADER_IS_EFI` in particular is
  recomputed, so shipping it in settings.ini is not a way to force a boot mode.
- it overlays `/etc/live/config.conf.d/deepin-installer-live.conf` by deleting
  each matching key and re-inserting it at line 1. The delete is
  `sed -i "/${itemName}/d"` — an unanchored regex, so it removes *any* line
  containing the name. With the five shipped `LIVE_*` keys that is harmless; it
  becomes a hazard the moment a plugin key shares a substring with one. This is
  also a second injection point worth remembering, since live-config on Debian
  live systems can itself be fed from the kernel command line.

## Solid is OSTree

`hooks/before_chroot/08_setup_system.job` branches the whole filesystem-creation
step:

```bash
# 磐石系统不再需要解压squashfs来生成文件系统，直接同步ostree仓库数据即可
if is_ostree_system; then
    ...     # derive root/boot/data devices from DI_MOUNTS_POINTS and record
            # them in DI_ROOT_DEVICE_CONFIG / DI_BOOT_DEVICE_CONFIG /
            # DI_DATA_DEVICE_CONFIG. No ostree sync happens in this hook.
else
    unsquashfs_filesystem
fi
```

*("The Solid system no longer needs to unpack the squashfs to create the
filesystem; it syncs the ostree repository data directly.")*

deepin.org describes Solid only as "core directories mounted read-only" and does
not name a mechanism. The code names it: **OSTree**. `DI_IS_INIT_RECOVERY` and
the snapshot-before-update behaviour follow from the same design.

It is `deb-ostree`, refspec `deb-ostree/main`, deployments under
`/ostree/deploy/deepin/deploy/<commit>.0`, repo under `$sysroot/ostree/repo`.

### It is a settings key, not a property

`ostree_funcs.sh:3`:

```bash
function is_ostree_system() {
    local immutable_system=$(installer_get "DI_IMMUTABLE_SYSTEM")
    if [[ $immutable_system == "true" ]]; then
        return 0
    else
        return 1
    fi
}
```

That is the whole test. `DI_IMMUTABLE_SYSTEM` is an ordinary key, overridable
through the same settings.ini iGloo already supplies. It ships `true` in both
the spec file and the runtime conf, so OSTree is what happens unless the plugin
says otherwise. Setting it `false` takes the `unsquashfs_filesystem` branch and
a conventional layout. Whether Deepin still tests that path is unknown, but the
knob exists — and the choice decides which of the two `parted_mount` shapes runs.

### The agent's write paths, on the non-OSTree branch

**Scope: this section describes `DI_IMMUTABLE_SYSTEM=false` only.**
`bind_data_mount()` (`partition_funcs.sh:223`) is called from the non-OSTree
branch of `parted_mount` and from the recovery path at `default_funcs.sh:823`.
It does not run in OSTree mode.

```bash
mount --bind /target/${data_mount_points}/home /target/home
mount --bind /target/${data_mount_points}/opt  /target/opt
```

`data_mount_points` is `DI_DATA_MOUNT_POINT_CONFIG` read for
`DI_OS_VERSION_ENV`: `/data` under `[General]`, `/persistent` under `[V23]` and
`[V25]`. So on a V25 install `/opt/igloo` lands on `/persistent/opt/igloo` and is
writable — conditional on `exists_data_part`, i.e. on the plugin actually
declaring a data mount point in `DI_MOUNTS_POINTS`. `/etc/grub.d` is writable
too — the installer edits it itself (`rm -f /target/etc/grub.d/15_ostree`,
`ostree_funcs.sh:164`).

**In OSTree mode the mechanism is different and the outcome is the same.**

*(Provenance: installed system, probe of 2026-09-07, `DI_IMMUTABLE_SYSTEM=true`,
`/run/ostree-booted` present.)*

Not bind mounts from the data partition — overlays:

```
/opt   opt-overlay   overlay   rw
/etc   etc-overlay   overlay   rw
/usr   usr-overlay   overlay   ro
```

with lowerdirs stacking the ostree deployment under
`/root/persistent/overlay/data/…`. A write test planted real files and read them
back:

```
WRITABLE   /opt  /etc  /etc/grub.d  /etc/xdg/autostart
WRITABLE   /usr/local/bin  /var/lib  /boot  /boot/efi
READ-ONLY  /usr
```

So every path the agent needs is writable on the default OSTree branch, without
setting `DI_IMMUTABLE_SYSTEM=false`.

**They survive a reboot.** All five planted files were still present and intact
after restarting the installed system (2026-09-07).

One observable changed: the three on overlay-backed paths went from one hardlink
to two, while the two on plain ext4 under `/persistent` stayed at one.

```
2  /etc/grub.d/99_igloo_probe
2  /etc/xdg/autostart/igloo-probe.desktop
2  /opt/igloo/probe
1  /usr/local/bin/igloo-probe        (on /persistent)
1  /var/lib/igloo/probe              (on /persistent, via /var)
```

Consistent with the file being hardlinked into the deployment or snapshot store
— `DI_IS_INIT_RECOVERY=true` and the repo carries a `snapshot/…` ref — but the
mechanism was not traced and should not be assumed.

**Still not shown: survival across an ostree update** that replaces the
deployment. On an overlay over a versioned deployment that is the question that
actually matters for an agent meant to keep working.

The layout that install produced, for reference:

```
/dev/nvme0n1p1  vfat  EFI        300M   /boot/efi
/dev/nvme0n1p2  ext4  Boot         4G   /boot
/dev/nvme0n1p3  swap  SWAP       5.7G   [SWAP]
/dev/nvme0n1p4  ext4  Roota       23G   /          (also /sysroot, /ostree)
/dev/nvme0n1p5  ext4  _dde_data   67G   /persistent (also /var, /home, /root)
```

Note `ostree admin status` fails on this system —
*"Unexpected state: /run/ostree-booted found, but no /boot/loader directory"* —
so the deployment is not a stock ostree layout and stock ostree tooling should
not be assumed to work against it.

## Open

Reconnaissance is finished; everything below needs a completed install, not more
reading.

1. **Does phase 1 leave undeclared partitions alone?** Untested directly — the
   canary carried one partition and it was declared. What *is* settled is the
   larger danger: a `type: "disk"` entry wipes the disk outright, so the answer
   only matters for policies that omit one. Test it on a multi-partition disk
   before bare metal.
2. ~~Is `isneedformat: false` honoured?~~ **Answered 2026-09-07: yes.** A
   `mkfs.vfat` wrapper showed no format command is invoked under `false` and the
   expected one under `true`, on otherwise identical policies. Remaining: repeat
   on a real disk rather than a loop device.
3. **Do the agent's files survive?** Writability is settled (2026-09-07: `/opt`,
   `/etc`, `/etc/grub.d`, `/etc/xdg/autostart`, `/boot`, `/boot/efi` all
   writable on the default OSTree branch; only `/usr` read-only). Persistence is
   not: the probe planted and read back in one session. Re-check after a reboot
   and after an ostree update that replaces the deployment.
4. ~~Can `DI_MOUNTS_POINTS` be supplied through settings.ini at all?~~
   **Closed 2026-09-07 as not applicable.** The value is a list of Linux device
   paths, which cannot be known when Igloo renders settings.ini on Windows: the
   partitions do not exist yet. It is written from the live system with
   `deepin-installer-config set`, and corrected from the real partition table once
   phase 1 has run. Whether the merge would accept the key is untested and no
   longer load-bearing.
5. ~~Where does the agent go?~~ **Answered 2026-09-07: `oem/hooks/in_chroot/`.**
   `init_workspace` copies `oem/hooks/*` off the medium into the installer's hooks
   tree on the install path, and `setup_chroot_hooks` mirrors that tree into
   `/target`, so the job runs inside the target chroot. It still needs its own
   systemd unit rather than a first-boot `.job`: that chain deletes itself via
   `first_boot_cleanup`. The earlier reading of this item was drawn from the live
   session, where the install-path functions do not run.
6. ~~Which enumeration of `DI_PARTITION_TYPE` 5 and 6 is authoritative?~~
   **Answered 2026-09-08: neither comment matters, the binary decides.**
   `PartitionAutoManager::start` switches on the value through an eight-entry jump table
   (`.rodata` at `0x233e34`): 0 `autoFullDisk`, 2 `autoFullDiskEncryption`,
   3 `autoSaveData`, 6 `autoCustomDisk`, 7 `autoLVMFullDisk`, and **1, 4 and 5 fall to the
   default branch**, which fails the install with
   `Unknown unattended installation mode <%1>`. So 6 is the custom/dual-boot mode both
   comments were pointing at, 5 is not implemented at all, and 1 ("manual") only exists
   when a human drives the GUI. Confirmed on a VM run the same day, which failed with
   exactly that message on type 1.
7. Align `SECURITY.md` with BR-02's conditional before a signature-less distro
   ships. Not a blocker for the plugin, but it makes a public promise inaccurate
   the day Deepin lands.

## What a plugin would look like

Nothing here needs a change to `IDistroPlugin`.

| Contract member | Deepin |
|---|---|
| `RenderInstallerConfigAsync` | a `settings.ini` of overrides — `DI_INSTALL_MODE=auto-install`, `DI_PARTITION_TYPE=1`, `DI_IMMUTABLE_SYSTEM`, account, locale, layout, `DI_TIMEZONE`, `DI_MOUNTS_POINTS`, and a partition operation list |
| `GetInstallerBootSpec` | `KernelCmdline` carries `boot=live livecd-installer DI_SETTINGS_FILE_PATH=…`; `ExtraIsoFiles` places the settings file on the staged volume |
| `GetAgentPayloadAsync` | the existing `_debian-family` agent, with **its own systemd unit** — Deepin's first-boot chain deletes itself (`first_boot_cleanup`). Delivered by `oem/hooks/in_chroot/`, which runs inside the target chroot; see Open #5 and [deepin-delivery-contract.md](deepin-delivery-contract.md). |
| `CheckCompatibility` | 64 GiB floor (`DI_DEVICE_MIN_SIZE_CONFIG`; note `DI_VALID_DEVICE_MIN_SIZE_CONFIG=45` is a second, lower threshold with an unknown role), plus the 4 GiB `/boot` Deepin wants on top of the usual free-space budget |




TERMINAL OUTPUT:



bash: warning: setlocale: LC_CTYPE: cannot change locale (zh_CN.UTF-8): No such file or directory
bash: warning: setlocale: LC_CTYPE: cannot change locale (zh_CN.UTF-8)
bash: warning: setlocale: LC_COLLATE: cannot change locale (zh_CN.UTF-8): No such file or directory
bash: warning: setlocale: LC_CTYPE: cannot change locale (zh_CN.UTF-8)
bash: warning: setlocale: LC_CTYPE: cannot change locale (zh_CN.UTF-8)
bash: warning: setlocale: LC_COLLATE: cannot change locale (zh_CN.UTF-8)
liveuser@liveuser-pc:~$ ls /usr/bin /usr/lib/deepin-installer* 2>/dev/null | grep -i install; dpkg -l 2>/dev/null | grep -i installer
deepin-deb-installer
deepin-deb-installer-dependsInstall
deepin-installer
deepin-installer-command-agent
deepin-installer-config
deepin-installer-first-boot
deepin-installer-live-config
deepin-installer-parted
deepin-installer-parted-util
desktop-file-install
dh_installxmlcatalogs
install
kernel-install
ll-installer
/usr/lib/deepin-installer:
ii  deepin-deb-installer                              6.5.46                                      amd64        Package Installer helps users install and remove local packages.
ii  deepin-installer                                  7.0.60                                      amd64        Release version of deepin installer
ii  deepin-installer-timezones                        6.0.4+dde                                   all          Language package for timezone names
ii  linglong-installer                                1.6.0-1                                     amd64        Linglong online store application installation tool.
liveuser@liveuser-pc:~$ sudo find / -xdev \( -name 'default_settings.ini' -o -name 'settings.ini' -o -name 'auto_part*.sh' \) 2>/dev/null; ls -la /etc/deepin-installer/ /usr/share/deepin-installer/ /cdrom/oem 2>/dev/null
/usr/share/deepin-app-store/settings.ini
/usr/share/deepin-app-store/newsettings/settings.ini
/usr/share/deepin-appstore/settings.ini
/usr/share/deepin-deepinid-daemon/settings/settings.ini
/usr/share/deepin-installer/configs/settings/default_settings.ini
/etc/deepin-installer/:
total 8
drwxr-xr-x 2 root root   60 Sep  3 10:03 .
drwxr-xr-x 1 root root  820 Sep  3 10:03 ..
-rw-r--r-- 1 root root 5007 Sep  3 10:03 deepin-installer.conf

/usr/share/deepin-installer/:
total 1
drwxr-xr-x 5 root root   55 Jan  1  1970 .
drwxr-xr-x 1 root root 2077 Jan  1  1970 ..
drwxr-xr-x 6 root root   86 Jan  1  1970 configs
drwxr-xr-x 3 root root 2329 Jan  1  1970 i18n
drwxr-xr-x 6 root root  274 Jan  1  1970 tools
liveuser@liveuser-pc:~$ sudo grep -aoE 'DI_[A-Z0-9_]+|skip_[a-z_]+_page|partition_do_auto_part|system_info_default_[a-z]+' $(which deepin-installer 2>/dev/null || echo /usr/bin/deepin-installer) | sort -u
DI_BACKGROUND_IMAGE
DI_BOOTLOADER_IS_EFI
DI_BOOT_PARTITION_FS_CONFIG
DI_BOOT_SIZE_CONFIG
DI_COMPONENT_PACKAGES
DI_COMPONENT_UNINSTALL
DI_CONFIG_DIR_ENV
DI_CONFIG_FILE_ENV
DI_CRYPTO_LOAD
DI_CRYPT_PASSWORD
DI_CRYPT_RECOVERY_KEY
DI_DATA_DEVICE_CONFIG
DI_DATA_MOUNT_POINT_CONFIG
DI_DDE_AVATAR_DIR_CONFIG
DI_DEFAULT_RESOLUTION
DI_DESKTOP_ENV
DI_DEVICETYPES_CONFIG
DI_DEVICE_LIST
DI_DEVICE_MIN_SIZE_CONFIG
DI_DISK_CRYPT_ALGORITHM_CONFIG
DI_DISK_CRYPT_KEY_FILE_CONFIG
DI_DISK_KEY_FILE_CONFIG
DI_DISK_PUB_RSA_FILE_CONFIG
DI_EFI_PARTITION_FS_CONFIG
DI_EFI_SIZE_CONFIG
DI_FIRST_IS_BOOTLOADER_CONFIG
DI_FORMAT_PARTITION_CNT_CONFIG
DI_GHOST_EXEC_CONFIG
DI_HOOKS_LIST
DI_HOSTNAME_RESERVED_CONFIG
DI_INSTALLER_STATUS
DI_INSTALL_MODE
DI_INSTALL_MODULE_DIR_ENV
DI_INSTALL_TOOLS_DIR_ENV
DI_IS_AUTO_DECRYPT
DI_IS_ENABLE_MOVIE_CONFIG
DI_IS_GHOST_MODE
DI_IS_INIT_RECOVERY
DI_LAYOUT
DI_LAYOUT_VARIANT
DI_LIVE_DIR_ENV
DI_LOCALE
DI_LVM2_PV_SIZE_CONFIG
DI_LVM_FILESYSTEM_CONFIG
DI_OLD_USERNAME_LIST
DI_OS_VERSION_ENV
DI_OTHER_SIZE_CONFIG
DI_PARTITION_CONFIG
DI_PARTITION_FILESYSTEM_CONFIG
DI_PARTITION_MOUNT_POINT_CONFIG
DI_PARTITION_TYPE
DI_PART_POLICY_FILE
DI_PASSWORD_CONTINUOUS_LENGTH_CONFIG
DI_PASSWORD_MAX_LEN_CONFIG
DI_PASSWORD_MIN_LEN_CONFIG
DI_PASSWORD_MONOTONOUS_LENGTH_CONFIG
DI_PASSWORD_PALINGROME_LENGTH_CONFIG
DI_PASSWORD_STRONG_CHECK_CONFIG
DI_PASSWORD_VALIDATE_CONFIG
DI_PASSWORD_VALIDATE_REQUIRED_CONFIG
DI_PERSISTENT_SIZE_CONFIG
DI_RECOVERY_BASE_SIZE_CONFIG
DI_ROOTA_SIZE_CONFIG
DI_SAVE_USER_DATA_LOAD
DI_SIZE_GUNIT_CONFIG
DI_SIZE_MUNIT_CONFIG
DI_SRC_DIR_ENV
DI_START_DEV
DI_SWAP_MAX_SIZE_CONFIG
DI_SYSTEM_FONT_SIZE_CONFIG
DI_TEST_PROC_CMDLINE
DI_TIMEZONE
DI_TPM_CHECK
DI_UI_DEFAULT_FONT_CONFIG
DI_USERNAME_MAX_LEN_CONFIG
DI_USERNAME_MIN_LEN_CONFIG
DI_USE_DEBUG_FRAME
DI_USE_HIDE_CRYPT_HEADER
liveuser@liveuser-pc:~$ cat /usr/share/deepin-installer/configs/settings/default_settings.ini
[General]

; --------------------------- 程序读写业务逻辑配置选项(rw) --------------- ;
;; 安装模式：默认安装（有后配置界面=default）
;; 无人值守安装=auto-install;
;; 无first-boot安装=no-first-boot
;; 产线无first-boot安装=auto-no-first-boot;
;; 如果需要增加没有后配置mode时名称中需要包含no-first-boot
;; 参考种类defalut_funcs.sh中is_have_first_boot的实现
DI_INSTALL_MODE="no-first-boot"

;; 语言设置配置选项
DI_LOCALE="zh_CN"

;; 用户体验计划配置选项
DI_USER_EXPERIENCE = false

;; 隐私协议配置选项
DI_PRIVACY_POLICY = false

;; 组件设置配置选项
DI_COMPONENT_TYPE = ""

;; 全盘加密密码配置选项
DI_CRYPT_PASSWORD = ""

;; 全盘加密自动解密
DI_IS_AUTO_DECRYPT = false

;; 磁盘设备列表配置选项
DI_DEVICE_LIST = ""

;; 对指定磁盘只分区和格式化，不进行安装配置选项
DI_DISK_ONLY_FORMAT = ""

;; 是否开启自动选盘
DI_ENABLE_AUTO_SELECT_DISK = true

;; 安装方式, 默认为全盘安装，具体取值定义在globals.h的enum InstallMode中
;; 0 - 全盘安装, 1 - 手动分区安装, 2- 全盘加密安装, 3 - 保留用户数据安装, 5 - reserve(预留), 6 - 单盘多系统安装, 7 - lvm全盘安装
DI_PARTITION_TYPE = 0

;; 键盘布局配置选项
DI_LAYOUT = "us"
DI_LAYOUT_VARIANT = ""

;; 是否ghost安装模式
DI_IS_GHOST_MODE = false

;; 时区配置选项
DI_TIMEZONE = "Asia/Beijing"

;; 用户图像的绝对路径
DI_AVATAR = ""

;; 用户名配置选项
DI_USERNAME = ""

;; 原有用户名列表，用于保留用户数据安装
DI_OLD_USERNAME_LIST = ""

;; 主机名配置选项
DI_HOSTNAME = ""

;; 用户密码配置选项
DI_PASSWORD = ""

;; 快速登录
DI_QUICK_LOGIN = true

;; 是否进入审核模式
DI_IS_CHECK_MODE = false
DI_AUTO_CHECK_MODE = false
;; 是否停留在审核模式
DI_CHECK_MODE_HOLD = false
;; 审核模式用户名
DI_CM_USER = "test"

;; 审核模式密码
DI_CM_PASSWORD = "Test@121!"

;; root用户密码配置选项
DI_ROOT_PASSWORD = ""

;; 是否EFI引导的方式配置选项(true/false)
;; 默认配置为空，用户可以定制引导选项，强制系统的引导方式
DI_BOOTLOADER_IS_EFI = ""
;; 引导分区配置选项
DI_BOOTLOADER = ""
;; 开启调试窗口
DI_USE_DEBUG_FRAME=true

;; hooks的脚本阶段列表, 列表以分号分割
DI_HOOKS_LIST = ""

;; 组件包配置选项和语言包配置选项
DI_COMPONENT_PACKAGES=""
DI_COMPONENT_LANGUAGE=""

;; 是否创建初始化备份(ostree)
DI_IS_INIT_RECOVERY = true

;; ghost
DI_UIMG_FILE = ""

;; 是否启用btrfs文件系统压缩选项
;; 0->不启用
;; 1->启用zstd格式压缩
DI_ENABLE_COMPRESS = 0

;; 按照完成是否自动重启
DI_REBOOT_AFTER_SETUP = false

;; 不可变系统
DI_IMMUTABLE_SYSTEM = true

;;
DI_SAVE_USER_DATA_CLEAN_LIST="/home/*/.cache;/home/*/.tmp;/home/*/.config/deepin/dde-welcome.conf;/home/*/.config/deepin/01-dde-welcome.conf"

;; 开启冰点还原
DI_ENABLE_ICE_RESTORE = false

;; 冰点还原白名单。配置举例"/home,/etc"，
DI_ICE_RESTORE_WHITELIST=""

;; 仓库地址
DI_APT_SOURCE_DEB = "deb [by-hash=force] https://packages.chinauos.cn/uos eagle main contrib non-free"
DI_APT_SOURCE_DEB_SRC = "#deb-src https://packages.chinauos.cn/uos eagle main contrib non-free"

; --------------------------- 程序读写业务逻辑配置选项 ---------------;

; --------------------------- 功能裁剪配置选项(ro) ---------------;
;; 用户体验计划裁剪选项
DI_USER_EXPERIENCE_LOAD = true
;; 隐私协议裁剪选项
DI_PRIVACY_POLICY_LOAD = true
;; 全盘加密功能裁剪选项
DI_CRYPTO_LOAD = true
;; 创建root用户功能裁剪选项
DI_ROOT_USER_LOAD = false
;; 保留用户数据功能裁剪选项
DI_SAVE_USER_DATA_LOAD = true
;; 系统版本选择插件裁剪选项
DI_EDITION_PLUGIN_LOAD = false
; --------------------------- 功能裁剪配置选项 ---------------;

; --------------------------- 页面功能配置选项(ro) ---------------;
;;　期望扫描出的磁盘类型
DI_DEVICETYPES_CONFIG = "disk"
;;　分区前端和后端交互的配置
DI_PARTITION_CONFIG = "/etc/deepin-installer/partition_policy.json"
;; 安装器界面默认字体大小
DI_SYSTEM_FONT_SIZE_CONFIG = 14
;; uefi引导下默认的启动选项
DI_BOOTLOADER_OPTION_CONFIG="uos"
;; 提供给dde的grub延时标志  0:不延时/1:延时5s
DI_GRUB_TIMEOUT_CONFIG = 1
;; 默认设置的grub分辨率
DI_GRUB_RESOLUTION_CONFIG = "auto"
;; 有效磁盘大小，单位G
DI_VALID_DEVICE_MIN_SIZE_CONFIG = 45
;; 磁盘最小可安装的大小，单位G
DI_DEVICE_MIN_SIZE_CONFIG = 64
;; DDE用户图像绝对路径
DI_DDE_AVATAR_DIR_CONFIG = "/var/lib/AccountsService/icons/"
;; EFI分区的默认size,单位M
DI_EFI_SIZE_CONFIG = 300
;; Boot分区的默认size,单位M
DI_BOOT_SIZE_CONFIG = 4096
;; Roota分区的默认size,单位M
DI_ROOTA_SIZE_CONFIG = 23552
;; Recovery分区的默认size,单位M
DI_RECOVERY_BASE_SIZE_CONFIG = 11264
;; SWAP分区最大大小，单位M
DI_SWAP_MAX_SIZE_CONFIG = 16384
;; Persistent分区的默认size,单位M
DI_PERSISTENT_SIZE_CONFIG = 20480
;; 其它分区最小，单位M
DI_OTHER_SIZE_CONFIG = 2048
;; LVM2 PV分区大小，单位M，因为PE的大小是4M，pvcreate的时候无法指定PE的大小，只能在构建vg的时候使用 -s 调整
;; 而实测发现4M的pv没有被合入vg，所以这里设置为5M比默认PE多1M
DI_LVM2_PV_SIZE_CONFIG = 5

;; DATA分区的最小空间占用磁盘的空间
DI_DATA_MIN_SIZE_CONFIG = 20%
;; 分区加密算法
DI_DISK_CRYPT_ALGORITHM_CONFIG = "--pbkdf-memory 512000 --pbkdf pbkdf2 --cipher sm4-xts-plain64 --key-size 256"
;; 开启引导分区是第一个分区的检查
DI_FIRST_IS_BOOTLOADER_CONFIG = false
;; 分区显示的单位
DI_SIZE_MUNIT_CONFIG = "MiB"
DI_SIZE_GUNIT_CONFIG = "GiB"
;; boot分区支持的文件系统配置
DI_BOOT_PARTITION_FS_CONFIG = "ext4;ext3"
;; efi分区支持的文件系统配置
DI_EFI_PARTITION_FS_CONFIG = "vfat"
;; 分区支持的文件系统和挂载点
DI_PARTITION_MOUNT_POINT_CONFIG="/;/boot;/home;/tmp;/var;/srv;/opt;/usr/local"
DI_PARTITION_FILESYSTEM_CONFIG="ext4;ext3;linux-swap;xfs;btrfs;lvm2 pv"
DI_LVM_FILESYSTEM_CONFIG="ext4;ext3;linux-swap;xfs;btrfs"

;; 用户名的最小长度，不可以为空
DI_USERNAME_MIN_LEN_CONFIG = 3
;; 用户名的最大长度
DI_USERNAME_MAX_LEN_CONFIG = 32

;; 系统保留主机名列表，以;分割
DI_HOSTNAME_RESERVED_CONFIG = "localhost"
;; 主机名是否锁定（不允许改变）
DI_LOCK_HOSTNAME_CONFIG = false
;; 主机名后缀，用户名和主机名后缀拼接成主机名
;; 例如用户名为："linux", 那么主机名为： "linux-PC"
DI_HOSTNAME_AUTO_SUFFIX_CONFIG = "-PC"

;; 用户密码采用密文
DI_PASSWORD_ENCRYPTION_CONFIG = true

;; 用户密码的最小长度，0意味着空密码
DI_PASSWORD_MIN_LEN_CONFIG = 1
;; 用户密码的最大长度
DI_PASSWORD_MAX_LEN_CONFIG = 510
;; 用户密码字符串集
DI_PASSWORD_VALIDATE_CONFIG = "1234567890;abcdefghijklmnopqrstuvwxyz;ABCDEFGHIJKLMNOPQRSTUVWXYZ;~`!@#$%^&*()-_+=|\\{}[]:\"'<>,.?/"

DI_PASSWORD_VALIDATE_REQUIRED_CONFIG = 1
;; 强密码校验开关
DI_PASSWORD_STRONG_CHECK_CONFIG = true
;; 回文的长度
DI_PASSWORD_PALINGROME_LENGTH_CONFIG = 0
;; 密码单调字符长度，设置为0表示不限制
DI_PASSWORD_MONOTONOUS_LENGTH_CONFIG = 0
;; 密码连续相同字符长度，设置为0表示不限制
DI_PASSWORD_CONTINUOUS_LENGTH_CONFIG = 0
;; 是否开启主界面的动画
DI_IS_ENABLE_MOVIE_CONFIG = true;
;; 默认的label为_dde_data分区（data分区）的挂载点
DI_DATA_MOUNT_POINT_CONFIG = "/data"
;; 配置格式化分区的次数，防止内核分区表没有更新导致的实际文件系统没有被格式化的问题
DI_FORMAT_PARTITION_CNT_CONFIG = 3
;; 内核驱动接口
DI_UTCS_EXEC_CONFIG=/usr/sbin/utcs
;; 设置时间模式
;; true  : 开机 ---> BIOS ---> UTC（将BIOS中的时间看成是UTC）---> (时区变化) ---> CST
;;       : 关机 ---> CST  ---> (时区变化) ---> UTC ---> 存储到 ---> BIOS
;; false : 开机 ---> BIOS ---> CST（将BIOS中的时间看成是CST）
;;       : 关机 ---> CST  ---> 存储到 ---> BIOS
DI_USE_RTC_TIME_CONFIG = true
;; 配置是否将系统时间同步到rtc时间中
DI_SYSTIME_TO_HC = true
;; 是否开启NTP服务
DI_IS_ENABLE_NTP = true
;; 是否执行deepin-installer-extra
DI_EXEC_EXTRA = true
;; tpm解密命令
DI_TPM_INITRAMFS_TOOL_CONFIG=""
DI_DISK_RSA_FILE_CONFIG=""
;; 公钥路径默认从镜像根路径，例如:pub.key，实际路径是: /${DI_LIVE_DIR_ENV}/pub.key
DI_DISK_PUB_RSA_FILE_CONFIG=""
DI_DISK_KEY_FILE_CONFIG=disk%1.key
DI_DISK_CRYPT_KEY_FILE_CONFIG=disk_crypt%1.key

# 镜像签名工具的路径， 以分号分割
DI_DEEPIN_SQUASHFS_VERIFY="/usr/bin/deepin-iso-verify"

DI_UI_DEFAULT_FONT_CONFIG="Noto Sans CJK SC"

#桌面环境
DI_DESKTOP_ENV=x11

;; 指定安装的系统版本Professional-专业版、E-教育版
DI_EDITION_NAME="Professional"

;; 默认分辨率
DI_DEFAULT_RESOLUTION="1920x1080;1400x1050;1440x900;1280x1024;1280x768;1024x768;1280x720;800x600"

;; 界面背景图片
DI_BACKGROUND_IMAGE="/usr/share/wallpapers/deepin/nirvana-wallpaper-dark.jpg"

;; unsquashfs的参数,如："-mem-percent 70"
DI_UNSQUASHFS_EXTRA_PARAMS=""

;; 是否保留授权数据
DI_IS_SAVE_AUTHORIZED_DATA_CONFIG = true

; --------------------------- 页面功能配置选项 ---------------;

[V20]
DI_DATA_MOUNT_POINT_CONFIG = "/data"

[V23]
DI_DATA_MOUNT_POINT_CONFIG = "/persistent"

[V25]
DI_DATA_MOUNT_POINT_CONFIG = "/persistent"
DI_PARTITION_MOUNT_POINT_CONFIG="/;/persistent;/boot;/home;/tmp"liveuser@liveuser-pc:~$ cat /etc/deepin-installer/deepin-installer.conf
[General]
DI_APT_SOURCE_DEB=deb https://community-packages.deepin.com/beige/ crimson main commercial community
DI_APT_SOURCE_DEB_SRC=#deb-src https://community-packages.deepin.com/beige/ crimson main commercial community
DI_AUTO_CHECK_MODE=false
DI_AVATAR=
DI_BACKGROUND_IMAGE=/usr/share/wallpapers/deepin/nirvana-wallpaper-dark.jpg
DI_BOOTLOADER=
DI_BOOTLOADER_IS_EFI=false
DI_BOOTLOADER_OPTION_CONFIG=deepin
DI_BOOT_PARTITION_FS_CONFIG="ext4;ext3"
DI_BOOT_SIZE_CONFIG=4096
DI_CHECK_MODE_HOLD=false
DI_CM_PASSWORD=Test@121!
DI_CM_USER=test
DI_COMPONENT_LANGUAGE=
DI_COMPONENT_PACKAGES=
DI_COMPONENT_TYPE=
DI_CRYPTO_LOAD=true
DI_CRYPT_PASSWORD=
DI_DATA_MIN_SIZE_CONFIG=20%
DI_DATA_MOUNT_POINT_CONFIG=/data
DI_DDE_AVATAR_DIR_CONFIG=/var/lib/AccountsService/icons/
DI_DEEPIN_SQUASHFS_VERIFY=/usr/bin/deepin-iso-verify
DI_DEFAULT_RESOLUTION="1920x1080;1400x1050;1440x900;1280x1024;1280x768;1024x768;1280x720;800x600"
DI_DESKTOP_ENV=x11
DI_DEVICETYPES_CONFIG=disk
DI_DEVICE_LIST=
DI_DEVICE_MIN_SIZE_CONFIG=64
DI_DISK_CRYPT_ALGORITHM_CONFIG=--pbkdf-memory 512000 --pbkdf pbkdf2 --key-size 256
DI_DISK_CRYPT_KEY_FILE_CONFIG=disk_crypt%1.key
DI_DISK_KEY_FILE_CONFIG=disk%1.key
DI_DISK_ONLY_FORMAT=
DI_DISK_PUB_RSA_FILE_CONFIG=
DI_DISK_RSA_FILE_CONFIG=
DI_EDITION_NAME=Desktop
DI_EDITION_PLUGIN_LOAD=false
DI_EFI_PARTITION_FS_CONFIG=vfat
DI_EFI_SIZE_CONFIG=300
DI_ENABLE_AUTO_SELECT_DISK=true
DI_ENABLE_COMPRESS=0
DI_ENABLE_ICE_RESTORE=false
DI_EXEC_EXTRA=true
DI_FIRST_IS_BOOTLOADER_CONFIG=false
DI_FORMAT_PARTITION_CNT_CONFIG=3
DI_GRUB_RESOLUTION_CONFIG=auto
DI_GRUB_TIMEOUT_CONFIG=1
DI_HOOKS_LIST=
DI_HOSTNAME=
DI_HOSTNAME_AUTO_SUFFIX_CONFIG=-PC
DI_HOSTNAME_RESERVED_CONFIG=localhost
DI_ICE_RESTORE_WHITELIST=
DI_IMMUTABLE_SYSTEM=true
DI_INSTALLER_STATUS=100
DI_INSTALL_MODE=default
DI_IS_AUTO_DECRYPT=false
DI_IS_CHECK_MODE=false
DI_IS_ENABLE_MOVIE_CONFIG=true
DI_IS_ENABLE_NTP=true
DI_IS_GHOST_MODE=false
DI_IS_INIT_RECOVERY=true
DI_IS_SAVE_AUTHORIZED_DATA_CONFIG=true
DI_LAYOUT=nl
DI_LAYOUT_VARIANT=us
DI_LC_HOOKS_LIST=/usr/share/deepin-installer/tools/hooks//live_config
DI_LOCALE=nl_NL
DI_LOCALTIME=Asia/Shanghai
DI_LOCK_HOSTNAME_CONFIG=false
DI_LVM2_PV_SIZE_CONFIG=5
DI_LVM_FILESYSTEM_CONFIG="ext4;ext3;linux-swap;xfs;btrfs"
DI_NVIDIA_CHECK=false
DI_OLD_USERNAME_LIST=
DI_OTHER_SIZE_CONFIG=2048
DI_PARTITION_CONFIG=/etc/deepin-installer/partition_policy.json
DI_PARTITION_FILESYSTEM_CONFIG="ext4;ext3;linux-swap;xfs;btrfs;lvm2 pv"
DI_PARTITION_MOUNT_POINT_CONFIG="/;/boot;/home;/tmp;/var;/srv;/opt;/usr/local"
DI_PARTITION_TYPE=0
DI_PASSWORD=
DI_PASSWORD_CONTINUOUS_LENGTH_CONFIG=0
DI_PASSWORD_ENCRYPTION_CONFIG=true
DI_PASSWORD_MAX_LEN_CONFIG=510
DI_PASSWORD_MIN_LEN_CONFIG=1
DI_PASSWORD_MONOTONOUS_LENGTH_CONFIG=0
DI_PASSWORD_PALINGROME_LENGTH_CONFIG=0
DI_PASSWORD_STRONG_CHECK_CONFIG=true
DI_PASSWORD_VALIDATE_CONFIG="1234567890;abcdefghijklmnopqrstuvwxyz;ABCDEFGHIJKLMNOPQRSTUVWXYZ;~`!@#$%^&*()-_+=|\\{}[]:\"'<>,.?/"
DI_PASSWORD_VALIDATE_REQUIRED_CONFIG=1
DI_PERSISTENT_SIZE_CONFIG=20480
DI_PRIVACY_POLICY=true
DI_PRIVACY_POLICY_LOAD=true
DI_QUICK_LOGIN=true
DI_REBOOT_AFTER_SETUP=false
DI_RECOVERY_BASE_SIZE_CONFIG=11264
DI_REGION=Belgium
DI_REGION_FORMAT=Dutch:Belgium
DI_REGION_FORMAT_CURRENCY_SYMBOL=€
DI_REGION_FORMAT_DECIMAL_SYMBOL=","
DI_REGION_FORMAT_DIGIT_GROUP_SEPARATOR=.
DI_REGION_FORMAT_FIRST_DAY_OF_WEEK_FORMAT=1
DI_REGION_FORMAT_LOCALE=nl_BE
DI_REGION_FORMAT_LOCALE_SET=nl_BE.UTF-8
DI_REGION_FORMAT_LONG_DATE_FORMAT=dddd d MMMM yyyy
DI_REGION_FORMAT_LONG_TIME_FORMAT=HH:mm:ss tttt
DI_REGION_FORMAT_NUMBER_FORMAT=123.456.789
DI_REGION_FORMAT_PAPER_SIZE=A4
DI_REGION_FORMAT_SHORT_DATE_FORMAT=d/MM/yyyy
DI_REGION_FORMAT_SHORT_TIME_FORMAT=HH:mm
DI_ROOTA_SIZE_CONFIG=23552
DI_ROOT_PASSWORD=
DI_ROOT_USER_LOAD=false
DI_SAVE_USER_DATA_CLEAN_LIST="/home/*/.cache;/home/*/.tmp;/home/*/.config/deepin/dde-welcome.conf;/home/*/.config/deepin/01-dde-welcome.conf"
DI_SAVE_USER_DATA_LOAD=true
DI_SIZE_GUNIT_CONFIG=GiB
DI_SIZE_MUNIT_CONFIG=MiB
DI_SWAP_MAX_SIZE_CONFIG=16384
DI_SYSTEM_FONT_SIZE_CONFIG=14
DI_SYSTIME_TO_HC=true
DI_TIMEZONE=Asia/Beijing
DI_TPM_ALGS=
DI_TPM_CHECK=false
DI_TPM_INITRAMFS_TOOL_CONFIG=
DI_TPM_PCR_BANCKS=
DI_UIMG_FILE=
DI_UI_DEFAULT_FONT_CONFIG=Noto Sans CJK SC
DI_UNSQUASHFS_EXTRA_PARAMS=
DI_USERNAME=
DI_USERNAME_MAX_LEN_CONFIG=32
DI_USERNAME_MIN_LEN_CONFIG=3
DI_USER_EXPERIENCE=false
DI_USER_EXPERIENCE_LOAD=false
DI_USE_DEBUG_FRAME=true
DI_USE_RTC_TIME_CONFIG=true
DI_UTCS_EXEC_CONFIG=/usr/sbin/utcs
DI_VALID_DEVICE_MIN_SIZE_CONFIG=45
LIVE_HOSTNAME=liveuser-pc
LIVE_LOCALES=zh_CN.UTF-8
LIVE_TIMEZONE=Asia/Shanghai
LIVE_USERNAME=liveuser
LIVE_USER_FULLNAME=Live User
apt_source_deb=deb https://community-packages.deepin.com/beige/ crimson main commercial community
apt_source_deb_src=#deb-src https://community-packages.deepin.com/beige/ crimson main commercial community

[V20]
DI_DATA_MOUNT_POINT_CONFIG=/data

[V23]
DI_DATA_MOUNT_POINT_CONFIG=/persistent

[V25]
DI_DATA_MOUNT_POINT_CONFIG=/persistent
DI_PARTITION_MOUNT_POINT_CONFIG="/;/persistent;/boot;/home;/tmp"
liveuser@liveuser-pc:~$ ls /sys/firmware/efi >/dev/null 2>&1 && echo UEFI || echo BIOS
BIOS
liveuser@liveuser-pc:~$ sudo grep -rn '/proc/cmdline' /usr/share/deepin-installer/ /usr/lib/deepin-installer/ /usr/bin/deepin-installer* 2>/dev/null | head -20
/usr/share/deepin-installer/tools/functions/bootloader_funcs.sh:263:    if [[ $(cat /proc/cmdline) =~ \ nomodeset(\ |$) ]]; then
/usr/share/deepin-installer/tools/functions/default_funcs.sh:107:# 从 /proc/cmdline 中提取指定 key 的值
/usr/share/deepin-installer/tools/functions/default_funcs.sh:113:    cmdline=$(cat /proc/cmdline)
/usr/share/deepin-installer/tools/functions/default_funcs.sh:123:# 从 /proc/cmdline 检查是否存在精确的 key=value 参数
/usr/share/deepin-installer/tools/functions/default_funcs.sh:128:    cmdline=$(cat /proc/cmdline)
/usr/share/deepin-installer/tools/functions/default_funcs.sh:134:    for _PARAMETER in $(cat /proc/cmdline); do
/usr/share/deepin-installer/tools/functions/default_funcs.sh:146:    for _PARAMETER in $(cat /proc/cmdline); do
/usr/share/deepin-installer/tools/functions/default_funcs.sh:1510:# 解析 /proc/cmdline 内核启动参数，判断 PXE 类型并提取网络路径。
/usr/share/deepin-installer/tools/functions/default_funcs.sh:1527:    local cmdline_content=$(cat /proc/cmdline)
/usr/share/deepin-installer/tools/functions/default_funcs.sh:1613:    local setting_ini_path=$(cat /proc/cmdline | grep "DI_SETTINGS_FILE_PATH=")
/usr/share/deepin-installer/tools/functions/default_funcs.sh:1756:# 根据 /proc/cmdline 中的审核模式参数写入安装器配置
/usr/share/deepin-installer/tools/functions/default_funcs.sh:1809:    for cmdLineParameter in $(cat /proc/cmdline); do
/usr/share/deepin-installer/tools/functions/dpkg_funcs.sh:9:# PXE 类型和网络路径由 get_pxe_type_net_path 从 /proc/cmdline 解析得到。
/usr/share/deepin-installer/tools/hooks/before_chroot/08_setup_system.job:37:# 根据 /proc/cmdline 中的审核模式参数设置安装器配置
liveuser@liveuser-pc:~$ sudo grep -rn 'deepin-installer\.conf\|DI_CONFIG_FILE\|DI_CONFIG_DIR\|CONFIG_FILE_ENV' /usr/share/deepin-installer/ 2>/dev/null | head -30
/usr/share/deepin-installer/configs/live-config/deepin-installer-live:21:    init_config $DI_CONFIG_FILE_ENV
/usr/share/deepin-installer/tools/check_hooks/check_mode_quit.sh:15:echo "${USER_PASSWORD}" | sudo -S deepin-installer-config set "${DI_CONFIG_FILE_ENV}"  "DI_CHECK_MODE_HOLD" "false"
/usr/share/deepin-installer/tools/deepin-installer-preinit:26:    init_config $DI_CONFIG_FILE_ENV
/usr/share/deepin-installer/tools/functions/default_funcs.sh:6:    [ -f "${DI_CONFIG_FILE_ENV}" ] || error "$DI_CONFIG_FILE_ENV is not defined"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:11:    $config_bin get "${DI_CONFIG_FILE_ENV}" "${sesion}" "${key}" 2>/dev/null
/usr/share/deepin-installer/tools/functions/default_funcs.sh:28:    $config_bin set "${DI_CONFIG_FILE_ENV}" "${sesion}" "${key}" "${value}" 2>/dev/null
/usr/share/deepin-installer/tools/functions/default_funcs.sh:67:    init_config "${DI_CONFIG_FILE_ENV}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:516:    local CONF_DIR=$(get_file_path $DI_CONFIG_FILE_ENV)
/usr/share/deepin-installer/tools/functions/default_funcs.sh:552:        deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_INSTALL_MODE" "default"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:593:        deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_INSTALL_MODE" "${install_mode}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:620:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_INSTALL_MODE" "default"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:623:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_IS_CHECK_MODE" "false"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:646:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_INSTALL_MODE" "${install_mode}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:650:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_IS_CHECK_MODE" "${is_check_mode}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:744:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_INSTALL_MODE" "default"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:747:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_IS_CHECK_MODE" "false"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:829:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_INSTALL_MODE" "${install_mode}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:833:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_IS_CHECK_MODE" "${is_check_mode}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:927:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_INSTALL_MODE" "default"
/usr/share/deepin-installer/tools/functions/default_funcs.sh:930:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_IS_CHECK_MODE" "false"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:361:    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_CRYPT_INFO" "${CRYPT_INFO}"
/usr/share/deepin-installer/tools/hooks/after_chroot/99_gen_experience.job:114:        install -Dm644 -v "${DI_CONFIG_DIR_ENV}/settings/deepin-user-experience" "${experience_file}"
/usr/share/deepin-installer/tools/hooks/after_chroot/99_gen_experience.job:119:    DI_CRYPT_INFO=$(deepin-installer-config get "/target/${DI_CONFIG_FILE_ENV}" "DI_CRYPT_INFO");
/usr/share/deepin-installer/tools/hooks/after_chroot/99_gen_experience.job:134:        install -Dm644 -v "${DI_CONFIG_DIR_ENV}/settings/installer_record.json" "${record_file}"
/usr/share/deepin-installer/tools/hooks/ghost/05_init_recovery.job:11:deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_IS_CHECK_MODE" "false"
/usr/share/deepin-installer/tools/scripts/init_environment.sh:8:export DI_CONFIG_DIR_ENV=$DI_HOME_DIR_ENV/configs
/usr/share/deepin-installer/tools/scripts/init_environment.sh:11:export DI_CONFIG_FILE_ENV=/etc/deepin-installer/deepin-installer.conf
/usr/share/deepin-installer/tools/scripts/init_environment.sh:29:echo "DI_CONFIG_DIR_ENV=$DI_CONFIG_DIR_ENV"
/usr/share/deepin-installer/tools/scripts/init_environment.sh:32:echo "DI_CONFIG_FILE_ENV=$DI_CONFIG_FILE_ENV"
liveuser@liveuser-pc:~$ ls -la /usr/share/deepin-installer/tools/hooks/live_config/ && cat /usr/share/deepin-installer/tools/hooks/live_config/* 2>/dev/null | head -80
total 1
drwxr-xr-x  2 root root  70 Jan  1  1970 .
drwxr-xr-x 12 root root 202 Jan  1  1970 ..
-rw-r--r--  1 root root 296 Jan  1  1970 00_setup_envs.job
-rw-r--r--  1 root root 246 Jan  1  1970 99_restart_lightdm.job
#!/bin/bash

# 导入函数库
source $DI_INSTALL_TOOLS_DIR_ENV/scripts/function_include.sh

# 调用函数
## 设置语言
setup_locale
## 设置键盘
setup_keyboard
## 设置时间模式
enable_local_rtc
## 设置时区
setup_timezone
# 设置区域格式
set_region_fomat

install_status 100
#!/bin/bash

#导入函数库
source $DI_INSTALL_TOOLS_DIR_ENV/scripts/function_include.sh

sleep 1

# 调用函数
## 替换掉启后配置安装器的lightdm
cleanup_lightdm_deepin_installer

## 重启lightdm
systemctl restart lightdm.service

liveuser@liveuser-pc:~$ sed -n '1600,1680p' /usr/share/deepin-installer/tools/functions/default_funcs.sh

            info "du -lh /tmp/oem/ : $(du -lh /tmp/oem/)"
            info "du -lh /tmp/pxemode/ : $(du -lh /tmp/pxemode/)"

            should_rebuild_config=1
        else
            warning "net_path is empty!"
        fi
    fi

    # 阶段 2: grub DI_SETTINGS_FILE_PATH — 允许通过内核参数指定一个额外的 settings.ini
    #   http/iso 场景: wget 从远端下载
    #   none 或 nfs 场景: 从安装介质本地复制（NFS 已挂载到本地，与优盘安装一致）
    local setting_ini_path=$(cat /proc/cmdline | grep "DI_SETTINGS_FILE_PATH=")
    if [ -n "${setting_ini_path}" ]; then
        mkdir -p /tmp/oem_settings_from_grub/ || true
        # 截取 = 号右边的值，再去掉空格后的其他内核参数
        setting_ini_path=${setting_ini_path#*DI_SETTINGS_FILE_PATH=}
        setting_ini_path=${setting_ini_path%% *}

        info "setting_ini_path is : ${setting_ini_path}"

        if [ "${pxe_install_type}" = "http" ] || [ "${pxe_install_type}" = "iso" ]; then
            # -O 指定输出文件名，-c 支持断点续传；不用 -r 递归（单文件下载）
            wget -O /tmp/oem_settings_from_grub/settings.ini -c ${setting_ini_path} || warning "download ${setting_ini_path} failed"
        else
            # NFS 或本地安装：文件系统已在本地挂载，直接复制
            cp -vf ${DI_LIVE_DIR_ENV}${setting_ini_path} /tmp/oem_settings_from_grub/settings.ini || warning "copy ${DI_LIVE_DIR_ENV}${setting_ini_path} failed"
        fi

        should_rebuild_config=1
    fi

    # 仅在实际下载/复制了新配置时才重建，避免无意义的配置重写
    if [ ${should_rebuild_config} -eq 1 ]; then
        rebuild_installer_config
    fi
}


# 读取INI配置文件的函数
read_ini_file() {
    key=$1
    section=$2
    configfile=$3
    cat $configfile | grep $key -w >/dev/null 2>&1
    if [[ $? -ne 0 ]]; then
        echo "read $key from ini fail"
        return 1
    fi

    local read_data=$(awk -F '=' '/\['$section'\]/{a=1} a==1&&$1~/'$key'/ {print $2;exit}' $configfile)
    echo "$read_data"
}

# 杀死在chroot阶段开启的进程，因为chroot阶段开启的进程，在退出chroot阶段后，会继续运行。
# 导致分区未卸载干净，影响deepin-clone执行初始化备份
kill_process_opened_by_chroot() {
    info "kill_process_opened_by_chroot"
    find /proc -maxdepth 1 -type d | grep -P '/proc/\d+' | while read -r path; do
        #echo "path=$path"
        if [ ! -f "${path}/cmdline" ]; then
            continue
        fi
        apppid=${path:6}
        exefile=$(readlink "${path}/exe" || echo "")
        #echo "execfile=$exefile"
        rootdir=$(readlink "${path}/root" || echo "")
        #echo "rootdir=$rootdir"
        if [ "${rootdir}" == "/target" ]; then
            info "${exefile} - ${rootdir}"
            kill -9 ${apppid} || echo "kill ${exefile}:${apppid} failed"
        fi
    done
}

copy_locales() {
    mkdir -p /boot/usec-crypt
    cp -vfr /etc/default/locale /boot/usec-crypt
}

liveuser@liveuser-pc:~$ 'sed -n '100,155p' /usr/share/deepin-installer/tools/functions/default_funcs.sh'
bash: sed -n 100,155p /usr/share/deepin-installer/tools/functions/default_funcs.sh: No such file or directory
liveuser@liveuser-pc:~$ sed -n '100,155p' /usr/share/deepin-installer/tools/functions/default_funcs.sh
    if ! is_empty_dir $work_dir;then
        for job in $(find $work_dir -maxdepth 1 -name "*.job" | sort); do
            bash $DI_INSTALL_TOOLS_DIR_ENV/scripts/hook_manager.sh $job
        done
    fi
}

# 从 /proc/cmdline 中提取指定 key 的值
# 用法：_extract_cmdline_value "DI_CM_PASSWORD"
# 输出：值内容（stdout）
extract_cmdline_value() {
    local key="$1"
    local cmdline
    cmdline=$(cat /proc/cmdline)
    local value=""

    if [[ "${cmdline}" =~ ${key}=([^ ]+) ]]; then
        value="${BASH_REMATCH[1]}"
    fi

    printf '%s' "${value}"
}

# 从 /proc/cmdline 检查是否存在精确的 key=value 参数
# 使用正则确保完整匹配（flag 后面必须是空格或行尾）
has_cmdline_flag() {
    local flag="$1"
    local cmdline
    cmdline=$(cat /proc/cmdline)
    [[ "${cmdline}" =~ (^|[[:space:]])${flag}($|[[:space:]]) ]]
}

is_livecd_install() {
    # Reading kernel command line
    for _PARAMETER in $(cat /proc/cmdline); do
        case "${_PARAMETER}" in
            live-config.livecd-installer|livecd-installer)
          return 0
          ;;
        esac
    done
    return 1
}

is_livecd() {
    # Reading kernel command line
    for _PARAMETER in $(cat /proc/cmdline); do
        case "${_PARAMETER}" in
            boot=live)
          return 0
          ;;
        esac
    done
    return 1
}

liveuser@liveuser-pc:~$ 'grep -n 'DI_LIVE_DIR_ENV\|DI_HOME_DIR_ENV\|DI_SRC_DIR_ENV' /usr/share/deepin-installer/tools/scripts/init_environment.sh'
bash: grep -n DI_LIVE_DIR_ENV|DI_HOME_DIR_ENV|DI_SRC_DIR_ENV /usr/share/deepin-installer/tools/scripts/init_environment.sh: No such file or directory
liveuser@liveuser-pc:~$ grep -n 'DI_LIVE_DIR_ENV\|DI_HOME_DIR_ENV\|DI_SRC_DIR_ENV' /usr/share/deepin-installer/tools/scripts/init_environment.sh
6:export DI_HOME_DIR_ENV=/usr/share/deepin-installer
7:export DI_TOOLS_DIR_ENV=$DI_HOME_DIR_ENV/tools
8:export DI_CONFIG_DIR_ENV=$DI_HOME_DIR_ENV/configs
9:export DI_SRC_DIR_ENV=$DI_HOME_DIR_ENV/src
10:export DI_MODULE_DIR_ENV=$DI_HOME_DIR_ENV/modules
13:export DI_INSTALL_DIR_ENV=$DI_HOME_DIR_ENV
14:export DI_INSTALL_TOOLS_DIR_ENV=$DI_HOME_DIR_ENV/tools
18:export DI_LIVE_DIR_ENV=/usr/lib/live/mount/medium/
19:export DI_OEM_DIR=$DI_LIVE_DIR_ENV/oem/
27:echo "DI_HOME_DIR_ENV=$DI_HOME_DIR_ENV"
30:echo "DI_SRC_DIR_ENV=$DI_SRC_DIR_ENV"
36:echo "DI_LIVE_DIR_ENV=$DI_LIVE_DIR_ENV"
liveuser@liveuser-pc:~$ grep -n -A40 '^rebuild_installer_config' /usr/share/deepin-installer/tools/functions/default_funcs.sh
66:rebuild_installer_config() {
67-    init_config "${DI_CONFIG_FILE_ENV}"
68-}
69-
70-umount_disk() {
71-    local array=($(lsblk -lpno NAME,MOUNTPOINT |grep -v /usr/lib/live/mount | awk '{print $1}'))
72-    for element in "${array[@]}"
73-       do
74-        umount ${element} || echo "umount ${element} failed"
75-    done
76-}
77-
78-dconfig_init() {
79-    if [ -f "/usr/bin/os-config.sh" ]; then
80-        os-config.sh remove || echo "dconfig os-config.sh remove failed"
81-        os-config.sh install || echo "dconfig os-config.sh install failed"
82-    else
83-        echo "dconfig not exist"
84-    fi
85-}
86-
87-live_hooks() {
88-    local OEM_DIR_CONFIG=$DI_LIVE_DIR_ENV/oem
89-    is_empty_dir "$OEM_DIR_CONFIG/live_hooks" || cp -vfr $OEM_DIR_CONFIG/live_hooks $DI_INSTALL_TOOLS_DIR_ENV/live_hooks
90-    local work_dir="$DI_INSTALL_TOOLS_DIR_ENV/live_hooks/"
91-    if ! is_empty_dir $work_dir;then
92-        for job in $(find $work_dir -maxdepth 1 -name "*.job" | sort); do
93-            bash $DI_INSTALL_TOOLS_DIR_ENV/scripts/hook_manager.sh $job
94-        done
95-    fi
96-}
97-
98-before_install() {
99-    local work_dir="$DI_INSTALL_TOOLS_DIR_ENV/hooks/before_install"
100-    if ! is_empty_dir $work_dir;then
101-        for job in $(find $work_dir -maxdepth 1 -name "*.job" | sort); do
102-            bash $DI_INSTALL_TOOLS_DIR_ENV/scripts/hook_manager.sh $job
103-        done
104-    fi
105-}
106-
liveuser@liveuser-pc:~$ cat /etc/deepin-installer/partition_policy.json
cat: /etc/deepin-installer/partition_policy.json: No such file or directory
liveuser@liveuser-pc:~$ cat /etc/deepin-installer/partition_policy.json
cat: /etc/deepin-installer/partition_policy.json: No such file or directory
liveuser@liveuser-pc:~$ grep -rn 'DI_PARTITION_TYPE\|PARTITION_TYPE' /usr/share/deepin-installer/tools/ | head -20
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:4:    local parted_type=$(installer_get "DI_PARTITION_TYPE")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:612:    local parted_type=$(installer_get "DI_PARTITION_TYPE")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:686:    local partition_type=$(installer_get "DI_PARTITION_TYPE")
/usr/share/deepin-installer/tools/hooks/after_chroot/99_gen_experience.job:39:    DI_PARTITION_TYPE=$(installer_get "DI_PARTITION_TYPE")
/usr/share/deepin-installer/tools/hooks/after_chroot/99_gen_experience.job:46:    if [ "$DI_PARTITION_TYPE" = "0" ]; then #FullDiskMode
/usr/share/deepin-installer/tools/hooks/after_chroot/99_gen_experience.job:49:    elif [ "$DI_PARTITION_TYPE" = "1" ]; then
/usr/share/deepin-installer/tools/hooks/after_chroot/99_gen_experience.job:52:    elif [ "$DI_PARTITION_TYPE" = "2" ]; then
/usr/share/deepin-installer/tools/hooks/after_chroot/99_gen_experience.job:56:    elif [ "$DI_PARTITION_TYPE" = "3" ]; then
/usr/share/deepin-installer/tools/hooks/after_chroot/99_gen_experience.job:60:    elif [ "$DI_PARTITION_TYPE" = "7" ]; then
liveuser@liveuser-pc:~$ grep -rn -A40 'init_config()' /usr/share/deepin-installer/tools/ | head -60
/usr/share/deepin-installer/tools/functions/default_funcs.sh:37:init_config() {
/usr/share/deepin-installer/tools/functions/default_funcs.sh-38-    local CONFIG_FILE=$1
/usr/share/deepin-installer/tools/functions/default_funcs.sh-39-    # 初始化live系统下安装器配置
/usr/share/deepin-installer/tools/functions/default_funcs.sh-40-    mkdir -p $(get_file_path $CONFIG_FILE)
/usr/share/deepin-installer/tools/functions/default_funcs.sh-41-    deepin-installer-config init $CONFIG_FILE
/usr/share/deepin-installer/tools/functions/default_funcs.sh-42-    # 初始化引导的类型的配置
/usr/share/deepin-installer/tools/functions/default_funcs.sh-43-    init_bootloader_type
/usr/share/deepin-installer/tools/functions/default_funcs.sh-44-    # 初始化tpm检测的配置
/usr/share/deepin-installer/tools/functions/default_funcs.sh-45-    init_tpm_check
/usr/share/deepin-installer/tools/functions/default_funcs.sh-46-    # 检查是否存在NVIDIA显卡
/usr/share/deepin-installer/tools/functions/default_funcs.sh-47-    init_nvidia_check
/usr/share/deepin-installer/tools/functions/default_funcs.sh-48-
/usr/share/deepin-installer/tools/functions/default_funcs.sh-49-    # 初始化live config的配置到安装器的配置中
/usr/share/deepin-installer/tools/functions/default_funcs.sh-50-    if [ -f /etc/live/config.conf.d/deepin-installer-live.conf ]; then
/usr/share/deepin-installer/tools/functions/default_funcs.sh-51-        local liveConfigItems=$(cat /etc/live/config.conf.d/deepin-installer-live.conf | tr '\n' ';')
/usr/share/deepin-installer/tools/functions/default_funcs.sh-52-        while [ -n "${liveConfigItems}" ]; do
/usr/share/deepin-installer/tools/functions/default_funcs.sh-53-            local item=${liveConfigItems%%;*}
/usr/share/deepin-installer/tools/functions/default_funcs.sh-54-            local itemName=${item%%=*}
/usr/share/deepin-installer/tools/functions/default_funcs.sh-55-            sed -i "/${itemName}/d" "${CONFIG_FILE}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh-56-            sed -i "1a ${item}" "${CONFIG_FILE}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh-57-            liveConfigItems=${liveConfigItems#*;}
/usr/share/deepin-installer/tools/functions/default_funcs.sh-58-        done
/usr/share/deepin-installer/tools/functions/default_funcs.sh-59-    fi
/usr/share/deepin-installer/tools/functions/default_funcs.sh-60-
/usr/share/deepin-installer/tools/functions/default_funcs.sh-61-    # 从command line 读取 DI_EDITION_NAME 写到配置文件中
/usr/share/deepin-installer/tools/functions/default_funcs.sh-62-    reade_edition_name_from_cmdline
/usr/share/deepin-installer/tools/functions/default_funcs.sh-63-}
/usr/share/deepin-installer/tools/functions/default_funcs.sh-64-
/usr/share/deepin-installer/tools/functions/default_funcs.sh-65-# 重新生成安装器配置文件，使新下载的覆盖配置按既定优先级重新合并。
/usr/share/deepin-installer/tools/functions/default_funcs.sh-66-rebuild_installer_config() {
/usr/share/deepin-installer/tools/functions/default_funcs.sh-67-    init_config "${DI_CONFIG_FILE_ENV}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh-68-}
/usr/share/deepin-installer/tools/functions/default_funcs.sh-69-
/usr/share/deepin-installer/tools/functions/default_funcs.sh-70-umount_disk() {
/usr/share/deepin-installer/tools/functions/default_funcs.sh-71-    local array=($(lsblk -lpno NAME,MOUNTPOINT |grep -v /usr/lib/live/mount | awk '{print $1}'))
/usr/share/deepin-installer/tools/functions/default_funcs.sh-72-    for element in "${array[@]}"
/usr/share/deepin-installer/tools/functions/default_funcs.sh-73-          do
/usr/share/deepin-installer/tools/functions/default_funcs.sh-74-        umount ${element} || echo "umount ${element} failed"
/usr/share/deepin-installer/tools/functions/default_funcs.sh-75-    done
/usr/share/deepin-installer/tools/functions/default_funcs.sh-76-}
/usr/share/deepin-installer/tools/functions/default_funcs.sh-77-
liveuser@liveuser-pc:~$ sed -n '1,40p;600,700p' /usr/share/deepin-installer/tools/functions/partition_funcs.sh
#!/bin/bash

parted_manager() {
    local parted_type=$(installer_get "DI_PARTITION_TYPE")
    local parted_conf=$(installer_get "DI_PARTITION_CONFIG")
    deepin-installer-parted -m $parted_type -c $parted_conf   # 分区管理
}

parted_mount() {
    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
    if ! is_ostree_system; then
        deepin-installer-parted -m 4 -c $mount_points   # 分区挂载
        bind_data_mount # 处理data分区内部目录的bind
        setup_data_info
        setup_efi_parted
    else
        # 此处只需要挂载root和和swap即可，其他分区需要等ostree加载完成后再进行挂载。
        mount_points=(${mount_points//;/ })
        for p in "${mount_points[@]}"
        do
            kv=(${p//=/ })
            if [ "${kv[1]}" == "/" ]; then
                [ -d /target ] || mkdir /target
                mount "${kv[0]}" "/target" || error "mount ${kv[0]} to /target failed !"
            fi
        done

        # 处理保留用户数据场景数据清理
        delete_data_without_home
    fi

    setup_swap # 处理swap分区
}

parted_umount() {
    if mount |grep /target; then
        echo "====umount /target===="
        umount -Rlf /target
    fi


## 判断是否全盘安装模式
is_full_disk_mode() {
    #FullDiskMode = 0, 全盘安装
    #AdvancedMode, 高级安装
    #Cryptsetup, 全盘加密
    #SaveData, 保留用户数据
    #MountMode = 4, 挂载分区
    #GhostMode, ghost安装
    #CustomMode, 自定义安装
    #LvmFullDisk 全盘lvm安装

    local parted_type=$(installer_get "DI_PARTITION_TYPE")
    local install_mode="02367"
    if [[ "${install_mode}" =~ ${parted_type} ]]; then
        return 0
    else
        return 1
    fi
}

setup_udisks_rules() {
    local UDISK_FILE="/target/etc/udev/rules.d/80-udisks-installer.rules"
    local usedUuids=""
    # 隐藏efi分区
    local EFI_ID=$(lsblk -o UUID,MOUNTPOINT -f | grep -i /target/boot/efi$ | awk '{print $1}')
    local EFI_PART_PATH=$(lsblk -o PATH,MOUNTPOINT -f | grep -i /target/boot/efi$ | awk '{print $1}')
    if [ -n "$EFI_ID" ]; then
        echo "# hide ${EFI_PART_PATH} efi" >> ${UDISK_FILE}
        echo "ENV{ID_FS_UUID}==\"${EFI_ID}\", ENV{UDISKS_IGNORE}=\"1\"" >> ${UDISK_FILE}
        usedUuids="${EFI_ID}"
    fi

    # 隐藏boot分区
    local BOOT_ID=$(lsblk -o UUID,MOUNTPOINT -f | grep -i /target/boot$ | awk '{print $1}')
    local BOOT_PART_PATH=$(lsblk -o PATH,MOUNTPOINT -f | grep -i /target/boot$ | awk '{print $1}')
    if [ -n "$BOOT_ID" ]; then
        echo "# hide ${BOOT_PART_PATH} boot" >> ${UDISK_FILE}
        echo "ENV{ID_FS_UUID}==\"${BOOT_ID}\", ENV{UDISKS_IGNORE}=\"1\"" >> ${UDISK_FILE}
        usedUuids="${usedUuids};${BOOT_ID}"
    fi

    # persistent和home分区同时存在时隐藏persistent分区
    if exists_data_part  && exist_home_part; then
        local data_device=$(installer_get DI_DATA_DEVICE_CONFIG)
        if [ -n "$data_device" ]; then
            local data_device_uuid=$(blkid -s UUID -o value $data_device)
            echo "# hide ${data_device} persistent" >> ${UDISK_FILE}
            echo "ENV{ID_FS_UUID}==\"${data_device_uuid}\", ENV{UDISKS_IGNORE}=\"1\"" >> ${UDISK_FILE}
        fi
    fi

    # 隐藏swap分区
    echo "# hide swap" >> ${UDISK_FILE}
    echo "ENV{ID_FS_TYPE}==\"swap\", ENV{UDISKS_IGNORE}=\"1\"" >> ${UDISK_FILE}

    # 隐藏使用到的加密分区
    if is_full_disk_mode; then
        local luks_parts=$(installer_get "DI_CRYPT_DEVICE")
        for lp in $luks_parts;do
            local part="$lp"
            local luks_name=${part##*/}
            local crypt_part=$(cryptsetup status ${part} | grep "device" | awk '{print $2}')
            local luks_uuid=""
            if [ -n "${crypt_part}" ]; then
                luks_uuid=$(lsblk -lpfo UUID,PATH | grep "${crypt_part}" | awk '{print $1}')
                if [ -n "${luks_uuid}" ]; then
                    echo "# hide ${part}" >> ${UDISK_FILE}
                    echo "ENV{ID_FS_UUID}==\"${luks_uuid}\", ENV{UDISKS_IGNORE}=\"1\"" >> ${UDISK_FILE}
                    usedUuids="${usedUuids};${luks_uuid}"
                fi
            fi
        done
    fi

    local useHideCryptHeader=$(installer_get "DI_USE_HIDE_CRYPT_HEADER")
    if [ "x${useHideCryptHeader}" = "xtrue" ]; then
        # 隐藏crypt header lvm分区
        echo "# hide all crypt header part"
        echo "ENV{DM_VG_NAME}==\"usec-vg-*\", ENV{DM_LV_NAME}==\"lv-*\", ENV{UDISKS_IGNORE}=\"1\""
        echo "ENV{DM_VG_NAME}==\"usec-vg-*\", ENV{DM_LV_NAME}==\"lv-*\", ENV{UDISKS_IGNORE}=\"1\"" >> ${UDISK_FILE}
    fi
}

# 保留用户数据安装场景，清理/persistent挂载点对应分区下除home目录之外的其他数据
delete_data_without_home() {
    local partition_type=$(installer_get "DI_PARTITION_TYPE")

    if [ ${partition_type} -eq 3 ]; then
        local mount_points=$(installer_get "DI_MOUNTS_POINTS")
        mount_points=(${mount_points//;/ })
        local data_device=""
        for p in "${mount_points[@]}"
        do
            kv=(${p//=/ })
            if [ "${kv[1]}" == "/persistent" ];then
                data_device="${kv[0]}"
                break
            fi
        done

liveuser@liveuser-pc:~$ grep -rn '"6"\|== 6\|-eq 6\|MultiSystem\|multi_system\|multisystem' /usr/share/deepin-installer/tools/ | head -20
liveuser@liveuser-pc:~$ cat /etc/live/config.conf.d/deepin-installer-live.conf 2>/dev/null; ls -la /etc/live/config.conf.d/
LIVE_HOSTNAME="liveuser-pc"
LIVE_USERNAME="liveuser"
LIVE_USER_FULLNAME="Live User"
LIVE_TIMEZONE="Asia/Shanghai"
LIVE_LOCALES=zh_CN.UTF-8
total 2
drwxr-xr-x 1 root root  49 Jan  1  1970 .
drwxr-xr-x 1 root root  36 Jan  1  1970 ..
-rw-r--r-- 1 root root 139 Jan  1  1970 deepin-installer-live.conf
liveuser@liveuser-pc:~$ deepin-installer-parted --help 2>&1 | head -40
Detected locale "C" with character encoding "ANSI_X3.4-1968", which is not UTF-8.
Qt depends on a UTF-8 locale, and has switched to "C.UTF-8" instead.
If this causes problems, reconfigure your locale. See the locale(1) manual
for more information.
2026-09-06, 04:18:18.382 [Warning] [PluginManager.cpp    PluginsManager::getPathPlugin       80] "/libDeepinInstallerPartition.so"  is load failed. "The shared library was not found."
No appenders registered with dlogger
liveuser@liveuser-pc:~$ grep -rn 'DI_MOUNTS_POINTS' /usr/share/deepin-installer/tools/ | head -20
/usr/share/deepin-installer/tools/functions/default_funcs.sh:820:    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
/usr/share/deepin-installer/tools/functions/default_funcs.sh:1003:    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh:86:    mount_points=$(installer_get "DI_MOUNTS_POINTS")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:10:    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:47:    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:211:    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:218:    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:689:        local mount_points=$(installer_get "DI_MOUNTS_POINTS")
/usr/share/deepin-installer/tools/hooks/before_chroot/08_setup_system.job:12:    mount_points=$(installer_get "DI_MOUNTS_POINTS")
liveuser@liveuser-pc:~$ grep -aoE '\-\-[a-z-]{2,}|Usage:|usage:' /usr/bin/deepin-installer-parted | sort -u | head -20
--auto
--bind
--force
--key-slot
--label
--mode
--output
--primary
--same-as
--timeout
--wipesignatures
--yes
liveuser@liveuser-pc:~$ sed -n '200,235p' /usr/share/deepin-installer/tools/functions/partition_funcs.sh
    if [ ! -n "$new_swap" ];then
        # 如果系统内有已存在的swap分区，则复用该分区，除了lvm中的swap分区
        local old_swap=$(lsblk -lfno PATH,FSTYPE,TYPE,MOUNTPOINT | grep "part" | grep "swap" | grep -v "SWAP"| awk '{print $1}')
        info "old_swap=$old_swap"
        if [ -n "$old_swap" ];then
            swapon $(lv_to_part "$old_swap")
        fi
    fi
}

exists_data_part() {
    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
    local data_mount_points=$(installer_get "DI_DATA_MOUNT_POINT_CONFIG" "${DI_OS_VERSION_ENV}")
    echo "$mount_points" | grep -wq "$data_mount_points"
    return $?
}

exist_home_part() {
    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
    echo "$mount_points" | grep -wq "/home"
    return $?
}

bind_data_mount() {
    if exists_data_part; then
        local data_mount_points=$(installer_get "DI_DATA_MOUNT_POINT_CONFIG" "${DI_OS_VERSION_ENV}")

        mkdir -p /target/${data_mount_points}/home
        mkdir -p /target/home
        mount --bind /target/${data_mount_points}/home /target/home || error "Faild to mount /target/home"

        mkdir -p /target/${data_mount_points}/opt
        mkdir -p /target/opt
        mount --bind /target/${data_mount_points}/opt /target/opt || error "Faild to mount /target/opt"

        mkdir -p /target/root
liveuser@liveuser-pc:~$ sed -n '1,45p' /usr/share/deepin-installer/tools/hooks/before_chroot/08_setup_system.job
#!/bin/bash

#导入函数库
source $DI_INSTALL_TOOLS_DIR_ENV/scripts/function_include.sh

# 写入解压文件系统状态
install_status 10

# 磐石系统不再需要解压squashfs来生成文件系统，直接同步ostree仓库数据即可
if is_ostree_system; then
    # /dev/nvme0n1p3=/;/dev/nvme0n1p2=/boot;/dev/nvme0n1p1=/boot/efi
    mount_points=$(installer_get "DI_MOUNTS_POINTS")
    info "mount points $mount_points"
    mount_points=(${mount_points//;/ })
    root_device=""
    boot_device=""
    data_device=""
    for p in "${mount_points[@]}"
    do
        kv=(${p//=/ })
        if [ "${kv[1]}" == "/" ];then
            root_device="${kv[0]}"
        elif [ "${kv[1]}" == "/boot" ];then
            boot_device="${kv[0]}"
        elif [ "${kv[1]}" == "/persistent" ];then
            data_device="${kv[0]}"
        fi
    done

    installer_set DI_ROOT_DEVICE_CONFIG "${root_device}"
    installer_set DI_BOOT_DEVICE_CONFIG "${boot_device}"
    installer_set DI_DATA_DEVICE_CONFIG "${data_device}"
else
    unsquashfs_filesystem
fi

# 根据 /proc/cmdline 中的审核模式参数设置安装器配置
setup_check_mode_from_grub
liveuser@liveuser-pc:~$ sed -n '815,845p' /usr/share/deepin-installer/tools/functions/default_funcs.sh
    fi

    umount /target/recovery

    # 分区挂载
    local mount_points=$(installer_get "DI_MOUNTS_POINTS")
    deepin-installer-parted -m 4 -c "$mount_points"   # 分区挂载
    setup_swap       # 处理swap分区
    bind_data_mount  # 处理data分区内部目录的bind
    setup_efi_parted
    setup_mountpoint # 绑定设备目录位置

    # 设置/target中的安装模式为当前设置的模式
    local install_mode=$(installer_get "DI_INSTALL_MODE")
    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_INSTALL_MODE" "${install_mode}"

    # 设置/target中审核模式启用状态为当前设置
    local is_check_mode=$(installer_get "DI_IS_CHECK_MODE")
    deepin-installer-config set /target"${DI_CONFIG_FILE_ENV}" "DI_IS_CHECK_MODE" "${is_check_mode}"

    # 还原/target中的firstboot服务状态
    if [ x"${is_firstboot_service_enabled}" = xenabled ]; then
      chroot /target systemctl enable deepin-installer-first-boot
    else
      chroot /target systemctl disable deepin-installer-first-boot
    fi
    info "restore set deepin-installer-first-boot service status $(chroot /target systemctl is-enabled deepin-installer-first-boot)"

    # 还原/target中的extra服务状态
    if [ x"${is_extra_service_enabled}" = xenabled ]; then
      chroot /target systemctl enable deepin-installer-extra
liveuser@liveuser-pc:~$ grep -rn -A15 'is_ostree_system()' /usr/share/deepin-installer/tools/functions/ | head -30
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh:3:function is_ostree_system() {
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-4-    local immutable_system=$(installer_get "DI_IMMUTABLE_SYSTEM")
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-5-    if [[ $immutable_system == "true" ]]; then
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-6-        return 0
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-7-    else
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-8-        return 1
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-9-    fi
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-10-}
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-11-
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-12-function ostree_get_deploy() {
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-13-    local commitID ostree ostree_refspec sysroot
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-14-    sysroot=$1
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-15-    ostree_refspec=$2
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-16-    commitID=$(ostree --repo=$sysroot/ostree/repo log "$ostree_refspec" | grep commit | head -n 1 | awk '{print $2}')
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-17-    ostree="/ostree/deploy/deepin/deploy/${commitID}.0"
/usr/share/deepin-installer/tools/functions/ostree_funcs.sh-18-    echo "$ostree"
liveuser@liveuser-pc:~$ grep -rn 'ostree\|/var/opt\|/sysroot' /usr/share/deepin-installer/tools/functions/ostree_funcs.sh | head -30
3:function is_ostree_system() {
12:function ostree_get_deploy() {
13:    local commitID ostree ostree_refspec sysroot
15:    ostree_refspec=$2
16:    commitID=$(ostree --repo=$sysroot/ostree/repo log "$ostree_refspec" | grep commit | head -n 1 | awk '{print $2}')
17:    ostree="/ostree/deploy/deepin/deploy/${commitID}.0"
18:    echo "$ostree"
21:function ostree_sys_init() {
26:    local ostree_ref_spec=$DI_REPO_BASE_NAME_ENV
28:        ostree_ref_spec=$(cat /uimg/localhost/etc/immutable.conf/base-name)
30:    info "ostree_ref_spec=$ostree_ref_spec"
31:    local sys_dir=$sysroot/ostree/deploy/$os_name/deploy
34:    local sys_commit=$(ostree --repo=$sysroot/ostree/repo refs -r | grep -E "^deployment/[0-9]+/0" | head -n 1 | awk '{print $2}' || echo "")
35:    # 如果sys_commit为空，则使用ostree_ref_spec
37:        info "sys_commit is empty, use ostree_ref_spec"
38:        sys_commit=$(ostree rev-parse --repo=$sysroot/ostree/repo $ostree_ref_spec)
40:        ostree checkout --repo=$sysroot/ostree/repo $ostree_ref_spec "$sysroot/ostree/deploy/$os_name/deploy/$sys_commit.0" || error "ostree_sys_init failed"
43:        ostree checkout --repo=$sysroot/ostree/repo $sys_commit $sys_dir/$sys_commit.0 || error "ostree_sys_init failed"
46:    info "ostree_sys_init ok"
49:function ostree_ext_mount() {
51:    local ostree=$2
69:        local ostree_arg=$2
70:        mkdir -p $datadir/ostree/deploy/deepin/var
72:        $mount_root_tool /target --ostree=$ostree --persistent=$data_device || error "$mount_root_tool /target $ostree failed"
74:        mount -o remount,rw /target/persistent/ostree /target/persistent/ostree || error "remount /target/persistent/ostree failed"
132:    ostree_ext_mount /ostree/deploy/deepin/deploy/$base_commit  ostree/data/$ext_commit/checkout
160:    printf 'sysroot-ro\x00\x00\x00\x00\x00\x00\x01\x00\x62\x0b\x14' > /target/run/ostree-booted
164:    rm -f /target/etc/grub.d/15_ostree || true
166:    local ext_commitId=$(ostree --repo=/target/persistent/ostree/repo rev-parse deb-ostree/main)
168:        ext_commitId=$(cat /uimg/localhost/persistent/ostree/data/status.list | head -1 | awk  -F. '{print $1}')
liveuser@liveuser-pc:~$ ^C
liveuser@liveuser-pc:~$ grep -rn -A30 'setup_efi_parted()' /usr/share/deepin-installer/tools/functions/
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:159:setup_efi_parted() {
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-160-    if is_uefi; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-161-
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-162-        local start_dev=$(installer_get "DI_START_DEV")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-163-        if [ -z "${start_dev}" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-164-            start_dev="null"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-165-        fi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-166-        local efi_part=$(lsblk -lf -o NAME,MOUNTPOINT | grep -E "/target/boot/efi$" | awk '{print $1}')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-167-        local old_efi_part=$(fdisk -l -o Device,Type | grep EFI | awk '{print $1}' | sed -n '1p')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-168-        if [ -z "$old_efi_part" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-169-            # 如果fdisk没有找到efi分区，可能是EFI分区的type没有被指定，尝试使用lsblk按label和文件系统类型查找
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-170-            old_efi_part=$(lsblk -lfp -o NAME,FSTYPE,LABEL,TRAN | grep -v "${start_dev}" | grep "vfat" | grep "EFI" | awk '{print $1}' | sed -n '1p')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-171-        fi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-172-        local roota_part=$(lsblk -lf -o NAME,MOUNTPOINT | grep -E "/target$" | awk '{print $1}')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-173-
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-174-        if [ -n "$efi_part" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-175-            setup_esp_parted "/dev/$efi_part"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-176-            installer_set DI_BOOTLOADER "/dev/$efi_part"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-177-
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-178-        elif [ -n "$old_efi_part" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-179-            setup_esp_parted "$old_efi_part"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-180-            # 判断esp分区是否有被挂载
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-181-            local mp=$(lsblk -nlf $old_efi_part -o MOUNTPOINT)
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-182-            if [ ! -n "$mp" ];then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-183-                # 复用磁盘中的efi分区
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-184-                mkdir -vp /target/boot/efi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-185-                mount "$old_efi_part" /target/boot/efi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-186-                installer_set DI_BOOTLOADER "$old_efi_part"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-187-            fi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-188-        fi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-189-    fi
liveuser@liveuser-pc:~$ ls -R /usr/share/deepin-installer/configs/
/usr/share/deepin-installer/configs/:
live-config  os  settings  slides

/usr/share/deepin-installer/configs/live-config:
deepin-installer-live  deepin-installer.desktop  deepin-installer_deepin.desktop

/usr/share/deepin-installer/configs/os:
Desktop  Device  Personal  Professional  Server

/usr/share/deepin-installer/configs/os/Desktop:
arm.ini  loongarch64.ini  loongson.ini  sw.ini  x86.ini

/usr/share/deepin-installer/configs/os/Device:
arm.ini  loongarch64.ini  loongson.ini  x86.ini

/usr/share/deepin-installer/configs/os/Personal:
arm.ini  loongarch64.ini  loongson.ini  sw.ini  x86.ini

/usr/share/deepin-installer/configs/os/Professional:
arm.ini  loongarch64.ini  loongson.ini  sw.ini  x86.ini

/usr/share/deepin-installer/configs/os/Server:
arm.ini  loongarch64.ini  loongson.ini  sw.ini  x86.ini

/usr/share/deepin-installer/configs/settings:
deepin-user-experience                    deepin_installer_live_config_plugins.json  default_settings.ini   installer_record.json  packages_choice.json   packages_sort.json
deepin_installer_first_boot_plugins.json  deepin_installer_plugins.json              full_disk_policy.json  languages.json         packages_default.json  partition_policy.json

/usr/share/deepin-installer/configs/slides:
installs  mains  uos-installtool.svg

/usr/share/deepin-installer/configs/slides/installs:
Default

/usr/share/deepin-installer/configs/slides/installs/Default:
'01-'$'\345\244\232\346\236\266\346\236\204\346\224\257\346\214\201''.png'                                      '04-'$'\345\205\250\346\226\260\346\241\214\351\235\242''.png'
'02-'$'\346\224\257\346\214\201\345\244\232\347\247\215\350\257\255\350\250\200''.png'                          '06-AI'$'\350\265\213\350\203\275''3.0.png'
'03-'$'\347\243\220\347\237\263\347\263\273\347\273\237\347\250\263\345\256\232\345\246\202\345\210\235''.png'  '07-'$'\345\272\224\347\224\250\347\224\237\346\200\201''.png'

/usr/share/deepin-installer/configs/slides/mains:
'Dream world.jpg'
liveuser@liveuser-pc:~$ sudo find / -xdev -name '*.json' -path '*install*' 2>/dev/null | head -20
/usr/share/deepin/credits/deepin-deb-installer.json
/usr/share/dsg/configs/org.deepin.installer/org.deepin.installer.json
/usr/share/deepin-installer/configs/settings/deepin_installer_first_boot_plugins.json
/usr/share/deepin-installer/configs/settings/deepin_installer_live_config_plugins.json
/usr/share/deepin-installer/configs/settings/deepin_installer_plugins.json
/usr/share/deepin-installer/configs/settings/full_disk_policy.json
/usr/share/deepin-installer/configs/settings/installer_record.json
/usr/share/deepin-installer/configs/settings/languages.json
/usr/share/deepin-installer/configs/settings/packages_choice.json
/usr/share/deepin-installer/configs/settings/packages_default.json
/usr/share/deepin-installer/configs/settings/packages_sort.json
/usr/share/deepin-installer/configs/settings/partition_policy.json
/var/lib/lastore/pacakge_installedTime.json
/var/lib/linglong/layers/c4c8932c2817b1fcf9d9c48cf3524f75e66df1b1687c4f988a18ea8f3af94e72/files/share/deepin/credits/deepin-deb-installer.json
/var/lib/linglong/layers/dce27522a6299a64a921d6fef31767aecf2ad7be705ee471c0e29c2a791abee7/files/share/deepin/credits/deepin-deb-installer.json
liveuser@liveuser-pc:~$ grep -rn -A25 'setup_esp_parted()' /usr/share/deepin-installer/tools/functions/
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:152:setup_esp_parted() {
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-153-    local part=$1
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-154-    local dev=$(part_to_device $1)
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-155-    local num=$(part_num $1)
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-156-    parted -s "$dev" set "$num" esp on
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-157-}
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-158-
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-159-setup_efi_parted() {
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-160-    if is_uefi; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-161-
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-162-        local start_dev=$(installer_get "DI_START_DEV")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-163-        if [ -z "${start_dev}" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-164-            start_dev="null"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-165-        fi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-166-        local efi_part=$(lsblk -lf -o NAME,MOUNTPOINT | grep -E "/target/boot/efi$" | awk '{print $1}')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-167-        local old_efi_part=$(fdisk -l -o Device,Type | grep EFI | awk '{print $1}' | sed -n '1p')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-168-        if [ -z "$old_efi_part" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-169-            # 如果fdisk没有找到efi分区，可能是EFI分区的type没有被指定，尝试使用lsblk按label和文件系统类型查找
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-170-            old_efi_part=$(lsblk -lfp -o NAME,FSTYPE,LABEL,TRAN | grep -v "${start_dev}" | grep "vfat" | grep "EFI" | awk '{print $1}' | sed -n '1p')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-171-        fi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-172-        local roota_part=$(lsblk -lf -o NAME,MOUNTPOINT | grep -E "/target$" | awk '{print $1}')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-173-
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-174-        if [ -n "$efi_part" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-175-            setup_esp_parted "/dev/$efi_part"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-176-            installer_set DI_BOOTLOADER "/dev/$efi_part"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-177-
liveuser@liveuser-pc:~$ 'ions/partition_funcs.sh-163-        if [ -z "${start_dev}" ]; then
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-164-            start_dev="null"
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-165-        fi
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-166-        local efi_part=$(lsblk -lf -o NAME,MOUNTPOINT | grep -E "/target/boot/efi$" | awk '{print $1}')
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-167-        local old_efi_part=$(fdisk -l -o Device,Type | grep EFI | awk '{print $1}' | sed -n '1p')
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-168-        if [ -z "$old_efi_part" ]; then
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-169-            # 如果fdisk没有找到efi分区，可能是EFI分区的type没有被指定，尝试使用lsblk按label和文件系统类型查找
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-170-            old_efi_part=$(lsblk -lfp -o NAME,FSTYPE,LABEL,TRAN | grep -v "${start_dev}" | grep "vfat" | grep "EFI" | awk '{print $1}' | sed -n '1p')
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-171-        fi
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-172-        local roota_part=$(lsblk -lf -o NAME,MOUNTPOINT | grep -E "/target$" | awk '{print $1}')
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-173-
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-174-        if [ -n "$efi_part" ]; then
> /usr/share/deepin-installer/tools/functions/partition_funcs.sh-175-            setup_esp_parted "/dev/$efi_part"
> ^C
liveuser@liveuser-pc:~$ ^C
liveuser@liveuser-pc:~$ ^C
liveuser@liveuser-pc:~$ grep -rn -A25 'setup_esp_parted()' /usr/share/deepin-installer/tools/functions/
/usr/share/deepin-installer/tools/functions/partition_funcs.sh:152:setup_esp_parted() {
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-153-    local part=$1
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-154-    local dev=$(part_to_device $1)
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-155-    local num=$(part_num $1)
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-156-    parted -s "$dev" set "$num" esp on
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-157-}
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-158-
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-159-setup_efi_parted() {
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-160-    if is_uefi; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-161-
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-162-        local start_dev=$(installer_get "DI_START_DEV")
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-163-        if [ -z "${start_dev}" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-164-            start_dev="null"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-165-        fi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-166-        local efi_part=$(lsblk -lf -o NAME,MOUNTPOINT | grep -E "/target/boot/efi$" | awk '{print $1}')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-167-        local old_efi_part=$(fdisk -l -o Device,Type | grep EFI | awk '{print $1}' | sed -n '1p')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-168-        if [ -z "$old_efi_part" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-169-            # 如果fdisk没有找到efi分区，可能是EFI分区的type没有被指定，尝试使用lsblk按label和文件系统类型查找
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-170-            old_efi_part=$(lsblk -lfp -o NAME,FSTYPE,LABEL,TRAN | grep -v "${start_dev}" | grep "vfat" | grep "EFI" | awk '{print $1}' | sed -n '1p')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-171-        fi
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-172-        local roota_part=$(lsblk -lf -o NAME,MOUNTPOINT | grep -E "/target$" | awk '{print $1}')
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-173-
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-174-        if [ -n "$efi_part" ]; then
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-175-            setup_esp_parted "/dev/$efi_part"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-176-            installer_set DI_BOOTLOADER "/dev/$efi_part"
/usr/share/deepin-installer/tools/functions/partition_funcs.sh-177-
liveuser@liveuser-pc:~$ cat /usr/share/deepin-installer/configs/settings/partition_policy.json
/*
    deepin-installer-parted输入模板
*/
[
    {
        "type":"disk",
        "device":"/dev/sda",
        "operate":"edit",
        "diskType":"msdos"
    },
    {
        "id":"",
        "type":"partition",
        "operate":"edit",
        "device":"",
        "filesystem": "ext4",
        "mountPoint": "/data2",
        "label": "_dde_data",
        "startPoint":"",
        "endPoint":"",
        "index":-1,
        "isstartpoint":true
    },
    {
        "id":"",
        "type":"partition",
        "operate":"new",
        "device":"",
        "filesystem": "ext4",
        "mountPoint": "/data2",
        "label": "_dde_data",
        "startPoint":"",
        "endPoint":"",
        "index":-1,
        "isstartpoint":true
    },
    {
        "id":"",
        "type":"partition",
        "operate":"delete",
        "device":"",
        "filesystem": "ext4",
        "mountPoint": "/data2",
        "label": "_dde_data",
        "startPoint":"",
        "endPoint":"",
        "index":-1,
        "isstartpoint":true
    },
    {
        "id":"",
        "type":"VG",
        "operate":"new",
        "vg":"vg0",
        "pv":["parted-id1", "parted-id2"],
    },
    {
        "id":"",
        "type":"VG",
        "operate":"delete",
        "vg":"vg0",
        "pv":["parted-id1", "parted-id2"],
    },
    {
        "id":"",
        "type":"LVM",
        "operate":"delete",
        "filesystem": "ext4",
        "mountPoint": "/data2",
        "label": "_dde_data",
        "size":"",
        "vg":"vg0"
    },
    {
        "id":"",
        "type":"LVM",
        "operate":"delete",
        "filesystem": "ext4",
        "mountPoint": "/data2",
        "label": "_dde_data",
        "size":"",
        "vg":"vg1"
    },
]


liveuser@liveuser-pc:~$
