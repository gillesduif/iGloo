"""Candidate package child boundary using the pinned Debian bubblewrap runtime.

Consumes already verified mounted-resource leases, not disk paths or labels.
The privileged canonical mount/session adapter is a separate boundary. This is
NOT registered as IDebianIsolatedStageHost or a supported Bootstrap implementation.
No arbitrary shell strings, host /dev binds, inherited environment or local fallback.
"""
import fcntl
import json
import os
from pathlib import Path
import signal
import stat
import subprocess
import time
from dataclasses import dataclass

from isolation_policy import CAPS, CAP_NAMES, ENVIRONMENT, Launch, Profile, Rejected, canonical, digest, require, seccomp_descriptor, sha
from isolation_observer import inode, mount_records, namespace_ids, status
from target_files import beneath, mount_id


@dataclass(frozen=True)
class Runtime:
    bubblewrap: str
    bubblewrap_hash: str
    gate: str
    gate_hash: str
    python: str
    python_hash: str
    observer: str
    observer_hash: str

    def verify(self):
        for path, expected in ((self.bubblewrap, self.bubblewrap_hash), (self.gate, self.gate_hash),
                               (self.python, self.python_hash), (self.observer, self.observer_hash)):
            require(sha(expected) and Path(path).is_absolute(), "InvalidRuntimeIdentity")
            info = os.stat(path, follow_symlinks=False)
            require(stat.S_ISREG(info.st_mode) and info.st_uid == 0 and not info.st_mode & 0o6022, "UnsafeRuntimeTool")
            require(os.path.realpath(path, strict=True) == path, "RuntimeToolSymlink")
            for parent in Path(path).parents:
                current = parent.stat()
                require(current.st_uid == 0 and (not current.st_mode & 0o022 or current.st_mode & stat.S_ISVTX), "UnprotectedRuntimeDirectory")
            require(digest(Path(path).read_bytes()) == expected, "RuntimeToolChanged")


class DirectoryLease:
    """FD pin plus named-path revalidation. Does not certify GPT identity itself."""
    def __init__(self, path):
        require(type(path) is str and os.path.isabs(path) and os.path.realpath(path, strict=True) == path, "UnsafeResourcePath")
        self.path = path
        self.fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        self.identity = inode(path)
        self.mount = mount_id(self.fd)
        self.verify()

    def verify(self):
        info = os.fstat(self.fd)
        require(self.identity == [info.st_dev, info.st_ino] == inode(self.path) and
                os.path.realpath(self.path, strict=True) == self.path and mount_id(self.fd) == self.mount, "ResourceLeaseChanged")
        # A detached clone can have the same inode/device as a connected directory.
        # It must not be handed to bwrap's source-path expansion (previous WSL-root incident).
        require(os.readlink(f"/proc/self/fd/{self.fd}") == self.path and any(
            m["Id"] == self.mount for m in mount_records(Path("/proc/self/mountinfo").read_text())), "DetachedResourceDescriptor")

    def close(self):
        if self.fd is not None:
            os.close(self.fd)
            self.fd = None


def sealed_copy(data, name):
    fd = os.memfd_create(name, os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING)
    try:
        with os.fdopen(os.dup(fd), "wb") as out:
            out.write(data)
        os.fchmod(fd, 0o555)
        fcntl.fcntl(fd, fcntl.F_ADD_SEALS, 15)
        os.lseek(fd, 0, os.SEEK_SET)
        return fd
    except BaseException:
        os.close(fd)
        raise


def verify_effective(observed, host_namespaces, resources, profile):
    """Checks actual kernel evidence, not requested bwrap flags or a status boolean."""
    expected_caps = sum(1 << c for c in CAPS[profile])
    require(observed["State"] == "T" and observed["Uid"] == [0]*4 and observed["Gid"] == [0]*4, "GateIdentityChanged")
    require(all(value == expected_caps for value in observed["Capabilities"].values()), "CapabilityPolicyNotEffective")
    require(observed["NoNewPrivs"] == 1 and observed["Seccomp"] == 2 and observed["SeccompFilters"] >= 1, "SyscallPolicyNotEffective")
    require(all(observed["Namespaces"][n] != host_namespaces[n] for n in ("mnt", "pid", "net", "ipc", "uts")), "NamespaceNotIsolated")
    require(observed["NetworkDevices"] == ["lo"] and not observed["SysEntries"], "NetworkOrFirmwareExposed")
    require(observed["RunEntries"] == ["igloo-gate", "igloo-source"], "UnexpectedRuntimeExposure")
    require(not observed["Sockets"], "ServiceSocketExposed")
    require(observed["Paths"] == {"/": resources["root"].identity, "/boot/efi": resources["esp"].identity,
                                  "/run/igloo-source": resources["payload"].identity}, "MountedResourceSubstituted")
    init = observed["Init"]
    require(init["NamespacePid"] == 1 and init["Root"] == resources["root"].identity and
            init["Namespaces"] == observed["Namespaces"] and init["EffectiveCapabilities"] == 0, "NamespaceInitExposed")
    require(all(d["AnonymousKind"] in ("anon_inode:[eventfd]", "anon_inode:[signalfd]") or
                (d["Number"] <= 2 and (d["Type"] in (stat.S_IFREG, stat.S_IFIFO) or
                d["Type"] == stat.S_IFCHR and d["Device"] == [1, 3])) for d in init["Descriptors"]), "NamespaceInitDescriptorExposed")
    descriptors = observed["Descriptors"]
    require([d["Number"] for d in descriptors] == [0, 1, 2] and all(
        d["Type"] == stat.S_IFIFO or d["Type"] == stat.S_IFREG or
        (d["Type"] == stat.S_IFCHR and d["Device"] == [1, 3]) for d in descriptors), "SetupDescriptorLeaked")
    allowed = {"/dev/null": (1, 3), "/dev/zero": (1, 5), "/dev/full": (1, 7), "/dev/random": (1, 8),
               "/dev/urandom": (1, 9), "/dev/tty": (5, 0), "/dev/pts/ptmx": (5, 2)}
    devices = observed["Devices"]
    require(len(devices) == len(allowed) and all(d["Type"] == "char" and
            allowed.get(d["Path"]) == (d["Major"], d["Minor"]) for d in devices), "UnexpectedRawDevice")
    require(observed["DeviceLinks"] == {"fd": "/proc/self/fd", "stdin": "/proc/self/fd/0", "stdout": "/proc/self/fd/1",
                                       "stderr": "/proc/self/fd/2", "ptmx": "pts/ptmx"}, "DeviceLinkChanged")
    mounts = observed["Mounts"]
    require(len({m["Path"] for m in mounts}) == len(mounts) and all(not m["Propagation"] for m in mounts), "StackedOrPropagatingMount")
    basic = {"/", "/boot/efi", "/dev", "/dev/pts", "/proc", "/sys", "/run", "/run/igloo-source", "/run/igloo-gate", "/tmp"}
    proc_masks = {"/proc/sys", "/proc/sysrq-trigger", "/proc/irq", "/proc/bus"}
    # The tool overlay is validated by the caller with the sealed expected bytes.
    tool_mounts = [m for m in mounts if m["Path"].startswith(("/usr/bin/", "/usr/sbin/", "/bin/", "/sbin/"))]
    require(len(tool_mounts) == 1 and "ro" in tool_mounts[0]["Options"], "ToolMountChanged")
    require({m["Path"] for m in mounts} <= basic | proc_masks | set(allowed) | {tool_mounts[0]["Path"]}, "UnexpectedMount")
    for path, mode in (("/", "ro" if profile == Profile.OBSERVER else "rw"), ("/boot/efi", "ro"),
                       ("/run/igloo-source", "ro"), ("/sys", "ro"), ("/proc", "ro")):
        matches = [m for m in mounts if m["Path"] == path]
        require(len(matches) == 1 and mode in matches[0]["Options"], "MountAccessChanged")
    for mount in mounts:
        require("nosuid" in mount["Options"], "MountAllowsSetId")
        if mount["Path"] not in allowed and mount["Path"] != "/dev/pts":
            require("nodev" in mount["Options"], "MountAllowsRawDevices")


class PackageBroker:
    def __init__(self, runtime):
        self.runtime = runtime

    def execute(self, launch: Launch, resources, durable_intent, *, input_fd=None):
        # Serialize cooperating broker sessions. This cannot defend against a
        # compromised privileged host; that is outside the package threat model.
        locks = []
        try:
            require(set(resources) == {"root", "esp", "payload"}, "ResourceSetChanged")
            for name in sorted(resources):
                resource = resources[name]
                resource.verify()
                fcntl.flock(resource.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                locks.append(resource.fd)
            return self._execute(launch, resources, durable_intent, input_fd=input_fd)
        finally:
            for fd in reversed(locks):
                fcntl.flock(fd, fcntl.LOCK_UN)

    def _execute(self, launch: Launch, resources, durable_intent, *, input_fd=None):
        """Called by a trusted session after canonical inventory/mount validation.

        durable_intent must fsync/reopen the stage intent; it receives only hashes.
        This returns command evidence, never AppliedAndVerified stage evidence.
        No automatic retry, cleanup, observer success or production stage promotion.
        """
        launch.validate()
        require(os.geteuid() == 0, "PrivilegedMountSupervisorRequired")
        require(set(resources) == {"root", "esp", "payload"}, "ResourceSetChanged")
        require(len({tuple(r.identity) for r in resources.values()}) == 3, "AliasedResources")
        require(callable(durable_intent), "DurableIntentRequired")
        self.runtime.verify()
        for resource in resources.values():
            resource.verify()
        require((launch.input_kind == "None") == (input_fd is None), "InputContractMismatch")
        if input_fd is not None:
            require(fcntl.fcntl(input_fd, fcntl.F_GET_SEALS) & 15 == 15, "InputMustBeSealed")
        # No child mount is imported recursively. Integration with the supervisor
        # must supply a root-only view; a detached open_tree FD is NOT a supported
        # bwrap source because its path resolution can lose the subtree root.
        root_path = resources["root"].path
        require(not any(m["Path"].startswith(root_path + "/") for m in mount_records(Path("/proc/self/mountinfo").read_text())),
                "RootContainsUnleasedMount")
        tool_fd = beneath(resources["root"].fd, launch.executable[1:], os.O_RDONLY)
        temporary = []
        process = None
        gate_pidfd = None
        command_started = False
        observation = None
        try:
            with os.fdopen(tool_fd, "rb") as tool:
                info = os.fstat(tool.fileno())
                require(stat.S_ISREG(info.st_mode) and not info.st_mode & 0o6022, "UnsafeTargetTool")
                content = tool.read(64 * 1024 * 1024 + 1)
            require(len(content) <= 64 * 1024 * 1024 and digest(content) == launch.tool_hash, "TargetToolChanged")
            tool_copy = sealed_copy(content, "igloo-tool"); temporary.append(tool_copy)
            gate_copy = sealed_copy(Path(self.runtime.gate).read_bytes(), "igloo-gate"); temporary.append(gate_copy)
            seccomp_fd = seccomp_descriptor(); temporary.append(seccomp_fd)
            filter_hash = digest(os.pread(seccomp_fd, os.fstat(seccomp_fd).st_size, 0))
            args = [self.runtime.gate, "--supervise", self.runtime.bubblewrap, "--unshare-pid", "--unshare-net", "--unshare-ipc", "--unshare-uts",
                    "--new-session", "--die-with-parent", "--cap-drop", "ALL", "--clearenv", "--chdir", "/"]
            args += ["--cap-add", "CAP_SETPCAP"]  # Gate drops this before readback or package exec.
            for cap in CAPS[launch.profile]:
                args += ["--cap-add", CAP_NAMES[cap]]
            for name, value in ENVIRONMENT.items():
                args += ["--setenv", name, value]
            args += ["--ro-bind" if launch.profile == Profile.OBSERVER else "--bind", f"/proc/self/fd/{resources['root'].fd}", "/",
                     "--ro-bind", f"/proc/self/fd/{resources['esp'].fd}", "/boot/efi", "--dev", "/dev", "--proc", "/proc",
                     "--remount-ro", "/proc", "--tmpfs", "/sys", "--remount-ro", "/sys", "--tmpfs", "/run", "--tmpfs", "/tmp",
                     "--ro-bind", f"/proc/self/fd/{resources['payload'].fd}", "/run/igloo-source",
                     "--perms", "0555", "--ro-bind-data", str(gate_copy), "/run/igloo-gate",
                     "--perms", "0555", "--ro-bind-data", str(tool_copy), launch.executable,
                     "--seccomp", str(seccomp_fd), "--", "/run/igloo-gate",
                     format(sum(1 << c for c in CAPS[launch.profile]), "x"), launch.executable, *launch.arguments]
            host_namespaces = namespace_ids("self")
            process = subprocess.Popen(args, stdin=subprocess.DEVNULL if input_fd is None else input_fd,
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=ENVIRONMENT,
                                       pass_fds=(*temporary, *(r.fd for r in resources.values())), start_new_session=True)
            deadline = time.monotonic() + min(30, launch.timeout_seconds)
            gate_pid = self._gate(process, deadline)
            gate_pidfd = os.pidfd_open(gate_pid)
            expected_paths = {"/": resources["root"].identity, "/boot/efi": resources["esp"].identity,
                              "/run/igloo-source": resources["payload"].identity}
            observation = self._observe(gate_pid, launch.timeout_seconds, expected_paths)
            verify_effective(observation, host_namespaces, resources, launch.profile)
            require(digest(Path(f"/proc/{gate_pid}/exe").read_bytes()) == self.runtime.gate_hash, "GateExecutableChanged")
            require(digest(Path(f"/proc/{observation['Init']['Pid']}/exe").read_bytes()) == self.runtime.bubblewrap_hash,
                    "NamespaceInitExecutableChanged")
            for resource in resources.values():
                resource.verify()
            require(digest(Path(f"/proc/{gate_pid}/root" + launch.executable).read_bytes()) == launch.tool_hash, "ToolOverlayChanged")
            durable_intent(launch.public_identity(), digest(canonical(observation)))
            # Re-read everything after durable I/O, not just a cached success flag.
            for resource in resources.values():
                resource.verify()
            require(self._observe(gate_pid, launch.timeout_seconds, expected_paths) == observation, "EffectivePolicyChangedAfterIntent")
            command_started = True  # A failed release is still an uncertain start.
            signal.pidfd_send_signal(gate_pidfd, signal.SIGCONT)
            code = process.wait(timeout=launch.timeout_seconds)
            return {"Generation": launch.generation, "PlanSha256": launch.plan_hash, "Stage": launch.stage,
                    "OperationSha256": launch.public_identity(), "ToolSha256": launch.tool_hash,
                    "State": "Exited", "ExitCode": code, "IsolationSha256": digest(canonical(observation)),
                    "SeccompSha256": filter_hash, "Observation": observation}
        except (OSError, ValueError, subprocess.TimeoutExpired):
            # Never emit command output, secret input or exception text. Setup can
            # have created mountpoint directories even before the command starts.
            return {"Generation": launch.generation, "PlanSha256": launch.plan_hash, "Stage": launch.stage,
                    "OperationSha256": launch.public_identity(), "State": "OutcomeUnknown" if command_started else "SetupRejected",
                    "ExitCode": None, "Observation": observation}
        finally:
            if gate_pidfd is not None:
                try:
                    signal.pidfd_send_signal(gate_pidfd, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                os.close(gate_pidfd)
            if process is not None:
                if process.poll() is None:
                    process.kill()
                process.wait()
            for fd in temporary:
                os.close(fd)

    def _observe(self, pid, timeout, expected_paths):
        self.runtime.verify()
        observed = subprocess.run([self.runtime.python, "-I", self.runtime.observer, str(pid),
                                   "--expected-paths", json.dumps(expected_paths)],
                                  stdin=subprocess.DEVNULL, capture_output=True, env=ENVIRONMENT,
                                  timeout=min(30, timeout), check=False)
        require(observed.returncode == 0 and len(observed.stdout) <= 16 * 1024 * 1024, "IndependentObserverUnavailable")
        return json.loads(observed.stdout)

    def _gate(self, process, deadline):
        while process.poll() is None and time.monotonic() < deadline:
            pending = [process.pid]
            seen = set()
            while pending:
                pid = pending.pop()
                require(pid not in seen and len(seen) < 16, "UnexpectedSetupProcessTree")
                seen.add(pid)
                try:
                    values = status(pid)
                    if values["State"].strip().split()[0] == "T" and digest(Path(f"/proc/{pid}/exe").read_bytes()) == self.runtime.gate_hash:
                        return pid
                    pending.extend(map(int, Path(f"/proc/{pid}/task/{pid}/children").read_text().split()))
                except FileNotFoundError:
                    continue
            time.sleep(0.01)
        raise Rejected("CommandGateUnavailable")
