#!/usr/bin/env python3
"""FD-relative target configuration operations; never a mount/ownership authority.

The isolated broker must supply a freshly verified root device and mount ID and
exclude concurrent writers. This primitive does not substitute for that broker.
No symlink traversal, cross-mount writes, device opens, recursive removal or ESP
writes. Missing openat2 support is a hard failure.
"""
import ctypes
import errno
import hashlib
import os
from pathlib import PurePosixPath
import platform
import stat
import uuid


class Rejected(ValueError):
    pass


class OpenHow(ctypes.Structure):
    _fields_ = [("flags", ctypes.c_uint64), ("mode", ctypes.c_uint64), ("resolve", ctypes.c_uint64)]


def beneath(fd, name, flags, mode=0):
    if platform.machine() != "x86_64":
        raise Rejected("OpenAt2ArchitectureUnsupported")
    # RESOLVE_BENEATH | NO_SYMLINKS | NO_XDEV; NO_SYMLINKS includes NO_MAGICLINKS.
    how = OpenHow(flags | os.O_CLOEXEC | os.O_NOFOLLOW, mode, 0x08 | 0x04 | 0x01)
    libc = ctypes.CDLL(None, use_errno=True)
    result = libc.syscall(ctypes.c_long(437), ctypes.c_int(fd), ctypes.c_char_p(os.fsencode(name)),
                          ctypes.byref(how), ctypes.sizeof(how))
    if result < 0:
        raise OSError(ctypes.get_errno(), "TargetOpenRejected")
    return result


def mount_id(fd):
    with open(f"/proc/self/fdinfo/{fd}", encoding="ascii") as info:
        return int(next(line.split()[1] for line in info if line.startswith("mnt_id:")))


def relative(path):
    parts = path.split("/")
    if not path.startswith("/") or any(x in ("", ".", "..") for x in parts[1:]) or "\x00" in path:
        raise Rejected("UnsafeTargetPath")
    if parts[1] not in ("etc", "usr", "var"):
        raise Rejected("TargetWriteOutsideConfiguration")
    return "/".join(parts[1:])


class TargetFiles:
    def __init__(self, root_fd, expected_device, expected_mount, owner=0, group=0):
        self.fd = os.dup(root_fd)
        self.owner, self.group = owner, group
        current = os.fstat(self.fd)
        if not stat.S_ISDIR(current.st_mode) or current.st_dev != expected_device or mount_id(self.fd) != expected_mount:
            self.close()
            raise Rejected("TargetRootChanged")
        self.device, self.mount = expected_device, expected_mount

    def close(self):
        if self.fd is not None:
            os.close(self.fd)
            self.fd = None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def parent(self, path):
        name = relative(path)
        if os.fstat(self.fd).st_dev != self.device or mount_id(self.fd) != self.mount:
            raise Rejected("TargetRootChanged")
        parent, _, leaf = name.rpartition("/")
        return beneath(self.fd, parent or ".", os.O_RDONLY | os.O_DIRECTORY), leaf

    def observe(self, path):
        parent, name = self.parent(path)
        try:
            try:
                info = os.stat(name, dir_fd=parent, follow_symlinks=False)
            except FileNotFoundError:
                return {"Kind": "Absent"}
            result = {"Owner": info.st_uid, "Group": info.st_gid, "Mode": stat.S_IMODE(info.st_mode)}
            if stat.S_ISLNK(info.st_mode):
                return {**result, "Kind": "SymbolicLink", "Target": os.readlink(name, dir_fd=parent)}
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
                raise Rejected("TargetObjectNotSingleRegularFile")
            fd = beneath(parent, name, os.O_RDONLY)
            try:
                before = os.fstat(fd)
                if (before.st_dev, before.st_ino) != (info.st_dev, info.st_ino):
                    raise Rejected("TargetChangedDuringRead")
                digest = hashlib.sha256()
                while data := os.read(fd, 65536):
                    digest.update(data)
                after = os.fstat(fd)
                if (before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (after.st_size, after.st_mtime_ns, after.st_ctime_ns):
                    raise Rejected("TargetChangedDuringRead")
                return {**result, "Kind": "File", "Length": before.st_size, "Sha256": digest.hexdigest().upper()}
            finally:
                os.close(fd)
        finally:
            os.close(parent)

    def apply(self, path, kind, content, mode, expected_before):
        # The caller must retain an exact independently acquired before-state. Neither
        # missing evidence nor an arbitrary preexisting package file authorizes deletion.
        if self.observe(path) != expected_before:
            raise Rejected("TargetBeforeStateChanged")
        if kind not in ("Utf8File", "SymbolicLink", "MustBeAbsent") or mode not in (0, 0o600, 0o644, 0o755, 0o777):
            raise Rejected("UnsupportedFileOperation")
        if (kind == "Utf8File" and mode not in (0o600, 0o644, 0o755) or
                kind == "SymbolicLink" and mode != 0o777 or kind == "MustBeAbsent" and mode != 0):
            raise Rejected("UnsafeTargetMode")
        if expected_before["Kind"] == "SymbolicLink" and kind == "Utf8File":
            raise Rejected("RefuseFileOverSymlink")
        parent, name = self.parent(path)
        temporary = ".igloo-" + str(uuid.uuid4())
        try:
            if kind == "MustBeAbsent":
                if expected_before["Kind"] != "Absent":
                    os.unlink(name, dir_fd=parent)
                    os.fsync(parent)
            elif kind == "SymbolicLink":
                target = PurePosixPath(content)
                if not target.is_absolute() or ".." in target.parts or str(target).startswith(("/proc/", "/dev/", "/sys/", "/boot/")):
                    raise Rejected("UnsafeLinkTarget")
                os.symlink(content, temporary, dir_fd=parent)
                os.chown(temporary, self.owner, self.group, dir_fd=parent, follow_symlinks=False)
                os.rename(temporary, name, src_dir_fd=parent, dst_dir_fd=parent)
                os.fsync(parent)
            else:
                data = content.encode("utf-8")
                fd = beneath(parent, temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
                try:
                    view = memoryview(data)
                    while view:
                        count = os.write(fd, view)
                        if count <= 0:
                            raise OSError(errno.EIO, "ShortTargetWrite")
                        view = view[count:]
                    os.fchown(fd, self.owner, self.group)
                    os.fchmod(fd, mode)
                    os.fsync(fd)
                finally:
                    os.close(fd)
                os.rename(temporary, name, src_dir_fd=parent, dst_dir_fd=parent)
                os.fsync(parent)
        finally:
            # Do not hide a failed operation with broad cleanup. An orphaned exact
            # temporary name is evidence for recovery, not permission to retry.
            os.close(parent)
        # A fresh open, not the write handle, supplies content/mode/owner evidence.
        readback = self.observe(path)
        if kind == "MustBeAbsent":
            expected = {"Kind": "Absent"}
        elif kind == "SymbolicLink":
            expected = {"Kind": "SymbolicLink", "Target": content, "Owner": self.owner, "Group": self.group, "Mode": 0o777}
        else:
            expected = {"Kind": "File", "Length": len(data), "Sha256": hashlib.sha256(data).hexdigest().upper(),
                        "Owner": self.owner, "Group": self.group, "Mode": mode}
        if readback != expected:
            raise Rejected("TargetReadbackMismatch")
        return readback
