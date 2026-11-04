"""Capture the exact disposable Debian v8 QEMU HMP screen as PNG."""

from pathlib import Path
import socket
import sys
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
name = sys.argv[1]
phase = sys.argv[2] if len(sys.argv) > 2 else 'install'
if not name.replace('-', '').isalnum() or phase not in ('install', 'firstboot', 'secondboot'):
    raise RuntimeError('Exact Debian v8 capture name or phase is invalid')
target = LAB / f'screen-{name}.png'
if target.exists():
    raise RuntimeError('Debian v8 screen capture would overwrite an observation')
with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as monitor:
    monitor.settimeout(5)
    monitor.connect(str(LAB / f'qemu-{phase}-v8.monitor'))
    monitor.recv(4096)
    monitor.sendall(('screendump ' + str(target) + ' -f png\n').encode())
    time.sleep(1)
    monitor.recv(65536)
if target.read_bytes()[:8] != b'\x89PNG\r\n\x1a\n':
    raise RuntimeError('Debian v8 QEMU did not produce a PNG capture')
print(target, target.stat().st_size)
