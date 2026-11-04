#!/usr/bin/env bash
# Extract bounded, secret-free evidence about installer OEMDRV mount teardown.
set -euo pipefail
umask 077

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v24.raw"
root="$lab/mint-root-fresh-v24-ro"
output="$lab/mint-fresh-v24-installer-fat-mounts.txt"
test -f "$target" && test ! -L "$target"
test ! -e "$output" && test ! -L "$output"
test ! -e /sys/block/nbd0/pid
test -z "$(pgrep -f '[q]emu-system-x86_64.*mint-target-fresh-v24.raw' || true)"
mkdir -p "$root"
test -z "$(findmnt -n "$root")"
loop=$(losetup --read-only --find --show --partscan "$target")
cleanup() {
    if mountpoint -q "$root"; then umount "$root"; fi
    losetup -d "$loop"
}
trap cleanup EXIT
test "$(losetup -n -O BACK-FILE "$loop")" = "$target"
test "$(lsblk -dnro PTUUID "$loop" | tr '[:lower:]' '[:upper:]')" = 3E264084-C958-41CB-AF82-3D247E3FC75D
udevadm settle
test -b "${loop}p4"
mount -t ext4 -o ro,noload "${loop}p4" "$root"

python3 - "$root" > "$output" <<'PY'
from pathlib import Path
import sys

root = Path(sys.argv[1])
for relative in (
    "var/log/igloo-install.log",
    "var/log/installer/syslog",
    "var/log/syslog",
):
    path = root / relative
    if not path.is_file() or path.is_symlink():
        print(relative, "absent")
        continue
    size = path.stat().st_size
    assert size <= 64 * 1024 * 1024, (relative, size)
    data = path.read_bytes()
    print(relative, "bytes", size)
    for needle in (
        b"/run/igloo-seed", b"remount,bind,ro", b"umount /run/igloo-seed",
        b"unmount /run/igloo-seed", b"igloo-bootstrap.sh --install",
        b"igloo-seed: mounting", b"igloo-partman: mounting",
    ):
        print("  ", needle.decode(), "occurrences", data.count(needle))
    # Only report a bounded, known success-command line, never arbitrary log
    # content that could contain a manifest password hash or Wi-Fi secret.
    print("  install-hook-ended", b"igloo-bootstrap.sh --install" in data)
PY

cat "$output"
umount "$root"
losetup -d "$loop"
trap - EXIT
