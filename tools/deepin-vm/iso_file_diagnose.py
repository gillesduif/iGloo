"""Inspect a failed iso_file_probe fixture read-only, optionally adding NTFS support.

No formatting, deployment, host devices, optical media or network are attached.
"""
import argparse
import hashlib
import json
from pathlib import Path
import socket
import subprocess
import time
import uuid

import probe


def ntfs_initrd(fixture: Path, destination: Path):
    """Test-only archive of two ISO-extracted binaries; no host executables included."""
    source = fixture / "ntfs-support"
    executable = (source / "usr/bin/ntfs-3g").read_bytes()
    library = (source / "usr/lib/x86_64-linux-gnu/libntfs-3g.so.89.0.0").read_bytes()
    archive = bytearray()

    def entry(name, data, mode):
        name = name.encode() + b"\0"
        fields = [0, mode, 0, 0, 1, 0, len(data), 0, 0, 0, 0, len(name), 0]
        archive.extend(b"070701" + "".join(f"{value:08x}" for value in fields).encode() + name)
        archive.extend(b"\0" * (-len(archive) % 4))
        archive.extend(data)
        archive.extend(b"\0" * (-len(archive) % 4))

    for directory in ("usr", "usr/bin", "usr/sbin", "usr/lib", "usr/lib/x86_64-linux-gnu"):
        entry(directory, b"", 0o40755)
    for name in ("usr/bin/ntfs-3g", "usr/sbin/mount.ntfs", "usr/sbin/mount.ntfs-3g"):
        entry(name, executable, 0o100755)
    entry("usr/lib/x86_64-linux-gnu/libntfs-3g.so.89", library, 0o100644)
    entry("TRAILER!!!", b"", 0)
    original = (fixture / "initrd-6.6").read_bytes()
    position = 0
    while position < len(original):
        while position < len(original) and original[position] == 0:
            position += 1
        if original[position:position + 4] == b"\x02\x21\x4c\x18":
            break
        header = original[position:position + 110]
        if len(header) != 110 or header[:6] != b"070701":
            raise ValueError("Unexpected initrd prefix; refusing to guess compression boundary")
        fields = [int(header[i:i + 8], 16) for i in range(6, 110, 8)]
        size, name_size = fields[6], fields[11]
        if name_size < 1 or name_size > 4096:
            raise ValueError("Invalid prefix archive filename")
        data_start = (position + 110 + name_size + 3) & ~3
        position = (data_start + size + 3) & ~3
        if position > len(original):
            raise ValueError("Truncated prefix archive")
    else:
        raise ValueError("Missing expected legacy-LZ4 payload")
    # Legacy LZ4 extends to EOF: appending gzip makes its decoder reject the tail.
    # Retain the early microcode archive and every byte of the compressed payload.
    with destination.open("xb") as output:
        output.write(original[:position])
        output.write(archive)
        output.write(original[position:])
    (destination.parent / "ntfs-support-hashes.json").write_text(json.dumps({
        "ntfs-3g": hashlib.sha256(executable).hexdigest(),
        "libntfs-3g.so.89": hashlib.sha256(library).hexdigest(),
    }, indent=2))


def main(fixture: Path, with_ntfs: bool = False, inspect: bool = False):
    fixture = fixture.resolve(strict=True)
    if any(c in str(fixture) for c in (",", "\n", "\r")):
        raise ValueError("Unsupported fixture path")
    image = fixture / "media.img"
    if not image.is_file() or image.is_symlink():
        raise ValueError("Expected an ordinary fixture image")
    for name in ("vmlinuz-6.6", "initrd-6.6", "media-layout.json"):
        if not (fixture / name).is_file():
            raise ValueError("Missing iso_file_probe artifact")
    output = fixture / ("ntfs-prefix-initramfs-inspection" if with_ntfs and inspect else
                        "ntfs-prefix-live-check" if with_ntfs else "initramfs-inspection")
    output.mkdir(exist_ok=False)
    args = probe.qemu_arguments(image, output)
    del args[args.index("-blockdev"):]
    args[args.index("-kernel") + 1] = str(fixture / "vmlinuz-6.6")
    args[args.index("-initrd") + 1] = str(fixture / "initrd-6.6")
    args[args.index("-append") + 1] = "boot=live union=overlay break=premount console=ttyS0,115200"
    args[args.index("-m") + 1] = "1024"
    if with_ntfs:
        ntfs_initrd(fixture, output / "initrd-ntfs")
        args[args.index("-initrd") + 1] = str(output / "initrd-ntfs")
        args[args.index("-append") + 1] = (
            "boot=live union=overlay console=ttyS0,115200 init=/bin/bash findiso=/deepin.iso "
            "live-media=/dev/disk/by-partuuid/" +
            str(uuid.UUID(json.loads((fixture / "media-layout.json").read_text())["partitions"][0]["partitionGuid"])))
        args[args.index("-m") + 1] = "4096"
        if inspect:
            args[args.index("-append") + 1] += " break=premount"
            args[args.index("-m") + 1] = "1024"
    args += ["-blockdev", json.dumps({"driver": "raw", "node-name": "media", "read-only": True,
             "file": {"driver": "file", "filename": str(image), "cache": {"direct": True}}}),
             "-device", "virtio-blk-pci,drive=media"]
    with (output / "qemu.log").open("wb") as log:
        process = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 15
            while not (output / "serial.sock").exists():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError("VM failed to start")
                time.sleep(0.1)
            with socket.socket(socket.AF_UNIX) as connection, (output / "console.log").open("wb") as console_log:
                connection.connect(str(output / "serial.sock"))
                connection.settimeout(1)
                console = probe.Console(connection, console_log)
                console.until(b":/# " if with_ntfs and not inspect else b"(initramfs)", timeout=180)
                console.command("stty -echo")
                command = "command -v ntfs-3g; command -v mount.ntfs; cat /proc/filesystems; blkid; ls /dev/disk/by-partuuid; ls /sbin/*ntfs* /bin/*ntfs* /usr/bin/*ntfs* /usr/sbin/*ntfs*; cat /conf/modules"
                if with_ntfs and not inspect:
                    command = "cat /proc/cmdline; findmnt -rn -o SOURCE,TARGET,FSTYPE,OPTIONS; losetup --list --output NAME,BACK-FILE,RO"
                elif with_ntfs:
                    command += "; /usr/bin/ntfs-3g --version; mkdir -p /run/ntfs-probe; mount -t ntfs -o ro /dev/vda1 /run/ntfs-probe; ls -l /run/ntfs-probe; cat /boot.log"
                result = console.command(command)
                (output / "inspection.log").write_bytes(result)
                print(result.decode(errors="replace"), flush=True)
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
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--with-ntfs", action="store_true")
    parser.add_argument("--inspect", action="store_true")
    options = parser.parse_args()
    main(options.fixture, options.with_ntfs, options.inspect)
