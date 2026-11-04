"""Capture one Debian v7 disposable guest screen from its exact HMP socket."""

from pathlib import Path
import socket
import sys
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
name = sys.argv[1]
assert name.replace('-', '').isalnum()
phase = sys.argv[2] if len(sys.argv) > 2 else 'install'
assert phase in ('install', 'firstboot', 'secondboot')
target = LAB / f'screen-{name}.png'
assert not target.exists()
with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as monitor:
    monitor.settimeout(5)
    monitor.connect(str(LAB / f'qemu-{phase}-v7.monitor'))
    monitor.recv(4096)
    monitor.sendall(('screendump ' + str(target) + ' -f png\n').encode())
    time.sleep(1)
    monitor.recv(65536)
assert target.read_bytes()[:8] == b'\x89PNG\r\n\x1a\n'
print(target, target.stat().st_size)
