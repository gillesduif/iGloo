#!/usr/bin/env python3
"""Hardware-grounded test for display-apply-gnome.py.

Ground truth: the RTX 5070 Debian 13 run of 2026-07-31 (display-hook-user.txt),
where the first version failed twice:
  1. mutter rejected the variant: monitor entries must be (connector, mode, {})
     triples - signature a(iiduba(ssa{sv})).
  2. Both Samsung Odyssey G70D panels report the SAME EDID serial 'H1AK500000',
     so identity matching alone bound both staged monitors to DP-4. Connector
     matching must come first, must consume each live monitor once, and must
     normalize the kernel name HDMI-A-2 to mutter's HDMI-2.

Expected output on that machine (from the real hook log):
  [(0, 0, 1.5, 0, true, [('DP-4', '3840x2160@143.988', {})]),
   (2560, 289, 1.5, 3, false, [('HDMI-2', '3840x2160@143.988', {})])]
"""
import importlib.util
import json
import sys
import tempfile
from pathlib import Path

MOD_PATH = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/display-apply-gnome.py"
spec = importlib.util.spec_from_file_location("dag", MOD_PATH)
dag = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dag)

# Mutter's real connector names on that machine: DP-4 and HDMI-2 (NOT HDMI-A-2).
# Both panels: vendor SAM, product Odyssey G70D, serial H1AK500000 (duplicated!).
SYNTHETIC_STATE = """(uint32 1, ([(('DP-4', 'SAM', 'Odyssey G70D', 'H1AK500000'), [('3840x2160@143.988', 3840, 2160, 143.98800659179688, 1.0, [1.0, 1.25, 1.5, 1.75, 2.0], {'is-current': <true>}), ('3840x2160@59.999', 3840, 2160, 59.998500823974609, 1.0, [1.0, 1.25, 1.5, 1.75, 2.0], {'is-current': <false>})], {'display-name': <'Odyssey G70D'>}), (('HDMI-2', 'SAM', 'Odyssey G70D', 'H1AK500000'), [('3840x2160@143.988', 3840, 2160, 143.98800659179688, 1.0, [1.0, 1.25, 1.5, 1.75, 2.0], {'is-current': <true>}), ('3840x2160@59.999', 3840, 2160, 59.998500823974609, 1.0, [1.0, 1.25, 1.5, 1.75, 2.0], {'is-current': <false>})], {'display-name': <'Odyssey G70D'>})], [], [], {'renderer': <'native'>}))"""

# The real staged layout from that run (display-layout-staged.json).
LAYOUT = [
    {"pnpId": "SAME06F", "vendor": "SAM", "product": "Odyssey G70D",
     "serial": "H1AK500000", "connector": "DP-4", "width": 3840, "height": 2160,
     "rate": 144, "rotation": "none", "x": 0, "y": 0, "primary": True, "scalePercent": 150},
    {"pnpId": "SAME069", "vendor": "SAM", "product": "Odyssey G70D",
     "serial": "H1AK500000", "connector": "HDMI-A-2", "width": 3840, "height": 2160,
     "rate": 144, "rotation": "right", "x": 3840, "y": 434, "primary": False, "scalePercent": 150},
]

captured = {}

class FakeCompleted:
    def __init__(self, rc=0, stdout="", stderr=""):
        self.returncode, self.stdout, self.stderr = rc, stdout, stderr

def fake_gdbus(method, *args, timeout=20):
    if method == "GetCurrentState":
        return FakeCompleted(0, SYNTHETIC_STATE)
    if method == "ApplyMonitorsConfig":
        captured["args"] = args
        return FakeCompleted(0, "()")
    raise AssertionError(f"unexpected method {method}")

dag.gdbus_call = fake_gdbus
dag.wait_for_shell = lambda timeout_s=90: True
dag.LOG_PATH = Path(tempfile.mkdtemp()) / "igloo-display.log"

layout_file = Path(tempfile.mkdtemp()) / "display-layout.json"
layout_file.write_text(json.dumps(LAYOUT), encoding="utf-8")

failures = []

# 1. connector normalization
if dag._norm_connector("HDMI-A-2") != "HDMI-2":
    failures.append(f"_norm_connector HDMI-A-2 -> {dag._norm_connector('HDMI-A-2')}")
if dag._norm_connector("DP-4") != "DP-4":
    failures.append(f"_norm_connector DP-4 -> {dag._norm_connector('DP-4')}")

# 2. parser
serial, monitors = dag.parse_current_state(SYNTHETIC_STATE)
if serial != 1:
    failures.append(f"serial: got {serial}, want 1")
if set(monitors) != {"DP-4", "HDMI-2"}:
    failures.append(f"connectors: got {sorted(monitors)}")
if monitors.get("DP-4", {}).get("serial") != "H1AK500000":
    failures.append("DP-4 serial parse")
m = dag.pick_mode(monitors.get("DP-4", {}).get("modes", []), 3840, 2160, 144)
if not m or m["id"] != "3840x2160@143.988":
    failures.append(f"pick_mode 144: {m and m['id']}")

# 3. full main() flow - the duplicated-serial scenario
sys.argv = ["display-apply-gnome.py", "--layout", str(layout_file)]
rc = dag.main()
if rc != 0:
    failures.append(f"main() rc={rc}, want 0")
args = captured.get("args")
if not args:
    failures.append("ApplyMonitorsConfig was never called")
else:
    serial_arg, method_arg, variant, props = args
    # method must be 1. Mutter's own interface says "0: verify 1: temporary
    # 2: persistent", and 2 is the branch that runs
    # request_persistent_confirmation() - the "Keep this display setup?" prompt
    # that reverts after 20 seconds if nobody clicks. Persistence comes from the
    # monitors.xml the agent writes, which carries a configuration per layout
    # mode so mutter actually matches it; this call only has to reach the session
    # that is already running. Both halves were wrong in turn: 1 without a
    # matching file lost the layout at logout, 2 with one prompted every time.
    if serial_arg != "1" or method_arg != "1":
        failures.append(f"serial/method args: {serial_arg}/{method_arg}, want 1/1")
    # Monitor entries are (connector, mode, {}) triples
    if "('DP-4', '3840x2160@143.988', {})" not in variant:
        failures.append(f"DP-4 triple missing/wrong: {variant}")
    if "('HDMI-2', '3840x2160@143.988', {})" not in variant:
        failures.append(f"HDMI-2 triple missing/wrong (duplicated serial bound both to DP-4?): {variant}")
    if variant.count("DP-4") != 1:
        failures.append(f"DP-4 appears {variant.count('DP-4')}x - live monitor consumed twice: {variant}")
    # Primary landscape at origin, portrait panel right of it at 150% scale:
    # 3840/1.5 = 2560, 434/1.5 = 289
    if "(0, 0, 1.5, 0, true, [" not in variant:
        failures.append(f"primary entry wrong: {variant}")
    if "(2560, 289, 1.5, 3, false, [" not in variant:
        failures.append(f"portrait entry wrong (pos/scale/transform): {variant}")
    if props != "{}":
        failures.append(f"props arg: {props}")

if failures:
    print("FAIL")
    for f in failures:
        print("  -", f)
    sys.exit(1)
print("PASS - duplicated EDID serials handled, HDMI-A-2->HDMI-2 normalized, (s s a{sv}) triples")
