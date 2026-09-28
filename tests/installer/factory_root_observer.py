#!/usr/bin/env python3
"""Read-only independent factory-tree audit, including metadata v1 cannot publish.

This emits observation evidence, not an artifact, signing claim or completion flag.
No package scripts, neutralization, extraction or target session operations occur.
"""
import base64
from collections import Counter, defaultdict
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import uuid


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest().upper()


def require(condition):
    if not condition:
        raise ValueError("FactoryReadbackPrecondition")


def inventory(root):
    entries = []; links = defaultdict(list); counts = Counter(); xattrs = Counter()
    capabilities = []; privileged = []; special = []; unicode_paths = []; sparse = []
    for directory, subdirs, files in os.walk(root, followlinks=False):
        for name in sorted(subdirs + files):
            path = Path(directory) / name; relative = "/" + str(path.relative_to(root))
            info = path.lstat(); mode = stat.S_IMODE(info.st_mode)
            kind = ("Directory" if stat.S_ISDIR(info.st_mode) else "File" if stat.S_ISREG(info.st_mode) else
                    "SymbolicLink" if stat.S_ISLNK(info.st_mode) else "CharacterDevice" if stat.S_ISCHR(info.st_mode) else
                    "BlockDevice" if stat.S_ISBLK(info.st_mode) else "Fifo" if stat.S_ISFIFO(info.st_mode) else "Socket" if stat.S_ISSOCK(info.st_mode) else "Unknown")
            counts[kind] += 1
            attributes = {a: base64.b64encode(os.getxattr(path, a, follow_symlinks=False)).decode("ascii")
                          for a in sorted(os.listxattr(path, follow_symlinks=False))}
            xattrs.update(attributes.keys())
            entry = {"Path": relative, "Type": kind, "Uid": info.st_uid, "Gid": info.st_gid, "Mode": mode,
                     "Length": info.st_size if kind == "File" else 0, "Sha256": digest(path) if kind == "File" else None,
                     "Target": os.readlink(path) if kind == "SymbolicLink" else None, "Xattrs": attributes}
            entries.append(entry)
            if kind == "File" and info.st_nlink > 1:
                links[(info.st_dev, info.st_ino)].append(relative)
            if mode & 0o6000:
                privileged.append({"Path": relative, "Mode": mode, "Uid": info.st_uid, "Gid": info.st_gid})
            if any(ord(c) < 32 or ord(c) >= 127 for c in relative):
                unicode_paths.append(relative)
            if "security.capability" in attributes:
                capabilities.append({"Path": relative, "BytesBase64": attributes["security.capability"], "Mode": mode, "Uid": info.st_uid, "Gid": info.st_gid})
            if kind not in ("Directory", "File", "SymbolicLink"):
                special.append({"Path": relative, "Type": kind, "Rdev": info.st_rdev})
            if kind == "File" and info.st_size > info.st_blocks * 512:
                sparse.append({"Path": relative, "Length": info.st_size, "Allocated": info.st_blocks * 512})
    return {"Entries": sorted(entries, key=lambda e: e["Path"]), "Types": dict(counts), "XattrNames": dict(xattrs),
            "HardlinkGroups": sorted(sorted(paths) for paths in links.values()), "PrivilegedModes": privileged,
            "NonAsciiOrControlPaths": unicode_paths, "Capabilities": capabilities, "SpecialFiles": special, "SparseFiles": sparse}


def package_observation(root, package_set):
    """Read-only package inspection, also used in a fresh post-import process."""
    require(root.is_dir() and not root.is_symlink())
    admin = str(root / "var/lib/dpkg")
    command = ["/usr/bin/dpkg-query", "--admindir=" + admin, "--show",
               "--showformat=${Package}\\t${Version}\\t${Architecture}\\t${Status}\\t${Triggers-Pending}\\t${Triggers-Awaited}\\n"]
    query = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120, check=False)
    packages = []
    if query.returncode == 0:
        for line in query.stdout.decode().splitlines():
            name, version, arch, state, pending, awaited = line.split("\t")
            packages.append({"Name": name, "Version": version, "Architecture": arch, "DpkgStatus": state,
                             "TriggersPending": pending, "TriggersAwaited": awaited})
    expected = {(p["Name"], p["Version"], p["Architecture"]) for p in package_set["Packages"]}
    actual = {(p["Name"], p["Version"], p["Architecture"]) for p in packages}
    audit = subprocess.run(["/usr/bin/dpkg", "--root=" + str(root), "--audit"], stdin=subprocess.DEVNULL,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120, check=False)
    result = {"BuildId": package_set["BuildId"], "PackageQueryExit": query.returncode, "Packages": packages,
              "MissingPackages": sorted(expected - actual), "UnexpectedPackages": sorted(actual - expected),
              "UnconfiguredPackages": [p for p in packages if p["DpkgStatus"] != "install ok installed"],
              "PendingTriggers": [p["Name"] for p in packages if p["TriggersPending"] or p["TriggersAwaited"]],
              "DpkgAuditExit": audit.returncode, "DpkgAuditStdout": audit.stdout.decode(), "DpkgAuditStderr": audit.stderr.decode()}
    result["PackageStateQualified"] = (query.returncode == audit.returncode == 0 and not audit.stdout and not audit.stderr and
                                         not result["MissingPackages"] and not result["UnexpectedPackages"] and
                                         not result["UnconfiguredPackages"] and not result["PendingTriggers"])
    return result


def observe(root, package_set):
    require(root.parent == Path("/factory") and root.name == "root-" + str(uuid.UUID(package_set["BuildId"])) and root.is_dir() and not root.is_symlink())
    require(Path("/sys/class/dmi/id/sys_vendor").read_text().strip() == "QEMU" and not Path("/sys/firmware/efi").exists())
    require(sorted(p.name for p in Path("/sys/class/net").iterdir()) == ["lo"])
    result = package_observation(root, package_set)
    result["Filesystem"] = inventory(root)
    file_owners = defaultdict(list)
    for listing in (root / "var/lib/dpkg/info").glob("*.list"):
        for path in listing.read_text().splitlines():
            file_owners[path].append(listing.name[:-5])
    for cap in result["Filesystem"]["Capabilities"]:
        cap["PackageOwners"] = file_owners.get(cap["Path"], [])
    result["BootEfiEntries"] = sorted(str(p.relative_to(root)) for p in (root / "boot/efi").rglob("*"))
    result["KernelArtifacts"] = sorted((e for e in result["Filesystem"]["Entries"] if e["Path"].startswith(("/boot/", "/usr/lib/modules/")) and
                                       ("vmlinuz" in e["Path"] or "initrd" in e["Path"] or e["Path"].endswith("/modules.dep"))), key=lambda e: e["Path"])
    return result


if __name__ == "__main__":
    source = json.loads(Path("/input/bundle/package-set.json").read_bytes())
    result = observe(Path("/factory") / ("root-" + source["BuildId"]), source)
    output = Path("/factory-evidence") / source["BuildId"] / "independent-root-audit.json"
    with output.open("x") as stream:
        json.dump(result, stream, sort_keys=True, separators=(",", ":")); stream.flush(); os.fsync(stream.fileno())
    print(json.dumps({"ReadbackSha256": digest(output), "PackageStateQualified": result["PackageStateQualified"],
                      "Packages": len(result["Packages"]), "Types": result["Filesystem"]["Types"],
                      "Capabilities": result["Filesystem"]["Capabilities"], "XattrNames": result["Filesystem"]["XattrNames"],
                      "NonAsciiOrControlPaths": len(result["Filesystem"]["NonAsciiOrControlPaths"]),
                      "SpecialFiles": result["Filesystem"]["SpecialFiles"], "PendingTriggers": result["PendingTriggers"]}), flush=True)
