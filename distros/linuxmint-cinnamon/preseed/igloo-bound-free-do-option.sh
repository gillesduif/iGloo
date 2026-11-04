#!/bin/sh
# Recheck the selected disk and free extent immediately before autopartition.
. /lib/partman/lib/base.sh
. /lib/partman/lib/auto-shared.sh
umask 077

[ "$#" -eq 1 ] || exit 99
bound=$(cat /tmp/igloo-bound-disk 2>/dev/null) || exit 99
selected=$(cat /tmp/igloo-bound-free-choice 2>/dev/null) || exit 99
saved_size=$(cat /tmp/igloo-bound-free-size 2>/dev/null) || exit 99
read -r expected_guid expected_bytes < /tmp/igloo-bound-identity || exit 99
[ "$1" = "$selected" ] && [ -b "$bound" ] || exit 99

dev=${1%//*}
id=${1#*//}
partman=$(dev_to_partman "$bound") || exit 99
[ -n "$id" ] && [ "$dev" = "$partman" ] && [ -d "$dev" ] || exit 99

guid_matches=0
size_matches=0
for sys in /sys/block/*; do
    disk="/dev/${sys##*/}"
    [ -b "$disk" ] || continue
    guid=$(blkid -p -s PTUUID -o value "$disk" 2>/dev/null | tr '[:upper:]' '[:lower:]')
    [ "$guid" = "$expected_guid" ] || continue
    guid_matches=$((guid_matches + 1))
    bytes=$(blockdev --getsize64 "$disk" 2>/dev/null) || continue
    if [ "$bytes" = "$expected_bytes" ]; then
        size_matches=$((size_matches + 1))
        [ "$disk" = "$bound" ] || exit 99
    fi
done
[ "$guid_matches" -eq 1 ] && [ "$size_matches" -eq 1 ] || exit 99

minimum_install_bytes={{MINIMUM_INSTALL_BYTES}}
reviewed_offset={{INSTALLER_FREE_OFFSET_BYTES}}
reviewed_size={{INSTALLER_FREE_SIZE_BYTES}}
if [ "$reviewed_offset" != none ] || [ "$reviewed_size" != none ]; then
    printf '%s\n' "$reviewed_offset" | grep -Eq '^[1-9][0-9]*$' || exit 99
    printf '%s\n' "$reviewed_size" | grep -Eq '^[1-9][0-9]*$' || exit 99
    [ "$reviewed_offset" != none ] && [ "$reviewed_size" != none ] || exit 99
fi

installable_free() {
    [ "$3" = free ] && [ "$4" != unusable ] &&
        longint_le "$minimum_install_bytes" "$2" || return 1
    if [ "$reviewed_offset" != none ]; then
        case "$1" in
            *-*) [ "${1%%-*}" = "$reviewed_offset" ] &&
                     [ "$2" = "$reviewed_size" ] || return 1 ;;
            *) return 1 ;;
        esac
    fi
    return 0
}

eligible_count=0
found=0
cd "$dev" || exit 99
open_dialog PARTITIONS
while { read_line num current_id size type fs path name; [ "$current_id" ]; }; do
    if installable_free "$current_id" "$size" "$fs" "$type"; then
        eligible_count=$((eligible_count + 1))
        if [ "$current_id" = "$id" ] && [ "$size" = "$saved_size" ]; then
            found=$((found + 1))
        fi
    fi
done
close_dialog
[ "$eligible_count" -eq 1 ] && [ "$found" -eq 1 ] || exit 99
if [ -c /dev/ttyS0 ]; then
    printf 'igloo-bound-free: applying bound extent %s size=%s\n' "$1" "$saved_size" > /dev/ttyS0
fi

# The reviewed preparation creates a dedicated 1 GiB Linux ESP and passes its
# freshly observed PARTUUID to the installer. Ubiquity otherwise reuses the
# preexisting Windows ESP, as the native two-disk v8 run demonstrated.
linux_esp_partuuid=$(cat /run/igloo-selected-linux-esp-partuuid 2>/dev/null) || exit 99
printf '%s\n' "$linux_esp_partuuid" |
    grep -Eq '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$' || exit 99
esp_type=c12a7328-f81f-11d2-ba4b-00a0c93ec93b
esp_matches=0
esp_dev=''
esp_id=''
esp_path=''
for candidate_dev in "$DEVICES"/*; do
    [ -d "$candidate_dev" ] || continue
    cd "$candidate_dev" || exit 99
    open_dialog PARTITIONS || exit 99
    while { read_line num partition_id partition_size type partition_fs path name; [ "$partition_id" ]; }; do
        [ "$partition_fs" != free ] && [ -b "$path" ] || continue
        partuuid=$(blkid -p -s PART_ENTRY_UUID -o value "$path" 2>/dev/null |
            tr '[:upper:]' '[:lower:]')
        [ "$partuuid" = "$linux_esp_partuuid" ] || continue
        esp_matches=$((esp_matches + 1))
        [ "$candidate_dev" = "$dev" ] && [ "$partition_fs" = fat32 ] &&
            [ "$partition_size" = 1073741824 ] || exit 99
        part_type=$(blkid -p -s PART_ENTRY_TYPE -o value "$path" 2>/dev/null |
            tr '[:upper:]' '[:lower:]')
        [ "$part_type" = "$esp_type" ] || exit 99
        esp_dev=$candidate_dev
        esp_id=$partition_id
        esp_path=$path
    done
    close_dialog || exit 99
done
[ "$esp_matches" -eq 1 ] && [ "$esp_dev" = "$dev" ] && [ -n "$esp_id" ] || exit 99

# The exact GPT ESP type and FAT32 geometry were read from the block device
# above. Ubiquity may restore method=efi on every bootable ESP after this
# action. Its fstab renderer has been replaced before any automatic choice
# was offered, and must select only the nominated dedicated ESP.
selected_method=0
: > /run/igloo-protected-esp-hashes || exit 99
chmod 0600 /run/igloo-protected-esp-hashes || exit 99
for candidate_dev in "$DEVICES"/*; do
    [ -d "$candidate_dev" ] || continue
    cd "$candidate_dev" || exit 99
    open_dialog PARTITIONS || exit 99
    while { read_line num partition_id partition_size type partition_fs path name; [ "$partition_id" ]; }; do
        [ "$partition_fs" != free ] && [ -b "$path" ] || continue
        part_type=$(blkid -p -s PART_ENTRY_TYPE -o value "$path" 2>/dev/null |
            tr '[:upper:]' '[:lower:]')
        [ "$part_type" = "$esp_type" ] || continue
        [ ! -e "$partition_id/format" ] || exit 99
        if [ "$candidate_dev" = "$esp_dev" ] && [ "$partition_id" = "$esp_id" ]; then
            [ -f "$partition_id/method" ] &&
                [ "$(cat "$partition_id/method")" = efi ] || exit 99
            selected_method=$((selected_method + 1))
        elif [ "$candidate_dev" = "$esp_dev" ]; then
            sha256sum "$path" >> /run/igloo-protected-esp-hashes || exit 99
        fi
    done
    close_dialog || exit 99
done
[ "$selected_method" -eq 1 ] || exit 99
fstab_entry=/lib/partman/fstab.d/efi
efi_source=/lib/partman/igloo-bound-efi-fstab.sh
[ -s "$efi_source" ] && [ -x "$efi_source" ] && [ -x "$fstab_entry" ] &&
    cmp -s "$efi_source" "$fstab_entry" || exit 99
printf '%s  %s\n' '{{IGLOO_BOUND_EFI_FSTAB_SHA256}}' "$efi_source" |
    sha256sum --check --status - || exit 99
printf '%s  %s\n' '{{IGLOO_BOUND_EFI_FSTAB_SHA256}}' "$fstab_entry" |
    sha256sum --check --status - || exit 99
findmnt -rn -o OPTIONS --mountpoint "$fstab_entry" |
    grep -Eq '(^|,)ro(,|$)' || exit 99
expected_efi_line="$esp_path /boot/efi vfat umask=0077 0 1"
actual_efi_line=$("$fstab_entry") || exit 99
[ "$actual_efi_line" = "$expected_efi_line" ] || exit 99
if [ -c /dev/ttyS0 ]; then
    printf 'igloo-bound-free: bound Linux ESP PARTUUID %s\n' "$linux_esp_partuuid" > /dev/ttyS0
fi

autopartition "$dev" "$id"
code=$?
[ "$code" -eq 255 ] && code=99
[ "$code" -eq 0 ] || exit "$code"

if [ -s /run/igloo-protected-esp-hashes ]; then
    sha256sum --check --status /run/igloo-protected-esp-hashes || exit 99
fi
actual_efi_line=$("$fstab_entry") || exit 99
[ "$actual_efi_line" = "$expected_efi_line" ] || exit 99

# The automatic recipe must not assign /boot/efi to a different ESP after
# consuming the bound free extent.
selected_method=0
for candidate_dev in "$DEVICES"/*; do
    [ -d "$candidate_dev" ] || continue
    cd "$candidate_dev" || exit 99
    open_dialog PARTITIONS || exit 99
    while { read_line num partition_id partition_size type partition_fs path name; [ "$partition_id" ]; }; do
        [ "$partition_fs" != free ] && [ -b "$path" ] || continue
        if [ "$candidate_dev" = "$esp_dev" ] && [ "$partition_id" = "$esp_id" ]; then
            [ -f "$partition_id/method" ] &&
                [ "$(cat "$partition_id/method")" = efi ] &&
                [ ! -e "$partition_id/format" ] || exit 99
            partuuid=$(blkid -p -s PART_ENTRY_UUID -o value "$path" 2>/dev/null |
                tr '[:upper:]' '[:lower:]')
            [ "$partuuid" = "$linux_esp_partuuid" ] || exit 99
            selected_method=$((selected_method + 1))
        fi
    done
    close_dialog || exit 99
done
[ "$selected_method" -eq 1 ] || exit 99
code=100
exit "$code"
