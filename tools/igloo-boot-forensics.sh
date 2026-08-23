#!/usr/bin/env bash
# =============================================================================
# igloo-boot-forensics - why does a sibling Linux stop booting after an install?
# -----------------------------------------------------------------------------
#     sudo bash igloo-boot-forensics.sh
#
# Mounts every Linux partition READ-ONLY, one at a time, and records what each
# installed system says about where its own disks are. Answers, with files
# rather than reasoning:
#
#   * did the GPT slot numbers move, and does the numbering still match the
#     physical order on disk;
#   * does any system name a partition by DEVICE PATH instead of by UUID -
#     in fstab, crypttab, the kernel command line, grub.cfg, a BLS entry, or
#     inside its initramfs;
#   * which UUIDs each system expects, and whether those still exist.
#
# Read-only throughout: every mount is -o ro and nothing is written outside the
# report file. Safe to run on a machine you are trying not to break further.
# =============================================================================
set -u

TS="$(date -u '+%Y%m%d-%H%M%SZ')"
HOST="$(hostname 2>/dev/null || echo unknown)"

#   Where to write. A USB stick if one is mounted, otherwise the home dir.
DEST=""
for c in /media/*/* /run/media/*/*; do
    [ -d "$c" ] && [ -w "$c" ] && { DEST="$c"; break; }
done
if [ -z "$DEST" ]; then
    DEST="$(getent passwd "${SUDO_USER:-root}" | cut -d: -f6)"
    [ -n "$DEST" ] && [ -d "$DEST" ] || DEST=/tmp
fi
OUT="$DEST/igloo-boot-forensics-$HOST-$TS.txt"

MNT_BASE=/mnt/igloo-forensics
MNT="$MNT_BASE"
mkdir -p "$MNT_BASE"
cleanup() { umount -R "$MNT_BASE" 2>/dev/null; rmdir "$MNT_BASE" 2>/dev/null; }
trap cleanup EXIT

say() { printf '%s\n' "$*" | tee -a "$OUT"; }
sec() { printf '\n===== %s\n' "$*" | tee -a "$OUT"; }
run() {
    printf '\n$ %s\n' "$*" >> "$OUT"
    "$@" >> "$OUT" 2>&1 || printf '(exit %d)\n' "$?" >> "$OUT"
}

: > "$OUT"
say "iGloo boot forensics"
say "host $HOST   $(date '+%F %T')"
say "report: $OUT"

#   1. The partition table, with slot numbers AND start offsets.
# A slot order that no longer follows the physical order is the fingerprint of
# a table that some tool rewrote rather than appended to.
sec "1. Partition tables"
for disk in /dev/nvme?n? /dev/sd? /dev/vd?; do
    [ -b "$disk" ] || continue
    run lsblk -o NAME,SIZE,FSTYPE,LABEL,PARTLABEL,PARTUUID,UUID "$disk"
    if command -v sgdisk >/dev/null 2>&1; then
        run sgdisk -p "$disk"
    elif command -v parted >/dev/null 2>&1; then
        run parted -s "$disk" unit s print
    else
        run fdisk -l "$disk"
    fi
done
run blkid

# Kernel name against PARTUUID, one line per partition. PARTUUID is fixed in the
# table; the nvmeXnY name is assigned at probe time and can differ between boots
# when a machine has more than one NVMe. Comparing this block across two reports
# is what makes such a swap visible - and a swap breaks anything that stored a
# device path instead of a UUID.
sec "1b. Kernel name -> PARTUUID (compare this between boots)"
run lsblk -rno NAME,PARTUUID,SIZE,FSTYPE

#   2. Firmware boot entries.
sec "2. UEFI"
if [ -d /sys/firmware/efi ]; then
    run efibootmgr -v
else
    say "(legacy boot, no UEFI variables)"
fi

#   3. Every installed system, from its own point of view.
sec "3. Installed systems"
# Collected per partition so a system whose /boot lives elsewhere still shows
# up: the /boot partition is examined in its own right, not only through the
# root that mounts it.
for part in $(lsblk -rno NAME,FSTYPE | awk '$2 ~ /^(ext[234]|btrfs|xfs|vfat)$/ {print $1}'); do
    dev="/dev/$part"
    MNT="$MNT_BASE"
    umount -R "$MNT_BASE" 2>/dev/null

    # A partition the running system already uses cannot be mounted again
    # read-only - the kernel refuses with "would change RO state" - so examine it
    # where it is. Skipping those silently is how the main ESP, the one holding
    # every distribution's bootloader, went unexamined twice.
    MOUNTED_ELSEWHERE=0
    if mount -o ro "$dev" "$MNT" 2>/dev/null; then
        :
    else
        EXISTING="$(findmnt -no TARGET --source "$dev" 2>/dev/null | head -1)"
        [ -n "$EXISTING" ] || continue
        MNT="$EXISTING"
        MOUNTED_ELSEWHERE=1
    fi

    IS_ROOT=0; IS_BOOT=0; IS_ESP=0
    [ -f "$MNT/etc/os-release" ] && IS_ROOT=1
    { [ -d "$MNT/grub2" ] || [ -d "$MNT/grub" ] || [ -d "$MNT/loader/entries" ] \
      || ls "$MNT"/initramfs-*.img "$MNT"/initrd.img-* >/dev/null 2>&1; } && IS_BOOT=1
    # An EFI system partition has EFI/<distro>/ at its root, not grub2/, so the
    # test above misses it - and that is where the boot chain starts: the stub
    # grub.cfg that points at the real one, and on a BLS system the grubenv
    # holding kernelopts. Skipping it left the first link unexamined.
    [ -d "$MNT/EFI" ] && IS_ESP=1
    [ "$IS_ROOT" = 1 ] || [ "$IS_BOOT" = 1 ] || [ "$IS_ESP" = 1 ] \
        || { [ "$MOUNTED_ELSEWHERE" = 0 ] && umount "$MNT" 2>/dev/null; continue; }

    if [ "$IS_ESP" = 1 ]; then
        printf '\n-------------------------------------------------------------\n' >> "$OUT"
        say "  $dev  EFI system partition"
        run find "$MNT/EFI" -maxdepth 2 -type d
        for cfg in "$MNT"/EFI/*/grub.cfg "$MNT"/EFI/*/BOOTX64.CSV; do
            [ -f "$cfg" ] || continue
            printf '\n$ cat %s\n' "$cfg" >> "$OUT"
            cat "$cfg" >> "$OUT" 2>&1
        done
        for env in "$MNT"/EFI/*/grubenv; do
            [ -f "$env" ] || continue
            printf '\n$ cat %s\n' "$env" >> "$OUT"
            tr -d '#' < "$env" | grep -vE '^\s*$' >> "$OUT" 2>&1
        done
        printf '\n$ device paths anywhere on this ESP\n' >> "$OUT"
        grep -ra -oE '/dev/(nvme[0-9]+n[0-9]+p[0-9]+|sd[a-z][0-9]+)' "$MNT/EFI" \
            2>/dev/null | sort -u | head -20 >> "$OUT"
        [ "$MOUNTED_ELSEWHERE" = 0 ] && umount -R "$MNT" 2>/dev/null
        continue
    fi

    printf '\n-------------------------------------------------------------\n' >> "$OUT"
    if [ "$IS_ROOT" = 1 ]; then
        NAME="$(sed -n 's/^PRETTY_NAME="\(.*\)"$/\1/p' "$MNT/etc/os-release")"
        say "  $dev  ROOT  $NAME"
        run cat "$MNT/etc/fstab"
        [ -s "$MNT/etc/crypttab" ] && run cat "$MNT/etc/crypttab"
        [ -f "$MNT/etc/default/grub" ] && run cat "$MNT/etc/default/grub"
        for f in "$MNT"/etc/default/grub.d/*.cfg; do
            [ -f "$f" ] && run cat "$f"
        done
        # Anything at all in /etc naming a disk by path. This is the question:
        # a UUID survives renumbering, a device path does not.
        printf '\n$ grep -rn "/dev/\\(nvme\\|sd\\|vd\\)" %s/etc/\n' "$MNT" >> "$OUT"
        grep -rn '/dev/\(nvme\|sd[a-z]\|vd[a-z]\)' "$MNT/etc/" 2>/dev/null \
            | grep -v '^\s*#' | head -40 >> "$OUT"

        #   The system's own account of its last boots.
        # A system that will not start still wrote down why, and that record sits
        # on the very partition being examined. Reading it here is the whole point
        # of mounting: it needs no working boot of the system under test.
        if [ -d "$MNT/var/log/journal" ]; then
            printf '\n$ journalctl -D %s/var/log/journal --list-boots\n' "$MNT" >> "$OUT"
            journalctl -D "$MNT/var/log/journal" --list-boots --no-pager 2>&1 \
                | tail -8 >> "$OUT"

            # -b -1 is the boot before the newest recorded one. On a system that
            # hangs, the newest IS the failed one, so both are taken.
            for b in 0 -1; do
                printf '\n$ boot %s: warnings and above\n' "$b" >> "$OUT"
                journalctl -D "$MNT/var/log/journal" -b "$b" -p warning \
                    --no-pager 2>/dev/null | tail -50 >> "$OUT"
                printf '\n$ boot %s: jobs, timeouts and device waits\n' "$b" >> "$OUT"
                journalctl -D "$MNT/var/log/journal" -b "$b" --no-pager 2>/dev/null \
                    | grep -iE 'job .*(running|timed out|failed)|dependency|\.device|timed out waiting' \
                    | tail -30 >> "$OUT"
            done
        else
            printf '\n(no persistent journal in %s/var/log/journal)\n' "$MNT" >> "$OUT"
        fi

        #   What could be waiting on a device, if the journal says nothing.
        # A .device unit with no timeout is not a mount - mounts default to 90 s.
        # So the thing waiting is a unit that names the device itself, and these
        # three directories are where a distribution or an installer would put it.
        printf '\n$ units naming a device or a mount dependency\n' >> "$OUT"
        grep -rlE 'RequiresMountsFor|\.device|What=/dev/' \
            "$MNT/etc/systemd/system/" "$MNT/etc/systemd/network/" 2>/dev/null \
            | head -20 >> "$OUT"
        grep -rhE 'RequiresMountsFor=|What=|After=.*\.device|Requires=.*\.device' \
            "$MNT/etc/systemd/system/" 2>/dev/null | head -20 >> "$OUT"

        printf '\n$ fstab options that disable the device timeout\n' >> "$OUT"
        grep -nE 'device-timeout|nofail|x-systemd' "$MNT/etc/fstab" 2>/dev/null >> "$OUT"

        printf '\n$ dracut configuration\n' >> "$OUT"
        for f in "$MNT"/etc/dracut.conf "$MNT"/etc/dracut.conf.d/*; do
            [ -f "$f" ] || continue
            printf '  --- %s\n' "$f" >> "$OUT"
            grep -vE '^\s*(#|$)' "$f" 2>/dev/null | sed 's/^/    /' >> "$OUT"
        done
    else
        say "  $dev  BOOT partition"
    fi

    #   grub.cfg: only the lines that name a device.
    for cfg in "$MNT/grub2/grub.cfg" "$MNT/grub/grub.cfg" \
               "$MNT/boot/grub2/grub.cfg" "$MNT/boot/grub/grub.cfg"; do
        [ -f "$cfg" ] || continue
        printf '\n$ grep -nE "root=|resume=|search .*--set" %s\n' "$cfg" >> "$OUT"
        grep -nE 'root=|resume=|search .*--set' "$cfg" 2>/dev/null | head -40 >> "$OUT"
    done

    #   grubenv. On Fedora with BootLoaderSpec this holds kernelopts, and
    #   grub.cfg only sets its own value when grubenv has none - so whatever is
    #   here overrides the UUID in the generated config. It lives next to
    #   grub.cfg on /boot, not on the ESP, which is why the ESP branch above
    #   does not find it.
    for env in "$MNT"/grub2/grubenv "$MNT"/grub/grubenv \
               "$MNT"/boot/grub2/grubenv "$MNT"/boot/grub/grubenv; do
        [ -f "$env" ] || continue
        printf '\n$ cat %s\n' "$env" >> "$OUT"
        tr -d '#' < "$env" | grep -vE '^\s*$' >> "$OUT" 2>&1
    done

    #   BootLoaderSpec entries (Fedora).
    for d in "$MNT/loader/entries" "$MNT/boot/loader/entries"; do
        [ -d "$d" ] || continue
        for e in "$d"/*.conf; do
            [ -f "$e" ] && run cat "$e"
        done
    done

    #   The initramfs. dracut in hostonly mode bakes the layout in, and nothing
    #   on the mounted filesystem shows it - only the archive does. An initramfs
    #   is a compressed cpio, often with a small UNCOMPRESSED cpio in front, so
    #   both halves are searched. Say which tool did the work: an empty result
    #   from a missing decompressor looks exactly like a clean image, and that
    #   ambiguity already cost one round.
    for img in "$MNT"/initramfs-*.img "$MNT"/initrd.img-* \
               "$MNT"/boot/initramfs-*.img "$MNT"/boot/initrd.img-*; do
        [ -f "$img" ] || continue
        printf '\n--- device paths inside %s\n' "$img" >> "$OUT"

        printf '    raw (uncompressed early cpio):\n' >> "$OUT"
        grep -a -oE '/dev/(nvme[0-9]+n[0-9]+p[0-9]+|sd[a-z][0-9]+)' "$img" 2>/dev/null \
            | sort -u | sed 's/^/      /' >> "$OUT"

        DECOMP=""
        for t in zstdcat xzcat zcat lz4cat lzop; do
            command -v "$t" >/dev/null 2>&1 || continue
            if "$t" < "$img" 2>/dev/null | head -c 4096 | grep -qa .; then
                DECOMP="$t"; break
            fi
        done
        if [ -n "$DECOMP" ]; then
            printf '    body (decompressed with %s):\n' "$DECOMP" >> "$OUT"
            "$DECOMP" < "$img" 2>/dev/null \
                | grep -a -oE '/dev/(nvme[0-9]+n[0-9]+p[0-9]+|sd[a-z][0-9]+)' \
                | sort -u | sed 's/^/      /' >> "$OUT"
        else
            printf '    body: NOT SEARCHED - no working decompressor found\n' >> "$OUT"
            printf '          (install zstd / xz-utils and re-run)\n' >> "$OUT"
        fi

        if command -v lsinitrd >/dev/null 2>&1; then
            printf '    dracut config files:\n' >> "$OUT"
            for f in /etc/cmdline.d/*.conf /etc/fstab.sys /etc/conf.d/resume.conf; do
                lsinitrd -f "$f" "$img" 2>/dev/null | sed 's/^/      /' >> "$OUT"
            done
        fi
    done

    [ "$MOUNTED_ELSEWHERE" = 0 ] && umount -R "$MNT" 2>/dev/null
done

#   4. This system's own view, live.
sec "4. Running system"
run cat /proc/cmdline
run findmnt -no SOURCE,FSTYPE /

# Both NVMe controllers are probed in parallel and whichever answers first
# becomes nvme0, which is why the names can differ between boots. A controller
# that answers slowly or drops out shows up here as a reset or a timeout - and
# that is also a device a boot could wait on forever.
sec "4b. NVMe probe and errors, this boot"
run bash -c 'dmesg 2>/dev/null | grep -iE "nvme|pcie bus error|link down" | head -40'
for d in /sys/class/nvme/nvme*; do
    [ -d "$d" ] || continue
    printf '\n%s -> %s  model: %s\n' "$(basename "$d")" \
        "$(readlink -f "$d" | sed 's|.*/||')" \
        "$(cat "$d/model" 2>/dev/null)" >> "$OUT"
done
if [ -f /var/log/igloo/agent.log ]; then
    printf '\n$ iGloo agent: partition + boot lines\n' >> "$OUT"
    grep -inE 'sfdisk|partition|efibootmgr|boot order|grub' /var/log/igloo/agent.log \
        2>/dev/null | tail -40 >> "$OUT"
else
    say "(no /var/log/igloo/agent.log on this system)"
fi

sync
echo
echo "==============================================================="
echo " Klaar. Stuur dit bestand door:"
echo "   $OUT"
echo "==============================================================="
