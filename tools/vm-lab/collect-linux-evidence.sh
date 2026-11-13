#!/usr/bin/env bash
# Collects the evidence of one iGloo run inside the installed Linux system. Reads only.
#
# Usage, as root after the first desktop login has settled:
#   bash collect-linux-evidence.sh --run r6-debian [--phase first-boot] [--user NAME]
#                                  [--out DIR] [--testdata DIR]
#
# Writes one folder per call below <out>/<run>-<date>/ and nothing anywhere else.
# It mounts nothing, starts no service and copies no manifest, key file or browser database.
# Copied logs pass a filter that replaces password hashes and key values.
set -u
umask 077

run_name=''
phase='first-boot'
target_user=''
out_root=''
testdata=''
max_copy_bytes=$((64 * 1024 * 1024))

while [ $# -gt 0 ]; do
    [ $# -ge 2 ] || { echo "Missing value for $1." >&2; exit 2; }
    case "$1" in
        --run) run_name=$2 ;;
        --phase) phase=$2 ;;
        --user) target_user=$2 ;;
        --out) out_root=$2 ;;
        --testdata) testdata=$2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
    shift 2
done

case "$run_name" in ''|*[!A-Za-z0-9._-]*) echo 'Pass --run NAME, for example --run r6-debian.' >&2; exit 2 ;; esac
case "$phase" in ''|*[!A-Za-z0-9._-]*) echo 'The phase may hold letters, digits, dot, dash and underscore.' >&2; exit 2 ;; esac
[ "$(id -u)" -eq 0 ] || { echo 'Run this script as root (sudo, or su -).' >&2; exit 2; }

if [ -z "$target_user" ]; then target_user=${SUDO_USER:-}; fi
if [ -z "$target_user" ] || [ "$target_user" = root ]; then
    target_user=$(getent passwd 1000 | cut -d: -f1)
fi
user_home=$(getent passwd "$target_user" | cut -d: -f6)
[ -n "$user_home" ] && [ -d "$user_home" ] || { echo "No home directory for user '$target_user'; pass --user NAME." >&2; exit 2; }
[ -n "$out_root" ] || out_root="$user_home/igloo-evidence"
[ -n "$testdata" ] || testdata="$user_home/Documents/igloo-testdata"

stamp_day=$(date +%Y%m%d)
stamp_time=$(date +%H%M%S)
run_dir="$out_root/$run_name-$stamp_day"
out="$run_dir/linux-$phase-$stamp_time"
case "$out/" in
    /run/igloo-seed*|/mnt/igloo_ntfs*|/boot/*|"$testdata"/*) echo 'The output folder must not be on the seed, the Windows volume, /boot or the test data.' >&2; exit 2 ;;
esac
probe=$out_root
while [ ! -e "$probe" ]; do probe=$(dirname "$probe"); done
case "$(findmnt -no LABEL -T "$probe" 2>/dev/null)" in
    OEMDRV|CIDATA|IGLOOISO) echo 'The output folder must not be on an installer partition.' >&2; exit 2 ;;
esac
root_was_there=1
[ -d "$out_root" ] || root_was_there=0
mkdir -p "$out" || exit 1

facts="$out/.facts"
problems="$out/.problems"
: > "$facts"; : > "$problems"
fact() { printf '%s\n' "$*" >> "$facts"; }
problem() { printf '%s\n' "$*" >> "$problems"; }

keys='(psk|password|passwd|passphrase|pwd|secret)[[:alnum:]_-]*"?[[:space:]]*[=:][[:space:]]*'
redact() {
    LC_ALL=C sed -E \
        -e 's/\$(1|2[abxy]?|5|6|7|y|gy|argon2(id|i|d))\$[^[:space:]"'"'"'\\,;]+/[REDACTED-HASH]/g' \
        -e 's/('"$keys"')([0-9]{1,6}|true|false|null|yes|no|none)([^[:alnum:]_]|$)/\1\x01\3\4/Ig' \
        -e 's/('"$keys"')("[^"]*"|[^[:space:]"\x01].*)/\1[REDACTED]/Ig' \
        -e 's/\x01//g'
}

# section FILE TITLE COMMAND...: appends the command output and a non-zero exit code.
section() {
    local file=$1 title=$2 status
    shift 2
    mkdir -p "$(dirname "$out/$file")"
    {
        printf '\n===== %s\n' "$title"
        if ! command -v "$1" >/dev/null 2>&1; then
            printf '(%s is not installed)\n' "$1"
        else
            "$@" 2>&1
            status=$?
            [ "$status" -eq 0 ] || printf '(exit code %s)\n' "$status"
        fi
    } | redact >> "$out/$file"
}

show_file() {
    local file=$1 path=$2
    {
        printf '\n===== %s\n' "$path"
        if [ -r "$path" ]; then cat -- "$path"; else printf '(not present)\n'; fi
    } | redact >> "$out/$file"
}

copy_text() {
    local source=$1 target=$2 size
    [ -f "$source" ] || return 0
    mkdir -p "$(dirname "$out/$target")"
    if [ -s "$source" ] && ! grep -Iq . "$source" 2>/dev/null; then
        problem "not copied (not a text file): $source"
        return 0
    fi
    size=$(stat -c %s -- "$source" 2>/dev/null || echo 0)
    if [ "$size" -gt "$max_copy_bytes" ]; then
        tail -c "$max_copy_bytes" -- "$source" | redact > "$out/$target"
        problem "copied only the last $max_copy_bytes bytes of $source"
    else
        redact < "$source" > "$out/$target"
    fi
}

copy_tree_text() {
    local source=$1 target=$2 path
    [ -d "$source" ] || return 0
    find "$source" -xdev -type f ! -path '*/cdebconf/*' -print0 2>/dev/null | while IFS= read -r -d '' path; do
        copy_text "$path" "$target/${path#"$source"/}"
    done
}

echo "... system"
virt=$(systemd-detect-virt 2>/dev/null)
{
    printf 'taken (UTC): %s\n' "$(date -u '+%Y-%m-%d %H:%M:%S')"
    printf 'host: %s; kernel: %s\n' "$(hostname)" "$(uname -srm)"
    printf 'firmware: %s\n' "$([ -d /sys/firmware/efi ] && echo UEFI || echo BIOS)"
    printf 'virtualisation: %s\n' "${virt:-unknown}"
    printf 'memory: %s\n' "$(awk '/MemTotal/ {printf "%.0f MiB", $2 / 1024}' /proc/meminfo)"
    printf 'processors: %s\n' "$(nproc 2>/dev/null)"
    printf 'target user: %s (%s)\n' "$target_user" "$user_home"
    printf 'kernel command line: %s\n' "$(cat /proc/cmdline)"
} | redact > "$out/system.txt"
show_file system.txt /etc/os-release
section system.txt 'mokutil --sb-state' mokutil --sb-state
section system.txt 'uptime' uptime
section system.txt 'who -b' who -b
fact "os: $(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-unknown}"); kernel $(uname -r); virtualisation ${virt:-unknown}"
fact "secure boot: $(if command -v mokutil >/dev/null 2>&1; then mokutil --sb-state 2>&1 | head -n 1; else echo 'mokutil is not installed'; fi)"

echo "... storage"
section storage.txt 'lsblk -f' lsblk -f
columns=NAME,SIZE,TYPE,FSTYPE,LABEL,PARTLABEL,PARTUUID,PARTTYPE,UUID
if lsblk -o MOUNTPOINTS >/dev/null 2>&1; then columns="$columns,MOUNTPOINTS"; else columns="$columns,MOUNTPOINT"; fi
section storage.txt "lsblk -b -o $columns" lsblk -b -o "$columns"
section storage.txt 'blkid -c /dev/null' blkid -c /dev/null
section storage.txt 'findmnt' findmnt
section storage.txt 'df -hT' df -hT
section storage.txt 'swapon --show' swapon --show
show_file storage.txt /etc/fstab
esp_total=0
for disk in $(lsblk -dno NAME,TYPE 2>/dev/null | awk '$2 == "disk" {print $1}'); do
    section storage.txt "sfdisk -d /dev/$disk" sfdisk -d "/dev/$disk"
    section storage.txt "sfdisk -F /dev/$disk (unallocated regions)" sfdisk -F "/dev/$disk"
    esp_here=$(lsblk -rno PARTTYPE "/dev/$disk" 2>/dev/null | grep -ci '^c12a7328-f81f-11d2-ba4b-00a0c93ec93b$')
    esp_total=$((esp_total + esp_here))
    fact "disk /dev/$disk: $(lsblk -dbno SIZE "/dev/$disk" 2>/dev/null) bytes, $(lsblk -rno TYPE "/dev/$disk" 2>/dev/null | grep -c '^part$') partitions, $esp_here EFI system partition(s)"
done
fact "EFI system partitions on all disks: $esp_total"
esp_source=$(findmnt -no SOURCE /boot/efi 2>/dev/null)
fact "root: $(findmnt -no SOURCE,FSTYPE / 2>/dev/null); /boot/efi: ${esp_source:-not mounted}"
if [ -n "$esp_source" ]; then
    fact "/boot/efi PARTUUID: $(lsblk -no PARTUUID "$esp_source" 2>/dev/null)"
    section storage.txt 'files on /boot/efi (names and sizes)' find /boot/efi -xdev -printf '%y %12s  %P\n'
fi

echo "... boot"
section boot.txt 'efibootmgr -v' efibootmgr -v
show_file boot.txt /etc/default/grub
for grub_cfg in /boot/grub/grub.cfg /boot/grub2/grub.cfg; do
    [ -r "$grub_cfg" ] || continue
    section boot.txt "menu entries in $grub_cfg" grep -E "^[[:space:]]*(menuentry|submenu) " "$grub_cfg"
    fact "grub: $(grep -cE '^[[:space:]]*menuentry ' "$grub_cfg") menu entries in $grub_cfg; Windows entries: $(grep -ciE '^[[:space:]]*menuentry .*windows' "$grub_cfg")"
done
section boot.txt 'grub-editenv list' grub-editenv list
section boot.txt 'grub2-editenv list' grub2-editenv list
section boot.txt 'ls -la /boot' ls -la /boot
section boot.txt 'ls -la /boot/loader/entries' ls -la /boot/loader/entries
fact "uefi: $(efibootmgr 2>/dev/null | grep -E '^(BootCurrent|BootOrder|BootNext)' | tr '\n' ' ')"
fact "uefi entries: $(efibootmgr 2>/dev/null | grep -E '^Boot[0-9A-Fa-f]{4}' | sed -E 's/[[:space:]]+(HD|PciRoot|VenHw|BBS|File|FvVol)\(.*$//' | tr '\n' ';')"

echo "... locale"
section locale.txt 'locale (environment of this script)' locale
section locale.txt 'locale -a' locale -a
for path in /etc/default/locale /etc/locale.conf /etc/default/keyboard /etc/vconsole.conf /etc/timezone /etc/adjtime; do
    show_file locale.txt "$path"
done
section locale.txt 'readlink /etc/localtime' readlink /etc/localtime
section locale.txt 'date' date
session_locale=''
{
    printf '\n===== locale variables of the desktop session of %s\n' "$target_user"
    for pid in $(pgrep -u "$target_user" 2>/dev/null); do
        case "$(cat "/proc/$pid/comm" 2>/dev/null)" in
            plasmashell|startplasma*|ksmserver|cinnamon*|gnome-shell|gnome-session*|xfce4-session)
                printf '%s (pid %s): %s\n' "$(cat "/proc/$pid/comm")" "$pid" \
                    "$(tr '\0' '\n' < "/proc/$pid/environ" 2>/dev/null | grep -E '^(LANG|LANGUAGE|LC_[A-Z_]+)=' | sort | tr '\n' ' ')" ;;
        esac
    done
} >> "$out/locale.txt"
session_locale=$(grep -E '\(pid [0-9]+\): ' "$out/locale.txt" | head -n 1 | sed -E 's/^[^:]*: //')
fact "locale files: $(cat /etc/default/locale /etc/locale.conf 2>/dev/null | grep -E '^(LANG|LC_TIME|LC_NUMERIC|LC_MONETARY)=' | tr '\n' ' '); clock: $(sed -n 3p /etc/adjtime 2>/dev/null)"
fact "locale of the desktop session: ${session_locale:-no desktop session found, log in first}"

echo "... agent"
for path in /var/log/igloo-post.log /var/log/igloo-install.log /var/log/igloo-watchdog.log; do
    copy_text "$path" "agent/$(basename "$path")"
done
copy_tree_text /var/log/igloo agent/var-log-igloo
for name in seed-identity.json seed-scrub-state.json; do
    copy_text "/var/lib/igloo/$name" "agent/var-lib-igloo/$name"
done
for name in igloo-credentials.log igloo-display.log; do
    copy_text "$user_home/.local/state/$name" "agent/user-state/$name"
done
section agent.txt 'ls -la /var/lib/igloo' ls -la /var/lib/igloo
section agent.txt 'ls -la /opt/igloo' ls -la /opt/igloo
section agent.txt "ls -la $user_home/.local/share/igloo" ls -la "$user_home/.local/share/igloo"
section agent.txt 'systemctl list-unit-files igloo*' systemctl list-unit-files 'igloo*' --no-pager
section agent.txt 'systemctl status igloo*' systemctl status 'igloo*' --no-pager -l --all
section agent.txt 'journalctl -u igloo* (all boots)' journalctl --no-pager -o short-iso -u 'igloo*'
section agent.txt 'journalctl -b -p err' journalctl --no-pager -o short-iso -b -p err
markers=''
for name in .done .had-failures .reboot-required .kernel-unverified; do
    [ -e "/var/lib/igloo/$name" ] && markers="$markers $name"
done
fact "agent markers in /var/lib/igloo:${markers:- none}"
agent_log=/var/log/igloo/agent.log
if [ -r "$agent_log" ]; then
    fact "agent.log: $(wc -l < "$agent_log") lines, $(grep -cE '\[(ERROR|CRITICAL)\]' "$agent_log") error line(s), $(grep -cE '\[WARNING\]' "$agent_log") warning line(s)"
    grep -E '\[(ERROR|CRITICAL|WARNING)\]' "$agent_log" | redact > "$out/agent-log-problems.txt"
    seed_line=$(grep -E 'Deleted installer partition|Installer partition cleanup did not finish|No partition carries installer seed GUID' "$agent_log" | tail -n 1 | sed -E 's/^.*\] [^:]+: //' | redact)
    fact "installer partition, last agent line: ${seed_line:-none}; firmware entries removed with it: $(grep -c 'Removed UEFI entry Boot' "$agent_log")"
else
    fact 'agent.log: not present'
fi
seed_guid=$(sed -n 's/.*"partitionGuid"[[:space:]]*:[[:space:]]*"\([0-9A-Fa-f-]*\)".*/\1/p' /var/lib/igloo/seed-identity.json 2>/dev/null | head -n 1)
if [ -n "$seed_guid" ]; then
    fact "installer partition $seed_guid: carried by $(lsblk -rno PARTUUID 2>/dev/null | grep -ci "^$seed_guid\$") partition(s), named by $(efibootmgr -v 2>/dev/null | grep -ci "GPT,$seed_guid,") firmware entry(ies)"
else
    fact 'installer partition: no partition GUID in /var/lib/igloo/seed-identity.json'
fi

echo "... installer logs"
mkdir -p "$out/installer"
copy_tree_text /var/log/installer installer/var-log-installer
copy_tree_text /var/log/anaconda installer/var-log-anaconda
if [ -r /root/anaconda-ks.cfg ]; then
    grep -E '^(ignoredisk|clearpart|autopart|part|partition|reqpart|zerombr|bootloader|logvol|volgroup|btrfs)\b' /root/anaconda-ks.cfg |
        redact > "$out/installer/kickstart-storage-lines.txt"
fi
fact "installer logs: $(find "$out/installer" -type f | wc -l) file(s) copied"

echo "... migrated data"
mkdir -p "$out/migration"
find "$user_home" -xdev -mindepth 1 -maxdepth 2 -printf '%y %12s  %TY-%Tm-%Td %TH:%TM  %u  %P\n' 2>/dev/null |
    LC_ALL=C sort -k 6 > "$out/migration/home-two-levels.txt"
if [ -d "$testdata" ]; then
    (
        cd "$testdata" || exit 1
        find . -mindepth 1 -type d -printf 'D %P/\n'
        find . -type f -printf '%P\0' | while IFS= read -r -d '' path; do
            printf 'F %s %s %s\n' "$(sha256sum < "$path" | cut -d' ' -f1)" "$(stat -c %s -- "$path")" "$path"
        done
        find . -mindepth 1 ! -type d ! -type f -printf 'X %y %P\n'
    ) | LC_ALL=C sort > "$out/migration/testdata-manifest.txt"
    fact "test data: $(grep -c '^F ' "$out/migration/testdata-manifest.txt") files, $(grep -c '^D ' "$out/migration/testdata-manifest.txt") directories, $(awk '$1 == "F" {sum += $3} END {printf "%d", sum}' "$out/migration/testdata-manifest.txt") bytes in $testdata; entries not owned by $target_user: $(find "$testdata" ! -user "$target_user" | wc -l)"
else
    fact "test data: $testdata does not exist"
fi

find "$user_home" -xdev -maxdepth 9 -type f \( -name 'Login Data' -o -name 'Bookmarks' -o -name 'Cookies' -o -name 'History' \
    -o -name 'Local State' -o -name 'Preferences' -o -name 'logins.json' -o -name 'key4.db' -o -name 'places.sqlite' \
    -o -name 'cookies.sqlite' -o -name 'profiles.ini' -o -name 'installs.ini' \) \
    -printf '%12s  %TY-%Tm-%Td %TH:%TM  %u  %P\n' 2>/dev/null | LC_ALL=C sort -k 5 > "$out/migration/browser-files.txt"
section migration/browsers.txt 'browser commands' sh -c 'for name in firefox firefox-esr google-chrome google-chrome-stable microsoft-edge microsoft-edge-stable chromium chromium-browser; do printf "%s: %s\n" "$name" "$(command -v "$name" || echo -)"; done'
section migration/browsers.txt 'flatpak applications (system)' ls /var/lib/flatpak/app
section migration/browsers.txt 'flatpak applications (user)' ls "$user_home/.local/share/flatpak/app"
section migration/browsers.txt 'flatpak data (user)' ls "$user_home/.var/app"
section migration/browsers.txt 'staged Chromium data' ls -la /var/lib/igloo/chromium
if command -v python3 >/dev/null 2>&1; then
    # Counts rows only; immutable=1 opens a database without a lock or a journal.
    python3 -I -B - "$user_home" > "$out/migration/browser-counts.txt" 2>&1 <<'PY'
import json
import os
import sqlite3
import sys

home = sys.argv[1]
QUERIES = {
    "Login Data": ("saved logins", "SELECT count(*) FROM logins"),
    "Cookies": ("cookies", "SELECT count(*) FROM cookies"),
    "History": ("history urls", "SELECT count(*) FROM urls"),
    "places.sqlite": ("bookmarks", "SELECT count(*) FROM moz_bookmarks WHERE type = 1"),
    "cookies.sqlite": ("cookies", "SELECT count(*) FROM moz_cookies"),
}


def count_urls(node):
    if isinstance(node, dict):
        own = 1 if node.get("type") == "url" else 0
        return own + sum(count_urls(child) for child in node.values())
    if isinstance(node, list):
        return sum(count_urls(child) for child in node)
    return 0


print("Row counts; a write-ahead log that was not checkpointed is not counted.")
for root, dirs, files in os.walk(home):
    if root[len(home):].count(os.sep) >= 9:
        dirs[:] = []
    dirs[:] = [d for d in dirs if d not in ("Cache", "Code Cache", "cache2", ".cache")]
    for name in sorted(files):
        path = os.path.join(root, name)
        shown = os.path.relpath(path, home)
        try:
            if name in QUERIES:
                label, query = QUERIES[name]
                quoted = path.replace("%", "%25").replace("?", "%3f").replace("#", "%23")
                connection = sqlite3.connect("file:" + quoted + "?mode=ro&immutable=1", uri=True)
                try:
                    print(f"{shown}: {connection.execute(query).fetchone()[0]} {label}")
                finally:
                    connection.close()
            elif name == "Bookmarks":
                with open(path, encoding="utf-8") as stream:
                    print(f"{shown}: {count_urls(json.load(stream).get('roots', {}))} bookmarks")
            elif name == "logins.json":
                with open(path, encoding="utf-8") as stream:
                    print(f"{shown}: {len(json.load(stream).get('logins', []))} saved logins")
        except Exception as error:
            print(f"{shown}: not readable ({type(error).__name__}: {error})")
PY
    fact "browser data: $(grep -c ': [0-9]' "$out/migration/browser-counts.txt") counted file(s), see migration/browser-counts.txt"
fi

section migration/wifi.txt 'nmcli connection show' nmcli -t -f NAME,UUID,TYPE,DEVICE,AUTOCONNECT connection show
section migration/wifi.txt 'nmcli device status' nmcli device status
section migration/wifi.txt 'rfkill list' rfkill list
section migration/wifi.txt 'ls -la /etc/NetworkManager/system-connections' ls -la /etc/NetworkManager/system-connections
wifi_profiles=0
{
    printf '\n===== key files: type and whether a key is stored (no value is copied)\n'
    for path in /etc/NetworkManager/system-connections/*; do
        [ -f "$path" ] || continue
        printf '%s: type=%s ssid-lines=%s psk-lines=%s\n' "$(basename "$path")" \
            "$(sed -n 's/^type=//p' "$path" | head -n 1)" "$(grep -c '^ssid=' "$path")" "$(grep -c '^psk=' "$path")"
    done
} >> "$out/migration/wifi.txt"
wifi_profiles=$(grep -c ': type=wifi ' "$out/migration/wifi.txt")
fact "wifi: $wifi_profiles saved Wi-Fi profile(s); Wi-Fi devices: $(nmcli -t -f TYPE device status 2>/dev/null | grep -c '^wifi$')"

echo "... summary"
{
    printf 'iGloo evidence, Linux side. run=%s phase=%s\n' "$run_name" "$phase"
    cat "$facts"
    printf '\n'
    if [ -s "$problems" ]; then
        printf 'Notes (%s):\n' "$(wc -l < "$problems")"
        cat "$problems"
    else
        printf 'Every section completed.\n'
    fi
} > "$out/summary.txt"
rm -f "$facts" "$problems"
[ "$root_was_there" -eq 1 ] || chown "$target_user": "$out_root" 2>/dev/null
chown -R "$target_user": "$run_dir" 2>/dev/null
printf '\n'
cat "$out/summary.txt"
printf '\nEvidence written to %s\n' "$out"
