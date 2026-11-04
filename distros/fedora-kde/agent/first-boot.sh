#!/usr/bin/env bash
# Igloo first-boot agent entry point.
# Runs through igloo-first-boot.service on every boot; the durable .done path
# completes without rerunning migration after the first successful boot.

set -euo pipefail

LOG_DIR=/var/log/igloo
MANIFEST=/var/lib/igloo/manifest.json
DONE_MARKER=/var/lib/igloo/.done
REBOOT_MARKER=/var/lib/igloo/.reboot-required
REBOOT_REQUESTED_BOOT_ID=/var/lib/igloo/.reboot-requested-boot-id
AGENT_DIR=/opt/igloo
SCRUB_JOURNAL=/var/lib/igloo/seed-scrub-state.json

mkdir -p "$LOG_DIR"
log() { echo "[$(date -u '+%Y-%m-%dT%H:%M:%SZ')] $*"; }

finish_success() {
    # Publish completion durably before removing the hash-only retry journal.
    # A crash between these actions is harmless: the next boot only repeats
    # journal cleanup and never reruns migration.
    python3 - "$DONE_MARKER" "$SCRUB_JOURNAL" <<'PY'
import os
import sys
from pathlib import Path

done, journal = map(Path, sys.argv[1:])
directory = done.parent
if not done.exists():
    temporary = done.with_name(done.name + ".igloo-writing")
    with temporary.open("wb") as output:
        output.write(b"complete\n")
        output.flush()
        os.fsync(output.fileno())
    os.replace(temporary, done)
    fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
journal.unlink(missing_ok=True)
fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY
}

request_pending_reboot() {
    if [ ! -f "$REBOOT_MARKER" ]; then
        return 0
    fi

    # Keep the request until a different kernel boot ID proves that a reboot
    # happened. Removing it before systemctl reboot loses the request if this
    # process is interrupted between those two operations.
    action="$(python3 - "$REBOOT_MARKER" "$REBOOT_REQUESTED_BOOT_ID" <<'PY'
import os
import sys
import uuid
from pathlib import Path

marker, requested = map(Path, sys.argv[1:])
boot_id = str(uuid.UUID(Path('/proc/sys/kernel/random/boot_id').read_text().strip()))
if requested.is_symlink():
    raise RuntimeError('Reboot request journal is redirected')
if requested.exists():
    previous = str(uuid.UUID(requested.read_text().strip()))
    if previous != boot_id:
        marker.unlink()
        requested.unlink()
        fd = os.open(marker.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
        print('complete')
        sys.exit(0)
else:
    # A crash may strand one temporary; a new invocation can still publish its
    # own boot ID and complete the pending reboot request.
    temporary = requested.with_name(requested.name + '.' + uuid.uuid4().hex + '.tmp')
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, 'w', encoding='ascii') as output:
            output.write(boot_id + '\n')
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, requested)
        directory = os.open(marker.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        temporary.unlink(missing_ok=True)
print('reboot')
PY
)"
    if [ "$action" = reboot ]; then
        log "reboot-required marker present - rebooting into a clean session"
        systemctl reboot
    fi
}

if [ -f "$DONE_MARKER" ]; then
    finish_success
    log "already done; retaining completed migration state"
    request_pending_reboot
    exit 0
fi
if [ ! -f "$MANIFEST" ]; then log "no manifest at $MANIFEST"; exit 1; fi

log "starting first-boot agent (manifest: $MANIFEST)"
# Preserve the agent exit status. Its bound-seed export step copies logs when
# reached; an earlier failure leaves the logs under /var/log/igloo for retry.
agent_status=0
python3 "$AGENT_DIR/agent.py" --manifest "$MANIFEST" --log-dir "$LOG_DIR" || agent_status=$?

if [ "$agent_status" -ne 0 ]; then
    log "first-boot agent failed (exit $agent_status); retaining inputs for retry"
    exit "$agent_status"
fi

# Mark done BEFORE any reboot so migration never runs a second time.
finish_success
log "first-boot agent finished"

# The agent writes this marker when it installed something that only takes
# effect after a clean boot (currently: the NVIDIA proprietary driver, which
# blacklists nouveau and builds a kernel module loaded on next boot).
# Because this unit is ordered Before=display-manager.service, rebooting here
# means the user never sees the broken first session - they boot straight into
# a working desktop on the new driver.
request_pending_reboot
