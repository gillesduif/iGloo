#!/usr/bin/env python3
"""Stop an exact disposable Mint QEMU run before host C: free space gets low."""

import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
MINIMUM_FREE_BYTES = 22 * 1024**3
RUNNERS = {
    "run-fresh-v23-installer.sh": "mint-monitor-fresh-v23-installer.sock",
    "run-fresh-v23-firstboot.sh": "mint-monitor-fresh-v23-firstboot.sock",
    "run-fresh-v23-seedfree.sh": "mint-monitor-fresh-v23-seedfree.sock",
}


def host_free_bytes():
    status = os.statvfs("/mnt/c")
    return status.f_bavail * status.f_frsize


def main():
    if len(sys.argv) != 3:
        raise SystemExit("Usage: capacity_watch.py <exact Mint runner> <new log path>")
    runner = Path(sys.argv[1]).resolve(strict=True)
    log_path = Path(sys.argv[2])
    if runner.parent != LAB or RUNNERS.get(runner.name) is None:
        raise SystemExit("Only the exact disposable Mint v23 runners are allowed")
    if log_path.parent != LAB or log_path.exists():
        raise SystemExit("Capacity log must be a new file in the Mint lab")
    monitor = LAB / RUNNERS[runner.name]
    if monitor.exists():
        raise SystemExit("The disposable Mint monitor socket already exists")
    start_free = host_free_bytes()
    if start_free < MINIMUM_FREE_BYTES:
        raise SystemExit("Host C: free space is already below the lab guard")

    with log_path.open("x", encoding="utf-8") as log:
        log.write(json.dumps({"event": "start", "hostFreeBytes": start_free}) + "\n")
        log.flush()
        os.fsync(log.fileno())
        guest = subprocess.Popen(["bash", str(runner)], cwd=LAB)
        last_record = 0.0
        stopped_for_capacity = False
        try:
            while guest.poll() is None:
                free = host_free_bytes()
                now = time.monotonic()
                if now - last_record >= 30 or free < MINIMUM_FREE_BYTES:
                    log.write(json.dumps({"event": "sample", "hostFreeBytes": free}) + "\n")
                    log.flush()
                    last_record = now
                if free < MINIMUM_FREE_BYTES:
                    stopped_for_capacity = True
                    log.write(json.dumps({"event": "capacity-stop", "hostFreeBytes": free}) + "\n")
                    log.flush()
                    os.fsync(log.fileno())
                    if monitor.exists():
                        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                            client.settimeout(3)
                            client.connect(str(monitor))
                            client.sendall(b"quit\n")
                    else:
                        guest.terminate()
                    try:
                        guest.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        guest.kill()
                        guest.wait()
                    break
                time.sleep(5)
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
