# Ubuntu plugin — status: IN DEVELOPMENT (parked 2026-07-13)

Parked after an extended VM debugging campaign to prioritise M15 (closed beta
with the three validated distros). **Most of the pipeline is proven working** —
this file records exactly what is proven, what the one open problem is, and
where to resume. Do not rediscover any of this.

## Proven working (each verified in logs on a real run)

| Stage | Evidence |
|---|---|
| ISO download 6 GB + GPG (detached SHA256SUMS) | acquisition completes, hashes verified |
| >4 GiB ISO on dedicated NTFS partition (`IGLOOISO`) | FAT32 4 GiB wall solved; casper found it |
| casper boot: `boot=casper toram layerfs-path=… iso-scan/filename=/ubuntu.iso` | live session boots; toram copies medium to RAM (`Copying live_media to ram`, 6.4 GB tmpfs) |
| cloud-init NoCloud seed (CIDATA) → autoinstall loaded | `autoinstall found in cloud-config`, all sections `load_autoinstall_data: SUCCESS` |
| Pre-created root partition from Windows (diskpart, GPT type `0FC63DAF…`) | wizard creates it; declared `preserve: true` in config |
| Full-fidelity all-preserved storage config (number/offset/size/type GUID/PARTUUID per partition) | passes subiquity convert (`int+None` crashes solved) |
| Early-commands: udisks2/cloud-init masks + self-verifying disk-release loop + watchdog | verify loop ran in 171 ms → `igloo-disk-released` (disk WAS free at Early stage) |
| RAM preflight blocker (<10 GiB → Ubuntu unselectable) | fires correctly in wizard on 6 GB VM |

## The one open problem

curtin storage v2 **always** rewrites the partition table and runs
`partprobe` (verified in curtin source — no skip path even for all-preserved
configs). partprobe fails with EBUSY if ANY partition on the target disk is
held. Holders keep reappearing after our release (udisks D-Bus reactivation,
cloud-init later stages, ntfs-3g FUSE daemon, leaked ISO loop). The current
template masks + kills + sweeps (watchdog, 3 s interval, logs every holder to
`/var/log/igloo-watchdog.log`) — **this final combination has never had a
complete run**: the last attempts died first to a `pkill -f` self-match bug
(fixed) and then to a VM graphics freeze (vmwgfx, unrelated to iGloo — VMware
host had "No 3D support available" at the time).

## Where to resume (in order)

1. Re-run on a VM **with working 3D accel** (or on real hardware): the current
   published template contains every fix; the freeze was at vmwgfx init,
   *before* the installer ever started.
2. If partprobe still fails: `/var/log/igloo-watchdog.log` now names every
   holder — kill/mask that specific one. No more blind iterations needed.
3. If partitioning passes, expect standard territory (format/extract/GRUB) and
   then first-boot: bootstrap via `igloo-bootstrap.service` (same two-phase
   pattern Mint validated).

## Hard-won rules encoded in the template (do not regress)

- Subiquity early-command payloads MUST be single-quoted (outer shell expands
  `$` inside double quotes → guard silently self-disables).
- Never `pkill -f` from an early-command (matches its own cmdline → SIGTERM →
  `AutoinstallUserSuppliedCmdError`).
- Never let curtin CREATE a partition on this disk (full GPT rewrite ⇒ new
  disklabel GUID + renumbering + partprobe on busy disk). Igloo pre-creates
  root; config preserves everything and only formats.
- `layout:` presets are whole-disk wipes; only explicit `config:` lists are
  dual-boot-safe.
- Preserved partitions need `partition_type` + `uuid` or curtin's rewrite
  clobbers them (Windows loses its type GUIDs / PARTUUIDs → boot entries break).
- toram needs ≥10 GiB RAM (preflight-blocked below that): below it casper
  silently skips toram and the disk can never be released.
