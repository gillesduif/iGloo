"""Capacity stop for only the exact disposable Debian v8 QEMU process."""

import json
import os
from pathlib import Path
import shutil
import signal
import socket
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
GIB = 1024 ** 3
START_FREE = 22 * GIB
STOP_FREE = 21 * GIB


def c_free_bytes():
    return shutil.disk_usage('/mnt/c').free


def prelaunch_capacity():
    free = c_free_bytes()
    memory = next(int(line.split()[1]) * 1024 for line in
                  Path('/proc/meminfo').read_text().splitlines()
                  if line.startswith('MemAvailable:'))
    if (LAB.resolve() != LAB or free < START_FREE or
            shutil.disk_usage('/').free < 25 * GIB or
            memory < 8 * GIB or not os.access('/dev/kvm', os.R_OK | os.W_OK)):
        raise RuntimeError('Debian v8 disposable C:/WSL/RAM/KVM launch gate is unavailable')
    return {'cFreeBytes': free, 'wslFreeBytes': shutil.disk_usage('/').free,
            'memoryAvailableBytes': memory, 'startFreeBytes': START_FREE,
            'stopFreeBytes': STOP_FREE}


def _active(pid, monitor):
    process = Path('/proc') / str(pid)
    if not process.is_dir():
        return False
    try:
        status = (process / 'status').read_text()
        command = (process / 'cmdline').read_bytes()
    except OSError:
        return False
    return ('State:\tZ' not in status and b'qemu-system-x86_64' in command and
            str(LAB / 'selected.raw').encode() in command and
            str(monitor).encode() in command)


def _send_monitor(monitor, command):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
        channel.settimeout(5)
        channel.connect(str(monitor))
        channel.sendall((command + '\n').encode())


def watch(pid, monitor, phase):
    if phase not in ('installer', 'firstboot', 'secondboot') or not _active(pid, monitor):
        raise RuntimeError('Exact Debian v8 QEMU process is unavailable to capacity watcher')
    minimum = c_free_bytes()
    started = time.monotonic()
    next_report = started + 30
    stop_sent = False
    forced_stop = False
    while _active(pid, monitor):
        free = c_free_bytes()
        minimum = min(minimum, free)
        if free < STOP_FREE and not stop_sent:
            stop_sent = True
            try:
                _send_monitor(monitor, 'system_powerdown')
            except OSError:
                if _active(pid, monitor):
                    os.kill(pid, signal.SIGTERM)
            stop_deadline = time.monotonic() + 30
            print(json.dumps({'capacityStopSent': True, 'cFreeBytes': free}), flush=True)
        if stop_sent and _active(pid, monitor) and time.monotonic() > stop_deadline:
            forced_stop = True
            try:
                _send_monitor(monitor, 'quit')
            except OSError:
                if _active(pid, monitor):
                    os.kill(pid, signal.SIGTERM)
            print(json.dumps({'capacityForcedStop': True, 'cFreeBytes': free}), flush=True)
            break
        if time.monotonic() >= next_report:
            print(json.dumps({'runningSeconds': round(time.monotonic() - started),
                              'cFreeBytes': free, 'minimumFreeBytes': minimum}), flush=True)
            next_report = time.monotonic() + 30
        time.sleep(2)
    summary = {'guestExited': not _active(pid, monitor),
               'capacityStopSent': stop_sent, 'capacityForcedStop': forced_stop,
               'minimumCFreeBytes': minimum, 'finalCFreeBytes': c_free_bytes(),
               'elapsedSeconds': round(time.monotonic() - started)}
    (LAB / f'capacity-watch-{phase}-v8.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary), flush=True)
    if stop_sent or not summary['guestExited']:
        raise RuntimeError('Debian v8 disposable guest stopped by capacity gate or did not exit')
