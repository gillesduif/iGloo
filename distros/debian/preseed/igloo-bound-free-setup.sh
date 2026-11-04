#!/bin/sh
# Run from the Debian installer initrd before partman builds its disk menu.
set -eu
umask 077

expected_guid="{{TARGET_DISK_GUID}}"
expected_bytes="{{TARGET_DISK_BYTES}}"
expected_free_offset="{{INSTALLER_FREE_OFFSET_BYTES}}"
expected_free_size="{{INSTALLER_FREE_SIZE_BYTES}}"
expected_linux_esp="{{LINUX_ESP_PARTITION_GUID}}"
minimum_install_bytes="{{MINIMUM_INSTALL_BYTES}}"
if [ "$expected_free_offset" = none ] && [ "$expected_free_size" = none ]; then
    # This fallback is only for the disposable direct-kernel lab path. The
    # reviewed Windows executor must supply the observed exact free extent.
    extent_mode=fallback
    expected_free_offset=0
    expected_free_size=0
else
    case "$expected_free_offset:$expected_free_size" in
        *[!0-9:]*|:*|*:) exit 1 ;;
    esac
    [ "$expected_free_offset" -gt 0 ] && [ "$expected_free_size" -gt 0 ] || exit 1
    printf '%s\n' "$expected_linux_esp" |
        grep -Eq '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$' || exit 1
    extent_mode=exact
fi
printf '%s %s %s %s\n' "$extent_mode" "$expected_free_offset" \
    "$expected_free_size" "$minimum_install_bytes" > /tmp/igloo-bound-free-extent
printf '%s\n' "$expected_linux_esp" | tr '[:upper:]' '[:lower:]' > /tmp/igloo-bound-linux-esp
guid_matches=0
size_matches=0
bound=''
for sys in /sys/block/*; do
    disk="/dev/${sys##*/}"
    [ -b "$disk" ] || continue
    guid=$(blkid -p -s PTUUID -o value "$disk" 2>/dev/null | tr '[:upper:]' '[:lower:]' || true)
    [ "$guid" = "$expected_guid" ] || continue
    guid_matches=$((guid_matches + 1))
    bytes=$(blockdev --getsize64 "$disk" 2>/dev/null || true)
    [ "$bytes" = "$expected_bytes" ] || continue
    size_matches=$((size_matches + 1))
    bound="$disk"
done
if [ "$guid_matches" -ne 1 ] || [ "$size_matches" -ne 1 ]; then
    logger -t igloo 'selected GPT disk identity or size is ambiguous'
    exit 1
fi

echo "$bound" > /tmp/igloo-bound-disk
echo "$expected_guid $expected_bytes" > /tmp/igloo-bound-identity
choice_dir=/lib/partman/automatically_partition/igloo_bound_free
mkdir -p "$choice_dir"
cp /igloo-bound-free-choices.sh "$choice_dir/choices"
cp /igloo-bound-free-do-option.sh "$choice_dir/do_option"
chmod 0755 "$choice_dir/choices" "$choice_dir/do_option"

# cdebconf falls back to the first stock guided choice when a preseeded choice
# is unavailable. Remove every other automated/manual choice from this
# disposable installer session, so a broken bound helper cannot fall through
# to global biggest_free or an entire-disk method.
disabled_dir=/tmp/igloo-disabled-partman-options
mkdir -p "$disabled_dir"
for option in /lib/partman/automatically_partition/*; do
    [ -d "$option" ] || continue
    [ "$option" = "$choice_dir" ] && continue
    name=${option##*/}
    if [ -e "$disabled_dir/$name" ]; then
        logger -t igloo 'partman option reappeared after quarantine'
        exit 1
    fi
    mv "$option" "$disabled_dir/$name"
done
