#!/bin/sh
set -eu
umask 077

if [ "${1:-}" = "--install" ]; then
    seed_partuuid='' seed_diskguid='' linux_esp_partuuid=''
    part_count=0 disk_count=0 esp_count=0
    for arg in $(cat /proc/cmdline); do
        case "$arg" in
            igloo.seedpartuuid=*) seed_partuuid=${arg#*=}; part_count=$((part_count + 1)) ;;
            igloo.seeddiskguid=*) seed_diskguid=${arg#*=}; disk_count=$((disk_count + 1)) ;;
            igloo.linuxesppartuuid=*) linux_esp_partuuid=${arg#*=}; esp_count=$((esp_count + 1)) ;;
        esac
    done
    test "$part_count" -eq 1
    test "$disk_count" -eq 1
    test "$esp_count" -eq 1
    printf '%s\n' "$seed_partuuid" | grep -Eq '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$'
    printf '%s\n' "$seed_diskguid" | grep -Eq '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$'
    printf '%s\n' "$linux_esp_partuuid" | grep -Eq '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$'
    seed_partuuid=$(printf '%s' "$seed_partuuid" | tr '[:upper:]' '[:lower:]')
    seed_diskguid=$(printf '%s' "$seed_diskguid" | tr '[:upper:]' '[:lower:]')
    linux_esp_partuuid=$(printf '%s' "$linux_esp_partuuid" | tr '[:upper:]' '[:lower:]')
    # This runs after grub-installer. Refuse a Ubiquity reroute to an existing
    # ESP or a second fstab EFI entry before enabling the first-boot agent.
    esp_path=$(readlink -f "/dev/disk/by-partuuid/$linux_esp_partuuid")
    test -b "$esp_path"
    test "$(readlink -f "$(findmnt -rn -o SOURCE --mountpoint /target/boot/efi)")" = "$esp_path"
    esp_uuid=$(blkid -p -s UUID -o value "$esp_path")
    test -n "$esp_uuid"
    fstab_esp_source=$(awk '$1 !~ /^#/ && $2 == "/boot/efi" {print $1}' /target/etc/fstab)
    test "$fstab_esp_source" = "UUID=$esp_uuid" || test "$fstab_esp_source" = "$esp_path"
    test -f /run/igloo-protected-esp-hashes
    if test -s /run/igloo-protected-esp-hashes; then
        sha256sum --check --status /run/igloo-protected-esp-hashes
    fi
    mkdir -p /target/opt/igloo /target/var/lib/igloo \
        /target/var/log/igloo /target/etc/systemd/system/multi-user.target.wants
    # User-session hooks execute scripts under /opt/igloo. The installer runs
    # with umask 077, so make only this code directory traversable; keep the
    # manifest and retry state under /var/lib/igloo private.
    chmod 0755 /target/opt /target/opt/igloo
    printf '%s\n' "$seed_partuuid" > /target/var/lib/igloo/selected-seed-partuuid
    printf '%s\n' "$seed_diskguid" > /target/var/lib/igloo/selected-seed-diskguid
    chmod 0600 /target/var/lib/igloo/selected-seed-partuuid \
        /target/var/lib/igloo/selected-seed-diskguid
    printf '%s  %s\n' '{{IGLOO_SEED_VERIFIER_SHA256}}' \
        /run/igloo-seed/igloo-agent/igloo-verify-seed.py | sha256sum --check --status -
    python3 /run/igloo-seed/igloo-agent/igloo-verify-seed.py \
        /target/var/lib/igloo/selected-seed-partuuid \
        /target/var/lib/igloo/selected-seed-diskguid /run/igloo-seed
    printf '%s  %s\n' '{{IGLOO_BOOTSTRAP_SERVICE_SHA256}}' \
        /run/igloo-seed/igloo-agent/igloo-bootstrap.service | sha256sum --check --status -
    install -m 0700 /run/igloo-seed/igloo-agent/igloo-bootstrap.sh \
        /target/opt/igloo/igloo-bootstrap.sh
    install -m 0644 /run/igloo-seed/igloo-agent/igloo-bootstrap.service \
        /target/etc/systemd/system/igloo-bootstrap.service
    ln -sf /etc/systemd/system/igloo-bootstrap.service \
        /target/etc/systemd/system/multi-user.target.wants/igloo-bootstrap.service
    # This shell is executing from the seed mount, so Casper releases that
    # live mount during shutdown. An explicit unmount here returns EBUSY.
    exit 0
fi

mkdir -p /opt/igloo /var/lib/igloo /var/log/igloo /run/igloo-seed
chmod 0755 "$(dirname /opt/igloo)" /opt/igloo
exec >> /var/log/igloo/bootstrap.log 2>&1
set -x

if test -f /var/lib/igloo/.done; then
    exec /opt/igloo/first-boot.sh
fi

seed_partuuid=$(cat /var/lib/igloo/selected-seed-partuuid)
printf '%s\n' "$seed_partuuid" | grep -Eq '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$'
modprobe vfat 2>/dev/null || true
seed_device=$(readlink -f "/dev/disk/by-partuuid/$seed_partuuid")
test -b "$seed_device"
if ! mountpoint -q /run/igloo-seed; then
    mount -t vfat -o ro "$seed_device" /run/igloo-seed
fi
# A failed or cancelled pass can leave its seed mounted in this boot. Never
# stack another mount over it or consume a different or writable seed on retry.
mounted_seed=$(findmnt -rn -o SOURCE --mountpoint /run/igloo-seed)
test "$(readlink -f "$mounted_seed")" = "$seed_device"
findmnt -rn -o OPTIONS --mountpoint /run/igloo-seed |
    grep -Eq '(^|,)ro(,|$)'

# This helper is pinned by this script's own installer-time SHA-256 check.
printf '%s  %s\n' '{{IGLOO_SEED_VERIFIER_SHA256}}' \
    /run/igloo-seed/igloo-agent/igloo-verify-seed.py | sha256sum --check --status -
install -m 0700 /run/igloo-seed/igloo-agent/igloo-verify-seed.py /opt/igloo/igloo-verify-seed.py
python3 /opt/igloo/igloo-verify-seed.py \
    /var/lib/igloo/selected-seed-partuuid \
    /var/lib/igloo/selected-seed-diskguid /run/igloo-seed \
    > /run/igloo-selected-media

test -s /run/igloo-seed/migration-manifest.json
test -s /run/igloo-seed/igloo-seed-identity.json
for file in agent.py first-boot.sh display-apply.py display-apply-gnome.py \
    igloo_boot.py igloo-first-boot.service grub-theme-stylish-1080p.tar.gz \
    grub-theme-stylish-4k.tar.gz; do
    test -s "/run/igloo-seed/igloo-agent/$file"
done

cp /run/igloo-seed/igloo-agent/agent.py /opt/igloo/agent.py
cp /run/igloo-seed/igloo-agent/first-boot.sh /opt/igloo/first-boot.sh
cp /run/igloo-seed/igloo-agent/display-apply.py /opt/igloo/display-apply.py
cp /run/igloo-seed/igloo-agent/display-apply-gnome.py /opt/igloo/display-apply-gnome.py
cp /run/igloo-seed/igloo-agent/igloo_boot.py /opt/igloo/igloo_boot.py
cp /run/igloo-seed/igloo-agent/grub-theme-stylish-*.tar.gz /opt/igloo/
while IFS= read -r selected_media; do
    case "$selected_media" in
        igloo-avatar.jpg) ;;
        *)
            printf '%s\n' "$selected_media" |
                LC_ALL=C grep -Eiq '^igloo-wallpaper\.[A-Za-z0-9]{2,8}$'
            ;;
    esac
    test -s "/run/igloo-seed/$selected_media"
    install -m 0644 "/run/igloo-seed/$selected_media" "/opt/igloo/$selected_media"
done < /run/igloo-selected-media

if test -s /var/lib/igloo/manifest.retry.json; then
    install -m 0600 /var/lib/igloo/manifest.retry.json /var/lib/igloo/manifest.json
else
    install -m 0600 /run/igloo-seed/migration-manifest.json /var/lib/igloo/manifest.json
fi
install -m 0600 /run/igloo-seed/igloo-seed-identity.json /var/lib/igloo/seed-identity.json
chmod 0755 /opt/igloo/agent.py /opt/igloo/first-boot.sh \
    /opt/igloo/display-apply.py /opt/igloo/display-apply-gnome.py
# The user-session credential hook imports agent.py, which imports this
# non-secret sibling module before its own logging is configured.
chmod 0644 /opt/igloo/igloo_boot.py

# The agent consumes selected staged files from the read-only seed mount.
exec /opt/igloo/first-boot.sh
