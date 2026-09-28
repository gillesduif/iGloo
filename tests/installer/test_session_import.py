"""Session dispatch + existing importer. Canonical/mount evidence here is explicitly fake.

Actual file creation/hash/fsync/dpkg-query mechanics run in disposable directories.
These tests are not GPT leases or the full configured GNOME artifact qualification.
"""
import copy
import datetime as dt
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "distros/debian/native"))
import block_session
import configured_root as root
import session_import as operation
import root_transport as transport
from test_configured_root import fixture


class MountEvidenceTests(unittest.TestCase):
    def evidence(self):
        receipt = {"Path": "/run/igloo/target/test", "MountId": 10, "Identity": [2049, 2]}
        record = {"Path": receipt["Path"], "Id": 10, "Root": "/", "Device": "8:1", "FileSystem": "ext4",
                  "Options": ["rw", "nosuid", "nodev"], "Propagation": []}
        return receipt, {"Namespace": "mnt:[test]", "Mounts": [record],
                         "Paths": [{"Identity": receipt["Identity"], "MountId": 10}]}

    def test_exact_connected_mount_observation(self):
        receipt, observation = self.evidence()
        operation.import_mount_record(observation, receipt, SimpleNamespace(major=8, minor=1), "mnt:[test]", False)

    def test_each_mount_substitution_rejected(self):
        for change in ("stacked", "child", "device", "mode", "filesystem-root", "inode", "namespace", "alias", "propagation"):
            with self.subTest(change=change):
                receipt, observation = self.evidence()
                mounted = observation["Mounts"][0]
                if change == "stacked": observation["Mounts"].append(copy.deepcopy(mounted))
                if change == "child": observation["Mounts"].append({**mounted, "Id": 11, "Path": mounted["Path"] + "/boot/efi"})
                if change == "device": mounted["Device"] = "8:2"
                if change == "mode": mounted["Options"] = ["ro", "nosuid", "nodev"]
                if change == "filesystem-root": mounted["Root"] = "/subdir"
                if change == "inode": observation["Paths"][0]["Identity"] = [2049, 3]
                if change == "namespace": observation["Namespace"] = "mnt:[other]"
                if change == "alias": observation["Mounts"].append({**mounted, "Id": 11, "Path": "/other"})
                if change == "propagation": mounted["Propagation"] = ["shared:1"]
                with self.assertRaises(ValueError):
                    operation.import_mount_record(observation, receipt, SimpleNamespace(major=8, minor=1), "mnt:[test]", False)

    def test_composed_root_cannot_supply_import_view(self):
        session = SimpleNamespace(declaration={"ImportPlan": {}}, supervisor=SimpleNamespace(receipts=[
            {"Role": "Root"}, {"Role": "LinuxEsp"}, {"Role": "Payload"}]))
        with self.assertRaises(ValueError): operation.ConnectedImportView(session)


class SessionImportDispatchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="igloo-session-import-fixture-")
        self.base = Path(self.temp.name)
        self.views = {}
        self.files = []
        for role in ("Root", "Payload"):
            directory = self.base / role; directory.mkdir()
            fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
            self.files.append(fd); info = os.fstat(fd)
            self.views[role] = root.RootView(fd, info.st_dev, info.st_ino, root.mount_id(fd))
        self.manifest, contents = fixture()
        data = root.canonical(self.manifest)
        stream = root.MAGIC + b"".join(contents[e["Path"]] for e in self.manifest["Entries"] if e["Type"] == "File")
        self.artifact = {"Manifest": {"Length": len(data)}, "Content": {"Length": len(stream)}, "ConfiguredPackages": self.manifest["Packages"]}
        descriptor = root.canonical(self.artifact)
        self.plan = {"BuildId": self.manifest["BuildId"], "DerivationId": "00000000-0000-0000-0000-000000000002", "ContentLength": len(stream),
                     "DescriptorSha256": root.digest(descriptor), "ManifestSha256": root.digest(data), "ContentSha256": root.digest(stream)}
        self.folder = self.base / "Payload" / "configured-root" / self.plan["BuildId"] / self.plan["DerivationId"]
        self.folder.mkdir(parents=True)
        for name, content in (("descriptor.json", descriptor), ("root.manifest.json", data), ("root.content", stream)):
            (self.folder / name).write_bytes(content)
        self.source_before = {p.name: p.read_bytes() for p in self.folder.iterdir()}
        self.events = []
        self.session = object.__new__(block_session.MountSession)
        self.session.declaration = {"ImportPlan": self.plan, "SessionId": "fixture-session", "GenerationId": "fixture-generation", "PlanSha256": "A" * 64}
        self.session.failed = self.session.closed = self.session.teardown = self.session.import_attempted = False
        self.session.acquired = True
        self.session.channel = SimpleNamespace(ask=self.ask, checkpoint=lambda record: self.events.append(("Checkpoint", record)))
        self.connected = SimpleNamespace(views=self.views, verify=lambda: None, close=lambda: None)
        self.patcher = patch.object(operation, "ConnectedImportView", return_value=self.connected)
        self.patcher.start()
        self.readback = patch.object(operation, "independent_readback", side_effect=self.observe)
        self.readback.start()

    def tearDown(self):
        self.patcher.stop(); self.readback.stop()
        for view in self.views.values(): view.close()
        for fd in self.files: os.close(fd)
        self.temp.cleanup()

    def ask(self, kind, **values):
        self.events.append((kind, values))
        if kind == "AuthenticateImportSource":
            return {"Artifact": self.artifact, "ExpiresAt": (dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)).isoformat()}
        return {"Reference": "fixture-result", "Sha256": "A" * 64}

    def observe(self, session, view, manifest_fd, manifest_hash, partial=False):
        # Real readonly semantic/dpkg primitives; mocked process boundary, explicitly fixture evidence.
        if partial:
            return {"HasContent": len(root.inspect_tree(view)) > 1, "PartialStateSha256": "C" * 64}
        return {"FilesystemSha256": root.verify_tree(view, self.manifest),
                "PackageStateSha256": root.verify_dpkg(view, self.manifest),
                "NeutralStateSha256": root.verify_neutral(view, self.manifest), "ObserverEvidenceSha256": "B" * 64}

    def dispatch(self): return self.session.perform("ImportConfiguredRoot")

    def chunked(self):
        content = (self.folder / 'root.content').read_bytes()
        (self.folder / 'root.content.0000').write_bytes(content)
        envelope = {k: self.plan[k] for k in ('BuildId', 'DerivationId', 'DescriptorSha256', 'ManifestSha256', 'ContentLength', 'ContentSha256')}
        envelope.update(Version=1, Type='Chunked', ChunkSize=transport.CHUNK_SIZE,
            Chunks=[dict(Index=0, Name='root.content.0000', Length=len(content), Sha256=root.digest(content))])
        data=transport.canonical(envelope)
        (self.folder/'transport.json').write_bytes(data)
        # Deliberate coexistence: explicit selection must never infer/fallback.
        self.plan.update(Transport='Chunked', TransportManifestSha256=root.digest(data))

    def test_chunked_canonical_dispatch_uses_same_semantic_importer(self):
        self.chunked()
        (self.folder/'root.content').write_bytes(b'not selected')
        result=self.dispatch()
        self.assertEqual('AppliedAndVerified', result['State'])
        self.assertIn('TransportManifest', self.events[0][1])
        self.assertEqual(self.plan['TransportManifestSha256'], self.events[1][1]['Record']['TransportManifestSha256'])

    def test_chunked_corruption_before_import_intent(self):
        self.chunked()
        target=self.folder/'root.content.0000'
        target.write_bytes(target.read_bytes()[:-1]+b'X')
        with self.assertRaises(ValueError): self.dispatch()
        self.assertFalse(any(kind=='ImportCheckpoint' for kind, _ in self.events))
        self.views['Root'].empty()

    def test_chunked_transport_wrong_plan_hash_before_import_intent(self):
        self.chunked(); self.plan['TransportManifestSha256']='F'*64
        with self.assertRaises(ValueError): self.dispatch()
        self.assertFalse(any(kind=='ImportCheckpoint' for kind, _ in self.events))

    def test_chunk_changed_after_intent_is_not_success_or_retryable(self):
        self.chunked()
        original=self.session.channel.checkpoint
        def replace(record):
            original(record)
            if record.get('State')=='IntentDurable':
                path=self.folder/'root.content.0000'; path.write_bytes(path.read_bytes()[:-1]+b'X')
                info=path.stat(); os.utime(path, ns=(info.st_atime_ns, info.st_mtime_ns+1000000000))
        self.session.channel.checkpoint=replace
        with self.assertRaises(ValueError): self.dispatch()
        self.assertEqual('OutcomeUnknown', self.events[-1][1]['Record']['Outcome'])
        with self.assertRaises(ValueError): self.dispatch()

    def test_closed_session_dispatch_calls_existing_importer(self):
        result = self.dispatch()
        self.assertEqual("AppliedAndVerified", result["State"])
        self.assertEqual(self.source_before, {p.name: p.read_bytes() for p in self.folder.iterdir()})
        self.assertEqual("AuthenticateImportSource", self.events[0][0])
        self.assertEqual("IntentDurable", self.events[1][1]["Record"]["Outcome"])
        self.assertEqual("AppliedAndVerified", self.events[-2][1]["Record"]["Outcome"])
        with self.assertRaises(ValueError): self.dispatch()

    def test_source_corruption_never_reserves_or_writes_root(self):
        path = self.folder / "root.content"; path.write_bytes(path.read_bytes()[:-1] + b"X")
        with self.assertRaises(ValueError): self.dispatch()
        self.assertFalse(any(k == "ImportCheckpoint" for k, _ in self.events))
        self.assertEqual([], os.listdir(self.views["Root"].fd))

    def test_nonempty_target_never_reserves(self):
        (self.base / "Root" / "unexpected").write_bytes(b"preserve")
        with self.assertRaises(ValueError): self.dispatch()
        self.assertFalse(any(k == "ImportCheckpoint" for k, _ in self.events))

    def test_real_artifact_fat32_limit_cannot_use_host_path_fallback(self):
        self.plan["ContentLength"] = 4641457180
        with self.assertRaisesRegex(ValueError, "Fat32SingleFile"): self.dispatch()
        self.assertEqual([], self.events)

    def test_failed_intent_reopen_stops_first_content_write(self):
        original = self.ask
        def fail(kind, **values):
            if kind == "ImportCheckpoint": raise OSError("fixture fsync/reopen failure")
            return original(kind, **values)
        self.session.channel.ask = fail
        with self.assertRaises(OSError): self.dispatch()
        self.assertEqual([], os.listdir(self.views["Root"].fd))

    def test_observer_failure_retains_partial_root_unknown_and_forbids_reexecution(self):
        with patch.object(operation, "independent_readback", side_effect=OSError("observer unavailable")):
            with self.assertRaises(OSError): self.dispatch()
        self.assertTrue(os.listdir(self.views["Root"].fd))
        self.assertEqual("OutcomeUnknown", self.events[-1][1]["Record"]["Outcome"])
        with self.assertRaises(ValueError): self.dispatch()

    def test_after_intent_stream_substitution_is_detected(self):
        count = 0
        def substitute():
            nonlocal count
            count += 1
            if count == 2:
                path = self.folder / "root.content"; path.write_bytes(b"changed")
        self.connected.verify = substitute
        with self.assertRaises(ValueError): self.dispatch()
        self.assertEqual([], os.listdir(self.views["Root"].fd))
        self.assertEqual("OutcomeUnknown", self.events[-1][1]["Record"]["Outcome"])

    def test_expired_source_pin_stops_before_intent(self):
        original = self.ask
        def expired(kind, **values):
            response = original(kind, **values)
            if kind == "AuthenticateImportSource": response["ExpiresAt"] = "2020-01-01T00:00:00+00:00"
            return response
        self.session.channel.ask = expired
        with self.assertRaises(ValueError): self.dispatch()
        self.assertFalse(any(k == "ImportCheckpoint" for k, _ in self.events))

    def test_insufficient_capacity_precedes_reservation(self):
        with patch.object(root.os, "fstatvfs", return_value=SimpleNamespace(f_bavail=0, f_blocks=0, f_frsize=4096)):
            with self.assertRaisesRegex(ValueError, "Capacity"): self.dispatch()
        self.assertFalse(any(k == "ImportCheckpoint" for k, _ in self.events))
        self.assertEqual([], os.listdir(self.views["Root"].fd))

    def test_metadata_failure_retains_partial_state(self):
        with patch.object(root, "_metadata", side_effect=OSError("fixture restoration failure")):
            with self.assertRaises(OSError): self.dispatch()
        self.assertTrue(os.listdir(self.views["Root"].fd))
        self.assertEqual("Failed", self.events[-1][1]["Record"]["Outcome"])

    def test_result_publication_failure_never_returns_success(self):
        original = self.ask
        def failed(kind, **values):
            if kind == "ImportCheckpoint" and values["Record"]["Outcome"] == "AppliedAndVerified":
                raise OSError("fixture result fsync failure")
            return original(kind, **values)
        self.session.channel.ask = failed
        with self.assertRaises(OSError): self.dispatch()
        self.assertEqual("Failed", self.events[-1][1]["Record"]["Outcome"])
        self.assertTrue(self.session.failed)


class JournalBindingTests(unittest.TestCase):
    def test_wrong_store_witness_never_reserves_generation(self):
        import deployment_journal
        import uuid
        with tempfile.TemporaryDirectory(prefix="igloo-journal-witness-") as store:
            os.chmod(store, 0o700)
            with self.assertRaisesRegex(ValueError, "substituted"):
                deployment_journal.perform(store, str(uuid.uuid4()), "A" * 64, "reserve", expected_store=[0, 0, 0])
            self.assertEqual([], os.listdir(store))
