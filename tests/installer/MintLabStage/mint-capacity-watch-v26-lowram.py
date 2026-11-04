#!/usr/bin/env python3
"""Bound one seed-only Mint 4 GiB diagnostic by the host C: free-space floor."""

import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
RUNNER = LAB / "run-fresh-v26-lowram-casper.sh"
MONITOR = LAB / "mint-monitor-fresh-v26-lowram.sock"
LOG = LAB / "mint-capacity-fresh-v26-lowram.jsonl"
PRELAUNCH_BYTES = 22 * 1024**3
STOP_BYTES = 21 * 1024**3 + 512 * 1024**2
ABSOLUTE_FLOOR_BYTES = 20 * 1024**3


def host_free_bytes() -> int:
    status = os.statvfs("/mnt/c")
    return status.f_bavail * status.f_frsize


def main() -> int:
    if sys.argv[1:]:
        raise SystemExit("This diagnostic watcher accepts no path arguments")
    if RUNNER.resolve(strict=True) != RUNNER or not RUNNER.is_file():
        raise SystemExit("Exact Mint low-RAM runner is unavailable or redirected")
    if LOG.exists() or LOG.is_symlink() or MONITOR.exists() or MONITOR.is_symlink():
        raise SystemExit("The disposable probe already has a log or monitor socket")
    start_free = host_free_bytes()
    if start_free <= PRELAUNCH_BYTES:
        raise SystemExit("Host C: must have more than 22 GiB free before launch")

    with LOG.open("x", encoding="utf-8") as log:
        def record(event: str, **details: object) -> None:
            log.write(json.dumps({"event": event, **details}) + "\n")
            log.flush()
            os.fsync(log.fileno())

        record("start", hostFreeBytes=start_free,
               prelaunchFreeBytes=PRELAUNCH_BYTES,
               proactiveStopBytes=STOP_BYTES,
               absoluteFloorBytes=ABSOLUTE_FLOOR_BYTES,
               pollSeconds=1, timeoutSeconds=720, guestMiB=4096)
        launch_free = host_free_bytes()
        if launch_free <= PRELAUNCH_BYTES:
            record("prelaunch-stop", hostFreeBytes=launch_free)
            return 95
        record("launch", hostFreeBytes=launch_free)
        guest = subprocess.Popen(["bash", str(RUNNER)], cwd=LAB)
        stopped_for_capacity = False
        last_sample = 0.0
        try:
            while guest.poll() is None:
                free = host_free_bytes()
                now = time.monotonic()
                if now - last_sample >= 30 or free <= STOP_BYTES:
                    record("sample", hostFreeBytes=free)
                    last_sample = now
                if free <= STOP_BYTES:
                    stopped_for_capacity = True
                    record("capacity-stop", hostFreeBytes=free)
                    if MONITOR.exists():
                        try:
                            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                                client.settimeout(3)
                                client.connect(str(MONITOR))
                                client.sendall(b"quit\n")
                        except OSError:
                            guest.terminate()
                    else:
                        guest.terminate()
                    try:
                        guest.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        guest.kill()
                        guest.wait()
                    break
                time.sleep(1)
        finally:
            if guest.poll() is None:
                guest.terminate()
                try:
                    guest.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    guest.kill()
                    guest.wait()
        record("exit", hostFreeBytes=host_free_bytes(),
               guestExitCode=guest.returncode, capacityStop=stopped_for_capacity)
        return 95 if stopped_for_capacity else guest.returncode


if __name__ == "__main__":
    raise SystemExit(main())
