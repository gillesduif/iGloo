#!/usr/bin/env bash
set -euo pipefail

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v23.raw"
decoy="$lab/mint-decoy-fresh-v23.raw"
seed="$lab/mint-oemdrv-fresh-v23.qcow2"
root="$lab/mint-root-fresh-v23-ro"
esp="$lab/mint-esp-fresh-v23-ro"
expected="$lab/mint-fresh-v23-protected-prehashes.txt"
output="$lab/mint-fresh-v23-postinstall-readback.txt"

test -f "$target" && test -f "$decoy" && test -f "$seed" && test -f "$expected"
test ! -e /sys/block/nbd0/pid
mkdir -p "$root" "$esp"
test ! "$(findmnt -n "$root")" && test ! "$(findmnt -n "$esp")"
loop=$(losetup --read-only --find --show --partscan "$target")
cleanup() {
    if mountpoint -q "$esp"; then umount "$esp"; fi
    if mountpoint -q "$root"; then umount "$root"; fi
    losetup -d "$loop"
}
trap cleanup EXIT
test "$(losetup -n -O BACK-FILE "$loop")" = "$target"
udevadm settle
for attempt in $(seq 1 20); do
    test -b "${loop}p4" && break
    sleep 1
done
test -b "${loop}p1" && test -b "${loop}p2" && test -b "${loop}p3" && test -b "${loop}p4"
test "$(lsblk -dnro PTUUID "$loop" | tr '[:lower:]' '[:upper:]')" = 4331A9F3-AF31-4ED8-9BFF-07C5400A42A4
test "$(lsblk -dnro PARTUUID "${loop}p3" | tr '[:lower:]' '[:upper:]')" = 052EAAAB-AE24-495D-8A79-8F567A1969D9

p1_hash=$(sha256sum "${loop}p1" | cut -d ' ' -f 1)
p2_hash=$(sha256sum "${loop}p2" | cut -d ' ' -f 1)
test "$p1_hash" = "$(awk 'NR == 1 { print $1 }' "$expected")"
test "$p2_hash" = "$(awk 'NR == 2 { print $1 }' "$expected")"
mount -t ext4 -o ro,noload "${loop}p4" "$root"
mount -t vfat -o ro "${loop}p3" "$esp"
test -f "$root/etc/fstab"
test -d "$esp/EFI"
{
    printf 'SELECTED TARGET GPT\n'; sgdisk -p "$target"
    printf 'SAME-SIZE DECOY GPT\n'; sgdisk -p "$decoy"
    printf 'PROTECTED FULL PARTITION HASHES\n'; printf 'p1 %s\np2 %s\n' "$p1_hash" "$p2_hash"
    printf 'LINUX ESP\n'; blkid "${loop}p3"; find "$esp/EFI" -maxdepth 3 -type f -printf '%P\n' | sort
    printf 'INSTALLED FSTAB\n'; cat "$root/etc/fstab"
    printf 'BOOTSTRAP DEPLOYMENT\n'; stat -c '%n %a %U:%G' "$root/opt/igloo" "$root/opt/igloo/igloo-bootstrap.sh" "$root/etc/systemd/system/igloo-bootstrap.service" "$root/var/lib/igloo"
    readlink "$root/etc/systemd/system/multi-user.target.wants/igloo-bootstrap.service"
    printf 'ESP READ-ONLY FSCK\n'; fsck.fat -n "${loop}p3"
    printf 'ALLOCATED BYTES\n'; du -B1 "$target" "$decoy" "$seed"
} > "$output"
cat "$output"
umount "$esp"
umount "$root"
losetup -d "$loop"
trap - EXIT
qemu-img check "$seed"
