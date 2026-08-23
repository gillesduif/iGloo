#!/usr/bin/env python3
"""Ground truth for the monitors.xml the first-boot agent writes.

Two bugs, both from the Debian 13 runs on desktop-living:

  2026-08-19  mutter refused the file outright - "Expected a number, got -826".
              Windows puts the primary monitor at 0,0 and lets the others go
              negative; mutter parses no negative coordinate and discards the
              whole file, so rotation, refresh rate and position fell back
              together. Fixed by shifting the origin.

  2026-08-21  the file parsed but was ignored, so the layout reverted at the
              next login. Mutter selects a stored configuration by the layout
              mode of the session; ours named none. The proof is in the file
              mutter wrote itself once the user confirmed the prompt: two
              <configuration> blocks, one per layoutmode. This test holds the
              agent's output to that shape.
"""
import importlib.util
import re
import sys
from pathlib import Path

MOD_PATH = Path(__file__).resolve().parents[2] / "distros/_debian-family/agent/agent.py"
spec = importlib.util.spec_from_file_location("ag", MOD_PATH)
ag = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ag)

# The reporter's real setup: two Odyssey G70D panels with the SAME EDID serial,
# the second rotated and sitting 826 px above the primary in Windows coordinates.
WANTED = [
    {"pnpId": "SAM", "widthPx": 3840, "heightPx": 2160, "refreshHz": 144,
     "rotationDegrees": 0, "positionX": 0, "positionY": 0, "isPrimary": True},
    {"pnpId": "SAM", "widthPx": 3840, "heightPx": 2160, "refreshHz": 144,
     "rotationDegrees": 270, "positionX": 3840, "positionY": -826, "isPrimary": False},
]
OUTPUTS = [
    {"connector": "DP-4", "pnp_id": "SAM", "vendor": "SAM",
     "product": "Odyssey G70D", "serial": "H1AK500000"},
    {"connector": "HDMI-A-2", "pnp_id": "SAM", "vendor": "SAM",
     "product": "Odyssey G70D", "serial": "H1AK500000"},
]

# _mode_is_supported reads /sys/class/drm; both panels advertise the mode.
ag._mode_is_supported = lambda connector, w, h: True

failures: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        print(f"  PASS  {name}")
    else:
        failures.append(name)
        print(f"  FAIL  {name}  {detail}")


def build_xml() -> str:
    logical, _cinnamon, matched = ag._match_display_layouts(WANTED, OUTPUTS)
    assert matched == 2, f"expected both monitors to match, got {matched}"
    body = "".join(logical)
    return ('<monitors version="2">\n'
            + "".join(f"  <configuration>\n    <layoutmode>{mode}</layoutmode>\n"
                      f"{body}  </configuration>\n"
                      for mode in ("physical", "logical"))
            + "</monitors>\n")


def test_no_negative_coordinates() -> None:
    print("the origin is shifted so mutter can parse it")
    xml = build_xml()
    negatives = re.findall(r"<(?:x|y)>(-\d+)</(?:x|y)>", xml)
    check("no negative x or y", not negatives, str(negatives))
    check("the second monitor keeps its 826 px offset above the first",
          "<y>826</y>" in xml and "<y>0</y>" in xml, xml)
    check("horizontal position is untouched", "<x>3840</x>" in xml, xml)


def test_both_layout_modes_present() -> None:
    print("one configuration per layout mode, as mutter writes them")
    xml = build_xml()
    check("two configurations", xml.count("<configuration>") == 2, xml)
    check("physical named", "<layoutmode>physical</layoutmode>" in xml, xml)
    check("logical named", "<layoutmode>logical</layoutmode>" in xml, xml)
    bodies = re.findall(r"<layoutmode>\w+</layoutmode>\n(.*?)  </configuration>",
                        xml, re.S)
    check("both carry the same layout", len(bodies) == 2 and bodies[0] == bodies[1])


def test_monitors_are_identified_by_connector_not_serial() -> None:
    print("two panels sharing one EDID serial are told apart")
    xml = build_xml()
    check("both connectors appear",
          "<connector>DP-4</connector>" in xml
          and "<connector>HDMI-A-2</connector>" in xml, xml)
    check("each connector exactly twice - once per configuration",
          xml.count("<connector>DP-4</connector>") == 2
          and xml.count("<connector>HDMI-A-2</connector>") == 2, xml)


def test_rotation_and_primary_survive() -> None:
    print("rotation and the primary flag reach the file")
    xml = build_xml()
    check("the second monitor is rotated right",
          "<rotation>right</rotation>" in xml, xml)
    check("the first monitor is primary", "<primary>yes</primary>" in xml, xml)
    check("exactly one primary per configuration",
          xml.count("<primary>yes</primary>") == 2, xml)


if __name__ == "__main__":
    for test in (test_no_negative_coordinates, test_both_layout_modes_present,
                 test_monitors_are_identified_by_connector_not_serial,
                 test_rotation_and_primary_survive):
        test()
    print()
    if failures:
        print(f"{len(failures)} FAILED: {', '.join(failures)}")
        sys.exit(1)
    print("all checks passed")
