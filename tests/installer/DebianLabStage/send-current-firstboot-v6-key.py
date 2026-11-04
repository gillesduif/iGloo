"""Send only bounded lab GUI/TTY keys to the exact disposable QEMU guest."""

from pathlib import Path
import socket
import sys
import time


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/current-firstboot-v6-child')
SECOND = (LAB / 'qemu-second.pid').is_file()
PID = LAB / ('qemu-second.pid' if SECOND else 'qemu.pid')
MONITOR = LAB / ('qemu-second.monitor' if SECOND else 'qemu.monitor')
mode = sys.argv[1] if len(sys.argv) == 2 else ''
if mode not in ('select-user', 'login-password', 'credential-password',
                'dismiss-tour', 'tty', 'login-tty', 'service', 'wifi', 'store', 'welcome', 'package', 'shutdown', 'status', 'poweroff', 'retry-credential', 'escape', 'mouse-help', 'mouse-to-dialog', 'select-ps2-mouse', 'close-overview'):
    raise RuntimeError('Unsupported lab key action')
if not PID.is_file():
    raise RuntimeError('The exact guest is not active')


def text(monitor, value):
    keys = {' ': 'spc', '-': 'minus', '.': 'dot', '/': 'slash', '=': 'equal'}
    for char in value:
        key = keys.get(char, char)
        if not (key.isalnum() or key in keys.values()):
            raise RuntimeError('Unsupported guest key')
        monitor.sendall(f'sendkey {key} 120\n'.encode())
        time.sleep(.15)
    monitor.sendall(b'sendkey ret\n')
    time.sleep(.5)


with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as monitor:
    monitor.settimeout(5)
    monitor.connect(str(MONITOR))
    monitor.recv(4096)
    if mode == 'select-user':
        monitor.sendall(b'sendkey ret 500\n')
        time.sleep(.8)
    elif mode in ('login-password', 'credential-password'):
        text(monitor, 'igloolab123')
    elif mode == 'dismiss-tour':
        monitor.sendall(b'sendkey ret\n')
        time.sleep(.8)
    elif mode == 'tty':
        monitor.sendall(b'sendkey ctrl-alt-f3\n')
        time.sleep(1)
        text(monitor, 'igloolab')
        time.sleep(2)
        text(monitor, 'igloolab123')
    elif mode == 'login-tty':
        text(monitor, 'igloolab')
        time.sleep(2)
        text(monitor, 'igloolab123')
    elif mode == 'service':
        text(monitor, 'systemctl is-active igloo-bootstrap.service')
    elif mode == 'wifi':
        text(monitor, 'nmcli connection show')
    elif mode == 'store':
        text(monitor, 'ls -l /home/igloolab/.local/share/igloo')
    elif mode == 'welcome':
        text(monitor, 'ls -la /home/igloolab/.igloo-welcome-done')
    elif mode == 'package':
        text(monitor, 'dpkg-query -W tree')
    elif mode == 'shutdown':
        monitor.sendall(b'system_powerdown\n')
    elif mode == 'status':
        monitor.sendall(b'info status\n')
        time.sleep(.2)
        print(monitor.recv(4096).decode(errors='replace'))
    elif mode == 'poweroff':
        text(monitor, 'sudo poweroff')
        text(monitor, 'igloolab123')
    elif mode == 'escape':
        monitor.sendall(b'sendkey esc\n')
    elif mode == 'retry-credential':
        monitor.sendall(b'sendkey esc\n')
        time.sleep(.5)
        monitor.sendall(b'sendkey alt-f2\n')
        time.sleep(.5)
        text(monitor, '/opt/igloo/credential-import.sh')
    elif mode == 'mouse-help':
        monitor.sendall(b'info mice\n')
        time.sleep(.2)
        print(monitor.recv(4096).decode(errors='replace'))
    elif mode == 'mouse-to-dialog':
        monitor.sendall(b'mouse_move -535 -520\n')
        time.sleep(.2)
        monitor.sendall(b'mouse_button 1\n')
        time.sleep(.2)
        monitor.sendall(b'mouse_button 0\n')
    elif mode == 'select-ps2-mouse':
        monitor.sendall(b'mouse_set 2\n')
    elif mode == 'close-overview':
        monitor.sendall(b'sendkey esc\n')
        time.sleep(.3)
        monitor.sendall(b'sendkey esc\n')
print(mode)
