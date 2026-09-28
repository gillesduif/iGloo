#!/usr/bin/env python3
"""Read-only toolchain identity. Identity is not runtime deployment qualification."""
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def tree_identity(root):
    root = Path(root).resolve(strict=True)
    entries = []
    for path in sorted(root.rglob("*")):
        name = path.relative_to(root).as_posix()
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode):
            # Debian's suite aliases are relative links. Reject links outside this tree.
            path.resolve(strict=True).relative_to(root)
            entries.append([name, "link", os.readlink(path)])
        elif stat.S_ISREG(info.st_mode):
            entries.append([name, "file", sha(path.read_bytes())])
        elif not stat.S_ISDIR(info.st_mode):
            raise ValueError("UnexpectedToolchainObject")
    if not entries:
        raise ValueError("EmptyToolchain")
    return sha(json.dumps(entries, separators=(",", ":")).encode())


def capture(paths, package_names):
    files = []
    for name in sorted(set(paths)):
        path = Path(name).resolve(strict=True)
        if not path.is_file():
            raise ValueError("RuntimeToolNotRegular")
        files.append({"Path": name, "ResolvedPath": str(path), "Sha256": sha(path.read_bytes())})
    packages = []
    for name in sorted(set(package_names)):
        result = subprocess.run(["/usr/bin/dpkg-query", "-W", "-f=${Package}\t${Version}\t${Architecture}\t${db:Status-Status}", name],
                                stdin=subprocess.DEVNULL, capture_output=True, timeout=15, check=True)
        fields = result.stdout.decode().split("\t")
        if len(fields) != 4 or fields[3] != "installed":
            raise ValueError("RuntimePackageNotConfigured")
        packages.append({"Name": fields[0], "Version": fields[1], "Architecture": fields[2]})
    return {"SchemaVersion": 1, "Files": files, "Packages": packages}


def verify(profile):
    current = capture([f["Path"] for f in profile["Files"]], [p["Name"] for p in profile["Packages"]])
    if current != profile:
        raise ValueError("RuntimeToolchainChanged")
    return sha(json.dumps(current, separators=(",", ":")).encode())
