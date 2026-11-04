#!/bin/sh
# partman-target runs fstab.d scripts both when mounting /target and when
# writing its fstab. Mint's stock EFI script chooses the first ESP on the
# autopartition disk, which can be the existing Windows ESP instead of the
# dedicated Linux ESP selected by Igloo.
. /lib/partman/lib/base.sh
. /lib/partman/lib/auto-shared.sh
set -e

fail() {
    if [ -c /dev/ttyS0 ]; then
        printf 'igloo-bound-efi: refused EFI mount: %s\n' "$1" > /dev/ttyS0
    fi
    exit 99
}

case "$(archdetect)" in
    amd64/efi) ;;
    *) fail 'unexpected installer architecture' ;;
esac

bound=$(cat /tmp/igloo-bound-disk 2>/dev/null) || fail 'selected disk missing'
read -r expected_guid expected_bytes < /tmp/igloo-bound-identity ||
    fail 'selected disk identity missing'
esp_partuuid=$(cat /run/igloo-selected-linux-esp-partuuid 2>/dev/null) ||
    fail 'Linux ESP identity missing'
printf '%s\n' "$expected_guid" |
    grep -Eq '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$' ||
    fail 'invalid selected disk GUID'
printf '%s\n' "$esp_partuuid" |
    grep -Eq '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$' ||
    fail 'invalid Linux ESP GUID'
case "$bound" in /dev/*) ;; *) fail 'invalid selected disk path' ;; esac
[ -b "$bound" ] && [ "$(readlink -f "$bound")" = "$bound" ] ||
    fail 'selected disk changed'
[ "$(blkid -p -s PTUUID -o value "$bound" 2>/dev/null |
    tr '[:upper:]' '[:lower:]')" = "$expected_guid" ] ||
    fail 'selected disk GUID changed'
[ "$(blockdev --getsize64 "$bound" 2>/dev/null)" = "$expected_bytes" ] ||
    fail 'selected disk size changed'

# Reject a duplicate partition GUID anywhere in the live block inventory.
physical_matches=0
physical_path=''
for sys in /sys/class/block/*; do
    [ -f "$sys/partition" ] || continue
    path="/dev/${sys##*/}"
    [ -b "$path" ] || continue
    partuuid=$(blkid -p -s PART_ENTRY_UUID -o value "$path" 2>/dev/null |
        tr '[:upper:]' '[:lower:]')
    [ "$partuuid" = "$esp_partuuid" ] || continue
    physical_matches=$((physical_matches + 1))
    physical_path=$path
done
[ "$physical_matches" -eq 1 ] || fail 'Linux ESP PARTUUID is not unique'
[ "$(blkid -p -s PART_ENTRY_TYPE -o value "$physical_path" 2>/dev/null |
    tr '[:upper:]' '[:lower:]')" = c12a7328-f81f-11d2-ba4b-00a0c93ec93b ] ||
    fail 'Linux ESP GPT type changed'
[ "$(blkid -p -s TYPE -o value "$physical_path" 2>/dev/null)" = vfat ] ||
    fail 'Linux ESP filesystem changed'
[ "$(blockdev --getsize64 "$physical_path" 2>/dev/null)" = 1073741824 ] ||
    fail 'Linux ESP size changed'

selected_dev=$(dev_to_partman "$bound") || fail 'selected disk is not in partman'
[ -n "$selected_dev" ] && [ -d "$selected_dev" ] ||
    fail 'selected disk is not in partman'
partman_matches=0
selected_path=''
for dev in "$DEVICES"/*; do
    [ -d "$dev" ] || continue
    cd "$dev" || fail 'partman device vanished'
    open_dialog PARTITIONS || fail 'partman inventory unavailable'
    while { read_line num id size type fs path name; [ "$id" ]; }; do
        [ "$fs" != free ] && [ -b "$path" ] || continue
        partuuid=$(blkid -p -s PART_ENTRY_UUID -o value "$path" 2>/dev/null |
            tr '[:upper:]' '[:lower:]')
        [ "$partuuid" = "$esp_partuuid" ] || continue
        partman_matches=$((partman_matches + 1))
        [ "$dev" = "$selected_dev" ] &&
            [ "$(readlink -f "$path")" = "$physical_path" ] &&
            [ "$fs" = fat32 ] && [ "$size" = 1073741824 ] ||
            fail 'Linux ESP moved outside the selected disk'
        [ ! -e "$id/format" ] && [ -f "$id/method" ] &&
            [ "$(cat "$id/method")" = efi ] ||
            fail 'Linux ESP is not preserved for EFI use'
        selected_path=$path
    done
    close_dialog || fail 'partman inventory close failed'
done
[ "$partman_matches" -eq 1 ] && [ -n "$selected_path" ] ||
    fail 'Linux ESP missing from partman'

[ -f /run/igloo-protected-esp-hashes ] ||
    fail 'protected ESP inventory missing'
if [ -s /run/igloo-protected-esp-hashes ]; then
    sha256sum --check --status /run/igloo-protected-esp-hashes ||
        fail 'an existing ESP changed'
fi

if [ -c /dev/ttyS0 ]; then
    printf 'igloo-bound-efi: using %s PARTUUID %s for /boot/efi\n' \
        "$selected_path" "$esp_partuuid" > /dev/ttyS0
fi
printf '%s /boot/efi vfat umask=0077 0 1\n' "$selected_path"
