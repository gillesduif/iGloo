"""Isolated-python entrypoint: authenticate the protected module tree before imports."""
import hashlib
import json
import os
from pathlib import Path
import stat
import sys
import types


def main():
    raw = sys.stdin.buffer.readline(8 * 1024 * 1024 + 1)
    if len(raw) > 8 * 1024 * 1024 or not raw.endswith(b"\n"):
        raise ValueError("Invalid session declaration")
    def pairs(values):
        result = {}
        for key, value in values:
            if key in result:
                raise ValueError("Duplicate declaration key")
            result[key] = value
        return result
    declaration = json.loads(raw, object_pairs_hook=pairs)
    directory = Path(__file__).resolve().parent
    modules = ("target_files.py", "isolation_policy.py", "isolation_observer.py", "package_broker.py", "mount_supervisor.py",
               "target_observer.py", "deployment_journal.py", "configured_root_metadata.py", "root_transport.py", "configured_root.py",
               "session_import.py", "block_session.py")
    hashes = declaration["ToolHashes"]
    if not {str(directory / name) for name in modules} <= set(hashes):
        raise ValueError("Incomplete module manifest")
    sources = {}
    for path, expected in hashes.items():
        info = os.stat(path, follow_symlinks=False)
        if os.path.realpath(path, strict=True) != path or not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o6022:
            raise ValueError("Unsafe session tool")
        for parent in Path(path).parents:
            owner = parent.stat()
            if owner.st_uid != 0 or owner.st_mode & 0o022 and not owner.st_mode & stat.S_ISVTX:
                raise ValueError("Unprotected session directory")
        content = Path(path).read_bytes()
        if hashlib.sha256(content).hexdigest().upper() != expected:
            raise ValueError("Session tool changed")
        if str(Path(path).parent) == str(directory) and Path(path).name in modules:
            sources[Path(path).name] = content
    # Compile the verified source bytes directly. -B alone would still READ an
    # unmanifested __pycache__ or extension module through normal import lookup.
    # Dependencies are loaded in order; neither CWD nor PYTHONPATH is introduced.
    for name in modules:
        module = types.ModuleType(name[:-3])
        module.__file__ = str(directory / name)
        module.__package__ = ""
        sys.modules[module.__name__] = module
        exec(compile(sources[name], module.__file__, "exec"), module.__dict__)
    if "ImportObservation" in declaration:
        return sys.modules["session_import"].observe_import(declaration)
    if "ImportJournalPreflight" in declaration:
        runtime = sys.modules["package_broker"].Runtime(**declaration["Runtime"])
        runtime.verify()
        inventory, numbers = sys.modules["block_session"].collect_stable_inventory(declaration, runtime)
        stores = sys.modules["session_import"].journal_store_observation(declaration["ImportJournalPreflight"])
        print(json.dumps({"Inventory": inventory, "DeviceNumbers": numbers, "Stores": stores}, separators=(",", ":")))
        return 0
    return sys.modules["block_session"].main(declaration)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, TypeError):
        sys.exit(1)
