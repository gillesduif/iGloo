#!/usr/bin/env python3
"""Inspect native Mint retry results against exact staged portable inputs."""

import hashlib
import json
from pathlib import Path
import sys


def sha(path: Path) -> str:
    assert path.is_file() and not path.is_symlink(), path
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    if len(sys.argv) != 4:
        raise SystemExit("Usage: check-retry-v29-idempotence.py <readonly-root> <stage> <manifest>")
    root, stage, manifest_path = map(Path, sys.argv[1:])
    manifest = json.loads(manifest_path.read_text())
    user = root / "home/mintlab"

    assert manifest["files"]["includedFolders"] == ["Documents"]
    document = Path("Documents/igloo-migration-lab.txt")
    assert sha(stage / "files" / document) == sha(user / document)
    assert len(list((user / "Documents").rglob(document.name))) == 1

    browser_destinations = {
        "Mozilla Firefox": Path(".mozilla/firefox"),
        "Google Chrome": Path(".config/google-chrome"),
    }
    for browser in manifest["browsers"]:
        name = browser["name"]
        assert name in browser_destinations
        source = stage / browser["profileStagingPath"]
        dest = user / browser_destinations[name]
        assert source.is_dir() and dest.is_dir()
        source_files = sorted(path for path in source.rglob("*") if path.is_file())
        assert source_files
        for path in source_files:
            relative = path.relative_to(source)
            if name == "Google Chrome":
                assert sha(path) == sha(dest / relative), (name, relative)
            elif relative.name == "compatibility.ini":
                source_lines = path.read_text().splitlines()
                expected = "\n".join(line for line in source_lines
                                     if not line.startswith(("LastPlatformDir=", "LastAppDir="))).strip() + "\n"
                assert (dest / relative).read_text() == expected
            elif relative.name == "prefs.js":
                expected = path.read_text().strip() + '\nuser_pref("browser.startup.page", 3);\n'
                assert (dest / relative).read_text() == expected
            elif relative.name == "profiles.ini":
                profile = (dest / relative).read_text()
                assert "[Install" not in profile
                assert profile.count("Default=1") == 1
                assert "Path=Profiles/lab.default" in profile
            else:
                assert sha(path) == sha(dest / relative), (name, relative)
        if name == "Google Chrome":
            assert len(list(dest.rglob("Bookmarks"))) == 1
        else:
            assert len(list(dest.rglob("prefs.js"))) == 1

    package_status = (root / "var/lib/dpkg/status").read_text()
    for package in ("mint-meta-codecs", "tree", "libnotify-bin"):
        records = [block for block in package_status.split("\n\n")
                   if f"Package: {package}\n" in block]
        assert len(records) == 1 and "Status: install ok installed\n" in records[0]
    assert len(list((root / "etc/NetworkManager/system-connections").glob("igloo-*.nmconnection"))) == 2
    print("Mint v29: staged document/Chrome bytes, normalized Firefox, one target each, three installed packages, two Wi-Fi profiles")


if __name__ == "__main__":
    main()
