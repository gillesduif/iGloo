"""Bounded, non-extracting newc/gzip initramfs reader.

No member is executed or materialized as a host path/device. Parsing integrity is
separate from kernel, input-closure and phase authorization checks.
"""
from dataclasses import dataclass
import hashlib
import posixpath
import re
import stat
import zlib

from isolation_policy import require

MAX_IMAGE = 512 * 1024 * 1024
MAX_EXPANDED = 1024 * 1024 * 1024
MAX_ENTRIES = 100000
MAX_MEMBER = 256 * 1024 * 1024


@dataclass(frozen=True)
class Member:
    path: str
    mode: int
    uid: int
    gid: int
    links: int
    inode: tuple
    data: bytes
    mtime: int = 0

    @property
    def sha256(self):
        return hashlib.sha256(self.data).hexdigest().upper()


def path_name(raw):
    require(raw.endswith(b'\0') and b'\0' not in raw[:-1], 'InitramfsNameTermination')
    try:
        name = raw[:-1].decode('utf-8', 'strict')
    except UnicodeError as error:
        raise ValueError('InitramfsNameEncoding') from error
    while name.startswith('./'):
        name = name[2:]
    require(name and not name.startswith('/') and
            (name == '.' or all(p not in ('', '.', '..') for p in name.split('/'))) and
            not any(ord(c) < 32 or ord(c) == 127 for c in name), 'InitramfsUnsafePath')
    return name


def _cpio(data, start, segment):
    offset = start
    members = []
    while True:
        require(len(members) < MAX_ENTRIES and offset + 110 <= len(data), 'InitramfsTruncatedHeader')
        header = data[offset:offset + 110]
        require(header[:6] in (b'070701', b'070702') and re.fullmatch(b'[0-9a-fA-F]{104}', header[6:]),
                'InitramfsCpioHeader')
        fields = [int(header[i:i + 8], 16) for i in range(6, 110, 8)]
        ino, mode, uid, gid, links, mtime, size, major, minor, rmajor, rminor, namesize, check = fields
        require(0 < namesize <= 4096 and size <= MAX_MEMBER, 'InitramfsMemberBudget')
        offset += 110
        require(offset + namesize <= len(data), 'InitramfsTruncatedName')
        name = path_name(data[offset:offset + namesize])
        offset += namesize
        padding = (- (offset - start)) % 4
        require(data[offset:offset + padding] == b'\0' * padding, 'InitramfsNamePadding')
        offset += padding
        require(offset + size <= len(data), 'InitramfsTruncatedMember')
        content = data[offset:offset + size]
        offset += size
        padding = (- (offset - start)) % 4
        require(data[offset:offset + padding] == b'\0' * padding, 'InitramfsDataPadding')
        offset += padding
        require(check == (sum(content) & 0xffffffff if header[:6] == b'070702' else 0), 'InitramfsCpioChecksum')
        if name == 'TRAILER!!!':
            require(size == 0, 'InitramfsTrailerData')
            return members, offset
        require(links > 0 and (stat.S_ISREG(mode) or stat.S_ISDIR(mode) or stat.S_ISLNK(mode)) and
                rmajor == rminor == 0, 'InitramfsSpecialOrSetIdMember')
        require(not stat.S_ISDIR(mode) or size == 0, 'InitramfsDirectoryData')
        require(name != '.' or stat.S_ISDIR(mode), 'InitramfsRootType')
        members.append(Member(name, mode, uid, gid, links, (segment, major, minor, ino), content, mtime))


def read_image(data, *, copied_setid=None):
    require(type(data) is bytes and 0 < len(data) <= MAX_IMAGE, 'InitramfsImageBudget')
    pending = data
    members = []
    expanded = 0
    segment = 0
    compressed = 0
    while pending:
        pending = pending.lstrip(b'\0')
        if not pending:
            break
        require(segment < 16, 'InitramfsSegmentBudget')
        if pending.startswith(b'\x1f\x8b'):
            decompressor = zlib.decompressobj(16 + zlib.MAX_WBITS)
            try:
                raw = decompressor.decompress(pending, MAX_EXPANDED - expanded + 1)
            except zlib.error as error:
                raise ValueError('InitramfsCompressionCorrupt') from error
            require(len(raw) <= MAX_EXPANDED - expanded and decompressor.eof and not decompressor.unconsumed_tail,
                    'InitramfsCompressionTruncatedOrBudget')
            expanded += len(raw)
            require(raw.startswith((b'070701', b'070702')), 'InitramfsCompressedFormat')
            parsed, end = _cpio(raw, 0, segment)
            require(not raw[end:].strip(b'\0'), 'InitramfsCompressedTrailingData')
            pending = decompressor.unused_data
            compressed += 1
        else:
            require(pending.startswith((b'070701', b'070702')), 'InitramfsUnsupportedSegment')
            parsed, end = _cpio(pending, 0, segment)
            expanded += end
            require(expanded <= MAX_EXPANDED, 'InitramfsExpandedBudget')
            pending = pending[end:]
        members.extend(parsed)
        require(len(members) <= MAX_ENTRIES, 'InitramfsEntryBudget')
        segment += 1
    require(compressed == 1 and members, 'InitramfsMainArchiveRequired')
    result = {}
    for member in members:
        if member.path in result:
            previous = result[member.path]
            exact_directory = stat.S_ISDIR(member.mode) and (member.mode, member.uid, member.gid) == (previous.mode, previous.uid, previous.gid)
            # The retained AMD and Intel early-cpio writers use their own umasks.
            # Only their shared directory headers may differ across segments.
            early_directory = member.path in ('.', 'kernel', 'kernel/x86', 'kernel/x86/microcode') and \
                member.inode[0] > previous.inode[0] and all(stat.S_ISDIR(m.mode) and m.uid == m.gid == 0 and
                    stat.S_IMODE(m.mode) in (0o700, 0o755) for m in (member, previous))
            require(exact_directory or early_directory, 'InitramfsDuplicateMember')
        result[member.path] = member
    groups = {}
    for member in result.values():
        if stat.S_ISREG(member.mode) and member.links > 1:
            groups.setdefault(member.inode, []).append(member)
    for group in groups.values():
        require(all((m.mode, m.uid, m.gid, m.links) ==
                    (group[0].mode, group[0].uid, group[0].gid, len(group)) for m in group),
                'InitramfsHardlinkSet')
        contents = [m.data for m in group if m.data]
        require(len(contents) <= 1, 'InitramfsHardlinkData')
        for member in group:
            result[member.path] = Member(member.path, member.mode, member.uid, member.gid,
                member.links, member.inode, contents[0] if contents else b'', member.mtime)
    # Resolve as an archive namespace, never with host realpath or extraction.
    destinations = {}
    for member in result.values():
        if member.mode & 0o6000:
            require(stat.S_ISREG(member.mode) and member.links == 1 and
                    (copied_setid or {}).get(member.path) ==
                    (member.uid, member.gid, stat.S_IMODE(member.mode), member.sha256),
                    'InitramfsSpecialOrSetIdMember')
        destination = resolve(result, member.path)
        if stat.S_ISLNK(member.mode):
            require(member.links == 1 and 0 < len(member.data) <= 4096 and b'\0' not in member.data,
                    'InitramfsLinkInvalid')
        else:
            previous = destinations.get(destination)
            require(previous is None or stat.S_ISDIR(member.mode) and stat.S_ISDIR(previous.mode) and
                    (member.mode, member.uid, member.gid) == (previous.mode, previous.uid, previous.gid),
                    'InitramfsAliasedDestination')
            destinations[destination] = member
    return result


def resolve(members, path):
    todo = path.split('/')
    current = []
    followed = 0
    while todo:
        part = todo.pop(0)
        if part in ('', '.'):
            continue
        if part == '..':
            require(current, 'InitramfsLinkEscape')
            current.pop()
            continue
        current.append(part)
        entry = members.get('/'.join(current))
        if entry is not None and stat.S_ISLNK(entry.mode):
            followed += 1
            require(followed <= 32, 'InitramfsLinkCycle')
            try:
                link = entry.data.decode('utf-8', 'strict')
            except UnicodeError as error:
                raise ValueError('InitramfsLinkEncoding') from error
            require(link and '\0' not in link, 'InitramfsLinkInvalid')
            current.pop()
            if link.startswith('/'):
                current = []
            todo = link.split('/') + todo
        elif todo:
            require(entry is None or stat.S_ISDIR(entry.mode), 'InitramfsNonDirectoryParent')
    return posixpath.join(*current) if current else '.'
