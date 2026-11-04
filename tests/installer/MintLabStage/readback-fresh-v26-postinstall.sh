#!/usr/bin/env bash
# Read-only native v26 target/ESP readback. The selected raw and seed are disposable.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v25.raw"
decoy="$lab/mint-decoy-fresh-v25.raw"
seed="$lab/mint-oemdrv-fresh-v26.qcow2"
root="$lab/mint-root-fresh-v26-ro"
esp="$lab/mint-esp-fresh-v26-ro"
output="$lab/mint-fresh-v26-postinstall-readback.txt"

for path in "$target" "$decoy" "$seed"; do
    test -f "$path" && test ! -L "$path"
    test "$(stat -c %h "$path")" = 1
done
test ! -e /sys/block/nbd0/pid
test -z "$(pgrep -f '[q]emu-system-x86_64.*mint-target-fresh-v25.raw' || true)"
mkdir -p "$root" "$esp"
test -z "$(findmnt -n "$root")"
test -z "$(findmnt -n "$esp")"
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
for part in 1 2 3 4; do test -b "${loop}p${part}"; done
test "$(lsblk -dnro PTUUID "$loop" | tr '[:lower:]' '[:upper:]')" = 47A16C85-8F90-47A7-85EA-6D4D38F020AE
test "$(lsblk -dnro PARTUUID "${loop}p3" | tr '[:lower:]' '[:upper:]')" = 145D845C-1968-40B6-967C-53ECAA713AB7

p1_hash=$(sha256sum "${loop}p1" | cut -d ' ' -f 1)
p2_hash=$(sha256sum "${loop}p2" | cut -d ' ' -f 1)
test "$p1_hash" = 9eb238e30cc0d2b30da0e3e2f4b155d30f474768d00465eecf8ba7d39258af98
test "$p2_hash" = 6210d3fb5081c5e597d1ec85664dacef4b0103db0ec69117608aec56fc5f02fe

mount -t ext4 -o ro,noload "${loop}p4" "$root"
mount -t vfat -o ro "${loop}p3" "$esp"
test -f "$root/etc/fstab"
test -d "$esp/EFI"
{
    printf 'SELECTED TARGET GPT\n'; sgdisk -p "$target"; sgdisk -v "$target"
    printf 'SAME-SIZE DECOY GPT\n'; sgdisk -p "$decoy"; sgdisk -v "$decoy"
    printf 'PROTECTED FULL PARTITION HASHES\n'; printf 'p1 %s\np2 %s\n' "$p1_hash" "$p2_hash"
    printf 'LINUX ESP\n'; blkid "${loop}p3"; find "$esp/EFI" -maxdepth 3 -type f -printf '%P\n' | sort
    printf 'INSTALLED FSTAB\n'; cat "$root/etc/fstab"
    printf 'BOOTSTRAP DEPLOYMENT\n'; stat -c '%n %a %U:%G' "$root/opt/igloo" "$root/opt/igloo/igloo-bootstrap.sh" "$root/etc/systemd/system/igloo-bootstrap.service" "$root/var/lib/igloo"
    readlink "$root/etc/systemd/system/multi-user.target.wants/igloo-bootstrap.service"
    printf 'ESP READ-ONLY FSCK\n'; fsck.fat -n "${loop}p3"
    printf 'ALLOCATED BYTES\n'; du -B1 "$target" "$decoy" "$seed"
} > "$output"
cat "$output"
