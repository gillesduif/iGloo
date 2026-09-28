"""Serial-only lab control; no shared folder, NIC or host service socket in guest."""
import base64
import re
import shlex
import socket
import sys
import time
import threading
import uuid
import zlib
from pathlib import Path

socket_path, source_path = sys.argv[1:]
source = base64.b64encode(zlib.compress(Path(source_path).read_bytes()))
nonce = uuid.uuid4().hex
wrapper = "import base64,zlib,traceback;print('BEGIN" + nonce + "',flush=True)\ntry:\n exec(zlib.decompress(base64.b64decode(" + "\n".join(repr(source[i:i+512]) for i in range(0, len(source), 512)) + ")))\nexcept Exception:\n traceback.print_exc()\nfinally:\n print('END" + nonce + "',flush=True)"
sock = socket.socket(socket.AF_UNIX)
sock.connect(socket_path)
sock.settimeout(1)
payload = ("python3 -c " + shlex.quote(wrapper) + "\n").encode()
def send():
    # Drain console concurrently; readline emits continuation prompts even with echo off.
    for offset in range(0, len(payload), 512):
        sock.sendall(payload[offset:offset+512])
        time.sleep(0.01)
sender = threading.Thread(target=send, daemon=True)
sender.start()
data = b""
deadline = time.monotonic() + 7200
while time.monotonic() < deadline:
    try:
        data += sock.recv(65536)
    except TimeoutError:
        continue
    match = re.search(rb"(?:\r|\n)BEGIN" + nonce.encode() + rb"\r?\n(.*?)END" + nonce.encode() + rb"\r?\n", data, re.S)
    if match:
        print(match[1].decode(errors='replace'))
        sys.exit(1 if b'Traceback (most recent call last)' in match[1] else 0)
raise TimeoutError(data[-4000:])
