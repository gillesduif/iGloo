#!/usr/bin/env python3
"""Read-only Linux GPT/filesystem acquisition for LinuxInstallerInventoryProtocol.

No mounts, udev writes, device creation, partitioning or firmware operations.
Unknown topology or probe errors produce an observation failure, never empty facts.
No caller-supplied command strings; commands below use argv with shell=False.
"""
import json
import os
import re
import subprocess
import sys
import uuid


class ObservationError(Exception):
    def __init__(self, state, code):
        super().__init__(code)
        self.state, self.code = state, code


def require(condition, code):
    if not condition:
        raise ObservationError("Ambiguous", code)


def native(argv):
    try:
        result = subprocess.run(argv, capture_output=True, text=True, check=False,
                                timeout=30, env={**os.environ, "LC_ALL": "C"})
    except PermissionError as error:
        raise ObservationError("AccessDenied", "InventoryCommandAccessDenied") from error
    except FileNotFoundError as error:
        raise ObservationError("Unsupported", "InventoryToolMissing") from error
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ObservationError("Unavailable", "InventoryCommandUnavailable") from error
    if result.returncode != 0:
        # Native nonzero exit is not evidence of absence or a localized-text diagnosis.
        raise ObservationError("Unavailable", f"{argv[0]}Exit{result.returncode}")
    return result.stdout


def guid(value):
    parsed = uuid.UUID(value)
    require(parsed.int != 0, "EmptyGptIdentity")
    return str(parsed)


def number(value):
    require(type(value) is int and 0 <= value < 2**64, "InvalidInventoryNumber")
    return value


def device(value):
    require(isinstance(value, str) and re.fullmatch(r"/dev/[A-Za-z0-9_/-]+", value), "InvalidDevicePath")
    return value


def filesystem(path, run):
    signatures = json.loads(run(["wipefs", "--no-act", "--json", "--output", "TYPE,UUID", path]))["signatures"]
    require(isinstance(signatures, list), "InvalidSignatureList")
    if not signatures:
        # Successful read-only libblkid scan: no RECOGNIZED signature, not proof of zero data.
        return {"availability": "Absent", "code": "NoRecognizedFilesystemSignature"}
    kinds = {s["type"] for s in signatures}
    ids = {s["uuid"] for s in signatures}
    require(len(kinds) == 1 and len(ids) == 1, "AmbivalentFilesystemSignatures")
    values = {}
    for line in run(["blkid", "--probe", "--output", "export", path]).splitlines():
        key, value = line.split("=", 1)
        require(key not in values, "DuplicateBlkidField")
        values[key] = value
    kind = values["TYPE"]
    require(kind in kinds, "FilesystemProbeDisagrees")
    fs_uuid = values.get("UUID")
    require(fs_uuid in ids, "FilesystemUuidDisagrees")
    if kind == "vfat":
        require(values.get("VERSION") == "FAT32", "FilesystemNotFat32")
        kind = "FAT32"
    elif kind == "squashfs":
        require(fs_uuid is None, "UnexpectedSquashfsUuid")
        kind = "SQUASHFS"
    else:
        require(isinstance(fs_uuid, str) and bool(fs_uuid), "FilesystemUuidMissing")
        kind = kind.upper()
    return {"availability": "Available", "type": kind, "uuid": fs_uuid}


def once(run):
    rows = json.loads(run(["lsblk", "--json", "--bytes", "--list", "--paths", "--all",
                           "--output", "NAME,TYPE,PKNAME,SIZE,LOG-SEC"]))["blockdevices"]
    require(isinstance(rows, list), "InvalidBlockInventory")
    names = [device(r["name"]) for r in rows]
    require(len(names) == len(set(names)), "DuplicateBlockPath")
    disks, partitions, external = [], [], []
    for row in rows:
        path, kind = device(row["name"]), row["type"]
        if kind in ("loop", "rom"):
            external.append({"devicePath": path, "fileSystem": filesystem(path, run)})
        elif kind == "disk":
            table = json.loads(run(["sfdisk", "--json", path]))["partitiontable"]
            if table["label"] != "gpt":
                raise ObservationError("Unsupported", "NonGptDiskVisible")
            require(table["device"] == path and table["unit"] == "sectors", "UnexpectedGptDeviceOrUnits")
            sector = number(table["sectorsize"])
            require(sector > 0 and sector == number(row["log-sec"]), "SectorSizeMismatch")
            disks.append({"devicePath": path, "gptDiskGuid": guid(table["id"]),
                          "sizeBytes": number(row["size"]), "logicalSectorSize": sector})
            for part in table["partitions"]:
                part_path = device(part["node"])
                matches = [r for r in rows if r["name"] == part_path and r["type"] == "part" and r["pkname"] == path]
                require(len(matches) == 1, "PartitionParentUnresolved")
                size = number(number(part["size"]) * sector)
                require(size == number(matches[0]["size"]), "PartitionSizeMismatch")
                partitions.append({"devicePath": part_path, "diskDevicePath": path,
                                   "partitionGuid": guid(part["uuid"]), "partitionType": guid(part["type"]),
                                   "offsetBytes": number(number(part["start"]) * sector), "sizeBytes": size,
                                   "fileSystem": filesystem(part_path, run)})
        elif kind != "part":
            raise ObservationError("Unsupported", "LayeredBlockTopologyUnsupported")
    require(len(partitions) == sum(r["type"] == "part" for r in rows), "UnaccountedPartition")
    require(len({d["gptDiskGuid"] for d in disks}) == len(disks), "DuplicateDiskGuid")
    require(len({p["partitionGuid"] for p in partitions}) == len(partitions), "DuplicatePartuuid")
    uuids = [p["fileSystem"]["uuid"].upper() for p in partitions + external
             if p["fileSystem"]["availability"] == "Available" and p["fileSystem"]["uuid"] is not None]
    require(len(uuids) == len(set(uuids)), "DuplicateFilesystemUuid")
    return {"schemaVersion": 1, "availability": "Available",
            "disks": sorted(disks, key=lambda d: d["gptDiskGuid"]),
            "partitions": sorted(partitions, key=lambda p: p["partitionGuid"]),
            "externalFileSystems": sorted(external, key=lambda e: e["devicePath"])}


def collect(run=native):
    try:
        first, second = once(run), once(run)
        require(first == second, "InventoryChangedDuringRead")
        return first
    except ObservationError as error:
        return {"schemaVersion": 1, "availability": error.state, "code": error.code}
    except (ValueError, KeyError, TypeError, OverflowError) as error:
        return {"schemaVersion": 1, "availability": "Ambiguous", "code": "MalformedNativeInventory:" + type(error).__name__}


if __name__ == "__main__":
    observation = collect() if sys.platform == "linux" else {
        "schemaVersion": 1, "availability": "Unsupported", "code": "LinuxRequired"}
    print(json.dumps(observation, sort_keys=True, separators=(",", ":")))
    sys.exit(0 if observation["availability"] == "Available" else 1)
