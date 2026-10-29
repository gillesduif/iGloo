#!/usr/bin/env bash
# Igloo first-boot agent entry point (Bazzite DX).
# Runs once on first boot via the igloo-first-boot.service systemd unit.

set -euo pipefail

LOG_DIR=/var/log/igloo
MANIFEST=/var/lib/igloo/manifest.json
DONE_MARKER=/var/lib/igloo/.done
AGENT_DIR=/opt/igloo

mkdir -p "$LOG_DIR"
log() { echo "[$(date -u '+%Y-%m-%dT%H:%M:%SZ')] $*"; }

if [ -f "$DONE_MARKER" ]; then log "already done; exiting"; exit 0; fi
if [ ! -f "$MANIFEST" ]; then log "no manifest at $MANIFEST"; touch "$DONE_MARKER"; exit 0; fi

log "starting first-boot agent (manifest: $MANIFEST)"
# || true: a crashing agent must not take the log export down with it.
python3 "$AGENT_DIR/agent.py" --manifest "$MANIFEST" --log-dir "$LOG_DIR" \
    || log "WARNING: agent exited nonzero - continuing to log export"

# Unconditional log export to the FAT32 seed, readable straight from Windows.
# The agent does this too, but only when it reaches that step.
EXPORT_MP=/run/igloo-seed-export
for label in OEMDRV CIDATA IGLOOISO; do
    dev="/dev/disk/by-label/$label"
    [ -e "$dev" ] || continue
    mkdir -p "$EXPORT_MP"
    if mount -t vfat "$dev" "$EXPORT_MP" 2>/dev/null; then
        mkdir -p "$EXPORT_MP/igloo-logs"
        cp "$LOG_DIR"/*.log "$EXPORT_MP/igloo-logs/" 2>/dev/null || true
        cp /var/log/igloo-*.log "$EXPORT_MP/igloo-logs/" 2>/dev/null || true
        [ -d /var/log/anaconda ] && { mkdir -p "$EXPORT_MP/igloo-logs/anaconda"; cp /var/log/anaconda/*.log "$EXPORT_MP/igloo-logs/anaconda/" 2>/dev/null || true; }
        log "logs exported to $label ($dev)"
        umount "$EXPORT_MP" 2>/dev/null
    else
        log "could not mount $dev for log export"
    fi
    break
done

# No reboot: the GPU driver ships in the image, so the first session already
# runs on the right driver.
touch "$DONE_MARKER"
log "first-boot agent finished"
