# The GRUB menu on a multi-boot disk

What `configure_boot_menu()` in `distros/_shared/agent/igloo_boot.py` changes about
the generated menu, and which bare-metal failure each change answers. Every finding
below was measured on desktop-living, not read in documentation.

## One visit to Windows made Windows the permanent default

Fedora's stock `/etc/default/grub` carries `GRUB_DEFAULT=saved`. Pairing that with
`GRUB_SAVEDEFAULT=true` — which iGloo did — writes whatever entry was last picked
into `grubenv` and boots it next time. Choosing Windows once was enough: from then
on the machine went straight to Windows and looked like Linux had never been
installed.

The drop-in now sets `GRUB_DEFAULT=0`. `10_linux` runs before `30_os-prober`, so
entry 0 is always this distro's own. `configure_boot_menu()` also runs
`grub-editenv - unset saved_entry`, because a machine installed by an earlier
build still carries the old value.

## Fedora hung forever on a partition that did not exist

After the Mint install of 2026-08-23, Fedora stopped booting:

```
[*] (1 of 2) Job dev-nvme1n1p6.device/start running (7min 25s / no limit)
```

Four rounds of forensics inside Fedora found nothing, because everything there is
correct: the ESP stub, `grub.cfg`, the BLS entries, `fstab` and the initramfs all
name the root filesystem by UUID. Fedora was not being booted by its own
bootloader. The menu on screen was Mint's, and its Fedora entry read:

```
linux /vmlinuz-6.19.10-300.fc44.x86_64 root=/dev/nvme1n1p6
```

Three things are wrong with that line, and all three come from the same cause.
`linux-boot-prober` reads a foreign distro's boot configuration to copy its kernel
command line. A BLS Fedora keeps that command line in `/boot/loader/entries/*.conf`,
not in `grub.cfg`, so there was nothing to read and os-prober fell back to naming
the partition it had just mounted. The entry therefore lost `ro`, lost
`rd.driver.blacklist=nouveau,nova_core` — nouveau loaded on the hung boot, which is
how the loss shows — and gained a kernel name in place of a UUID.

Kernel names are assigned in probe order. By the time the machine rebooted, the two
NVMe controllers had enumerated the other way round: Fedora's root was
`/dev/nvme0n1p6`, and `/dev/nvme1n1p6` did not exist on the machine at all — that
disk is a Gigabyte with exactly two NTFS partitions. systemd turns `root=` into a
device unit and waits for it with no timeout, so the boot never ended.

The Debian entry on the same menu carried `root=UUID=…` and kept booting throughout.
That is the differential that identifies the cause.

Two changes, on different sides of the problem:

- **`ks.cfg.template` sets `GRUB_ENABLE_BLSCFG=false`.** Fedora's own `grub.cfg`
  then contains classic `menuentry` blocks, so a sibling's os-prober can read the
  full command line and copies it verbatim, blacklist and all.
- **The fixup hook rewrites `root=/dev/…` to `root=UUID=…`** in the generated
  `grub.cfg`, whatever produced it. This covers the ordering the first change
  cannot: an OS installed before iGloo, or one iGloo does not manage. The rewrite
  runs before the single-disk guard below, because a second disk is exactly the
  condition that makes a kernel name dangerous.

The rewrite can only resolve a device that still exists when `grub-mkconfig` runs.
That is the normal case — os-prober had the partition mounted moments earlier. A
path that no longer resolves is left alone rather than guessed at, and
`_verify()` logs `VERIFICATION FAILED` for every one that remains.

## The menu scanned every device on every boot

`grub-probe` produces no `--hint=` on NVMe, so each `search --fs-uuid` line searched
all disks. The same hook derives one hint per line from the UUID on that line; a
shared hint would be wrong for the os-prober entries of every other OS. The UUID
stays the source of truth, so a stale hint only costs the old scan. This half is
skipped when more than one disk is present, since `hd0` is then ambiguous.

## Where the hook lives

`grub.cfg` is regenerated whenever a kernel is installed, which undoes both fixups,
so they are installed as a kernel hook rather than applied once:

| Family | Path |
|---|---|
| Debian, Mint | `/etc/kernel/postinst.d/zzz-igloo-grub-fixups` |
| Fedora | `/etc/kernel/install.d/99-igloo-grub-fixups.install` |

A `grub` package upgrade also re-runs `grub-mkconfig`, and nothing in `/etc/kernel`
fires for that. `igloo-grub-fixups.service` therefore runs the same script on every
boot as well: a hand-run `update-grub` costs one failed boot attempt at worst
instead of leaving the machine unbootable.

## The UEFI boot order

`put_self_first_in_boot_order()` moves the entry named by `BootCurrent` to the front
— by definition the one this agent is running from, so no name matching is needed.
Windows Boot Manager takes the front slot back on updates and on some ordinary
boots, and the first-boot agent is gated on `/var/lib/igloo/.done`, so it never ran
again. `igloo-boot-order.service` re-asserts the order on every boot instead.

## Menu labels

`_patch_os_prober_labels()` renames `Windows Boot Manager` to `Windows 11`, drops
the `(on /dev/…)` suffix, and gives sibling distros their real GRUB class so the
menu shows the right logo. `_patch_linux_submenu()` cuts `10_linux` short after the
newest kernel's entry, so there is one entry per OS and no *Advanced options*
submenu. Both are marker-guarded and both can be reverted by a `grub` package
upgrade; every failure here is cosmetic and leaves the system bootable.

## Tests

`tests/agent/boot_order_test.py` covers the default entry and the boot-order unit.
`tests/agent/grub_root_uuid_test.py` runs the real hook script over the verbatim
lines from Mint's `grub.cfg`, including the case where the device no longer resolves.
