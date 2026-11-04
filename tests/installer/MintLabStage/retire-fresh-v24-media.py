#!/usr/bin/env python3
"""Retire only the fully read back disposable Mint v24 media."""

import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
TARGET = LAB / "mint-target-fresh-v24.raw"
DECOY = LAB / "mint-decoy-fresh-v24.raw"
SEED = LAB / "mint-oemdrv-fresh-v24.qcow2"
PROTECTED_PARENT = LAB / "mint-oemdrv-final-v20.raw"
COMPACT = LAB / "mint-fresh-v24-compact"
PARENT_SHA256 = "ac731edc31f52d8afab70d3e83279af054672cd44f89db4ef22bb746d538e860"
DECOY_SHA256 = "f8f5d3eb82c578f1a7990b53bc1392d46df45391b4c3ed68ca3db017d0ca0e71"
REQUIRED = (
    LAB / "mint-fresh-v24-postinstall-readback.txt",
    LAB / "mint-fresh-v24-firstboot-readback.txt",
    LAB / "mint-fresh-v24-seedfree-readback.txt",
    LAB / "mint-fresh-v24-seed-identity.json",
    LAB / "mint-fresh-v24-installer-serial.log",
    LAB / "mint-fresh-v24-firstboot-serial.log",
    LAB / "mint-fresh-v24-seedfree-serial.log",
    LAB / "mint-fresh-v24-first-login-1.png",
    LAB / "mint-fresh-v24-first-login-3.png",
    LAB / "mint-fresh-v24-seedfree-desktop.png",
    LAB / "mint-fresh-v24-fat-preinstaller.txt",
    LAB / "mint-fresh-v24-fat-postinstaller.txt",
    LAB / "mint-fresh-v24-fat-postfirstboot.txt",
    LAB / "mint-fresh-v24-fat-postseedfree.txt",
    LAB / "mint-fresh-v24-installer-fat-mounts.txt",
    LAB / "mint-capacity-fresh-v24-installer-kvm-retry.jsonl",
    LAB / "mint-capacity-fresh-v24-firstboot.jsonl",
    LAB / "mint-capacity-fresh-v24-seedfree.jsonl",
    COMPACT / "agent.log",
    COMPACT / "credential-login.log",
    COMPACT / "fstab",
    COMPACT / "installed-manifest-redacted.json",
    COMPACT / "seed-manifest-redacted.json",
    COMPACT / "seed-preseed-redacted.cfg",
    COMPACT / "seed-identity.json",
)


def free_bytes(path: str) -> int:
    info = os.statvfs(path)
    return info.f_bavail * info.f_frsize


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def qemu_info(path: Path) -> dict:
    return json.loads(subprocess.check_output(
        ["qemu-img", "info", "--output=json", str(path)], text=True
    ))


def ensure_idle() -> None:
    for process in Path("/proc").iterdir():
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
    assert not any(path.exists() for path in Path("/sys/block").glob("nbd*/pid")), "NBD still attached"
    for mount in (
        LAB / "mint-root-fresh-v24-ro", LAB / "mint-seed-fresh-v24-ro",
        LAB / "mint-esp-fresh-v24-ro",
    ):
        assert subprocess.run(["mountpoint", "-q", str(mount)], check=False).returncode != 0


def audit() -> dict:
    ensure_idle()
    assert LAB.resolve(strict=True) == LAB
    assert COMPACT.is_dir() and not COMPACT.is_symlink()
    for artifact in REQUIRED:
        assert artifact.is_file() and not artifact.is_symlink(), f"Missing readback: {artifact}"
    for path in (TARGET, DECOY, SEED):
        assert path.parent == LAB and path.resolve(strict=True) == path and not path.is_symlink()
        metadata = path.stat()
        assert stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1
        assert metadata.st_uid == 1000
    assert TARGET.stat().st_size == 40 * 1024**3
    assert DECOY.stat().st_size == 40 * 1024**3
    assert DECOY.stat().st_blocks * 512 == 40960
    assert SEED.stat().st_size < 1024**3
    assert PROTECTED_PARENT.is_file() and not PROTECTED_PARENT.is_symlink()
    assert sha256(PROTECTED_PARENT) == PARENT_SHA256
    assert qemu_info(SEED)["full-backing-filename"] == str(PROTECTED_PARENT)
    target_table = subprocess.check_output(["sgdisk", "-p", str(TARGET)], text=True)
    decoy_table = subprocess.check_output(["sgdisk", "-p", str(DECOY)], text=True)
    assert "3E264084-C958-41CB-AF82-3D247E3FC75D" in target_table
    assert "   4        11536384        83884031" in target_table
    assert "490A4E9E-81DB-4620-87E3-230641BAF5DD" in decoy_table
    assert "Number  Start (sector)" in decoy_table and "   1            " not in decoy_table
    post = (LAB / "mint-fresh-v24-postinstall-readback.txt").read_text()
    first = (LAB / "mint-fresh-v24-firstboot-readback.txt").read_text()
    second = (LAB / "mint-fresh-v24-seedfree-readback.txt").read_text()
    assert "p1 3b7de0e4e0e6309809daaf9c9c0540391299be7b8d34370e1fa6244fea113526" in post
    assert "Mint v24: exact firstboot migration" in first and "Imported 1" in (COMPACT / "credential-login.log").read_text()
    assert "decoy " + DECOY_SHA256 in second
    assert "Mint v24: exact firstboot migration" in second

    qcow2_images = sorted(path for path in Path("/home/gillesduif").rglob("*.qcow2") if path.is_file())
    for image in qcow2_images:
        info = qemu_info(image)
        for key in ("backing-filename", "full-backing-filename"):
            backing = info.get(key)
            if backing:
                resolved = Path(backing)
                if not resolved.is_absolute():
                    resolved = image.parent / resolved
                assert resolved.resolve() not in (TARGET, DECOY, SEED), f"Live descendant: {image}"
    return {
        "target": str(TARGET), "targetAllocatedBytes": TARGET.stat().st_blocks * 512,
        "decoy": str(DECOY), "decoyAllocatedBytes": DECOY.stat().st_blocks * 512,
        "seedChild": str(SEED), "seedChildAllocatedBytes": SEED.stat().st_blocks * 512,
        "protectedSeedParent": str(PROTECTED_PARENT), "protectedSeedParentSha256": PARENT_SHA256,
        "qcow2HeadersChecked": len(qcow2_images), "retainedReadbacks": len(REQUIRED),
        "hostFreeBeforeBytes": free_bytes("/mnt/c"), "wslFreeBeforeBytes": free_bytes(str(LAB)),
    }


def write_exclusive(path: Path, value: dict) -> None:
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


if __name__ == "__main__":
    if sys.argv[1:] not in ([], ["--retire"]):
        raise SystemExit("usage: retire-fresh-v24-media.py [--retire]")
    report = audit()
    if sys.argv[1:] == ["--retire"]:
        write_exclusive(COMPACT / "retirement-plan.json", report)
        for path in (SEED, DECOY, TARGET):
            path.unlink()
        os.sync()
        assert not TARGET.exists() and not DECOY.exists() and not SEED.exists()
        report["retired"] = True
        report["hostFreeAfterBytes"] = free_bytes("/mnt/c")
        report["wslFreeAfterBytes"] = free_bytes(str(LAB))
        write_exclusive(COMPACT / "retirement.json", report)
    print(json.dumps(report, indent=2))
