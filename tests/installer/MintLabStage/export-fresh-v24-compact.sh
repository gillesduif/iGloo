#!/usr/bin/env bash
# Preserve small native guest and scrubbed-seed evidence before exact media retirement.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v24.raw"
seed="$lab/mint-oemdrv-fresh-v24.qcow2"
root="$lab/mint-root-fresh-v24-ro"
seedmnt="$lab/mint-seed-fresh-v24-ro"
export_dir="$lab/mint-fresh-v24-compact"
test -f "$target" && test ! -L "$target"
test -f "$seed" && test ! -L "$seed"
test ! -e "$export_dir" && test ! -L "$export_dir"
modprobe nbd max_part=16
test ! -e /sys/block/nbd0/pid
test -z "$(pgrep -f '[q]emu-system-x86_64.*mint-target-fresh-v24.raw' || true)"
mkdir -p "$root" "$seedmnt" "$export_dir"
chmod 0700 "$export_dir"
test -z "$(findmnt -n "$root")" && test -z "$(findmnt -n "$seedmnt")"
loop=$(losetup --read-only --find --show --partscan "$target")
connected=0
cleanup() {
    if mountpoint -q "$seedmnt"; then umount "$seedmnt"; fi
    if mountpoint -q "$root"; then umount "$root"; fi
    if test "$connected" -eq 1; then qemu-nbd --disconnect /dev/nbd0; fi
    losetup -d "$loop"
}
trap cleanup EXIT
test "$(losetup -n -O BACK-FILE "$loop")" = "$target"
udevadm settle
test "$(lsblk -dnro PTUUID "$loop" | tr '[:lower:]' '[:upper:]')" = 3E264084-C958-41CB-AF82-3D247E3FC75D
qemu-nbd --read-only --connect=/dev/nbd0 --format=qcow2 "$seed"
connected=1
udevadm settle
test -b "${loop}p4" && test -b /dev/nbd0p1
test "$(lsblk -dnro PTUUID /dev/nbd0 | tr '[:lower:]' '[:upper:]')" = 65DE9CA9-A4B6-4A0F-B027-E15DFCD9E570
mount -t ext4 -o ro,noload "${loop}p4" "$root"
mount -t vfat -o ro /dev/nbd0p1 "$seedmnt"

install -m 0600 "$root/var/log/igloo/agent.log" "$export_dir/agent.log"
install -m 0600 "$root/home/mintlab/.local/state/igloo-credentials.log" "$export_dir/credential-login.log"
install -m 0600 "$root/etc/fstab" "$export_dir/fstab"
install -m 0600 "$root/var/lib/igloo/manifest.json" "$export_dir/installed-manifest-redacted.json"
install -m 0600 "$root/etc/systemd/system/igloo-bootstrap.service" "$export_dir/bootstrap.service"
install -m 0600 "$root/etc/xdg/autostart/igloo-welcome.desktop" "$export_dir/welcome.desktop"
install -m 0600 "$seedmnt/migration-manifest.json" "$export_dir/seed-manifest-redacted.json"
install -m 0600 "$seedmnt/preseed.cfg" "$export_dir/seed-preseed-redacted.cfg"
install -m 0600 "$seedmnt/igloo-seed-identity.json" "$export_dir/seed-identity.json"
sha256sum "$seedmnt/igloo-boot/initrd" > "$export_dir/seed-initrd-redacted-sha256.txt"
if test -f "$root/var/log/syslog"; then
    grep -E 'igloo|FAT-fs|Powering Off|NetworkManager' "$root/var/log/syslog" |
        tail -n 200 > "$export_dir/guest-syslog-filtered.txt" || true
fi
find "$export_dir" -maxdepth 1 -type f -printf '%f %s bytes\n' | sort
umount "$seedmnt"
umount "$root"
qemu-nbd --disconnect /dev/nbd0
connected=0
losetup -d "$loop"
trap - EXIT
