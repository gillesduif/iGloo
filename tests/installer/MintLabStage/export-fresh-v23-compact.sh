#!/usr/bin/env bash
set -euo pipefail

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v23.raw"
seed="$lab/mint-oemdrv-fresh-v23.qcow2"
root="$lab/mint-root-fresh-v23-ro"
seedmnt="$lab/mint-seed-fresh-v23-ro"
export_dir="$lab/mint-fresh-v23-compact"
test -f "$target" && test -f "$seed" && test ! -e "$export_dir"
test ! -e /sys/block/nbd0/pid
mkdir -p "$root" "$seedmnt" "$export_dir"
chmod 0700 "$export_dir"
test ! "$(findmnt -n "$root")" && test ! "$(findmnt -n "$seedmnt")"
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
qemu-nbd --read-only --connect=/dev/nbd0 --format=qcow2 "$seed"
connected=1
udevadm settle
test -b "${loop}p4" && test -b /dev/nbd0p1
mount -t ext4 -o ro,noload "${loop}p4" "$root"
mount -t vfat -o ro /dev/nbd0p1 "$seedmnt"

install -m 0600 "$root/var/log/igloo/agent.log" "$export_dir/agent.log"
install -m 0600 "$root/home/mintlab/.local/state/igloo-credentials.log" "$export_dir/credential-login.log"
install -m 0600 "$root/etc/fstab" "$export_dir/fstab"
install -m 0600 "$root/var/lib/igloo/manifest.json" "$export_dir/installed-manifest-redacted.json"
install -m 0600 "$root/etc/systemd/system/igloo-bootstrap.service" "$export_dir/bootstrap.service"
install -m 0600 "$seedmnt/migration-manifest.json" "$export_dir/seed-manifest-redacted.json"
install -m 0600 "$seedmnt/preseed.cfg" "$export_dir/seed-preseed-redacted.cfg"
install -m 0600 "$seedmnt/igloo-seed-identity.json" "$export_dir/seed-identity.json"
sha256sum "$seedmnt/igloo-boot/initrd" > "$export_dir/seed-initrd-redacted-sha256.txt"
if test -f "$root/var/log/syslog"; then
    grep -E 'igloo|FAT-fs|Powering Off|NetworkManager' "$root/var/log/syslog" |
        tail -n 200 > "$export_dir/guest-syslog-filtered.txt" || true
fi
chown -R gillesduif:gillesduif "$export_dir"
find "$export_dir" -maxdepth 1 -type f -printf '%f %s bytes\n' | sort
umount "$seedmnt"
umount "$root"
qemu-nbd --disconnect /dev/nbd0
connected=0
losetup -d "$loop"
trap - EXIT
