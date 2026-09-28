#!/usr/bin/env python3
"""Independent readback primitives. No deployment commands or side effects.

Caller still needs the shared canonical inventory/mount resolver and effective
isolation proof. File evidence is never authority to select a disk or partition.
"""
import hashlib
import os
import re
import stat
import subprocess


def package_state(administration_fd):
    # The directory FD must have been opened beneath the pinned root without
    # symlinks/mount crossings. dpkg-query reads the database; it runs no scripts.
    result = subprocess.run(["/usr/bin/dpkg-query", "--admindir=/proc/self/fd/" + str(administration_fd),
                             "--show", "--showformat=${Package}\t${Version}\t${Architecture}\t${Status}\n"],
                            pass_fds=(administration_fd,), stdin=subprocess.DEVNULL, capture_output=True,
                            env={"PATH": "/usr/bin:/bin", "LC_ALL": "C"}, timeout=30, check=False)
    if result.returncode != 0 or len(result.stdout) > 16 * 1024 * 1024:
        raise OSError("IndependentDpkgReadUnavailable")
    packages = []
    for line in result.stdout.decode().splitlines():
        values = line.split("\t")
        if len(values) != 4 or not re.fullmatch(r"[a-z0-9][a-z0-9+.-]+", values[0]):
            raise ValueError("MalformedDpkgReadback")
        packages.append(dict(zip(("Name", "Version", "Architecture", "DpkgStatus"), values)))
    if len({p["Name"] for p in packages}) != len(packages):
        raise ValueError("AmbiguousDpkgReadback")
    return sorted(packages, key=lambda p: p["Name"])


def artifact(directory_fd, name, max_length=1024 * 1024 * 1024):
    if not re.fullmatch(r"[A-Za-z0-9_.+-]+", name) or name in (".", ".."):
        raise ValueError("UnsafeArtifactName")
    descriptor = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=directory_fd)
    try:
        before = os.fstat(descriptor)
        if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or not 0 < before.st_size <= max_length:
            raise ValueError("InvalidBootArtifact")
        sha = hashlib.sha256()
        while block := os.read(descriptor, 65536):
            sha.update(block)
        after = os.fstat(descriptor)
        if (before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (after.st_size, after.st_mtime_ns, after.st_ctime_ns):
            raise ValueError("ArtifactChangedDuringRead")
        return {"Length": before.st_size, "Sha256": sha.hexdigest().upper(), "RegularFile": True,
                "ContainsSymlink": False, "Owner": before.st_uid, "Group": before.st_gid, "Mode": stat.S_IMODE(before.st_mode)}
    finally:
        os.close(descriptor)


def mounts(device_paths, target_paths):
    # stat joins ephemeral device numbers to canonical inventory in the .NET resolver.
    # Never infer UUID/ownership from mount source text or a transient /dev name here.
    numbers = []
    for path in device_paths:
        info = os.stat(path, follow_symlinks=False)
        if not stat.S_ISBLK(info.st_mode):
            raise ValueError("RuntimeDeviceNotBlock")
        numbers.append({"DevicePath": path, "Major": os.major(info.st_rdev), "Minor": os.minor(info.st_rdev)})
    paths = []
    for path in target_paths:
        resolved = os.path.realpath(path, strict=True)
        paths.append({"RequestedPath": path, "ResolvedPath": resolved,
                      "IsDirectory": stat.S_ISDIR(os.stat(path).st_mode), "ContainsSymlink": resolved != path})
    with open("/proc/self/mountinfo", encoding="utf-8") as source:
        raw = source.read()
    return {"MountInfo": raw, "DeviceNumbers": numbers, "Paths": paths}
