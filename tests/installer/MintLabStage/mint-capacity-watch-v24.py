#!/usr/bin/env python3
"""Stop only a named disposable Mint v24 VM before host C: reaches its floor."""

import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
ABSOLUTE_FLOOR_BYTES = 20 * 1024**3
PRELAUNCH_FREE_BYTES = 22 * 1024**3
STOP_FREE_BYTES = 21 * 1024**3 + 512 * 1024**2
POLL_SECONDS = 1
RUNNERS = {
    "run-fresh-v24-installer.sh": "mint-monitor-fresh-v24-installer.sock",
    "run-fresh-v24-firstboot.sh": "mint-monitor-fresh-v24-firstboot.sock",
    "run-fresh-v24-seedfree.sh": "mint-monitor-fresh-v24-seedfree.sock",
}


def host_free_bytes() -> int:
    status = os.statvfs("/mnt/c")
    return status.f_bavail * status.f_frsize


def main() -> int:
    if len(sys.argv) != 3:
        raise SystemExit("Usage: mint-capacity-watch-v24.py <exact runner> <new log>")
    runner = Path(sys.argv[1]).resolve(strict=True)
    log_path = Path(sys.argv[2])
    if runner.parent != LAB or runner.name not in RUNNERS:
        raise SystemExit("Only exact disposable Mint v24 runners are allowed")
    if log_path.parent.resolve(strict=True) != LAB or log_path.exists() or log_path.is_symlink():
        raise SystemExit("Capacity log must be a new file in the Mint lab")
    if not log_path.name.startswith("mint-capacity-fresh-v24-") or log_path.suffix != ".jsonl":
        raise SystemExit("Capacity log name must identify the Mint v24 run")
    monitor = LAB / RUNNERS[runner.name]
    if monitor.exists() or monitor.is_symlink():
        raise SystemExit("The disposable Mint monitor socket already exists")
    start_free = host_free_bytes()
    if start_free <= PRELAUNCH_FREE_BYTES:
        raise SystemExit("Host C: must have more than 22 GiB free before VM launch")

    with log_path.open("x", encoding="utf-8") as log:
        log.write(json.dumps({"event": "start", "hostFreeBytes": start_free,
                              "prelaunchFreeBytes": PRELAUNCH_FREE_BYTES,
                              "proactiveStopBytes": STOP_FREE_BYTES,
                              "absoluteFloorBytes": ABSOLUTE_FLOOR_BYTES,
                              "pollSeconds": POLL_SECONDS}) + "\n")
        log.flush()
        os.fsync(log.fileno())
        launch_free = host_free_bytes()
        if launch_free <= PRELAUNCH_FREE_BYTES:
            log.write(json.dumps({"event": "prelaunch-stop", "hostFreeBytes": launch_free}) + "\n")
            log.flush()
            os.fsync(log.fileno())
            return 95
        log.write(json.dumps({"event": "launch", "hostFreeBytes": launch_free}) + "\n")
        log.flush()
        guest = subprocess.Popen(["bash", str(runner)], cwd=LAB)
        stopped_for_capacity = False
        last_record = 0.0
        try:
            while guest.poll() is None:
                free = host_free_bytes()
                now = time.monotonic()
                if now - last_record >= 30 or free <= STOP_FREE_BYTES:
                    log.write(json.dumps({"event": "sample", "hostFreeBytes": free}) + "\n")
                    log.flush()
                    last_record = now
                if free <= STOP_FREE_BYTES:
                    stopped_for_capacity = True
                    log.write(json.dumps({"event": "capacity-stop", "hostFreeBytes": free,
                                          "proactiveStopBytes": STOP_FREE_BYTES,
                                          "absoluteFloorBytes": ABSOLUTE_FLOOR_BYTES}) + "\n")
                    log.flush()
                    os.fsync(log.fileno())
                    if monitor.exists():
                        try:
                            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                                client.settimeout(3)
                                client.connect(str(monitor))
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
                time.sleep(POLL_SECONDS)
        finally:
            if guest.poll() is None:
                guest.terminate()
                try:
                    guest.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    guest.kill()
                    guest.wait()
        final_free = host_free_bytes()
        log.write(json.dumps({"event": "exit", "hostFreeBytes": final_free,
                              "guestExitCode": guest.returncode,
                              "capacityStop": stopped_for_capacity}) + "\n")
        log.flush()
        os.fsync(log.fileno())
        print(f"Mint VM exit={guest.returncode}, host C: free={final_free}, "
              f"capacity stop={stopped_for_capacity}")
        return 95 if stopped_for_capacity else guest.returncode


if __name__ == "__main__":
    raise SystemExit(main())
