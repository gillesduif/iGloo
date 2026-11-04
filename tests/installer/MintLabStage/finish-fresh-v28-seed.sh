#!/usr/bin/env bash
# Verify the bound v28 seed, then release its FAT mount and NBD device.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
seed="$lab/mint-oemdrv-fresh-v28.qcow2"
mnt="$lab/mint-seed-fresh-v28-mnt"
identity="$lab/mint-fresh-v28-seed-identity.json"
test ! -e "$identity"
test -e /sys/block/nbd0/pid
test "$(findmnt -rn -o SOURCE --mountpoint "$mnt")" = /dev/nbd0p1

mounted=1
connected=1
cleanup() {
    if test "$mounted" -eq 1; then umount "$mnt" || true; fi
    if test "$connected" -eq 1; then qemu-nbd --disconnect /dev/nbd0 || true; fi
}
trap cleanup EXIT

python3 "$lab/verify-fresh-v28-seed.py"
cp "$mnt/igloo-seed-identity.json" "$identity"
sync
umount "$mnt"; mounted=0
fsck.fat -n /dev/nbd0p1
qemu-nbd --disconnect /dev/nbd0; connected=0
udevadm settle
trap - EXIT

python3 - "$seed" <<'PY'
import json
import subprocess
import sys

info = json.loads(subprocess.check_output(
    ['qemu-img', 'info', '--output=json', sys.argv[1]], text=True))
assert info['format'] == 'qcow2'
assert info['backing-filename'] == '/home/gillesduif/igloo-mint-22.3-lab/mint-oemdrv-final-v20.raw'
assert info['backing-filename-format'] == 'raw'
PY
sha256sum "$identity"
qemu-img check "$seed"
du -B1 "$seed"
