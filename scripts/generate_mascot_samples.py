"""Export arc-length samples of the checked-in Tux SVG for the native rig.

Generation only: pip install svgpathtools==1.7.1, then run this script from
the repository root. The app embeds the generated numbers, never the SVG.
"""

import json
import xml.etree.ElementTree as ET
from pathlib import Path

from svgpathtools import parse_path


ROOT = Path(__file__).resolve().parents[1]
svg = ET.parse(ROOT / "docs/assets/Tux.svg").getroot()
ns = {"s": "http://www.w3.org/2000/svg"}
paths = {
    node.get("id"): parse_path(node.get("d"))
    for node in svg.findall(".//s:path", ns)
    if node.get("id") and node.get("d")
}


def sample(path, count):
    length = path.length(error=1e-9)
    return [
        [round(z.real, 8), round(z.imag, 8)]
        for i in range(count + 1)
        for z in [path.point(path.ilength(length * i / count))]
    ]


data = {}
for side in ("left", "right"):
    base = [paths[f"wing_{side}_base"]]
    if side == "right":
        base.append(paths["wing_tip_right_base"])
    data[side] = {
        "root": sample(base[0], 600),
        "far": [sample(path, 240) for path in base],
        "base": [sample(path, 1400) for path in base],
        "glare": [sample(paths[f"wing_{side}_glare"], 900)],
    }

out = ROOT / "src/Igloo.Mascot.Core/Artwork/WingSamples.json"
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps(data, separators=(",", ":")) + "\n", encoding="utf-8")
print(out)
