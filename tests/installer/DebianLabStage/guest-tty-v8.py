"""Read the exact disposable Debian v8 guest service state via QEMU HMP."""

from pathlib import Path
import socket
import sys
import time

from fresh_v8_capacity import _active


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
phase, mode = sys.argv[1:]
if phase not in ('firstboot', 'secondboot') or mode not in (
        'switch', 'login', 'status', 'gui', 'gui-select', 'gui-password', 'credential-password',
        'mouse-help', 'mouse-info', 'gui-click', 'gui-tab', 'gui-shift-tab', 'gui-space',
        'gdm-status', 'gdm-journal', 'gdm-journal-root', 'gdm-unit-journal', 'sudo-password',
        'powerdown', 'guest-poweroff'):
    raise RuntimeError('Expected exact v8 guest phase and read-only tty command')
monitor_path = LAB / f'qemu-{phase}-v8.monitor'
pid = int((LAB / f'qemu-{phase}-v8.pid').read_text())
if not _active(pid, monitor_path):
    raise RuntimeError('Exact disposable v8 guest is not active')

keys = {' ': 'spc', '-': 'minus', '.': 'dot', '/': 'slash'}


def send_text(monitor, value):
    for char in value:
        key = keys.get(char, char)
        if not key.isalnum() and key not in keys.values():
            raise RuntimeError('Unsupported diagnostic key')
        monitor.sendall(f'sendkey {key}\n'.encode())
        time.sleep(.08)
    monitor.sendall(b'sendkey ret\n')
    time.sleep(.5)


with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as monitor:
    monitor.settimeout(5)
    monitor.connect(str(monitor_path))
    monitor.recv(4096)
    if mode == 'switch':
        monitor.sendall(b'sendkey ctrl-alt-f3\n')
        time.sleep(1)
        print(monitor.recv(4096).decode(errors='replace').strip())
    elif mode == 'powerdown':
        monitor.sendall(b'system_powerdown\n')
    elif mode == 'guest-poweroff':
        send_text(monitor, 'sudo systemctl poweroff')
    elif mode == 'gui':
        monitor.sendall(b'sendkey ctrl-alt-f1\n')
        time.sleep(1)
    elif mode == 'login':
        send_text(monitor, 'igloolab')
        send_text(monitor, 'igloolab123')
    elif mode == 'gui-select':
        monitor.sendall(b'sendkey ret 500\n')
    elif mode == 'gui-tab':
        monitor.sendall(b'sendkey tab 500\n')
        time.sleep(.7)
        monitor.sendall(b'sendkey ret 500\n')
    elif mode == 'gui-shift-tab':
        monitor.sendall(b'sendkey shift-tab 500\n')
        time.sleep(.7)
        monitor.sendall(b'sendkey ret 500\n')
    elif mode == 'gui-space':
        monitor.sendall(b'sendkey spc 500\n')
    elif mode == 'mouse-help':
        monitor.sendall(b'help mouse_move\n')
        time.sleep(.2)
        print(monitor.recv(4096).decode(errors='replace').strip())
    elif mode == 'mouse-info':
        monitor.sendall(b'info mice\n')
        time.sleep(.2)
        print(monitor.recv(4096).decode(errors='replace').strip())
    elif mode == 'gui-click':
        monitor.sendall(b'mouse_move -515 -380\n')
        time.sleep(.3)
        monitor.sendall(b'mouse_button 1\n')
        time.sleep(.3)
        monitor.sendall(b'mouse_button 0\n')
        time.sleep(.3)
        print(monitor.recv(4096).decode(errors='replace').strip())
    elif mode in ('gui-password', 'credential-password'):
        send_text(monitor, 'igloolab123')
    elif mode == 'gdm-status':
        send_text(monitor, 'systemctl status gdm --no-pager')
    elif mode == 'gdm-journal':
        send_text(monitor, 'journalctl -b -u gdm3 --no-pager -n 20')
    elif mode == 'gdm-journal-root':
        send_text(monitor, 'sudo journalctl -b -u gdm3 --no-pager -n 20')
    elif mode == 'gdm-unit-journal':
        send_text(monitor, 'sudo journalctl -b -u gdm --no-pager -n 20')
    elif mode == 'sudo-password':
        send_text(monitor, 'igloolab123')
    else:
        send_text(monitor, 'systemctl status igloo-bootstrap.service --no-pager')
print('sent exact guest read-only diagnostic:', phase, mode)
