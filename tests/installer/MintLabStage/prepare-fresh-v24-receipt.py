#!/usr/bin/env python3
"""Bind the disposable Mint v24 reviewed extent to its observed GPT bytes."""

import json
import os
import pathlib
import re
import subprocess
import uuid


LAB = pathlib.Path("/home/gillesduif/igloo-mint-22.3-lab")
TARGET = LAB / "mint-target-fresh-v24.raw"
IDS = LAB / "mint-fresh-v24-ids.json"
OUTPUT = LAB / "mint-fresh-v24-reviewed-receipt.json"
SECTOR = 512


def sgdisk(*arguments: str) -> str:
    return subprocess.check_output(
        ["sgdisk", *arguments, str(TARGET)], text=True
    )


def partition(number: int) -> tuple[str, str, int, int]:
    info = sgdisk("-i", str(number))

    def field(pattern: str) -> str:
        match = re.search(pattern, info, flags=re.MULTILINE)
        if match is None:
            raise ValueError(f"Partition {number} is missing a required GPT field")
        return match.group(1)

    return (
        field(r"^Partition GUID code: ([A-F0-9-]{36}) "),
        field(r"^Partition unique GUID: ([A-F0-9-]{36})$"),
        int(field(r"^First sector: (\d+) ")),
        int(field(r"^Last sector: (\d+) ")),
    )


def main() -> None:
    ids = json.loads(IDS.read_text(encoding="utf-8"))
    if OUTPUT.exists() or not TARGET.is_file() or TARGET.stat().st_size != 40 * 1024**3:
        raise ValueError("Fresh exact target or unused receipt path is required")
    table = sgdisk("-p")
    disk_guid = re.search(r"^Disk identifier \(GUID\): ([A-F0-9-]{36})$", table, re.MULTILINE)
    if disk_guid is None or disk_guid.group(1).lower() != ids["targetDiskGuid"].lower():
        raise ValueError("Fresh target GPT disk GUID changed")
    if [int(value) for value in re.findall(r"^\s*(\d+)\s+\d+\s+\d+\s+\d", table, re.MULTILINE)] != [1, 2, 3]:
        raise ValueError("Fresh target partition inventory changed")

    esp_type = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B"
    ntfs_type = "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"
    p1, p2, p3 = (partition(number) for number in (1, 2, 3))
    if not (
        p1[0] == esp_type and p1[2:] == (2048, 1050623)
        and p2[0] == ntfs_type and p2[2:] == (1050624, 9439231)
        and p3[0] == esp_type and p3[2:] == (9439232, 11536383)
        and p3[1].lower() == ids["linuxEspPartuuid"].lower()
    ):
        raise ValueError("Fresh target Windows-like source or Linux ESP geometry changed")
    first_free = p3[3] + 1
    last_free = int(sgdisk("-E").strip())
    if first_free != 11536384 or last_free != 83886046:
        raise ValueError("Fresh reviewed root extent differs from observed GPT")
    output = {
        "generationId": str(uuid.uuid4()),
        "targetDiskGuid": disk_guid.group(1),
        "linuxEspPartitionGuid": p3[1],
        "firstSector": first_free,
        "lastSector": last_free,
        "offsetBytes": first_free * SECTOR,
        "sizeBytes": (last_free - first_free + 1) * SECTOR,
        "rootMinimumBytes": 20 * 1024**3,
    }
    fd = os.open(OUTPUT, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(output, stream, separators=(",", ":"))
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    print(json.dumps(output, separators=(",", ":")))


if __name__ == "__main__":
    main()
