#!/usr/bin/env python3
"""Explicit offline VM derivation harness. Not imported by migration dispatch.

Only a previously derived, evidence-bound root below /factory-neutral is accepted.
Source root is read-only to this harness; commands run only in the derived VM.
The external controller separately proves virtual-disk ancestry/no passthrough.
"""
import argparse
import base64
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "distros/debian/native"))
import configured_root as artifact
import configured_root_neutralization as neutral
import factory_root_observer as observer
import factory_publication_gate as gate
import factory_neutral_audit as secrets

BUILD = "99695826-82bc-4077-b4cf-a22cebc706da"
SOURCE_AUDIT = "A76733DB00ECD9FB0703B4F8D31B4ECE1D3274ADA07B7F89791039AFF2C6A579"
PACKAGE_SET = "BE86797B73250B19569BC6A229EB1C13D22A651DEAEF315CE8C10406A5AD1272"


def checkpoint(directory, name, value):
    data = artifact.canonical(value); path = directory / (name + ".json")
    with path.open("xb") as output:
        output.write(data); output.flush(); os.fsync(output.fileno())
    fd = os.open(directory, os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)
    observed = subprocess.check_output(["/usr/bin/sha256sum", str(path)], timeout=30).decode().split()[0].upper()
    artifact.require(observed == artifact.digest(data), "CheckpointIndependentReopen")
    return observed


def environment(workspace):
    artifact.require(workspace.parent == Path("/factory-neutral") and str(uuid.UUID(workspace.name)) == workspace.name,
                     "NotDerivedFactoryWorkspace")
    artifact.require(os.geteuid() == 0 and Path("/sys/class/dmi/id/sys_vendor").read_text().strip() == "QEMU" and
                     not Path("/sys/firmware/efi").exists() and sorted(p.name for p in Path("/sys/class/net").iterdir()) == ["lo"], "NotOfflineFactoryVm")
    artifact.require(Path("/sys/block/vda/serial").read_text().strip() == "IGLOO-FACTORY", "FactoryDiskChanged")
    derived = json.loads((workspace / "derivation-result.json").read_bytes())
    artifact.require(derived == {"Outcome": "AppliedAndVerified", "SourceUnchanged": True,
                                 "DerivedSemanticEquality": True, "Entries": 144936}, "DerivationUnavailable")
    source = Path("/factory-evidence") / BUILD / "independent-root-audit.json"
    artifact.require(observer.digest(source) == SOURCE_AUDIT and observer.digest(Path("/input/bundle/package-set.json")) == PACKAGE_SET,
                     "SourceEvidenceChanged")
    artifact.require(not (workspace / "root").is_symlink(), "DerivedRootSubstitution")
    return json.loads(source.read_bytes()), json.loads(Path("/input/bundle/package-set.json").read_bytes())


def package_readback(workspace, number):
    # Each observation is a NEW process invoking independent dpkg query/audit.
    process = subprocess.run([sys.executable, "-B", str(Path(__file__).resolve()), "packages", str(workspace)],
                             stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120, check=False)
    artifact.require(process.returncode == 0, "PackageObserverUnavailable")
    result = json.loads(process.stdout)
    checkpoint(workspace, "packages-" + number, result)
    artifact.require(result["PackageStateQualified"] and len(result["Packages"]) == 1594, "PackageStateChanged")
    return result


def execute(workspace):
    raw, packages = environment(workspace); root = workspace / "root"
    view_fd = os.open(root, os.O_DIRECTORY | os.O_NOFOLLOW)
    view = artifact.RootView(view_fd, os.fstat(view_fd).st_dev, os.fstat(view_fd).st_ino, artifact.mount_id(view_fd))
    try:
        before = observer.inventory(root)
        artifact.require(before["Entries"] == raw["Filesystem"]["Entries"] and
                         before["HardlinkGroups"] == raw["Filesystem"]["HardlinkGroups"], "DerivedBeforeChanged")
        plan = neutral.plan(raw, SOURCE_AUDIT, PACKAGE_SET, packages["PolicySha256"], (root / "etc/exim4/update-exim4.conf.conf").read_bytes())
        plan_hash = checkpoint(workspace, "neutralization-plan", plan)
        package_readback(workspace, "before")
        # Supported debconf API, not hand editing its database or invoking dpkg
        # configuration/maintainer scripts. The backup remains an explicit object.
        old = (root / "var/cache/debconf/config.dat").read_bytes()
        checkpoint(workspace, "debconf-intent", {"PlanSha256": plan_hash, "Before": plan["Debconf"],
                   "Argv": ["/usr/bin/debconf-communicate", "exim4-config"], "Outcome": "IntentDurable"})
        commands = b"SET exim4/dc_other_hostnames \nSET exim4/mailname \nFSET exim4/dc_other_hostnames mailname false\n"
        result = subprocess.run(["/usr/sbin/chroot", str(root), "/usr/bin/debconf-communicate", "exim4-config"], input=commands,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30, check=False,
                                env={"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C.UTF-8", "DEBIAN_FRONTEND": "noninteractive"})
        artifact.require(result.returncode == 0 and result.stdout.splitlines() == [b"0 value set", b"0 value set", b"0 false"], "DebconfOperationUnverified")
        changed = (root / "var/cache/debconf/config.dat").read_bytes()
        neutral.verify_debconf(old, changed)
        checkpoint(workspace, "debconf-result", {"Outcome": "AppliedAndVerified", "CurrentSha256": artifact.digest(changed)})
        backup = neutral.observe_object(view, "/var/cache/debconf/config.dat-old")
        artifact.require(backup["Sha256"] == artifact.digest(old), "DebconfBackupNotNominatedInput")
        change = {"Path": "/var/cache/debconf/config.dat-old", "Operation": "Remove", "Before": backup, "After": neutral.absent()}
        checkpoint(workspace, "debconf-backup-intent", {"Outcome": "IntentDurable", "Change": change, "Reason": "Exact pre-neutralization backup; current database retains all other state"})
        neutral.apply_object(view, change)
        checkpoint(workspace, "debconf-backup-result", {"Outcome": "AppliedAndVerified", "After": neutral.observe_object(view, change["Path"])})
        package_readback(workspace, "debconf")
        for number, change in enumerate(plan["Changes"]):
            name = "object-" + str(number).zfill(3)
            checkpoint(workspace, name + "-intent", {"PlanSha256": plan_hash, "Change": change, "Outcome": "IntentDurable"})
            try:
                neutral.apply_object(view, change)
                package_readback(workspace, name)
                checkpoint(workspace, name + "-result", {"Outcome": "AppliedAndVerified", "After": neutral.observe_object(view, change["Path"])})
            except (OSError, ValueError, subprocess.SubprocessError):
                # Do not erase partial files or promote an exception to NotStarted.
                try: observed = neutral.observe_object(view, change["Path"])
                except (OSError, ValueError): observed = None
                checkpoint(workspace, name + "-result", {"Outcome": "Failed" if observed is not None else "OutcomeUnknown", "Observed": observed})
                raise
        # Compare the ENTIRE transformed tree to the exact planned delta. Not just
        # the paths that the mutator remembers writing.
        after = observer.inventory(root)
        expected = {e["Path"]: e for e in before["Entries"]}
        for change in plan["Changes"]:
            if change["After"]["Type"] == "Absent": expected.pop(change["Path"])
            else: expected[change["Path"]] = change["After"]
        expected.pop("/var/cache/debconf/config.dat-old")
        expected["/var/cache/debconf/config.dat"] = neutral.observe_object(view, "/var/cache/debconf/config.dat")
        artifact.require(sorted(expected.values(), key=lambda e: e["Path"]) == after["Entries"], "UnexpectedNeutralizationDelta")
        artifact.require(after["HardlinkGroups"] == before["HardlinkGroups"], "NeutralizationChangedHardlinkTopology")
        checkpoint(workspace, "neutralization-result", {"Outcome": "AppliedAndVerified", "PlanSha256": plan_hash,
                   "PostFilesystemObservationSha256": artifact.digest(artifact.canonical(after)), "ExactDeltaVerified": True})
    finally:
        view.close(); os.close(view_fd)


def readback(workspace):
    raw, packages = environment(workspace); root = workspace / "root"
    output = workspace / "verification-v2"; output.mkdir(mode=0o700)
    observed = observer.package_observation(root, packages); observed["Filesystem"] = observer.inventory(root)
    checkpoint(output, "neutral-root-observation", observed)
    gate_result = gate.evaluate(root, observed)
    checkpoint(output, "neutral-publication-gate", gate_result)
    tokens = [b"igloo-factory", b"/input/bundle", ("/factory/root-" + BUILD).encode(),
              b"/tmp/igloo-241-factory", BUILD.encode(), b"9a4856c180c241468a74380f663ddf94"]
    secret_result = secrets.audit(root, observed["Filesystem"]["Entries"], tokens)
    checkpoint(output, "whole-tree-neutral-audit", secret_result)
    artifact.require(gate_result["PublicationChecksPassed"] and secret_result["Qualified"], "NeutralPublicationBlocked")
    print(json.dumps({"Gate": gate_result, "SecretAudit": secret_result}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("action", choices=("execute", "readback", "packages")); parser.add_argument("workspace", type=Path)
    args = parser.parse_args()
    if args.action == "packages":
        _, package_set = environment(args.workspace)
        print(json.dumps(observer.package_observation(args.workspace / "root", package_set)))
    elif args.action == "execute": execute(args.workspace)
    else: readback(args.workspace)
