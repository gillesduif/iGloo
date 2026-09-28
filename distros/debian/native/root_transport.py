"""Physical FAT32 transport of an unchanged semantic stream; no source authority.

All production opens are beneath the canonical payload FD. The retained handles
and their mount/inode identities survive verification and import. Host root is
outside the exclusive-runtime threat model. No reconstructed stream is written.
"""
import hashlib
import json
import os
import re
import stat
import uuid

from target_files import beneath, mount_id

CHUNK_SIZE = 1073741824
MAX_CONTENT = 64 * CHUNK_SIZE
MAX_MANIFEST = 32768
BUFFER = 65536
RESERVE = 64 * 1024 * 1024


def require(ok, code):
    if not ok:
        raise ValueError(code)


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "DuplicateTransportField")
        result[key] = value
    return result


def fields(value, expected):
    require(type(value) is dict and set(value) == set(expected.split()), "TransportFields")


def number(value, low, high):
    require(type(value) is int and low <= value <= high, "TransportInteger")


def parse(data):
    require(0 < len(data) <= MAX_MANIFEST, "TransportManifestSize")
    def integer(token):
        require(token.isascii() and token.isdecimal() and len(token) <= 20, "TransportInteger")
        return int(token)
    try:
        value = json.loads(data, object_pairs_hook=unique, parse_int=integer)
    except RecursionError as error:
        raise ValueError("TransportNesting") from error
    fields(value, "Version Type DescriptorSha256 ManifestSha256 BuildId DerivationId ContentLength ContentSha256 ChunkSize Chunks")
    require(type(value["Version"]) is int and value["Version"] == 1 and value["Type"] == "Chunked", "TransportVersionOrType")
    for key in ("DescriptorSha256", "ManifestSha256", "ContentSha256"):
        require(type(value[key]) is str and re.fullmatch(r"[0-9A-F]{64}", value[key]), "TransportHash")
    for key in ("BuildId", "DerivationId"):
        require(type(value[key]) is str and str(uuid.UUID(value[key])) == value[key] and uuid.UUID(value[key]).int != 0, "TransportUuid")
    number(value["ContentLength"], 1, MAX_CONTENT)
    number(value["ChunkSize"], CHUNK_SIZE, CHUNK_SIZE)
    chunks = value["Chunks"]
    require(type(chunks) is list and len(chunks) == (value["ContentLength"] + CHUNK_SIZE - 1) // CHUNK_SIZE, "TransportChunkCount")
    for index, chunk in enumerate(chunks):
        fields(chunk, "Index Name Length Sha256")
        number(chunk["Index"], index, index)
        require(chunk["Name"] == f"root.content.{index:04d}", "TransportChunkName")
        number(chunk["Length"], min(CHUNK_SIZE, value["ContentLength"] - index * CHUNK_SIZE), min(CHUNK_SIZE, value["ContentLength"] - index * CHUNK_SIZE))
        require(type(chunk["Sha256"]) is str and re.fullmatch(r"[0-9A-F]{64}", chunk["Sha256"]), "TransportChunkHash")
    return value


def bind(value, plan):
    for key in ("BuildId", "DerivationId", "DescriptorSha256", "ManifestSha256", "ContentLength", "ContentSha256"):
        require(value[key] == plan[key], "TransportArtifactBinding")


def identity(fd):
    info = os.fstat(fd)
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, "TransportNotUniqueRegularFile")
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns, mount_id(fd))


class LogicalStream:
    """Bounded pread only; at most 64 chunk FDs, never fictitious stat/regular FD."""
    def __init__(self, descriptors, lengths, owned=False):
        self.fds = tuple(descriptors)
        self.lengths = tuple(lengths)
        self.owned = owned
        require(0 < len(self.fds) <= 64 and len(self.fds) == len(self.lengths), "TransportFdBudget")
        self.identities = tuple(identity(fd) for fd in self.fds)
        require(len({(i[0], i[1]) for i in self.identities}) == len(self.identities), "TransportObjectAlias")
        require(all(i[2] == size for i, size in zip(self.identities, self.lengths)), "TransportPhysicalLength")
        self.length = sum(self.lengths)
        require(0 < self.length <= MAX_CONTENT, "TransportLogicalLength")

    def check(self):
        require(tuple(identity(fd) for fd in self.fds) == self.identities, "TransportSourceChanged")

    def pread(self, count, offset):
        number(count, 0, BUFFER)
        number(offset, 0, self.length)
        self.check()
        remaining = min(count, self.length - offset)
        result = bytearray()
        start = 0
        for fd, size in zip(self.fds, self.lengths):
            if remaining and offset < start + size:
                local = offset - start
                amount = min(remaining, size - local)
                block = os.pread(fd, amount, local)
                require(len(block) == amount, "TransportShortRead")
                result.extend(block)
                remaining -= amount
                offset += amount
            start += size
        self.check()
        return bytes(result)

    def verify(self, expected, chunks=None):
        total = hashlib.sha256()
        for index, (fd, size) in enumerate(zip(self.fds, self.lengths)):
            sha = hashlib.sha256()
            offset = 0
            while offset < size:
                block = os.pread(fd, min(BUFFER, size - offset), offset)
                require(block, "TransportShortRead")
                offset += len(block)
                sha.update(block)
                total.update(block)
            require(not os.pread(fd, 1, size), "TransportExtraBytes")
            if chunks is not None:
                require(sha.hexdigest().upper() == chunks[index]["Sha256"], "TransportChunkHashMismatch")
        self.check()
        require(total.hexdigest().upper() == expected, "TransportAggregateHashMismatch")

    def close(self):
        if self.owned:
            for fd in self.fds:
                os.close(fd)
            self.owned = False


def as_stream(source):
    return source if isinstance(source, LogicalStream) else LogicalStream([source], [os.fstat(source).st_size])


def open_chunks(parent, folder, data, expected_hash, plan, expected_mount):
    require(digest(data) == expected_hash, "TransportManifestHashMismatch")
    value = parse(data)
    bind(value, plan)
    fds = []
    try:
        for chunk in value["Chunks"]:
            fd = beneath(parent, folder + "/" + chunk["Name"], os.O_RDONLY | os.O_CLOEXEC)
            fds.append(fd)
            require(identity(fd)[-1] == expected_mount, "TransportWrongMount")
        stream = LogicalStream(fds, [c["Length"] for c in value["Chunks"]], owned=True)
        stream.verify(plan["ContentSha256"], value["Chunks"])
        return stream
    except BaseException:
        for fd in fds:
            os.close(fd)
        raise


def required_capacity(lengths, allocation_unit, other_required_bytes):
    number(allocation_unit, 512, 65536)
    require(allocation_unit & (allocation_unit - 1) == 0, "PayloadAllocationUnit")
    number(other_required_bytes, 0, MAX_CONTENT)
    require(0 < len(lengths) <= 128, "PayloadObjectCount")
    total = other_required_bytes + RESERVE
    for length in lengths:
        number(length, 1, 0xffffffff)
        total += ((length + allocation_unit - 1) // allocation_unit) * allocation_unit
    require(total <= MAX_CONTENT + 1024 ** 3, "PayloadCapacityOverflow")
    return total


def produce(source_fd, output_parent_fd, output_name, binding):
    """Offline transport producer. Caller first authenticates original descriptor/source.

    New directory only, marker published last. Failures leave forensic partial output;
    absent transport.json is never a usable set. Existing output is never resumed.
    """
    require(re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", output_name), "TransportOutputName")
    source = as_stream(source_fd)
    require(source.length == binding["ContentLength"], "TransportProducerLength")
    source.verify(binding["ContentSha256"])
    os.mkdir(output_name, mode=0o700, dir_fd=output_parent_fd)
    directory = beneath(output_parent_fd, output_name, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        total = hashlib.sha256()
        chunks = []
        offset = 0
        while offset < source.length:
            index = len(chunks)
            length = min(CHUNK_SIZE, source.length - offset)
            name = f"root.content.{index:04d}"
            fd = beneath(directory, name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC, 0o600)
            sha = hashlib.sha256()
            try:
                remaining = length
                while remaining:
                    block = source.pread(min(BUFFER, remaining), offset)
                    require(block, "TransportProducerEof")
                    written = 0
                    while written < len(block):
                        count = os.write(fd, block[written:])
                        require(count > 0, "TransportProducerShortWrite")
                        written += count
                    total.update(block); sha.update(block)
                    remaining -= len(block); offset += len(block)
                os.fsync(fd)
            finally:
                os.close(fd)
            chunks.append({"Index": index, "Name": name, "Length": length, "Sha256": sha.hexdigest().upper()})
        source.check()
        require(total.hexdigest().upper() == binding["ContentSha256"], "TransportProducerAggregate")
        value = {key: binding[key] for key in ("BuildId", "DerivationId", "DescriptorSha256", "ManifestSha256", "ContentLength", "ContentSha256")}
        value.update(Version=1, Type="Chunked", ChunkSize=CHUNK_SIZE, Chunks=chunks)
        data = canonical(value)
        parse(data)
        # Independent handles/readback BEFORE publishing the transport manifest.
        stream = open_chunks(directory, ".", data, digest(data), binding, mount_id(directory))
        try:
            stream.verify(binding["ContentSha256"], chunks)
        finally:
            stream.close()
        fd = beneath(directory, "transport.json", os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC, 0o600)
        try:
            require(os.write(fd, data) == len(data), "TransportManifestShortWrite")
            os.fsync(fd)
        finally:
            os.close(fd)
        os.fsync(directory)
        os.fsync(output_parent_fd)
        reopened = beneath(directory, "transport.json", os.O_RDONLY | os.O_CLOEXEC)
        try:
            require(os.pread(reopened, MAX_MANIFEST + 1, 0) == data, "TransportPublicationReopen")
        finally:
            os.close(reopened)
        return value
    finally:
        os.close(directory)
