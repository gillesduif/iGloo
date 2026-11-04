#!/usr/bin/env python3
"""Verify Mint v32 identity/capacity, then launch its bounded VM watcher."""

import json
import os
from pathlib import Path
import subprocess
import sys


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
OUT = LAB / "mint-postlogin-v32"
CHILD = OUT / "target-child.qcow2"
PARENT = LAB / "mint-postlogin-v31/target-child.qcow2"
TARGET_BASE = LAB / "mint-target-fresh-v28.raw"
DECOY = LAB / "mint-decoy-fresh-v28.raw"
WATCHER = LAB / "capacity-watch-postlogin-v32.py"
RUNNER = LAB / "run-postlogin-v32.sh"


def read(*argv: str) -> str:
    return subprocess.check_output(argv, text=True).strip()


def main() -> None:
    check_only = sys.argv[1:] == ["--check-only"]
    if sys.argv[1:] and not check_only:
        raise RuntimeError("Use --check-only or no argument")
    if os.geteuid() or not OUT.is_dir() or OUT.is_symlink():
        raise RuntimeError("The exact Mint v32 directory is unavailable")
    for file in (CHILD, PARENT, OUT / "OVMF_VARS.fd", TARGET_BASE, DECOY, WATCHER, RUNNER):
        if not file.is_file() or file.is_symlink() or file.stat().st_nlink != 1:
            raise RuntimeError(f"Mint v32 input is absent or redirected: {file}")
    if ((OUT / "qemu-monitor.sock").exists()
            or (OUT / "capacity-v32-corrected.jsonl").exists()):
        raise RuntimeError("The corrected Mint v32 runner has already been started")
    for disk, guid in ((TARGET_BASE, "7862b40b-f6bd-4012-bec8-e5c2faa509e2"),
                       (DECOY, "7a9c98ef-c49c-42c7-8d41-c6bcb6ca8b03")):
        if f"disk identifier (guid): {guid}" not in read("sgdisk", "-p", str(disk)).lower():
            raise RuntimeError("Mint selected or decoy disk identity changed")
    info = json.loads(read("qemu-img", "info", "--output=json", str(CHILD)))
    if (info.get("format") != "qcow2" or info.get("virtual-size") != 40 * 1024**3
            or info.get("backing-filename") != str(PARENT)
            or info.get("backing-filename-format") != "qcow2"):
        raise RuntimeError("Mint v32 child backing chain differs from review")
    subprocess.run(["qemu-img", "check", str(CHILD)], check=True, stdout=subprocess.DEVNULL)
    runner = RUNNER.read_text()
    watcher = WATCHER.read_text()
    if ('OUT = LAB / "mint-postlogin-v32"' not in watcher
            or 'RUNNER = LAB / "run-postlogin-v32.sh"' not in watcher
            or 'MONITOR = OUT / "qemu-monitor.sock"' not in watcher
            or 'LOG = OUT / "capacity-v32-corrected.jsonl"' not in watcher):
        raise RuntimeError("Mint v32 watcher does not launch the exact v32 runner")
    if ('out="$lab/mint-postlogin-v32"' not in runner
            or 'target="$out/target-child.qcow2"' not in runner
            or 'vars="$out/OVMF_VARS.fd"' not in runner
            or 'parent="$lab/mint-postlogin-v31/target-child.qcow2"' not in runner
            or '-drive if=none,id=target,format=qcow2,file="$target"' not in runner
            or '-drive if=none,id=seed,' in runner
            or 'readonly=on,file="$decoy"' not in runner):
        raise RuntimeError("Mint v32 runner media binding is unsafe")
    free = os.statvfs("/mnt/c")
    free_bytes = free.f_bavail * free.f_frsize
    if free_bytes <= 45 * 1024**3:
        raise RuntimeError("C: fails the 45 GiB Mint v32 launch gate")
    slot_occupied = (any(Path(f"/sys/block/nbd{index}/pid").exists() for index in (0, 1))
                     or subprocess.run(["pgrep", "-f", "[q]emu-system-x86_64"],
                                       capture_output=True).returncode == 0)
    if check_only:
        print(f"Mint v32 static preflight passed; C: free {free_bytes / 1024**3:.2f} GiB; "
              f"VM slot occupied: {slot_occupied}")
        return
    if slot_occupied:
        raise RuntimeError("Another installer VM or NBD attachment is active")
    print(f"Mint v32 preflight passed: selected/decoy GUIDs, v31 COW parent, "
          f"seed detached, C: free {free_bytes / 1024**3:.2f} GiB", flush=True)
    os.execv("/usr/bin/python3", ["python3", str(WATCHER)])


if __name__ == "__main__":
    main()
