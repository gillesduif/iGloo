#!/usr/bin/env python3
"""Read-only checks for the Mint v24 Wi-Fi and first-login repairs."""

import argparse
import configparser
import hashlib
import importlib.util
import json
import pathlib
import sqlite3
import stat
import uuid


EXPECTED_AGENT_SHA256 = "e94caa198f26bc863ce6932f5d11a6da2d6641a126b957e10c3da49324da35ba"


def profile_name(ssid: str) -> str:
    stem = "".join(char if char.isascii() and (char.isalnum() or char in "._-") else "_" for char in ssid)
    stem = stem.strip("_")[:48]
    digest = hashlib.sha256(ssid.encode("utf-8")).hexdigest()
    return f"igloo-{stem or 'wifi'}-{digest}.nmconnection"


def sha(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def is_installed(root: pathlib.Path, package: str) -> bool:
    status = (root / "var/lib/dpkg/status").read_text(encoding="utf-8")
    return any(
        f"Package: {package}\n" in entry and "Status: install ok installed\n" in entry
        for entry in status.split("\n\n")
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=pathlib.Path)
    parser.add_argument("manifest", type=pathlib.Path)
    parser.add_argument("--seed", type=pathlib.Path)
    parser.add_argument("--after-login", action="store_true")
    args = parser.parse_args()
    root = args.root
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    assert (root / "var/lib/igloo/.done").is_file()
    assert not (root / "var/lib/igloo/manifest.retry.json").exists()
    assert not (root / "var/lib/igloo/seed-retry").exists()
    assert sha(root / "opt/igloo/agent.py") == EXPECTED_AGENT_SHA256
    assert stat.S_IMODE((root / "opt/igloo").stat().st_mode) == 0o755
    assert stat.S_IMODE((root / "opt/igloo/igloo_boot.py").stat().st_mode) == 0o644
    assert is_installed(root, "mint-meta-codecs")
    assert is_installed(root, "tree") and (root / "usr/bin/tree").is_file()
    # The Cinnamon login hook must have a real notification sender. A marker
    # alone cannot show that the desktop notification path was deployable.
    assert is_installed(root, "libnotify-bin")
    assert (root / "usr/bin/notify-send").is_file()
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

    networks = manifest["wifiNetworks"]
    assert len(networks) == 2 and len({item["ssid"] for item in networks}) == 2
    for item in networks:
        ssid = item["ssid"]
        path = root / "etc/NetworkManager/system-connections" / profile_name(ssid)
        assert path.is_file() and not path.is_symlink(), ssid
        metadata = path.stat()
        assert metadata.st_uid == 0 and stat.S_IMODE(metadata.st_mode) == 0o600, ssid
        keyfile = configparser.ConfigParser(interpolation=None)
        keyfile.read(path, encoding="utf-8")
        assert keyfile["connection"]["id"] == ssid
        assert keyfile["connection"]["uuid"] == str(uuid.uuid5(uuid.NAMESPACE_URL, "igloo:wifi:" + ssid))
        assert keyfile["wifi"]["ssid"] == ssid
        assert keyfile["wifi-security"]["key-mgmt"] == "wpa-psk"
        if item.get("psk"):
            assert keyfile["wifi-security"]["psk"] == item["psk"]
            assert keyfile["connection"]["autoconnect"] == "true"
        else:
            assert "psk" not in keyfile["wifi-security"]
            assert keyfile["wifi-security"]["psk-flags"] == "1"
            assert keyfile["connection"]["autoconnect"] == "false"

    log = (root / "var/log/igloo/agent.log").read_text(encoding="utf-8", errors="replace")
    assert log.count("=== Igloo first-boot agent done (0 failure(s)) ===") == 1
    assert log.count("step codecs: OK") == 1, "First-boot steps reran"
    for step in (
        "wifi", "codecs", "suggested-pkgs", "user-files", "wallpaper",
        "account-picture", "stage-credentials", "redact-seed", "cleanup-seed",
        "welcome-app",
    ):
        assert f"step {step}: OK" in log, step
    assert "Wi-Fi migration complete: 2 profile(s) written" in log
    assert "Running: nmcli connection reload" in log
    desktop = root / "etc/xdg/autostart/igloo-welcome.desktop"
    assert desktop.is_file() and not desktop.is_symlink()
    assert stat.S_IMODE(desktop.stat().st_mode) == 0o644
    only_show_in = next(
        line.removeprefix("OnlyShowIn=")
        for line in desktop.read_text(encoding="utf-8").splitlines()
        if line.startswith("OnlyShowIn=")
    )
    assert "X-Cinnamon" in only_show_in.split(";"), "Cinnamon would skip the welcome hook"
    welcome = root / "opt/igloo/igloo-welcome.sh"
    assert welcome.is_file() and not welcome.is_symlink()
    assert stat.S_IMODE(welcome.stat().st_mode) == 0o755
    secret_bytes = tuple(secret.encode() for secret in (
        manifest["user"]["linuxPasswordCrypted"],
        next(item["psk"] for item in networks if item.get("psk")),
        next(item["credentialsBlob"] for item in manifest["browsers"]
             if item.get("name") == "Google Chrome" and item.get("credentialsBlob")),
        "igloo-credential-v22",
    ))
    redacted_files = [("installed manifest", root / "var/lib/igloo/manifest.json")]
    if args.seed:
        redacted_files.extend((
            ("seed manifest", args.seed / "migration-manifest.json"),
            ("seed preseed", args.seed / "preseed.cfg"),
            ("seed initrd", args.seed / "igloo-boot/initrd"),
        ))
    for label, path in redacted_files:
        data = path.read_bytes()
        assert all(secret not in data for secret in secret_bytes), label
    if args.seed:
        assert (root / "var/lib/igloo/seed-identity.json").read_bytes() == (
            args.seed / "igloo-seed-identity.json"
        ).read_bytes()
        # A cancelled OEMDRV rewrite must not strand an unbound temporary
        # containing the original manifest, preseed, or injected initrd.
        for directory in (args.seed, args.seed / "igloo-boot"):
            assert directory.is_dir() and not directory.is_symlink()
            assert not any(child.name.startswith(".igloo-") for child in directory.iterdir()), (
                f"Unbound OEMDRV scrub temporary remains in {directory}"
            )

    # Ubiquity and debconf may retain copies outside the seed. Check only
    # bounded installed logs/state; a positive match needs a specific repair.
    inspect = [root / "var/log/syslog", root / "var/log/auth.log",
               root / "var/cache/debconf/config.dat", root / "var/cache/debconf/passwords.dat"]
    for directory in (root / "var/log/installer", root / "var/log/igloo"):
        assert not directory.is_symlink(), f"Redirected installer log directory: {directory}"
        if directory.is_dir():
            inspect.extend(path for path in directory.rglob("*") if path.is_file())
    for path in inspect:
        if not path.exists():
            continue
        assert not path.is_symlink(), f"Redirected installer log: {path}"
        assert path.stat().st_size <= 64 * 1024**2, f"Installer log exceeds readback bound: {path}"
        data = path.read_bytes()
        assert all(secret not in data for secret in secret_bytes), (
            f"A selected synthetic secret remains in {path}"
        )

    store = root / "home/mintlab/.local/share/igloo/credentials.json"
    expected_password_hash = manifest["user"]["linuxPasswordCrypted"]
    assert any(
        line.startswith("mintlab:" + expected_password_hash + ":")
        for line in (root / "etc/shadow").read_text().splitlines()
    )
    if args.after_login:
        welcome_marker = root / "home/mintlab/.igloo-welcome-done"
        uid = int(next(
            line.split(":")[2] for line in (root / "etc/passwd").read_text().splitlines()
            if line.startswith("mintlab:")
        ))
        assert welcome_marker.is_file() and not welcome_marker.is_symlink()
        assert welcome_marker.stat().st_uid == uid
        assert not store.exists(), "Encrypted credential retry store was not retired"
        user_log = root / "home/mintlab/.local/state/igloo-credentials.log"
        assert "Imported 1 browser login(s); credential store removed" in user_log.read_text(
            encoding="utf-8", errors="replace"
        )
        database = root / "home/mintlab/.config/google-chrome/Default/Login Data"
        with sqlite3.connect(f"file:{database}?mode=ro", uri=True) as connection:
            rows = connection.execute(
                "SELECT origin_url, username_value, password_value FROM logins WHERE origin_url=?",
                ("https://mint-login.invalid/login",),
            ).fetchall()
        assert len(rows) == 1 and rows[0][1] == "mint-credential-lab"
        agent_source = root / "opt/igloo/agent.py"
        spec = importlib.util.spec_from_file_location("mint_agent_v24_readback", agent_source)
        agent = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(agent)
        assert rows[0][2] == agent._chromium_v10_encrypt("igloo-credential-v22")
        assert b"igloo-credential-v22" not in database.read_bytes()
        syslog = root / "var/log/syslog"
        if syslog.is_file():
            assert "igloo-welcome.desktop': Permission denied" not in syslog.read_text(
                encoding="utf-8", errors="replace"
            )
    else:
        assert store.is_file() and not store.is_symlink()
        uid = int(next(
            line.split(":")[2] for line in (root / "etc/passwd").read_text().splitlines()
            if line.startswith("mintlab:")
        ))
        assert store.stat().st_uid == uid and stat.S_IMODE(store.stat().st_mode) == 0o600
    print("Mint v24: exact firstboot migration, two private WPA profiles, welcome and saved-login state")


if __name__ == "__main__":
    main()
