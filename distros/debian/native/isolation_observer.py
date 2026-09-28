"""Fresh-process, read-only kernel acquisition for a stopped command gate.

Does not enter the target namespaces or run target executables. This reports
mechanism evidence; canonical storage ownership remains the shared resolver's job.
"""
import argparse
import json
import os
from pathlib import Path
import stat


def status(pid):
    return dict(line.split(":", 1) for line in Path(f"/proc/{pid}/status").read_text().splitlines() if ":" in line)


def inode(path):
    value = os.stat(path)
    return [value.st_dev, value.st_ino]


def unescape(value):
    for code, char in (("\\040", " "), ("\\011", "\t"), ("\\012", "\n"), ("\\134", "\\")):
        value = value.replace(code, char)
    return value


def mount_records(text):
    result = []
    for line in text.splitlines():
        fields = line.split(" ")
        split = fields.index("-")
        if split < 6 or len(fields) != split + 4:
            raise ValueError("MalformedMountReadback")
        result.append({"Id": int(fields[0]), "Parent": int(fields[1]), "Device": fields[2],
                       "Root": unescape(fields[3]), "Path": unescape(fields[4]), "Options": fields[5].split(","),
                       "Propagation": fields[6:split], "FileSystem": fields[split+1],
                       "SuperOptions": fields[split+3].split(",")})
    if not result or len({m["Id"] for m in result}) != len(result):
        raise ValueError("AmbiguousMountReadback")
    return result


def namespace_ids(pid):
    return {name: os.stat(f"/proc/{pid}/ns/{name}").st_ino for name in ("mnt", "pid", "net", "ipc", "uts", "user")}


def special_files(root):
    # A socket elsewhere in the owned root could be a path into a host service.
    # Do not follow symlinks or /proc. No target code is running while this walks.
    devices, sockets = [], []
    count = 0
    for current, dirs, names in os.walk(root, followlinks=False):
        relative = os.path.relpath(current, root)
        if relative == ".":
            dirs[:] = [d for d in dirs if d not in ("proc", "sys")]
        for name in dirs + names:
            count += 1
            if count > 1000000:
                raise ValueError("RootInspectionLimitExceeded")
            path = os.path.join(current, name)
            info = os.lstat(path)
            target = "/" + os.path.relpath(path, root)
            if stat.S_ISCHR(info.st_mode) or stat.S_ISBLK(info.st_mode):
                devices.append({"Path": target, "Type": "block" if stat.S_ISBLK(info.st_mode) else "char",
                                "Major": os.major(info.st_rdev), "Minor": os.minor(info.st_rdev)})
            elif stat.S_ISSOCK(info.st_mode):
                sockets.append(target)
    return sorted(devices, key=lambda d: d["Path"]), sorted(sockets)


def observe(pid, expected_paths=None):
    before = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]
    values = status(pid)
    root = f"/proc/{pid}/root"
    paths = {path: inode(root + path) for path in ("/", "/boot/efi", "/run/igloo-source")}
    if expected_paths is not None and paths != expected_paths:
        raise ValueError("ResourceIdentityMismatchBeforeTreeInspection")
    devices, sockets = special_files(root)
    fds = []
    for entry in Path(f"/proc/{pid}/fd").iterdir():
        info = entry.stat()
        fds.append({"Number": int(entry.name), "Type": stat.S_IFMT(info.st_mode),
                    "Device": [os.major(info.st_rdev), os.minor(info.st_rdev)]})
    links = {name: os.readlink(root + "/dev/" + name) for name in ("fd", "stdin", "stdout", "stderr", "ptmx")}
    parent = int(values["PPid"])
    parent_state = status(parent)
    parent_fds = []
    for entry in Path(f"/proc/{parent}/fd").iterdir():
        info = entry.stat()
        parent_fds.append({"Number": int(entry.name), "Type": stat.S_IFMT(info.st_mode),
                           "Device": [os.major(info.st_rdev), os.minor(info.st_rdev)],
                           "AnonymousKind": os.readlink(entry) if os.readlink(entry).startswith("anon_inode:") else None})
    result = {"Pid": pid, "StartTicks": before, "State": values["State"].strip().split()[0],
              "Uid": list(map(int, values["Uid"].split())), "Gid": list(map(int, values["Gid"].split())),
              "Capabilities": {n: int(values[n].strip(), 16) for n in ("CapInh", "CapPrm", "CapEff", "CapBnd", "CapAmb")},
              "NoNewPrivs": int(values["NoNewPrivs"]), "Seccomp": int(values["Seccomp"]),
              "SeccompFilters": int(values["Seccomp_filters"]), "Namespaces": namespace_ids(pid), "Paths": paths,
              "Mounts": mount_records(Path(f"/proc/{pid}/mountinfo").read_text()),
              "Devices": devices, "Sockets": sockets, "Descriptors": sorted(fds, key=lambda f: f["Number"]),
              "DeviceLinks": links,
              "Init": {"Pid": parent, "NamespacePid": int(parent_state["NSpid"].split()[-1]),
                       "Root": inode(f"/proc/{parent}/root"), "Namespaces": namespace_ids(parent),
                       "EffectiveCapabilities": int(parent_state["CapEff"].strip(), 16), "Descriptors": parent_fds},
              "NetworkDevices": sorted(line.split(":")[0].strip() for line in
                                       Path(f"/proc/{pid}/net/dev").read_text().splitlines()[2:]),
              "SysEntries": sorted(os.listdir(root + "/sys")),
              "RunEntries": sorted(os.listdir(root + "/run"))}
    after = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]
    if before != after or status(pid)["State"].strip().split()[0] != "T":
        raise ValueError("CommandGateChangedDuringReadback")
    return result


def observe_mount_session(pid, paths):
    require_paths = type(paths) is list and 0 < len(paths) <= 16 and all(
        type(p) is str and p.startswith("/") and all(c not in ("", ".", "..") for c in p.split("/")[1:]) for p in paths)
    if not require_paths or len(set(paths)) != len(paths):
        raise ValueError("InvalidMountObservationPaths")
    root = f"/proc/{pid}/root"
    result = []
    for path in paths:
        current = root
        for component in path.split("/")[1:]:
            current += "/" + component
            if stat.S_ISLNK(os.lstat(current).st_mode):
                raise ValueError("MountDestinationSymlink")
        descriptor = os.open(current, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            info = os.fstat(descriptor)
            with open(f"/proc/self/fdinfo/{descriptor}", encoding="ascii") as source:
                mount = int(next(line.split()[1] for line in source if line.startswith("mnt_id:")))
            result.append({"Path": path, "Identity": [info.st_dev, info.st_ino], "MountId": mount,
                           "Device": f"{os.major(info.st_dev)}:{os.minor(info.st_dev)}"})
        finally:
            os.close(descriptor)
    return {"Namespace": namespace_ids(pid)["mnt"], "Paths": result,
            "Mounts": mount_records(Path(f"/proc/{pid}/mountinfo").read_text())}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("pid", type=int)
    parser.add_argument("--mount-paths")
    parser.add_argument("--expected-paths")
    options = parser.parse_args()
    print(json.dumps(observe_mount_session(options.pid, json.loads(options.mount_paths)) if options.mount_paths else
                     observe(options.pid, json.loads(options.expected_paths) if options.expected_paths else None), sort_keys=True))
