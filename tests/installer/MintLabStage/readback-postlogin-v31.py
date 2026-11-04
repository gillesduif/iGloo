#!/usr/bin/env python3
"""Read the bounded Mint v31 child after QEMU exits, without guest writes."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
OUT = LAB / "mint-postlogin-v31"
CHILD = OUT / "target-child.qcow2"
PARENT = LAB / "mint-postlogin-v30/target-child.qcow2"
PREHASH = LAB / "mint-fresh-v28-protected-prehashes.txt"
GUID = "7862b40b-f6bd-4012-bec8-e5c2faa509e2"


def command(*args: str) -> str:
    return subprocess.check_output(args, text=True).strip()


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(4 * 1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> None:
    if os.geteuid() or not CHILD.is_file() or CHILD.is_symlink():
        raise RuntimeError("Expected root and exact v31 child")
    if subprocess.run(["pgrep", "-f", "[q]emu-system-x86_64"], capture_output=True).returncode == 0:
        raise RuntimeError("Installer VM is still active")
    if Path("/sys/block/nbd0/pid").exists():
        raise RuntimeError("NBD0 is already attached")
    info = json.loads(command("qemu-img", "info", "--output=json", str(CHILD)))
    if info.get("backing-filename") != str(PARENT) or info.get("backing-filename-format") != "qcow2":
        raise RuntimeError("The v31 child backing chain changed")
    subprocess.run(["modprobe", "nbd", "max_part=16"], check=True)
    mnt = OUT / "root-mnt"
    if subprocess.run(["findmnt", "-rn", "-o", "SOURCE", "--mountpoint", str(mnt)],
                      capture_output=True).returncode == 0:
        raise RuntimeError("Mountpoint is occupied")
    connected = mounted = False
    try:
        subprocess.run(["qemu-nbd", "--read-only", "--connect=/dev/nbd0", "--format=qcow2", str(CHILD)], check=True)
        connected = True
        command("udevadm", "settle")
        if command("lsblk", "-dnro", "PTUUID", "/dev/nbd0").lower() != GUID:
            raise RuntimeError("Selected target GUID changed")
        expected = [line.split()[0] for line in PREHASH.read_text().splitlines()[:2]]
        protected_ok = [digest(Path(f"/dev/nbd0p{i}")) == value for i, value in enumerate(expected, 1)]
        if not all(protected_ok):
            raise RuntimeError("Protected Windows partition changed")
        subprocess.run(["mount", "-t", "ext4", "-o", "ro,noload", "/dev/nbd0p4", str(mnt)], check=True)
        mounted = True
        home = mnt / "home/mintlab"
        store = home / ".local/share/igloo/credentials.json"
        receipt = store.with_name("credentials.done.json")
        log = home / ".local/state/igloo-credentials.log"
        chrome = home / ".var/app/com.google.Chrome/config/google-chrome/Default"
        flatpak = mnt / "var/lib/flatpak/app/com.google.Chrome"
        evidence = OUT / "offline-readback"
        if evidence.exists() or evidence.is_symlink():
            raise RuntimeError("Existing v31 readback must be preserved")
        evidence.mkdir(mode=0o700)
        for src, name in ((log, "igloo-credentials.log"),
                          (receipt, "credentials.done.json"),
                          (chrome / "Login Data", "Login Data"),
                          (chrome / "Login Data-wal", "Login Data-wal"),
                          (chrome / "Login Data-shm", "Login Data-shm")):
            if src.is_file() and not src.is_symlink():
                dst = evidence / name
                shutil.copyfile(src, dst)
                dst.chmod(stat.S_IRUSR | stat.S_IWUSR)
        receipt_status = None
        if receipt.is_file() and not receipt.is_symlink():
            receipt_status = json.loads(receipt.read_text()).get("status")
        result = {"selectedTargetGuid": GUID, "protectedWindowsPartitionsUnchanged": protected_ok,
                  "chromeFlatpakInstalled": flatpak.exists(), "credentialStorePending": store.is_file(),
                  "credentialReceiptPresent": receipt.is_file(),
                  "credentialReceiptStatus": receipt_status,
                  "guestLogCopied": (evidence / "igloo-credentials.log").is_file(),
                  "nativeChromeLoginDataCopied": (evidence / "Login Data").is_file()}
        (evidence / "result.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result))
    finally:
        if mounted:
            subprocess.run(["umount", str(mnt)], check=True)
        if connected:
            subprocess.run(["qemu-nbd", "--disconnect", "/dev/nbd0"], check=True)


if __name__ == "__main__":
    main()
