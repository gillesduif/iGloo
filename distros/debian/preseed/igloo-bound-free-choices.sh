#!/bin/sh
# Run by partman-auto after its disk inventory is ready. Expose only free space
# on the disk identified by the preseed early hook; never use global biggest_free.
# partman's base.sh reads unset optional variables (including PARTMAN_TEST).
# Nounset aborts this choice and silently leaves stock biggest_free selectable.
set -e
. /lib/partman/lib/base.sh
. /lib/partman/lib/auto-shared.sh
umask 077

blocked() {
    printf 'invalid\tNo usable free space on the selected iGloo disk\n'
}

disk=$(cat /tmp/igloo-bound-disk 2>/dev/null) || { blocked; exit 0; }
[ -b "$disk" ] || { blocked; exit 0; }
dev=$(dev_to_partman "$disk") || { blocked; exit 0; }
[ -n "$dev" ] && [ -d "$dev" ] || { blocked; exit 0; }

read -r extent_mode expected_start expected_size minimum_size < /tmp/igloo-bound-free-extent || { blocked; exit 0; }
case "$extent_mode" in exact|fallback) ;; *) blocked; exit 0 ;; esac
rm -f /tmp/igloo-bound-free-choice /tmp/igloo-bound-free-size
chosen_id=
chosen_size=
matches=0
cd "$dev"
open_dialog PARTITIONS
while { read_line num id size type fs path name; [ "$id" ]; }; do
    [ "$fs" = free ] && [ "$type" != unusable ] || continue
    if [ "$extent_mode" = exact ]; then
        case "$id" in *-*) ;; *) continue ;; esac
        [ "${id%%-*}" = "$expected_start" ] && [ "$size" = "$expected_size" ] || continue
    else
        longint_le "$minimum_size" "$size" || continue
    fi
    matches=$((matches + 1))
    chosen_id=$id
    chosen_size=$size
done
close_dialog

if [ "$matches" -ne 1 ]; then
    blocked
    exit 0
fi

choice="$dev//$chosen_id"
printf '%s\n' "$choice" > /tmp/igloo-bound-free-choice
printf '%s\n' "$chosen_size" > /tmp/igloo-bound-free-size
db_metaget partman-auto/text/use_biggest_free description
printf '%s\t%s\n' "$choice" "$RET"
