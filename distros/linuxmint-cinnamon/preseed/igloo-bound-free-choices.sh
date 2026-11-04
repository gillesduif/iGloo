#!/bin/sh
# Keep the Ubiquity biggest_free choice name, but scan only the exact target.
. /lib/partman/lib/base.sh
. /lib/partman/lib/auto-shared.sh
umask 077

disk=$(cat /tmp/igloo-bound-disk 2>/dev/null) || exit 99
[ -b "$disk" ] || exit 99
dev=$(dev_to_partman "$disk") || exit 99
[ -n "$dev" ] && [ -d "$dev" ] || exit 99

# A reviewed handoff binds the exact byte offset/size from the post-creation
# disk inventory. Ordinary lab rendering remains fail closed on multiple
# installable gaps; it does not infer a Windows preparation receipt.
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
selected_id=''
selected_size=0
cd "$dev"
open_dialog PARTITIONS
while { read_line num id size type fs path name; [ "$id" ]; }; do
    if installable_free "$id" "$size" "$fs" "$type"; then
        eligible_count=$((eligible_count + 1))
        selected_id=$id
        selected_size=$size
    fi
done
close_dialog

[ "$eligible_count" -eq 1 ] && [ -n "$selected_id" ] || exit 99
choice="$dev//$selected_id"
printf '%s\n' "$choice" > /tmp/igloo-bound-free-choice
printf '%s\n' "$selected_size" > /tmp/igloo-bound-free-size
if [ -c /dev/ttyS0 ]; then
    printf 'igloo-bound-free: sole installable extent %s size=%s\n' "$choice" "$selected_size" > /dev/ttyS0
fi
db_metaget partman-auto/text/use_biggest_free description
printf '%s\t%s\n' "$choice" "$RET"
