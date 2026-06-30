#!/usr/bin/env bash
# Igloo first-boot launcher (Debian family).
# Runs the migration agent exactly once, then reboots if a driver install needs
# a clean boot before the display manager starts.
set -uo pipefail

mkdir -p /var/log/igloo
python3 /opt/igloo/agent.py \
    --manifest /var/lib/igloo/manifest.json \
    --log-dir  /var/log/igloo

# Mark complete so this one-shot service never runs again
# (the unit has ConditionPathExists=!/var/lib/igloo/.done).
touch /var/lib/igloo/.done

# A driver step (e.g. NVIDIA) asked for a clean reboot before the desktop.
if [ -f /var/lib/igloo/.reboot-required ]; then
    rm -f /var/lib/igloo/.reboot-required
    systemctl reboot
fi

exit 0
