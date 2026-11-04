#!/usr/bin/env bash
# Read-only qualification of the preserved disposable Debian v6 installed root.
set -euo pipefail

base=/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6
image=$base/selected.raw
mountpoint=$base/mnt-installed-root-ro
test "$(id -u)" = 0
test "$(stat -c %s "$image")" = 32212254720
test "$(stat -c %h "$image")" = 1
test -z "$(losetup -a)"
! pgrep -af '[q]emu-system-x86_64' >/dev/null
! findmnt -M "$mountpoint" >/dev/null

loop=$(losetup --find --show --partscan --read-only "$image")
cleanup() {
    if mountpoint -q "$mountpoint"; then umount "$mountpoint"; fi
    losetup -d "$loop"
}
trap cleanup EXIT
test "$(readlink -f "$(losetup --noheadings --output BACK-FILE "$loop")")" = "$(readlink -f "$image")"
printf 'selectedDiskGuid=%s\n' "$(lsblk -dnro PTUUID "$loop")"
linux_esp_guid=$(sgdisk -i 3 "$image" | awk '/Partition unique GUID:/ {print tolower($4)}')
test "$linux_esp_guid" = 6cfd80d0-dd42-4b42-91d9-f114559685d4
printf 'linuxEspPartuuid=%s\n' "$linux_esp_guid"
mount -o ro,noload,nodev,nosuid "${loop}p4" "$mountpoint"
python3 - "$mountpoint" <<'PY'
import crypt
import json
from pathlib import Path
import stat
import sys

root = Path(sys.argv[1])
manifest = json.loads(Path('/home/gillesduif/igloo-lab/debian-20261017/exact-efi-v6/manifest.json').read_text())
user = manifest['user']['preferredLinuxUsername']
shadow = next(line.split(':')[1] for line in (root / 'etc/shadow').read_text().splitlines()
              if line.startswith(user + ':'))
bootstrap = (root / 'opt/igloo/igloo-bootstrap.sh').read_text()
print(json.dumps({
    'rootUser': user,
    'uid': (root / 'home' / user).stat().st_uid,
    'done': (root / 'var/lib/igloo/.done').is_file(),
    'bootstrapService': (root / 'etc/systemd/system/igloo-bootstrap.service').is_file(),
    'shadowMatchesManifest': shadow == manifest['user']['linuxPasswordCrypted'],
    'knownLabPasswordMatches': crypt.crypt('igloolab123', shadow) == shadow,
    'optIglooMode': oct(stat.S_IMODE((root / 'opt/igloo').stat().st_mode)),
    'bootstrapUsesSelectedSeedPartuuid': 'selected-seed-partuuid' in bootstrap,
    'bootstrapUsesSelectedSeedDiskguid': 'selected-seed-diskguid' in bootstrap,
    'bootstrapCopiesBoundAgent': '/run/igloo-seed/igloo-agent/agent.py' in bootstrap,
    'bootstrapUsesCurrentVerifier': '/opt/igloo/igloo-verify-seed.py' in bootstrap,
    'bootstrapSupportsRetryManifest': 'manifest.retry.json' in bootstrap,
    'fstab': (root / 'etc/fstab').read_text()[-400:],
}, indent=2))
if __import__('os').environ.get('IGLOO_INSPECT_BOOTSTRAP') == '1':
    print('--- installed v6 bootstrap ---')
    print(bootstrap)
PY
