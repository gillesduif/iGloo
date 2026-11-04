#!/bin/sh
# The only writer entry for the iGloo choice. Recheck GPT identity, disk size,
# selected partman device and the exact free extent before autopartition runs.
. /lib/partman/lib/base.sh
. /lib/partman/lib/auto-shared.sh

[ "$#" -eq 1 ] || exit 99
bound=$(cat /tmp/igloo-bound-disk 2>/dev/null) || exit 99
selected=$(cat /tmp/igloo-bound-free-choice 2>/dev/null) || exit 99
saved_size=$(cat /tmp/igloo-bound-free-size 2>/dev/null) || exit 99
read -r extent_mode expected_start expected_size minimum_size < /tmp/igloo-bound-free-extent || exit 99
read -r expected_guid expected_bytes < /tmp/igloo-bound-identity || exit 99
[ "$1" = "$selected" ] && [ -b "$bound" ] || exit 99
case "$extent_mode" in exact|fallback) ;; *) exit 99 ;; esac

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

found=0
eligible=0
cd "$dev" || exit 99
open_dialog PARTITIONS || exit 99
while { read_line num current_id size type fs path name; [ "$current_id" ]; }; do
    [ "$fs" = free ] && [ "$type" != unusable ] || continue
    if [ "$extent_mode" = exact ]; then
        case "$current_id" in *-*) ;; *) continue ;; esac
        [ "${current_id%%-*}" = "$expected_start" ] && [ "$size" = "$expected_size" ] || continue
    else
        longint_le "$minimum_size" "$size" || continue
    fi
    eligible=$((eligible + 1))
    [ "$current_id" = "$id" ] && [ "$size" = "$saved_size" ] && found=$((found + 1))
done
close_dialog || exit 99
[ "$eligible" -eq 1 ] && [ "$found" -eq 1 ] || exit 99

free_id=$id

# The reviewed free-extent path requires the Windows-prepared Linux ESP token.
# The fallback disposable direct-kernel lab can run without one. partman-efi marks
# every existing ESP as method=efi; partman-auto's $reusemethod then chooses
# the last eligible one, while fstab.d/efi uses the first remaining method=efi.
# Restrict both to the exact precreated ESP before partman-auto runs. Keep
# old_efi on the Windows ESP so efi_sync_flag preserves its GPT ESP flag.
esp_partuuid=
esp_tokens=0
for arg in $(cat /proc/cmdline); do
    case "$arg" in
        igloo.linuxesppartuuid=*)
            esp_tokens=$((esp_tokens + 1))
            esp_partuuid=${arg#*=}
            ;;
    esac
done
[ "$esp_tokens" -le 1 ] || exit 99
if [ "$extent_mode" = exact ] && [ "$esp_tokens" -ne 1 ]; then
    logger -t igloo 'reviewed free extent has no exact Linux ESP PARTUUID'
    exit 99
fi
if [ "$esp_tokens" -eq 1 ]; then
    printf '%s\n' "$esp_partuuid" |
        grep -Eq '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$' || exit 99
    esp_partuuid=$(printf '%s' "$esp_partuuid" | tr '[:upper:]' '[:lower:]')
    if [ "$extent_mode" = exact ]; then
        expected_linux_esp=$(cat /tmp/igloo-bound-linux-esp 2>/dev/null) || exit 99
        [ "$esp_partuuid" = "$expected_linux_esp" ] || exit 99
    fi
    esp_type=c12a7328-f81f-11d2-ba4b-00a0c93ec93b
    esp_matches=0
    esp_id=
    for esp_dev in "$DEVICES"/*; do
        [ -d "$esp_dev" ] || continue
        cd "$esp_dev" || exit 99
        open_dialog PARTITIONS || exit 99
        while { read_line num partition_id partition_size type partition_fs path name; [ "$partition_id" ]; }; do
            [ "$partition_fs" != free ] && [ -b "$path" ] || continue
            partuuid=$(blkid -p -s PART_ENTRY_UUID -o value "$path" 2>/dev/null |
                tr '[:upper:]' '[:lower:]')
            [ "$partuuid" = "$esp_partuuid" ] || continue
            esp_matches=$((esp_matches + 1))
            [ "$esp_dev" = "$dev" ] && [ "$partition_fs" = fat32 ] &&
                [ "$partition_size" = 1073741824 ] || exit 99
            part_type=$(blkid -p -s PART_ENTRY_TYPE -o value "$path" 2>/dev/null |
                tr '[:upper:]' '[:lower:]')
            [ "$part_type" = "$esp_type" ] || exit 99
            esp_id=$partition_id
        done
        close_dialog || exit 99
    done
    [ "$esp_matches" -eq 1 ] && [ -n "$esp_id" ] || exit 99

    cd "$dev" || exit 99
    esp_checked=0
    open_dialog PARTITIONS || exit 99
    while { read_line num partition_id partition_size type partition_fs path name; [ "$partition_id" ]; }; do
        [ "$partition_fs" != free ] && [ -b "$path" ] || continue
        part_type=$(blkid -p -s PART_ENTRY_TYPE -o value "$path" 2>/dev/null |
            tr '[:upper:]' '[:lower:]')
        [ "$part_type" = "$esp_type" ] || continue
        [ -f "$partition_id/old_efi" ] && [ ! -e "$partition_id/format" ] || exit 99
        [ -f "$partition_id/method" ] &&
            [ "$(cat "$partition_id/method")" = efi ] || exit 99
        if [ "$partition_id" = "$esp_id" ]; then
            esp_checked=$((esp_checked + 1))
        else
            if [ -e "$partition_id/method-old" ]; then
                [ "$(cat "$partition_id/method-old")" = efi ] || exit 99
                rm "$partition_id/method-old" || exit 99
            fi
            rm "$partition_id/method" || exit 99
        fi
    done
    close_dialog || exit 99
    [ "$esp_checked" -eq 1 ] || exit 99
    logger -t igloo "partman ESP method bound to PARTUUID $esp_partuuid"
fi

autopartition "$dev" "$free_id"
code=$?
[ "$code" -eq 255 ] && code=99

if [ "$code" -eq 0 ] && [ "$esp_tokens" -eq 1 ]; then
    # A recipe change must not silently allocate a second ESP or select the
    # existing Windows ESP for /boot/efi.
    selected_method=0
    cd "$dev" || exit 99
    open_dialog PARTITIONS || exit 99
    while { read_line num partition_id partition_size type partition_fs path name; [ "$partition_id" ]; }; do
        [ "$partition_fs" != free ] || continue
        if [ "$partition_id" = "$esp_id" ]; then
            [ -b "$path" ] || exit 99
            partuuid=$(blkid -p -s PART_ENTRY_UUID -o value "$path" 2>/dev/null |
                tr '[:upper:]' '[:lower:]')
            [ "$partuuid" = "$esp_partuuid" ] || exit 99
            [ -f "$partition_id/method" ] &&
                [ "$(cat "$partition_id/method")" = efi ] &&
                [ ! -e "$partition_id/format" ] || exit 99
            selected_method=$((selected_method + 1))
        elif [ -f "$partition_id/method" ]; then
            [ "$(cat "$partition_id/method")" != efi ] || exit 99
        fi
    done
    close_dialog || exit 99
    [ "$selected_method" -eq 1 ] || exit 99
fi
exit "$code"
