#!/usr/bin/env python3
"""Retire only the fully read back disposable Mint v26 target, decoy and seed child."""

import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
TARGET = LAB / "mint-target-fresh-v25.raw"
DECOY = LAB / "mint-decoy-fresh-v25.raw"
SEED = LAB / "mint-oemdrv-fresh-v26.qcow2"
PROTECTED_BACKING = LAB / "mint-oemdrv-final-v20.raw"
PLAN = LAB / "mint-fresh-v26-retirement-plan.json"
RESULT = LAB / "mint-fresh-v26-retirement.json"
EXPECTED_PARENT_SHA256 = "ac731edc31f52d8afab70d3e83279af054672cd44f89db4ef22bb746d538e860"
EXPECTED_SEED_SHA256 = "0790088512d1f1f6bdd668b7d309fdad7b9f0a51b4fc5ce6f71d19a0f2474fd1"
EXPECTED_DECOY_SHA256 = "482ede1b21c3d965a1ef121e576e6d196562fe62a2aa5ebc2721e0cfac9c9718"
EXPECTED_P1_SHA256 = "9eb238e30cc0d2b30da0e3e2f4b155d30f474768d00465eecf8ba7d39258af98"
EXPECTED_P2_SHA256 = "6210d3fb5081c5e597d1ec85664dacef4b0103db0ec69117608aec56fc5f02fe"
REQUIRED_EVIDENCE = (
    "mint-fresh-v26-postinstall-readback.txt",
    "mint-fresh-v26-firstboot-readback.txt",
    "mint-fresh-v26-seedfree-readback.txt",
    "mint-fresh-v26-fat-inherited-repair.txt",
    "mint-fresh-v26-fat-postinstaller.txt",
    "mint-fresh-v26-fat-postfirstboot.txt",
    "mint-fresh-v26-fat-postseedfree.txt",
    "mint-fresh-v26-installer-serial.log",
    "mint-fresh-v26-firstboot-serial.log",
    "mint-fresh-v26-seedfree-serial.log",
    "mint-fresh-v26-credential-prompt.png",
    "mint-fresh-v26-seedfree-desktop.png",
    "mint-fresh-v26-firstboot-systemctl.png",
    "mint-fresh-v26-seed-identity.json",
    "mint-fresh-v26-browser-receipt.json",
    "mint-capacity-fresh-v26-installer.jsonl",
    "mint-capacity-fresh-v26-firstboot-retry.jsonl",
    "mint-capacity-fresh-v26-seedfree.jsonl",
    "mint-capacity-fresh-v26-lowram.jsonl",
    "mint-fresh-v26-lowram-screen-1.png",
    "mint-fresh-v26-decoy-full-sha256.txt",
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


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
        ["qemu-img", "info", "--output=json", str(path)], text=True))


def ensure_detached() -> None:
    for process in Path("/proc").iterdir():
        if not process.name.isdigit():
            continue
        try:
            argv0 = (process / "cmdline").read_bytes().split(b"\0", 1)[0]
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            continue
        require(os.path.basename(os.fsdecode(argv0)) not in (
            "qemu-system-x86_64", "qemu-nbd"), "A QEMU or NBD process is active")
    require(not subprocess.check_output(["losetup", "-a"]), "A loop device is active")
    require(not any(Path("/sys/block").glob("nbd*/pid")), "An NBD device is active")
    mounts = subprocess.check_output(["findmnt", "-rn", "-o", "TARGET,SOURCE"], text=True)
    require(str(LAB) not in mounts, "A Mint lab mount is active")
    for path in (TARGET, DECOY, SEED):
        require(subprocess.run(["fuser", "-s", str(path)], check=False).returncode == 1,
                f"A media file is open or fuser failed: {path}")


def audit() -> dict:
    require(LAB.resolve(strict=True) == LAB and LAB.is_dir() and not LAB.is_symlink(),
            "Mint lab root is redirected")
    ensure_detached()
    for name in REQUIRED_EVIDENCE:
        path = LAB / name
        require(path.is_file() and not path.is_symlink() and path.stat().st_size > 0,
                f"Compact readback is unavailable: {name}")
    for path in (TARGET, DECOY, SEED):
        require(path.parent == LAB and path.resolve(strict=True) == path and not path.is_symlink(),
                f"Task-owned media path is redirected: {path}")
        metadata = path.stat()
        require(stat.S_ISREG(metadata.st_mode) and metadata.st_uid == 1000 and metadata.st_nlink == 1,
                f"Task-owned media is not a single-link regular file: {path}")
    require(TARGET.stat().st_size == 40 * 1024**3 and DECOY.stat().st_size == 40 * 1024**3,
            "Disposable target or decoy geometry changed")
    require(SEED.stat().st_size == 170852352 and SEED.stat().st_blocks * 512 == 170795008,
            "Disposable seed child allocation changed")
    require(PROTECTED_BACKING.is_file() and not PROTECTED_BACKING.is_symlink() and
            PROTECTED_BACKING.resolve(strict=True) == PROTECTED_BACKING,
            "Protected v20 backing is unavailable or redirected")
    require(sha256(PROTECTED_BACKING) == EXPECTED_PARENT_SHA256,
            "Protected v20 backing hash changed")
    require(sha256(SEED) == EXPECTED_SEED_SHA256,
            "Disposable v26 seed child hash changed")
    info = qemu_info(SEED)
    require(info.get("format") == "qcow2" and
            info.get("full-backing-filename") == str(PROTECTED_BACKING) and
            info.get("backing-filename-format") == "raw",
            "Disposable seed child has the wrong backing chain")
    target_table = subprocess.check_output(["sgdisk", "-p", str(TARGET)], text=True)
    decoy_table = subprocess.check_output(["sgdisk", "-p", str(DECOY)], text=True)
    require("47A16C85-8F90-47A7-85EA-6D4D38F020AE" in target_table and
            "   4        11536384        83884031" in target_table,
            "Disposable selected target GPT differs")
    require("88306617-7DE0-4A53-8668-C2D0CB792F88" in decoy_table and
            "Number  Start (sector)" in decoy_table and "   1            " not in decoy_table,
            "Same-size blank decoy GPT differs")
    post = (LAB / "mint-fresh-v26-postinstall-readback.txt").read_text()
    second = (LAB / "mint-fresh-v26-seedfree-readback.txt").read_text()
    fat = (LAB / "mint-fresh-v26-fat-postseedfree.txt").read_text()
    decoy_hash = (LAB / "mint-fresh-v26-decoy-full-sha256.txt").read_text()
    require("/boot/efi was on /dev/vdb3" in post and
            "p1 " + EXPECTED_P1_SHA256 in second and
            "p2 " + EXPECTED_P2_SHA256 in second and
            decoy_hash == f"{EXPECTED_DECOY_SHA256}  {DECOY}\n",
            "Final installed/decoy/protected-partition readback is incomplete")
    require("primary-backup-boot-differences []" in fat and
            fat.count("clean-shutdown-bit True") == 2 and "read-only-fsck-exit 0" in fat,
            "Final seed FAT clean-state readback is incomplete")

    images = sorted(path for path in Path("/home/gillesduif").rglob("*.qcow2")
                    if path.is_file() and not path.is_symlink())
    for image in images:
        if image == SEED:
            continue
        image_info = qemu_info(image)
        for key in ("backing-filename", "full-backing-filename"):
            backing = image_info.get(key)
            if backing:
                resolved = Path(backing)
                if not resolved.is_absolute():
                    resolved = image.parent / resolved
                require(resolved.resolve(strict=True) not in (TARGET, DECOY, SEED),
                        f"A qcow2 child still depends on disposable media: {image}")

    return {
        "target": str(TARGET), "targetAllocatedBytes": TARGET.stat().st_blocks * 512,
        "decoy": str(DECOY), "decoyAllocatedBytes": DECOY.stat().st_blocks * 512,
        "seedChild": str(SEED), "seedChildAllocatedBytes": SEED.stat().st_blocks * 512,
        "protectedBacking": str(PROTECTED_BACKING),
        "protectedBackingSha256": EXPECTED_PARENT_SHA256,
        "seedChildSha256": EXPECTED_SEED_SHA256,
        "qcow2HeadersChecked": len(images),
        "retainedEvidenceCount": len(REQUIRED_EVIDENCE),
        "hostFreeBeforeBytes": free_bytes("/mnt/c"),
        "wslFreeBeforeBytes": free_bytes(str(LAB)),
    }


def write_exclusive(path: Path, data: dict) -> None:
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
        json.dump(data, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    directory = os.open(str(path.parent), os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


if __name__ == "__main__":
    if sys.argv[1:] not in ([], ["--retire"]):
        raise SystemExit("usage: retire-fresh-v26-media.py [--retire]")
    report = audit()
    if sys.argv[1:] == ["--retire"]:
        require(not PLAN.exists() and not RESULT.exists(), "Retirement already attempted")
        write_exclusive(PLAN, report)
        for path in (SEED, DECOY, TARGET):
            path.unlink()
        os.sync()
        require(not SEED.exists() and not DECOY.exists() and not TARGET.exists(),
                "Disposable media removal is incomplete")
        report["retired"] = True
        report["hostFreeAfterBytes"] = free_bytes("/mnt/c")
        report["wslFreeAfterBytes"] = free_bytes(str(LAB))
        write_exclusive(RESULT, report)
    print(json.dumps(report, indent=2))
