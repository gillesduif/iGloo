"""Native mount-system-call fixtures; mounts tmpfs only inside a private namespace.

No loop devices, physical block opens or format operation. The parent invokes
this only through command_gate --supervise and independently checks no host mount
was added. Every failure exits the disposable namespace; this is not a claim of
successful production unmount or recovery after a failed operation.
"""
import json
import os
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "distros/debian/native"))
from package_broker import DirectoryLease, Runtime
from mount_supervisor import ExactMountSupervisor
from isolation_observer import namespace_ids


def run(request):
    runtime = Runtime(**request["Runtime"])
    records = []
    case = request["Case"]
    fired = False

    def checkpoint(value):
        nonlocal fired
        if case == "intent-failure" or (case == "result-failure" and value["State"] == "AppliedAndVerified"):
            fired = True
            raise OSError("fixture fsync failure")
        records.append(value)

    with tempfile.TemporaryDirectory(prefix="igloo-private-mount-") as work:
        base = Path(work)
        a, b = base / "first", base / "second"
        a.mkdir(); b.mkdir()
        left, right = DirectoryLease(str(a)), DirectoryLease(str(b))
        session = ExactMountSupervisor(request["Generation"], "A"*64, request["ParentNamespace"], runtime,
                                       lambda _: (_ for _ in ()).throw(ValueError("No physical device in this fixture")), checkpoint)
        observed = "NotStarted"
        busy = None
        try:
            if case == "syscall-failure":
                with patch.object(session.calls, "detached", side_effect=OSError("fixture")):
                    session.mount_scratch(left)
            elif case == "observer-failure":
                with patch.object(session, "_observe", side_effect=OSError("fixture")):
                    session.mount_scratch(left)
            elif case == "attach-partial":
                original = session.calls.attach

                def partial(*args):
                    original(*args)
                    raise OSError("applied then unavailable")
                with patch.object(session.calls, "attach", side_effect=partial):
                    session.mount_scratch(left)
            else:
                session.mount_scratch(left)
                if case == "success":
                    session.mount_scratch(right)
                    second = session.unmount_next()
                    first = session.unmount_next()
                    assert second["Path"] == str(b) and first["Path"] == str(a)
                    assert not session.receipts
                    observed = "AppliedAndVerified"
                elif case == "busy":
                    busy = os.open(a, os.O_RDONLY | os.O_DIRECTORY)
                    session.unmount_next()
                elif case == "unmount-failure":
                    with patch.object(session.calls, "unmount", side_effect=OSError("busy fixture")):
                        session.unmount_next()
                elif case == "mount-replaced":
                    # Rename inside the owned disposable directory, never a host mount.
                    a.rename(base / "moved")
                    a.mkdir()
                    session.unmount_next()
                else:
                    raise AssertionError("Expected injected failure")
        except (OSError, ValueError):
            observed = "RejectedWithIntent" if records else "RejectedBeforeIntent"
            if case not in ("observer-failure", "mount-replaced"):
                assert session.poisoned
            assert not any(r["Action"] == "Unmount" and r["State"] == "AppliedAndVerified" for r in records)
        finally:
            if busy is not None:
                os.close(busy)
            left.close(); right.close()
            # Do not recurse into remaining mounts. These names are inside our
            # private namespace and disappear at process exit. Parent removes only
            # the empty backing directories after confirming host mount absence.
            if any(m["Path"].startswith(work + "/") for m in __import__("isolation_observer").mount_records(Path("/proc/self/mountinfo").read_text())):
                result = {"Case": case, "State": observed, "Records": records, "RetainedFixture": work, "FsyncInjection": fired}
                print(json.dumps(result), flush=True)
                os._exit(0)
        return {"Case": case, "State": observed, "Records": records, "FsyncInjection": fired}


if __name__ == "__main__":
    print(json.dumps(run(json.loads(sys.stdin.read()))))
