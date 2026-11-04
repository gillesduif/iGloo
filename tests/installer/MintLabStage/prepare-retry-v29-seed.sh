#!/usr/bin/env bash
# Expose only the v29 disposable seed child for the repaired agent and rebinding.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
seed="$lab/mint-oemdrv-retry-v29.qcow2"
base="$lab/mint-oemdrv-fresh-v28.qcow2"
mnt="$lab/mint-seed-retry-v29-mnt"
old_agent=297a9594f01c7c9b7717baf17e434af7458cd24060465f8b02e46a0aa17d6012
old_identity=d3c4a791d2ea8bd954bec7583e3aef2a1af2d23b00b5700c2d52a16dbd365bc0

test -f "$seed" && test ! -L "$seed" && test "$(stat -c %h "$seed")" = 1
test -f "$base" && test ! -L "$base" && test "$(stat -c %h "$base")" = 1
test ! -e "$mnt" && test ! -e /sys/block/nbd0/pid
test -z "$(pgrep -f '[q]emu-system-x86_64' || true)"
test "$(df -B1 --output=avail /mnt/c | tail -1 | tr -d ' ')" -gt 26843545600
python3 - "$seed" "$base" <<'PY'
import json
import subprocess
import sys

info = json.loads(subprocess.check_output(
    ['qemu-img', 'info', '--output=json', sys.argv[1]], text=True))
assert info['format'] == 'qcow2' and info['backing-filename'] == sys.argv[2]
assert info['backing-filename-format'] == 'qcow2' and info['virtual-size'] == 4823449600
PY

connected=0
mounted=0
leave_mounted=0
cleanup() {
    if test "$leave_mounted" -eq 0; then
        if test "$mounted" -eq 1; then umount "$mnt" || true; fi
        if test "$connected" -eq 1; then qemu-nbd --disconnect /dev/nbd0 || true; fi
    fi
}
trap cleanup EXIT
qemu-nbd --connect=/dev/nbd0 --format=qcow2 "$seed"
connected=1
udevadm settle
test -b /dev/nbd0p1
test "$(lsblk -dnro PTUUID /dev/nbd0 | tr '[:lower:]' '[:upper:]')" = 65DE9CA9-A4B6-4A0F-B027-E15DFCD9E570
test "$(lsblk -dnro PARTUUID /dev/nbd0p1 | tr '[:lower:]' '[:upper:]')" = D80F6663-7280-4D6D-8B18-CAD62E3F0C94
fsck.fat -n /dev/nbd0p1
mkdir "$mnt"
mount -t vfat -o uid=1000,gid=1000,umask=000 /dev/nbd0p1 "$mnt"
mounted=1
test "$(findmnt -rn -o SOURCE --mountpoint "$mnt")" = /dev/nbd0p1
test "$(sha256sum "$mnt/igloo-agent/agent.py" | cut -d ' ' -f 1)" = "$old_agent"
test "$(sha256sum "$mnt/igloo-seed-identity.json" | cut -d ' ' -f 1)" = "$old_identity"
cmp -s "$mnt/migration-manifest.json" "$lab/mint-provider-stage-fresh-v28/migration-manifest.json"
leave_mounted=1
printf 'V29 seed child mounted for repaired agent; base untouched\n'
