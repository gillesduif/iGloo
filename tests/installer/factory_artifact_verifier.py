#!/usr/bin/env python3
"""Fresh-process verifier for the real DEVELOPMENT artifact and its imported tree.

External descriptor pins are supplied separately. This is not a release authority,
a target lease issuer or a production NativeSupported stage.
"""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "distros/debian/native"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import configured_root as artifact
from factory_root_observer import digest, package_observation
from factory_neutralization_rehearsal import checkpoint, environment, SOURCE_AUDIT
import factory_neutral_audit


def subobject_hash(data, name):
    text = data.decode("utf-8"); marker = '"' + name + '":'
    start = text.index(marker) + len(marker)
    _, end = json.JSONDecoder().raw_decode(text[start:])
    return artifact.digest(text[start:start + end].encode())


def authenticate(descriptor, pin, now):
    artifact.require(pin["Use"] == "DevelopmentImportOnly" and pin["ProductionAuthentication"] == "Unsupported" and
                     artifact.digest(descriptor) == pin["DescriptorSha256"], "DevelopmentDescriptorPinMismatch")
    value = json.loads(descriptor, object_pairs_hook=artifact.unique_pairs)
    parse = lambda text: dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
    artifact.require(parse(pin["NotBeforeUtc"]) <= now < parse(pin["NotAfterUtc"]) and
                     parse(value["CreatedAtUtc"]) <= now < parse(value["SupportedUntilUtc"]) and
                     value["BuildId"] == pin["BuildId"] and value["PackageSet"]["PolicySha256"] == pin["PolicySha256"] and
                     value["SchemaVersion"] == 2 and value["Release"] == "trixie" and value["Architecture"] == "amd64", "ArtifactWindowOrProfile")
    artifact.require(value["Content"]["Path"] == "root.content" and value["Manifest"]["Path"] == "root.manifest.json" and
                     value["Content"]["Sha256"] == pin["ContentSha256"] and value["Manifest"]["Sha256"] == pin["ManifestSha256"] and
                     subobject_hash(descriptor, "PackageSet") == value["PackageSetSha256"], "ArtifactSourceOrContentBinding")
    evidence = value["Attestation"]
    artifact.require(evidence["BuildId"] == value["BuildId"] and evidence["PackageSetSha256"] == value["PackageSetSha256"] and
                     evidence["PolicySha256"] == pin["PolicySha256"] and evidence["ManifestSha256"] == pin["ManifestSha256"] and
                     evidence["ContentSha256"] == pin["ContentSha256"] and evidence["BuilderProfileSha256"] == subobject_hash(descriptor, "Builder"), "AttestationBinding")
    return value


def verify(workspace, pin_path, imported=False):
    _, package_set = environment(workspace)
    output = workspace / "artifact"; descriptor = (output / "descriptor.json").read_bytes()
    pin = json.loads(pin_path.read_bytes()); now = dt.datetime.now(dt.timezone.utc)
    value = authenticate(descriptor, pin, now)
    evidence = value["Attestation"]; neutral = evidence["Neutralization"]
    artifact.require(neutral["SourceBuildId"] == value["BuildId"] and neutral["SourcePreserved"] and neutral["ExactDeltaVerified"] and
                     neutral["PackageStateVerified"] and neutral["DerivationId"] == workspace.name and
                     neutral["PlanVersion"] == "debian-trixie-neutralization-2026-09-28-v2", "NeutralizationReceiptBinding")
    bindings = {"PlanSha256": "neutralization-plan.json", "ResultSha256": "neutralization-result.json",
                "PostObservationSha256": "verification-v2/neutral-root-observation.json",
                "WholeTreeAuditSha256": "verification-v2/whole-tree-neutral-audit.json"}
    artifact.require(all(digest(workspace / path) == neutral[field] for field, path in bindings.items()), "NeutralizationEvidenceChanged")
    plan = json.loads((workspace / "neutralization-plan.json").read_bytes())
    result = json.loads((workspace / "neutralization-result.json").read_bytes())
    artifact.require(plan["PlanVersion"] == neutral["PlanVersion"] and plan["SourceBuildId"] == value["BuildId"] and
                     plan["SourceAuditSha256"] == neutral["SourceObservationSha256"] == SOURCE_AUDIT and
                     plan["PackageSetSha256"] == neutral["SourcePackageSetFileSha256"] and
                     plan["PolicySha256"] == pin["PolicySha256"] and
                     artifact.digest(artifact.canonical(plan["Regeneration"])) == neutral["RegenerationContractSha256"] and
                     result["PlanSha256"] == neutral["PlanSha256"] and result["Outcome"] == "AppliedAndVerified" and
                     result["ExactDeltaVerified"] and evidence["DpkgReadbackSha256"] == neutral["PostObservationSha256"] and
                     evidence["NeutralStateReadbackSha256"] == neutral["WholeTreeAuditSha256"] and
                     evidence["DependencyReadbackSha256"] == subobject_hash(descriptor, "DependencyPolicy"),
                     "NeutralizationProvenanceMismatch")
    artifact.require(digest(Path("/input/bundle/package-set.json")) == neutral["SourcePackageSetFileSha256"] and
                     value["PackageSet"] == package_set and digest(Path(__file__)) == evidence["IndependentVerifierSha256"], "VerifierOrPackageSetChanged")
    observed = json.loads((workspace / "verification-v2/neutral-root-observation.json").read_bytes())
    artifact.require(artifact.digest(artifact.canonical(observed["Filesystem"])) == result["PostFilesystemObservationSha256"],
                     "NeutralizationResultReadbackMismatch")
    artifact.require(observed["PackageStateQualified"] and json.loads((workspace / "verification-v2/whole-tree-neutral-audit.json").read_bytes())["Qualified"], "IncompleteNeutralEvidence")
    data = (output / "root.manifest.json").read_bytes()
    stream = os.open(output / "root.content", os.O_RDONLY | os.O_NOFOLLOW)
    try:
        manifest = artifact.verify_source(data, stream, artifact.DevelopmentPin(value["BuildId"], pin["ManifestSha256"], pin["ContentSha256"],
                                         dt.datetime.fromisoformat(pin["NotAfterUtc"].replace("Z", "+00:00"))), now)
    finally: os.close(stream)
    artifact.require((output / "root.content").stat().st_size == value["Content"]["Length"] and
                     len(data) == value["Manifest"]["Length"] and manifest["Packages"] == sorted(value["ConfiguredPackages"], key=lambda p: p["Name"]), "ManifestDescriptorMismatch")
    root = workspace / ("import-target" if imported else "root")
    fd = os.open(root, os.O_DIRECTORY | os.O_NOFOLLOW); info = os.fstat(fd)
    view = artifact.RootView(fd, info.st_dev, info.st_ino, artifact.mount_id(fd))
    try:
        tree = artifact.verify_tree(view, manifest); packages_hash = artifact.verify_dpkg(view, manifest); artifact.verify_neutral(view, manifest)
    finally: view.close(); os.close(fd)
    packages = package_observation(root, package_set)
    artifact.require(packages["PackageStateQualified"] and len(packages["Packages"]) == 1594, "FreshPackageReadbackFailed")
    # Includes compressed/binary scanning and the exact system-account policy.
    identity = factory_neutral_audit.audit(root, manifest["Entries"], [b"igloo-factory", b"/input/bundle", value["BuildId"].encode(), b"9a4856c180c241468a74380f663ddf94"])
    artifact.require(identity["Qualified"], "FreshNeutralReadbackFailed")
    result = {"Result": "ArtifactValid", "Use": "DevelopmentImportOnly", "ProductionAuthentication": "Unsupported",
              "DescriptorSha256": pin["DescriptorSha256"], "ManifestSha256": pin["ManifestSha256"], "ContentSha256": pin["ContentSha256"],
              "FilesystemSha256": tree, "PackageStateSha256": packages_hash, "PackageCount": len(packages["Packages"]),
              "DpkgAuditClean": True, "PendingTriggers": [], "NeutralAudit": identity, "ImportedRoot": imported,
              "RootDevice": info.st_dev, "RootInode": info.st_ino, "VerifiedAtUtc": now.isoformat(), "VerifierSha256": digest(Path(__file__))}
    checkpoint(workspace, "import-independent-verification" if imported else "artifact-independent-verification", result)
    print(json.dumps(result), flush=True)


if __name__ == "__main__": verify(Path(sys.argv[1]), Path(sys.argv[2]), len(sys.argv) == 4 and sys.argv[3] == "imported")
