"""Read-only link and graph audit for the docs/ Obsidian vault.

Run from the repository root: python scripts/validate_docs_vault.py [--shareable]
This checks local files; Obsidian remains the authority for its own resolver/rendering.
It does not implement every Obsidian alias, embed, or Markdown extension.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import unicodedata
from collections import Counter, defaultdict, deque
from pathlib import Path
from urllib.parse import unquote


ROOT = Path(__file__).resolve().parents[1]
VAULT = ROOT / "docs"
FENCE = re.compile(r"^\s*(`{3,}|~{3,})")
WIKI = re.compile(r"!?\[\[([^\]]+)\]\]")
MARKDOWN = re.compile(r"!?\[[^\]]*\]\((<[^>]+>|(?:[^()]|\([^()]*\))+)\)")
HEADING = re.compile(r"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", re.M)
BLOCK = re.compile(r"(?:^|\s)\^([A-Za-z0-9-]+)\s*$", re.M)
HUBS = {"README.md", "community.md", "fleet-map.md", "shared-engine.md",
        "distributions.md", "safety-and-evidence.md", "development.md",
        "direction.md", "vault/graph-guide.md"}


def clean(text: str) -> str:
    """Remove frontmatter, fenced/inline code and HTML comments before link parsing."""
    if text.startswith("---\n"):
        end = text.find("\n---", 4)
        if end >= 0:
            text = " " * (end + 4) + text[end + 4:]
    text = re.sub(r"<!--.*?-->", lambda m: " " * len(m.group()), text, flags=re.S)
    lines, marker = [], None
    for line in text.splitlines(keepends=True):
        m = FENCE.match(line)
        if marker:
            if m and m.group(1)[0] == marker[0] and len(m.group(1)) >= len(marker):
                marker = None
            lines.append("\n")
        elif m:
            marker = m.group(1)
            lines.append("\n")
        else:
            lines.append(line)
    text = "".join(lines)
    return re.sub(r"(`+)(.*?)\1", lambda m: " " * len(m.group()), text)


def properties(text: str) -> dict[str, object]:
    if not text.startswith("---\n"):
        return {}
    end = text.find("\n---", 4)
    if end < 0:
        return {}
    result: dict[str, object] = {}
    current = None
    for line in text[4:end].splitlines():
        match = re.match(r"^([a-zA-Z][\w-]*):\s*(.*)$", line)
        if match:
            current = match.group(1)
            value = match.group(2).strip()
            if value == "" or value == "[]":
                result[current] = []
            elif value.startswith("[") and value.endswith("]"):
                result[current] = [part.strip().strip('"\'') for part in value[1:-1].split(",")
                                   if part.strip()]
            else:
                result[current] = value.strip('"\'')
        elif current and (item := re.match(r"^\s+-\s+(.+)$", line)):
            if isinstance(result[current], list):
                result[current].append(item.group(1).strip().strip('"\''))
    return result


def slug(text: str) -> str:
    text = re.sub(r"\[([^]]+)\]\([^)]*\)", r"\1", text)
    text = re.sub(r"\*|_|`", "", text).strip().casefold()
    text = unicodedata.normalize("NFKC", text)
    text = re.sub(r"\s+", "-", text)
    return "".join(c for c in text if c.isalnum() or c in "-_")


def resolve(source: str, target: str, kind: str, files: dict[str, str],
            basenames: dict[str, list[str]], aliases: dict[str, list[str]]):
    target = target.strip()
    angle_destination = target.startswith("<") and target.endswith(">")
    encoded_destination = "%20" in target.casefold()
    if angle_destination:
        target = target[1:-1]
    target = unquote(target).replace("\\", "/")
    target = target.split("|", 1)[0] if kind == "wiki" else target if angle_destination or encoded_destination else target.split(" ", 1)[0]
    if not target or target.startswith(("#", "http:", "https:", "mailto:", "data:", "obsidian:")):
        return None, None
    target = target.split("?", 1)[0]
    path, _, anchor = target.partition("#")
    if kind == "md":
        candidate = (VAULT / source).parent / path
        if candidate.is_dir():
            return None, None
        if not candidate.suffix:
            candidate = candidate.with_suffix(".md")
        if not candidate.exists():
            return None, "missing"
        try:
            rel = candidate.resolve().relative_to(VAULT.resolve()).as_posix()
        except ValueError:
            return None, None  # valid repository source outside vault
        if rel not in files and rel.casefold() in {p.casefold() for p in files}:
            return None, "case-mismatch"
        resolved = rel if rel in files else None  # attachments are not note nodes
        if resolved is None:
            return None, None
    else:
        key = path.removesuffix(".md").lstrip("/")
        if "/" in key:
            rel = key + ".md"
            if rel not in files:
                return None, "case-mismatch" if rel.casefold() in {p.casefold() for p in files} else "missing"
            resolved = rel
        else:
            hits = basenames.get(key.casefold(), []) + aliases.get(key.casefold(), [])
            hits = sorted(set(hits))
            if len(hits) > 1:
                return None, "ambiguous"
            if not hits:
                return None, "missing"
            resolved = hits[0]
            if key != Path(resolved).stem and key not in (aliases.get(key.casefold(), []) or []):
                if key.casefold() == Path(resolved).stem.casefold():
                    return None, "case-mismatch"
    if anchor:
        body = clean(files[resolved])
        if anchor.startswith("^"):
            good = anchor[1:] in BLOCK.findall(body)
        else:
            good = slug(anchor) in {slug(h) for h in HEADING.findall(body)}
        if not good:
            return resolved, "missing-heading"
    return resolved, None


def audit(tracked_only: bool = False, shareable: bool = False) -> dict:
    tracked = None
    if tracked_only or shareable:
        command = ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", "docs"] if shareable else ["git", "ls-files", "--", "docs"]
        output = subprocess.check_output(command, cwd=ROOT, text=True)
        tracked = {p.removeprefix("docs/") for p in output.splitlines() if p.endswith(".md")}
    files = {p.relative_to(VAULT).as_posix(): p.read_text(encoding="utf-8-sig")
             for p in VAULT.rglob("*.md") if ".obsidian" not in p.parts and "dev-vault" not in p.parts
             and (tracked is None or p.relative_to(VAULT).as_posix() in tracked)}
    meta = {p: properties(body) for p, body in files.items()}
    basenames, aliases = defaultdict(list), defaultdict(list)
    for p, props in meta.items():
        basenames[Path(p).stem.casefold()].append(p)
        for alias in props.get("aliases", []) if isinstance(props.get("aliases"), list) else []:
            aliases[alias.casefold()].append(p)
    edges, issues = set(), []
    for p, body in files.items():
        cleaned = clean(body)
        for kind, regex in (("wiki", WIKI), ("md", MARKDOWN)):
            for match in regex.finditer(cleaned):
                target = match.group(1)
                dest, error = resolve(p, target, kind, files, basenames, aliases)
                if error:
                    issues.append({"source": p, "target": target, "kind": error})
                elif dest and dest != p:
                    edges.add((p, dest))
    active = {p for p in files if meta[p].get("doc_status", meta[p].get("status"))
              not in ("historical", "superseded")
              and not p.startswith(("funding/", "marketing/"))
              and p != "vault/note-template.md" and not p.startswith(("19 - Templates/", "99 - Archive/"))}
    hubs = HUBS | {p for p, props in meta.items() if props.get("type") in ("moc", "hub", "navigation")}
    neighbors = {p: set() for p in files}
    indegree = Counter()
    for a, b in edges:
        neighbors[a].add(b)
        neighbors[b].add(a)
        indegree[b] += 1
    orphans = sorted(p for p in active if not neighbors[p])
    unseen = active.copy()
    components = []
    while unseen:
        start = next(iter(unseen))
        group = {start}
        queue = deque([start])
        unseen.remove(start)
        while queue:
            for q in neighbors[queue.popleft()] & unseen:
                unseen.remove(q)
                group.add(q)
                queue.append(q)
        components.append(sorted(group))
    seen = set()
    home = "00 - Home/Project Dashboard.md" if "00 - Home/Project Dashboard.md" in files else "README.md"
    if home in files:
        queue = deque([home])
        seen.add(home)
        while queue:
            for b in (v for a, v in edges if a == queue[0]):
                if b not in seen:
                    seen.add(b)
                    queue.append(b)
            queue.popleft()
    alias_duplicates = {k: v for k, v in aliases.items() if len(set(v)) > 1}
    basename_duplicates = {k: v for k, v in basenames.items() if len(v) > 1}
    metadata_issues = []
    for p, props in meta.items():
        if not props:
            continue  # legacy notes are reported separately
        for key in ("title", "type", "status"):
            if key not in props or not isinstance(props[key], str):
                metadata_issues.append({"note": p, "property": key, "problem": "missing-or-non-scalar"})
        if not isinstance(props.get("graph_cluster", props.get("area")), str):
            metadata_issues.append({"note": p, "property": "graph_cluster-or-area", "problem": "missing-or-non-scalar"})
        for key in ("tags", "aliases", "sources"):
            if key in props and not isinstance(props[key], list):
                metadata_issues.append({"note": p, "property": key, "problem": "expected-list"})
        old_status = ("current", "draft", "historical", "superseded")
        capability_status = ("proposed", "planned", "in-progress", "implemented", "blocked",
                             "deferred", "deprecated", "unknown", "not-applicable", "mixed")
        allowed_status = capability_status if "doc_status" in props else old_status
        if props.get("status") not in allowed_status:
            metadata_issues.append({"note": p, "property": "status", "problem": "invalid-value"})
        if "doc_status" in props and props["doc_status"] not in old_status:
            metadata_issues.append({"note": p, "property": "doc_status", "problem": "invalid-value"})
        if "validation_status" in props and props["validation_status"] not in (
                "unverified", "partial", "verified", "failed", "not-applicable"):
            metadata_issues.append({"note": p, "property": "validation_status", "problem": "invalid-value"})
        if "availability" in props and props["availability"] not in (
                "branch-only", "released", "unknown", "not-applicable"):
            metadata_issues.append({"note": p, "property": "availability", "problem": "invalid-value"})
        if "implementation" in props and props["implementation"] not in ("live", "partial", "planned", "blocked", "unknown", "not-applicable"):
            metadata_issues.append({"note": p, "property": "implementation", "problem": "invalid-value"})
    nonhub = active - hubs
    nonhub_unseen = nonhub.copy()
    nonhub_components = []
    while nonhub_unseen:
        start = next(iter(nonhub_unseen))
        group, queue = {start}, deque([start])
        nonhub_unseen.remove(start)
        while queue:
            for q in neighbors[queue.popleft()] & nonhub_unseen:
                nonhub_unseen.remove(q)
                group.add(q)
                queue.append(q)
        nonhub_components.append(sorted(group))
    return {"notes": len(files), "active_notes": len(active), "edges": len(edges),
            "navigation_edges": sum(1 for a, _ in edges if a in hubs),
            "substantive_edges": sum(1 for a, _ in edges if a not in hubs),
            "orphans": orphans, "components": components,
            "unreachable_from_home": sorted(active - seen) if home in files else None,
            "issues": issues, "duplicate_aliases": alias_duplicates,
            "duplicate_basenames": basename_duplicates, "metadata_issues": metadata_issues,
            "components_without_hubs": sorted(nonhub_components, key=len, reverse=True),
            "missing_schema": sorted(p for p in active if not meta[p]),
            "top_indegree": indegree.most_common(10)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--tracked-only", action="store_true", help="Audit only tracked Markdown files")
    parser.add_argument("--shareable", action="store_true", help="Audit tracked and unignored untracked Markdown files")
    args = parser.parse_args()
    result = audit(args.tracked_only, args.shareable)
    print(json.dumps(result, indent=2, ensure_ascii=False))
