"""Boot the exact disposable Debian v6 child with a live C: capacity stop."""

import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/current-firstboot-v6-child')
CHILD = LAB / 'selected-child.qcow2'
SEED = LAB / 'seed.raw'
VARS = LAB / 'ovmf-vars.fd'
SEED_FREE = sys.argv[1:] == ['--seed-free-launch-and-watch']
PID = LAB / ('qemu-second.pid' if SEED_FREE else 'qemu.pid')
MONITOR = LAB / ('qemu-second.monitor' if SEED_FREE else 'qemu.monitor')
GIB = 1024 ** 3
STOP_FREE = 21 * GIB


def output(*args):
    return subprocess.check_output(args, text=True).strip()


def active(pid):
    process = Path('/proc') / str(pid)
    if not process.is_dir():
        return False
    status = (process / 'status').read_text()
    if 'State:\tZ' in status:
        return False
    return str(CHILD).encode() in (process / 'cmdline').read_bytes()


def stop_guest(pid, command):
    try:
        send_monitor(command)
    except OSError:
        if active(pid):
            os.kill(pid, signal.SIGTERM)


def send_monitor(command):
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
        channel.settimeout(5)
        channel.connect(str(MONITOR))
        channel.sendall((command + '\n').encode())


def launch():
    if (os.geteuid() != 0 or LAB.resolve() != LAB or
            PID.exists() or MONITOR.exists() or
            not (LAB / 'preboot-check.json').is_file() or
            not CHILD.is_file() or not SEED.is_file() or not VARS.is_file() or
            CHILD.is_symlink() or SEED.is_symlink() or VARS.is_symlink() or
            shutil.disk_usage('/mnt/c').free < 22 * GIB or
            output('losetup', '-a') or
            output('blockdev', '--getsize64', '/dev/nbd0') != '0' or
            subprocess.run(['pgrep', '-af', '[q]emu-system-x86_64'],
                           capture_output=True).returncode != 1):
        raise RuntimeError('The exact first-boot guest or capacity gate is unavailable')
    prep = json.loads((LAB / 'preparation.json').read_text())
    stage = json.loads((LAB / 'seed-stage.json').read_text())
    preboot = json.loads((LAB / 'preboot-check.json').read_text())
    if (prep['childDiskGuid'] != preboot['childDiskGuid'] or
            stage['seedDiskGuid'] != preboot['seedDiskGuid'] or
            stage['seedPartitionGuid'] != preboot['seedPartitionGuid'] or
            SEED.stat().st_size != 256 * 1024 * 1024):
        raise RuntimeError('The reviewed disposable identities changed before launch')
    if SEED_FREE:
        readback = json.loads((LAB / 'firstboot-offline-readback.json').read_text())
        if (not readback['done']['present'] or
                readback['retryManifest']['present'] or
                readback['retrySeed']['present'] or
                not readback['agentRecentSuccess']):
            raise RuntimeError('First boot is not durably complete')
    command = [
        'qemu-system-x86_64', '-machine', 'q35,accel=kvm', '-cpu', 'host',
        '-m', '6144', '-smp', '4',
        '-drive', 'if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd',
        '-drive', f'if=pflash,format=raw,file={VARS}',
        '-drive', f'if=none,id=selected,format=qcow2,file={CHILD},cache=none',
        '-device', 'virtio-blk-pci,drive=selected,bootindex=1',
        '-nic', 'user,model=virtio-net-pci',
        '-display', 'none', '-vnc', '127.0.0.1:6',
        '-monitor', f'unix:{MONITOR},server,nowait',
        '-serial', f'file:{LAB / ("qemu.second.serial.log" if SEED_FREE else "qemu.serial.log")}',
        '-no-reboot', '-pidfile', str(PID), '-daemonize',
    ]
    if not SEED_FREE:
        nic_index = command.index('-nic')
        command[nic_index:nic_index] = [
            '-drive', f'if=none,id=seed,format=raw,file={SEED},cache=none',
            '-device', 'virtio-blk-pci,drive=seed',
        ]
    (LAB / ('qemu-command-second.json' if SEED_FREE else 'qemu-command.json')).write_text(json.dumps(command, indent=2) + '\n')
    subprocess.run(command, check=True)
    time.sleep(1)
    pid = int(PID.read_text().strip())
    if not active(pid):
        raise RuntimeError('QEMU exited immediately')
    print(json.dumps({'qemuPid': pid, 'cFreeBytes': shutil.disk_usage('/mnt/c').free,
                      'stopBelowBytes': STOP_FREE, 'lab': str(LAB)}), flush=True)
    return pid


def watch(pid):
    minimum = shutil.disk_usage('/mnt/c').free
    started = time.monotonic()
    next_report = started + 30
    stop_sent = False
    while active(pid):
        free = shutil.disk_usage('/mnt/c').free
        minimum = min(minimum, free)
        if free < STOP_FREE and not stop_sent:
            stop_sent = True
            stop_guest(pid, 'system_powerdown')
            print(json.dumps({'capacityStopSent': True, 'cFreeBytes': free}), flush=True)
            stop_deadline = time.monotonic() + 30
        if stop_sent and active(pid) and time.monotonic() > stop_deadline:
            stop_guest(pid, 'quit')
            print(json.dumps({'capacityForcedStop': True, 'cFreeBytes': free}), flush=True)
            break
        if time.monotonic() >= next_report:
            print(json.dumps({'runningSeconds': round(time.monotonic() - started),
                              'cFreeBytes': free, 'minimumFreeBytes': minimum}), flush=True)
            next_report = time.monotonic() + 30
        time.sleep(2)
    summary = {
        'guestExited': not active(pid),
        'capacityStopSent': stop_sent,
        'minimumCFreeBytes': minimum,
        'finalCFreeBytes': shutil.disk_usage('/mnt/c').free,
        'elapsedSeconds': round(time.monotonic() - started),
    }
    (LAB / ('capacity-watch-second.json' if SEED_FREE else 'capacity-watch.json')).write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary), flush=True)


if __name__ == '__main__':
    if sys.argv[1:] not in (['--launch-and-watch'], ['--seed-free-launch-and-watch']):
        raise RuntimeError('Expected first or seed-free launch mode')
    watch(launch())
