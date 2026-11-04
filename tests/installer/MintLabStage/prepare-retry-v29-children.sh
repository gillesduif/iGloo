#!/usr/bin/env bash
# Keep the failed v28 Mint target and seed immutable while retrying first boot.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target_base="$lab/mint-target-fresh-v28.raw"
seed_base="$lab/mint-oemdrv-fresh-v28.qcow2"
target="$lab/mint-target-retry-v29.qcow2"
seed="$lab/mint-oemdrv-retry-v29.qcow2"
vars_base="$lab/OVMF_VARS-fresh-v28.fd"
vars="$lab/OVMF_VARS-retry-v29.fd"

for path in "$target_base" "$seed_base" "$vars_base"; do
    test -f "$path" && test ! -L "$path" && test "$(stat -c %h "$path")" = 1
done
for path in "$target" "$seed" "$vars"; do test ! -e "$path"; done
test ! -e /sys/block/nbd0/pid
test -z "$(pgrep -f '[q]emu-system-x86_64' || true)"
test -z "$(losetup -j "$target_base")"
test "$(stat -c %s "$target_base")" -eq 42949672960
test "$(sgdisk -p "$target_base" | sed -n 's/^Disk identifier (GUID): //p')" = 7862B40B-F6BD-4012-BEC8-E5C2FAA509E2
test "$(df -B1 --output=avail /mnt/c | tail -1 | tr -d ' ')" -gt 34359738368
qemu-img check "$seed_base"
python3 - "$seed_base" "$lab/mint-oemdrv-final-v20.raw" <<'PY'
import json
import subprocess
import sys

info = json.loads(subprocess.check_output(
    ['qemu-img', 'info', '--output=json', sys.argv[1]], text=True))
assert info['format'] == 'qcow2' and info['backing-filename'] == sys.argv[2]
assert info['backing-filename-format'] == 'raw'
assert info['virtual-size'] == 4823449600
PY

qemu-img create -f qcow2 -F raw -b "$target_base" "$target"
qemu-img create -f qcow2 -F qcow2 -b "$seed_base" "$seed"
cp -- "$vars_base" "$vars"
python3 - "$target" "$target_base" "$seed" "$seed_base" <<'PY'
import json
import subprocess
import sys

for child, parent, format, size in (
        (sys.argv[1], sys.argv[2], 'raw', 42949672960),
        (sys.argv[3], sys.argv[4], 'qcow2', 4823449600)):
    info = json.loads(subprocess.check_output(
        ['qemu-img', 'info', '--output=json', child], text=True))
    assert info['format'] == 'qcow2' and info['backing-filename'] == parent
    assert info['backing-filename-format'] == format and info['virtual-size'] == size
PY
du -B1 "$target" "$seed" "$vars"
