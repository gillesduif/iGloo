#!/usr/bin/env bash
# Igloo first-boot launcher (Debian family). The agent pass is committed once;
# later boots only finish backup cleanup or a requested driver reboot.
set -euo pipefail
umask 077

STATE_DIR=/var/lib/igloo
BOOT_ID_PATH=/proc/sys/kernel/random/boot_id

sync_state_dir() {
    python3 - <<'PY'
import os
fd = os.open('/var/lib/igloo', os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY
}

remove_retry_backups() {
    if [ -e "$STATE_DIR/seed-retry" ]; then
        rm -rf -- "$STATE_DIR/seed-retry" || return 1
    fi
    if [ -e "$STATE_DIR/manifest.retry.json" ]; then
        rm -f -- "$STATE_DIR/manifest.retry.json" || return 1
    fi
    sync_state_dir
}

request_pending_reboot() {
    local requested_boot_id current_boot_id
    local journal="$STATE_DIR/.reboot-requested-boot-id"
    if [ ! -f "$STATE_DIR/.reboot-required" ]; then
        # A crash after retiring the request on the next boot may leave only
        # the journal. It has no remaining reboot authority.
        if [ -e "$journal" ]; then
            rm -f -- "$journal"
            sync_state_dir
        fi
        return 0
    fi
    current_boot_id=$(cat "$BOOT_ID_PATH")
    [[ "$current_boot_id" =~ ^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$ ]] || return 1
    if [ -f "$journal" ]; then
        requested_boot_id=$(cat "$journal")
        [[ "$requested_boot_id" =~ ^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$ ]] || return 1
        if [ "$requested_boot_id" != "$current_boot_id" ]; then
            # A different kernel boot id proves that the requested reboot
            # occurred. Keep the journal until the request removal is durable.
            rm -f -- "$STATE_DIR/.reboot-required"
            sync_state_dir
            rm -f -- "$journal"
            sync_state_dir
            return 0
        fi
    fi
    # Retain the original reason and a durable same-boot request until an
    # actual later boot is observed. A SIGTERM after systemctl accepts reboot
    # cannot silently consume the request.
    python3 - "$current_boot_id" <<'PY'
import os
from pathlib import Path
import sys

state = Path('/var/lib/igloo')
temporary = state / '.reboot-requested-boot-id.tmp'
fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
with os.fdopen(fd, 'w') as stream:
    stream.write(sys.argv[1] + '\n')
    stream.flush()
    os.fsync(stream.fileno())
os.replace(temporary, state / '.reboot-requested-boot-id')
dirfd = os.open(state, os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(dirfd)
finally:
    os.close(dirfd)
PY
    if ! systemctl reboot; then
        return 1
    fi
}

if [ -f "$STATE_DIR/.done" ]; then
    remove_retry_backups
    request_pending_reboot
    exit 0
fi

mkdir -p /var/log/igloo
python3 /opt/igloo/agent.py \
    --manifest "$STATE_DIR/manifest.json" \
    --log-dir  /var/log/igloo

# Write and fsync the completion marker before deleting the only protected
# retry copy of the original manifest. If cleanup fails, the durable marker
# prevents an unsafe second agent pass; the service retries cleanup next boot.
python3 - <<'PY'
import os
from pathlib import Path

state = Path('/var/lib/igloo')
temporary = state / '.done.tmp'
with temporary.open('wb') as stream:
    stream.write(b'1\n')
    stream.flush()
    os.fsync(stream.fileno())
os.replace(temporary, state / '.done')
fd = os.open(state, os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY

remove_retry_backups
request_pending_reboot
exit 0
