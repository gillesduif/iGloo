"""Exact create-new publication of validated lab UserData evidence.

This is a target-file primitive, not session authority. The canonical caller must
journal/reopen declaration() before publish(), and independently journal its
result afterwards. No arbitrary destination, overwrite, retry or cleanup API.
"""
import os
import stat

from debian_first_boot import INPUT_ROOT
from isolation_policy import canonical, digest, require
from target_files import beneath, mount_id
from userdata_contract import INPUT_RECEIPT, INPUT_EVIDENCE, validate_admitted_bundle

PARTS = str(INPUT_ROOT).lstrip('/').split('/')


def identity(fd):
    s = os.fstat(fd)
    require(stat.S_ISDIR(s.st_mode) and s.st_uid == 0 and s.st_gid == 0 and
            stat.S_IMODE(s.st_mode) & 0o022 == 0 and not os.listxattr(fd), 'UserDataAdmissionParentProtection')
    return [s.st_dev, s.st_ino, mount_id(fd), stat.S_IMODE(s.st_mode)]


def before(root):
    """Pin each existing protected parent; do not follow links or create outputs."""
    parents = []; fd = os.dup(root)
    try:
        parents.append({'Path': '/', 'Identity': identity(fd)})
        for i, part in enumerate(PARTS):
            try:
                child = beneath(fd, part, os.O_RDONLY | os.O_DIRECTORY)
            except FileNotFoundError:
                require(i >= 2, 'UserDataAdmissionSystemParentMissing')  # /var/lib already exists
                return {'Parents': parents, 'AbsentFrom': i, 'FilesAbsent': True}
            os.close(fd); fd = child
            parents.append({'Path': '/' + '/'.join(PARTS[:i+1]), 'Identity': identity(fd)})
        for name in (INPUT_RECEIPT, INPUT_EVIDENCE):
            try: os.stat(name, dir_fd=fd, follow_symlinks=False)
            except FileNotFoundError: continue
            raise ValueError('UserDataAdmissionAlreadyExists')
        return {'Parents': parents, 'AbsentFrom': len(PARTS), 'FilesAbsent': True}
    finally: os.close(fd)


def declaration(root, bundle, receipt, authority):
    checked = validate_admitted_bundle(bundle, receipt, authority)
    return {'SchemaVersion': 1, 'Policy': 'CreateNewLabUserDataAdmissionV1', 'Destination': str(INPUT_ROOT),
        'Before': before(root), 'AuthoritySha256': digest(authority), 'ResultSha256': checked['ResultSha256'],
        'Files': [{'Name': INPUT_EVIDENCE, 'Length': len(bundle), 'Sha256': digest(bundle), 'Uid': 0, 'Gid': 0, 'Mode': 0o644},
                  {'Name': INPUT_RECEIPT, 'Length': len(receipt), 'Sha256': digest(receipt), 'Uid': 0, 'Gid': 0, 'Mode': 0o644}]}


def read(fd, name):
    file = beneath(fd, name, os.O_RDONLY)
    try:
        s = os.fstat(file)
        require(stat.S_ISREG(s.st_mode) and s.st_nlink == 1 and (s.st_uid, s.st_gid, stat.S_IMODE(s.st_mode)) == (0, 0, 0o644) and
                not os.listxattr(file) and 0 < s.st_size <= 1024*1024, 'UserDataAdmissionFileMetadata')
        raw = os.pread(file, s.st_size+1, 0); end = os.fstat(file)
        require(len(raw) == s.st_size and (s.st_ino, s.st_dev, s.st_size, s.st_mtime_ns, s.st_ctime_ns) ==
                (end.st_ino, end.st_dev, end.st_size, end.st_mtime_ns, end.st_ctime_ns), 'UserDataAdmissionReadChanged')
        return raw
    finally: os.close(file)


def observe(root, authority):
    fd = os.dup(root)
    try:
        root_identity = identity(fd)
        for part in PARTS:
            child = beneath(fd, part, os.O_RDONLY | os.O_DIRECTORY); os.close(fd); fd = child
            current = identity(fd)
            require(current[0] == root_identity[0] and current[2] == root_identity[2], 'UserDataAdmissionMountChanged')
        bundle, receipt = read(fd, INPUT_EVIDENCE), read(fd, INPUT_RECEIPT)
        return {**validate_admitted_bundle(bundle, receipt, authority), 'InputDirectory': str(INPUT_ROOT),
                'DirectoryIdentity': identity(fd), 'ReceiptSha256': digest(receipt)}
    finally: os.close(fd)


def publish(root, bundle, receipt, authority, intent, revalidate):
    # Reopen/intent persistence belongs to the session, and must precede this
    # call. A failure after create-new is retained; this function never retries.
    revalidate()
    require(declaration(root, bundle, receipt, authority) == intent, 'UserDataAdmissionIntentChanged')
    fd = os.dup(root)
    try:
        for i, part in enumerate(PARTS):
            revalidate()
            if i >= intent['Before']['AbsentFrom']:
                os.mkdir(part, 0o755, dir_fd=fd)
                child = beneath(fd, part, os.O_RDONLY | os.O_DIRECTORY)
                os.fchown(child, 0, 0); os.fchmod(child, 0o755); os.fsync(child); os.fsync(fd)
            else:
                child = beneath(fd, part, os.O_RDONLY | os.O_DIRECTORY)
                require(identity(child) == intent['Before']['Parents'][i+1]['Identity'], 'UserDataAdmissionParentChanged')
            os.close(fd); fd = child
        for spec, raw in zip(intent['Files'], (bundle, receipt)):
            revalidate()
            # Bind the connected path again, including newly created parents.
            connected = beneath(root, '/'.join(PARTS), os.O_RDONLY | os.O_DIRECTORY)
            try: require(identity(connected) == identity(fd), 'UserDataAdmissionDetachedDirectory')
            finally: os.close(connected)
            output = beneath(fd, spec['Name'], os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            try:
                pending = memoryview(raw)
                while pending:
                    count = os.write(output, pending); require(count > 0, 'UserDataAdmissionShortWrite'); pending = pending[count:]
                os.fchown(output, 0, 0); os.fchmod(output, 0o644); os.fsync(output)
            finally: os.close(output)
            os.fsync(fd)
            require(read(fd, spec['Name']) == raw, 'UserDataAdmissionReopenFailed')
        revalidate()
        return observe(root, authority)
    finally: os.close(fd)
