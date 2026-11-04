# ============================================================================
# Igloo kickstart template for Fedora KDE
# ----------------------------------------------------------------------------
# Rendered at runtime by the Igloo Windows-side app. Placeholders:
#   en_US.UTF-8             e.g. en_US.UTF-8
#   us             e.g. be, us, de
#   us            same as KEYMAP (XKB layout name)
#   UTC           IANA tz, e.g. Europe/Brussels
#   fixture-pc           <linux-username>-pc
#   fixture     the user's chosen Linux login name
#   Fixture          display name (falls back to username)
#   104857600000  exact byte size of the user-selected target disk
#   00000000-0000-0000-0000-000000000001   Windows-observed GPT disk GUID of that selected disk
#     model string of the target disk (informational)
#   fixture   Windows login name retained for migration metadata
#   dual-boot       "replace"   → wipe entire disk (clearpart --all)
#                          "dual-boot" → keep existing partitions, use free space
#                                        (clearpart --none; Windows was already
#                                         shrunk by the Igloo app before boot)
#      space-separated list of folder names (informational)
#                          (e.g. "Documents Downloads Pictures")
#    selected Gecko OEMDRV paths and Linux destinations
#    selected Chromium OEMDRV paths
#   --lock    rendered by the plugin as either
#                          "--password=<crypt-hash> --iscrypted"
#                          "--lock"                              (fallback if no password)
#             SSID of the network Windows was connected to, escaped
#                          for a bash double-quoted string ("" if none / wired-only)
#              cleartext pre-shared key for  ("" if open
#                          network or no key recovered)
#   url --metalink="https://mirrors.fedoraproject.org/metalink?repo=fedora-44&arch=x86_64" the Anaconda install-source directive (a full `url …`
#                          line) telling the netinstall where to fetch packages.
#                          Rendered from distro.json's iso.stage2Url - typically a
#                          Fedora metalink ("Closest mirror"). Without it Anaconda
#                          stops with "Error setting up repositories".
#
# The boot entry points Anaconda to this file by exact OEMDRV GPT partition ID.
# ============================================================================

# The Windows client has already collected every installer choice. Anaconda's
# graphical ProgressSpoke still requests interaction under inst.noninteractive
# on Fedora 44 and aborts before touching the disk. cmdline honors this complete
# kickstart and stops if a required choice is missing.
cmdline

# Do NOT run the post-install setup wizard (initial-setup / the GTK first-boot
# wizard) on first boot. Without this line the kickstart default is --enable,
# and Anaconda's "Configure Initial Setup" task leaves initial-setup.service
# active: the user then gets a wizard asking for language, keyboard layout and
# timezone - all pre-filled, because every one of them is already configured
# right here - plus extra Next clicks before the login screen. Pure friction
# on a migration that promises "boot straight to the desktop".
firstboot --disable

lang en_US.UTF-8
# Only the XKB layout is given; Anaconda derives a matching virtual-console
# keymap from it. Do NOT add --vckeymap=us: vconsole names differ from
# XKB names (e.g. Belgian is xkb "be" but vconsole "be-latin1"), so feeding the
# XKB name to --vckeymap makes Anaconda reject the layout for any non-US keyboard.
keyboard --xlayouts='us'
timezone UTC --utc

network --bootproto=dhcp --device=link --activate --hostname=fixture-pc

# ── Install source ────────────────────────────────────────────────────────────
# Direct-install boots with a LOCAL stage-2 on the bound OEMDRV partition, but the
# RPM packages for a netinstall must come from the network. This line points
# Anaconda at the Fedora mirror metalink (the "Closest mirror" option) so the
# repository is configured automatically - no interactive source prompt.
url --metalink="https://mirrors.fedoraproject.org/metalink?repo=fedora-44&arch=x86_64"

rootpw --lock
user --name=fixture --groups=wheel --gecos="Fixture" --lock

# Storage bound to the reviewed preparation generation.

%pre --interpreter=/bin/bash --erroronfail
set -euo pipefail
TARGET_BYTES="104857600000"
TARGET_DISK_GUID="00000000-0000-0000-0000-000000000001"
TARGET_MODEL=""
INSTALL_MODE="dual-boot"
TARGET_DISK=""
MATCHES=0
GUID_MATCHES=0

log() { echo "[igloo-pre] $*" >&2; }
log "Searching for target disk: GPT=$TARGET_DISK_GUID model='$TARGET_MODEL' size=${TARGET_BYTES} bytes"

# Verify one exact seed before emitting any destructive storage directives.
# This validator is also rerun by %post before it copies the agent or manifest.
cat > /tmp/igloo-validate-seed.py <<'IGLOO_SEED_VALIDATOR'
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import unicodedata
import uuid

device = os.path.realpath(sys.argv[1])
seed = Path(sys.argv[2])
if not stat.S_ISBLK(os.stat(device).st_mode):
    raise RuntimeError("OEMDRV is not a block partition")
identity_bytes = (seed / "igloo-seed-identity.json").read_bytes()
identity = json.loads(identity_bytes)
if identity.get("version") != 1:
    raise RuntimeError("Unsupported OEMDRV identity version")
expected_partition = uuid.UUID(identity["partitionGuid"])
expected_disk = uuid.UUID(identity["diskGuid"])
if expected_partition.int == 0 or expected_disk.int == 0:
    raise RuntimeError("OEMDRV has an empty GPT identity")

def read_id(args):
    return subprocess.check_output(args, text=True).strip()

partition = uuid.UUID(read_id(["blkid", "-s", "PARTUUID", "-o", "value", device]))
parents = read_id(["lsblk", "-n", "-o", "PKNAME", device]).splitlines()
if len(parents) != 1 or not re.fullmatch(r"[A-Za-z0-9._-]+", parents[0]):
    raise RuntimeError("OEMDRV parent disk is unavailable or ambiguous")
disk = uuid.UUID(read_id(["blkid", "-s", "PTUUID", "-o", "value", "/dev/" + parents[0]]))
if partition != expected_partition or disk != expected_disk:
    raise RuntimeError("OEMDRV GPT identity does not match its seed identity")

# Direct-install carries the Windows-observed seed IDs in the kernel command
# line. Raw USB boot has no external seed hint; the one OEMDRV must still prove
# its own GPT identity and exact staged file hashes.
hints = {"igloo.seedpartuuid": [], "igloo.seeddiskguid": []}
for token in Path("/proc/cmdline").read_text().split():
    for key in hints:
        if token.startswith(key + "="):
            hints[key].append(token.split("=", 1)[1])
if any(hints.values()):
    if len(hints["igloo.seedpartuuid"]) != 1 or len(hints["igloo.seeddiskguid"]) != 1:
        raise RuntimeError("Incomplete or ambiguous OEMDRV boot identity")
    if (uuid.UUID(hints["igloo.seedpartuuid"][0]) != expected_partition or
            uuid.UUID(hints["igloo.seeddiskguid"][0]) != expected_disk):
        raise RuntimeError("OEMDRV does not match the Windows-observed boot identity")

entries = identity.get("files")
if not isinstance(entries, list):
    raise RuntimeError("OEMDRV file identity list is missing")
seen = set()
bound_inputs = {"migration-manifest.json", "ks.cfg", "preseed.cfg", "igloo-boot/initrd"}
required_agent = {"agent.py", "first-boot.sh", "display-apply.py", "igloo_boot.py"}
manifest = json.loads((seed / "migration-manifest.json").read_bytes())
selected_media = set()
for key, pattern in (("wallpaper", r"igloo-wallpaper\.[A-Za-z0-9]{2,8}"),
                     ("accountPicture", r"igloo-avatar\.jpg")):
    item = manifest.get(key)
    if item is None:
        continue
    name = item.get("fileName") if isinstance(item, dict) else None
    valid = (isinstance(name, str) and
             (name == "igloo-avatar.jpg" if key == "accountPicture" else
              re.fullmatch(pattern, name, flags=re.IGNORECASE) is not None))
    if not valid:
        raise RuntimeError("Selected OEMDRV media has an unsafe file name")
    selected_media.add(name)

def safe_user_segment(segment):
    base = segment.split(".", 1)[0].upper()
    reserved = base in {"CON", "PRN", "AUX", "NUL"} or re.fullmatch(
        r"(?:COM|LPT)[1-9]", base) is not None
    return (bool(segment) and not segment.isspace() and segment not in {".", ".."}
            and segment == unicodedata.normalize("NFC", segment) and not reserved
            and not segment.endswith((" ", "."))
            and all(unicodedata.category(char) != "Cc" and
                    char not in '<>":|?*/\\' for char in segment))

def safe_user_path(path):
    return (isinstance(path, str) and 0 < len(path) <= 1024 and
            "\\" not in path and all(safe_user_segment(part)
                                      for part in path.split("/")))

def selected_user_paths():
    roots = set()
    root_aliases = set()

    def add_root(path):
        if not safe_user_path(path):
            raise RuntimeError("Selected OEMDRV user-data path is unsafe")
        if len(roots) >= 1024:
            raise RuntimeError("Selected OEMDRV user-data root inventory is too large")
        alias = unicodedata.normalize("NFC", path).casefold()
        if alias in root_aliases:
            raise RuntimeError("Selected OEMDRV user-data roots are ambiguous")
        root_aliases.add(alias)
        roots.add(path)

    selected_files = manifest.get("files")
    if selected_files is None:
        selected_files = {}
    if not isinstance(selected_files, dict):
        raise RuntimeError("Selected OEMDRV folder metadata is invalid")
    folders = selected_files.get("folders")
    if folders is None:
        folders = []
    if not isinstance(folders, list):
        raise RuntimeError("Selected OEMDRV folder metadata is invalid")
    if folders:
        if any(not isinstance(folder, dict) or not isinstance(folder.get("name"), str)
               for folder in folders):
            raise RuntimeError("Selected OEMDRV folder metadata is invalid")
        folder_names = [folder["name"] for folder in folders]
    else:
        folder_names = selected_files.get("includedFolders")
        if folder_names is None:
            folder_names = []
        if not isinstance(folder_names, list) or any(
                not isinstance(name, str) for name in folder_names):
            raise RuntimeError("Selected OEMDRV folder metadata is invalid")
    staging_path = selected_files.get("stagingPath")
    if staging_path is not None and not isinstance(staging_path, str):
        raise RuntimeError("Selected OEMDRV folder staging metadata is invalid")
    if (staging_path and not staging_path.isspace()) or (seed / "files").exists():
        for name in folder_names:
            add_root("files/" + name)

    browsers = manifest.get("browsers")
    if browsers is None:
        browsers = []
    if not isinstance(browsers, list):
        raise RuntimeError("Selected OEMDRV browser metadata is invalid")
    for browser in browsers:
        if not isinstance(browser, dict):
            raise RuntimeError("Selected OEMDRV browser metadata is invalid")
        staged = browser.get("profileStagingPath")
        if staged is None or staged == "":
            continue  # Historical NTFS-only browser selection.
        source = browser.get("sourceRelativePath")
        if not isinstance(staged, str) or not isinstance(source, str):
            raise RuntimeError("Staged browser selection has no source binding")
        source = source.replace("\\", "/")
        if not safe_user_path(source) or staged != "browser-profiles/" + source:
            raise RuntimeError("Staged browser selection differs from its source")
        add_root(staged)

    observed = set()
    directories = set()
    aliases = set()
    entries_seen = 0
    path_characters = 0
    for namespace in ("files", "browser-profiles"):
        namespace_root = seed / namespace
        chosen = {root for root in roots if root.startswith(namespace + "/")}
        if namespace_root.is_symlink() or (namespace_root.exists() and
                                           (not namespace_root.is_dir() or not chosen)):
            raise RuntimeError("OEMDRV contains an unselected or redirected user-data root")
        if not namespace_root.is_dir():
            if chosen:
                raise RuntimeError("Selected OEMDRV user-data root is missing")
            continue
        pending = [namespace_root]
        while pending:
            for item in pending.pop().iterdir():
                if item.is_symlink():
                    raise RuntimeError("Selected OEMDRV user data contains a redirected entry")
                relative = item.relative_to(seed).as_posix()
                if not safe_user_path(relative):
                    raise RuntimeError("Selected OEMDRV user-data path is unsafe")
                alias = unicodedata.normalize("NFC", relative).casefold()
                entries_seen += 1
                if alias in aliases or entries_seen > 20000:
                    raise RuntimeError("Selected OEMDRV user-data inventory is ambiguous or excessive")
                aliases.add(alias)
                if item.is_dir():
                    if not any(relative == root or relative.startswith(root + "/") or
                               root.startswith(relative + "/") for root in chosen):
                        raise RuntimeError("OEMDRV contains an unselected user-data directory")
                    directories.add(relative)
                    pending.append(item)
                elif item.is_file():
                    if not any(relative.startswith(root + "/") for root in chosen):
                        raise RuntimeError("OEMDRV contains an unselected user-data file")
                    path_characters += len(relative)
                    if len(observed) >= 10000 or path_characters > 2000000:
                        raise RuntimeError("Selected OEMDRV user-data inventory is too large")
                    observed.add(relative)
                else:
                    raise RuntimeError("Selected OEMDRV user data contains a special entry")
    for root in roots:
        selected = seed / root
        if selected.is_symlink() or not selected.is_dir():
            raise RuntimeError("Selected OEMDRV user-data directory is missing")
    for directory in directories:
        if (not any(directory == root or root.startswith(directory + "/")
                    for root in roots) and
                not any(path.startswith(directory + "/") for path in observed)):
            raise RuntimeError("Selected OEMDRV user data contains an unbound empty directory")
    return observed

selected_user = selected_user_paths()

def safe_provider_path(name):
    if not isinstance(name, str) or len(name) > 256 or "\\" in name or ":" in name:
        return False
    parts = name.split("/")
    return (len(parts) >= 2 and all(re.fullmatch(r"[A-Za-z0-9_.-]+", part)
                                    and part not in {".", ".."} for part in parts)
            and parts[0].lower() not in {"files", "browser-profiles", "efi",
                                         "images", "igloo-boot"})

for entry in entries:
    if not isinstance(entry, dict):
        raise RuntimeError("Malformed OEMDRV file identity")
    name, expected_hash = entry.get("path"), entry.get("sha256")
    if (not isinstance(name, str) or
            (name not in bound_inputs and name not in selected_media
             and name not in selected_user
             and not safe_provider_path(name)) or
            name in seen or
            not isinstance(expected_hash, str) or
            re.fullmatch(r"[0-9A-F]{64}", expected_hash) is None):
        raise RuntimeError("Unsafe or duplicated OEMDRV file identity")
    seen.add(name)
    path = seed / name
    if path.is_symlink() or not path.is_file():
        raise RuntimeError("OEMDRV file is missing or redirected: " + name)
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    actual_hash = digest.hexdigest().upper()
    if actual_hash != expected_hash:
        raise RuntimeError("OEMDRV file differs from its bound identity: " + name)
if not {"migration-manifest.json", "ks.cfg"}.issubset(seen):
    raise RuntimeError("OEMDRV lacks bound Fedora inputs")
bound_media = {name for name in seen if name.lower().startswith(
    "igloo-wallpaper.") or name.lower() == "igloo-avatar.jpg"}
if bound_media != selected_media:
    raise RuntimeError("Selected OEMDRV media differs from its identity")
if {name for name in seen if name in selected_user} != selected_user:
    raise RuntimeError("Selected OEMDRV user data differs from its identity")
agent_root = seed / "igloo-agent"
if not agent_root.is_dir() or agent_root.is_symlink():
    raise RuntimeError("OEMDRV first-boot agent directory is unavailable")
actual_agent = set()
for path in agent_root.iterdir():
    if path.is_symlink() or not path.is_file():
        raise RuntimeError("OEMDRV first-boot agent has an unexpected entry")
    actual_agent.add("igloo-agent/" + path.name)
bound_agent = {name for name in seen if name.startswith("igloo-agent/")}
if (actual_agent != bound_agent or
        not {"igloo-agent/" + name for name in required_agent}.issubset(bound_agent)):
    raise RuntimeError("OEMDRV first-boot agent inventory differs from its identity")
print(str(disk))
IGLOO_SEED_VALIDATOR

mapfile -t SEED_DEVICES < <(blkid -t LABEL=OEMDRV -o device 2>/dev/null)
if [ "${#SEED_DEVICES[@]}" -ne 1 ]; then
    log "ERROR: expected exactly one OEMDRV partition; found ${#SEED_DEVICES[@]}"
    exit 1
fi
PRE_SEED=/tmp/igloo_pre_oemdrv
mkdir -p "$PRE_SEED"
mount -t vfat -o ro,nosuid,nodev,noexec "${SEED_DEVICES[0]}" "$PRE_SEED"
SEED_DISK_GUID=$(python3 /tmp/igloo-validate-seed.py "${SEED_DEVICES[0]}" "$PRE_SEED")
umount "$PRE_SEED"
if [ -z "$SEED_DISK_GUID" ] || [ "${SEED_DISK_GUID,,}" = "${TARGET_DISK_GUID,,}" ]; then
    log "ERROR: selected target is the installer seed disk or seed identity is unavailable"
    exit 1
fi
log "Bound OEMDRV identity and staged inputs verified"

if [ -z "$TARGET_DISK_GUID" ] || [ "$TARGET_DISK_GUID" = "00000000-0000-0000-0000-000000000000" ]; then
    log "ERROR: selected GPT disk identity is unavailable"
    exit 1
fi

# Enumerate all physical (non-partition) block devices.
# /sys/block/*/size is in 512-byte sectors; multiply to get bytes.
for sys_dev in /sys/block/sd* /sys/block/nvme*n1 /sys/block/vd* /sys/block/xvd*; do
    [ -e "$sys_dev/size" ] || continue
    devname=$(basename "$sys_dev")

    # Skip removable devices (USB sticks, SD cards, etc.)
    removable=$(cat "$sys_dev/removable" 2>/dev/null || echo 0)
    [ "$removable" = "1" ] && { log "  $devname: removable, skipping"; continue; }

    size_sectors=$(cat "$sys_dev/size" 2>/dev/null || echo 0)
    size_bytes=$(( size_sectors * 512 ))
    log "  $devname: ${size_bytes} bytes"

    observed_guid=$(blkid -p -s PTUUID -o value "/dev/$devname" 2>/dev/null || true)
    if [ -n "$observed_guid" ] && [ "${observed_guid,,}" = "${TARGET_DISK_GUID,,}" ]; then
        GUID_MATCHES=$(( GUID_MATCHES + 1 ))
        if [ "$size_bytes" = "$TARGET_BYTES" ] && [ "$TARGET_BYTES" != "0" ]; then
            TARGET_DISK="$devname"
            MATCHES=$(( MATCHES + 1 ))
            log "  --> matched exact GPT identity and size (candidate $MATCHES)"
        else
            log "  --> GPT identity matched but size changed; refusing this disk"
        fi
    fi
done

if [ "$GUID_MATCHES" -ne 1 ] || [ "$MATCHES" -ne 1 ]; then
    log "ERROR: expected exactly one disk matching GPT $TARGET_DISK_GUID and $TARGET_BYTES bytes; found $GUID_MATCHES identity and $MATCHES exact matches"
    exit 1
fi
if [ "$INSTALL_MODE" != "replace" ] && [ "$INSTALL_MODE" != "dual-boot" ]; then
    log "ERROR: unsupported install mode '$INSTALL_MODE'"
    exit 1
fi

    log "Target disk: /dev/$TARGET_DISK  (mode: $INSTALL_MODE)"

verify_owned_partition() {
    local expected_guid="$1" expected_offset="$2" expected_size="$3"
    local expected_type="$4" expected_fs="$5" expected_uuid="$6"
    local device parent name start sectors observed_type observed_fs observed_uuid linked
    local -a matches
    mapfile -t matches < <(blkid -t "PARTUUID=$expected_guid" -o device 2>/dev/null)
    if [ "${#matches[@]}" -ne 1 ]; then
        log "ERROR: owned partition $expected_guid is absent or ambiguous"
        exit 1
    fi
    device=$(readlink -f "${matches[0]}")
    linked=$(readlink -f "/dev/disk/by-partuuid/$expected_guid" 2>/dev/null || true)
    if [ "$linked" != "$device" ]; then
        log "ERROR: owned partition $expected_guid has no matching by-partuuid device path"
        exit 1
    fi
    parent=$(lsblk -n -o PKNAME "$device" | tr -d '[:space:]')
    name=$(basename "$device")
    if [ "$parent" != "$TARGET_DISK" ] ||
       [ ! -r "/sys/class/block/$name/start" ] ||
       [ ! -r "/sys/class/block/$name/size" ]; then
        log "ERROR: owned partition $expected_guid has the wrong parent disk"
        exit 1
    fi
    start=$(cat "/sys/class/block/$name/start")
    sectors=$(cat "/sys/class/block/$name/size")
    if [ "$((start * 512))" -ne "$expected_offset" ] ||
       [ "$((sectors * 512))" -ne "$expected_size" ]; then
        log "ERROR: owned partition $expected_guid geometry changed"
        exit 1
    fi
    observed_type=$(lsblk -n -o PARTTYPE "$device" | tr -d '[:space:]')
    observed_fs=$(blkid -p -s TYPE -o value "$device" 2>/dev/null || true)
    observed_uuid=$(blkid -p -s UUID -o value "$device" 2>/dev/null || true)
    if [ "${observed_type,,}" != "$expected_type" ]; then
        log "ERROR: owned partition $expected_guid type changed"
        exit 1
    fi
    if [ "$expected_fs" = NONE ]; then
        if [ -n "$observed_fs" ]; then
            log "ERROR: owned root $expected_guid already has a filesystem"
            exit 1
        fi
    elif [ "$observed_fs" != "$expected_fs" ] ||
         [ "${observed_uuid,,}" != "${expected_uuid,,}" ]; then
        log "ERROR: owned partition $expected_guid filesystem changed"
        exit 1
    fi
}
verify_owned_partition 00000000-0000-0000-0000-000000000010 41943040000 1073741824 c12a7328-f81f-11d2-ba4b-00a0c93ec93b vfat 2222-BBBB
verify_owned_partition 00000000-0000-0000-0000-000000000012 44065357824 20971520000 0fc63daf-8483-4772-8e79-3d69d8477de4 NONE NONE

log "Reviewed owned root and Linux ESP identities verified"
# Check the complete selected GPT partition set, including preserved Windows
# partitions, before Anaconda can consume a storage directive.
EXPECTED_PARTITIONS=$(LC_ALL=C sort <<'IGLOO_EXPECTED_PARTITIONS'
00000000-0000-0000-0000-000000000002|c12a7328-f81f-11d2-ba4b-00a0c93ec93b|1048576|272629760
00000000-0000-0000-0000-000000000003|ebd0a0a2-b9e5-4433-87c0-68b6b72699c7|273678336|41669361664
00000000-0000-0000-0000-000000000010|c12a7328-f81f-11d2-ba4b-00a0c93ec93b|41943040000|1073741824
00000000-0000-0000-0000-000000000011|ebd0a0a2-b9e5-4433-87c0-68b6b72699c7|43016781824|1048576000
00000000-0000-0000-0000-000000000012|0fc63daf-8483-4772-8e79-3d69d8477de4|44065357824|20971520000
IGLOO_EXPECTED_PARTITIONS
)
OBSERVED_PARTITIONS=()
PARTITION_ROWS=$(lsblk -nr -o PATH,TYPE "/dev/$TARGET_DISK") || exit 1
while read -r partition_path partition_kind; do
    [ "$partition_kind" = part ] || continue
    partition_name=$(basename "$(readlink -f "$partition_path")")
    observed_guid=$(blkid -s PARTUUID -o value "$partition_path") || exit 1
    observed_type=$(lsblk -n -o PARTTYPE "$partition_path" | tr -d '[:space:]')
    [ -r "/sys/class/block/$partition_name/start" ] || exit 1
    [ -r "/sys/class/block/$partition_name/size" ] || exit 1
    observed_start=$(cat "/sys/class/block/$partition_name/start")
    observed_sectors=$(cat "/sys/class/block/$partition_name/size")
    OBSERVED_PARTITIONS+=("${observed_guid,,}|${observed_type,,}|$((observed_start * 512))|$((observed_sectors * 512))")
done <<< "$PARTITION_ROWS"
if [ "${#OBSERVED_PARTITIONS[@]}" -ne 5 ] ||
   [ "$(printf '%s\n' "${OBSERVED_PARTITIONS[@]}" | LC_ALL=C sort)" != "$EXPECTED_PARTITIONS" ]; then
    log "ERROR: reviewed target GPT partition set changed"
    exit 1
fi
log "Reviewed complete target GPT partition set verified"
if [[ ! "$TARGET_DISK" =~ ^[A-Za-z0-9_-]+$ ]]; then
    log "ERROR: selected target device name is unsafe"
    exit 1
fi
{
    printf 'ignoredisk --only-use=%s\n' "$TARGET_DISK"
    printf 'clearpart --none\n'
    printf 'bootloader --boot-drive=%s\n' "$TARGET_DISK"
    printf 'part /boot/efi --onpart=/dev/disk/by-partuuid/00000000-0000-0000-0000-000000000010 --noformat\n'
    printf 'part / --onpart=/dev/disk/by-partuuid/00000000-0000-0000-0000-000000000012 --fstype=ext4\n'
} > /tmp/ks-storage.cfg
log "Reviewed storage directives written for the uniquely matched target"
%end

%include /tmp/ks-storage.cfg


# Pull in the Wi-Fi network command generated by %pre above (may be empty).


# ── Packages ──────────────────────────────────────────────────────────────────

%packages
@^kde-desktop-environment
@standard
@hardware-support
python3
python3-pip
rsync
git
libnotify
mokutil
%end

reboot

# ── Post-install (outside chroot) ─────────────────────────────────────────────
# Runs with access to both the installer environment (/run/install/repo = OEMDRV)
# and the freshly-installed system (/mnt/sysimage).  This is where we:
#   1. Copy the Igloo agent + manifest into the new system.
#   2. Copy the user's staged files (Documents, Downloads, …) into their home dir.
#   3. Register the igloo-first-boot.service systemd unit.
# ─────────────────────────────────────────────────────────────────────────────

%post --nochroot --erroronfail --interpreter=/bin/bash --log=/mnt/sysimage/var/log/igloo-post.log
set -euo pipefail

SYSIMAGE="/mnt/sysimage"

# With netinstall, /run/install/repo is the network package source. Locate
# exactly one OEMDRV and revalidate the same GPT seed and file hashes that %pre
# checked before storage mutation.
mapfile -t SEED_DEVICES < <(blkid -t LABEL=OEMDRV -o device 2>/dev/null)
if [ "${#SEED_DEVICES[@]}" -ne 1 ]; then
    echo "[igloo-post] ERROR: expected exactly one OEMDRV partition; found ${#SEED_DEVICES[@]}" >&2
    exit 1
fi
OEMDRV_DEV="${SEED_DEVICES[0]}"
OEMDRV="/tmp/igloo_oemdrv"
mkdir -p "$OEMDRV"
mount -t vfat -o ro,nosuid,nodev,noexec "$OEMDRV_DEV" "$OEMDRV"
python3 /tmp/igloo-validate-seed.py "$OEMDRV_DEV" "$OEMDRV" >/dev/null
if [ ! -f "$OEMDRV/igloo-agent/first-boot.sh" ] || [ ! -f "$OEMDRV/igloo-agent/agent.py" ]; then
    echo "[igloo-post] ERROR: Required first-boot agent files are missing on OEMDRV" >&2
    exit 1
fi
if [ ! -f "$OEMDRV/migration-manifest.json" ]; then
    echo "[igloo-post] ERROR: Migration manifest is missing on OEMDRV" >&2
    exit 1
fi
if [ ! -f "$OEMDRV/igloo-seed-identity.json" ] || [ ! -f "$OEMDRV/ks.cfg" ]; then
    echo "[igloo-post] ERROR: Bound installer seed identity or kickstart is missing on OEMDRV" >&2
    exit 1
fi
USERNAME="fixture"
WINDOWS_USERNAME="fixture"
INCLUDED_FOLDERS=""
USER_HOME="$SYSIMAGE/home/$USERNAME"

log() { echo "[igloo-post] $*"; }

stage_chromium_plain() {
    local cname="$1" src="$2" stage staged_any f
    stage="$SYSIMAGE/var/lib/igloo/chromium/$cname"
    install -d -m 0700 "$stage" "$stage/Network"
    staged_any=0
    # A committed SQLite row can still live only in its WAL. Carry it with
    # the database; SQLite rebuilds the transient -shm file on Linux.
    for f in Bookmarks History History-wal Favicons Favicons-wal \
             "Top Sites" "Top Sites-wal" "Network/Cookies" \
             "Network/Cookies-wal"; do
        [ -f "$src/$f" ] || continue
        case "$f" in
            *-wal)
                if [ ! -f "$src/${f%-wal}" ]; then
                    log "ERROR: selected $cname WAL has no database: $f"
                    return 1
                fi
                ;;
        esac
        if ! cp -p "$src/$f" "$stage/$f"; then
            log "ERROR: could not stage selected $cname file $f"
            return 1
        fi
        chmod 0600 "$stage/$f"
        staged_any=1
    done
    if [ -d "$src/Sessions" ]; then
        if ! cp -a "$src/Sessions" "$stage/"; then
            log "ERROR: could not stage selected $cname Sessions"
            return 1
        fi
        staged_any=1
    fi
    [ "$staged_any" = "1" ] \
        && log "  Staged $cname profile files for the first-boot agent" \
        || rmdir "$stage/Network" "$stage" 2>/dev/null || true
}

log "Starting Igloo post-install (nochroot)"
log "  OEMDRV   : $OEMDRV"
log "  SYSIMAGE : $SYSIMAGE"
log "  USERNAME : $USERNAME"

# ── 1. Create Igloo directories ──────────────────────────────────────────────
install -d -m 0755 \
    "$SYSIMAGE/opt/igloo" \
    "$SYSIMAGE/var/lib/igloo" \
    "$SYSIMAGE/var/log/igloo"

# ── 2. Copy the first-boot agent ────────────────────────────────────────────
if [ -n "$OEMDRV" ] && [ -d "$OEMDRV/igloo-agent" ]; then
    # Use 'install' rather than 'cp -a' + 'chmod +x' so that:
    #   a) permissions are set atomically (not dependent on FAT32 mount options),
    #   b) CRLF line endings from Windows are stripped inline via process
    #      substitution - a \r in the shebang causes status=203/EXEC on Linux.
    for src in "$OEMDRV/igloo-agent"/*.sh "$OEMDRV/igloo-agent"/*.py; do
        [ -f "$src" ] || continue
        fname=$(basename "$src")
        # tr -d '\r' strips any Windows CRLF before writing to the target FS.
        tr -d '\r' < "$src" | install -m 0755 /dev/stdin "$SYSIMAGE/opt/igloo/$fname"
        log "  Installed $fname (0755, LF-only)"
    done
    # Binary payload: copied verbatim, never through tr, which would corrupt gzip.
    for src in "$OEMDRV/igloo-agent"/*.tar.gz; do
        [ -f "$src" ] || continue
        cp "$src" "$SYSIMAGE/opt/igloo/" && log "  Installed $(basename "$src") (binary)"
    done
    log "Agent installed to /opt/igloo"
fi

# ── 3. Copy the migration manifest ──────────────────────────────────────────
if [ -n "$OEMDRV" ] && [ -f "$OEMDRV/migration-manifest.json" ]; then
    install -m 0600 "$OEMDRV/migration-manifest.json" "$SYSIMAGE/var/lib/igloo/manifest.json"
    install -m 0600 "$OEMDRV/igloo-seed-identity.json" "$SYSIMAGE/var/lib/igloo/seed-identity.json"
    log "Manifest copied to /var/lib/igloo/manifest.json"
fi

# ── 3b. Copy exactly the selected, identity-verified media ──────────────────
# The validator above proved the manifest-selected names and their file hashes.
python3 - "$OEMDRV/migration-manifest.json" > /tmp/igloo-selected-media.lst <<'IGLOO_SELECTED_MEDIA'
import json
from pathlib import Path
import re
import sys

manifest = json.loads(Path(sys.argv[1]).read_bytes())
for key, pattern in (("wallpaper", r"igloo-wallpaper\.[A-Za-z0-9]{2,8}"),
                     ("accountPicture", r"igloo-avatar\.jpg")):
    item = manifest.get(key)
    if item is None:
        continue
    name = item.get("fileName") if isinstance(item, dict) else None
    valid = (isinstance(name, str) and
             (name == "igloo-avatar.jpg" if key == "accountPicture" else
              re.fullmatch(pattern, name, flags=re.IGNORECASE) is not None))
    if not valid:
        raise RuntimeError("Selected OEMDRV media has an unsafe file name")
    print(name)
IGLOO_SELECTED_MEDIA
while IFS= read -r media; do
    [ -f "$OEMDRV/$media" ] || { log "ERROR: selected media is missing: $media"; exit 1; }
    cp "$OEMDRV/$media" "$SYSIMAGE/opt/igloo/$media"
    log "Copied: $media"
done < /tmp/igloo-selected-media.lst

# ── 4. Copy user files ───────────────────────────────────────────────────────
# Core stages each selected folder under OEMDRV/files for USB and direct mode.

copy_user_files_done=0

prepare_private_user_copy_target() {
    local target="$1" relative component current redirected
    local -a components
    case "$target" in
        "$USER_HOME"/*) ;;
        *) log "ERROR: selected user destination is outside the installed home"; return 1 ;;
    esac
    if [ -L "$USER_HOME" ] || [ ! -d "$USER_HOME" ]; then
        log "ERROR: installed user home is missing or redirected"
        return 1
    fi
    relative="${target#"$USER_HOME"/}"
    IFS='/' read -r -a components <<< "$relative"
    current="$USER_HOME"
    for component in "${components[@]}"; do
        if [ -z "$component" ] || [ "$component" = "." ] || [ "$component" = ".." ]; then
            log "ERROR: selected user destination has an unsafe component"
            return 1
        fi
        current="$current/$component"
        if [ -L "$current" ] || { [ -e "$current" ] && [ ! -d "$current" ]; }; then
            log "ERROR: selected user destination is redirected or is not a directory"
            return 1
        fi
        if [ ! -d "$current" ]; then
            mkdir -m 0700 "$current"
        fi
    done
    # A pre-existing nested link can redirect cp -a even if the root itself is
    # a real directory. Reject it before copying or changing existing content.
    redirected=$(find "$target" -type l -print -quit) || return 1
    if [ -n "$redirected" ]; then
        log "ERROR: selected user destination contains a redirected entry"
        return 1
    fi
    chmod 0700 "$target"
}

if [ ! -d "$USER_HOME" ]; then
    log "ERROR: installed user home is missing: $USER_HOME"
    exit 1
fi
if [ -d "$USER_HOME" ]; then
    if [ -n "$OEMDRV" ] && [ -d "$OEMDRV/files" ]; then
        # ── Staged USB or direct mode ────────────────────────────────────────
        log "Copying selected staged user files from OEMDRV/files/ to $USER_HOME"
        while IFS= read -r folder_name; do
            [ -n "$folder_name" ] || continue
            folder_src="$OEMDRV/files/$folder_name"
            if [ ! -d "$folder_src" ]; then
                log "ERROR: selected folder is missing from OEMDRV/files: $folder_name"
                exit 1
            fi
            folder_dst="$USER_HOME/$folder_name"
            prepare_private_user_copy_target "$folder_dst"
            cp -a "$folder_src/." "$folder_dst/"
            find "$folder_dst" -type d -exec chmod 0700 {} +
            # Remove public FAT modes while keeping an existing owner's execute
            # bit on scripts in selected folders.
            find "$folder_dst" -type f -exec chmod go-rwx {} +
            log "  Copied $folder_name"
        done <<'STAGEDFOLDERS'

STAGEDFOLDERS
        copy_user_files_done=1
    fi
    if [ "$copy_user_files_done" != "1" ] && [ "0" = "1" ]; then
        # A username and directory name cannot prove which NTFS partition owns
        # the selected profile. Current Core stages selected content on OEMDRV.
        log "ERROR: selected Windows content is absent from the bound OEMDRV staging tree"
        exit 1
    fi
    # Current staging binds each selected browser to an explicit path inside
    # OEMDRV. Missing selected data is a migration failure, not a silent skip.
    while IFS='|' read -r brel bdst; do
        [ -n "$brel" ] || continue
        src="$OEMDRV/$brel"
        if [ ! -d "$src" ]; then
            log "ERROR: selected Gecko profile is missing from $brel"
            exit 1
        fi
        dst="$USER_HOME/$bdst"
        prepare_private_user_copy_target "$dst"
        # FAT stages expose copied profile modes as public. Keep the root
        # private throughout the copy, then normalize every copied entry before
        # the installed user can access the profile.
        cp -a "$src/." "$dst/"
        find "$dst" -type d -exec chmod 0700 {} +
        find "$dst" -type f -exec chmod 0600 {} +
        log "Copied selected Gecko profile $brel -> $bdst"
    done <<'STAGED_BROWSERMAP'

STAGED_BROWSERMAP

    while IFS='|' read -r cname brel; do
        [ -n "$cname" ] || continue
        src="$OEMDRV/$brel/Default"
        if [ ! -d "$src" ]; then
            log "ERROR: selected Chromium Default profile is missing from $brel"
            exit 1
        fi
        stage_chromium_plain "$cname" "$src"
    done <<'STAGED_CHROMIUMMAP'

STAGED_CHROMIUMMAP
fi

# The selected browser may be the only staged user data. Anaconda creates the
# account home, but these %post copies run as root and must belong to the user.
if [ -d "$USER_HOME" ]; then
    USER_UID=$(grep "^${USERNAME}:" "$SYSIMAGE/etc/passwd" 2>/dev/null | cut -d: -f3 || true)
    USER_GID=$(grep "^${USERNAME}:" "$SYSIMAGE/etc/passwd" 2>/dev/null | cut -d: -f4 || true)
    if [ -n "${USER_UID:-}" ] && [ -n "${USER_GID:-}" ]; then
        chown -R "${USER_UID}:${USER_GID}" "$USER_HOME"
        log "Ownership set to ${USER_UID}:${USER_GID} on $USER_HOME"
    else
        log "ERROR: could not determine uid:gid for $USERNAME"
        exit 1
    fi
fi

# ── 5. Register the igloo-first-boot systemd service ────────────────────────
cat > "$SYSIMAGE/etc/systemd/system/igloo-first-boot.service" <<'UNIT'
[Unit]
Description=Igloo first-boot migration agent
Documentation=https://github.com/gillesduif/iGloo
After=network-online.target
Wants=network-online.target
# Hold the login screen until first-boot setup finishes. The agent performs
# heavy package operations (RPM Fusion, codecs, NVIDIA driver build, Flatpaks)
# that must NOT race the Plasma session start - doing so leaves the user on a
# half-initialised desktop (black screen). Ordering before the display manager
# means setup completes (and, for NVIDIA, the machine reboots) before any
# graphical session is created.
Before=display-manager.service
ConditionPathExists=/var/lib/igloo/manifest.json

[Service]
Type=oneshot
ExecStart=/opt/igloo/first-boot.sh
RemainAfterExit=yes
StandardOutput=append:/var/log/igloo/first-boot.log
StandardError=append:/var/log/igloo/first-boot.log
# The agent builds the NVIDIA kernel module for EVERY installed kernel (an install
# commonly ends up with two), and the open-module variant that Blackwell GPUs
# require is compiled from full source - noticeably slower than the proprietary
# one. 1800s was sized for a single proprietary build; being killed part-way leaves
# a kernel with no GPU driver, which is precisely the failure this is meant to
# prevent. Doubled to give the worst realistic case room.
TimeoutStartSec=3600

[Install]
WantedBy=multi-user.target
UNIT

# Enable via chroot; fall back to manual symlink if systemctl isn't accessible.
chroot "$SYSIMAGE" systemctl enable igloo-first-boot.service 2>/dev/null || \
    ln -sf /etc/systemd/system/igloo-first-boot.service \
           "$SYSIMAGE/etc/systemd/system/multi-user.target.wants/igloo-first-boot.service"

log "igloo-first-boot.service registered"

# Anaconda's unattended cmdline mode leaves the installed system at
# multi-user.target. Fedora 44 KDE includes Plasma Login Manager rather than
# SDDM; explicitly enable its display-manager alias and the graphical target.
# Fail the install if the KDE login screen cannot be made reachable.
chroot "$SYSIMAGE" rpm -q plasma-login-manager >/dev/null
chroot "$SYSIMAGE" systemctl enable --force plasmalogin.service
chroot "$SYSIMAGE" systemctl set-default graphical.target
log "Plasma Login Manager and graphical.target enabled"

# Fedora 44's KDE package group also enables Plasma Setup, whose wizard asks
# for a second account before the login screen. This kickstart has already
# created and verified the migrated account, so use the wizard's own completion
# flag to let the display manager show that account directly.
touch "$SYSIMAGE/etc/plasma-setup-done"
log "Skipped duplicate Plasma Setup account wizard"

# ── 6. Enable os-prober so GRUB detects Windows for dual-boot ───────────────
# Fedora 37+ disables os-prober by default (GRUB 2.06 security change).
# Without this, grub2-mkconfig never scans for Windows Boot Manager and the
# GRUB menu only shows Fedora - end users cannot get back to Windows.
if ! grep -q "GRUB_DISABLE_OS_PROBER=false" "$SYSIMAGE/etc/default/grub" 2>/dev/null; then
    echo "GRUB_DISABLE_OS_PROBER=false" >> "$SYSIMAGE/etc/default/grub"
    log "Enabled os-prober (GRUB_DISABLE_OS_PROBER=false)"
fi

# Leave BootLoaderSpec behind: BLS titles are kernel version strings that cannot
# be renamed, so the M15 menu work has nothing to hook into. With it off,
# grub2-mkconfig emits classic menuentries, the same shape as Debian and Mint.
# Verified on Fedora 44: 99-grub-mkconfig.install re-runs grub2-mkconfig on
# kernel install when this is false (RHBZ 1899903), and grubby-deprecated is
# neither available nor needed.
# Replace, do not skip. Anaconda writes GRUB_ENABLE_BLSCFG=true itself, so a
# guard of the "add it only when absent" shape never fires and the setting
# silently stays on - measured on the Fedora 44 install of 2026-08-23, whose
# /etc/default/grub still read "true" and whose agent then reported
# "VERIFICATION FAILED: the Advanced options submenu is still in grub.cfg".
if grep -q "^GRUB_ENABLE_BLSCFG=" "$SYSIMAGE/etc/default/grub" 2>/dev/null; then
    sed -i 's/^GRUB_ENABLE_BLSCFG=.*/GRUB_ENABLE_BLSCFG=false/' \
        "$SYSIMAGE/etc/default/grub"
    log "Disabled BootLoaderSpec (replaced an existing GRUB_ENABLE_BLSCFG line)"
else
    echo "GRUB_ENABLE_BLSCFG=false" >> "$SYSIMAGE/etc/default/grub"
    log "Disabled BootLoaderSpec (GRUB_ENABLE_BLSCFG=false)"
fi
# Regenerate GRUB config now that os-prober is enabled.
# Anaconda already ran grub2-mkconfig during install (without os-prober).
# We run it again here so a detected Windows Boot Manager is included.
chroot "$SYSIMAGE" grub2-mkconfig -o /boot/grub2/grub.cfg 2>/dev/null \
    && log "GRUB config regenerated" \
    || log "WARNING: grub2-mkconfig failed; run 'sudo grub2-mkconfig -o /boot/grub2/grub.cfg' after first boot"

# The installer seed is mounted read-only. EFI and partition cleanup wait for
# an exact owned partition and boot-entry identity; a label or description
# alone cannot authorize deleting either one.

# Unmount OEMDRV if we mounted it.
if [ -n "$OEMDRV" ] && mountpoint -q "$OEMDRV" 2>/dev/null; then
    umount "$OEMDRV" 2>/dev/null || true
fi

log "Post-install complete"
%end
