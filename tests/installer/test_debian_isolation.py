"""Deterministic policy/readback tests; no mounts or privileged setup."""
import copy
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "distros/debian/native"))
import isolation_policy as policy
import package_broker as broker
import isolation_observer as observer
import mount_supervisor as supervisor


def launch():
    return policy.Launch("d638b5ba-bcef-4da0-88fb-f6a815446343", "A"*64, "ConfigureLocale",
                         policy.Profile.CONFIGURATION, "/usr/sbin/locale-gen", "B"*64, ())


def readback():
    resources = {role: SimpleNamespace(identity=[8, n]) for n, role in enumerate(("root", "esp", "payload"), 1)}
    host = {name: n for n, name in enumerate(("mnt", "pid", "net", "ipc", "uts", "user"), 1)}
    paths = {"/": [8, 1], "/boot/efi": [8, 2], "/run/igloo-source": [8, 3]}
    mounts = [{"Id": n, "Path": path, "Options": ["rw" if path == "/" else "ro", "nosuid", "nodev"], "Propagation": []}
              for n, path in enumerate((*paths, "/sys", "/proc", "/usr/sbin/locale-gen"), 1)]
    devices = {"null": (1, 3), "zero": (1, 5), "full": (1, 7), "random": (1, 8), "urandom": (1, 9), "tty": (5, 0), "pts/ptmx": (5, 2)}
    observed = {"State": "T", "Uid": [0]*4, "Gid": [0]*4,
                "Capabilities": {name: 0xdb for name in ("CapInh", "CapEff", "CapPrm", "CapBnd", "CapAmb")},
                "NoNewPrivs": 1, "Seccomp": 2, "SeccompFilters": 1,
                "Namespaces": {name: value+10 for name, value in host.items()}, "NetworkDevices": ["lo"],
                "SysEntries": [], "RunEntries": ["igloo-gate", "igloo-source"], "Sockets": [], "Paths": paths,
                "Descriptors": [{"Number": n, "Type": stat.S_IFCHR, "Device": [1, 3]} for n in range(3)],
                "Devices": [{"Path": "/dev/"+name, "Type": "char", "Major": major, "Minor": minor} for name, (major, minor) in devices.items()],
                "DeviceLinks": {"fd": "/proc/self/fd", "stdin": "/proc/self/fd/0", "stdout": "/proc/self/fd/1", "stderr": "/proc/self/fd/2", "ptmx": "pts/ptmx"},
                "Mounts": mounts}
    observed["Init"] = {"NamespacePid": 1, "Root": [8, 1], "Namespaces": observed["Namespaces"].copy(),
                         "EffectiveCapabilities": 0, "Descriptors": []}
    return observed, host, resources


class IsolationPolicyTests(unittest.TestCase):
    def test_valid_launch_has_stable_public_identity(self):
        self.assertEqual(launch().public_identity(), launch().public_identity())
        self.assertNotIn("LD_PRELOAD", policy.ENVIRONMENT)
        self.assertNotIn("DISPLAY", policy.ENVIRONMENT)

    def test_bad_argv_rejected(self):
        from dataclasses import replace
        for args in (("bad\x00arg",), ["shell"], ("x"*4097,)):
            with self.subTest(args_type=type(args).__name__), self.assertRaises(policy.Rejected):
                replace(launch(), arguments=args).validate()

    def test_secret_input_only_for_exact_command(self):
        from dataclasses import replace
        with self.assertRaises(policy.Rejected):
            replace(launch(), input_kind="EncryptedPassword").validate()
        valid = replace(launch(), stage="ConfigureUser", executable="/usr/sbin/chpasswd", arguments=("--encrypted",), input_kind="EncryptedPassword")
        self.assertEqual(valid.validate(), valid)

    def test_bootstrap_and_firmware_have_no_profile(self):
        from dataclasses import replace
        for stage in ("Bootstrap", "ConfigureSignedPackages", "FinalizeFirmware", "FinalizeLoaderFiles"):
            with self.subTest(stage=stage), self.assertRaises(policy.Rejected):
                replace(launch(), stage=stage).validate()

    def test_missing_runtime_rejected_before_any_process(self):
        runtime = broker.Runtime("/missing", "A"*64, "/missing", "A"*64, "/missing", "A"*64, "/missing", "A"*64)
        with self.assertRaises(OSError):
            runtime.verify()

    def test_capabilities_exclude_mount_rawio_firmware_and_devices(self):
        for capabilities in policy.CAPS.values():
            self.assertFalse({16, 17, 18, 19, 21, 22, 27}.intersection(capabilities))

    def test_seccomp_export_is_sealed(self):
        import fcntl
        fd = policy.seccomp_descriptor()
        try:
            self.assertEqual(fcntl.fcntl(fd, fcntl.F_GET_SEALS), 15)
            self.assertGreater(os.fstat(fd).st_size, 0)
            with self.assertRaises(PermissionError):
                os.write(fd, b"alter")
        finally:
            os.close(fd)

    def test_valid_effective_readback_order_independent(self):
        value, host, resources = readback()
        value["Mounts"].reverse(); value["Devices"].reverse()
        broker.verify_effective(value, host, resources, policy.Profile.CONFIGURATION)

    def test_directory_symlink_or_replaced_inode_rejected(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name)
            (path/"root").mkdir()
            (path/"link").symlink_to("root")
            with self.assertRaises(policy.Rejected):
                broker.DirectoryLease(str(path/"link"))
            lease = broker.DirectoryLease(str(path/"root"))
            try:
                (path/"root").rename(path/"old")
                (path/"root").mkdir()
                with self.assertRaises(policy.Rejected):
                    lease.verify()
            finally:
                lease.close()

    def test_lease_mount_substitution_rejected(self):
        with tempfile.TemporaryDirectory() as name:
            lease = broker.DirectoryLease(name)
            try:
                with patch.object(broker, "mount_id", return_value=lease.mount+1), self.assertRaises(policy.Rejected):
                    lease.verify()
            finally:
                lease.close()

    def test_mount_supervisor_cannot_use_host_namespace(self):
        with self.assertRaisesRegex(policy.Rejected, "MountSessionNotPrivate"):
            supervisor.ExactMountSupervisor(launch().generation, "A"*64, observer.namespace_ids("self")["mnt"], None, None, None)

    def test_mount_supervisor_rejects_regular_file_as_block_source(self):
        session = object.__new__(supervisor.ExactMountSupervisor)
        session.generation, session.plan_hash = launch().generation, "A"*64
        session.revalidate = lambda _: self.fail("Canonical provider must not receive a regular file device")
        with tempfile.TemporaryFile() as file:
            with self.assertRaisesRegex(policy.Rejected, "BlockDescriptorChanged"):
                session._source(supervisor.BlockLease(launch().generation, "A"*64, "Root", file.fileno(), 8, 1, "B"*64))


def changed_readback(case):
    value, host, resources = readback()
    if case == "capability": value["Capabilities"]["CapEff"] |= 1 << 21
    elif case == "bounding": value["Capabilities"]["CapBnd"] = 2**41-1
    elif case == "ambient": value["Capabilities"]["CapAmb"] |= 1 << 22
    elif case == "seccomp": value["Seccomp"] = 0
    elif case == "nnp": value["NoNewPrivs"] = 0
    elif case in ("mnt", "pid", "net", "ipc", "uts"): value["Namespaces"][case] = host[case]
    elif case == "network": value["NetworkDevices"].append("eth0")
    elif case == "firmware": value["SysEntries"].append("firmware")
    elif case == "run": value["RunEntries"].append("systemd")
    elif case == "socket": value["Sockets"].append("/etc/host-service")
    elif case == "block": value["Devices"].append({"Path": "/dev/windows", "Type": "block", "Major": 8, "Minor": 2})
    elif case == "wrong-char": value["Devices"][0]["Major"] = 10
    elif case == "fd": value["Descriptors"].append({"Number": 3, "Type": stat.S_IFBLK, "Device": [8, 2]})
    elif case == "source": value["Paths"]["/run/igloo-source"] = [8, 90]
    elif case == "esp": value["Paths"]["/boot/efi"] = [8, 90]
    elif case == "root": value["Paths"]["/"] = [8, 90]
    elif case == "propagation": value["Mounts"][0]["Propagation"] = ["master:7"]
    elif case == "extra-mount": value["Mounts"].append({"Id": 90, "Path": "/mnt/windows", "Propagation": [], "Options": ["ro", "nodev", "nosuid"]})
    elif case == "stack": value["Mounts"].append(copy.deepcopy(value["Mounts"][0]))
    elif case == "nodev": value["Mounts"][0]["Options"].remove("nodev")
    elif case == "nosuid": value["Mounts"][0]["Options"].remove("nosuid")
    elif case == "rw-esp": value["Mounts"][1]["Options"][0] = "rw"
    elif case == "rw-payload": value["Mounts"][2]["Options"][0] = "rw"
    elif case == "dev-link": value["DeviceLinks"]["fd"] = "/host/proc/self/fd"
    elif case == "running": value["State"] = "R"
    elif case == "init-root": value["Init"]["Root"] = [8, 100]
    elif case == "init-fd": value["Init"]["Descriptors"] = [{"Number": 5, "Type": stat.S_IFDIR, "Device": [0, 0], "AnonymousKind": None}]
    else: raise AssertionError(case)
    return value, host, resources


def negative(case):
    def test(self):
        with self.assertRaises(policy.Rejected):
            broker.verify_effective(*changed_readback(case), policy.Profile.CONFIGURATION)
    return test


for _case in ("capability", "bounding", "ambient", "seccomp", "nnp", "mnt", "pid", "net", "ipc", "uts", "network", "firmware",
              "run", "socket", "block", "wrong-char", "fd", "source", "esp", "root", "propagation", "extra-mount", "stack", "nodev", "nosuid",
              "rw-esp", "rw-payload", "dev-link", "running", "init-root", "init-fd"):
    setattr(IsolationPolicyTests, "test_reject_"+_case.replace("-", "_"), negative(_case))


if __name__ == "__main__":
    unittest.main()
