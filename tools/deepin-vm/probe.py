"""Boot the pinned Deepin live environment without a writable disk or network.

This is an investigation harness, not an installer. Run on Linux with QEMU,
xorriso and access to KVM. All guest commands below are read-only; the stock
installer never starts because the live system boots directly into a shell.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import os
from pathlib import Path
import shutil
import socket
import stat
import subprocess
import time


ISO_SHA256 = "f875c9a605bfe6a8425d1d353a3c1ec755bf37f5b0a3231ca19e2145da0ff450"
ISO_SIZE = 6976131072
PROBE = """
echo IGLOO_PACKAGES
dpkg-query -W deepin-installer deepin-immutable-ctl ostree grub-efi-amd64-bin
echo IGLOO_DEPLOY_HELP
deepin-immutable-ctl admin deploy --help
echo IGLOO_MOUNTS
findmnt -rn -o TARGET,SOURCE,FSTYPE
echo IGLOO_BLOCK_DEVICES
lsblk -b -o NAME,TYPE,SIZE,LOG-SEC
echo IGLOO_IDENTITY_COLLECTOR
python3 -c 'import sys; sys.path.insert(0,"/run"); import igloo_target; print(igloo_target.collect_layouts(verified_optical_media={"f875c9a605bfe6a8425d1d353a3c1ec755bf37f5b0a3231ca19e2145da0ff450":6976131072}))'
echo IGLOO_COLLECTOR_EXIT=$?
"""


def verify_iso(path: Path) -> Path:
    path = path.resolve(strict=True)
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or info.st_size != ISO_SIZE:
        raise ValueError("Expected the pinned Deepin ISO as an ordinary file")
    with path.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
        if hasattr(os, "posix_fadvise"):
            os.posix_fadvise(source.fileno(), 0, 0, os.POSIX_FADV_DONTNEED)
    if digest != ISO_SHA256:
        raise ValueError("Deepin ISO checksum mismatch")
    return path


def qemu_arguments(iso: Path, output: Path) -> list[str]:
    # Deliberately no disk argument, shared filesystem, network or GUI installer.
    # JSON block options also avoid commas in filenames becoming QEMU options.
    import json

    media = json.dumps({"driver": "raw", "node-name": "iso", "read-only": True,
                        "file": {"driver": "file", "filename": str(iso),
                                 "cache": {"direct": True}}})
    return ["qemu-system-x86_64", "-enable-kvm", "-machine", "q35", "-nodefaults",
            "-m", "4096", "-smp", "2",
            "-display", "none", "-monitor", "none", "-no-reboot", "-nic", "none",
            "-serial", f"unix:{output}/serial.sock,server=on,wait=off",
            "-kernel", str(output / "vmlinuz-6.6"),
            "-initrd", str(output / "initrd-6.6"),
            "-append", "boot=live union=overlay console=ttyS0,115200 "
            "init=/bin/bash locales=en_US.UTF-8",
            "-blockdev", media, "-device", "ide-cd,drive=iso"]


class Console:
    def __init__(self, connection: socket.socket, log):
        self.connection = connection
        self.log = log
        self.pending = b""

    def until(self, marker: bytes, timeout: int = 90) -> bytes:
        deadline = time.monotonic() + timeout
        while marker not in self.pending:
            if time.monotonic() >= deadline:
                raise TimeoutError(f"VM console did not reach {marker!r}")
            try:
                data = self.connection.recv(65536)
            except socket.timeout:
                continue
            if not data:
                raise RuntimeError("VM console disconnected")
            self.log.write(data)
            self.log.flush()
            self.pending += data
        end = self.pending.index(marker) + len(marker)
        result, self.pending = self.pending[:end], self.pending[end:]
        return result

    def command(self, command: str, timeout: int = 90) -> bytes:
        # Split marker in the command, so terminal echo cannot satisfy it.
        self.connection.sendall((command + "; printf '\\nIGLOO_%s\\n' DONE\n").encode())
        return self.until(b"\nIGLOO_DONE\r\n", timeout)

    def upload(self, content: bytes, name: str) -> None:
        if name not in ("igloo_target.py", "probe.sh", "immutable_guest.py", "fixture-manifest.json"):
            raise ValueError("Only fixed probe files may be uploaded")
        encoded = base64.b64encode(content).decode("ascii")
        self.command(f": > /run/{name}.b64")
        # Keep each terminal input line well below Linux's canonical input limit.
        for start in range(0, len(encoded), 512):
            self.command(f"printf '%s' '{encoded[start:start + 512]}' >> /run/{name}.b64")
        self.command(f"base64 -d /run/{name}.b64 > /run/{name}")


def run(iso: Path, output: Path) -> None:
    if not os.access("/dev/kvm", os.R_OK | os.W_OK):
        raise RuntimeError("KVM access is required for this probe")
    for tool in ("qemu-system-x86_64", "xorriso"):
        if shutil.which(tool) is None:
            raise RuntimeError(f"Missing dependency: {tool}")
    iso = verify_iso(iso)
    output = output.resolve()
    # Never overwrite earlier evidence or accept QEMU socket option delimiters.
    if any(char in str(output) for char in (",", "\n", "\r")):
        raise ValueError("Output path contains unsupported characters")
    output.mkdir(parents=True, exist_ok=False)
    with (output / "extraction.log").open("wb") as log:
        subprocess.run(["xorriso", "-osirrox", "on", "-indev", str(iso),
                        "-extract", "/live/vmlinuz-6.6", str(output / "vmlinuz-6.6"),
                        "-extract", "/live/initrd-6.6", str(output / "initrd-6.6")],
                       check=True, stdout=log, stderr=subprocess.STDOUT)
    with (output / "qemu.log").open("wb") as errors:
        process = subprocess.Popen(qemu_arguments(iso, output), stdin=subprocess.DEVNULL,
                                   stdout=errors, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 15
            while not (output / "serial.sock").exists():
                if process.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError("QEMU failed to open its console; see qemu.log")
                time.sleep(0.1)
            with socket.socket(socket.AF_UNIX) as connection, (output / "console.log").open("wb") as log:
                connection.connect(str(output / "serial.sock"))
                connection.settimeout(1)
                console = Console(connection, log)
                console.until(b":/# ")
                console.command("stty -echo; export TERM=dumb")
                collector = Path(__file__).resolve().parents[2] / "distros/_shared/installer/igloo_target.py"
                console.upload(collector.read_bytes(), "igloo_target.py")
                console.upload(PROBE.encode(), "probe.sh")
                result = console.command("bash /run/probe.sh", timeout=600)
                (output / "probe.log").write_bytes(result)
                print(result.decode(errors="replace"))
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    run(args.iso, args.output)
