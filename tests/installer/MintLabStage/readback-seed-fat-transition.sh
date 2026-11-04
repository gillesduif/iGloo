#!/usr/bin/env bash
# Run only with the exclusive disposable lab NBD slot, between VM phases.
# Usage: readback-seed-fat-transition.sh <qcow2-seed> <seed-diskguid> <seed-partuuid> <phase> <output>
set -euo pipefail
umask 077

test "$#" -eq 5
seed=$1
expected_diskguid=$2
expected_partuuid=$3
phase=$4
output=$5
lab=/home/gillesduif/igloo-mint-22.3-lab
test "$(realpath "$seed")" = "$lab/$(basename "$seed")"
test "$(realpath "$(dirname "$output")")" = "$lab"
case "$(basename "$output")" in mint-fresh-v24-fat-*.txt) ;; *) exit 2 ;; esac
test -f "$seed" && test ! -e "$output" && test ! -L "$output"
test ! -e /sys/block/nbd0/pid
test ! "$(findmnt -n /dev/nbd0p1)"

qemu-nbd --read-only --connect=/dev/nbd0 --format=qcow2 "$seed"
trap 'qemu-nbd --disconnect /dev/nbd0' EXIT
udevadm settle
test -b /dev/nbd0p1
observed_diskguid=$(lsblk -dnro PTUUID /dev/nbd0)
observed_partuuid=$(lsblk -dnro PARTUUID /dev/nbd0p1)
test "${observed_diskguid,,}" = "${expected_diskguid,,}"
test "${observed_partuuid,,}" = "${expected_partuuid,,}"

{
    printf 'phase %s\nseed %s\ndiskGuid %s\npartitionGuid %s\n' \
        "$phase" "$seed" "$observed_diskguid" "$observed_partuuid"
    python3 - /dev/nbd0p1 <<'PY'
import struct
import sys

with open(sys.argv[1], "rb", buffering=0) as partition:
    primary = partition.read(512)
    bytes_per_sector = struct.unpack_from("<H", primary, 11)[0]
    backup_sector = struct.unpack_from("<H", primary, 50)[0]
    reserved_sectors = struct.unpack_from("<H", primary, 14)[0]
    fat_count = primary[16]
    fat_sectors = struct.unpack_from("<I", primary, 36)[0]
    assert bytes_per_sector == 512 and 0 < backup_sector < reserved_sectors
    partition.seek(backup_sector * bytes_per_sector)
    backup = partition.read(bytes_per_sector)
    differences = [(i, primary[i], backup[i]) for i in range(bytes_per_sector)
                   if primary[i] != backup[i]]
    print("primary-backup-boot-differences", differences)
    for index in range(fat_count):
        partition.seek((reserved_sectors + index * fat_sectors) * bytes_per_sector + 4)
        entry = struct.unpack("<I", partition.read(4))[0]
        print("fat-entry-1", index, hex(entry), "clean-shutdown-bit", bool(entry & 0x08000000))
PY
    if fsck.fat -n /dev/nbd0p1; then
        printf 'read-only-fsck-exit 0\n'
    else
        printf 'read-only-fsck-exit %s\n' "$?"
    fi
} > "$output"

qemu-nbd --disconnect /dev/nbd0
trap - EXIT
