#!/bin/bash
# Boot the retained v26 OEMDRV through a temporary QEMU snapshot at 4 GiB.
# No selected target or decoy disk is attached to this compatibility probe.
set -euo pipefail

lab=/home/gillesduif/igloo-mint-22.3-lab
seed="$lab/mint-oemdrv-fresh-v26.qcow2"
backing="$lab/mint-oemdrv-final-v20.raw"
vars="$lab/OVMF_VARS-fresh-v26-lowram.fd"
monitor="$lab/mint-monitor-fresh-v26-lowram.sock"
serial="$lab/mint-fresh-v26-lowram-serial.log"

test "$(readlink -f "$seed")" = "$seed"
test "$(readlink -f "$backing")" = "$backing"
test "$(stat -c '%h' "$seed")" = 1
test "$(stat -c '%h' "$backing")" = 1
test "$(stat -c '%s' "$seed")" = 170852352
test "$(stat -c '%s' "$backing")" = 4823449600
test -s "$vars"
test ! -L "$vars"
test ! -e "$monitor"
test ! -e "$serial"
test -r /dev/kvm
test -w /dev/kvm
test ! -e /sys/block/nbd0/pid
test -z "$(losetup -a)"

python3 - "$seed" "$backing" <<'PY'
import json
import subprocess
import sys

info = json.loads(subprocess.check_output(
    ['qemu-img', 'info', '--output=json', sys.argv[1]], text=True))
assert info['format'] == 'qcow2'
assert info['full-backing-filename'] == sys.argv[2]
assert info['backing-filename-format'] == 'raw'
assert info['virtual-size'] == 4823449600
assert not info['format-specific']['data']['corrupt']
PY

exec timeout --signal=TERM --kill-after=5s 720s qemu-system-x86_64 \
    -machine q35,accel=kvm -cpu host -smp 2 -m 4096 \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,file="$vars" \
    -drive if=none,id=seed,format=qcow2,file="$seed",snapshot=on \
    -device virtio-blk-pci,drive=seed,bootindex=1 \
    -net none -vga std -display vnc=127.0.0.1:8 \
    -monitor unix:"$monitor",server,nowait \
    -serial file:"$serial" -no-reboot
