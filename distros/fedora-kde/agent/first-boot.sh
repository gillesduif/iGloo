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
# || true: a crashing agent must not take the log export and the reboot logic
# down with it (set -e would abort the script mid-run otherwise).
python3 "$AGENT_DIR/agent.py" --manifest "$MANIFEST" --log-dir "$LOG_DIR" \
    || log "WARNING: agent exited nonzero - continuing to log export"

# Unconditional log export to the FAT32 seed (OEMDRV/CIDATA). The agent's own
# export-logs step does this too, but only when the run REACHES that step; a
# crashed or killed agent would otherwise leave a blind failure. The seed is
# readable straight from Windows, and in USB mode it is never deleted.
EXPORT_MP=/run/igloo-seed-export
for label in OEMDRV CIDATA IGLOOISO; do
    dev="/dev/disk/by-label/$label"
    [ -e "$dev" ] || continue
    mkdir -p "$EXPORT_MP"
    if mount -t vfat "$dev" "$EXPORT_MP" 2>/dev/null; then
        mkdir -p "$EXPORT_MP/igloo-logs"
        cp "$LOG_DIR"/*.log "$EXPORT_MP/igloo-logs/" 2>/dev/null || true
        [ -d /var/log/anaconda ] && { mkdir -p "$EXPORT_MP/igloo-logs/anaconda"; cp /var/log/anaconda/*.log "$EXPORT_MP/igloo-logs/anaconda/" 2>/dev/null || true; }
        # The akmods build logs carry the actual compiler error when the
        # nvidia module fails for a kernel - the agent log only says THAT.
        [ -d /var/cache/akmods ] && find /var/cache/akmods -name '*.log' -exec cp {} "$EXPORT_MP/igloo-logs/" \; 2>/dev/null || true
        log "logs exported to $label ($dev)"
        umount "$EXPORT_MP" 2>/dev/null
    else
        log "could not mount $dev for log export"
    fi
    break
done

# Mark done BEFORE any reboot so the service never runs a second time.
touch "$DONE_MARKER"
log "first-boot agent finished"

# The agent writes this marker when it installed something that only takes
# effect after a clean boot (currently: the NVIDIA proprietary driver, which
# blacklists nouveau and builds a kernel module loaded on next boot).
# Because this unit is ordered Before=display-manager.service, rebooting here
# means the user never sees the broken first session - they boot straight into
# a working desktop on the new driver.
if [ -f "$REBOOT_MARKER" ]; then
    rm -f "$REBOOT_MARKER"
    log "reboot-required marker present - rebooting into a clean session"
    systemctl reboot
fi
