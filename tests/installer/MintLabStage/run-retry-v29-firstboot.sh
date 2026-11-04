#!/usr/bin/env bash
# Retry the failed Mint v28 first boot on two bounded v29 child images only.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-retry-v29.qcow2"
target_base="$lab/mint-target-fresh-v28.raw"
seed="$lab/mint-oemdrv-retry-v29.qcow2"
seed_base="$lab/mint-oemdrv-fresh-v28.qcow2"
decoy="$lab/mint-decoy-fresh-v28.raw"
vars="$lab/OVMF_VARS-retry-v29.fd"
identity="$lab/mint-retry-v29-seed-identity.json"
for file in "$target" "$target_base" "$seed" "$seed_base" "$decoy" "$vars" "$identity"; do
    test -f "$file" && test ! -L "$file" && test "$(stat -c %h "$file")" = 1
done
test ! -e /sys/block/nbd0/pid
test -z "$(losetup -j "$target_base")"
test "$(sgdisk -p "$target_base" | sed -n 's/^Disk identifier (GUID): //p')" = 7862B40B-F6BD-4012-BEC8-E5C2FAA509E2
python3 - "$target" "$target_base" "$seed" "$seed_base" "$identity" <<'PY'
import json
import subprocess
import sys
from pathlib import Path

for child, base, fmt, size in (
        (sys.argv[1], sys.argv[2], 'raw', 42949672960),
        (sys.argv[3], sys.argv[4], 'qcow2', 4823449600)):
    info = json.loads(subprocess.check_output(
        ['qemu-img', 'info', '--output=json', child], text=True))
    assert info['format'] == 'qcow2' and info['backing-filename'] == base
    assert info['backing-filename-format'] == fmt and info['virtual-size'] == size
identity = json.loads(Path(sys.argv[5]).read_text())
assert identity['diskGuid'].lower() == '65de9ca9-a4b6-4a0f-b027-e15dfcd9e570'
assert identity['partitionGuid'].lower() == 'd80f6663-7280-4d6d-8b18-cad62e3f0c94'
assert len(identity['files']) == 21
assert any(entry['path'] == 'igloo-agent/agent.py' and
           entry['sha256'].lower() == 'cdc2c3be300ec1f545c07fff4f59b4a0c3bc7a4258e6c243ccd5daeb37b86a7b'
           for entry in identity['files'])
PY

exec qemu-system-x86_64 \
    -machine q35,accel=kvm -cpu host -smp 4 -m 7168 \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,file="$vars" \
    -drive if=none,id=target,format=qcow2,file="$target" \
    -device virtio-blk-pci,drive=target,bootindex=1 \
    -drive if=none,id=seed,format=qcow2,file="$seed" \
    -device virtio-blk-pci,drive=seed,bootindex=2 \
    -drive if=none,id=decoy,format=raw,readonly=on,file="$decoy" \
    -device virtio-blk-pci,drive=decoy,bootindex=3 \
    -netdev user,id=n0 -device virtio-net-pci,netdev=n0 \
    -vga std -display vnc=127.0.0.1:0 \
    -monitor unix:"$lab/mint-monitor-retry-v29-firstboot.sock",server,nowait \
    -serial file:"$lab/mint-retry-v29-firstboot-serial.log" -no-reboot
