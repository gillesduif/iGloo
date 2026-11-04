#!/usr/bin/env bash
set -euo pipefail

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v23.raw"
decoy="$lab/mint-decoy-fresh-v23.raw"
seed="$lab/mint-oemdrv-fresh-v23.qcow2"
root="$lab/mint-root-fresh-v23-ro"
seedmnt="$lab/mint-seed-fresh-v23-ro"
output="$lab/mint-fresh-v23-firstboot-readback.txt"
test -f "$target" && test -f "$decoy" && test -f "$seed"
test ! -e /sys/block/nbd0/pid
mkdir -p "$root" "$seedmnt"
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
test -b "${loop}p1" && test -b "${loop}p2" && test -b "${loop}p4"
test -b /dev/nbd0p1
mount -t ext4 -o ro,noload "${loop}p4" "$root"
mount -t vfat -o ro /dev/nbd0p1 "$seedmnt"
{
    python3 "$lab/check-fresh-v23-firstboot.py" "$root" "$seedmnt" "$lab/mint-fresh-v23-manifest.input.json"
    printf 'PROTECTED FULL HASHES\n'
    sha256sum "${loop}p1" "${loop}p2"
    printf 'SELECTED AND DECOY GPT\n'
    sgdisk -p "$target"
    sgdisk -p "$decoy"
    printf 'NETWORKMANAGER AND CODEC LOG LINES\n'
    grep -E 'step (wifi|codecs|suggested-pkgs): OK|Running: nmcli connection reload|Wrote NetworkManager profile|Multimedia codecs installed' "$root/var/log/igloo/agent.log"
    printf 'SEED READ-ONLY FSCK\n'
    fsck.fat -n /dev/nbd0p1
    printf 'ALLOCATED BYTES\n'
    du -B1 "$target" "$decoy" "$seed"
} > "$output"
cat "$output"
umount "$seedmnt"
umount "$root"
qemu-nbd --disconnect /dev/nbd0
connected=0
losetup -d "$loop"
trap - EXIT
qemu-img check "$seed"
