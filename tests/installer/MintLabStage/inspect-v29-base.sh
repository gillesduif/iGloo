#!/usr/bin/env bash
# Read-only inspection of the preserved v20 postinstall target before child boot.
set -euo pipefail

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-final-v11.raw"
stage="$lab/mint-provider-stage-final-v20"
root="$lab/mint-root-current-agent-v29-baseline-ro"
test -f "$target" && test ! -L "$target" && test "$(stat -c %h "$target")" = 1
test -f "$stage/migration-manifest.json"
for sysdev in /sys/block/nbd*; do test ! -e "$sysdev/pid"; done
test -z "$(pgrep -f '[q]emu-system-x86_64' || true)"
test "$(sgdisk -p "$target" | sed -n 's/^Disk identifier (GUID): //p')" = AD868592-19D7-4DB4-914E-BC3EC93771E3
mkdir -p "$root"
test -z "$(findmnt -rn -o TARGET --mountpoint "$root")"
loop=$(losetup --read-only --find --show --partscan "$target")
cleanup() {
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
test -b "${loop}p4"
test "$(blkid -s TYPE -o value "${loop}p4")" = ext4
mount -t ext4 -o ro,noload "${loop}p4" "$root"
python3 - "$root" "$stage" <<'PY'
from hashlib import sha256
from pathlib import Path
import sys

root, stage = map(Path, sys.argv[1:])
manifest = root / 'var/lib/igloo/manifest.json'
assert manifest.is_file() and manifest.read_bytes() == (stage / 'migration-manifest.json').read_bytes()
assert not (root / 'var/lib/igloo/.done').exists()
assert (root / 'var/lib/igloo/selected-seed-partuuid').read_text().strip().lower() == 'd80f6663-7280-4d6d-8b18-cad62e3f0c94'
assert (root / 'var/lib/igloo/selected-seed-diskguid').read_text().strip().lower() == '65de9ca9-a4b6-4a0f-b027-e15dfcd9e570'
assert (root / 'etc/systemd/system/igloo-bootstrap.service').is_file()
assert (root / 'opt/igloo/igloo-bootstrap.sh').is_file()
agent = root / 'opt/igloo/agent.py'
assert agent.is_file()
print('Preserved v20 target: exact staged manifest, pending firstboot, bound seed IDs, bootstrap service')
print('Installed agent SHA256:', sha256(agent.read_bytes()).hexdigest())
PY
