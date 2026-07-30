#!/usr/bin/env python3
"""
iGloo KDE display-layout applier.

Runs once per user at the first Plasma login (via
/etc/xdg/autostart/igloo-display-layout.desktop -> display-apply.sh), because
kscreen-doctor needs a running KWin session - the first-boot agent executes as
root before any session exists, so it stages /opt/igloo/display-layout.json
and this script does the actual apply.

The layout is keyed by EDID PnP id, not connector name: connector names are
not stable across kernels/drivers (the same physical port appeared as DP-4
under kernel 6.19.10/nouveau and as DP-1 under 7.1.5/nvidia on the RTX 5070
bare-metal test machine). The PnP id comes from the monitor's own EDID, so it
always matches what KWin sees.

Exit code 0 = layout applied (or nothing to apply); 1 = transient failure,
the wrapper leaves the done-marker absent so the next login retries.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

LOG_PATH = Path.home() / ".local/state/igloo-display.log"


def log(msg: str) -> None:
    line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}"
    print(line, flush=True)
    try:
        LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
        with LOG_PATH.open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass


def edid_pnp_id(edid: bytes) -> str | None:
    """EDID bytes 8-11: manufacturer (3 letters) + product code, e.g. 'SAM E06F'.

    Same identity Windows reports in the monitor device id, which is what the
    manifest and the staged layout use.
    """
    if len(edid) < 12:
        return None
    raw = (edid[8] << 8) | edid[9]
    vendor = "".join(chr(((raw >> shift) & 0x1F) + 0x40) for shift in (10, 5, 0))
    if not vendor.isalpha():
        return None
    product_code = edid[10] | (edid[11] << 8)
    return f"{vendor}{product_code:04X}"


def connector_by_pnp() -> dict[str, str]:
    """Map PnP id -> current DRM connector name for every connected output."""
    mapping: dict[str, str] = {}
    for card in sorted(Path("/sys/class/drm").glob("card*-*")):
        try:
            if (card / "status").read_text().strip() != "connected":
                continue
            pnp = edid_pnp_id((card / "edid").read_bytes())
        except OSError:
            continue
        if pnp:
            # "card1-HDMI-A-1" -> "HDMI-A-1", the name kscreen-doctor uses.
            mapping[pnp.upper()] = card.name.split("-", 1)[1]
    return mapping


def wait_for_kscreen(timeout_s: int = 90) -> bool:
    """Wait until kscreen-doctor can talk to KWin (session may still be starting)."""
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        res = subprocess.run(["kscreen-doctor", "-o"], capture_output=True, timeout=15)
        if res.returncode == 0:
            return True
        time.sleep(3)
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply iGloo display layout via kscreen-doctor")
    parser.add_argument("--layout", required=True, type=Path)
    args = parser.parse_args()

    try:
        layout = json.loads(args.layout.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        # Nothing (valid) to apply - treat as done so we do not retry forever.
        log(f"no usable layout at {args.layout} ({exc}) - nothing to do")
        return 0
    if not layout:
        log("layout is empty - nothing to do")
        return 0

    if not wait_for_kscreen():
        log("kscreen-doctor could not reach KWin within 90s - will retry at next login")
        return 1

    by_pnp = connector_by_pnp()
    log(f"connected outputs by PnP id: {by_pnp}")

    # One atomic kscreen-doctor call: per output set mode, rotation, scale,
    # position; priority 1 marks the primary (Plasma 6 convention), the rest follow.
    #
    # KWin positions outputs in LOGICAL pixels, but the staged layout carries
    # Windows' PHYSICAL pixel positions: with 150% scaling a 3840-wide primary is
    # only 2560 logical wide, so a raw x=3840 would leave a gap KWin refuses
    # ("gaps between screens are not supported"). Convert by dividing by the
    # PRIMARY's scale - exact when every screen shares one scale (the common
    # case); mixed per-monitor scales would need a proper geometric layout pass
    # and are approximated here.
    primary_scale = 1.0
    for m in layout:
        if m.get("primary"):
            try:
                primary_scale = float(m.get("scale") or 1.0) or 1.0
            except (TypeError, ValueError):
                primary_scale = 1.0
            break

    cmd: list[str] = ["kscreen-doctor"]
    applied = 0
    priority = 1
    for mon in sorted(layout, key=lambda m: not m.get("primary")):
        conn = by_pnp.get(str(mon.get("pnpId", "")).upper())
        if not conn:
            log(f"monitor {mon.get('pnpId')} is not attached - skipped")
            continue
        w, h, rate = int(mon["width"]), int(mon["height"]), int(mon.get("rate") or 60)
        try:
            scale = float(mon.get("scale") or 1.0) or 1.0
        except (TypeError, ValueError):
            scale = 1.0
        x = round(int(mon.get("x", 0)) / primary_scale)
        y = round(int(mon.get("y", 0)) / primary_scale)
        cmd += [
            f"output.{conn}.mode.{w}x{h}@{rate}",
            f"output.{conn}.rotation.{mon.get('rotation', 'none')}",
            # %g keeps 1.0 -> "1" and 1.5 -> "1.5", the forms kscreen-doctor expects.
            f"output.{conn}.scale.{scale:g}",
            f"output.{conn}.position.{x},{y}",
            f"output.{conn}.priority.{priority}",
        ]
        priority += 1
        applied += 1

    if applied == 0:
        log("no staged monitor matched an attached output - nothing to do")
        return 0

    log("running: " + " ".join(cmd))
    res = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
    if res.returncode != 0:
        log(f"kscreen-doctor failed (exit {res.returncode}): "
            f"{(res.stderr or res.stdout or '').strip()[:400]} - will retry at next login")
        return 1

    log(f"layout applied to {applied} output(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
