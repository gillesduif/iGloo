"""Inline the verified Chromium credential section into both first-boot agents.

Re-runnable: if a previously generated section exists between the markers it
is replaced, otherwise the section is inserted before the agent's
redact_manifest definition. Run after editing chromium_import.py:

    python sync_to_agents.py
"""

import sys
from pathlib import Path

HERE = Path(__file__).parent
REPO = HERE.parent.parent

SOURCE = HERE / "chromium_import.py"
BEGIN = "# === BEGIN AGENT SECTION"
END = "# === END AGENT SECTION"

AGENTS = [
    REPO / "distros" / "_debian-family" / "agent" / "agent.py",
    REPO / "distros" / "fedora-kde" / "agent" / "agent.py",
]

ANCHOR = "def redact_manifest"


def extract_section() -> str:
    lines = SOURCE.read_text(encoding="utf-8").splitlines(keepends=True)
    begin = next(i for i, l in enumerate(lines) if l.startswith(BEGIN))
    end = next(i for i, l in enumerate(lines) if l.startswith(END))
    return "".join(lines[begin:end])


def sync(agent: Path, section: str) -> str:
    text = agent.read_text(encoding="utf-8")
    if BEGIN in text:
        head = text[:text.index(BEGIN)]
        tail = text[text.index(END):]
        tail = tail[tail.index("\n") + 1:]
        agent.write_text(head + section + tail, encoding="utf-8")
        return "replaced existing section"
    anchor_at = text.index(ANCHOR)
    line_start = text.rindex("\n", 0, anchor_at) + 1
    agent.write_text(text[:line_start] + section + "\n" + text[line_start:],
                     encoding="utf-8")
    return "inserted new section"


def main() -> int:
    section = extract_section()
    for agent in AGENTS:
        action = sync(agent, section)
        print(f"{agent.relative_to(REPO)}: {action}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
