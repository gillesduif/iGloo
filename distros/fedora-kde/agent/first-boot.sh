#!/usr/bin/env bash
# Igloo first-boot agent entry point.
# Runs once on first boot via the igloo-first-boot.service systemd unit.

set -euo pipefail

LOG_DIR=/var/log/igloo
MANIFEST=/var/lib/igloo/manifest.json
DONE_MARKER=/var/lib/igloo/.done
REBOOT_MARKER=/var/lib/igloo/.reboot-required
AGENT_DIR=/opt/igloo

mkdir -p "$LOG_DIR"
log() { echo "[$(date -u '+%Y-%m-%dT%H:%M:%SZ')] $*"; }

if [ -f "$DONE_MARKER" ]; then log "already done; exiting"; exit 0; fi
if [ ! -f "$MANIFEST" ]; then log "no manifest at $MANIFEST"; touch "$DONE_MARKER"; exit 0; fi

log "starting first-boot agent (manifest: $MANIFEST)"
python3 "$AGENT_DIR/agent.py" --manifest "$MANIFEST" --log-dir "$LOG_DIR"

# Mark done BEFORE any reboot so the service never runs a second time.
touch "$DONE_MARKER"
log "first-boot agent finished"

# The agent writes this marker when it installed something that only takes
# effect after a clean boot (currently: the NVIDIA proprietary driver, which
# blacklists nouveau and builds a kernel module loaded on next boot).
# Because this unit is ordered Before=display-manager.service, rebooting here
# means the user never sees the broken first session — they boot straight into
# a working desktop on the new driver.
if [ -f "$REBOOT_MARKER" ]; then
    rm -f "$REBOOT_MARKER"
    log "reboot-required marker present — rebooting into a clean session"
    systemctl reboot
fi
