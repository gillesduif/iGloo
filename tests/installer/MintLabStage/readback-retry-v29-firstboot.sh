#!/usr/bin/env bash
# Read-only readback of the bounded Mint first-boot retry child, its bound seed,
# and the protected partitions of the selected exact target.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-retry-v29.qcow2"
target_base="$lab/mint-target-fresh-v28.raw"
seed="$lab/mint-oemdrv-retry-v29.qcow2"
seed_base="$lab/mint-oemdrv-fresh-v28.qcow2"
decoy="$lab/mint-decoy-fresh-v28.raw"
expected="$lab/mint-fresh-v28-protected-prehashes.txt"
manifest="$lab/mint-fresh-v28-manifest.input.json"
root="$lab/mint-root-retry-v29-ro"
esp="$lab/mint-esp-retry-v29-ro"
seedmnt="$lab/mint-seed-retry-v29-ro"
output="$lab/mint-retry-v29-firstboot-readback-final.txt"

for file in "$target" "$target_base" "$seed" "$seed_base" "$decoy" "$expected" "$manifest"; do
    test -f "$file" && test ! -L "$file" && test "$(stat -c %h "$file")" = 1
done
test ! -e "$output" && test ! -L "$output"
test ! -e /sys/block/nbd0/pid && test ! -e /sys/block/nbd1/pid
test -z "$(pgrep -f '[q]emu-system-x86_64.*mint-target-retry-v29.qcow2' || true)"
test "$(df -B1 --output=avail /mnt/c | tail -1 | tr -d ' ')" -gt 21474836480

python3 - "$target" "$target_base" "$seed" "$seed_base" <<'PY'
import json
import subprocess
import sys
for child, backing, backing_format, size in (
        (sys.argv[1], sys.argv[2], 'raw', 42949672960),
        (sys.argv[3], sys.argv[4], 'qcow2', 4823449600)):
    info = json.loads(subprocess.check_output(
        ['qemu-img', 'info', '--output=json', child], text=True))
    assert info['format'] == 'qcow2'
    assert info['backing-filename'] == backing
    assert info['backing-filename-format'] == backing_format
    assert info['virtual-size'] == size
PY

mkdir -p "$root" "$esp" "$seedmnt"
for mount in "$root" "$esp" "$seedmnt"; do
    test -z "$(findmnt -rn --mountpoint "$mount")"
done
target_connected=0
seed_connected=0
cleanup() {
    if mountpoint -q "$seedmnt"; then umount "$seedmnt"; fi
    if mountpoint -q "$esp"; then umount "$esp"; fi
    if mountpoint -q "$root"; then umount "$root"; fi
    if test "$seed_connected" -eq 1; then qemu-nbd --disconnect /dev/nbd1; fi
    if test "$target_connected" -eq 1; then qemu-nbd --disconnect /dev/nbd0; fi
}
trap cleanup EXIT
qemu-nbd --read-only --connect=/dev/nbd0 --format=qcow2 "$target"
target_connected=1
qemu-nbd --read-only --connect=/dev/nbd1 --format=qcow2 "$seed"
seed_connected=1
udevadm settle
for part in 1 2 3 4; do test -b "/dev/nbd0p${part}"; done
test -b /dev/nbd1p1
test "$(lsblk -dnro PTUUID /dev/nbd0 | tr '[:lower:]' '[:upper:]')" = 7862B40B-F6BD-4012-BEC8-E5C2FAA509E2
test "$(lsblk -dnro PARTUUID /dev/nbd0p3 | tr '[:lower:]' '[:upper:]')" = 99B3F3BE-7150-4183-8453-596BAD5E3EA7
test "$(lsblk -dnro PTUUID /dev/nbd1 | tr '[:lower:]' '[:upper:]')" = 65DE9CA9-A4B6-4A0F-B027-E15DFCD9E570
test "$(lsblk -dnro PARTUUID /dev/nbd1p1 | tr '[:lower:]' '[:upper:]')" = D80F6663-7280-4D6D-8B18-CAD62E3F0C94

p1_hash=$(sha256sum /dev/nbd0p1 | cut -d ' ' -f 1)
p2_hash=$(sha256sum /dev/nbd0p2 | cut -d ' ' -f 1)
test "$p1_hash" = "$(awk 'NR == 1 { print $1 }' "$expected")"
test "$p2_hash" = "$(awk 'NR == 2 { print $1 }' "$expected")"
mount -t ext4 -o ro,noload /dev/nbd0p4 "$root"
mount -t vfat -o ro /dev/nbd0p3 "$esp"
mount -t vfat -o ro /dev/nbd1p1 "$seedmnt"
test -f "$esp/EFI/ubuntu/shimx64.efi" && test -f "$esp/EFI/ubuntu/grubx64.efi"

{
    python3 "$lab/check-retry-v29-deltas.py" "$root" "$manifest" --seed "$seedmnt"
    printf 'SELECTED TARGET GPT\n'; sgdisk -p /dev/nbd0; sgdisk -v /dev/nbd0
    printf 'SAME-SIZE DECOY GPT\n'; sgdisk -p "$decoy"; sgdisk -v "$decoy"
    printf 'PROTECTED FULL PARTITION HASHES\n'; printf 'p1 %s\np2 %s\n' "$p1_hash" "$p2_hash"
    printf 'LINUX ESP\n'; find "$esp/EFI" -maxdepth 3 -type f -printf '%P\n' | sort
    printf 'INSTALLED FSTAB\n'; cat "$root/etc/fstab"
    printf 'ALLOCATED BYTES\n'; du -B1 "$target" "$target_base" "$decoy" "$seed" "$seed_base"
} > "$output"
cat "$output"

cleanup
target_connected=0
seed_connected=0
trap - EXIT
qemu-img check "$target"
qemu-img check "$seed"
