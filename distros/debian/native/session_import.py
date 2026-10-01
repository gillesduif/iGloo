"""Closed configured-root phase of the canonical mount supervisor.

No path/device API, target code, shell, format, bootstrap or ESP write. The trusted
importer needs CHOWN/FOWNER/DAC_OVERRIDE/SETFCAP to restore the reviewed metadata;
these are supervisor privileges, never capabilities granted to package scripts.
"""
import base64
import datetime as dt
import json
import os
import stat
import subprocess
from pathlib import Path

import configured_root as root
import root_transport as transport
from isolation_policy import ENVIRONMENT, canonical, digest, require
from package_broker import DirectoryLease
from target_files import beneath, mount_id
from isolation_observer import mount_records


def journal_store_observation(paths):
    require(len(paths) == 2 and paths[0] != paths[1], "SeparateImportJournalStoresRequired")
    observed = []
    mounts = mount_records(Path("/proc/self/mountinfo").read_text())
    for path in paths:
        lease = DirectoryLease(path)
        try:
            info = os.fstat(lease.fd)
            require(info.st_uid == 0 and stat.S_IMODE(info.st_mode) == 0o700, "ImportJournalStoreNotPrivate")
            selected = [m for m in mounts if m["Id"] == lease.mount]
            require(len(selected) == 1 and selected[0]["FileSystem"] == "ext4" and selected[0]["Root"] == "/" and
                    "rw" in selected[0]["Options"], "ImportJournalNotPersistentExt4")
            require(not any(m["Path"].startswith(path.rstrip("/") + "/") for m in mounts), "JournalStoreHasChildMount")
            observed.append({"Path": path, "Device": info.st_dev, "Inode": info.st_ino, "MountId": lease.mount,
                             "Major": os.major(info.st_dev), "Minor": os.minor(info.st_dev), "FileSystem": "EXT4"})
            lease.verify()
        finally:
            lease.close()
    require((observed[0]["Device"], observed[0]["Inode"]) != (observed[1]["Device"], observed[1]["Inode"]), "JournalStoresAlias")
    return observed


def import_mount_record(observed, receipt, lease, namespace, readonly):
    """Fresh external mountinfo/stat joined to the retained canonical FD, not a new resolver."""
    path = receipt["Path"]
    matches = [m for m in observed["Mounts"] if m["Path"] == path]
    require(observed["Namespace"] == namespace and len(matches) == 1 and
            not any(m["Propagation"] for m in observed["Mounts"]), "ImportNamespaceOrStackedMount")
    selected = matches[0]
    require(selected["Id"] == receipt["MountId"] and selected["Root"] == "/" and
            selected["Device"] == f"{lease.major}:{lease.minor}" and
            selected["FileSystem"] == ("vfat" if readonly else "ext4") and
            {"ro" if readonly else "rw", "nosuid", "nodev"} <= set(selected["Options"]), "ImportMountChanged")
    require(not any(m["Path"].startswith(path + "/") for m in observed["Mounts"]), "ImportHasChildMount")
    require([m for m in observed["Mounts"] if m["Device"] == selected["Device"]] == matches,
            "ImportSourceHasAliasMount")
    require(len(observed["Paths"]) == 1 and observed["Paths"][0]["Identity"] == receipt["Identity"] and
            observed["Paths"][0]["MountId"] == receipt["MountId"], "ImportPathChanged")


class ConnectedImportView:
    def __init__(self, session):
        self.session = session
        self.views = {}
        require((session.declaration.get("ImportPlan") is not None or
                 session.declaration.get("LabConfiguration") is not None or
                 session.declaration.get("LabInitramfs") is not None or
                 session.declaration.get("LabUserData") is not None or
                 (session.declaration.get("StorageSmoke") or {}).get("Scope") == "StorageSmoke") and
                [r["Role"] for r in session.supervisor.receipts] == ["Root", "Payload"], "ImportPhaseTopologyRequired")
        try:
            self.verify()
            for receipt in session.supervisor.receipts:
                # DirectoryLease's connected-path check is retained. No detached clones/bwrap.
                directory = DirectoryLease(receipt["Path"])
                try:
                    require(directory.fd is not None and directory.mount == receipt["MountId"] and
                            directory.identity == receipt["Identity"], "ImportConnectedDirectoryChanged")
                    self.views[receipt["Role"]] = root.RootView(directory.fd, *receipt["Identity"], receipt["MountId"])
                finally:
                    directory.close()
        except (OSError, ValueError):
            self.close()
            raise

    def verify(self):
        supervisor = self.session.supervisor
        supervisor._private()
        require([r["Role"] for r in supervisor.receipts] == ["Root", "Payload"], "ImportTopologyChanged")
        for receipt in supervisor.receipts:
            lease = self.session.blocks.leases[receipt["Role"]]
            supervisor._source(lease)  # shared .NET resolver, including preserved/unique set
            import_mount_record(supervisor._observe(receipt["Path"]), receipt, lease,
                                supervisor.namespace, receipt["Role"] == "Payload")
            require(mount_id(receipt["Descriptor"]) == receipt["MountId"], "ImportReceiptDescriptorChanged")
        for view in self.views.values():
            view.check()

    def close(self):
        for view in self.views.values():
            view.close()
        self.views.clear()


def file_identity(fd):
    info = os.fstat(fd)
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, "ImportSourceNotRegularUniqueFile")
    return [info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns, mount_id(fd)]


def read_bound(fd, limit):
    identity = file_identity(fd)
    require(0 < identity[2] <= limit, "ImportSourceSizeRejected")
    data = os.pread(fd, identity[2] + 1, 0)
    require(len(data) == identity[2] and file_identity(fd) == identity, "ImportSourceReadChanged")
    return data


def observe_import(declaration):
    """Fresh trusted process; only root/manifest read descriptors are inherited, no raw block FD."""
    request = declaration["ImportObservation"]
    manifest_bytes = read_bound(request["ManifestFd"], root.MAX_MANIFEST)
    require(digest(manifest_bytes) == request["ManifestSha256"], "ObserverManifestChanged")
    manifest = root.validate_manifest(manifest_bytes)
    view = root.RootView(request["RootFd"], *request["ExpectedRoot"])
    try:
        result = {"GenerationId": declaration["GenerationId"], "SessionId": declaration["SessionId"],
                  "PlanSha256": declaration["PlanSha256"], "ManifestSha256": request["ManifestSha256"],
                  "RootWitness": request["ExpectedRoot"]}
        if request.get("Partial") is True:
            entries = root.inspect_tree(view)
            result.update(HasContent=len(entries) > 1, PartialStateSha256=digest(canonical([
                [path, info.st_size, info.st_ino, info.st_mode, sha] for path, info, _, _, sha in entries])))
        else:
            result.update(FilesystemSha256=root.verify_tree(view, manifest), PackageStateSha256=root.verify_dpkg(view, manifest),
                          NeutralStateSha256=root.verify_neutral(view, manifest))
        print(canonical(result).decode("ascii"))
    finally:
        view.close()
    return 0


def independent_readback(session, view, manifest_fd, manifest_hash, partial=False):
    declaration = {**session.declaration, "ImportObservation": {"RootFd": view.fd, "ExpectedRoot": list(view.expected),
        "ManifestFd": manifest_fd, "ManifestSha256": manifest_hash, "Partial": partial}}
    observed = subprocess.run([session.runtime.python, "-I", "-B", session.declaration["EntryPath"]],
        input=canonical(declaration) + b"\n", capture_output=True, timeout=7200, env=ENVIRONMENT,
        pass_fds=(view.fd, manifest_fd), check=False)
    require(observed.returncode == 0 and 0 < len(observed.stdout) < 65536, "IndependentImportObserverUnavailable")
    evidence = json.loads(observed.stdout)
    require(all(evidence[k] == session.declaration[k] for k in ("GenerationId", "SessionId", "PlanSha256")) and
            evidence["RootWitness"] == list(view.expected) and evidence["ManifestSha256"] == manifest_hash,
            "IndependentImportObserverBindingChanged")
    # Preserve the actual independent observation, not just a mutator-produced success assertion.
    return {**evidence, "ObserverEvidence": evidence, "ObserverEvidenceSha256": digest(canonical(evidence))}


def perform_import(session):
    require(not session.import_attempted, "ImportSessionSingleUse")
    session.import_attempted = True
    plan = session.declaration["ImportPlan"]
    require(plan is not None, "ImportPlanRequired")
    kind = plan.get("Transport", "SingleFile")
    require(kind in ("SingleFile", "Chunked"), "UnknownRootTransport")
    require(kind != "SingleFile" or 0 < plan["ContentLength"] <= 0xffffffff, "ConfiguredRootContentExceedsFat32SingleFile")
    connected = ConnectedImportView(session)
    descriptors = []
    source = None
    intent = False
    try:
        view = connected.views["Root"]
        payload = connected.views["Payload"]
        folder = "configured-root/" + plan["BuildId"] + "/" + plan["DerivationId"]
        for name in ("descriptor.json", "root.manifest.json", "root.content" if kind == "SingleFile" else "transport.json"):
            descriptors.append(beneath(payload.fd, folder + "/" + name, os.O_RDONLY | os.O_CLOEXEC))
        descriptor_fd, manifest_fd, content_fd = descriptors
        identities = [file_identity(fd) for fd in descriptors]
        require(all(identity[-1] == payload.expected[2] for identity in identities), "ImportSourceWrongMount")
        descriptor = read_bound(descriptor_fd, 4 * 1024 * 1024)
        require(digest(descriptor) == plan["DescriptorSha256"], "ImportDescriptorChanged")
        transport_data = read_bound(content_fd, transport.MAX_MANIFEST) if kind == "Chunked" else None
        auth = session.channel.ask("AuthenticateImportSource", Descriptor=base64.b64encode(descriptor).decode("ascii"),
            **({"TransportManifest": base64.b64encode(transport_data).decode("ascii")} if transport_data is not None else {}))
        artifact = auth["Artifact"]
        expires = dt.datetime.fromisoformat(auth["ExpiresAt"]).astimezone(dt.timezone.utc)
        manifest_bytes = read_bound(manifest_fd, root.MAX_MANIFEST)
        require(len(manifest_bytes) == artifact["Manifest"]["Length"], "ImportSourceLengthChanged")
        if kind == "Chunked":
            source = transport.open_chunks(payload.fd, folder, transport_data, plan["TransportManifestSha256"], plan, payload.expected[2])
            physical = [i[2] for i in source.identities]
        else:
            require(plan.get("TransportManifestSha256") is None, "AmbiguousRootTransport")
            source = transport.as_stream(content_fd)
            physical = [source.length]
        require(source.length == artifact["Content"]["Length"], "ImportSourceLengthChanged")
        # Whole payload capacity, including explicit reserve for other mandatory payload.
        # Existing allocation/generation is never resized by the importer.
        capacity = os.fstatvfs(payload.fd)
        needed = transport.required_capacity(physical + [identities[0][2], identities[1][2]] +
            ([identities[2][2]] if kind == "Chunked" else []), capacity.f_frsize, plan.get("OtherPayloadBytes", 0))
        require(capacity.f_blocks * capacity.f_frsize >= needed and
                capacity.f_bavail * capacity.f_frsize >= transport.RESERVE, "PayloadCapacityInsufficient")
        pin = root.DevelopmentPin(plan["BuildId"], plan["ManifestSha256"], plan["ContentSha256"], expires)
        manifest = root.verify_source(manifest_bytes, source, pin, dt.datetime.now(dt.timezone.utc))
        require(sorted(manifest["Packages"], key=lambda p: p["Name"]) ==
                sorted(artifact["ConfiguredPackages"], key=lambda p: p["Name"]), "ArtifactPackageManifestMismatch")
        view.empty()
        root.verify_capacity(view, manifest)
        connected.verify()
        # Separate import store reservation and reopened intent precede session effect intent.
        session.channel.ask("ImportCheckpoint", Record={"Outcome": "IntentDurable", "SourceFiles": identities,
                            "ChunkFiles": list(source.identities), "TransportManifestSha256": plan.get("TransportManifestSha256"),
                            "RootWitness": list(view.expected)})
        intent = True
        session.channel.checkpoint({"State": "IntentDurable", "Operation": "ImportConfiguredRoot"})
        connected.verify()  # journal I/O is a race boundary
        require([file_identity(fd) for fd in descriptors] == identities, "ImportSourceChangedAfterIntent")
        source.check()
        root.import_files(view, manifest, source, lambda count: session.channel.ask("ImportCheckpoint",
            Record={"Outcome": "Progress", "FilesWritten": count}))
        source.verify(plan["ContentSha256"])
        require([file_identity(fd) for fd in descriptors] == identities, "ImportSourceChangedDuringImport")
        connected.verify()
        observation = independent_readback(session, view, manifest_fd, plan["ManifestSha256"])
        connected.verify()
        result = session.channel.ask("ImportCheckpoint", Record={"Outcome": "AppliedAndVerified", **observation})
        session.channel.checkpoint({"State": "AppliedAndVerified", "ImportResultReference": result["Reference"],
                                   "ImportResultSha256": result["Sha256"]})
        return {"Reference": result["Reference"], "Sha256": result["Sha256"],
                "GenerationId": session.declaration["GenerationId"], "PlanSha256": session.declaration["PlanSha256"],
                "BuildId": plan["BuildId"], "DerivationId": plan["DerivationId"],
                "Transport": kind, "TransportManifestSha256": plan.get("TransportManifestSha256"),
                "DescriptorSha256": plan["DescriptorSha256"], "Qualification": "DevelopmentImportOnly"}
    except (OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        if not intent and type(error) is ValueError and str(error) in (
                "TransportChunkHashMismatch", "TransportAggregateHashMismatch"):
            # A closed diagnostic checkpoint, not an import reservation or teardown.
            # The supervisor still poisons/exits; no retry is enabled by this record.
            session.channel.ask("ImportSourceRejected", Code=str(error))
        if intent:
            # An observer or checkpoint failure cannot be rewritten into "nothing changed".
            # If this publication also fails the prior durable intent remains OutcomeUnknown.
            outcome, evidence = "OutcomeUnknown", {}
            try:
                connected.verify()
                evidence = independent_readback(session, view, manifest_fd, plan["ManifestSha256"], partial=True)
                if evidence.get("HasContent") is True:
                    outcome = "Failed"
            except (OSError, ValueError, KeyError, subprocess.TimeoutExpired):
                pass
            session.channel.ask("ImportCheckpoint", Record={"Outcome": outcome, **evidence})
        raise
    finally:
        if source is not None:
            source.close()
        for fd in descriptors:
            os.close(fd)
        connected.close()
