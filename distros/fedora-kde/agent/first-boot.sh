#!/usr/bin/env bash
# Igloo first-boot agent entry point.
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
python3 "$AGENT_DIR/agent.py" --manifest "$MANIFEST" --log-dir "$LOG_DIR"
touch "$DONE_MARKER"
log "first-boot agent finished"
