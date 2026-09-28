#!/usr/bin/env python3
"""Corrupt the COMPLETE real stream in a separate VM-owned regular-file copy.

This supplements the small real-metadata derivatives. No source artifact is
modified; each rejected import has a different empty target and journal. The
fixture retains every failed copy/target and never supplies a block device.
"""
import datetime as dt
import json
import os
from pathlib import Path
import shutil
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
import configured_root as artifact
from factory_artifact_verifier import authenticate
from factory_neutralization_rehearsal import checkpoint, environment
from factory_root_observer import digest


def run(workspace):
    environment(workspace)
    qualified = json.loads((workspace / "artifact-independent-verification.json").read_bytes())
    artifact.require(qualified["Result"] == "ArtifactValid", "SourceNotQualified")
    trust = json.loads((workspace / "external-development-pin.json").read_bytes())
    now = dt.datetime.now(dt.timezone.utc)
    value = authenticate((workspace / "artifact/descriptor.json").read_bytes(), trust, now)
    manifest = (workspace / "artifact/root.manifest.json").read_bytes()
    original = workspace / "artifact/root.content"
    directory = workspace / "full-source-failures"; directory.mkdir(mode=0o700)
    source = directory / "content-copy"
    checkpoint(directory, "copy-intent", {"Outcome": "IntentDurable", "SourceSha256": trust["ContentSha256"],
               "Destination": str(source), "Bytes": value["Content"]["Length"]})
    with original.open("rb") as input_file, source.open("xb") as output:
        shutil.copyfileobj(input_file, output, 1024 * 1024); output.flush(); os.fsync(output.fileno())
    artifact.require(digest(source) == trust["ContentSha256"] and source.stat().st_ino != original.stat().st_ino,
                     "FailureCopyNotExactIndependentFile")
    checkpoint(directory, "copy-result", {"Outcome": "AppliedAndVerified", "Sha256": digest(source)})
    pin = artifact.DevelopmentPin(value["BuildId"], trust["ManifestSha256"], trust["ContentSha256"],
                                   dt.datetime.fromisoformat(trust["NotAfterUtc"]))
    outcomes = []

    def rejected(name, data, stream_path):
        target = directory / (name + "-target"); target.mkdir(mode=0o700)
        store = directory / (name + "-journal"); store.mkdir(mode=0o700)
        generation = str(uuid.uuid4()); plan = artifact.digest(name.encode())
        journal = artifact.ImportJournal(str(store), generation, plan, value["BuildId"])
        fd = os.open(target, os.O_DIRECTORY | os.O_NOFOLLOW); state = os.fstat(fd)
        view = artifact.RootView(fd, state.st_dev, state.st_ino, artifact.mount_id(fd))
        stream = os.open(stream_path, os.O_RDONLY | os.O_NOFOLLOW)
        try:
            try:
                artifact.import_development_fixture(view, data, stream, pin, journal, generation, plan, now)
            except artifact.Rejected as error:
                reason = str(error)
            else:
                raise AssertionError("Corrupted full source accepted")
            artifact.require(not list(target.iterdir()) and not list(store.iterdir()), "CorruptSourceReachedImportIntent")
            result = {"Case": name, "Outcome": "NotStarted", "RejectedBeforeIntent": True,
                      "TargetUnchanged": True, "Reason": reason, "Generation": generation}
            checkpoint(directory, name + "-result", result); outcomes.append(result)
        finally:
            os.close(stream); view.close(); os.close(fd)

    # Deliberate mutations apply only to the independently verified test copy.
    with original.open("rb") as f:
        f.seek(-1, os.SEEK_END); last = f.read(1)
    checkpoint(directory, "corruption-intent", {"Outcome": "IntentDurable", "Copy": str(source), "Operation": "FlipLastByte"})
    with source.open("r+b") as f:
        f.seek(-1, os.SEEK_END); f.write(bytes([last[0] ^ 1])); f.flush(); os.fsync(f.fileno())
    rejected("complete-stream-byte-corruption", manifest, source)
    # Restoring a test SOURCE is not retrying a partially imported target. Both
    # rejected target roots remain untouched and are never used again.
    with source.open("r+b") as f:
        f.seek(-1, os.SEEK_END); f.write(last); f.flush(); os.fsync(f.fileno())
    artifact.require(digest(source) == trust["ContentSha256"], "RestoredFailureSourceChanged")
    checkpoint(directory, "truncation-intent", {"Outcome": "IntentDurable", "Copy": str(source), "Operation": "TruncateOneByte"})
    with source.open("r+b") as f:
        f.truncate(value["Content"]["Length"] - 1); f.flush(); os.fsync(f.fileno())
    rejected("complete-stream-truncation", manifest, source)
    rejected("complete-manifest-corruption", manifest[:-1], original)
    artifact.require(digest(original) == trust["ContentSha256"], "OriginalSourceChanged")
    checkpoint(workspace, "full-source-failure-tests", {"Passed": len(outcomes), "Failed": 0, "Skipped": 0,
               "CompleteSourceBytes": value["Content"]["Length"], "OriginalSourceUnchanged": True,
               "Results": outcomes, "FailedCopiesRetained": True})
    print("3 complete-source failures rejected before import intent; original unchanged", flush=True)


if __name__ == "__main__": run(Path(sys.argv[1]))
