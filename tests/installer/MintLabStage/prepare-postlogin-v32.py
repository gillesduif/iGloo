#!/usr/bin/env python3
"""Create a fresh COW continuation of Mint's pending v31 browser import.

This only creates v32 files. It does not attach a disk or start QEMU.
"""

import json
import os
from pathlib import Path
import shutil
import subprocess


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
SOURCE = LAB / "mint-postlogin-v31"
OUT = LAB / "mint-postlogin-v32"
PARENT = SOURCE / "target-child.qcow2"
CHILD = OUT / "target-child.qcow2"
PREVIOUS_READBACK = SOURCE / "offline-readback/result.json"
TEMPLATES = (
    (LAB / "run-postlogin-v31.sh", LAB / "run-postlogin-v32.sh"),
    (LAB / "capacity-watch-postlogin-v31.py", LAB / "capacity-watch-postlogin-v32.py"),
)


def read(*argv: str) -> str:
    return subprocess.check_output(argv, text=True).strip()


def main() -> None:
    if os.geteuid() or OUT.exists() or OUT.is_symlink():
        raise RuntimeError("Run as root with an absent exact v32 output directory")
    for path in (PARENT, SOURCE / "OVMF_VARS.fd", PREVIOUS_READBACK, *(x for pair in TEMPLATES for x in pair[:1])):
        if not path.is_file() or path.is_symlink() or path.stat().st_nlink != 1:
            raise RuntimeError(f"Mint v31 input is absent or redirected: {path}")
    for _, destination in TEMPLATES:
        if destination.exists() or destination.is_symlink():
            raise RuntimeError(f"Mint v32 helper already exists: {destination}")
    result = json.loads(PREVIOUS_READBACK.read_text())
    if (result.get("protectedWindowsPartitionsUnchanged") != [True, True]
            or result.get("selectedTargetGuid") != "7862b40b-f6bd-4012-bec8-e5c2faa509e2"
            or result.get("credentialStorePending") is not True
            or result.get("credentialReceiptPresent") is not False
            or result.get("chromeFlatpakInstalled") is not True):
        raise RuntimeError("v31 readback does not support a pending browser continuation")
    info = json.loads(read("qemu-img", "info", "--output=json", str(PARENT)))
    if info.get("format") != "qcow2" or info.get("virtual-size") != 40 * 1024**3:
        raise RuntimeError("v31 backing image changed")
    subprocess.run(["qemu-img", "check", str(PARENT)], check=True,
                   stdout=subprocess.DEVNULL)
    free = os.statvfs("/mnt/c")
    if free.f_bavail * free.f_frsize <= 45 * 1024**3:
        raise RuntimeError("C: fails the 45 GiB Mint v32 preparation gate")
    OUT.mkdir(mode=0o700)
    read("qemu-img", "create", "-f", "qcow2", "-F", "qcow2", "-b", str(PARENT), str(CHILD))
    shutil.copy2(SOURCE / "OVMF_VARS.fd", OUT / "OVMF_VARS.fd")
    (OUT / "OVMF_VARS.fd").chmod(0o600)
    for source, destination in TEMPLATES:
        text = source.read_text()
        text = text.replace("mint-postlogin-v31", "mint-postlogin-v32")
        text = text.replace("run-postlogin-v31.sh", "run-postlogin-v32.sh")
        text = text.replace('LOG = OUT / "capacity.jsonl"',
                            'LOG = OUT / "capacity-v32-corrected.jsonl"')
        text = text.replace("mint-postlogin-v30/target-child.qcow2",
                            "mint-postlogin-v31/target-child.qcow2")
        text = text.replace("more than 23 GiB", "more than 45 GiB")
        destination.write_text(text)
        destination.chmod(0o700)
    child_info = json.loads(read("qemu-img", "info", "--output=json", str(CHILD)))
    if (child_info.get("backing-filename") != str(PARENT)
            or child_info.get("backing-filename-format") != "qcow2"):
        raise RuntimeError("Mint v32 child has the wrong parent")
    print("Mint v32 COW and capacity watcher prepared; QEMU and NBD untouched")


if __name__ == "__main__":
    main()
