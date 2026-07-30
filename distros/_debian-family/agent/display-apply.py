#!/usr/bin/env python3
"""
iGloo Cinnamon (X11) display-layout applier.

Runs once per user at the first Cinnamon login (via
/etc/xdg/autostart/igloo-display-layout.desktop -> display-apply.sh), for two
reasons that both come down to "the first-boot agent runs too early":

1. The agent executes as root before any X server exists, so it cannot query
   RandR. It writes ~/.config/monitors.xml with KERNEL DRM connector names
   (DP-4, HDMI-A-2) - which is what Wayland compositors (Mutter/Muffin) use,
   so that copy stays correct for GNOME-on-Wayland.

2. Cinnamon on X11 (Linux Mint's default session) matches monitors.xml
   against RANDR output names, and the NVIDIA X driver names those
   DIFFERENTLY (DP-0, HDMI-0). The boot-time file therefore matched nothing
   and Cinnamon discarded it - observed on the RTX 5070 Mint bare-metal test
   (July 2026): monitors.xml gone by first login, layout stuck at defaults
   (60 Hz, no rotation).

With X running, this script maps each staged monitor's EDID PnP id to its
RANDR output name (parsed from `xrandr --verbose` EDID blocks - the same
identity Windows and the agent use, so it survives driver renaming), rewrites
~/.config/monitors.xml with those names so the layout PERSISTS across logins,
and applies the layout immediately with one xrandr call.

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

# Cinnamon reports XDG_CURRENT_DESKTOP=X-Cinnamon, but some Mint releases have
# used plain "Cinnamon" - the autostart entry lists both; this script does not
# re-check the desktop, it just needs an X server.
ROTATION_TO_XRANDR = {"none": "normal", "left": "left",
                      "inverted": "inverted", "right": "right"}


def log(msg: str) -> None:
    line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}"
    print(line, flush=True)
    try:
        LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
        with LOG_PATH.open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass


def edid_identity(edid: bytes) -> dict[str, str] | None:
    """Vendor, product name, serial and PnP id from raw EDID bytes.

    Same parsing as the first-boot agent's _edid_identity: bytes 8-9 hold the
    manufacturer as three 5-bit letters, 10-11 the product code, and the
    descriptors from 0x36 carry the name (tag 0xFC) and serial string (0xFF).
    The PnP id ("SAME06F") is the identity Windows reports, which is what the
    staged layout is keyed by.
    """
    if len(edid) < 128:
        return None
    raw = (edid[8] << 8) | edid[9]
    vendor = "".join(chr(((raw >> shift) & 0x1F) + 0x40) for shift in (10, 5, 0))
    if not vendor.isalpha():
        return None
    product_code = edid[10] | (edid[11] << 8)
    serial_num = int.from_bytes(edid[12:16], "little")

    name, serial_str = "", ""
    for base in (0x36, 0x48, 0x5A, 0x6C):
        block = edid[base:base + 18]
        if len(block) < 18 or block[0:3] != b"\x00\x00\x00":
            continue
        text = block[5:18].split(b"\n")[0].decode("ascii", "ignore").strip()
        if block[3] == 0xFC:
            name = text
        elif block[3] == 0xFF:
            serial_str = text

    return {
        "pnp_id": f"{vendor}{product_code:04X}",
        "vendor": vendor,
        "product": name or f"0x{product_code:04X}",
        "serial": serial_str or str(serial_num),
    }


def wait_for_x(timeout_s: int = 90) -> bool:
    """Wait until xrandr can talk to the X server (session may still be starting)."""
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if subprocess.run(["xrandr"], capture_output=True, timeout=15).returncode == 0:
            return True
        time.sleep(3)
    return False


def randr_outputs_by_pnp() -> dict[str, dict[str, str]]:
    """Map PnP id -> {name, vendor, product, serial} for connected RandR outputs.

    The RandR name comes from the xrandr output header; the identity from that
    output's EDID block in `xrandr --verbose`. Matching by EDID (not by name)
    is what makes this robust against the kernel-DRM vs X-driver naming split.
    """
    res = subprocess.run(["xrandr", "--verbose"], capture_output=True, text=True, timeout=20)
    if res.returncode != 0:
        return {}

    outputs: dict[str, dict[str, str]] = {}
    current: str | None = None
    in_edid = False
    edid_lines: list[str] = []

    def flush() -> None:
        nonlocal current, edid_lines
        if current and edid_lines:
            try:
                ident = edid_identity(bytes.fromhex("".join(edid_lines)))
            except ValueError:
                ident = None
            if ident:
                outputs[ident["pnp_id"].upper()] = {"name": current, **ident}
        current, edid_lines = None, []

    for line in res.stdout.splitlines():
        header = re.match(r"^(\S+) connected", line)
        if header:
            flush()
            current = header.group(1)
            in_edid = False
            continue
        if current is None:
            continue
        stripped = line.strip()
        if stripped == "EDID:":
            in_edid = True
            continue
        if in_edid and re.fullmatch(r"[0-9a-fA-F]{32}", stripped):
            edid_lines.append(stripped)
        elif in_edid:
            in_edid = False   # first non-hex property after the EDID block
    flush()
    return outputs


def write_monitors_xml(path: Path, applied: list[dict[str, object]]) -> None:
    """Rewrite ~/.config/monitors.xml with RANDR connector names.

    cinnamon-settings-daemon reads this file at login and when it changes, so
    this is what makes the layout persist; the direct xrandr call only covers
    the current session.
    """
    parts: list[str] = []
    for m in applied:
        ident = m["ident"]
        rotation = ROTATION_TO_XRANDR.get(str(m.get("rotation", "none")), "normal")
        parts.append(
            "    <logicalmonitor>\n"
            f"      <x>{m['x']}</x>\n"
            f"      <y>{m['y']}</y>\n"
            "      <scale>1</scale>\n"
            + ("      <primary>yes</primary>\n" if m.get("primary") else "")
            + f"      <transform><rotation>{rotation}</rotation></transform>\n"
            "      <monitor>\n"
            "        <monitorspec>\n"
            f"          <connector>{ident['name']}</connector>\n"
            f"          <vendor>{ident['vendor']}</vendor>\n"
            f"          <product>{ident['product']}</product>\n"
            f"          <serial>{ident['serial']}</serial>\n"
            "        </monitorspec>\n"
            f"        <mode><width>{m['width']}</width><height>{m['height']}</height>"
            f"<rate>{m['rate']}</rate></mode>\n"
            "      </monitor>\n"
            "    </logicalmonitor>\n")
    xml = ('<monitors version="2">\n  <configuration>\n'
           + "".join(parts) + "  </configuration>\n</monitors>\n")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(xml, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply iGloo display layout via xrandr (X11)")
    parser.add_argument("--layout", required=True, type=Path)
    args = parser.parse_args()

    # Not an X11 session (Wayland): the boot-time monitors.xml with DRM names
    # is the right artefact there - leave it alone and consider ourselves done.
    if not (subprocess.run(["xrandr"], capture_output=True, timeout=10).returncode == 0):
        if not wait_for_x():
            log("no X server reachable - assuming a Wayland session, nothing to do")
            return 0

    try:
        layout = json.loads(args.layout.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        # Nothing (valid) to apply - treat as done so we do not retry forever.
        log(f"no usable layout at {args.layout} ({exc}) - nothing to do")
        return 0
    if not layout:
        log("layout is empty - nothing to do")
        return 0

    by_pnp = randr_outputs_by_pnp()
    log(f"connected RandR outputs by PnP id: {sorted(by_pnp)}")

    applied: list[dict[str, object]] = []
    for mon in layout:
        ident = by_pnp.get(str(mon.get("pnpId", "")).upper())
        if not ident:
            log(f"monitor {mon.get('pnpId')} is not attached - skipped")
            continue
        applied.append({**mon, "ident": ident,
                        "rate": int(mon.get("rate") or 60) or 60})

    if not applied:
        log("no staged monitor matched an attached output - nothing to do")
        return 0

    # Persist first: cinnamon-settings-daemon watches monitors.xml and re-reads
    # it at the next login; with RandR names the configuration finally matches.
    xml_path = Path.home() / ".config/monitors.xml"
    try:
        write_monitors_xml(xml_path, applied)
        log(f"wrote {xml_path} with RandR connector names for {len(applied)} monitor(s)")
    except OSError as exc:
        log(f"could not write {xml_path} ({exc}) - applying via xrandr only")

    # Then apply immediately: one xrandr call, primary first. Positions are
    # physical pixels; Cinnamon on X11 has no per-output scaling applied here,
    # so physical == logical. --rate accepts the nearest advertised rate, so
    # Windows' whole-Hz 144 lands on the panel's 143.99.
    cmd: list[str] = ["xrandr"]
    for m in sorted(applied, key=lambda m: not m.get("primary")):
        rotation = ROTATION_TO_XRANDR.get(str(m.get("rotation", "none")), "normal")
        cmd += ["--output", str(m["ident"]["name"]),
                "--mode", f"{m['width']}x{m['height']}",
                "--rate", str(m["rate"]),
                "--rotate", rotation,
                "--pos", f"{m['x']}x{m['y']}"]
        if m.get("primary"):
            cmd.append("--primary")

    log("running: " + " ".join(cmd))
    res = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
    if res.returncode != 0:
        log(f"xrandr failed (exit {res.returncode}): "
            f"{(res.stderr or res.stdout or '').strip()[:400]} - will retry at next login")
        return 1

    log(f"layout applied to {len(applied)} output(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
