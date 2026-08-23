#!/usr/bin/env python3
"""
iGloo GNOME (Wayland) display-layout applier.

Runs once per user at the first GNOME login (via
/etc/xdg/autostart/igloo-display-layout.desktop -> display-apply.sh).

Why this exists: the boot-time ~/.config/monitors.xml is only a SEED. Mutter
reads it, normalizes it, and then quietly declines to apply it whenever any
detail fails its own validation - on the Debian 13 bare-metal run (RTX 5070)
the session came up 60 Hz/landscape while the file on disk carried the
perfectly reasonable 144 Hz/portrait layout. The file path gives no error, no
log, no way to match the panel's exact advertised rate (143.99x, not 144).

This script instead uses mutter's own D-Bus API - the same one
gnome-control-center drives - so every guess disappears:

  * GetCurrentState returns the EXACT mode ids mutter accepts, refresh rates
    included; the wanted mode is matched by resolution and nearest rate.
  * ApplyMonitorsConfig applies the layout immediately, in-session, with the
    temporary method - no prompt. Keeping it across logouts stays the file's
    job; the persistent method would ask the user to confirm instead.
  * Monitors are matched by EDID identity (vendor/product/serial), the same
    identity Windows and the first-boot agent use, never by output order.

Exit code 0 = layout applied (or nothing to apply); 1 = transient failure,
the wrapper leaves the done-marker absent so the next login retries.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
from pathlib import Path

LOG_PATH = Path.home() / ".local/state/igloo-display.log"

GDBUS_DEST = "org.gnome.Shell"
GDBUS_PATH = "/org/gnome/Mutter/DisplayConfig"
GDBUS_IFACE = "org.gnome.Mutter.DisplayConfig"

# monitors.xml rotation word -> MetaMonitorTransform (wayland output transform:
# 0 normal, 1 = 90 CCW ("left"), 2 = 180, 3 = 270 CCW = 90 CW ("right")).
ROTATION_TO_TRANSFORM = {"none": 0, "left": 1, "inverted": 2, "right": 3}

# org.gnome.Mutter.DisplayConfig.ApplyMonitorsConfig: "0: verify 1: temporary
# 2: persistent". Temporary, deliberately: persistence comes from the
# monitors.xml the agent writes, which mutter reads at every session start. 2
# makes mutter run request_persistent_confirmation() instead - the "Keep this
# display setup?" prompt, which reverts after 20 seconds if nobody clicks. That
# is unacceptable on a machine the user has just migrated to, and buys nothing
# the file does not already give us.
APPLY_METHOD_TEMPORARY = 1


def log(msg: str) -> None:
    line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}"
    print(line, flush=True)
    try:
        LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
        with LOG_PATH.open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass  # logging must never crash the agent; stdout already has the line


def gdbus_call(method: str, *args: str, timeout: int = 20) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["gdbus", "call", "--session", "--dest", GDBUS_DEST,
         "--object-path", GDBUS_PATH, "--method", f"{GDBUS_IFACE}.{method}", *args],
        capture_output=True, text=True, timeout=timeout)


def wait_for_shell(timeout_s: int = 90) -> bool:
    """Wait until gnome-shell answers GetCurrentState (session may be starting)."""
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if gdbus_call("GetCurrentState", timeout=15).returncode == 0:
            return True
        time.sleep(3)
    return False


#   GetCurrentState parsing (GVariant text)                                 

_MONITOR_START = re.compile(
    r"\(\('([^']+)',\s*'([^']*)',\s*'([^']*)',\s*'([^']*)'\),\s*\[")
_MODE = re.compile(
    r"\('([^']+)',\s*(\d+),\s*(\d+),\s*([0-9]+(?:\.[0-9]+)?),\s*"
    r"([0-9]+(?:\.[0-9]+)?),\s*\[([0-9.,\s]*)\]")


def parse_current_state(text: str) -> tuple[int | None, dict[str, dict]]:
    """Parse the GVariant text returned by GetCurrentState into a dict of monitors."""
    # The monitors array is the only place 4-tuples of quoted strings followed
    # by '[' appear at this nesting level, so the start-regex cannot match
    # anything else. Mode tuples are matched inside each monitor's slice.
    serial_m = re.search(r"\(uint32 (\d+),", text)
    serial = int(serial_m.group(1)) if serial_m else None

    starts = list(_MONITOR_START.finditer(text))
    monitors: dict[str, dict] = {}

    for i, m in enumerate(starts):
        end = starts[i + 1].start() if i + 1 < len(starts) else len(text)
        block = text[m.start():end]
        modes = []

        for mm in _MODE.finditer(block):
            scales = [float(s) for s in mm.group(6).split(",") if s.strip()] or [1.0]
            modes.append({
                "id": mm.group(1),
                "width": int(mm.group(2)),
                "height": int(mm.group(3)),
                "rate": float(mm.group(4)),
                "scales": scales,
            })

        if modes:
            monitors[m.group(1)] = {
                "connector": m.group(1),
                "vendor": m.group(2),
                "product": m.group(3),
                "serial": m.group(4),
                "modes": modes,
            }

    return serial, monitors


def _norm_connector(name: str) -> str:
    """Normalize connector names to ignore the "-A-" vs "-B-" suffixes."""
    return (name or "").replace("-A-", "-")


def pick_mode(modes: list[dict], width: int, height: int, rate: int) -> dict | None:
    """Exact resolution, nearest refresh rate (Windows' whole-Hz 144 vs 143.99x)."""
    candidates = [m for m in modes if m["width"] == width and m["height"] == height]

    if not candidates:
        return None
    
    return min(candidates, key=lambda m: abs(m["rate"] - rate))


def pick_scale(supported: list[float], target: float) -> float:
    """Closest scale mutter allows (fractional scaling may be off -> integers)."""
    return min(supported, key=lambda s: (abs(s - target), s))


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply iGloo display layout via mutter D-Bus (GNOME)")
    parser.add_argument("--layout", required=True, type=Path)
    args = parser.parse_args()

    try:
        layout = json.loads(args.layout.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        log(f"no usable layout at {args.layout} ({exc}) - nothing to do")
        return 0
    if not layout:
        log("layout is empty - nothing to do")
        return 0

    if not wait_for_shell():
        log("gnome-shell did not answer within 90s - will retry at next login")
        return 1

    state = gdbus_call("GetCurrentState", timeout=20)
    serial, monitors = parse_current_state(state.stdout or "")
    if serial is None or not monitors:
        log("could not parse GetCurrentState - will retry at next login")
        return 1
    log(f"mutter serial={serial}, connectors: {sorted(monitors)}")

    # Match staged monitors to mutter's live monitors: connector name first
    # (same boot, names are stable), EDID identity as fallback - serials can be
    # duplicated, see docs/reference/hardware-findings.md #8.
    live_by_norm = {_norm_connector(c["connector"]): c for c in monitors.values()}
    used: set[str] = set()
    wanted: list[dict] = []
    for mon in layout:
        hit = None
        cand = live_by_norm.get(_norm_connector(mon.get("connector")))
        if cand is not None and cand["connector"] not in used:
            hit = cand
        if hit is None:
            for cand in monitors.values():
                if cand["connector"] in used:
                    continue
                if (mon.get("vendor"), mon.get("product"), mon.get("serial")) == \
(cand["vendor"], cand["product"], cand["serial"]):
                    hit = cand
                    break
        if hit is None:
            log(f"monitor {mon.get('pnpId')} ({mon.get('product')}) is not attached - skipped")
            continue
        used.add(hit["connector"])
        if hit["connector"] != mon.get("connector"):
            log(f"staged connector {mon.get('connector')} matched live {hit['connector']}")
        mode = pick_mode(hit["modes"], int(mon["width"]), int(mon["height"]), int(mon.get("rate") or 60) or 60)
        if mode is None:
            log(f"{hit['connector']} advertises no {mon['width']}x{mon['height']} mode - skipped")
            continue
        wanted.append({**mon, "_live": hit, "_mode": mode})

    if not wanted:
        log("no staged monitor matched an attached output - nothing to do")
        return 0

    #   Scale + logical positions (same convention as the Fedora KDE hook)   
    primary = next((m for m in wanted if m.get("primary")), wanted[0])
    primary_scale = pick_scale(primary["_mode"]["scales"], (int(primary.get("scalePercent") or 100) or 100) / 100.0)

    logical_monitors: list[str] = []
    for m in wanted:
        conn = m["_live"]["connector"]
        scale = pick_scale(m["_mode"]["scales"], (int(m.get("scalePercent") or 100) or 100) / 100.0)
        x = round(int(m.get("x", 0)) / primary_scale)
        y = round(int(m.get("y", 0)) / primary_scale)
        transform = ROTATION_TO_TRANSFORM.get(str(m.get("rotation", "none")), 0)
        is_primary = "true" if m.get("primary") else "false"

        # Must send full tuple (ssa{sv}) including properties dict.
        # Omitting it causes Mutter to reject the call (observed RTX 5070, July 2026).

        logical_monitors.append(
            f"({x}, {y}, {scale}, {transform}, {is_primary}, "
            f"[('{conn}', '{m['_mode']['id']}', {{}})])")
        log(f"  {conn} -> {m['_mode']['id']} scale={scale} transform={transform} "
            f"at ({x},{y}){' primary' if m.get('primary') else ''}")

    variant = "[" + ", ".join(logical_monitors) + "]"
    # Log the number, not a word for it: the word said "persistent" while the
    # number said temporary, and the log looked correct for weeks.
    log(f"applying via ApplyMonitorsConfig(serial={serial}, method={APPLY_METHOD_TEMPORARY})")
    res = gdbus_call("ApplyMonitorsConfig", str(serial), str(APPLY_METHOD_TEMPORARY),
                     variant, "{}", timeout=30)
    if res.returncode != 0:
        log(f"ApplyMonitorsConfig failed: {(res.stderr or res.stdout or '').strip()[:400]}"
            " - will retry at next login")
        return 1

    log(f"layout applied to {len(wanted)} output(s); mutter persists it itself now")
    return 0


if __name__ == "__main__":
    sys.exit(main())
