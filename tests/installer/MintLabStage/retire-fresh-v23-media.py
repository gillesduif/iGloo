#!/usr/bin/env python3
"""Retire only the fully read back, task-created Mint v23 target and seed child."""

import json
import os
import pathlib
import stat
import subprocess
import sys


LAB = pathlib.Path("/home/gillesduif/igloo-mint-22.3-lab")
TARGET = LAB / "mint-target-fresh-v23.raw"
SEED = LAB / "mint-oemdrv-fresh-v23.qcow2"
PROTECTED_SEED_PARENT = LAB / "mint-oemdrv-final-v20.raw"
DECOY = LAB / "mint-decoy-fresh-v23.raw"
COMPACT = LAB / "mint-fresh-v23-compact"
REQUIRED = (
    LAB / "mint-fresh-v23-postinstall-readback.txt",
    LAB / "mint-fresh-v23-firstboot-readback.txt",
    LAB / "mint-fresh-v23-seedfree-readback.txt",
    LAB / "mint-fresh-v23-seed-identity.json",
    LAB / "mint-fresh-v23-installer-serial.log",
    LAB / "mint-fresh-v23-firstboot-serial.log",
    LAB / "mint-fresh-v23-seedfree-serial.log",
    LAB / "mint-fresh-v23-seedfree-credential-prompt-2.png",
    COMPACT / "agent.log",
    COMPACT / "credential-login.log",
    COMPACT / "seed-fat-postseedfree.txt",
    COMPACT / "seed-manifest-redacted.json",
    COMPACT / "seed-preseed-redacted.cfg",
)


def free_bytes(path: str) -> int:
    info = os.statvfs(path)
    return info.f_bavail * info.f_frsize


def qemu_info(path: pathlib.Path) -> dict:
    return json.loads(subprocess.check_output(
        ["qemu-img", "info", "--output=json", str(path)], text=True
    ))


def ensure_idle() -> None:
    for process in pathlib.Path("/proc").iterdir():
        if not process.name.isdigit():
            continue
        try:
            argv0 = (process / "cmdline").read_bytes().split(b"\0", 1)[0]
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            continue
        assert os.path.basename(os.fsdecode(argv0)) not in (
            "qemu-system-x86_64", "qemu-nbd"
        ), "QEMU or NBD is still active"
    assert not subprocess.check_output(["losetup", "-a"]), "Loop device still attached"
    assert not any(path.exists() for path in pathlib.Path("/sys/block").glob("nbd*/pid")), "NBD still attached"
    for mount in (LAB / "mint-root-fresh-v23-ro", LAB / "mint-seed-fresh-v23-ro"):
        assert subprocess.run(["mountpoint", "-q", str(mount)], check=False).returncode != 0


def audit() -> dict:
    ensure_idle()
    for artifact in REQUIRED:
        assert artifact.is_file(), f"Missing retained readback: {artifact}"
    assert DECOY.is_file() and DECOY.stat().st_blocks * 512 == 40960
    for path in (TARGET, SEED):
        assert path.parent == LAB and path.is_file() and not path.is_symlink()
        metadata = path.stat()
        assert stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1
        assert metadata.st_uid == 1000
    assert TARGET.stat().st_size == 40 * 1024**3
    assert qemu_info(SEED)["backing-filename"] == str(PROTECTED_SEED_PARENT)
    assert PROTECTED_SEED_PARENT.is_file()
    table = subprocess.check_output(["sgdisk", "-p", str(TARGET)], text=True)
    assert "4331A9F3-AF31-4ED8-9BFF-07C5400A42A4" in table
    assert "   4        11536384        83884031" in table
    post = (LAB / "mint-fresh-v23-postinstall-readback.txt").read_text()
    first = (LAB / "mint-fresh-v23-firstboot-readback.txt").read_text()
    second = (LAB / "mint-fresh-v23-seedfree-readback.txt").read_text()
    assert "p1 d780b7d5c96ea910f19f1d64a7c1a62fd68687a8b06637d04bc70852c297a0ed" in post
    assert "Fresh Mint v23 firstboot: durable done" in first
    assert "Mint v23 seed-free GUI: one exact Chromium login" in second
    assert "40960" in second and "9025BBB3-4A9A-452C-B4F6-B4959B51CDCA" in second

    qcow2_images = sorted(path for path in pathlib.Path("/home/gillesduif").rglob("*.qcow2") if path.is_file())
    for image in qcow2_images:
        info = qemu_info(image)
        for key in ("backing-filename", "full-backing-filename"):
            backing = info.get(key)
            if backing:
                resolved = pathlib.Path(backing)
                if not resolved.is_absolute():
                    resolved = image.parent / resolved
                assert resolved.resolve() not in (TARGET, SEED), f"Live descendant: {image}"
    return {
        "target": str(TARGET),
        "targetAllocatedBytes": TARGET.stat().st_blocks * 512,
        "seed": str(SEED),
        "seedAllocatedBytes": SEED.stat().st_blocks * 512,
        "qcow2HeadersChecked": len(qcow2_images),
        "retainedReadbacks": len(REQUIRED),
        "hostFreeBeforeBytes": free_bytes("/mnt/c"),
        "wslFreeBeforeBytes": free_bytes(str(LAB)),
    }


if __name__ == "__main__":
    if sys.argv[1:] not in ([], ["--retire"]):
        raise SystemExit("usage: retire-fresh-v23-media.py [--retire]")
    report = audit()
    if sys.argv[1:] == ["--retire"]:
        TARGET.unlink()
        SEED.unlink()
        assert not TARGET.exists() and not SEED.exists()
        report["retired"] = True
        report["hostFreeAfterBytes"] = free_bytes("/mnt/c")
        report["wslFreeAfterBytes"] = free_bytes(str(LAB))
        output = COMPACT / "retirement.json"
        descriptor = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
    print(json.dumps(report, indent=2))
