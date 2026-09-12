"""Read-only resolution of an iGloo installation receipt against Linux GPTs.

The version 1 receipt proves the observed disk/partition identities at the time
of resolution. It is not permission to format a device, an exclusive lock, or a
defence against deliberate byte-for-byte clones. A future writer must recheck
identity, mount/holder state, signatures and quiescence immediately before any
mutation. This module never mounts, formats, repairs or writes a device.

``resolve`` accepts observations made by a trusted collector. Production callers
use ``collect_layouts``: both GPT copies and their CRCs are checked, then every
GPT entry is bound to the kernel partition table through sysfs and device IDs.
Names, labels, partition numbers and enumeration order never select a target.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import sys
from typing import Any, BinaryIO
import uuid
import zlib


LINUX_FILESYSTEM = "0fc63daf-8483-4772-8e79-3d69d8477de4"
EFI_SYSTEM = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b"
_ZERO_GUID = "00000000-0000-0000-0000-000000000000"
_MAX_INT64 = (1 << 63) - 1
_MIB = 1024 * 1024
_MAX_TABLE_BYTES = 16 * _MIB
_MAX_MANIFEST_BYTES = 16 * _MIB
_MAX_JSON_DEPTH = 64
_DEVICE_PATH = re.compile(r"/dev/[A-Za-z0-9_.+-]+\Z")
_PARTITION_KEYS = {"partitionGuid", "gptType", "offsetBytes", "lengthBytes"}
_DISK_KEYS = {"diskGuid", "logicalSectorSize", "diskSizeBytes", "partitions"}
_TARGET_KEYS = {
    "version", "installationId", "ownership", "disk", "rootPartitionGuid",
    "espPartitionGuid",
}


class IdentityError(ValueError):
    """Missing, malformed, stale or ambiguous installation identity."""


def _object(value: Any, keys: set[str], context: str, *, extras: bool = False) -> dict:
    if not isinstance(value, dict) or not keys.issubset(value):
        raise IdentityError(f"{context}: required fields are missing")
    if not extras and set(value) != keys:
        raise IdentityError(f"{context}: unknown version 1 fields")
    return value


def _integer(value: Any, context: str, *, minimum: int = 1) -> int:
    # bool is an int subclass, but is not an integer in this contract.
    if type(value) is not int or not minimum <= value <= _MAX_INT64:
        raise IdentityError(f"{context}: expected a bounded integer")
    return value


def _guid(value: Any, context: str) -> str:
    if not isinstance(value, str):
        raise IdentityError(f"{context}: expected a canonical GUID")
    try:
        canonical = str(uuid.UUID(value))
    except (ValueError, AttributeError) as exc:
        raise IdentityError(f"{context}: invalid GUID") from exc
    if value != canonical or canonical == _ZERO_GUID:
        raise IdentityError(f"{context}: expected a nonempty lowercase D GUID")
    return value


def _path(value: Any, context: str) -> str:
    if not isinstance(value, str) or not _DEVICE_PATH.fullmatch(value):
        raise IdentityError(f"{context}: invalid kernel device path")
    return value


def _validate_disk(value: Any, *, observed: bool) -> dict:
    disk = _object(value, _DISK_KEYS, "disk", extras=observed)
    _guid(disk["diskGuid"], "disk GUID")
    sector = _integer(disk["logicalSectorSize"], "logical sector size")
    if sector not in (512, 4096):
        raise IdentityError("unsupported logical sector size")
    size = _integer(disk["diskSizeBytes"], "disk size")
    if size % sector or size < 2 * _MIB:
        raise IdentityError("invalid disk size or alignment")
    partitions = disk["partitions"]
    if not isinstance(partitions, list) or len(partitions) > 4096:
        raise IdentityError("disk must record its complete partition list (maximum 4096)")
    seen: set[str] = set()
    extents = []
    for part in partitions:
        _object(part, _PARTITION_KEYS, "partition", extras=observed)
        guid = _guid(part["partitionGuid"], "partition GUID")
        _guid(part["gptType"], "GPT type")
        if guid in seen:
            raise IdentityError("duplicate partition GUID")
        seen.add(guid)
        offset = _integer(part["offsetBytes"], "partition offset")
        length = _integer(part["lengthBytes"], "partition length")
        if offset % sector or length % sector:
            raise IdentityError("unaligned partition extent")
        if offset < 2 * sector or offset + length > size - sector:
            raise IdentityError("partition extent outside the disk")
        extents.append((offset, offset + length))
    extents.sort()
    if any(previous[1] > current[0] for previous, current in zip(extents, extents[1:])):
        raise IdentityError("overlapping partition extents")
    return disk


def _receipt(manifest: Any, expected_installation_id: str, require_esp: bool) -> dict:
    expected = _guid(expected_installation_id, "expected installation ID")
    _object(manifest, {"schemaVersion", "installationId", "installationTarget"},
            "manifest", extras=True)
    if _integer(manifest["schemaVersion"], "manifest schema version") != 1:
        raise IdentityError("unsupported manifest schema version")
    if _guid(manifest["installationId"], "manifest installation ID") != expected:
        raise IdentityError("stale installation ID")
    target = _object(manifest["installationTarget"], _TARGET_KEYS, "installation target")
    if _integer(target["version"], "target version") != 1:
        raise IdentityError("unsupported installation target version")
    if _guid(target["installationId"], "target installation ID") != expected:
        raise IdentityError("installation ID mismatch")
    if target["ownership"] != "created-by-igloo":
        raise IdentityError("root ownership was not recorded by iGloo")
    disk = _validate_disk(target["disk"], observed=False)
    parts = {part["partitionGuid"]: part for part in disk["partitions"]}
    root_guid = _guid(target["rootPartitionGuid"], "root partition GUID")
    root = parts.get(root_guid)
    if root is None or root["gptType"] != LINUX_FILESYSTEM:
        raise IdentityError("owned root is missing or has the wrong GPT type")
    if root["offsetBytes"] < _MIB or root["offsetBytes"] + root["lengthBytes"] > disk["diskSizeBytes"] - _MIB:
        raise IdentityError("owned root is outside the conservative allocation bounds")
    esp_guid = target["espPartitionGuid"]
    if esp_guid is None:
        if require_esp:
            raise IdentityError("an explicit existing ESP is required")
    else:
        _guid(esp_guid, "ESP partition GUID")
        if esp_guid == root_guid or esp_guid not in parts or parts[esp_guid]["gptType"] != EFI_SYSTEM:
            raise IdentityError("ESP is missing, aliases root, or has the wrong GPT type")
    return target


def _identity(disk: dict) -> tuple:
    return (disk["diskGuid"], disk["logicalSectorSize"], disk["diskSizeBytes"],
            frozenset(tuple(part[key] for key in sorted(_PARTITION_KEYS))
                      for part in disk["partitions"]))


def resolve(manifest: dict, layouts: list[dict], expected_installation_id: str,
            require_esp: bool = True) -> dict:
    """Return paths only after exact whole-layout and global uniqueness checks.

    Observed dictionaries have the receipt's disk/partition identity fields plus
    ``devicePath``. Other collector fields (such as partition numbers) are not
    identity. Callers must supply a complete trusted disk inventory, never a
    prefiltered list containing only the desired disk.
    """
    if type(require_esp) is not bool:
        raise IdentityError("require_esp must be a boolean")
    target = _receipt(manifest, expected_installation_id, require_esp)
    if not isinstance(layouts, list) or not layouts:
        raise IdentityError("no observed GPT disks")
    disk_guids: set[str] = set()
    partition_guids: set[str] = set()
    paths: set[str] = set()
    candidate = None
    for item in layouts:
        disk = _validate_disk(item, observed=True)
        if disk["diskGuid"] in disk_guids:
            raise IdentityError("ambiguous duplicate disk GUID")
        disk_guids.add(disk["diskGuid"])
        for entry in [disk, *disk["partitions"]]:
            path = _path(entry.get("devicePath"), "observed device")
            if path in paths:
                raise IdentityError("duplicate observed device path")
            paths.add(path)
        for part in disk["partitions"]:
            if part["partitionGuid"] in partition_guids:
                raise IdentityError("ambiguous partition GUID across disks")
            partition_guids.add(part["partitionGuid"])
        if disk["diskGuid"] == target["disk"]["diskGuid"]:
            candidate = disk
    if candidate is None:
        raise IdentityError("recorded disk was not found")
    if _identity(candidate) != _identity(target["disk"]):
        raise IdentityError("observed disk layout differs from the installation receipt")
    parts = {part["partitionGuid"]: part for part in candidate["partitions"]}
    esp_guid = target["espPartitionGuid"]
    return {
        "installationId": target["installationId"],
        "diskDevice": candidate["devicePath"],
        "rootDevice": parts[target["rootPartitionGuid"]]["devicePath"],
        "espDevice": parts[esp_guid]["devicePath"] if esp_guid is not None else None,
    }


def _read_at(stream: BinaryIO, offset: int, size: int) -> bytes:
    stream.seek(offset)
    data = stream.read(size)
    if len(data) != size:
        raise IdentityError("short read while inspecting GPT")
    return data


def _header(stream: BinaryIO, sector: int, disk_size: int, lba: int) -> dict:
    data = _read_at(stream, lba * sector, sector)
    if data[:8] != b"EFI PART":
        raise IdentityError("missing GPT header")
    revision, header_size, checksum, reserved = struct.unpack_from("<IIII", data, 8)
    if revision != 0x10000 or header_size != 92 or reserved:
        raise IdentityError("unsupported or malformed GPT header")
    checked = bytearray(data[:header_size])
    checked[16:20] = b"\0" * 4
    if zlib.crc32(checked) != checksum:
        raise IdentityError("GPT header CRC mismatch")
    current, other, first, last = struct.unpack_from("<QQQQ", data, 24)
    final_lba = disk_size // sector - 1
    if current != lba or other != (final_lba if lba == 1 else 1):
        raise IdentityError("GPT header location mismatch")
    if first < 2 or first > last or last >= final_lba:
        raise IdentityError("invalid GPT usable area")
    disk_guid = _guid(str(uuid.UUID(bytes_le=data[56:72])), "GPT disk GUID")
    table_lba, count, entry_size, table_crc = struct.unpack_from("<QIII", data, 72)
    # Unknown entry extensions cannot silently change version 1 semantics.
    if not 1 <= count <= 4096 or entry_size != 128 or count * entry_size > _MAX_TABLE_BYTES:
        raise IdentityError("unsupported GPT partition array")
    table_size = count * entry_size
    table_end = table_lba + (table_size + sector - 1) // sector
    if lba == 1:
        if table_lba < 2 or table_end > first:
            raise IdentityError("primary GPT array overlaps usable space")
    elif table_lba <= last or table_end > final_lba:
        raise IdentityError("backup GPT array overlaps usable space or header")
    table = _read_at(stream, table_lba * sector, table_size)
    if zlib.crc32(table) != table_crc:
        raise IdentityError("GPT partition array CRC mismatch")
    return {"diskGuid": disk_guid, "first": first, "last": last,
            "count": count, "entrySize": entry_size, "table": table}


def read_gpt(stream: BinaryIO, logical_sector_size: int, disk_size_bytes: int) -> dict | None:
    """Inspect a seekable read-only disk/image stream, never repair it.

    Return None only for media without a protective MBR or either GPT signature.
    GPT-looking but invalid media are errors, not disks silently skipped during
    clone detection. The same parser is used for real devices and sparse tests.
    """
    sector = _integer(logical_sector_size, "logical sector size")
    size = _integer(disk_size_bytes, "disk size")
    # Optical media commonly use 2048-byte sectors. Inspect them enough to
    # exclude non-GPT media, but never silently exclude GPT-looking media.
    if sector not in (512, 2048, 4096) or size % sector or size < sector * 4:
        raise IdentityError("unsupported disk geometry")
    first_sector = _read_at(stream, 0, sector)
    primary = _read_at(stream, sector, sector)
    backup = _read_at(stream, size - sector, sector)
    mbr_entries = [first_sector[446 + i * 16:462 + i * 16] for i in range(4)]
    protective = any(entry[4] == 0xEE for entry in mbr_entries)
    if not protective and primary[:8] != b"EFI PART" and backup[:8] != b"EFI PART":
        return None
    if sector not in (512, 4096):
        raise IdentityError("GPT media have an unsupported logical sector size")
    active = [entry for entry in mbr_entries if any(entry)]
    if first_sector[510:512] != b"\x55\xaa" or len(active) != 1 or active[0][4] != 0xEE:
        raise IdentityError("invalid or hybrid protective MBR")
    mbr_start, mbr_length = struct.unpack_from("<II", active[0], 8)
    if mbr_start != 1 or mbr_length != min(size // sector - 1, 0xFFFFFFFF):
        raise IdentityError("protective MBR geometry mismatch")
    head = _header(stream, sector, size, 1)
    tail = _header(stream, sector, size, size // sector - 1)
    if head != tail:
        raise IdentityError("primary and backup GPT disagree")
    partitions = []
    for index in range(head["count"]):
        entry = head["table"][index * 128:(index + 1) * 128]
        if entry[:16] == b"\0" * 16:
            if any(entry):
                raise IdentityError("nonempty unused GPT entry")
            continue
        first, last = struct.unpack_from("<QQ", entry, 32)
        if first < head["first"] or last > head["last"] or last < first:
            raise IdentityError("GPT partition outside usable area")
        partitions.append({
            "partitionGuid": str(uuid.UUID(bytes_le=entry[16:32])),
            "gptType": str(uuid.UUID(bytes_le=entry[:16])),
            "offsetBytes": first * sector,
            "lengthBytes": (last - first + 1) * sector,
            "partitionNumber": index + 1,
        })
    result = {"diskGuid": head["diskGuid"], "logicalSectorSize": sector,
              "diskSizeBytes": size, "partitions": partitions}
    # An empty GPT cannot contain our receipt, but retain its disk GUID for
    # global duplicate detection rather than silently dropping it.
    _validate_disk(result, observed=True)
    return result


def _sysfs_int(path: Path) -> int:
    text = path.read_text(encoding="ascii").strip()
    if not text.isdecimal():
        raise IdentityError(f"invalid sysfs integer: {path.name}")
    return _integer(int(text), f"sysfs {path.name}", minimum=0)


def _device_id(path: Path) -> tuple[int, int]:
    text = (path / "dev").read_text(encoding="ascii").strip()
    if not re.fullmatch(r"[0-9]+:[0-9]+", text):
        raise IdentityError("invalid sysfs device ID")
    major, minor = (int(number) for number in text.split(":"))
    return major, minor


def _bind_kernel_table(disk: dict, disk_node: Path, nodes: list[Path]) -> dict:
    """Bind GPT entry numbers to sysfs only after comparing exact extents."""
    kernel = {}
    for node in nodes:
        if not (node / "partition").exists() or node.resolve().parent != disk_node.resolve():
            continue
        number = _sysfs_int(node / "partition")
        if number < 1 or number in kernel:
            raise IdentityError("duplicate or invalid kernel partition number")
        kernel[number] = node
    expected = {part["partitionNumber"] for part in disk["partitions"]}
    if set(kernel) != expected:
        raise IdentityError("kernel partition table differs from GPT")
    for part in disk["partitions"]:
        node = kernel[part["partitionNumber"]]
        # Linux sysfs start/size always count 512-byte sectors, including 4Kn.
        if _sysfs_int(node / "start") * 512 != part["offsetBytes"] or _sysfs_int(node / "size") * 512 != part["lengthBytes"]:
            raise IdentityError("stale kernel partition extent")
        path = _path(f"/dev/{node.name}", "partition device")
        info = os.stat(path)
        if not stat.S_ISBLK(info.st_mode) or (os.major(info.st_rdev), os.minor(info.st_rdev)) != _device_id(node):
            raise IdentityError("partition device node does not match sysfs")
        part["devicePath"] = path
    return disk


def _optical_pins(value: dict[str, int] | None) -> dict[str, int]:
    if value is None:
        return {}
    if type(value) is not dict or len(value) > 8:
        raise IdentityError("expected at most eight trusted optical media pins")
    result = dict(value)
    for digest, size in result.items():
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise IdentityError("optical media pin must be a lowercase SHA256")
        if _integer(size, "optical media size") % 2048:
            raise IdentityError("optical media size must align to 2048 bytes")
    return result


def _verified_optical(stream: BinaryIO, node: Path, sector: int, size: int,
                      pins: dict[str, int], ioctl) -> bool:
    # An optical ISO can contain a hybrid 512-byte GPT while the drive exposes
    # 2048-byte sectors. Never treat this exception as a generic GPT parse retry.
    # Pins come from trusted installer code, NOT from the migration manifest.
    if not pins or sector != 2048 or size not in pins.values():
        return False
    if _sysfs_int(node / "device/type") != 5 or _sysfs_int(node / "ro") != 1:
        return False
    if struct.unpack("=I", ioctl(stream.fileno(), 0x125E, bytes(4)))[0] != 1:
        return False  # BLKROGET: require the opened device itself to be read-only.
    stream.seek(0)
    digest = hashlib.sha256()
    remaining = size
    while remaining:
        chunk = stream.read(min(1024 * 1024, remaining))
        if not chunk:
            raise IdentityError("short read while verifying optical media")
        digest.update(chunk)
        remaining -= len(chunk)
    if pins.get(digest.hexdigest()) != size:
        raise IdentityError("optical media checksum does not match the trusted pin")
    if _sysfs_int(node / "ro") != 1 or struct.unpack("=I", ioctl(stream.fileno(), 0x125E, bytes(4)))[0] != 1:
        raise IdentityError("optical media read-only state changed during verification")
    return True


def _optical_identities(stream: BinaryIO, size: int) -> tuple[set[str], set[str]]:
    """Retain hybrid image GUIDs for collision detection, never target resolution."""
    disks, partitions = set(), set()
    for sector in (512, 4096):
        if size % sector or size < 4 * sector:
            continue
        if _read_at(stream, sector, 8) != b"EFI PART":
            continue
        head = _header(stream, sector, size, 1)
        if head != _header(stream, sector, size, size // sector - 1):
            raise IdentityError("verified optical GPT copies disagree")
        disks.add(head["diskGuid"])
        for index in range(head["count"]):
            entry = head["table"][index * 128:(index + 1) * 128]
            if entry[:16] != bytes(16):
                guid = _guid(str(uuid.UUID(bytes_le=entry[16:32])), "optical partition GUID")
                if guid in partitions:
                    raise IdentityError("duplicate optical partition GUID")
                partitions.add(guid)
    return disks, partitions


def _check_optical_collisions(layouts: list[dict], disks: set[str], partitions: set[str]) -> None:
    for layout in layouts:
        if layout["diskGuid"] in disks:
            raise IdentityError("disk GUID also occurs on verified optical media")
        if any(part["partitionGuid"] in partitions for part in layout["partitions"]):
            raise IdentityError("partition GUID also occurs on verified optical media")


def collect_layouts(*, verified_optical_media: dict[str, int] | None = None) -> list[dict]:
    """Read all nonempty whole block devices on Linux, failing closed on errors.

    Unreadable/corrupt GPT-looking devices are not skipped: they could contain a
    duplicate identity. Loop/device-mapper aliases are included, so ambiguity
    fails closed. Trusted caller-supplied SHA256/size pins may exclude exact,
    read-only 2048-byte optical media; they must never come from a manifest.
    No external commands or partition rescans are performed.
    """
    if sys.platform != "linux":
        raise IdentityError("live collection requires Linux")
    pins = _optical_pins(verified_optical_media)
    import fcntl  # Linux-only; the pure parser/resolver also run on Windows.

    sysfs = Path("/sys/class/block")
    nodes = sorted(sysfs.iterdir())
    layouts = []
    optical_disks, optical_partitions = set(), set()
    for node in nodes:
        if (node / "partition").exists():
            continue
        sysfs_size = _sysfs_int(node / "size") * 512
        if sysfs_size == 0:
            continue
        path = _path(f"/dev/{node.name}", "disk device")
        with open(path, "rb", buffering=0) as stream:
            info = os.fstat(stream.fileno())
            if not stat.S_ISBLK(info.st_mode) or (os.major(info.st_rdev), os.minor(info.st_rdev)) != _device_id(node):
                raise IdentityError("disk device node does not match sysfs")
            size = struct.unpack("=Q", fcntl.ioctl(stream.fileno(), 0x80081272, bytes(8)))[0]
            sector = struct.unpack("=I", fcntl.ioctl(stream.fileno(), 0x1268, bytes(4)))[0]
            if size != sysfs_size or sector != _sysfs_int(node / "queue/logical_block_size"):
                raise IdentityError("kernel disk geometry changed during collection")
            if _verified_optical(stream, node, sector, size, pins, fcntl.ioctl):
                disks, partitions = _optical_identities(stream, size)
                if optical_disks.intersection(disks) or optical_partitions.intersection(partitions):
                    raise IdentityError("duplicate GPT identities on verified optical media")
                optical_disks.update(disks)
                optical_partitions.update(partitions)
                layout = None
            else:
                layout = read_gpt(stream, sector, size)
            if layout is not None:
                layout["devicePath"] = path
                _bind_kernel_table(layout, node, nodes)
                # A second observation rejects changes during sysfs binding.
                stripped = {**layout, "partitions": [
                    {key: value for key, value in part.items() if key != "devicePath"}
                    for part in layout["partitions"]]}
                stripped.pop("devicePath")
                if read_gpt(stream, sector, size) != stripped:
                    raise IdentityError("GPT changed during collection")
                layouts.append(layout)
            if _sysfs_int(node / "size") * 512 != size or _device_id(node) != (os.major(info.st_rdev), os.minor(info.st_rdev)):
                raise IdentityError("disk changed during collection")
            if struct.unpack("=Q", fcntl.ioctl(stream.fileno(), 0x80081272, bytes(8)))[0] != size or struct.unpack("=I", fcntl.ioctl(stream.fileno(), 0x1268, bytes(4)))[0] != sector:
                raise IdentityError("opened device geometry changed during collection")
    if [node.name for node in sorted(sysfs.iterdir())] != [node.name for node in nodes]:
        raise IdentityError("block device inventory changed during collection")
    _check_optical_collisions(layouts, optical_disks, optical_partitions)
    return layouts


def _unique_object(pairs: list[tuple[str, Any]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise IdentityError(f"duplicate JSON field: {key}")
        result[key] = value
    return result


def _reject_constant(value: str) -> None:
    raise IdentityError(f"invalid JSON constant: {value}")


def read_claim(path: str | Path, expected_installation_id: str,
               require_esp: bool = True) -> dict:
    """Read and validate a bounded manifest before any block-device collection.

    Returns the complete manifest for ``resolve``. Unknown migration fields are
    retained; unknown target fields, duplicate keys and nesting over 64 fail.
    """
    if type(require_esp) is not bool:
        raise IdentityError("require_esp must be a boolean")
    with Path(path).open("rb") as stream:
        data = stream.read(_MAX_MANIFEST_BYTES + 1)
    if len(data) > _MAX_MANIFEST_BYTES:
        raise IdentityError("oversized migration manifest")
    # Bound nesting before invoking the recursive JSON parser. Quotes and
    # escaped quotes are handled so braces inside string values do not count.
    depth = 0
    in_string = False
    escaped = False
    for byte in data:
        if in_string:
            if escaped:
                escaped = False
            elif byte == 92:
                escaped = True
            elif byte == 34:
                in_string = False
        elif byte == 34:
            in_string = True
        elif byte in (91, 123):
            depth += 1
            if depth > _MAX_JSON_DEPTH:
                raise IdentityError("migration manifest exceeds maximum nesting")
        elif byte in (93, 125):
            depth -= 1
    try:
        manifest = json.loads(data.decode("utf-8"), object_pairs_hook=_unique_object,
                              parse_constant=_reject_constant)
    except (ValueError, RecursionError) as exc:
        raise IdentityError("malformed migration manifest JSON") from exc
    _receipt(manifest, expected_installation_id, require_esp)
    return manifest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--expected-installation-id", required=True)
    args = parser.parse_args(argv)
    try:
        # Reject malformed/stale receipts before inspecting any block devices.
        manifest = read_claim(args.manifest, args.expected_installation_id)
        result = resolve(manifest, collect_layouts(), args.expected_installation_id)
        print(json.dumps(result, sort_keys=True))
        return 0
    except (IdentityError, OSError, ValueError, KeyError, struct.error) as exc:
        print(f"igloo-target: refused: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
