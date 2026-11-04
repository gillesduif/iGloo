#!/usr/bin/env bash
# Repair only the disposable v24 child after recording its inherited FAT state.
set -euo pipefail
umask 077
lab=/home/gillesduif/igloo-mint-22.3-lab
seed="$lab/mint-oemdrv-fresh-v24.qcow2"
baseline="$lab/mint-fresh-v24-fat-inherited.txt"
report="$lab/mint-fresh-v24-fat-inherited-repair.txt"
test -s "$baseline" && grep -q 'read-only-fsck-exit 1' "$baseline"
test -s "$seed" && test ! -e "$report" && test ! -e /sys/block/nbd0/pid
python3 - "$seed" "$lab/mint-oemdrv-final-v20.raw" <<'PY'
import json, subprocess, sys
info = json.loads(subprocess.check_output(
    ['qemu-img', 'info', '--output=json', sys.argv[1]], text=True))
assert info['format'] == 'qcow2' and info['backing-filename'] == sys.argv[2]
assert info['backing-filename-format'] == 'raw' and info['virtual-size'] == 4823449600
PY
qemu-nbd --connect=/dev/nbd0 --format=qcow2 "$seed"
trap 'qemu-nbd --disconnect /dev/nbd0' EXIT
udevadm settle
test -b /dev/nbd0p1
test "$(lsblk -dnro PTUUID /dev/nbd0)" = 65de9ca9-a4b6-4a0f-b027-e15dfcd9e570
test "$(lsblk -dnro PARTUUID /dev/nbd0p1)" = d80f6663-7280-4d6d-8b18-cad62e3f0c94
set +e
fsck.fat -a /dev/nbd0p1 > "$report" 2>&1
repair_status=$?
set -e
test "$repair_status" -eq 0 || test "$repair_status" -eq 1
printf 'repair-exit %s\n' "$repair_status" >> "$report"
fsck.fat -n /dev/nbd0p1 >> "$report" 2>&1
qemu-nbd --disconnect /dev/nbd0
trap - EXIT
