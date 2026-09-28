"""Closed package/configuration launch policy. No bootstrap/firmware profile.

Only amd64 Linux is supported. libseccomp supplies ABI-correct syscall numbers;
unknown syscalls/library/platform fail before starting a command. This is a deny
layer over namespace, nodev mount and capability restrictions, not a standalone
sandbox. Runtime library/tool hashes must also be qualified by the caller.
"""
import ctypes
import errno
import fcntl
import hashlib
import json
import os
import platform
import re
from dataclasses import dataclass
from enum import Enum
import uuid


class Rejected(ValueError):
    pass


class Profile(str, Enum):
    PACKAGE = "Package"
    CONFIGURATION = "Configuration"
    OBSERVER = "Observer"


CAPS = {
    Profile.PACKAGE: (0, 1, 3, 4, 6, 7, 31),
    Profile.CONFIGURATION: (0, 1, 3, 4, 6, 7),
    Profile.OBSERVER: (),
}
CAP_NAMES = {0: "CAP_CHOWN", 1: "CAP_DAC_OVERRIDE", 3: "CAP_FOWNER", 4: "CAP_FSETID",
             6: "CAP_SETGID", 7: "CAP_SETUID", 31: "CAP_SETFCAP"}
ENVIRONMENT = {"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C", "HOME": "/root",
               "DEBIAN_FRONTEND": "noninteractive", "DEBCONF_NONINTERACTIVE_SEEN": "true"}
STAGES = {
    Profile.PACKAGE: frozenset(("ConfigurePackagePolicy", "InstallKernel", "InstallFirmware", "InstallDesktop")),
    Profile.CONFIGURATION: frozenset(("ConfigureLocale", "ConfigureUser", "ConfigureSudo", "GenerateInitramfs")),
    Profile.OBSERVER: frozenset(("InspectPackageState", "InspectArtifacts")),
}
# Deny old and new mount APIs, device creation, namespace creation/entry, kernel
# controls, alternative raw-I/O interfaces and cross-process descriptor access.
DENIED = (
    "mount", "umount2", "pivot_root", "chroot", "fsopen", "fsconfig", "fsmount",
    "fspick", "open_tree", "move_mount", "mount_setattr", "swapon", "swapoff",
    "reboot", "kexec_load", "kexec_file_load", "init_module", "finit_module", "delete_module",
    "iopl", "ioperm", "mknod", "mknodat", "unshare", "setns", "open_by_handle_at",
    "name_to_handle_at", "ptrace", "process_vm_writev", "process_vm_readv", "pidfd_getfd",
    "bpf", "perf_event_open", "userfaultfd", "io_uring_setup", "io_uring_enter", "io_uring_register",
    "keyctl", "add_key", "request_key", "acct", "quotactl", "quotactl_fd",
    "settimeofday", "clock_settime", "clock_adjtime", "adjtimex", "sethostname", "setdomainname",
)
NAMESPACE_FLAGS = 0x00000080 | 0x00020000 | 0x02000000 | 0x04000000 | 0x08000000 | 0x10000000 | 0x20000000 | 0x40000000


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def require(value, code):
    if not value:
        raise Rejected(code)


def sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9A-F]{64}", value) is not None


@dataclass(frozen=True)
class Launch:
    generation: str
    plan_hash: str
    stage: str
    profile: Profile
    executable: str
    tool_hash: str
    arguments: tuple
    timeout_seconds: int = 300
    input_kind: str = "None"

    def validate(self):
        require(str(uuid.UUID(self.generation)) == self.generation and uuid.UUID(self.generation).int != 0, "InvalidGeneration")
        require(sha(self.plan_hash) and sha(self.tool_hash), "InvalidLaunchHash")
        require(self.profile in STAGES and self.stage in STAGES[self.profile], "StageProfileUnsupported")
        require(re.fullmatch(r"/(usr/(s?bin)|s?bin)/[A-Za-z0-9_.+-]+", self.executable) is not None,
                "InvalidExecutablePath")
        require(type(self.arguments) is tuple and len(self.arguments) <= 8192 and
                all(type(a) is str and "\x00" not in a and len(a) <= 4096 for a in self.arguments), "InvalidArgv")
        require(type(self.timeout_seconds) is int and 1 <= self.timeout_seconds <= 7200, "InvalidTimeout")
        require(self.input_kind in ("None", "EncryptedPassword", "PublicDebconf"), "InvalidInputContract")
        require(self.input_kind != "EncryptedPassword" or
                (self.stage, self.executable, self.arguments) == ("ConfigureUser", "/usr/sbin/chpasswd", ("--encrypted",)),
                "CredentialCommandMismatch")
        require(self.input_kind != "PublicDebconf" or
                (self.stage, self.executable, self.arguments) == ("ConfigurePackagePolicy", "/usr/bin/debconf-set-selections", ()),
                "DebconfCommandMismatch")
        return self

    def public_identity(self):
        self.validate()
        # Public argv identifies intent, but is not copied into command receipts.
        return digest(canonical({"Generation": self.generation, "Plan": self.plan_hash, "Stage": self.stage,
                                 "Profile": self.profile.value, "Executable": self.executable, "Tool": self.tool_hash,
                                 "Argv": self.arguments, "Input": self.input_kind, "Timeout": self.timeout_seconds}))


class Comparison(ctypes.Structure):
    _fields_ = [("arg", ctypes.c_uint), ("op", ctypes.c_uint),
                ("datum_a", ctypes.c_uint64), ("datum_b", ctypes.c_uint64)]


def seccomp_descriptor():
    require(platform.machine() == "x86_64", "SeccompArchitectureUnsupported")
    library = ctypes.CDLL("libseccomp.so.2", use_errno=True)
    library.seccomp_init.argtypes = [ctypes.c_uint32]
    library.seccomp_init.restype = ctypes.c_void_p
    library.seccomp_syscall_resolve_name.argtypes = [ctypes.c_char_p]
    library.seccomp_syscall_resolve_name.restype = ctypes.c_int
    library.seccomp_rule_add_array.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_int,
                                               ctypes.c_uint, ctypes.POINTER(Comparison)]
    library.seccomp_rule_add_array.restype = ctypes.c_int
    library.seccomp_export_bpf.argtypes = [ctypes.c_void_p, ctypes.c_int]
    library.seccomp_export_bpf.restype = ctypes.c_int
    library.seccomp_release.argtypes = [ctypes.c_void_p]
    context = library.seccomp_init(0x7fff0000)  # ALLOW; unknown ABI is killed by libseccomp.
    require(context, "SeccompContextUnavailable")
    descriptor = os.memfd_create("igloo-seccomp", os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING)
    try:
        def rule(name, error=errno.EPERM, comparison=None):
            number = library.seccomp_syscall_resolve_name(name.encode())
            require(number >= 0, "SeccompSyscallUnsupported:" + name)
            require(library.seccomp_rule_add_array(context, 0x00050000 | error, number,
                                                  int(comparison is not None), ctypes.byref(comparison) if comparison else None) == 0,
                    "SeccompRuleRejected:" + name)
        for name in DENIED:
            rule(name)
        # glibc falls back to clone when clone3 is ENOSYS. Pointer arguments of
        # clone3 cannot be safely inspected by classic seccomp.
        rule("clone3", errno.ENOSYS)
        for bit in (1 << n for n in range(32) if NAMESPACE_FLAGS & (1 << n)):
            rule("clone", comparison=Comparison(0, 7, bit, bit))  # MASKED_EQ
        # UNIX sockets are confined by both filesystem and network namespaces.
        rule("socket", comparison=Comparison(0, 1, 1, 0))  # NE AF_UNIX
        rule("socketpair", comparison=Comparison(0, 1, 1, 0))
        # TIOCSTI / TIOCLINUX, even if a controlling terminal was mistakenly supplied.
        for request in (0x5412, 0x541c):
            rule("ioctl", comparison=Comparison(1, 4, request, 0))
        require(library.seccomp_export_bpf(context, descriptor) == 0, "SeccompExportFailed")
        os.lseek(descriptor, 0, os.SEEK_SET)
        fcntl.fcntl(descriptor, fcntl.F_ADD_SEALS, fcntl.F_SEAL_SEAL | fcntl.F_SEAL_SHRINK | fcntl.F_SEAL_GROW | fcntl.F_SEAL_WRITE)
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise
    finally:
        library.seccomp_release(context)
