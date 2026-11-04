#!/usr/bin/env bash
set -euo pipefail

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v23.raw"
decoy="$lab/mint-decoy-fresh-v23.raw"
root="$lab/mint-root-fresh-v23-ro"
expected="$lab/mint-fresh-v23-protected-prehashes.txt"
output="$lab/mint-fresh-v23-seedfree-readback.txt"
test -f "$target" && test -f "$decoy" && test -f "$expected"
test ! -e /sys/block/nbd0/pid
mkdir -p "$root"
test ! "$(findmnt -n "$root")"
loop=$(losetup --read-only --find --show --partscan "$target")
cleanup() {
    if mountpoint -q "$root"; then umount "$root"; fi
    losetup -d "$loop"
}
trap cleanup EXIT
test "$(losetup -n -O BACK-FILE "$loop")" = "$target"
udevadm settle
test -b "${loop}p1" && test -b "${loop}p2" && test -b "${loop}p4"
mount -t ext4 -o ro,noload "${loop}p4" "$root"
{
    python3 "$lab/check-fresh-v23-seedfree.py" "$root" \
        "$lab/mint-fresh-v23-manifest.input.json" \
        "$lab/mint-provider-stage-fresh-v23-codecs/igloo-agent/agent.py"
    printf 'PROTECTED FULL HASHES\n'
    p1_hash=$(sha256sum "${loop}p1" | cut -d ' ' -f 1)
    p2_hash=$(sha256sum "${loop}p2" | cut -d ' ' -f 1)
    test "$p1_hash" = "$(awk 'NR == 1 { print $1 }' "$expected")"
    test "$p2_hash" = "$(awk 'NR == 2 { print $1 }' "$expected")"
    printf 'p1 %s\np2 %s\n' "$p1_hash" "$p2_hash"
    printf 'SELECTED AND DECOY GPT\n'
    sgdisk -p "$target"
    sgdisk -p "$decoy"
    printf 'ALLOCATED BYTES\n'
    du -B1 "$target" "$decoy"
} > "$output"
cat "$output"
umount "$root"
losetup -d "$loop"
trap - EXIT
