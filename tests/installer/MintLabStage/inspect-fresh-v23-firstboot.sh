#!/usr/bin/env bash
set -euo pipefail

lab=/home/gillesduif/igloo-mint-22.3-lab
target="$lab/mint-target-fresh-v23.raw"
root="$lab/mint-root-fresh-v23-ro"
test -f "$target" && test ! -e /sys/block/nbd0/pid
mkdir -p "$root"
test ! "$(findmnt -n "$root")"
loop=$(losetup --read-only --find --show --partscan "$target")
cleanup() {
    if mountpoint -q "$root"; then umount "$root"; fi
    losetup -d "$loop"
}
trap cleanup EXIT
test "$(losetup -n -O BACK-FILE "$loop")" = "$target"
udevadm settle
test -b "${loop}p4"
mount -t ext4 -o ro,noload "${loop}p4" "$root"
printf 'FIRST-BOOT MARKERS\n'
find "$root/var/lib/igloo" -maxdepth 1 -type f -printf '%f\n' | sort
printf 'AGENT LOG TAIL\n'
if test -f "$root/var/log/igloo/agent.log"; then
    tail -n 90 "$root/var/log/igloo/agent.log"
else
    printf 'agent.log absent\n'
fi
printf 'NETWORKMANAGER PROFILE METADATA\n'
find "$root/etc/NetworkManager/system-connections" -maxdepth 1 -type f -name '*.nmconnection' -printf '%f %m %u:%g\n' 2>/dev/null || true
printf 'CREDENTIAL STORE METADATA\n'
if test -f "$root/home/mintlab/.local/share/igloo/credentials.json"; then
    stat -c '%n %a %U:%G' "$root/home/mintlab/.local/share/igloo/credentials.json"
else
    printf 'credentials.json absent\n'
fi
