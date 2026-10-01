"""Create-new publication of one accepted image. Not an execution authority.

The canonical caller supplies connected, freshly observed directory descriptors,
an accepted candidate binding and durable intent/reopen before invoking publish.
Failure retains the exact partial destination; this primitive never retries or
cleans it up. Generator processes and their writable handles must be gone first.
"""
import hashlib
import os
import stat

from isolation_policy import require
from target_files import beneath, mount_id


RELEASE = '6.12.107+deb13-amd64'
LEAF = 'initrd.img-' + RELEASE
MAX_IMAGE = 512 * 1024 * 1024


def identity(fd):
    s = os.fstat(fd)
    return [s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns, s.st_ctime_ns, mount_id(fd)]


def candidate(fd):
    s = os.fstat(fd)
    require(stat.S_ISREG(s.st_mode) and s.st_nlink == 1 and
            (s.st_uid, s.st_gid, stat.S_IMODE(s.st_mode)) == (0, 0, 0o600) and
            not os.listxattr(fd) and 0 < s.st_size <= MAX_IMAGE, 'CandidatePublicationMetadata')
    before = identity(fd); h = hashlib.sha256(); offset = 0
    while data := os.pread(fd, min(1024 * 1024, before[2] - offset), offset):
        h.update(data); offset += len(data)
    require(offset == before[2] and identity(fd) == before, 'CandidateChangedDuringRead')
    return {'Identity': before, 'Length': offset, 'Sha256': h.hexdigest().upper()}


def parent(root_fd, expected_root, expected_boot=None):
    require(identity(root_fd) == expected_root, 'PublicationRootChanged')
    fd = beneath(root_fd, 'boot', os.O_RDONLY | os.O_DIRECTORY)
    try:
        s = os.fstat(fd)
        require((s.st_uid, s.st_gid, stat.S_IMODE(s.st_mode)) == (0, 0, 0o755) and
                not os.listxattr(fd), 'PublicationParentMetadata')
        binding = [s.st_dev, s.st_ino, mount_id(fd)]
        require(expected_boot is None or binding == expected_boot, 'PublicationParentChanged')
        return fd, binding
    except BaseException:
        os.close(fd); raise


def absent(fd):
    try: os.stat(LEAF, dir_fd=fd, follow_symlinks=False)
    except FileNotFoundError: return
    raise ValueError('InitramfsDestinationExists')


def declaration(root_fd, candidate_fd):
    root_identity = identity(root_fd)
    fd, boot = parent(root_fd, root_identity)
    try: absent(fd)
    finally: os.close(fd)
    return {'Policy': 'CreateNewInitramfsImageV1', 'Destination': '/boot/' + LEAF,
            'RootIdentity': root_identity, 'BootIdentity': boot,
            'Candidate': candidate(candidate_fd), 'Uid': 0, 'Gid': 0, 'Mode': 0o600}


def publish(root_fd, candidate_fd, intent, revalidate):
    """Caller must have independently reopened this exact intent first."""
    require(set(intent) == {'Policy', 'Destination', 'RootIdentity', 'BootIdentity', 'Candidate', 'Uid', 'Gid', 'Mode'} and
            (intent['Policy'], intent['Destination'], intent['Uid'], intent['Gid'], intent['Mode']) ==
            ('CreateNewInitramfsImageV1', '/boot/' + LEAF, 0, 0, 0o600), 'PublicationDeclarationChanged')
    require(candidate(candidate_fd) == intent['Candidate'], 'CandidateSubstituted')
    revalidate()
    fd, _ = parent(root_fd, intent['RootIdentity'], intent['BootIdentity'])
    try:
        absent(fd)
        output = beneath(fd, LEAF, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        try:
            h = hashlib.sha256(); offset = 0
            while offset < intent['Candidate']['Length']:
                data = os.pread(candidate_fd, min(1024 * 1024, intent['Candidate']['Length'] - offset), offset)
                require(bool(data), 'CandidateShortRead'); h.update(data)
                remaining = memoryview(data)
                while remaining:
                    count = os.write(output, remaining)
                    require(count > 0, 'PublicationShortWrite'); remaining = remaining[count:]
                offset += len(data)
            require(h.hexdigest().upper() == intent['Candidate']['Sha256'] and
                    candidate(candidate_fd) == intent['Candidate'], 'CandidateChangedDuringPublication')
            os.fchown(output, 0, 0); os.fchmod(output, 0o600); os.fsync(output)
            created = identity(output)
        finally: os.close(output)
        os.fsync(fd)
        revalidate()
        # Reopen through the connected root, not the retained parent/write handle.
        reopened_parent, _ = parent(root_fd, intent['RootIdentity'], intent['BootIdentity'])
        try:
            reopened = beneath(reopened_parent, LEAF, os.O_RDONLY)
            try:
                result = candidate(reopened)
                require(result['Identity'] == created and result['Length'] == intent['Candidate']['Length'] and
                        result['Sha256'] == intent['Candidate']['Sha256'], 'PublishedImageReadbackChanged')
                return result
            finally: os.close(reopened)
        finally: os.close(reopened_parent)
    finally: os.close(fd)
