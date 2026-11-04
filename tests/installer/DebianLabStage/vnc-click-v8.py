"""Click only the synthetic account tile in the exact disposable v8 guest."""

from pathlib import Path
import socket
import struct
import sys
import time

from fresh_v8_capacity import _active


LAB = Path('/home/gillesduif/igloo-lab/debian-20261017/fresh-source-v8')
phase = sys.argv[1]
action = sys.argv[2] if len(sys.argv) > 2 else 'account'
if phase not in ('firstboot', 'secondboot') or action not in (
        'account', 'return', 'escape', 'credential-focus', 'credential-submit'):
    raise RuntimeError('Unexpected Debian v8 guest phase')
monitor = LAB / f'qemu-{phase}-v8.monitor'
pid = int((LAB / f'qemu-{phase}-v8.pid').read_text())
if not _active(pid, monitor):
    raise RuntimeError('Exact disposable v8 QEMU process is unavailable')


def receive(sock, size):
    data = b''
    while len(data) < size:
        block = sock.recv(size - len(data))
        if not block:
            raise RuntimeError('VNC handshake ended early')
        data += block
    return data


with socket.create_connection(('127.0.0.1', 5905), timeout=5) as sock:
    version = receive(sock, 12)
    if version != b'RFB 003.008\n':
        raise RuntimeError('Unexpected local QEMU VNC protocol')
    sock.sendall(version)
    choices = receive(sock, receive(sock, 1)[0])
    if 1 not in choices:
        raise RuntimeError('Unexpected QEMU VNC authentication')
    sock.sendall(b'\x01')
    if receive(sock, 4) != b'\x00\x00\x00\x00':
        raise RuntimeError('QEMU VNC authentication failed')
    sock.sendall(b'\x01')
    header = receive(sock, 24)
    width, height = struct.unpack('>HH', header[:4])
    name_size = struct.unpack('>I', header[20:24])[0]
    receive(sock, name_size)
    if (width, height) != (1280, 800):
        raise RuntimeError('Unexpected disposable guest display geometry')
    if action in ('account', 'credential-focus'):
        x, y = (640, 350) if action == 'account' else (675, 400)
        sock.sendall(struct.pack('>BBHH', 5, 1, x, y))
        time.sleep(.2)
        sock.sendall(struct.pack('>BBHH', 5, 0, x, y))
        time.sleep(.2)
    if action == 'credential-submit':
        for char in 'igloolab123':
            keysym = ord(char)
            sock.sendall(struct.pack('>BBHI', 4, 1, 0, keysym))
            time.sleep(.06)
            sock.sendall(struct.pack('>BBHI', 4, 0, 0, keysym))
            time.sleep(.06)
    if action in ('account', 'return', 'escape', 'credential-submit'):
        keysym = 0xff1b if action == 'escape' else 0xff0d
        sock.sendall(struct.pack('>BBHI', 4, 1, 0, keysym))
        time.sleep(.2)
        sock.sendall(struct.pack('>BBHI', 4, 0, 0, keysym))
print('sent exact disposable guest VNC action', phase, action)
