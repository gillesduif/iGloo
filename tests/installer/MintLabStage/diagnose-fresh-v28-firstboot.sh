#!/usr/bin/env bash
# Read-only failure triage for the exact disposable Mint v28 first boot.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v28.raw"
root="$lab/mint-root-fresh-v28-diag-ro"
test -f "$target" && test ! -L "$target" && test "$(stat -c %h "$target")" = 1
test ! -e /sys/block/nbd0/pid
test -z "$(pgrep -f '[q]emu-system-x86_64.*mint-target-fresh-v28.raw' || true)"
test "$(sgdisk -p "$target" | sed -n 's/^Disk identifier (GUID): //p')" = 7862B40B-F6BD-4012-BEC8-E5C2FAA509E2
mkdir -p "$root"
test -z "$(findmnt -rn -o TARGET --mountpoint "$root")"
loop=$(losetup --read-only --find --show --partscan "$target")
cleanup() {
    if mountpoint -q "$root"; then umount "$root"; fi
    losetup -d "$loop"
}
trap cleanup EXIT
udevadm settle
test -b "${loop}p4"
mount -t ext4 -o ro,noload "${loop}p4" "$root"

python3 - "$root" "$lab/mint-fresh-v28-manifest.input.json" <<'PY'
import hashlib
import json
from pathlib import Path
import re
import sys

root, manifest_path = map(Path, sys.argv[1:])
manifest = json.loads(manifest_path.read_text())
secrets = [manifest['user']['linuxPasswordCrypted']]
secrets += [network['psk'] for network in manifest['wifiNetworks'] if network.get('psk')]
secrets += [browser['credentialsBlob'] for browser in manifest['browsers'] if browser.get('credentialsBlob')]

state = root / 'var/lib/igloo'
print('STATE ENTRIES:', sorted(path.name for path in state.iterdir()))
for name in ('agent.py', 'igloo-bootstrap.sh', 'igloo-verify-seed.py'):
    path = root / 'opt/igloo' / name
    print(name, hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else 'absent')
for name in ('bootstrap.log', 'agent.log'):
    path = root / 'var/log/igloo' / name
    print(name, 'present' if path.is_file() else 'absent')
    if not path.is_file():
        continue
    lines = path.read_text(errors='replace').splitlines()[-80:]
    for line in lines:
        for secret in secrets:
            if secret:
                line = line.replace(secret, '[REDACTED]')
        print(line[:500])
    if name == 'agent.log':
        print('SELECTED BROWSER/PACKAGE STEP LINES:')
        pattern = re.compile(r'(step (flathub|suggested-pkgs|user-files)|--- step: (flathub|suggested-pkgs|user-files)|chrom(e|ium)|flatpak)', re.I)
        matches = [line for line in path.read_text(errors='replace').splitlines() if pattern.search(line)]
        for line in matches[-45:]:
            for secret in secrets:
                if secret:
                    line = line.replace(secret, '[REDACTED]')
            print(line[:500])
for relative in ('usr/bin/google-chrome', 'usr/bin/chromium', 'usr/bin/flatpak',
                 'var/lib/flatpak/app/com.google.Chrome', 'var/lib/flatpak/app/org.chromium.Chromium'):
    print(relative, (root / relative).exists())
PY

if test -d "$root/var/log/journal"; then
    printf 'BOOTSTRAP UNIT JOURNAL\n'
    journalctl --directory="$root/var/log/journal" -b -u igloo-bootstrap.service \
        -o cat --no-pager | tail -n 30
fi
