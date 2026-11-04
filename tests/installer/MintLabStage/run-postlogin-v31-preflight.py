#!/usr/bin/env python3
"""Verify exact Mint lab media and host capacity before starting v31 QEMU."""

import json
import os
from pathlib import Path
import subprocess


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
OUT = LAB / "mint-postlogin-v31"
CHILD = OUT / "target-child.qcow2"
PARENT = LAB / "mint-postlogin-v30/target-child.qcow2"
TARGET_BASE = LAB / "mint-target-fresh-v28.raw"
DECOY = LAB / "mint-decoy-fresh-v28.raw"
WATCHER = LAB / "capacity-watch-postlogin-v31.py"
RUNNER = LAB / "run-postlogin-v31.sh"


def read(*argv: str) -> str:
    return subprocess.check_output(argv, text=True).strip()


def main() -> None:
    if os.geteuid() != 0 or not OUT.is_dir() or OUT.is_symlink():
        raise RuntimeError("The exact Mint v31 child directory is unavailable")
    for file in (CHILD, PARENT, TARGET_BASE, DECOY, WATCHER, RUNNER):
        if not file.is_file() or file.is_symlink() or file.stat().st_nlink != 1:
            raise RuntimeError(f"Mint v31 input is absent or redirected: {file}")
    if (OUT / "qemu-monitor.sock").exists() or (OUT / "capacity.jsonl").exists():
        raise RuntimeError("Mint v31 QEMU has already been started")
    if any(Path(f"/sys/block/nbd{index}/pid").exists() for index in (0, 1)):
        raise RuntimeError("Another lab has an NBD device attached")
    if subprocess.run(["pgrep", "-f", "[q]emu-system-x86_64"],
                      capture_output=True).returncode == 0:
        raise RuntimeError("Another installer VM is still running")
    for disk, guid in ((TARGET_BASE, "7862b40b-f6bd-4012-bec8-e5c2faa509e2"),
                       (DECOY, "7a9c98ef-c49c-42c7-8d41-c6bcb6ca8b03")):
        if f"disk identifier (guid): {guid}" not in read("sgdisk", "-p", str(disk)).lower():
            raise RuntimeError("Mint selected or decoy disk identity changed")
    info = json.loads(read("qemu-img", "info", "--output=json", str(CHILD)))
    if (info["format"] != "qcow2" or info["virtual-size"] != 40 * 1024**3
            or info["backing-filename"] != str(PARENT)
            or info["backing-filename-format"] != "qcow2"):
        raise RuntimeError("Mint v31 child backing chain differs from review")
    subprocess.run(["qemu-img", "check", str(CHILD)], check=True,
                   stdout=subprocess.DEVNULL)
    runner = RUNNER.read_text()
    if ('parent="$lab/mint-postlogin-v30/target-child.qcow2"' not in runner
            or '-drive if=none,id=seed,' in runner
            or 'readonly=on,file="$decoy"' not in runner):
        raise RuntimeError("Mint v31 runner media binding is unsafe")
    free = os.statvfs("/mnt/c")
    free_bytes = free.f_bavail * free.f_frsize
    if free_bytes <= 45 * 1024**3:
        raise RuntimeError("C: fails the 45 GiB Mint v31 launch gate")
    print(f"Mint v31 preflight passed: selected/decoy GUIDs, COW parent, seed detached, "
          f"C: free {free_bytes / 1024**3:.2f} GiB", flush=True)
    os.execv("/usr/bin/python3", ["python3", str(WATCHER)])


if __name__ == "__main__":
    main()
