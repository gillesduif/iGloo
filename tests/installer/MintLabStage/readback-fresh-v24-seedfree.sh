#!/usr/bin/env bash
# Read-only final Mint v24 check after installed boot without OEMDRV or decoy.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v24.raw"
decoy="$lab/mint-decoy-fresh-v24.raw"
root="$lab/mint-root-fresh-v24-ro"
protected="$lab/mint-fresh-v24-protected-prehashes.txt"
decoy_pre="$lab/mint-fresh-v24-decoy-prehash.txt"
output="$lab/mint-fresh-v24-seedfree-readback.txt"
for path in "$target" "$decoy" "$protected" "$decoy_pre"; do
    test -f "$path" && test ! -L "$path"
done
test ! -e "$output" && test ! -L "$output"
test ! -e /sys/block/nbd0/pid
test -z "$(pgrep -f '[q]emu-system-x86_64.*mint-target-fresh-v24.raw' || true)"
mkdir -p "$root"
test -z "$(findmnt -n "$root")"

loop=$(losetup --read-only --find --show --partscan "$target")
cleanup() {
    if mountpoint -q "$root"; then umount "$root"; fi
    losetup -d "$loop"
}
trap cleanup EXIT
test "$(losetup -n -O BACK-FILE "$loop")" = "$target"
udevadm settle
test -b "${loop}p1" && test -b "${loop}p2" && test -b "${loop}p3" && test -b "${loop}p4"
test "$(lsblk -dnro PTUUID "$loop" | tr '[:lower:]' '[:upper:]')" = 3E264084-C958-41CB-AF82-3D247E3FC75D
test "$(lsblk -dnro PARTUUID "${loop}p3" | tr '[:lower:]' '[:upper:]')" = 4534D391-723F-42D5-8E2E-CBD523E4416F
mount -t ext4 -o ro,noload "${loop}p4" "$root"

{
    python3 "$lab/check-fresh-v24-deltas.py" "$root" \
        "$lab/mint-fresh-v24-manifest.input.json" --after-login
    printf 'PROTECTED FULL PARTITION HASHES\n'
    p1_hash=$(sha256sum "${loop}p1" | cut -d ' ' -f 1)
    p2_hash=$(sha256sum "${loop}p2" | cut -d ' ' -f 1)
    test "$p1_hash" = "$(awk 'NR == 1 { print $1 }' "$protected")"
    test "$p2_hash" = "$(awk 'NR == 2 { print $1 }' "$protected")"
    printf 'p1 %s\np2 %s\n' "$p1_hash" "$p2_hash"
    printf 'SAME-SIZE DECOY FULL HASH\n'
    decoy_hash=$(sha256sum "$decoy" | cut -d ' ' -f 1)
    test "$decoy_hash" = "$(awk 'NR == 1 { print $1 }' "$decoy_pre")"
    printf 'decoy %s\n' "$decoy_hash"
    printf 'SELECTED AND DECOY GPT\n'
    sgdisk -p "$target"
    sgdisk -p "$decoy"
    printf 'ALLOCATED BYTES\n'
    du -B1 "$target" "$decoy" "$lab/mint-oemdrv-fresh-v24.qcow2"
} > "$output"

cat "$output"
umount "$root"
losetup -d "$loop"
trap - EXIT
