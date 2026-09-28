"""Explicit Linux-native qualification, not normal deterministic test discovery.

Requires root, a separately acquired/hash-verified bwrap, gcc and static libc.
Only fresh ordinary /tmp directories are writable; no block device is opened,
mounted, formatted or created. Not a Debian bootstrap/install/boot rehearsal.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import stat
import unittest
import uuid
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "distros/debian/native"))
import package_broker as broker
from isolation_policy import Launch, Profile, digest
from isolation_observer import namespace_ids
from dataclasses import asdict


class BrokerRehearsal(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if os.geteuid() != 0:
            raise RuntimeError("Explicit native fixture needs a root namespace supervisor")
        expected = os.environ["IGLOO_BWRAP_SHA256"]
        source = Path(os.environ["IGLOO_BWRAP_PATH"])
        if digest(source.read_bytes()) != expected:
            raise RuntimeError("Fixture bwrap hash differs from authenticated artifact")
        cls.work = tempfile.TemporaryDirectory(prefix="igloo-broker-runtime-")
        runtime = Path(cls.work.name)
        cls.bwrap = runtime / "bwrap"
        shutil.copyfile(source, cls.bwrap); cls.bwrap.chmod(0o755)
        cls.gate = runtime / "gate"
        cls.probe = runtime / "probe"
        for source, target in ((ROOT / "distros/debian/native/command_gate.c", cls.gate),
                               (ROOT / "tests/installer/broker_probe.c", cls.probe)):
            subprocess.run(["/usr/bin/gcc", "-static", "-O2", "-Wall", "-Wextra", "-Werror", "-o", str(target), str(source)], check=True)
        cls.observer = runtime / "observer.py"
        shutil.copyfile(ROOT / "distros/debian/native/isolation_observer.py", cls.observer); cls.observer.chmod(0o644)
        python = str(Path(sys.executable).resolve())
        cls.runtime = broker.Runtime(str(cls.bwrap), expected, str(cls.gate), digest(cls.gate.read_bytes()),
                                     python, digest(Path(python).read_bytes()), str(cls.observer), digest(cls.observer.read_bytes()))

    @classmethod
    def tearDownClass(cls):
        cls.work.cleanup()

    def setUp(self):
        self.work = tempfile.TemporaryDirectory(prefix="igloo-broker-fixture-")
        self.base = Path(self.work.name)
        self.paths = {name: self.base / name for name in ("root", "esp", "payload")}
        for path in self.paths.values():
            path.mkdir()
        for name in ("usr/bin", "etc", "boot/efi", "dev", "proc", "sys", "run", "tmp", "root"):
            (self.paths["root"] / name).mkdir(parents=True, exist_ok=True)
        shutil.copyfile(self.probe, self.paths["root"] / "usr/bin/probe")
        (self.paths["root"] / "usr/bin/probe").chmod(0o755)
        (self.paths["payload"] / "source").write_bytes(b"authenticated fixture")
        self.leases = {name: broker.DirectoryLease(str(path)) for name, path in self.paths.items()}
        self.intents = []

    def tearDown(self):
        for lease in self.leases.values():
            lease.close()
        # Bubblewrap's entire private namespace is gone; no mounts persist here.
        self.assertFalse(any(m["Path"].startswith(str(self.base) + "/") for m in
                             broker.mount_records(Path("/proc/self/mountinfo").read_text())))
        self.work.cleanup()

    def run_probe(self, name, *, timeout=10, intent=None, profile=Profile.CONFIGURATION):
        stage = "ConfigurePackagePolicy" if profile == Profile.PACKAGE else "ConfigureLocale"
        launch = Launch(str(uuid.uuid4()), "A"*64, stage, profile,
                        "/usr/bin/probe", digest(self.probe.read_bytes()), (name,), timeout)
        return broker.PackageBroker(self.runtime).execute(launch, self.leases,
                   intent if intent is not None else lambda operation, isolation: self.intents.append((operation, isolation)))

    def test_01_effective_policy_and_success(self):
        result = self.run_probe("success")
        if result["State"] != "Exited":
            print(json.dumps(result, sort_keys=True), file=sys.stderr)
        self.assertEqual(result["State"], "Exited")
        self.assertEqual(result["ExitCode"], 0)
        self.assertEqual(len(self.intents), 1)

    def test_fresh_import_observer_uses_authenticated_entry_and_real_dpkg_readback(self):
        # Ordinary directory fixture, not a canonical block lease or GNOME artifact.
        from types import SimpleNamespace
        from test_configured_root import ConfiguredRootTests
        import session_import
        import configured_root
        fixture = ConfiguredRootTests(); fixture.setUp()
        manifest_fd = None
        try:
            fixture.run_import()
            manifest = self.base / "observer-manifest.json"
            manifest.write_bytes(configured_root.canonical(fixture.manifest))
            manifest_fd = os.open(manifest, os.O_RDONLY | os.O_CLOEXEC)
            tools = self.base / "import-observer-tools"; tools.mkdir(mode=0o700)
            names = ("session_entry.py", "target_files.py", "isolation_policy.py", "isolation_observer.py", "package_broker.py",
                     "mount_supervisor.py", "target_observer.py", "deployment_journal.py", "configured_root_metadata.py",
                     "root_transport.py", "configured_root.py", "session_import.py", "block_session.py")
            for name in names:
                shutil.copyfile(ROOT / "distros/debian/native" / name, tools / name)
            declaration = {"GenerationId": str(uuid.uuid4()), "SessionId": str(uuid.uuid4()), "PlanSha256": "A" * 64,
                           "EntryPath": str(tools / "session_entry.py"),
                           "ToolHashes": {str(tools / n): digest((tools / n).read_bytes()) for n in names}}
            session = SimpleNamespace(declaration=declaration, runtime=self.runtime)
            result = session_import.independent_readback(session, fixture.view, manifest_fd, digest(manifest.read_bytes()))
            self.assertEqual(configured_root.verify_tree(fixture.view, fixture.manifest), result["FilesystemSha256"])
            self.assertEqual(configured_root.verify_dpkg(fixture.view, fixture.manifest), result["PackageStateSha256"])
            self.assertEqual(digest(configured_root.canonical(result["ObserverEvidence"])), result["ObserverEvidenceSha256"])
            partial = session_import.independent_readback(session, fixture.view, manifest_fd, digest(manifest.read_bytes()), partial=True)
            self.assertTrue(partial["HasContent"])
        finally:
            if manifest_fd is not None: os.close(manifest_fd)
            fixture.tearDown()

    def test_package_profile_cannot_chroot_for_bootstrap(self):
        result = self.run_probe("chroot", profile=Profile.PACKAGE)
        self.assertEqual((result["State"], result["ExitCode"]), ("Exited", 0))

    def test_package_profile_cannot_create_bootstrap_null_device(self):
        result = self.run_probe("bootstrap-null", profile=Profile.PACKAGE)
        self.assertEqual((result["State"], result["ExitCode"]), ("Exited", 0))

    def test_02_command_file_write_with_independent_readback(self):
        self.assertEqual(self.run_probe("write")["ExitCode"], 0)
        self.assertEqual((self.paths["root"] / "etc/result").read_bytes(), b"ok")

    def test_03_setup_failure_never_invokes_command(self):
        with patch.object(broker, "seccomp_descriptor", side_effect=OSError("fixture")):
            self.assertEqual(self.run_probe("write")["State"], "SetupRejected")
        self.assertEqual(self.intents, [])
        self.assertFalse((self.paths["root"] / "etc/result").exists())

    def test_04_journal_failure_does_not_release_gate(self):
        def fail(*_):
            raise OSError("fsync fixture")
        self.assertEqual(self.run_probe("write", intent=fail)["State"], "SetupRejected")
        self.assertFalse((self.paths["root"] / "etc/result").exists())

    def test_05_child_death_is_not_success(self):
        result = self.run_probe("die")
        self.assertEqual(result["State"], "Exited")
        self.assertNotEqual(result["ExitCode"], 0)

    def test_06_timeout_is_unknown(self):
        result = self.run_probe("timeout", timeout=2)
        self.assertEqual(result["State"], "OutcomeUnknown")
        self.assertIsNone(result["ExitCode"])

    def test_07_service_socket_in_root_rejected(self):
        import socket
        with socket.socket(socket.AF_UNIX) as service:
            service.bind(str(self.paths["root"] / "etc/service"))
            self.assertEqual(self.run_probe("write")["State"], "SetupRejected")
        self.assertEqual(self.intents, [])

    def test_08_independent_observer_failure(self):
        with patch.object(broker, "verify_effective", side_effect=ValueError("observer fixture")):
            self.assertEqual(self.run_probe("write")["State"], "SetupRejected")
        self.assertEqual(self.intents, [])

    def test_09_resource_substitution_during_intent(self):
        def substitute(*_):
            self.paths["esp"].rename(self.base / "original-esp")
            self.paths["esp"].mkdir()
        self.assertEqual(self.run_probe("write", intent=substitute)["State"], "SetupRejected")
        self.assertFalse((self.paths["root"] / "etc/result").exists())

    def test_10_package_capability_profile(self):
        launch = Launch(str(uuid.uuid4()), "A"*64, "InstallKernel", Profile.PACKAGE,
                        "/usr/bin/probe", digest(self.probe.read_bytes()), ("success",), 10)
        result = broker.PackageBroker(self.runtime).execute(launch, self.leases, lambda *_: None)
        self.assertEqual(result["State"], "Exited")
        self.assertEqual(result["Observation"]["Capabilities"]["CapBnd"], 0x800000db)

    def test_11_observer_profile_has_no_capabilities(self):
        launch = Launch(str(uuid.uuid4()), "A"*64, "InspectArtifacts", Profile.OBSERVER,
                        "/usr/bin/probe", digest(self.probe.read_bytes()), ("success",), 10)
        result = broker.PackageBroker(self.runtime).execute(launch, self.leases, lambda *_: None)
        self.assertEqual(result["State"], "Exited")
        self.assertEqual(result["Observation"]["Capabilities"]["CapBnd"], 0)

    def test_12_block_node_even_in_owned_root_rejected(self):
        # No device is opened. Major 4095 is not registered in this fixture kernel.
        self.assertNotIn("4095", [line.split()[0] for line in Path("/proc/devices").read_text().splitlines() if line[:1].isdigit()])
        os.mknod(self.paths["root"] / "etc/windows-fixture", stat.S_IFBLK | 0o600, os.makedev(4095, 1048575))
        self.assertEqual(self.run_probe("write")["State"], "SetupRejected")
        self.assertEqual(self.intents, [])

    def mount_case(self, case):
        before = Path("/proc/self/mountinfo").read_text()
        request = {"Runtime": asdict(self.runtime), "Case": case, "Generation": str(uuid.uuid4()),
                   "ParentNamespace": namespace_ids("self")["mnt"]}
        result = subprocess.run([str(self.gate), "--supervise", str(Path(sys.executable).resolve()), "-B",
                                 str(ROOT / "tests/installer/mount_rehearsal.py")], input=json.dumps(request).encode(),
                                capture_output=True, check=False, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        evidence = json.loads(result.stdout)
        # Kernel mount IDs/table unchanged outside the disposable child namespace.
        self.assertEqual(Path("/proc/self/mountinfo").read_text(), before)
        if "RetainedFixture" in evidence:
            path = Path(evidence["RetainedFixture"])
            self.assertEqual(path.parent, Path("/tmp"))
            self.assertTrue(path.name.startswith("igloo-private-mount-"))
            self.assertFalse(path.is_symlink())
            for child in path.iterdir():
                self.assertIn(child.name, ("first", "second", "moved"))
                self.assertTrue(child.is_dir() and not child.is_symlink())
                self.assertEqual(list(child.iterdir()), [])
                child.rmdir()
            path.rmdir()
        return evidence

    def test_mount_exact_native_reverse_teardown(self):
        evidence = self.mount_case("success")
        self.assertEqual(evidence["State"], "AppliedAndVerified")
        self.assertEqual([r["State"] for r in evidence["Records"]], ["IntentDurable", "AppliedAndVerified"] * 4)

    def session_case(self, case):
        # Real persistent supervisor, no lease is accepted and no block is opened.
        # The fixture acknowledges only startup/close; production uses the .NET resolver.
        import deployment_journal
        directory = self.base / "session-tools"; directory.mkdir(mode=0o700)
        names = ("session_entry.py", "block_session.py", "mount_supervisor.py", "package_broker.py",
                 "target_observer.py", "deployment_journal.py", "configured_root_metadata.py", "root_transport.py", "configured_root.py", "session_import.py",
                 "isolation_policy.py", "isolation_observer.py", "target_files.py")
        for name in names:
            shutil.copyfile(ROOT / "distros/debian/native" / name, directory / name)
        collector = directory / "collect_inventory.py"
        shutil.copyfile(ROOT / "distros/_shared/installer/collect_inventory.py", collector)
        paths = [*(directory / n for n in names), collector, Path(self.runtime.python), self.gate, self.bwrap, self.observer]
        request = {"SessionId": str(uuid.uuid4()), "GenerationId": str(uuid.uuid4()), "PlanSha256": "A" * 64,
                   "ParentMountNamespace": namespace_ids("self")["mnt"], "Runtime": asdict(self.runtime),
                   "CollectorPath": str(collector), "CollectorSha256": digest(collector.read_bytes()),
                   "ToolHashes": {str(p): digest(p.read_bytes()) for p in paths}}
        if case == "tool-changed":
            with (directory / "block_session.py").open("a") as changed:
                changed.write("\n# changed fixture\n")
        store = self.base / "session-journal"; store.mkdir(mode=0o700)
        deployment_journal.perform(str(store), request["GenerationId"], request["PlanSha256"], "reserve")
        records = []
        def checkpoint(record):
            data = json.dumps({"PlanSha256": request["PlanSha256"], "Record": record}).encode()
            reference = deployment_journal.perform(str(store), request["GenerationId"], request["PlanSha256"], "append", data).decode()
            read = subprocess.run([self.runtime.python, "-I", str(ROOT / "distros/debian/native/deployment_journal.py"), "read",
                "--store", str(store), "--generation", request["GenerationId"], "--plan-hash", request["PlanSha256"], "--reference", reference],
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False, timeout=10)
            self.assertEqual(read.returncode, 0); self.assertEqual(read.stdout, data)
            records.append(record)
        checkpoint({"State": "IntentDurable", "Action": "CreatePrivateSupervisor"})
        before = Path("/proc/self/mountinfo").read_text()
        process = subprocess.Popen([str(self.gate), "--supervise", self.runtime.python, "-I", "-B", str(directory / "session_entry.py")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, env=broker.ENVIRONMENT)
        def send(value):
            process.stdin.write(json.dumps(value) + "\n"); process.stdin.flush()
        try:
            send(request)
            first = process.stdout.readline()
            if case == "tool-changed":
                self.assertEqual(first, ""); self.assertEqual(process.wait(timeout=10), 1)
                self.assertEqual(len(records), 1)
                return
            ready = json.loads(first)
            self.assertEqual(ready["Kind"], "SessionReady")
            self.assertNotEqual(ready["Observation"]["Namespace"], request["ParentMountNamespace"])
            self.assertTrue(all(not m["Propagation"] for m in ready["Observation"]["Mounts"]))
            checkpoint({"State": "AppliedAndVerified", "Action": "CreatePrivateSupervisor", "Evidence": ready})
            send({"SessionId": request["SessionId"], "Accepted": True})
            send({"SessionId": request["SessionId"], "Action": "Close" if case == "close" else "AcquireLeases"})
            if case == "reject-ownership":
                observation = json.loads(process.stdout.readline())
                self.assertEqual(observation["Kind"], "Inventory")
                # Even an available machine inventory is NOT this fixture's ownership receipt.
                send({"Challenge": observation["Challenge"], "Accepted": False})
                self.assertEqual(process.wait(timeout=15), 1)
                self.assertEqual(len(records), 2)
                return
            for state in ("IntentDurable", "AppliedAndVerified"):
                event = json.loads(process.stdout.readline())
                self.assertEqual(event["Record"]["State"], state)
                checkpoint(event)
                send({"Challenge": event["Challenge"], "Accepted": True})
            self.assertEqual(json.loads(process.stdout.readline())["State"], "AppliedAndVerified")
            self.assertEqual(process.wait(timeout=15), 0)
        finally:
            if process.poll() is None:
                process.kill(); process.wait(timeout=10)
            process.stdin.close(); process.stdout.close()
            self.assertEqual(Path("/proc/self/mountinfo").read_text(), before)

    def test_session_private_startup_and_durable_close(self):
        self.session_case("close")

    def test_session_changed_tool_rejected_before_setup(self):
        self.session_case("tool-changed")

    def test_session_ownership_rejected_before_block_open(self):
        self.session_case("reject-ownership")

    def test_detached_root_clone_never_reaches_bubblewrap(self):
        # Reproduce ONLY the kernel FD shape from the rejected experiment. Never
        # hand it to bwrap and never attach it to the host or a target directory.
        import ctypes
        from mount_supervisor import LinuxMountCalls
        from target_files import mount_id
        from isolation_policy import Rejected
        original = self.leases["root"]
        fd = LinuxMountCalls().call(428, ctypes.c_int(original.fd), ctypes.c_char_p(b""),
                                  ctypes.c_uint(1 | os.O_CLOEXEC | 0x1000))  # CLONE | CLOEXEC | AT_EMPTY_PATH
        connected = original.fd; connected_mount = original.mount
        try:
            original.fd = fd; original.mount = mount_id(fd)
            with self.assertRaises(Rejected):
                original.verify()
        finally:
            original.fd = connected; original.mount = connected_mount
            os.close(fd)
        original.verify()
        self.assertEqual(self.intents, [])


def negative_test(name):
    def test(self):
        result = self.run_probe(name)
        self.assertEqual(result["State"], "Exited")
        self.assertEqual(result["ExitCode"], 0)
    return test


for _name in ("mount", "remount", "efivarfs", "reboot", "module", "mknod", "namespace", "network",
              "firmware", "service", "raw", "esp", "payload", "file-source", "regain-capability", "chroot", "bootstrap-null"):
    setattr(BrokerRehearsal, "test_native_" + _name.replace("-", "_"), negative_test(_name))


def mount_failure_test(name):
    def test(self):
        evidence = self.mount_case(name)
        self.assertIn(evidence["State"], ("RejectedBeforeIntent", "RejectedWithIntent"))
    return test


for _name in ("intent-failure", "result-failure", "syscall-failure", "observer-failure", "attach-partial", "busy", "unmount-failure", "mount-replaced"):
    setattr(BrokerRehearsal, "test_mount_" + _name.replace("-", "_"), mount_failure_test(_name))


if __name__ == "__main__":
    unittest.main()
