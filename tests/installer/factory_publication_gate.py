#!/usr/bin/env python3
"""Fresh, read-only factory publication gate. Never edits or publishes the root.

This is intentionally a diagnostic until reviewed package-specific neutralization
exists. Raw factory metadata is retained, including unsupported special files;
there is no filtering step that can turn a failed tree into a successful artifact.
"""
import copy
import json
import os
from pathlib import Path
import stat
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "distros/debian/native"))
import configured_root as root_artifact


def candidate_manifest(root, audit):
    entries = copy.deepcopy(audit["Filesystem"]["Entries"])
    info = root.lstat()
    entries.append({"Path": "/", "Type": "Directory", "Uid": info.st_uid, "Gid": info.st_gid,
                    "Mode": stat.S_IMODE(info.st_mode), "Length": 0, "Sha256": None, "Target": None,
                    "Xattrs": {}})
    by_path = {e["Path"]: e for e in entries}
    for group in audit["Filesystem"]["HardlinkGroups"]:
        primary, *others = sorted(group)
        for path in others:
            by_path[path].update(Type="HardLink", Length=0, Sha256=None, Target=primary)
    packages = [{k: p[k] for k in ("Name", "Version", "Architecture", "DpkgStatus")} for p in audit["Packages"]]
    return {"SchemaVersion": 2, "BuildId": audit["BuildId"], "Format": root_artifact.FORMAT,
            "MachineNeutralPolicy": root_artifact.NEUTRAL, "Entries": sorted(entries, key=lambda e: e["Path"]),
            "Packages": sorted(packages, key=lambda p: p["Name"])}


def evaluate(root, audit):
    errors = []
    if not audit["PackageStateQualified"]:
        errors.append("PackageStateNotQualified")
    manifest = candidate_manifest(root, audit)
    descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    view = None
    try:
        info = os.fstat(descriptor)
        view = root_artifact.RootView(descriptor, info.st_dev, info.st_ino, root_artifact.mount_id(descriptor))
        for check, operation in (
                ("Manifest", lambda: root_artifact.validate_manifest(root_artifact.canonical(manifest))),
                ("Filesystem", lambda: root_artifact.verify_tree(view, manifest)),
                ("Dpkg", lambda: root_artifact.verify_dpkg(view, manifest)),
                ("NeutralState", lambda: root_artifact.verify_neutral(view, manifest))):
            try:
                operation()
            except (OSError, ValueError) as error:
                errors.append(check + ":" + str(error))
    finally:
        if view is not None:
            view.close()
        os.close(descriptor)
    by_path = {e["Path"]: e for e in manifest["Entries"]}
    # Do not copy private-key bytes into evidence. Exact path + existing observation
    # is enough to explain why this source cannot be published.
    residues = sorted(p for p in by_path if p in (
        "/etc/hostname", "/etc/mailname", "/etc/apt/sources.list",
        "/etc/ssl/private/ssl-cert-snakeoil.key", "/etc/ssl/certs/ssl-cert-snakeoil.pem") or
        (p.startswith("/var/log/") and by_path[p]["Type"] == "File" and by_path[p]["Length"] > 0))
    return {"BuildId": audit["BuildId"], "PackageStateQualified": audit["PackageStateQualified"],
            "CandidateManifestSchema": 2, "PublicationChecksPassed": not errors, "Blockers": errors,
            "ObservedResiduePaths": residues, "SourceTreeMutated": False,
            "StreamProduced": False, "ProductionAuthentication": "Unsupported"}


if __name__ == "__main__":
    # The guest harness installs imports alongside this script; fixed build identity
    # comes from the read-only, externally pinned input manifest, never caller paths.
    from factory_root_observer import observe
    package_set = json.loads(Path("/input/bundle/package-set.json").read_bytes())
    directory = Path("/factory") / ("root-" + package_set["BuildId"])
    observed = observe(directory, package_set)
    result = evaluate(directory, observed)
    destination = Path("/factory-evidence") / package_set["BuildId"] / "publication-gate-v2.json"
    with destination.open("x") as stream:
        json.dump(result, stream, sort_keys=True); stream.flush(); os.fsync(stream.fileno())
    print(json.dumps(result), flush=True)
