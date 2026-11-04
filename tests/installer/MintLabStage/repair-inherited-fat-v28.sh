#!/usr/bin/env bash
# Repair only the fresh v28 copy-on-write seed after recording inherited FAT errors.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
seed="$lab/mint-oemdrv-fresh-v28.qcow2"
base="$lab/mint-oemdrv-final-v20.raw"
baseline="$lab/mint-fresh-v28-fat-inherited.txt"
report="$lab/mint-fresh-v28-fat-inherited-repair.txt"

test -f "$seed" && test ! -L "$seed" && test "$(stat -c %h "$seed")" = 1
test -f "$base" && test ! -L "$base" && test "$(stat -c %h "$base")" = 1
test ! -e "$baseline" && test ! -e "$report"
test ! -e /sys/block/nbd0/pid
test -z "$(pgrep -f '[q]emu-system-x86_64' || true)"
python3 - "$seed" "$base" <<'PY'
import json
import subprocess
import sys

info = json.loads(subprocess.check_output(
    ['qemu-img', 'info', '--output=json', sys.argv[1]], text=True))
assert info['format'] == 'qcow2'
assert info['backing-filename'] == sys.argv[2]
assert info['backing-filename-format'] == 'raw'
assert info['virtual-size'] == 4823449600
PY

qemu-nbd --connect=/dev/nbd0 --format=qcow2 "$seed"
trap 'qemu-nbd --disconnect /dev/nbd0' EXIT
udevadm settle
test -b /dev/nbd0p1
test "$(lsblk -dnro PTUUID /dev/nbd0 | tr '[:lower:]' '[:upper:]')" = 65DE9CA9-A4B6-4A0F-B027-E15DFCD9E570
test "$(lsblk -dnro PARTUUID /dev/nbd0p1 | tr '[:lower:]' '[:upper:]')" = D80F6663-7280-4D6D-8B18-CAD62E3F0C94

set +e
fsck.fat -n /dev/nbd0p1 > "$baseline" 2>&1
baseline_status=$?
set -e
printf 'read-only-fsck-exit %s\n' "$baseline_status" >> "$baseline"
test "$baseline_status" -eq 1

set +e
fsck.fat -a /dev/nbd0p1 > "$report" 2>&1
repair_status=$?
set -e
printf 'repair-exit %s\n' "$repair_status" >> "$report"
test "$repair_status" -eq 0 || test "$repair_status" -eq 1
fsck.fat -n /dev/nbd0p1 >> "$report" 2>&1
qemu-nbd --disconnect /dev/nbd0
trap - EXIT
cat "$baseline" "$report"
