#!/usr/bin/env python3
"""Prepare a bounded Mint COW for a real browser and synthetic login import.

The existing v30 child and every historical Mint image remain unchanged. This
script writes only the new v31 child; it deliberately does not boot the VM.
"""

import ast
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import stat
import subprocess

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
OUT = LAB / "mint-postlogin-v31"
PARENT = LAB / "mint-postlogin-v30/target-child.qcow2"
DECOY = LAB / "mint-decoy-fresh-v28.raw"
VARS = LAB / "mint-postlogin-v30/OVMF_VARS.fd"
PREHASH = LAB / "mint-fresh-v28-protected-prehashes.txt"
PASSWORD_HELPER = LAB / "mint-hmp-type-credential-v22.py"
AGENT = Path(__file__).resolve().parents[3] / "distros/_debian-family/agent/agent.py"
TARGET_GUID = "7862b40b-f6bd-4012-bec8-e5c2faa509e2"
CHROME_APP = "com.google.Chrome"
URL = "https://mint-v31-credential.invalid/login"
USERNAME = "mint-v31-synthetic"


def run(*args: str) -> str:
    return subprocess.check_output(args, text=True).strip()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(4 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def private_write(path: Path, contents: bytes, owner: tuple[int, int] | None = None) -> None:
    temporary = path.with_name(path.name + ".v31-writing")
    if temporary.exists() or temporary.is_symlink():
        raise RuntimeError("A v31 temporary file already exists")
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(contents)
            output.flush()
            os.fsync(output.fileno())
        if owner is not None:
            os.chown(temporary, *owner)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def lab_account_password() -> str:
    """Read the existing synthetic-only login helper without echoing its value."""
    tree = ast.parse(PASSWORD_HELPER.read_text(encoding="utf-8"))
    values = [node.value.value for node in tree.body
              if isinstance(node, ast.Assign)
              and any(isinstance(target, ast.Name) and target.id == "synthetic_password"
                      for target in node.targets)
              and isinstance(node.value, ast.Constant)
              and isinstance(node.value.value, str)]
    if len(values) != 1 or not values[0].isascii() or not values[0].isalnum():
        raise RuntimeError("The synthetic Mint account password helper changed")
    return values[0]


def main() -> None:
    if os.geteuid() != 0 or OUT.exists() or OUT.is_symlink():
        raise RuntimeError("Run as root with an absent exact v31 output directory")
    for path in (PARENT, DECOY, VARS, PREHASH, PASSWORD_HELPER, AGENT):
        if not path.is_file() or path.is_symlink() or path.stat().st_nlink != 1:
            raise RuntimeError(f"Required exact Mint input is absent or redirected: {path}")
    if Path("/sys/block/nbd0/pid").exists() or Path("/sys/block/nbd1/pid").exists():
        raise RuntimeError("A network block device is already attached")
    if subprocess.run(["pgrep", "-f", "[q]emu-system-x86_64"],
                      capture_output=True).returncode == 0:
        raise RuntimeError("Wait until the other installer VM releases QEMU")
    free = os.statvfs("/mnt/c")
    if free.f_bavail * free.f_frsize <= 45 * 1024**3:
        raise RuntimeError("C: has less than the 45 GiB Mint v31 preparation gate")
    info = json.loads(run("qemu-img", "info", "--output=json", str(PARENT)))
    if info["format"] != "qcow2" or info["virtual-size"] != 40 * 1024**3:
        raise RuntimeError("The v30 parent image has an unexpected format or size")
    subprocess.run(["qemu-img", "check", str(PARENT)], check=True,
                   stdout=subprocess.DEVNULL)
    if TARGET_GUID not in run("sgdisk", "-p", str(LAB / "mint-target-fresh-v28.raw")).lower():
        raise RuntimeError("The protected Mint target identity changed")
    runner = LAB / "run-postlogin-v31.sh"
    watcher = LAB / "capacity-watch-postlogin-v31.py"
    for output, source in ((runner, LAB / "run-postlogin-v30.sh"),
                           (watcher, LAB / "capacity-watch-postlogin-v30.py")):
        if output.exists() or output.is_symlink() or not source.is_file():
            raise RuntimeError("Mint v31 runner state is not fresh")

    OUT.mkdir(mode=0o700)
    child = OUT / "target-child.qcow2"
    run("qemu-img", "create", "-f", "qcow2", "-F", "qcow2", "-b", str(PARENT), str(child))
    shutil.copy2(VARS, OUT / "OVMF_VARS.fd")
    os.chmod(OUT / "OVMF_VARS.fd", 0o600)
    info = json.loads(run("qemu-img", "info", "--output=json", str(child)))
    if info["backing-filename"] != str(PARENT) or info["backing-filename-format"] != "qcow2":
        raise RuntimeError("The Mint v31 child has the wrong parent")

    root = OUT / "root-mnt"
    root.mkdir(mode=0o700)
    connected = mounted = False
    try:
        subprocess.run(["qemu-nbd", "--connect=/dev/nbd0", "--format=qcow2", str(child)],
                       check=True)
        connected = True
        run("udevadm", "settle")
        guid = run("lsblk", "-dnro", "PTUUID", "/dev/nbd0").lower()
        if guid != TARGET_GUID:
            raise RuntimeError("Mint v31 target disk GUID differs from the selected target")
        expected = [line.split()[0] for line in PREHASH.read_text().splitlines()[:2]]
        for index, digest in enumerate(expected, start=1):
            if sha256_file(Path(f"/dev/nbd0p{index}")) != digest:
                raise RuntimeError(f"Protected Mint partition {index} changed")
        subprocess.run(["mount", "-t", "ext4", "-o", "rw", "/dev/nbd0p4", str(root)],
                       check=True)
        mounted = True

        home = root / "home/mintlab"
        credential_dir = home / ".local/share/igloo"
        store = credential_dir / "credentials.json"
        if (not home.is_dir() or home.is_symlink() or not store.is_file()
                or store.is_symlink() or stat.S_IMODE(store.stat().st_mode) != 0o600
                or store.with_name("credentials.done.json").exists()):
            raise RuntimeError("Expected private pending Mint credential store is absent")
        owner = (home.stat().st_uid, home.stat().st_gid)

        password = lab_account_password()
        synthetic_login = secrets.token_urlsafe(18)
        payload = json.dumps({"browser": "Google Chrome", "logins": [
            {"url": URL, "username": USERNAME, "password": synthetic_login}],
            "cookies": []}, separators=(",", ":")).encode("utf-8")
        salt, nonce = secrets.token_bytes(16), secrets.token_bytes(12)
        key = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, 600_000, 32)
        blob = base64.b64encode(b"IGCRD001" + salt + nonce
                                + AESGCM(key).encrypt(nonce, payload, None)).decode("ascii")
        entry = {"name": "Google Chrome", "blob": blob}
        selected = [entry]
        canonical = json.dumps(selected, sort_keys=True, separators=(",", ":"),
                               ensure_ascii=False).encode()
        selection_hash = hashlib.sha256(canonical).hexdigest()
        private_write(store, json.dumps({"attempts": 0, "browsers": selected,
                                         "selectionSha256": selection_hash}).encode(), owner)
        private_write(OUT / "synthetic-envelope.json", json.dumps({
            "entry": entry, "selectionSha256": selection_hash,
            "url": URL, "username": USERNAME,
            "passwordSha256": hashlib.sha256(synthetic_login.encode()).hexdigest(),
        }).encode())

        installed_agent = root / "opt/igloo/agent.py"
        private_write(installed_agent, AGENT.read_bytes())
        os.chmod(installed_agent, 0o755)

        lab_dir = root / "opt/igloo-lab"
        lab_dir.mkdir(mode=0o755, exist_ok=True)
        manifest = {"browsers": [{"name": "Google Chrome", "engine": "chromium"}],
                    "suggestedPackages": []}
        (lab_dir / "selected-browser.json").write_text(json.dumps(manifest) + "\n")
        installer = lab_dir / "install-browser.sh"
        installer.write_text("""#!/bin/bash
set -euo pipefail
if test -f /var/lib/igloo-lab/chrome-ready; then exit 0; fi
printf 'MINT_V31_CHROME_INSTALL_START\\n' >/dev/ttyS0
cd /opt/igloo
python3 - <<'PY'
import agent
import json
with open('/opt/igloo-lab/selected-browser.json', encoding='utf-8') as input_file:
    manifest = json.load(input_file)
agent.setup_flathub(manifest)
agent.install_suggested_packages(manifest)
from pathlib import Path
home = Path('/home/mintlab')
agent._place_chromium_files('Google Chrome',
                            home / '.config/google-chrome/Default', home)
agent.run_cmd(['chown', '-R', 'mintlab:mintlab',
               str(home / '.var/app/com.google.Chrome')])
PY
flatpak info --system com.google.Chrome >/dev/null
install -d -m 0700 /var/lib/igloo-lab
install -m 0600 /dev/null /var/lib/igloo-lab/chrome-ready
printf 'MINT_V31_CHROME_INSTALL_READY\\n' >/dev/ttyS0
""")
        installer.chmod(0o755)
        unit = root / "etc/systemd/system/igloo-lab-browser-install.service"
        unit.write_text("""[Unit]
Description=Install selected browser in disposable Mint credential lab
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
TimeoutStartSec=1200
ExecStart=/opt/igloo-lab/install-browser.sh

[Install]
WantedBy=multi-user.target
""")
        wants = root / "etc/systemd/system/multi-user.target.wants"
        wants.mkdir(exist_ok=True)
        (wants / unit.name).symlink_to("../" + unit.name)
        os.sync()
    finally:
        if mounted:
            subprocess.run(["umount", str(root)], check=True)
        if connected:
            subprocess.run(["qemu-nbd", "--disconnect", "/dev/nbd0"], check=True)

    subprocess.run(["qemu-img", "check", str(child)], check=True,
                   stdout=subprocess.DEVNULL)
    runner_text = (LAB / "run-postlogin-v30.sh").read_text().replace(
        "mint-postlogin-v30", "mint-postlogin-v31").replace(
        'parent="$lab/mint-target-retry-v29.qcow2"',
        'parent="$lab/mint-postlogin-v30/target-child.qcow2"')
    if 'parent="$lab/mint-postlogin-v30/target-child.qcow2"' not in runner_text:
        raise RuntimeError("Mint v31 runner parent binding failed")
    runner.write_text(runner_text)
    runner.chmod(0o700)
    watcher_text = (LAB / "capacity-watch-postlogin-v30.py").read_text().replace(
        "postlogin-v30", "postlogin-v31").replace(
        "PRELAUNCH_FREE = 23 * 1024**3", "PRELAUNCH_FREE = 45 * 1024**3").replace(
        "STOP_FREE = 21 * 1024**3 + 512 * 1024**2",
        "STOP_FREE = 42 * 1024**3 + 512 * 1024**2").replace(
        "ABSOLUTE_FLOOR = 21 * 1024**3", "ABSOLUTE_FLOOR = 42 * 1024**3").replace(
        "MAX_SECONDS = 900", "MAX_SECONDS = 1800")
    watcher.write_text(watcher_text)
    watcher.chmod(0o700)
    print("Mint v31 child prepared with new synthetic envelope and current agent; "
          "protected target partitions verified; browser install unit is guest-only")


if __name__ == "__main__":
    main()
