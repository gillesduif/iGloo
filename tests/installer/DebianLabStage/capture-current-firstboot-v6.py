"""Capture the disposable v6 child display through its exact HMP socket."""

from pathlib import Path
import socket
import sys
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/current-firstboot-v6-child')
SECOND = (LAB / 'qemu-second.pid').is_file()
PID = LAB / ('qemu-second.pid' if SECOND else 'qemu.pid')
MONITOR = LAB / ('qemu-second.monitor' if SECOND else 'qemu.monitor')
if len(sys.argv) != 2 or not sys.argv[1].startswith('screen-') or not sys.argv[1].endswith('.png'):
    raise RuntimeError('Expected a screen-*.png name inside the exact lab')
target = LAB / sys.argv[1]
if target.exists() or target.is_symlink() or not PID.is_file():
    raise RuntimeError('The exact screenshot output is unavailable')
with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as monitor:
    monitor.settimeout(5)
    monitor.connect(str(MONITOR))
    monitor.recv(4096)
    monitor.sendall(f'screendump {target} -f png\n'.encode())
    time.sleep(.5)
    response = monitor.recv(4096).decode(errors='replace')
if not target.is_file() or target.stat().st_size == 0 or 'Error' in response:
    raise RuntimeError('HMP did not produce a native guest screenshot: ' + response)
print(str(target))
