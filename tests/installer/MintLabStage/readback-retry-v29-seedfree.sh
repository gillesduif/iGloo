#!/usr/bin/env bash
# Read-only verification after the current Mint target booted without OEMDRV.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-retry-v29.qcow2"
target_base="$lab/mint-target-fresh-v28.raw"
manifest="$lab/mint-fresh-v28-manifest.input.json"
expected="$lab/mint-fresh-v28-protected-prehashes.txt"
root="$lab/mint-root-retry-v29-seedfree-ro"
serial="$lab/mint-retry-v29-seedfree-serial.log"
capacity="$lab/mint-capacity-retry-v29-seedfree.jsonl"
output="$lab/mint-retry-v29-seedfree-readback.txt"
for file in "$target" "$target_base" "$manifest" "$expected" "$serial" "$capacity"; do
    test -f "$file" && test ! -L "$file" && test "$(stat -c %h "$file")" = 1
done
test ! -e "$output" && test ! -L "$output"
test ! -e /sys/block/nbd0/pid && test ! -e /sys/block/nbd1/pid
test -z "$(pgrep -f '[q]emu-system-x86_64.*mint-target-retry-v29.qcow2' || true)"
test "$(df -B1 --output=avail /mnt/c | tail -1 | tr -d ' ')" -gt 21474836480

python3 - "$target" "$target_base" "$capacity" "$serial" <<'PY'
import json
from pathlib import Path
import subprocess
import sys
info = json.loads(subprocess.check_output(
    ['qemu-img', 'info', '--output=json', sys.argv[1]], text=True))
assert info['format'] == 'qcow2'
assert info['backing-filename'] == sys.argv[2]
assert info['backing-filename-format'] == 'raw'
assert info['virtual-size'] == 42949672960
events = [json.loads(line) for line in Path(sys.argv[3]).read_text().splitlines()]
assert events[0]['event'] == 'start' and events[-1]['event'] == 'exit'
assert events[-1]['guestExitCode'] == 0 and not events[-1]['capacityStop']
assert not any(event['event'] == 'capacity-stop' for event in events)
serial = Path(sys.argv[4]).read_text(errors='replace')
assert '99B3F3BE-7150-4183-8453-596BAD5E3EA7' in serial
PY

mkdir -p "$root"
test -z "$(findmnt -rn --mountpoint "$root")"
connected=0
cleanup() {
    if mountpoint -q "$root"; then umount "$root"; fi
    if test "$connected" -eq 1; then qemu-nbd --disconnect /dev/nbd0; fi
}
trap cleanup EXIT
qemu-nbd --read-only --connect=/dev/nbd0 --format=qcow2 "$target"
connected=1
udevadm settle
for part in 1 2 3 4; do test -b "/dev/nbd0p${part}"; done
test "$(lsblk -dnro PTUUID /dev/nbd0 | tr '[:lower:]' '[:upper:]')" = 7862B40B-F6BD-4012-BEC8-E5C2FAA509E2
test "$(lsblk -dnro PARTUUID /dev/nbd0p3 | tr '[:lower:]' '[:upper:]')" = 99B3F3BE-7150-4183-8453-596BAD5E3EA7
p1_hash=$(sha256sum /dev/nbd0p1 | cut -d ' ' -f 1)
p2_hash=$(sha256sum /dev/nbd0p2 | cut -d ' ' -f 1)
test "$p1_hash" = "$(awk 'NR == 1 { print $1 }' "$expected")"
test "$p2_hash" = "$(awk 'NR == 2 { print $1 }' "$expected")"
mount -t ext4 -o ro,noload /dev/nbd0p4 "$root"
{
    python3 "$lab/check-retry-v29-deltas.py" "$root" "$manifest"
    printf 'SEED-DETACHED BOOT\n'; printf 'Selected Linux ESP partition GUID: 99B3F3BE-7150-4183-8453-596BAD5E3EA7\n'
    printf 'PROTECTED FULL PARTITION HASHES\n'; printf 'p1 %s\np2 %s\n' "$p1_hash" "$p2_hash"
    printf 'FIRST-BOOT COMPLETION\n'; stat -c '%n %a %U:%G' "$root/var/lib/igloo/.done"
    printf 'ALLOCATED BYTES\n'; du -B1 "$target" "$target_base"
} > "$output"
cat "$output"
cleanup
connected=0
trap - EXIT
qemu-img check "$target"
