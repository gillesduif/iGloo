#!/usr/bin/env python3
"""Type a synthetic Mint lab password, then click the observed Zenity OK.

Use `type` only with the credential dialog focused. Inspect a fresh guest
screenshot before invoking `click-ok`: it must show Zenity's OK at (810,445).
The two commands are deliberately separate so no blind click can submit data.
"""

import ast
from pathlib import Path
import socket
import struct
import sys
import time


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
OUT = LAB / "mint-postlogin-v32"
CHILD = OUT / "target-child.qcow2"
MONITOR = OUT / "qemu-monitor.sock"
HELPER = LAB / "mint-hmp-type-credential-v22.py"


def require_exact_guest() -> None:
    processes = []
    for item in Path("/proc").iterdir():
        if not item.name.isdigit():
            continue
        try:
            args = (item / "cmdline").read_bytes().split(b"\0")
        except OSError:
            continue
        if args and Path(args[0].decode(errors="ignore")).name == "qemu-system-x86_64":
            processes.append(args)
    expected = str(CHILD).encode()
    if (len(processes) != 1 or not any(expected in arg for arg in processes[0])
            or not MONITOR.is_socket()):
        raise RuntimeError("The exact Mint v32 QEMU guest is not the only running VM")


def synthetic_password() -> str:
    tree = ast.parse(HELPER.read_text(encoding="utf-8"))
    values = [node.value.value for node in tree.body if isinstance(node, ast.Assign)
              and any(isinstance(target, ast.Name) and target.id == "synthetic_password"
                      for target in node.targets)
              and isinstance(node.value, ast.Constant)
              and isinstance(node.value.value, str)]
    if len(values) != 1 or not values[0].isascii() or not values[0].isalnum():
        raise RuntimeError("Synthetic lab credential helper changed")
    return values[0]


def type_password() -> None:
    with socket.socket(socket.AF_UNIX) as sock:
        sock.settimeout(10)
        sock.connect(str(MONITOR))
        sock.recv(4096)
        for letter in synthetic_password():
            sock.sendall(f"sendkey {letter}\n".encode("ascii"))
            time.sleep(0.15)
    print("Synthetic password typed without Enter; inspect the dialog before click-ok")


def read_exact(sock: socket.socket, length: int) -> bytes:
    data = bytearray()
    while len(data) < length:
        block = sock.recv(length - len(data))
        if not block:
            raise RuntimeError("VNC closed during handshake")
        data.extend(block)
    return bytes(data)


def click_ok() -> None:
    # QEMU VNC is bound to WSL loopback only by the reviewed Mint runner.
    with socket.create_connection(("127.0.0.1", 5909), timeout=5) as sock:
        sock.settimeout(5)
        version = read_exact(sock, 12)
        if version not in (b"RFB 003.008\n", b"RFB 003.007\n"):
            raise RuntimeError("Unexpected QEMU VNC protocol")
        sock.sendall(version)
        count = read_exact(sock, 1)[0]
        if not count or 1 not in read_exact(sock, count):
            raise RuntimeError("QEMU VNC does not offer unauthenticated local access")
        sock.sendall(b"\x01")
        if struct.unpack(">I", read_exact(sock, 4))[0] != 0:
            raise RuntimeError("QEMU VNC handshake rejected")
        sock.sendall(b"\x01")
        init = read_exact(sock, 24)
        width, height = struct.unpack(">HH", init[:4])
        name_length = struct.unpack(">I", init[20:24])[0]
        if name_length > 4096:
            raise RuntimeError("Unexpected QEMU VNC server name")
        read_exact(sock, name_length)
        if (width, height) != (1280, 800):
            raise RuntimeError(f"Zenity OK location not reviewed at {width}x{height}")
        sock.sendall(struct.pack(">BBHH", 5, 1, 810, 445))
        time.sleep(0.12)
        sock.sendall(struct.pack(">BBHH", 5, 0, 810, 445))
    print("Clicked the observed Zenity OK at (810,445); inspect guest log and receipt")


def main() -> None:
    if sys.argv[1:] not in (["type"], ["click-ok"]):
        raise RuntimeError("Use type or click-ok after visually inspecting the prompt")
    require_exact_guest()
    if sys.argv[1] == "type":
        type_password()
    else:
        click_ok()


if __name__ == "__main__":
    main()
