"""Read-only guest boot of the pinned ISO inside a new ordinary NTFS/GPT image.

This probes live-boot only, not Windows preparation, EFI handoff or deployment.
No existing disk image is accepted and no host block device is attached.
"""
import argparse
import copy
import importlib.util
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import uuid

import probe


def main(iso: Path, output: Path):
    iso = probe.verify_iso(iso)
    output = output.resolve()
    if any(c in str(output) for c in (",", "\n", "\r")):
        raise ValueError("Unsupported output path")
    output.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[2]
    spec = importlib.util.spec_from_file_location("fixture", repo / "tests/installer/target_identity_test.py")
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    manifest = copy.deepcopy(fixture.FIXTURE)
    disk = manifest["installationTarget"]["disk"]
    disk["diskGuid"] = str(uuid.uuid4())
    disk["diskSizeBytes"] = 16 << 30
    disk["logicalSectorSize"] = 512
    part = {"partitionGuid": str(uuid.uuid4()),
            "gptType": "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
            "offsetBytes": 1 << 20, "lengthBytes": 14 << 30}
    disk["partitions"] = [part]
    image = output / "media.img"
    fixture.make_gpt(image, manifest, slots=(1,))
    volume = output / "ntfs.img"
    with volume.open("xb") as stream:
        stream.truncate(part["lengthBytes"])
    with (output / "preparation.log").open("wb") as log:
        # -F permits mkntfs on this newly created regular file; never a device.
        subprocess.run(["mkntfs", "-F", "-Q", "-s", "512", str(volume)],
                       check=True, stdout=log, stderr=subprocess.STDOUT)
        subprocess.run(["ntfscp", str(volume), str(iso), "/deepin.iso"],
                       check=True, stdout=log, stderr=subprocess.STDOUT)
        subprocess.run(["xorriso", "-osirrox", "on", "-indev", str(iso),
                        "-extract", "/live/vmlinuz-6.6", str(output / "vmlinuz-6.6"),
                        "-extract", "/live/initrd-6.6", str(output / "initrd-6.6")],
                       check=True, stdout=log, stderr=subprocess.STDOUT)
    # Preserve sparse holes while copying the fresh filesystem into its GPT extent.
    with volume.open("rb") as source, image.open("r+b") as target:
        position = 0
        while position < part["lengthBytes"]:
            try:
                start = os.lseek(source.fileno(), position, os.SEEK_DATA)
            except OSError as ex:
                import errno
                if ex.errno == errno.ENXIO:
                    break
                raise
            end = os.lseek(source.fileno(), start, os.SEEK_HOLE)
            source.seek(start)
            target.seek(part["offsetBytes"] + start)
            while source.tell() < end:
                data = source.read(min(4 << 20, end - source.tell()))
                if not data:
                    raise RuntimeError("Truncated fresh NTFS fixture")
                target.write(data)
            position = end
        target.flush()
        os.fsync(target.fileno())
    args = probe.qemu_arguments(iso, output)
    # Remove the optical ISO entirely. The guest sees only the read-only NTFS image.
    del args[args.index("-blockdev"):]
    args[args.index("-append") + 1] += (
        " findiso=/deepin.iso live-media=/dev/disk/by-partuuid/" + part["partitionGuid"])
    args += ["-blockdev", json.dumps({"driver": "raw", "node-name": "media", "read-only": True,
             "file": {"driver": "file", "filename": str(image), "cache": {"direct": True}}}),
             "-device", "virtio-blk-pci,drive=media"]
    (output / "qemu-arguments.json").write_text(json.dumps(args, indent=2))
    (output / "media-layout.json").write_text(json.dumps(disk, indent=2))
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
                console.until(b":/# ", timeout=180)
                console.command("stty -echo; export TERM=dumb")
                result = console.command("cat /proc/cmdline; findmnt -rn -o SOURCE,TARGET,FSTYPE,OPTIONS; losetup --list --output NAME,BACK-FILE,RO")
                (output / "live-mounts.log").write_bytes(result)
                print("IGLOO_ISO_FILE_LIVE_SHELL_REACHED", flush=True)
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
    options = parser.parse_args()
    main(options.iso, options.output)
