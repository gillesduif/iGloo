#!/usr/bin/env python3
"""Read-only verification of the fresh Mint v23 installed first boot."""

import configparser
import hashlib
import json
import pathlib
import stat
import sys


root, seed, manifest_path = map(pathlib.Path, sys.argv[1:])
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
wifi = manifest["wifiNetworks"][0]
credential = next(
    item["credentialsBlob"] for item in manifest["browsers"]
    if item.get("name") == "Google Chrome" and item.get("credentialsBlob")
)


def sha(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def is_installed(package: str) -> bool:
    status = (root / "var/lib/dpkg/status").read_text(encoding="utf-8")
    return any(
        f"Package: {package}\n" in entry
        and "Status: install ok installed\n" in entry
        for entry in status.split("\n\n")
    )


assert (root / "var/lib/igloo/.done").is_file()
assert not (root / "var/lib/igloo/manifest.retry.json").exists()
assert not (root / "var/lib/igloo/seed-retry").exists()
assert (root / "var/lib/igloo/seed-identity.json").read_bytes() == (
    seed / "igloo-seed-identity.json"
).read_bytes()
agent = root / "opt/igloo/agent.py"
assert sha(agent) == "c0dac744a5d8ccfbbf128340e76403a45d399ba9ae22dd7e10d5a05e46fcc038"
assert stat.S_IMODE((root / "opt/igloo").stat().st_mode) == 0o755
assert stat.S_IMODE((root / "opt/igloo/igloo_boot.py").stat().st_mode) == 0o644
assert stat.S_IMODE((root / "var/lib/igloo").stat().st_mode) == 0o700

log = (root / "var/log/igloo/agent.log").read_text(encoding="utf-8", errors="replace")
assert log.count("=== Igloo first-boot agent done (0 failure(s)) ===") == 1
for step in (
    "wifi", "codecs", "suggested-pkgs", "user-files", "wallpaper",
    "account-picture", "stage-credentials", "redact-seed", "cleanup-seed",
):
    assert f"step {step}: OK" in log, step
assert "Running: nmcli connection reload" in log
assert "Wrote NetworkManager profile" in log
assert is_installed("mint-meta-codecs")
assert is_installed("tree") and (root / "usr/bin/tree").is_file()

keyfile = root / "etc/NetworkManager/system-connections/IglooLabNoRadio.nmconnection"
assert keyfile.is_file() and not keyfile.is_symlink()
key_stat = keyfile.stat()
assert stat.S_IMODE(key_stat.st_mode) == 0o600 and key_stat.st_uid == 0
config = configparser.ConfigParser(interpolation=None)
config.read(keyfile, encoding="utf-8")
assert config["connection"]["id"] == wifi["ssid"]
assert config["wifi"]["ssid"] == wifi["ssid"]
assert config["wifi-security"]["psk"] == wifi["psk"]

for relative in (
    "home/mintlab/Documents/igloo-migration-lab.txt",
    "home/mintlab/.mozilla/firefox/Profiles/lab.default/prefs.js",
    "home/mintlab/.config/google-chrome/Default/Bookmarks",
):
    assert (root / relative).is_file(), relative
assert sha(root / "home/mintlab/Pictures/wallpaper.PNG") == (
    "f3d180c7610e4a3fce2d54ed6681d12c8d202c4b949ab5a576c601e188efcac6"
)
assert sha(root / "var/lib/AccountsService/icons/mintlab") == (
    "ad1e7511713f030a8a1d73f36f26d70eda212a2b43e9ea8d2ad7b4e0271c4c4a"
)

store = root / "home/mintlab/.local/share/igloo/credentials.json"
assert store.is_file() and not store.is_symlink()
uid = int(next(
    line.split(":")[2] for line in (root / "etc/passwd").read_text().splitlines()
    if line.startswith("mintlab:")
))
assert stat.S_IMODE(store.stat().st_mode) == 0o600 and store.stat().st_uid == uid
saved = json.loads(store.read_text(encoding="utf-8"))
assert saved["attempts"] == 0
assert saved["browsers"] == [{"name": "Google Chrome", "blob": credential}]
assert stat.S_IMODE((root / "etc/xdg/autostart/igloo-credential-import.desktop").stat().st_mode) == 0o644

for label, path in (
    ("installed manifest", root / "var/lib/igloo/manifest.json"),
    ("seed manifest", seed / "migration-manifest.json"),
    ("seed preseed", seed / "preseed.cfg"),
    ("seed initrd", seed / "igloo-boot/initrd"),
):
    data = path.read_bytes()
    for secret in (manifest["user"]["linuxPasswordCrypted"], wifi["psk"], credential):
        assert secret.encode() not in data, label
    print(label, "scrubbed", len(data), "bytes")

assert "UUID=8060-2E31  /boot/efi" in (root / "etc/fstab").read_text()
print("Fresh Mint v23 firstboot: durable done, selected files/browser/media, codecs/tree, private Wi-Fi profile and encrypted credential store")
