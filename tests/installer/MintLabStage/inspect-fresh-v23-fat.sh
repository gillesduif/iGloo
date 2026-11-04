#!/usr/bin/env bash
set -euo pipefail

seed=/home/gillesduif/igloo-mint-22.3-lab/mint-oemdrv-fresh-v23.qcow2
test -f "$seed" && test ! -e /sys/block/nbd0/pid
qemu-nbd --read-only --connect=/dev/nbd0 --format=qcow2 "$seed"
trap 'qemu-nbd --disconnect /dev/nbd0' EXIT
udevadm settle
test -b /dev/nbd0p1
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
    assert bytes_per_sector == 512 and backup_sector < reserved_sectors
    partition.seek(backup_sector * bytes_per_sector)
    backup = partition.read(bytes_per_sector)
    differences = [offset for offset in range(bytes_per_sector) if primary[offset] != backup[offset]]
    print("primary-backup-boot-differences", [(offset, primary[offset], backup[offset]) for offset in differences])
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
qemu-nbd --disconnect /dev/nbd0
trap - EXIT
