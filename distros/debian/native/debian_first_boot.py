#!/usr/bin/env python3
"""Restricted first-boot evidence worker. No subprocess, network or deployment cleanup.

Privileged user-data import/enrollment are separate producers. Their required,
generation-bound receipts must already exist; absence is failure, never success.
This worker cannot implement those operations under DynamicUser/ProtectHome.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import uuid

PROFILE = "debian-first-boot-evidence-v1"
INPUT_ROOT = Path("/var/lib/igloo/first-boot-input")
STATE_ROOT = Path("/var/lib/igloo-deployment")
MAX_INPUT = 1024 * 1024


class Rejected(ValueError):
    pass


def require(value, code):
    if not value:
        raise Rejected(code)


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def decode(data):
    def unique(pairs):
        out = {}
        for key, value in pairs:
            require(key not in out, "DuplicateEvidenceKey")
            out[key] = value
        return out
    return json.loads(data, object_pairs_hook=unique)


def open_directory(path, *, final_owner):
    path = Path(path)
    require(path.is_absolute() and ".." not in path.parts, "UnsafeEvidenceDirectory")
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        for part in path.parts[1:]:
            parent = os.fstat(fd)
            require(parent.st_uid == 0 and stat.S_IMODE(parent.st_mode) & 0o022 == 0, "UnprotectedEvidenceAncestor")
            next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
            os.close(fd)
            fd = next_fd
        info = os.fstat(fd)
        require(info.st_uid == final_owner and stat.S_IMODE(info.st_mode) & 0o022 == 0, "EvidenceDirectoryOwnerOrMode")
        return fd
    except BaseException:
        os.close(fd)
        raise


def state_directory():
    # systemd.exec(5) documents DynamicUser's one exact StateDirectory indirection.
    # Do not follow arbitrary links. Pin and inspect the root-owned parent first,
    # then open the known canonical destination without traversing any link.
    parent = open_directory(STATE_ROOT.parent, final_owner=0)
    try:
        info = os.stat(STATE_ROOT.name, dir_fd=parent, follow_symlinks=False)
        if stat.S_ISLNK(info.st_mode):
            require(info.st_uid == 0 and os.readlink(STATE_ROOT.name, dir_fd=parent) in
                    ("private/igloo-deployment", "/var/lib/private/igloo-deployment"), "UnexpectedStateDirectoryLink")
            return open_directory("/var/lib/private/igloo-deployment", final_owner=os.geteuid())
        return open_directory(STATE_ROOT, final_owner=os.geteuid())
    finally:
        os.close(parent)


def read_at(directory, name, owner):
    require(re.fullmatch(r"[A-Za-z0-9_.-]+", name) and name not in (".", ".."), "UnsafeEvidenceName")
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=directory)
    try:
        info = os.fstat(fd)
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_uid == owner and
                stat.S_IMODE(info.st_mode) & 0o022 == 0 and 0 < info.st_size <= MAX_INPUT, "UnprotectedEvidenceFile")
        data = bytearray()
        while block := os.read(fd, 65536):
            data.extend(block)
            require(len(data) <= MAX_INPUT, "OversizedEvidence")
        after = os.fstat(fd)
        require((info.st_size, info.st_mtime_ns, info.st_ctime_ns) == (after.st_size, after.st_mtime_ns, after.st_ctime_ns), "EvidenceChangedDuringRead")
        return bytes(data)
    finally:
        os.close(fd)


def validate_configuration(config):
    require(set(config) == {"SchemaVersion", "GenerationId", "Profile", "WorkerSha256", "RequiredReceipts"}, "WorkerConfigurationShape")
    require(config["SchemaVersion"] == 1 and config["Profile"] == PROFILE and uuid.UUID(config["GenerationId"]).int != 0, "WorkerGenerationOrProfile")
    require(re.fullmatch(r"[0-9A-F]{64}", config["WorkerSha256"]), "WorkerHashMissing")
    requirements = config["RequiredReceipts"]
    require(isinstance(requirements, list) and requirements, "RequiredCompletionEvidenceMissing")
    allowed = {"DeploymentContent", "UserData", "Enrollment"}
    require(len({r["Kind"] for r in requirements}) == len(requirements) and
            any(r["Kind"] == "DeploymentContent" for r in requirements), "CompletionRequirementsIncomplete")
    for item in requirements:
        require(set(item) == {"Kind", "FileName", "Sha256"} and item["Kind"] in allowed and
                re.fullmatch(r"[a-z0-9-]+\.json", item["FileName"]) and re.fullmatch(r"[0-9A-F]{64}", item["Sha256"]),
                "InvalidCompletionRequirement")
    require(len({r["FileName"] for r in requirements}) == len(requirements), "DuplicateCompletionFile")


def persist(directory, name, value):
    try:
        os.stat(name, dir_fd=directory, follow_symlinks=False)
    except FileNotFoundError:
        pass
    else:
        raise FileExistsError("WorkerEvidenceAlreadyExists")
    data = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    temporary = ".pending-" + str(uuid.uuid4())
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600, dir_fd=directory)
    try:
        with os.fdopen(fd, "wb", closefd=False) as stream:
            stream.write(data)
            stream.flush()
            os.fsync(fd)
    finally:
        os.close(fd)
    # Publish only after file fsync. link is create-new (no replacement), unlike
    # rename. A directory fsync failure after publication is still OutcomeUnknown;
    # the receipt consumer must also require this systemd invocation's exit evidence.
    os.link(temporary, name, src_dir_fd=directory, dst_dir_fd=directory, follow_symlinks=False)
    os.unlink(temporary, dir_fd=directory)
    os.fsync(directory)
    require(read_at(directory, name, os.geteuid()) == data, "WorkerDurableReopenFailed")
    return digest(data)


def validate_completion_receipt(receipt, generation, kind):
    require(set(receipt) == {"SchemaVersion", "GenerationId", "Kind", "State", "EvidenceSha256"} and
                    receipt["SchemaVersion"] == 1 and receipt["GenerationId"] == generation and
                    receipt["Kind"] == kind and receipt["State"] == "AppliedAndVerified" and
                    re.fullmatch(r"[0-9A-F]{64}", receipt["EvidenceSha256"]), "RequiredReceiptIncomplete")


def execute(config_bytes, worker_bytes, inputs_fd, state_fd, invocation_id, *, input_owner=0):
    config = decode(config_bytes)
    validate_configuration(config)
    require(digest(worker_bytes) == config["WorkerSha256"], "WorkerPayloadChanged")
    require(uuid.UUID(invocation_id).int != 0, "ServiceInvocationMissing")
    base = {"SchemaVersion": 1, "GenerationId": config["GenerationId"], "Profile": PROFILE,
            "InvocationId": str(uuid.UUID(invocation_id)),
            "WorkerSha256": digest(worker_bytes), "ConfigurationSha256": digest(config_bytes)}
    # Existing intent means a previous process may have run. Never overwrite, retry or
    # convert an unknown outcome into success. No exception text enters the receipt.
    intent = persist(state_fd, "intent.json", {**base, "State": "FirstBootOutcomeUnknown"})
    try:
        verified = []
        for item in config["RequiredReceipts"]:
            data = read_at(inputs_fd, item["FileName"], input_owner)
            require(digest(data) == item["Sha256"], "RequiredReceiptHashMismatch")
            receipt = decode(data)
            validate_completion_receipt(receipt, config["GenerationId"], item["Kind"])
            verified.append(item)
        result = {**base, "State": "FirstBootSucceeded", "IntentSha256": intent, "VerifiedReceipts": verified}
        persist(state_fd, "outcome.json", result)
        return 0
    except (OSError, ValueError, KeyError, TypeError):
        # If persistence fails too, durable intent remains OutcomeUnknown. Never emit success.
        persist(state_fd, "outcome.json", {**base, "State": "FirstBootFailed", "IntentSha256": intent, "VerifiedReceipts": []})
        return 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", choices=("/etc/igloo/deployment.json",), required=True)
    args = parser.parse_args()
    descriptors = []
    try:
        config_dir = open_directory(Path(args.config).parent, final_owner=0); descriptors.append(config_dir)
        worker_dir = open_directory(Path(__file__).absolute().parent, final_owner=0); descriptors.append(worker_dir)
        inputs = open_directory(INPUT_ROOT, final_owner=0); descriptors.append(inputs)
        state = state_directory(); descriptors.append(state)
        return execute(read_at(config_dir, "deployment.json", 0), read_at(worker_dir, Path(__file__).name, 0), inputs, state,
                       os.environ.get("INVOCATION_ID", ""))
    except (OSError, ValueError, KeyError, TypeError):
        return 1
    finally:
        for descriptor in descriptors:
            os.close(descriptor)


if __name__ == "__main__":
    sys.exit(main())
