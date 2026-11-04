#!/usr/bin/env python3
"""Read-only saved-login and durability check after Mint's seed-free boot."""

import importlib.util
import json
import pathlib
import sqlite3
import sys


root, manifest_path, agent_source = map(pathlib.Path, sys.argv[1:])
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
assert (root / "var/lib/igloo/.done").is_file()
assert not (root / "var/lib/igloo/manifest.retry.json").exists()
assert not (root / "var/lib/igloo/seed-retry").exists()
store = root / "home/mintlab/.local/share/igloo/credentials.json"
assert not store.exists(), "Encrypted retry store was not retired after GUI import"

user_log = root / "home/mintlab/.local/state/igloo-credentials.log"
assert user_log.is_file()
assert "Imported 1 browser login(s); credential store removed" in user_log.read_text(
    encoding="utf-8", errors="replace"
)
agent_log = (root / "var/log/igloo/agent.log").read_text(encoding="utf-8", errors="replace")
assert agent_log.count("step codecs: OK") == 1, "First-boot steps reran on seed-free login"
assert "=== Igloo first-boot agent done (0 failure(s)) ===" in agent_log

spec = importlib.util.spec_from_file_location("mint_agent_v23_readback", agent_source)
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)
database = root / "home/mintlab/.config/google-chrome/Default/Login Data"
assert database.is_file()
with sqlite3.connect(f"file:{database}?mode=ro", uri=True) as connection:
    rows = connection.execute(
        "SELECT origin_url, username_value, password_value FROM logins WHERE origin_url=?",
        ("https://mint-login.invalid/login",),
    ).fetchall()
assert len(rows) == 1 and rows[0][1] == "mint-credential-lab"
assert rows[0][2] == agent._chromium_v10_encrypt("igloo-credential-v22")
assert b"igloo-credential-v22" not in database.read_bytes()

expected_hash = manifest["user"]["linuxPasswordCrypted"]
assert any(
    line.startswith("mintlab:" + expected_hash + ":")
    for line in (root / "etc/shadow").read_text().splitlines()
)
for relative in (
    "home/mintlab/Documents/igloo-migration-lab.txt",
    "home/mintlab/.mozilla/firefox/Profiles/lab.default/prefs.js",
    "home/mintlab/.config/google-chrome/Default/Bookmarks",
    "home/mintlab/Pictures/wallpaper.PNG",
):
    assert (root / relative).is_file(), relative
print("Mint v23 seed-free GUI: one exact Chromium login URL/user with encrypted v10 password, private store retired, no first-boot rerun")
