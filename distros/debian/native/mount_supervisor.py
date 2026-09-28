"""FD-anchored native mount mechanics for a trusted private-namespace session.

The caller must supply the shared canonical resolver as `revalidate`; this module
does NOT infer ownership from stat, paths, filesystem type or a caller boolean.
block_session.py composes this callback through the private canonical .NET pipe.
Real block qualification is still outstanding; tests use tmpfs, never physical
blocks. No format/partition/reboot/firmware or lazy/force unmount API.
"""
import ctypes
from dataclasses import dataclass
import fcntl
import json
import os
from pathlib import Path
import platform
import stat
import subprocess
import uuid

from isolation_policy import ENVIRONMENT, Rejected, canonical, digest, require, sha
from isolation_observer import mount_records, namespace_ids
from target_files import mount_id


@dataclass(frozen=True)
class BlockLease:
    generation: str
    plan_hash: str
    role: str
    descriptor: int
    major: int
    minor: int
    canonical_hash: str


class LinuxMountCalls:
    def __init__(self):
        require(platform.machine() == "x86_64", "MountArchitectureUnsupported")
        self.libc = ctypes.CDLL(None, use_errno=True)
        self.libc.syscall.restype = ctypes.c_long
        self.libc.umount2.argtypes = [ctypes.c_char_p, ctypes.c_int]
        self.libc.umount2.restype = ctypes.c_int

    def call(self, number, *args):
        result = self.libc.syscall(ctypes.c_long(number), *args)
        if result < 0:
            raise OSError(ctypes.get_errno(), "ExactMountSystemCallFailed")
        return result

    def detached(self, filesystem, source, readonly):
        context = self.call(430, ctypes.c_char_p(filesystem.encode()), ctypes.c_uint(1))  # fsopen CLOEXEC
        try:
            if source is not None:
                self.call(431, ctypes.c_int(context), ctypes.c_uint(1), ctypes.c_char_p(b"source"),
                          ctypes.c_char_p(f"/proc/self/fd/{source}".encode()), ctypes.c_int(0))
            if readonly:
                self.call(431, ctypes.c_int(context), ctypes.c_uint(0), ctypes.c_char_p(b"ro"), ctypes.c_void_p(), ctypes.c_int(0))
            if filesystem == "tmpfs":
                for key, value in ((b"size", b"16777216"), (b"mode", b"0700")):
                    self.call(431, ctypes.c_int(context), ctypes.c_uint(1), ctypes.c_char_p(key), ctypes.c_char_p(value), ctypes.c_int(0))
            self.call(431, ctypes.c_int(context), ctypes.c_uint(6), ctypes.c_void_p(), ctypes.c_void_p(), ctypes.c_int(0))
            # Per-mount NOSUID|NODEV always. No executables on ESP/payload/scratch.
            attributes = 2 | 4 | (1 if readonly else 0) | (0 if filesystem == "ext4" else 8)
            return self.call(432, ctypes.c_int(context), ctypes.c_uint(1), ctypes.c_uint(attributes))
        finally:
            os.close(context)

    def attach(self, detached, destination):
        self.call(429, ctypes.c_int(detached), ctypes.c_char_p(b""), ctypes.c_int(destination),
                  ctypes.c_char_p(b""), ctypes.c_uint(0x04 | 0x40))  # move_mount F/T_EMPTY_PATH

    def unmount(self, parent_descriptor, name):
        # The observed mount descriptor must first close or it keeps the mount
        # busy. Pin its parent; prohibit a final symlink. The session is single-
        # writer and no package process may run during teardown.
        if self.libc.umount2(f"/proc/self/fd/{parent_descriptor}/{name}".encode(), 8) < 0:  # UMOUNT_NOFOLLOW
            raise OSError(ctypes.get_errno(), "ExactUnmountFailed")


class ExactMountSupervisor:
    def __init__(self, generation, plan_hash, parent_namespace, runtime, revalidate, checkpoint, calls=None):
        require(str(uuid.UUID(generation)) == generation and uuid.UUID(generation).int and sha(plan_hash), "InvalidMountSession")
        require(namespace_ids("self")["mnt"] != parent_namespace, "MountSessionNotPrivate")
        require(callable(revalidate) and callable(checkpoint), "MountSessionBoundariesRequired")
        self.generation, self.plan_hash = generation, plan_hash
        self.namespace = namespace_ids("self")["mnt"]
        self.runtime, self.revalidate, self.checkpoint = runtime, revalidate, checkpoint
        self.calls = calls if calls is not None else LinuxMountCalls()
        self.receipts = []
        self.poisoned = False
        self.runtime.verify()
        self._private()

    def _private(self):
        require(not self.poisoned and namespace_ids("self")["mnt"] == self.namespace, "MountSessionStoppedOrChanged")
        require(all(not m["Propagation"] for m in mount_records(Path("/proc/self/mountinfo").read_text())), "MountPropagationNotPrivate")

    def _observe(self, path):
        self.runtime.verify()
        result = subprocess.run([self.runtime.python, "-I", self.runtime.observer, str(os.getpid()),
                                 "--mount-paths", json.dumps([path])], stdin=subprocess.DEVNULL,
                                capture_output=True, env=ENVIRONMENT, timeout=30, check=False)
        require(result.returncode == 0 and len(result.stdout) <= 8 * 1024 * 1024, "MountObserverUnavailable")
        value = json.loads(result.stdout)
        require(value["Namespace"] == self.namespace and all(not m["Propagation"] for m in value["Mounts"]), "MountNamespaceChanged")
        return value

    def _source(self, lease):
        require(lease.generation == self.generation and lease.plan_hash == self.plan_hash and sha(lease.canonical_hash), "BlockLeaseBindingChanged")
        require(lease.role in ("Root", "LinuxEsp", "Payload"), "UnexpectedMountRole")
        info = os.fstat(lease.descriptor)
        require(stat.S_ISBLK(info.st_mode) and (os.major(info.st_rdev), os.minor(info.st_rdev)) ==
                (lease.major, lease.minor), "BlockDescriptorChanged")
        flags = fcntl.fcntl(lease.descriptor, fcntl.F_GETFL)
        require((flags & os.O_ACCMODE) == (os.O_RDWR if lease.role == "Root" else os.O_RDONLY), "BlockAccessModeChanged")
        # Only the existing shared resolver can produce this fresh witness. A stat
        # device number is never a substitute for generation/GPT/UUID/geometry.
        require(self.revalidate(lease) == lease.canonical_hash, "CanonicalSourceChanged")
        return info

    def mount_block(self, lease, destination):
        self._private()
        self._source(lease)
        root = "/run/igloo/target/" + self.generation
        expected = {"Root": root, "LinuxEsp": root + "/boot/efi", "Payload": "/run/igloo/source/" + self.generation}
        require(destination.path == expected[lease.role], "UndeclaredMountDestination")
        require(lease.role == "Root" or any(r["Role"] == "Root" for r in self.receipts), "RootMustMountFirst")
        require(not any(r["Role"] == lease.role for r in self.receipts), "DuplicateOwnedMount")
        require(not any(m["Device"] == f"{lease.major}:{lease.minor}" for m in
                        mount_records(Path("/proc/self/mountinfo").read_text())), "SourceAlreadyMounted")
        return self._mount(lease.role, destination, "ext4" if lease.role == "Root" else "vfat",
                           lease.descriptor, lease.role != "Root", lease)

    def mount_scratch(self, destination):
        # Bounded helper tmpfs. It is never reported as an owned root/ESP/payload.
        return self._mount("Scratch", destination, "tmpfs", None, False, None)

    def _record(self, state, action, role, path, before, after=None):
        record = {"Generation": self.generation, "PlanSha256": self.plan_hash, "State": state,
                  "Action": action, "Role": role, "Path": path, "Before": before, "After": after}
        # Trusted stage adapter must fsync and independently reopen. Failure poisons
        # the session even if the syscall has already attached/unmounted a filesystem.
        self.checkpoint(record)
        return record

    def _mount(self, role, destination, filesystem, source, readonly, lease):
        self._private()
        destination.verify()
        before = self._observe(destination.path)
        require(not any(m["Path"] == destination.path for m in before["Mounts"]), "MountDestinationOccupied")
        detached = None
        parent_fd = None
        mounted_fd = None
        try:
            self._record("IntentDurable", "Mount", role, destination.path, before)
            destination.verify()
            if lease is not None:
                self._source(lease)
            detached = self.calls.detached(filesystem, source, readonly)
            parent_path = str(Path(destination.path).parent)
            require(os.path.realpath(parent_path, strict=True) == parent_path, "MountParentSubstituted")
            parent_fd = os.open(parent_path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            self.calls.attach(detached, destination.fd)
            after = self._observe(destination.path)
            paths = after["Paths"]
            selected = [m for m in after["Mounts"] if m["Path"] == destination.path]
            require(len(paths) == len(selected) == 1 and paths[0]["MountId"] == selected[0]["Id"] and
                    selected[0]["FileSystem"] == filesystem and selected[0]["Root"] == "/" and
                    {"nosuid", "nodev", "ro" if readonly else "rw"} <= set(selected[0]["Options"]), "MountedFilesystemMismatch")
            if lease is not None:
                require(selected[0]["Device"] == f"{lease.major}:{lease.minor}", "MountedDeviceMismatch")
                self._source(lease)
            before_ids = {m["Id"] for m in before["Mounts"]}
            require([m for m in after["Mounts"] if m["Id"] in before_ids] == before["Mounts"] and
                    len(after["Mounts"]) == len(before["Mounts"]) + 1, "UnexpectedMountEffect")
            receipt = self._record("AppliedAndVerified", "Mount", role, destination.path, before, after)
            mounted_fd = os.open(destination.path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
            require(mount_id(mounted_fd) == selected[0]["Id"], "MountedDescriptorChanged")
            receipt = {**receipt, "MountId": selected[0]["Id"], "Descriptor": mounted_fd, "Identity": paths[0]["Identity"],
                       "ParentDescriptor": parent_fd, "Name": Path(destination.path).name, "Lease": lease}
            self.receipts.append(receipt)
            parent_fd = mounted_fd = None  # Session owns the retained descriptors.
            return receipt
        except (OSError, ValueError, subprocess.TimeoutExpired):
            self.poisoned = True
            # A durable prior intent survives. Do not widen cleanup after failure.
            raise
        finally:
            if detached is not None:
                os.close(detached)
            if parent_fd is not None:
                os.close(parent_fd)
            if mounted_fd is not None:
                os.close(mounted_fd)

    def unmount_next(self):
        self._private()
        require(self.receipts, "NoOwnedMountToRemove")
        # Declared dependency order, not discovery order. Scratch helpers first.
        rank = {"Scratch": 0, "LinuxEsp": 1, "Payload": 2, "Root": 3}
        receipt = min(reversed(self.receipts), key=lambda r: rank[r["Role"]])
        if receipt["Lease"] is not None:
            self._source(receipt["Lease"])
        before = self._observe(receipt["Path"])
        matches = [m for m in before["Mounts"] if m["Id"] == receipt["MountId"]]
        require(len(matches) == 1 and matches[0]["Path"] == receipt["Path"] and
                before["Paths"][0]["Identity"] == receipt["Identity"] and
                before["Paths"][0]["MountId"] == receipt["MountId"] and
                mount_id(receipt["Descriptor"]) == receipt["MountId"], "OwnedMountChangedBeforeUnmount")
        require(not any(m["Path"].startswith(receipt["Path"] + "/") for m in before["Mounts"]), "UnmountHasDependentMount")
        try:
            self._record("IntentDurable", "Unmount", receipt["Role"], receipt["Path"], before)
            if receipt["Lease"] is not None:
                self._source(receipt["Lease"])
            require(self._observe(receipt["Path"]) == before, "MountChangedAfterUnmountIntent")
            os.close(receipt["Descriptor"])
            receipt["Descriptor"] = None
            self.calls.unmount(receipt["ParentDescriptor"], receipt["Name"])
            after = self._observe(receipt["Path"])
            if receipt["Lease"] is not None:
                self._source(receipt["Lease"])
            require(after["Mounts"] == [m for m in before["Mounts"] if m["Id"] != receipt["MountId"]] and
                    all(m["Path"] != receipt["Path"] for m in after["Mounts"]), "UnmountNotVerified")
            result = self._record("AppliedAndVerified", "Unmount", receipt["Role"], receipt["Path"], before, after)
            os.close(receipt["ParentDescriptor"])
            self.receipts.remove(receipt)
            return result
        except (OSError, ValueError, subprocess.TimeoutExpired):
            self.poisoned = True
            raise
