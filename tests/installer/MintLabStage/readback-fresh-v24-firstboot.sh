#!/usr/bin/env bash
# Read-only, exact-target verification after the disposable Mint v24 first boot.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v24.raw"
decoy="$lab/mint-decoy-fresh-v24.raw"
seed="$lab/mint-oemdrv-fresh-v24.qcow2"
root="$lab/mint-root-fresh-v24-ro"
seedmnt="$lab/mint-seed-fresh-v24-ro"
output="$lab/mint-fresh-v24-firstboot-readback.txt"
expected="$lab/mint-fresh-v24-protected-prehashes.txt"
manifest="$lab/mint-fresh-v24-manifest.input.json"

for path in "$target" "$decoy" "$seed" "$expected" "$manifest"; do
    test -f "$path" && test ! -L "$path"
done
test ! -e "$output" && test ! -L "$output"
test ! -e /sys/block/nbd0/pid
test -z "$(pgrep -f '[q]emu-system-x86_64.*mint-target-fresh-v24.raw' || true)"
mkdir -p "$root" "$seedmnt"
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
qemu-nbd --read-only --connect=/dev/nbd0 --format=qcow2 "$seed"
connected=1
udevadm settle
test -b "${loop}p1" && test -b "${loop}p2" && test -b "${loop}p3" && test -b "${loop}p4"
test -b /dev/nbd0p1
test "$(lsblk -dnro PTUUID "$loop" | tr '[:lower:]' '[:upper:]')" = 3E264084-C958-41CB-AF82-3D247E3FC75D
test "$(lsblk -dnro PARTUUID "${loop}p3" | tr '[:lower:]' '[:upper:]')" = 4534D391-723F-42D5-8E2E-CBD523E4416F
test "$(lsblk -dnro PTUUID /dev/nbd0 | tr '[:lower:]' '[:upper:]')" = 65DE9CA9-A4B6-4A0F-B027-E15DFCD9E570
test "$(lsblk -dnro PARTUUID /dev/nbd0p1 | tr '[:lower:]' '[:upper:]')" = D80F6663-7280-4D6D-8B18-CAD62E3F0C94

p1_hash=$(sha256sum "${loop}p1" | cut -d ' ' -f 1)
p2_hash=$(sha256sum "${loop}p2" | cut -d ' ' -f 1)
test "$p1_hash" = "$(awk 'NR == 1 { print $1 }' "$expected")"
test "$p2_hash" = "$(awk 'NR == 2 { print $1 }' "$expected")"
mount -t ext4 -o ro,noload "${loop}p4" "$root"
mount -t vfat -o ro /dev/nbd0p1 "$seedmnt"

{
    python3 "$lab/check-fresh-v24-deltas.py" "$root" "$manifest" --seed "$seedmnt" --after-login
    printf 'SELECTED TARGET GPT\n'; sgdisk -p "$target"
    printf 'SAME-SIZE DECOY GPT\n'; sgdisk -p "$decoy"
    printf 'PROTECTED FULL PARTITION HASHES\n'; printf 'p1 %s\np2 %s\n' "$p1_hash" "$p2_hash"
    printf 'FSTAB\n'; cat "$root/etc/fstab"
    printf 'BOOTSTRAP/JOURNAL ORDER\n'
    if test -d "$root/var/log/journal"; then
        journalctl --directory="$root/var/log/journal" -b -u igloo-bootstrap.service -u lightdm.service -o short-precise --no-pager | tail -n 45
    fi
    printf 'ALLOCATED BYTES\n'; du -B1 "$target" "$decoy" "$seed"
} > "$output"

cat "$output"
umount "$seedmnt"
umount "$root"
qemu-nbd --disconnect /dev/nbd0
connected=0
losetup -d "$loop"
trap - EXIT
qemu-img check "$seed"
