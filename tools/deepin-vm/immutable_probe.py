"""Test immutable extraction/mounting only in a newly created disposable VM disk.

No existing target-image argument exists. The only writable VM storage is a
fresh sparse regular file created inside a new output directory. No host disk,
network, shared folder or existing user VM is attached. This is not an installer.
"""

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import socket
import subprocess
import time
import uuid

import probe


def extract_image_range(image: Path, destination: Path, offset: int, length: int):
    """Read an aligned image range into a new ordinary evidence file."""
    if (type(offset) is not int or type(length) is not int or offset < 0
            or length <= 0 or offset % 512 or length % 512):
        raise ValueError("Expected a positive sector-aligned image range")
    image = image.resolve(strict=True)
    if not image.is_file():
        raise ValueError("Only ordinary fixture image files are permitted")
    with destination.open("xb"):
        pass
    # qemu-img 8.2 dd applies count as an input end position, including skip.
    # Passing only length/512 can silently produce an empty file at high offsets.
    subprocess.run(["qemu-img", "dd", "-f", "qcow2", "-O", "raw", "bs=512",
                    f"skip={offset // 512}", f"count={(offset + length) // 512}",
                    f"if={image}", f"of={destination}"], check=True,
                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if destination.stat().st_size != length:
        raise RuntimeError("Image range extraction returned an unexpected byte count")


def preservation_samples(image: Path, manifest: dict) -> dict[str, str]:
    """Reserved GPT regions and bounded non-root data samples, not a full backup."""
    claim = manifest["installationTarget"]
    disk = claim["disk"]
    mib = 1024 * 1024
    regions = [(0, mib), (disk["diskSizeBytes"] - mib, mib)]
    for part in disk["partitions"]:
        if part["partitionGuid"] == claim["rootPartitionGuid"]:
            continue
        length = min(mib, part["lengthBytes"])
        regions += [(part["offsetBytes"], length),
                    (part["offsetBytes"] + part["lengthBytes"] - length, length)]
    result = {}
    with image.open("rb") as stream:
        for offset, length in regions:
            stream.seek(offset)
            data = stream.read(length)
            if len(data) != length:
                raise RuntimeError("Truncated disposable fixture")
            result[f"{offset}:{length}"] = hashlib.sha256(data).hexdigest()
    return result


def main(iso: Path, output: Path, deploy: bool = False):
    iso = probe.verify_iso(iso)
    output = output.resolve()
    if any(c in str(output) for c in (",", "\n", "\r")):
        raise ValueError("Unsupported output path")
    output.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[2]
    spec = importlib.util.spec_from_file_location("fixture", repo / "tests/installer/target_identity_test.py")
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    manifest = json.loads(json.dumps(fixture.FIXTURE))
    nonce = str(uuid.uuid4())
    manifest["installationId"] = nonce
    claim = manifest["installationTarget"]
    claim["installationId"] = nonce
    claim["disk"]["diskGuid"] = str(uuid.uuid4())
    claim["disk"]["diskSizeBytes"] = 192 * 1024 ** 3
    claim["disk"]["partitions"][3]["lengthBytes"] = 64 * 1024 ** 3
    claim["disk"]["partitions"][4]["offsetBytes"] = 180 * 1024 ** 3
    image = output / "disposable.img"
    fixture.make_gpt(image, manifest)
    image.chmod(0o600)
    before = preservation_samples(image, manifest)
    (output / "fixture-manifest.json").write_text(json.dumps(manifest, indent=2))
    with (output / "extraction.log").open("wb") as log:
        subprocess.run(["xorriso", "-osirrox", "on", "-indev", str(iso),
                        "-extract", "/live/vmlinuz-6.6", str(output / "vmlinuz-6.6"),
                        "-extract", "/live/initrd-6.6", str(output / "initrd-6.6")],
                       check=True, stdout=log, stderr=subprocess.STDOUT)
    args = probe.qemu_arguments(iso, output)
    args += ["-blockdev", json.dumps({"driver": "raw", "node-name": "fixture",
              "file": {"driver": "file", "filename": str(image), "cache": {"direct": True}}}),
             "-device", f"virtio-blk-pci,drive=fixture,serial={nonce[:20]}"]
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
                console.until(b":/# ")
                console.command("stty -echo; export TERM=dumb")
                console.upload((repo / "distros/_shared/installer/igloo_target.py").read_bytes(), "igloo_target.py")
                console.upload(json.dumps(manifest).encode(), "fixture-manifest.json")
                console.upload(Path(__file__).with_name("immutable_guest.py").read_bytes(), "immutable_guest.py")
                suffix = " --deploy" if deploy else ""
                result = console.command(f"unshare --mount --propagation private python3 /run/immutable_guest.py {nonce}{suffix}", timeout=1800)
                (output / "experiment.log").write_bytes(result)
                print(result.decode(errors="replace"))
                if b"\nIGLOO_IMMUTABLE_MOUNT_EXPERIMENT_PASSED\r\n" not in result:
                    raise RuntimeError("Immutable experiment failed; see experiment.log")
                if deploy and b"\nIGLOO_IMMUTABLE_DEPLOY_EXPERIMENT_PASSED\r\n" not in result:
                    raise RuntimeError("Immutable deployment failed; see experiment.log")
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
            after = preservation_samples(image, manifest)
            with image.open("rb") as stream:
                layout = fixture.target.read_gpt(stream, 512, claim["disk"]["diskSizeBytes"])
            unchanged = fixture.target._identity(layout) == fixture.target._identity(claim["disk"])
            (output / "preservation.json").write_text(json.dumps({
                "gptIdentityUnchanged": unchanged, "reservedAndNonRootSamplesUnchanged": before == after,
                "before": before, "after": after}, indent=2))
            if not unchanged or before != after:
                raise RuntimeError("Disposable fixture preservation checks failed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--deploy", action="store_true", help="Also test root-only immutable deployment; no ESP/EFI writes")
    arguments = parser.parse_args()
    main(arguments.iso, arguments.output, arguments.deploy)
