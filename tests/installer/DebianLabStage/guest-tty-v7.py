"""Read-only tty checks in the disposable Debian v7 guest via QEMU HMP."""

from pathlib import Path
import socket
import sys
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v7')
mode = sys.argv[1]
assert mode in ('login', 'service', 'network', 'gui-select', 'gui-password', 'credential-password')
phase = 'secondboot' if mode.startswith('gui-') or mode == 'credential-password' else 'firstboot'
KEYS = {' ': 'spc', '-': 'minus', '.': 'dot', '/': 'slash', '=': 'equal'}


def send_text(monitor, value):
    for char in value:
        key = KEYS.get(char, char)
        assert key.isalnum() or key in KEYS.values(), char
        monitor.sendall(f'sendkey {key}\n'.encode())
        time.sleep(.08)
    monitor.sendall(b'sendkey ret\n')
    time.sleep(.5)


with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as monitor:
    monitor.settimeout(3)
    monitor.connect(str(LAB / f'qemu-{phase}-v7.monitor'))
    monitor.recv(4096)
    if mode == 'login':
        send_text(monitor, 'igloolab')
        time.sleep(.5)
        send_text(monitor, 'igloolab123')
    elif mode == 'service':
        send_text(monitor, 'systemctl show igloo-bootstrap.service -p ActiveState -p Result')
    elif mode == 'network':
        send_text(monitor, 'nmcli general status')
    elif mode == 'gui-select':
        monitor.sendall(b'sendkey ret 500\n')
        time.sleep(1)
        monitor.recv(4096)
    elif mode in ('gui-password', 'credential-password'):
        send_text(monitor, 'igloolab123')
print('sent read-only guest tty check:', mode)
