#!/usr/bin/env python3
"""Candidate semantic artifact mechanics, NOT a production session or CLI installer.

The only destination input is an already open root FD plus its independent witness.
Production has no dispatch to this module until the canonical supervisor can issue
that witness and supply a qualified root-only view. Tests use ordinary disposable
directories; neither a directory FD nor a development pin authorizes a real disk.

No tar extraction, target executables, mount, device, firmware or package scripts.
Exclusive trusted-supervisor ownership is required (host root is not an adversary
this FD-relative primitive can contain). Unknown metadata is rejected, not stripped.
"""
import base64
import datetime as dt
import hashlib
import json
import os
import re
import stat
import struct
import uuid
from collections import Counter
from dataclasses import dataclass

from target_files import beneath, mount_id
import target_observer
import deployment_journal
import configured_root_metadata as metadata_v2
import root_transport

MAGIC = b"IGLOO-SEMANTIC-ROOT-V1\n"
FORMAT = "igloo-semantic-root-stream-v1"
NEUTRAL = "debian-trixie-machine-neutral-v1"
MAX_BYTES = 64 * 1024 ** 3
MAX_ENTRIES = 500000
MAX_MANIFEST = 128 * 1024 ** 2


class Rejected(ValueError):
    pass


def require(value, code):
    if not value:
        raise Rejected(code)


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def canonical(value):
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("ascii")


def unique_pairs(pairs):
    result = {}
    for name, value in pairs:
        require(name not in result, "DuplicateProperty")
        result[name] = value
    return result


def hash_value(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9A-F]{64}", value) is not None


def path_parts(path):
    require(isinstance(path, str) and 0 < len(path.encode("utf-8")) <= 4096 and
            path.startswith("/") and (all(32 <= ord(c) < 127 for c in path) or path in metadata_v2.UNICODE_PATHS) and
            ("\\" not in path or path in metadata_v2.ESCAPED_UNIT_PATHS),
            "UnsupportedPathEncoding")
    parts = path[1:].split("/") if path != "/" else []
    require(len(parts) <= 128 and all(p not in ("", ".", "..") and len(p) <= 255 for p in parts), "UnsafeManifestPath")
    return parts


def link_destination(path, target, schema=1):
    require(isinstance(target, str) and 0 < len(target) <= 4096 and "\\" not in target and
            all(ord(c) >= 32 and ord(c) != 127 for c in target), "UnsafeLinkEncoding")
    parts = [] if target.startswith("/") else path_parts(path)[:-1]
    for part in target.split("/"):
        if part in ("", "."):
            continue
        if part == "..":
            require(parts, "LinkEscape")
            parts.pop()
        else:
            parts.append(part)
    result = "/" + "/".join(parts)
    require(all(32 <= ord(c) < 127 for c in target) or
            (schema == 2 and result in metadata_v2.UNICODE_PATHS), "UnsafeLinkEncoding")
    require((schema == 2 and metadata_v2.runtime_link_allowed(path, target, result)) or
            not any(result == p or result.startswith(p + "/") for p in ("/proc", "/sys", "/dev", "/boot/efi")),
            "LinkToExcludedRuntime")
    return result


def attributes(entry, schema=1):
    require(isinstance(entry["Xattrs"], dict) and len(entry["Xattrs"]) <= 32, "InvalidXattrs")
    result = {}
    for name, encoded in entry["Xattrs"].items():
        require(isinstance(encoded, str) and len(encoded) <= 87384, "OversizedXattr")
        try:
            data = base64.b64decode(encoded, validate=True)
        except ValueError as error:
            raise Rejected("MalformedXattr") from error
        require(len(data) <= 65536 and base64.b64encode(data).decode() == encoded, "NonCanonicalXattr")
        if name == "security.capability":
            # Only the explicit reviewed iputils-ping CAP_NET_RAW v2 effective value.
            # Other package capabilities/ACLs need a new reviewed profile, not silent loss.
            require((entry["Type"] == "File" and entry["Path"] == "/usr/bin/ping" and
                    entry["Uid"] == 0 and entry["Gid"] == 0 and entry["Mode"] == 0o755 and
                    data == struct.pack("<IIIII", 0x02000001, 1 << 13, 0, 0, 0)) or
                    (schema == 2 and metadata_v2.capability_allowed(entry, data)), "UndeclaredCapability")
        elif schema == 2 and name in metadata_v2.ACL_NAMES:
            require(metadata_v2.acl_allowed(entry, name, data), "UndeclaredAcl")
        else:
            require(re.fullmatch(r"user\.[a-zA-Z0-9_.-]{1,120}", name) is not None and
                    entry["Type"] in ("File", "Directory"), "UnsupportedXattr")
        result[name] = data
    return result


def validate_manifest(data):
    require(0 < len(data) <= MAX_MANIFEST, "ManifestSize")
    value = json.loads(data, object_pairs_hook=unique_pairs)
    require(isinstance(value, dict) and set(value) == {"SchemaVersion", "BuildId", "Format", "MachineNeutralPolicy", "Entries", "Packages"}, "ManifestFields")
    require(type(value["SchemaVersion"]) is int and value["SchemaVersion"] in (1, 2) and value["Format"] == FORMAT and
            value["MachineNeutralPolicy"] == NEUTRAL and str(uuid.UUID(value["BuildId"])) == value["BuildId"] and
            uuid.UUID(value["BuildId"]).int != 0, "ManifestProfile")
    entries = value["Entries"]
    require(isinstance(entries, list) and 0 < len(entries) <= MAX_ENTRIES, "ManifestEntryCount")
    require(all(isinstance(e, dict) and set(e) == {"Path", "Type", "Uid", "Gid", "Mode", "Length", "Sha256", "Target", "Xattrs"}
                for e in entries), "EntryFields")
    for entry in entries:
        path_parts(entry["Path"])
        require(value["SchemaVersion"] == 2 or (entry["Path"].isascii() and "\\" not in entry["Path"]), "UnsupportedPathEncoding")
    paths = [e["Path"] for e in entries]
    require(paths == sorted(set(paths)) and paths[0] == "/", "DuplicateOrNonCanonicalEntryOrder")
    lookup = {e["Path"]: e for e in entries}
    logical = 0
    for e in entries:
        path, kind = e["Path"], e["Type"]
        require(kind in ("Directory", "File", "SymbolicLink", "HardLink"), "SpecialFileRejected")
        require(all(type(e[k]) is int and 0 <= e[k] < 2 ** 32 - 1 for k in ("Uid", "Gid")) and
                type(e["Mode"]) is int and 0 <= e["Mode"] <= 0o7777 and
                type(e["Length"]) is int and 0 <= e["Length"] <= MAX_BYTES, "InvalidMetadata")
        require(path != "/lost+found" and not path.startswith("/lost+found/"), "FilesystemScaffoldingNotArtifactContent")
        parts = path_parts(path)
        if parts:
            parent = "/" + "/".join(parts[:-1])
            require(parent in lookup and lookup[parent]["Type"] == "Directory", "ParentNotDirectory")
        else:
            require(kind == "Directory" and e["Mode"] == 0o755, "RootMetadata")
        for runtime in ("/dev", "/proc", "/sys", "/run", "/tmp", "/boot/efi"):
            require(not path.startswith(runtime + "/") and (path != runtime or kind == "Directory"), "RuntimeContentRejected")
        attrs = attributes(e, value["SchemaVersion"])
        if kind == "File":
            require(hash_value(e["Sha256"]) and e["Target"] is None, "FileMetadata")
            logical += e["Length"]
        else:
            require(e["Length"] == 0 and e["Sha256"] is None, "NonFileContent")
            if kind == "Directory":
                require(e["Target"] is None, "DirectoryTarget")
            elif kind == "SymbolicLink":
                require(e["Mode"] == 0o777 and not attrs, "LinkMetadata")
                link_destination(path, e["Target"], value["SchemaVersion"])
            else:
                target = e["Target"]
                require(target in lookup and lookup[target]["Type"] == "File" and target != path, "HardLinkOutsideManifest")
                require(not attrs and not lookup[target]["Xattrs"] and
                        all(e[k] == lookup[target][k] for k in ("Uid", "Gid", "Mode")), "HardLinkMetadata")
    require(logical <= MAX_BYTES, "LogicalSizeLimit")
    # Reject cycles and indirect magic/escape links, including links below a link target.
    for entry in entries:
        if entry["Type"] != "SymbolicLink":
            continue
        current = entry["Path"]
        seen = set()
        for _ in range(40):
            parts = path_parts(current)
            prefix = next(("/" + "/".join(parts[:i]) for i in range(1, len(parts) + 1)
                           if lookup.get("/" + "/".join(parts[:i]), {}).get("Type") == "SymbolicLink"), None)
            if prefix is None:
                break
            require(prefix not in seen, "SymbolicLinkCycle")
            seen.add(prefix)
            current = link_destination(prefix, lookup[prefix]["Target"], value["SchemaVersion"]) + current[len(prefix):]
        else:
            raise Rejected("SymbolicLinkDepth")
    packages = value["Packages"]
    require(isinstance(packages, list) and 0 < len(packages) <= 10000, "PackageManifestMissing")
    require(all(isinstance(p, dict) and set(p) == {"Name", "Version", "Architecture", "DpkgStatus"} and
                re.fullmatch(r"[a-z0-9][a-z0-9+.-]+", p["Name"]) and
                re.fullmatch(r"[0-9][A-Za-z0-9.+:~\-]*", p["Version"]) and p["Architecture"] in ("amd64", "all") and
                p["DpkgStatus"] == "install ok installed" for p in packages), "UnconfiguredPackage")
    require([p["Name"] for p in packages] == sorted({p["Name"] for p in packages}), "PackageOrderOrDuplicate")
    if value["SchemaVersion"] == 2:
        require(metadata_v2.package_bindings(entries).issubset(
            {(p["Name"], p["Version"], p["Architecture"]) for p in packages}), "MetadataPackageBinding")
    return value


@dataclass(frozen=True)
class DevelopmentPin:
    build_id: str
    manifest_sha256: str
    content_sha256: str
    expires_at: dt.datetime


def verify_source(manifest_bytes, content_fd, pin, now):
    require(isinstance(pin, DevelopmentPin) and now.tzinfo == dt.timezone.utc and
            pin.expires_at.tzinfo == dt.timezone.utc and now < pin.expires_at and
            hash_value(pin.manifest_sha256) and hash_value(pin.content_sha256), "ExternalDevelopmentPinRequired")
    require(digest(manifest_bytes) == pin.manifest_sha256, "ManifestHashMismatch")
    manifest = validate_manifest(manifest_bytes)
    require(manifest["BuildId"] == pin.build_id, "ArtifactBuildMismatch")
    source = root_transport.as_stream(content_fd)
    expected_size = len(MAGIC) + sum(e["Length"] for e in manifest["Entries"] if e["Type"] == "File")
    require(source.length == expected_size, "StreamSizeOrType")
    total = hashlib.sha256()
    offset = 0
    require(source.pread(len(MAGIC), 0) == MAGIC, "StreamFormat")
    total.update(MAGIC)
    offset += len(MAGIC)
    for entry in manifest["Entries"]:
        if entry["Type"] != "File":
            continue
        sha = hashlib.sha256()
        remaining = entry["Length"]
        while remaining:
            block = source.pread(min(65536, remaining), offset)
            require(block, "TruncatedContent")
            sha.update(block)
            total.update(block)
            offset += len(block)
            remaining -= len(block)
        require(sha.hexdigest().upper() == entry["Sha256"], "FileContentHashMismatch")
    require(total.hexdigest().upper() == pin.content_sha256, "ArtifactHashMismatch")
    source.check()
    return manifest


class RootView:
    """Low-level FD primitive; its witness must come from the trusted canonical session."""
    def __init__(self, descriptor, expected_device, expected_inode, expected_mount):
        self.fd = os.dup(descriptor)
        self.expected = (expected_device, expected_inode, expected_mount)
        self.connected = os.readlink(f"/proc/self/fd/{self.fd}")
        try:
            self.check()
        except (OSError, ValueError):
            self.close()
            raise

    def close(self):
        if self.fd is not None:
            os.close(self.fd)
            self.fd = None

    def check(self):
        info = os.fstat(self.fd)
        require(stat.S_ISDIR(info.st_mode) and (info.st_dev, info.st_ino, mount_id(self.fd)) == self.expected, "RootWitnessChanged")
        require(self.connected.startswith("/") and not self.connected.endswith(" (deleted)"), "DetachedRoot")
        # Opening from / with openat2 also rejects symlinks in the connected ancestry.
        parent = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
        try:
            # NO_XDEV is intentional for child paths, but the trusted root may itself be a mount.
            # Ancestor traversal uses individually no-follow directory FDs instead.
            for part in self.connected.strip("/").split("/"):
                next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
                os.close(parent)
                parent = next_fd
            current = os.fstat(parent)
            require((current.st_dev, current.st_ino, mount_id(parent)) == self.expected, "RootReplaced")
        finally:
            os.close(parent)
        with open("/proc/self/mountinfo", encoding="utf-8") as mounts:
            for line in mounts:
                encoded = line.split()[4]
                path = re.sub(r"\\([0-7]{3})", lambda m: chr(int(m[1], 8)), encoded)
                require(not path.startswith(self.connected.rstrip("/") + "/"), "UnexpectedChildMount")

    def parent(self, path):
        self.check()
        parts = path_parts(path)
        require(parts, "RootNotLeaf")
        return beneath(self.fd, "/".join(parts[:-1]) or ".", os.O_RDONLY | os.O_DIRECTORY), parts[-1]

    def empty(self):
        self.check()
        names = os.listdir(self.fd)
        require(set(names) <= {"lost+found"}, "RootNotFresh")
        if names:
            child = beneath(self.fd, "lost+found", os.O_RDONLY | os.O_DIRECTORY)
            try:
                info = os.fstat(child)
                require(info.st_uid == 0 and info.st_gid == 0 and stat.S_IMODE(info.st_mode) == 0o700 and
                        not os.listdir(child) and not os.listxattr(child), "UnprovenFilesystemScaffolding")
            finally:
                os.close(child)
        require(not os.listxattr(self.fd), "UnexpectedRootAttributes")


def _metadata(fd, entry, schema=1):
    os.fchown(fd, entry["Uid"], entry["Gid"])
    os.fchmod(fd, entry["Mode"])
    require(not os.listxattr(fd), "InheritedOrUnexpectedAttributes")
    for name, value in attributes(entry, schema).items():
        os.setxattr(fd, name, value, flags=os.XATTR_CREATE)
    os.fsync(fd)


def verify_capacity(view, manifest):
    entries = manifest["Entries"]
    logical = sum(e["Length"] for e in entries)
    capacity = os.fstatvfs(view.fd)
    # Conservative data/metadata reserve; ENOSPC remains an observed partial failure.
    require(capacity.f_bavail * capacity.f_frsize >= logical + len(entries) * 8192 + 16 * 1024 ** 2, "InsufficientImportCapacity")


def import_files(view, manifest, content_fd, checkpoint):
    """Caller has verified full source + reopened intent. No rollback or overwrite."""
    source = root_transport.as_stream(content_fd)
    view.empty()
    verify_capacity(view, manifest)
    entries = manifest["Entries"]
    for entry in sorted((e for e in entries if e["Type"] == "Directory" and e["Path"] != "/"),
                        key=lambda e: (e["Path"].count("/"), e["Path"])):
        parent, name = view.parent(entry["Path"])
        try:
            os.mkdir(name, 0o700, dir_fd=parent)
            os.fsync(parent)
        finally:
            os.close(parent)
    offset = len(MAGIC)
    count = 0
    for entry in entries:
        if entry["Type"] != "File":
            continue
        parent, name = view.parent(entry["Path"])
        try:
            descriptor = beneath(parent, name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            try:
                sha = hashlib.sha256()
                remaining = entry["Length"]
                while remaining:
                    block = source.pread(min(65536, remaining), offset)
                    require(block, "TruncatedDuringImport")
                    sha.update(block)
                    offset += len(block)
                    remaining -= len(block)
                    data = memoryview(block)
                    while data:
                        written = os.write(descriptor, data)
                        require(written > 0, "ShortWrite")
                        data = data[written:]
                require(sha.hexdigest().upper() == entry["Sha256"], "ChangedContentDuringImport")
                _metadata(descriptor, entry, manifest["SchemaVersion"])
            finally:
                os.close(descriptor)
            os.fsync(parent)
        finally:
            os.close(parent)
        count += 1
        if count % 64 == 0:
            checkpoint(count)
    # Links are objects, created only after all files. No imported symlink is traversed.
    for entry in entries:
        if entry["Type"] not in ("SymbolicLink", "HardLink"):
            continue
        parent, name = view.parent(entry["Path"])
        try:
            if entry["Type"] == "SymbolicLink":
                os.symlink(entry["Target"], name, dir_fd=parent)
                os.chown(name, entry["Uid"], entry["Gid"], dir_fd=parent, follow_symlinks=False)
            else:
                source, leaf = view.parent(entry["Target"])
                try:
                    os.link(leaf, name, src_dir_fd=source, dst_dir_fd=parent, follow_symlinks=False)
                finally:
                    os.close(source)
            os.fsync(parent)
        finally:
            os.close(parent)
    for entry in sorted((e for e in entries if e["Type"] == "Directory"), key=lambda e: -e["Path"].count("/")):
        descriptor = beneath(view.fd, entry["Path"][1:] or ".", os.O_RDONLY | os.O_DIRECTORY)
        try:
            _metadata(descriptor, entry, manifest["SchemaVersion"])
        finally:
            os.close(descriptor)
    view.check()
    os.fsync(view.fd)


def inspect_tree(view):
    """Fresh FD walk. Does not trust the importer or its output handles."""
    view.check()
    entries = []
    def walk(directory, path):
        info = os.fstat(directory)
        require(info.st_dev == view.expected[0] and mount_id(directory) == view.expected[2], "ReadbackMountChanged")
        entries.append((path, info, None, {n: os.getxattr(directory, n) for n in os.listxattr(directory)}, None))
        require(len(entries) <= MAX_ENTRIES, "ReadbackLimit")
        for name in sorted(os.listdir(directory)):
            child_path = path.rstrip("/") + "/" + name
            path_parts(child_path)
            if child_path == "/lost+found":
                lost = beneath(directory, name, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    data = os.fstat(lost)
                    require(data.st_uid == 0 and data.st_gid == 0 and stat.S_IMODE(data.st_mode) == 0o700 and
                            not os.listdir(lost) and not os.listxattr(lost), "ScaffoldingChanged")
                finally:
                    os.close(lost)
                continue
            before = os.stat(name, dir_fd=directory, follow_symlinks=False)
            if stat.S_ISLNK(before.st_mode):
                value = os.readlink(name, dir_fd=directory)
                link_path = f"/proc/self/fd/{directory}/" + name
                require(not os.listxattr(link_path, follow_symlinks=False), "SymlinkAttributes")
                entries.append((child_path, before, value, {}, None))
            elif stat.S_ISDIR(before.st_mode) or stat.S_ISREG(before.st_mode):
                child = beneath(directory, name, os.O_RDONLY | (os.O_DIRECTORY if stat.S_ISDIR(before.st_mode) else 0))
                try:
                    opened = os.fstat(child)
                    require((before.st_dev, before.st_ino) == (opened.st_dev, opened.st_ino), "ReadbackSubstitution")
                    if stat.S_ISDIR(before.st_mode):
                        walk(child, child_path)
                    else:
                        sha = hashlib.sha256()
                        require(before.st_size <= MAX_BYTES, "ReadbackFileLimit")
                        while data := os.read(child, 65536):
                            sha.update(data)
                        entries.append((child_path, before, None, {n: os.getxattr(child, n) for n in os.listxattr(child)}, sha.hexdigest().upper()))
                finally:
                    os.close(child)
            else:
                raise Rejected("ReadbackSpecialFile")
            after = os.stat(name, dir_fd=directory, follow_symlinks=False)
            require((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns) ==
                    (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns), "ReadbackChanged")
            require(len(entries) <= MAX_ENTRIES, "ReadbackLimit")
    walk(view.fd, "/")
    view.check()
    return entries


def verify_tree(view, manifest):
    actual = {p: (s, t, a, h) for p, s, t, a, h in inspect_tree(view)}
    expected = {e["Path"]: e for e in manifest["Entries"]}
    require(set(actual) == set(expected), "FilesystemSetMismatch")
    links = Counter(e["Target"] for e in manifest["Entries"] if e["Type"] == "HardLink")
    for path, entry in expected.items():
        info, target, attrs, sha = actual[path]
        require((info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode)) ==
                (entry["Uid"], entry["Gid"], entry["Mode"]), "FilesystemMetadataMismatch")
        kind = entry["Type"]
        require(stat.S_ISDIR(info.st_mode) if kind == "Directory" else
                stat.S_ISLNK(info.st_mode) if kind == "SymbolicLink" else stat.S_ISREG(info.st_mode), "FilesystemTypeMismatch")
        if kind == "File":
            require(info.st_size == entry["Length"] and sha == entry["Sha256"] and
                    info.st_nlink == 1 + links[path], "FileOrHardlinkMismatch")
        elif kind == "SymbolicLink":
            require(target == entry["Target"] and info.st_nlink == 1, "SymlinkMismatch")
        elif kind == "HardLink":
            other = actual[entry["Target"]][0]
            require((info.st_dev, info.st_ino, info.st_nlink) == (other.st_dev, other.st_ino, 1 + links[entry["Target"]]), "HardlinkMismatch")
        require(attrs == attributes(entry, manifest["SchemaVersion"]), "XattrMismatch")
    if manifest["SchemaVersion"] == 2 and any(e["Path"] == "/var/log/journal" and e["Xattrs"] for e in manifest["Entries"]):
        groups = [line.split(":") for line in read_small(view, "/etc/group").decode("utf-8").splitlines()]
        require(sum(len(g) == 4 and g[0] == "adm" and g[2] == "4" for g in groups) == 1 and
                sum(len(g) == 4 and g[0] == "systemd-journal" and g[2] == "997" for g in groups) == 1 and
                sum(len(g) == 4 and g[2] in ("4", "997") for g in groups) == 2, "JournalAclGroupBinding")
    return digest(canonical(manifest["Entries"]))


def read_small(view, path, limit=16 * 1024 * 1024):
    descriptor = beneath(view.fd, path[1:], os.O_RDONLY)
    try:
        info = os.fstat(descriptor)
        require(stat.S_ISREG(info.st_mode) and info.st_size <= limit, "StateFileTypeOrSize")
        data = os.read(descriptor, limit + 1)
        require(len(data) == info.st_size, "StateFileChanged")
        return data
    finally:
        os.close(descriptor)


def verify_dpkg(view, manifest):
    status = read_small(view, "/var/lib/dpkg/status").decode("utf-8")
    require(not re.search(r"(?mi)^Triggers-(Pending|Awaited):", status), "DpkgPendingTriggers")
    descriptor = beneath(view.fd, "var/lib/dpkg", os.O_RDONLY | os.O_DIRECTORY)
    try:
        packages = target_observer.package_state(descriptor)  # fresh dpkg-query, never chroot or scripts
        require(packages == manifest["Packages"], "DpkgPackageStateMismatch")
    finally:
        os.close(descriptor)
    paths = {e["Path"]: e for e in manifest["Entries"]}
    require(not any(p.startswith("/var/lib/dpkg/updates/") for p in paths), "DpkgUpdatesPending")
    if "/var/lib/dpkg/triggers/Unincorp" in paths:
        require(read_small(view, "/var/lib/dpkg/triggers/Unincorp") == b"", "DpkgUnincorporatedTriggers")
    # Diversions, alternatives, info and trigger files are covered by the exact filesystem
    # walk, not reconstructed from package stdout. Factory dpkg --audit is still mandatory.
    return digest(canonical(packages))


def pack_verified_fixture(view, manifest_bytes, output_fd):
    """Build-side mechanical packer, not a package builder or a publication authority.

    The expected manifest is independent input, not synthesized from an unqualified
    tree. Output must be a new empty regular file outside the source. The caller must
    independently reopen the result and supply an EXTERNAL pin to import it.
    """
    manifest = validate_manifest(manifest_bytes)
    verify_tree(view, manifest)
    verify_dpkg(view, manifest)
    verify_neutral(view, manifest)
    output = os.fstat(output_fd)
    require(stat.S_ISREG(output.st_mode) and output.st_size == 0 and output.st_nlink == 1,
            "PackOutputNotNewRegularFile")
    destination = os.readlink(f"/proc/self/fd/{output_fd}")
    require(destination.startswith("/") and not destination.endswith(" (deleted)") and
            not destination.startswith(view.connected.rstrip("/") + "/"), "PackOutputInsideSource")
    sha = hashlib.sha256()
    def write(data):
        sha.update(data)
        buffer = memoryview(data)
        while buffer:
            count = os.write(output_fd, buffer)
            require(count > 0, "ShortPackWrite")
            buffer = buffer[count:]
    write(MAGIC)
    for entry in manifest["Entries"]:
        if entry["Type"] != "File":
            continue
        source = beneath(view.fd, entry["Path"][1:], os.O_RDONLY)
        try:
            while data := os.read(source, 65536):
                write(data)
        finally:
            os.close(source)
    os.fsync(output_fd)
    verify_tree(view, manifest)
    return {"Length": os.fstat(output_fd).st_size, "Sha256": sha.hexdigest().upper(),
            "Qualification": "MechanicsFixtureOnly"}


def verify_neutral(view, manifest):
    entries = {e["Path"]: e for e in manifest["Entries"]}
    for name in ("/etc/machine-id", "/etc/fstab"):
        require(name in entries and entries[name]["Type"] == "File" and read_small(view, name) == b"", "MachineIdentityResidue")
    require(entries.get("/var/lib/dbus/machine-id", {}).get("Target") == "/etc/machine-id", "DbusIdentityRelationship")
    require(entries.get("/etc/resolv.conf", {}).get("Target") == "/run/NetworkManager/resolv.conf", "ResolverResidue")
    forbidden = ("/etc/hostname", "/etc/mailname", "/usr/sbin/policy-rc.d", "/var/lib/systemd/random-seed", "/boot/grub/grub.cfg",
                 "/etc/apt/igloo-offline.sources", "/etc/default/grub.d/99-igloo-no-os-prober.cfg",
                 "/etc/apt/apt.conf.d/99mmdebstrap",
                 "/etc/ssl/private/ssl-cert-snakeoil.key", "/etc/ssl/certs/ssl-cert-snakeoil.pem")
    require(not any(p in entries for p in forbidden), "BuildConfigurationResidue")
    for path, entry in entries.items():
        require(not (path in ("/initrd.img", "/initrd.img.old") or path.startswith("/boot/initrd.img-")),
                "FactoryInitramfsNotTargetEvidence")
        require(not (path.startswith("/etc/ssh/ssh_host_") or
                     (path.startswith("/etc/ssl/private/") and entry["Type"] != "Directory") or
                     path.startswith("/etc/NetworkManager/system-connections/") or
                     path.startswith("/var/lib/igloo/") or path.startswith("/var/lib/NetworkManager/") or
                     path.startswith("/root/.ssh/") or path.startswith("/home/") or
                     (path.startswith("/etc/apt/") and path.endswith((".list", ".sources")))), "MachineSpecificResidue")
        if path.startswith("/var/log/") and entry["Type"] != "Directory":
            require((entry["Type"] == "File" and entry["Length"] == 0) or
                    (manifest["SchemaVersion"] == 2 and path == "/var/log/README" and
                     entry["Type"] == "SymbolicLink" and entry["Target"] == "../../usr/share/doc/systemd/README.logs"), "BuildLogsRetained")
        # Actual factory discovery: exim/debconf retain the factory name even after
        # /etc/hostname is removed. Do not delete or edit package state to fake neutrality.
        if path in ("/etc/exim4/update-exim4.conf.conf", "/var/lib/exim4/config.autogenerated",
                    "/var/cache/debconf/config.dat", "/var/cache/debconf/config.dat-old"):
            require(b"igloo-factory" not in read_small(view, path), "FactoryHostnameInPackageState")
    # No reusable credentials, including locked hashes such as !<hash>.
    for line in read_small(view, "/etc/shadow").decode("utf-8").splitlines():
        fields = line.split(":")
        require(len(fields) == 9 and fields[1] and all(c in "!*" for c in fields[1]), "CredentialResidue")
    return digest(canonical({"Policy": NEUTRAL, "BuildId": manifest["BuildId"]}))


class ImportJournal:
    def __init__(self, store, generation, plan_hash, build_id):
        self.store, self.generation, self.plan_hash, self.build_id = store, generation, plan_hash, build_id
        self.used = False

    def begin(self):
        require(not self.used, "ImportIsSingleUse")
        self.used = True
        deployment_journal.perform(self.store, self.generation, self.plan_hash, "reserve")

    def record(self, outcome, **evidence):
        data = canonical({"SchemaVersion": 1, "GenerationId": self.generation, "PlanSha256": self.plan_hash,
                          "BuildId": self.build_id, "Operation": "ImportConfiguredRootMechanicsFixture",
                          "Outcome": outcome, "Evidence": evidence})
        reference = deployment_journal.perform(self.store, self.generation, self.plan_hash, "append", data).decode("ascii")
        require(deployment_journal.perform(self.store, self.generation, self.plan_hash, "read", reference=reference) == data,
                "ImportJournalReopenMismatch")


def import_development_fixture(view, manifest_bytes, content_fd, pin, journal, generation, plan_hash, now):
    """Explicit non-production harness. There is intentionally no path-based production CLI."""
    require(generation == journal.generation and plan_hash == journal.plan_hash and
            pin.build_id == journal.build_id, "ImportGenerationOrPlanMismatch")
    manifest = verify_source(manifest_bytes, content_fd, pin, now)
    view.empty()
    journal.begin()
    journal.record("IntentDurable", ManifestSha256=pin.manifest_sha256, ContentSha256=pin.content_sha256,
                   RootDevice=view.expected[0], RootInode=view.expected[1], MountId=view.expected[2])
    try:
        import_files(view, manifest, content_fd, lambda count: journal.record("IntentDurable", FilesWritten=count))
        tree = verify_tree(view, manifest)
        packages = verify_dpkg(view, manifest)
        neutral = verify_neutral(view, manifest)
        journal.record("AppliedAndVerified", FilesystemSha256=tree, PackageStateSha256=packages, NeutralStateSha256=neutral)
        return {"Outcome": "AppliedAndVerified", "Qualification": "MechanicsFixtureOnly"}
    except (OSError, ValueError):
        outcome = "OutcomeUnknown"
        observed = None
        try:
            partial = inspect_tree(view)
            observed = digest(canonical([(p, s.st_size, s.st_ino, s.st_mode, h) for p, s, _, _, h in partial]))
            outcome = "Failed"
        except (OSError, ValueError):
            pass  # Unavailable readback is not evidence of absence or successful cleanup.
        journal.record(outcome, PartialStateSha256=observed)
        raise
