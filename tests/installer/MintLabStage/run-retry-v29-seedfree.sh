#!/usr/bin/env bash
# Reboot the exact Mint retry target without OEMDRV to test durable first boot.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-retry-v29.qcow2"
target_base="$lab/mint-target-fresh-v28.raw"
decoy="$lab/mint-decoy-fresh-v28.raw"
vars="$lab/OVMF_VARS-retry-v29.fd"
readback="$lab/mint-retry-v29-firstboot-readback-final.txt"
for file in "$target" "$target_base" "$decoy" "$vars" "$readback"; do
    test -f "$file" && test ! -L "$file" && test "$(stat -c %h "$file")" = 1
done
grep -q '^Mint v29: exact firstboot migration' "$readback"
test ! -e /sys/block/nbd0/pid && test ! -e /sys/block/nbd1/pid
test -z "$(losetup -j "$target_base")"
test "$(sgdisk -p "$target_base" | sed -n 's/^Disk identifier (GUID): //p')" = 7862B40B-F6BD-4012-BEC8-E5C2FAA509E2

python3 - "$target" "$target_base" <<'PY'
import json
import subprocess
import sys
info = json.loads(subprocess.check_output(
    ['qemu-img', 'info', '--output=json', sys.argv[1]], text=True))
assert info['format'] == 'qcow2'
assert info['backing-filename'] == sys.argv[2]
assert info['backing-filename-format'] == 'raw'
assert info['virtual-size'] == 42949672960
PY

exec qemu-system-x86_64 \
    -machine q35,accel=kvm -cpu host -smp 4 -m 7168 \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,file="$vars" \
    -drive if=none,id=target,format=qcow2,file="$target" \
    -device virtio-blk-pci,drive=target,bootindex=1 \
    -drive if=none,id=decoy,format=raw,readonly=on,file="$decoy" \
    -device virtio-blk-pci,drive=decoy,bootindex=2 \
    -netdev user,id=n0 -device virtio-net-pci,netdev=n0 \
    -vga std -display vnc=127.0.0.1:0 \
    -monitor unix:"$lab/mint-monitor-retry-v29-seedfree.sock",server,nowait \
    -serial file:"$lab/mint-retry-v29-seedfree-serial.log" -no-reboot
