"""Bounded, no-follow acquisition of exact guest completion record bytes.

This is a record-copy primitive, not mount or execution authority. The stage
producer supplies fixed roots; caller-selected paths never authorize effects.
"""
import hashlib
import os
from pathlib import Path
import re
import stat


NAME = re.compile(r'[A-Za-z0-9_.-]+\Z')
MAX_FILES = 4096
MAX_ENTRIES = 8192
MAX_BYTES = 16 * 1024 * 1024
MAX_FILE = 4 * 1024 * 1024
MAX_DEPTH = 12


def require(value, code):
    if not value:
        raise ValueError(code)


def _mount(fd):
    with open(f'/proc/self/fdinfo/{fd}', encoding='ascii') as info:
        values = [line.split()[1] for line in info if line.startswith('mnt_id:')]
    require(len(values) == 1, 'StageRecordMountUnavailable')
    return int(values[0])


def _identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid,
            info.st_gid, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def _protected(info, directory):
    require((stat.S_ISDIR(info.st_mode) if directory else
             stat.S_ISREG(info.st_mode)) and info.st_uid == 0 and
            not info.st_mode & 0o022 and
            (directory or info.st_nlink == 1),
            'StageRecordObjectUnprotected')


def _leaf(name):
    require(type(name) is str and len(name) <= 128 and
            NAME.fullmatch(name) is not None and name not in ('.', '..'),
            'StageRecordNameUnsupported')


def _directory(path):
    """Open an absolute directory one component at a time, never following links."""
    path = Path(path)
    require(path.is_absolute(), 'StageRecordRootRelative')
    fd = os.open('/', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        _protected(os.fstat(fd), True)
        for part in path.parts[1:]:
            _leaf(part)
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                            dir_fd=fd)
            try:
                _protected(os.fstat(child), True)
            except BaseException:
                os.close(child)
                raise
            os.close(fd); fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise


def _read_file(parent, name, device, mount, limit):
    _leaf(name)
    before_name = os.stat(name, dir_fd=parent, follow_symlinks=False)
    _protected(before_name, False)
    require(before_name.st_dev == device and before_name.st_size <= limit,
            'StageRecordFileBoundOrMountChanged')
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=parent)
    try:
        before = os.fstat(fd)
        require(_identity(before) == _identity(before_name) and
                _mount(fd) == mount, 'StageRecordFileSubstituted')
        data = bytearray()
        while block := os.read(fd, min(65536, limit + 1 - len(data))):
            data.extend(block)
            require(len(data) <= limit, 'StageRecordFileTooLarge')
        after = os.fstat(fd)
        after_name = os.stat(name, dir_fd=parent, follow_symlinks=False)
        require(len(data) == before.st_size and
                _identity(before) == _identity(after) == _identity(after_name) and
                _mount(fd) == mount, 'StageRecordFileChangedDuringRead')
        return bytes(data)
    finally:
        os.close(fd)


def read_file(path, *, limit=MAX_FILE):
    """Read one nominated root-owned file with an independently reopened parent."""
    path = Path(path)
    require(type(limit) is int and 0 <= limit <= 24 * 1024 * 1024,
            'StageRecordReadLimitChanged')
    parent = _directory(path.parent)
    try:
        before = os.fstat(parent); mount = _mount(parent)
        data = _read_file(parent, path.name, before.st_dev, mount, limit)
        reopened = _directory(path.parent)
        try:
            require(_identity(os.fstat(reopened)) == _identity(before) and
                    _mount(reopened) == mount, 'StageRecordParentSubstituted')
        finally:
            os.close(reopened)
        return data
    finally:
        os.close(parent)


def snapshot_tree(root, *, max_bytes=MAX_BYTES, max_file=MAX_FILE):
    """Return exact files and empty-directory names from one protected mount."""
    root = Path(root)
    require(type(max_bytes) is int and 0 <= max_bytes <= 128 * 1024 * 1024 and
            type(max_file) is int and 0 <= max_file <= 24 * 1024 * 1024,
            'StageRecordTreeLimitsChanged')
    fd = _directory(root)
    try:
        original = os.fstat(fd); device = original.st_dev; mount = _mount(fd)
        files, directories = {}, []
        total = 0

        def visit(parent, prefix, depth):
            nonlocal total
            require(depth <= MAX_DEPTH, 'StageRecordDepthBound')
            before_dir = os.fstat(parent)
            require(before_dir.st_dev == device and _mount(parent) == mount,
                    'StageRecordDirectoryMountChanged')
            leaves = sorted(os.listdir(parent))
            require(len(files) + len(directories) + len(leaves) <= MAX_ENTRIES,
                    'StageRecordEntryBound')
            for leaf in leaves:
                _leaf(leaf)
                name = prefix + leaf
                info = os.stat(leaf, dir_fd=parent, follow_symlinks=False)
                require(info.st_dev == device, 'StageRecordMountChanged')
                if stat.S_ISDIR(info.st_mode):
                    _protected(info, True)
                    child = os.open(leaf, os.O_RDONLY | os.O_DIRECTORY |
                                    os.O_NOFOLLOW, dir_fd=parent)
                    try:
                        require(_identity(os.fstat(child)) == _identity(info) and
                                _mount(child) == mount,
                                'StageRecordDirectorySubstituted')
                        directories.append(name)
                        visit(child, name + '/', depth + 1)
                        require(_identity(os.fstat(child)) == _identity(info) ==
                                _identity(os.stat(leaf, dir_fd=parent,
                                                  follow_symlinks=False)),
                                'StageRecordDirectoryChangedDuringRead')
                    finally:
                        os.close(child)
                else:
                    data = _read_file(parent, leaf, device, mount, max_file)
                    total += len(data)
                    require(len(files) < MAX_FILES and total <= max_bytes,
                            'StageRecordTreeBound')
                    files[name] = data
            require(_identity(os.fstat(parent)) == _identity(before_dir),
                    'StageRecordDirectoryChangedDuringRead')

        visit(fd, '', 0)
        reopened = _directory(root)
        try:
            require(_identity(os.fstat(reopened)) == _identity(original) and
                    _mount(reopened) == mount,
                    'StageRecordRootSubstituted')
        finally:
            os.close(reopened)
        return files, tuple(directories)
    finally:
        os.close(fd)


def copy_tree(source, destination):
    """Create-new, flush and independently read back a bounded journal copy."""
    source, destination = Path(source), Path(destination)
    files, dirs = snapshot_tree(source)
    parent = _directory(destination.parent)
    try:
        _leaf(destination.name)
        require(not destination.exists() and
                not destination.is_symlink(), 'StageRecordCopyDestinationExists')
        os.mkdir(destination.name, mode=0o700, dir_fd=parent)
        os.fsync(parent)
        root = os.open(destination.name, os.O_RDONLY | os.O_DIRECTORY |
                       os.O_NOFOLLOW, dir_fd=parent)
        try:
            for name in sorted(dirs, key=lambda value: (value.count('/'), value)):
                parts = name.split('/')
                current = os.dup(root)
                try:
                    for part in parts[:-1]:
                        child = os.open(part, os.O_RDONLY | os.O_DIRECTORY |
                                        os.O_NOFOLLOW, dir_fd=current)
                        os.close(current); current = child
                    os.mkdir(parts[-1], mode=0o700, dir_fd=current)
                    os.fsync(current)
                finally:
                    os.close(current)
            for name, data in sorted(files.items()):
                parts = name.split('/')
                current = os.dup(root)
                try:
                    for part in parts[:-1]:
                        child = os.open(part, os.O_RDONLY | os.O_DIRECTORY |
                                        os.O_NOFOLLOW, dir_fd=current)
                        os.close(current); current = child
                    output = os.open(parts[-1], os.O_WRONLY | os.O_CREAT |
                                     os.O_EXCL | os.O_NOFOLLOW, 0o600,
                                     dir_fd=current)
                    try:
                        with os.fdopen(output, 'wb', closefd=False) as stream:
                            stream.write(data); stream.flush(); os.fsync(stream.fileno())
                    finally:
                        os.close(output)
                    os.fsync(current)
                finally:
                    os.close(current)
            for name in sorted(dirs, key=lambda value: (-value.count('/'), value)):
                directory = os.dup(root)
                try:
                    for part in name.split('/'):
                        child = os.open(part, os.O_RDONLY | os.O_DIRECTORY |
                                        os.O_NOFOLLOW, dir_fd=directory)
                        os.close(directory); directory = child
                    os.fsync(directory)
                finally:
                    os.close(directory)
            os.fsync(root)
        finally:
            os.close(root)
    finally:
        os.close(parent)
    copied, copied_dirs = snapshot_tree(destination)
    require(copied == files and copied_dirs == dirs and
            snapshot_tree(source) == (files, dirs),
            'StageRecordCopyReadbackChanged')
    return files, dirs
