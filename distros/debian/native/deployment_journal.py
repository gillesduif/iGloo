#!/usr/bin/env python3
"""Create-new Linux deployment checkpoints with file AND directory fsync.

Only operates below a caller-provided, already existing private journal directory.
No retry/resume/delete/recovery API. A partial file/reservation is evidence to inspect.
"""
import argparse
import hashlib
import json
import os
from pathlib import PurePosixPath
import re
import stat
import sys
import uuid

LIMIT = 32 * 1024 * 1024
REFERENCE = r"[0-9]{8}-[0-9A-F]{64}\.json"


def open_directory(path):
    parts = PurePosixPath(path).parts
    if not parts or parts[0] != "/" or any(p in (".", "..") for p in parts):
        raise ValueError("Absolute private store required")
    descriptor = os.open("/", os.O_RDONLY | os.O_DIRECTORY)
    try:
        for part in parts[1:]:
            next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = next_fd
        info = os.fstat(descriptor)
        if info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) != 0o700:
            raise ValueError("Journal directory must be owner-only")
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def durable_create(directory, name, data):
    fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
    with os.fdopen(fd, "wb") as output:
        output.write(data)
        output.flush()
        os.fsync(output.fileno())
    os.fsync(directory)  # Failure remains failure, including on filesystems without this support.


def read(directory, name):
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory)
    with os.fdopen(fd, "rb") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) != 0o600:
            raise ValueError("Substituted journal artifact")
        data = source.read(LIMIT + 1)
    if not data or len(data) > LIMIT:
        raise ValueError("Invalid checkpoint size")
    return data


def perform(store, generation, plan_hash, action, data=b"", reference=None, expected_store=None):
    if str(uuid.UUID(generation)) != generation or uuid.UUID(generation).int == 0 or not re.fullmatch(r"[0-9A-F]{64}", plan_hash):
        raise ValueError("Invalid generation or plan")
    parent = open_directory(store)
    try:
        if expected_store is not None:
            info = os.fstat(parent)
            with open(f"/proc/self/fdinfo/{parent}", encoding="ascii") as source:
                mount = int(next(line.split()[1] for line in source if line.startswith("mnt_id:")))
            if [info.st_dev, info.st_ino, mount] != expected_store:
                raise ValueError("Persistent journal store substituted")
        if action == "reserve":
            os.mkdir(generation, 0o700, dir_fd=parent)  # Existing generation is never resumed.
            os.fsync(parent)
        directory = os.open(generation, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
        try:
            info = os.fstat(directory)
            if info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) != 0o700:
                raise ValueError("Substituted journal generation")
            if action == "reserve":
                durable_create(directory, "plan.sha256", plan_hash.encode("ascii"))
                return b"reserved"
            if read(directory, "plan.sha256") != plan_hash.encode("ascii"):
                raise ValueError("Changed journal plan")
            if action == "append":
                if not data or len(data) > LIMIT or json.loads(data)["PlanSha256"] != plan_hash:
                    raise ValueError("Checkpoint plan mismatch")
                entries = sorted(n for n in os.listdir(directory) if n != "plan.sha256")
                if len(entries) >= 10000 or any(not re.fullmatch(REFERENCE, name) or not name.startswith(f"{i:08d}-")
                                              for i, name in enumerate(entries)):
                    raise ValueError("Incomplete journal sequence")
                for name in entries:
                    if hashlib.sha256(read(directory, name)).hexdigest().upper() != name[9:-5]:
                        raise ValueError("Previous checkpoint corrupt")
                name = f"{len(entries):08d}-{hashlib.sha256(data).hexdigest().upper()}.json"
                durable_create(directory, name, data)
                return name.encode("ascii")
            if action != "read" or not re.fullmatch(REFERENCE, reference or ""):
                raise ValueError("Invalid journal reference")
            data = read(directory, reference)
            if hashlib.sha256(data).hexdigest().upper() != reference[9:-5]:
                raise ValueError("Checkpoint integrity mismatch")
            return data
        finally:
            os.close(directory)
    finally:
        os.close(parent)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("reserve", "append", "read"))
    parser.add_argument("--store", required=True)
    parser.add_argument("--generation", required=True)
    parser.add_argument("--plan-hash", required=True)
    parser.add_argument("--reference")
    parser.add_argument("--expected-store")
    args = parser.parse_args()
    try:
        data = sys.stdin.buffer.read(LIMIT + 1) if args.action == "append" else b""
        sys.stdout.buffer.write(perform(args.store, args.generation, args.plan_hash, args.action, data, args.reference,
                                       json.loads(args.expected_store) if args.expected_store else None))
        return 0
    except (OSError, ValueError, KeyError) as error:
        # Never report checkpoint/credential contents in diagnostics.
        print(type(error).__name__, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
