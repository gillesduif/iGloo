"""Click only the disposable guest's credential dialog over its local VNC port."""

import socket
import struct
import sys
import time


if sys.argv[1:] not in (['upper'], ['lower']):
    raise RuntimeError('Expected upper or lower credential dialog')
point = (670, 240) if sys.argv[1] == 'upper' else (670, 550)


def exact(sock, count):
    data = bytearray()
    while len(data) < count:
        chunk = sock.recv(count - len(data))
        if not chunk:
            raise RuntimeError('VNC connection closed')
        data.extend(chunk)
    return bytes(data)


with socket.create_connection(('127.0.0.1', 5906), timeout=5) as client:
    client.settimeout(5)
    protocol = exact(client, 12)
    if protocol not in (b'RFB 003.008\n', b'RFB 003.007\n'):
        raise RuntimeError('Unexpected RFB version')
    client.sendall(protocol)
    count = exact(client, 1)[0]
    if count == 0:
        raise RuntimeError('No unauthenticated VNC mode')
    modes = exact(client, count)
    if 1 not in modes:
        raise RuntimeError('VNC requires unsupported authentication')
    client.sendall(b'\x01')
    if exact(client, 4) != b'\x00' * 4:
        raise RuntimeError('VNC authentication failed')
    client.sendall(b'\x01')
    header = exact(client, 24)
    width, height = struct.unpack('>HH', header[:4])
    name_size = struct.unpack('>I', header[20:24])[0]
    if (width, height) != (1280, 800) or name_size > 1024:
        raise RuntimeError('Unexpected disposable display')
    exact(client, name_size)
    x, y = point
    client.sendall(struct.pack('>BBHH', 5, 0, x, y))
    client.sendall(struct.pack('>BBHH', 5, 1, x, y))
    time.sleep(.12)
    client.sendall(struct.pack('>BBHH', 5, 0, x, y))
print(sys.argv[1])
