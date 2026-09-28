"""Protocol/lease failures only. Does not open, mount or format a physical block."""
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import uuid
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "distros/debian/native"))
import block_session as session
from isolation_policy import Rejected
from mount_supervisor import BlockLease
from package_broker import DirectoryLease


def declaration():
    return {"SessionId": str(uuid.uuid4()), "GenerationId": str(uuid.uuid4()), "PlanSha256": "A" * 64,
            "ParentMountNamespace": "mnt:[fixture]"}


class MessageTests(unittest.TestCase):
    def test_duplicate_property(self):
        with self.assertRaises(Rejected):
            session.read_message(io.BytesIO(b'{"x":1,"x":2}\n'))

    def test_pipe_eof(self):
        with self.assertRaises(Rejected):
            session.read_message(io.BytesIO(b''))

    def test_unterminated_message(self):
        with self.assertRaises(Rejected):
            session.read_message(io.BytesIO(b'{}'))

    def test_oversized(self):
        with patch.object(session, "LIMIT", 8), self.assertRaises(Rejected):
            session.read_message(io.BytesIO(b'{"x":1000}\n'))

    def test_wrong_challenge(self):
        channel = session.AuthorityChannel(declaration(), io.BytesIO(b'{"Accepted":true,"Challenge":"wrong"}\n'), io.BytesIO())
        with self.assertRaises(Rejected):
            channel.ask("Inventory")

    def test_no_ack_does_not_return_to_effect(self):
        channel = session.AuthorityChannel(declaration(), io.BytesIO(), io.BytesIO())
        with self.assertRaises(Rejected):
            channel.checkpoint({"State": "IntentDurable"})

    def test_challenge_and_generation_are_bound(self):
        sink = io.BytesIO(); declaration_value = declaration()
        channel = session.AuthorityChannel(declaration_value, io.BytesIO(), sink)
        expected = uuid.uuid4()
        with patch.object(session.uuid, "uuid4", return_value=expected):
            channel.source = io.BytesIO(json.dumps({"Challenge": str(expected), "Accepted": True}).encode() + b'\n')
            channel.ask("Inventory")
        self.assertEqual(declaration_value["SessionId"], json.loads(sink.getvalue())["SessionId"])


class LeaseTests(unittest.TestCase):
    def test_ordinary_file_is_not_block_identity(self):
        with tempfile.TemporaryDirectory(prefix="igloo-lease-fixture-") as work:
            path = Path(work) / "block"; path.write_bytes(b'fixture')
            fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC)
            try:
                blocks = session.CanonicalBlocks(declaration(), Mock(), Mock())
                with self.assertRaises(Rejected):
                    blocks._descriptor(BlockLease("generation", "A" * 64, "Root", fd, 8, 1, "B" * 64), {"Partition": {"SizeBytes": 7}})
            finally:
                os.close(fd)

    def test_missing_canonical_authority_cannot_open_any_device(self):
        blocks = session.CanonicalBlocks(declaration(), Mock(), Mock())
        with patch.object(blocks, "inventory", side_effect=Rejected("Unavailable")), patch.object(session.os, "open") as opened:
            with self.assertRaises(Rejected):
                blocks.acquire()
            opened.assert_not_called()
        self.assertTrue(blocks.failed)

    def test_intent_failure_cannot_open_any_device(self):
        channel = Mock(); channel.checkpoint.side_effect = OSError("fsync")
        blocks = session.CanonicalBlocks(declaration(), Mock(), channel)
        with patch.object(blocks, "inventory", return_value=[]), patch.object(session.os, "open") as opened:
            with self.assertRaises(OSError):
                blocks.acquire()
            opened.assert_not_called()
        self.assertTrue(blocks.failed)

    def test_identity_changes_after_intent_cannot_open(self):
        blocks = session.CanonicalBlocks(declaration(), Mock(), Mock())
        with patch.object(blocks, "inventory", side_effect=[[], Rejected("Changed")]), patch.object(session.os, "open") as opened:
            with self.assertRaises(Rejected):
                blocks.acquire()
            opened.assert_not_called()

    def test_second_acquisition_rejected(self):
        blocks = session.CanonicalBlocks(declaration(), Mock(), Mock()); blocks.failed = True
        with patch.object(blocks, "inventory") as inventory, self.assertRaises(Rejected):
            blocks.acquire()
        inventory.assert_not_called()

    def test_stale_lease_cannot_reacquire(self):
        blocks = session.CanonicalBlocks(declaration(), Mock(), Mock())
        lease = BlockLease("g", "A" * 64, "Root", 3, 8, 1, "B" * 64)
        with self.assertRaises(Rejected):
            blocks.revalidate(lease)

    def test_fresh_identity_failure_invalidates_active_lease(self):
        blocks = session.CanonicalBlocks(declaration(), Mock(), Mock())
        lease = BlockLease("g", "A" * 64, "Root", 3, 8, 1, "B" * 64); blocks.leases["Root"] = lease
        with patch.object(blocks, "inventory", side_effect=Rejected("Changed")), self.assertRaises(Rejected):
            blocks.revalidate(lease)
        self.assertTrue(blocks.failed)


class ConnectedRootRegressionTests(unittest.TestCase):
    def test_disconnected_mount_fd_rejected_even_with_same_inode(self):
        with tempfile.TemporaryDirectory(prefix="igloo-connected-fixture-") as path:
            lease = DirectoryLease(path)
            try:
                with patch("package_broker.mount_records", return_value=[]), self.assertRaisesRegex(Rejected, "DetachedResourceDescriptor"):
                    lease.verify()
            finally:
                lease.close()

    def test_fd_expanding_to_host_root_rejected_before_bwrap(self):
        with tempfile.TemporaryDirectory(prefix="igloo-connected-fixture-") as path:
            lease = DirectoryLease(path)
            try:
                with patch("package_broker.os.readlink", return_value="/"), self.assertRaisesRegex(Rejected, "DetachedResourceDescriptor"):
                    lease.verify()
            finally:
                lease.close()

    def test_replaced_named_root_rejected(self):
        with tempfile.TemporaryDirectory(prefix="igloo-connected-fixture-") as work:
            path = Path(work) / "root"; path.mkdir(); lease = DirectoryLease(str(path))
            try:
                path.rename(Path(work) / "old"); path.mkdir()
                with self.assertRaises(Rejected):
                    lease.verify()
            finally:
                lease.close()


class SessionOrderTests(unittest.TestCase):
    def make_session(self):
        with patch.object(session, "ExactMountSupervisor"):
            result = session.MountSession(declaration(), Mock(), Mock())
        return result

    def test_command_execution_not_exposed(self):
        value = self.make_session()
        with self.assertRaises(ValueError):
            value.perform("Bootstrap")
        self.assertTrue(value.failed)

    def test_dirty_session_cannot_close(self):
        value = self.make_session(); value.supervisor.receipts = [{"Role": "Root"}]
        with self.assertRaises(Rejected):
            value.perform("Close")
        value.channel.checkpoint.assert_not_called()

    def test_wrong_unmount_order_rejected(self):
        value = self.make_session(); value.supervisor.receipts = [{"Role": "Root"}, {"Role": "LinuxEsp"}]
        with self.assertRaises(Rejected):
            value.perform("UnmountRoot")
        value.supervisor.unmount_next.assert_not_called()

    def test_inspect_requires_acquisition(self):
        with self.assertRaises(Rejected):
            self.make_session().perform("Inspect")

    def test_closed_session_cannot_be_reused(self):
        value = self.make_session(); value.supervisor.receipts = []
        value.perform("Close")
        with self.assertRaises(Rejected):
            value.perform("AcquireLeases")

    def test_teardown_forbids_remount(self):
        value = self.make_session(); value.teardown = True
        with self.assertRaises(Rejected):
            value.perform("MountRoot")
        value.supervisor.mount_block.assert_not_called()
