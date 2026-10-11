"""Bounded mount evidence for read-only import-checkpoint inspection.

This grants no installation or successor authority. The caller must attach a
read-only loop and request ext4 noload; this observer checks the resulting
kernel state before any journal or root readback.
"""

import os
from pathlib import Path
import re
import stat
import hashlib
import json
import subprocess

from isolation_observer import mount_records
from target_files import mount_id


class MountRejected(ValueError):
    def __init__(self, code, observation):
        super().__init__(code)
        self.code = code
        self.observation = observation


def evaluate(observation, *, path, image, image_identity, namespace, offset, size):
    """Return individual predicates; never infer noload from presentation text."""
    mounts = observation['Mounts']
    selected = [m for m in mounts if m['Path'] == path]
    mounted = selected[0] if len(selected) == 1 else None
    loop = observation['Loop']
    checks = {
        'Namespace': observation['Namespace'] == namespace and observation['ParentNamespace'] != namespace,
        'OneExactMount': len(selected) == 1,
        'NoChildOrStackedMount': len(selected) == 1 and not any(
            m['Path'].startswith(path.rstrip('/') + '/') for m in mounts),
        'PrivatePropagation': mounted is not None and not mounted['Propagation'],
        'Ext4MountRoot': mounted is not None and mounted['FileSystem'] == 'ext4' and mounted['Root'] == '/',
        'ReadOnlyMount': mounted is not None and {'ro', 'nosuid', 'nodev', 'noexec'} <= set(mounted['Options'])
            and 'rw' not in mounted['Options'],
        # Linux reports an ext4 mount requested with noload as norecovery.
        'NoJournalReplay': mounted is not None and 'norecovery' in mounted['SuperOptions'],
        'MountDeviceAndDescriptor': mounted is not None and mounted['Device'] == loop['Device']
            and mounted['Id'] == observation['MountId'] and observation['StatDevice'] == loop['StatDevice'],
        'NoDeviceAlias': mounted is not None and [m for m in mounts if m['Device'] == mounted['Device']] == selected,
        'LoopReadOnly': loop['ReadOnly'] is True,
        'LoopSourceAndExtent': loop['BackingFile'] == image and loop['Offset'] == offset
            and loop['SizeLimit'] == size and loop['Source'] == observation['MountSource']
            and loop['SourceDevice'] == loop['Device'],
        'ImageIdentity': observation['ImageIdentity'] == list(image_identity),
    }
    return checks


def require_mount(observation, **expected):
    checks = evaluate(observation, **expected)
    for name, passed in checks.items():
        if not passed:
            raise MountRejected(name, {**observation, 'Predicates': checks, 'FailedPredicate': name})
    return {**observation, 'Predicates': checks, 'FailedPredicate': None}


def observe(path, image):
    path, image = str(path), str(image)
    raw = Path('/proc/self/mountinfo').read_text()
    mounts = mount_records(raw)
    selected = [m for m in mounts if m['Path'] == path]
    source = None
    selected_raw = None
    if len(selected) == 1:
        for line in raw.splitlines():
            fields = line.split(' ')
            if int(fields[0]) == selected[0]['Id']:
                source = fields[fields.index('-') + 2]
                selected_raw = line
                break
    loop = {'Source': source, 'BackingFile': None, 'Device': None, 'SourceDevice': None, 'StatDevice': None,
            'ReadOnly': False, 'Offset': None, 'SizeLimit': None}
    if source is not None and re.fullmatch(r'/dev/loop[0-9]+', source):
        sysfs = Path('/sys/class/block') / Path(source).name
        major, minor = map(int, (sysfs/'dev').read_text().strip().split(':'))
        loop.update(BackingFile=(sysfs/'loop/backing_file').read_text().strip(),
                    Device=f'{major}:{minor}', SourceDevice=f'{os.major(os.stat(source).st_rdev)}:{os.minor(os.stat(source).st_rdev)}',
                    StatDevice=os.makedev(major, minor),
                    ReadOnly=(sysfs/'ro').read_text().strip() == '1',
                    Offset=int((sysfs/'loop/offset').read_text()),
                    SizeLimit=int((sysfs/'loop/sizelimit').read_text()))
    image_info = os.lstat(image)
    if not stat.S_ISREG(image_info.st_mode) or image_info.st_nlink != 1:
        raise ValueError('InspectionImageTypeChanged')
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        current_mount_id = mount_id(descriptor)
    finally:
        os.close(descriptor)
    findmnt = json.loads(subprocess.check_output(
        ['findmnt', '-J', '-T', path, '-o', 'TARGET,SOURCE,FSTYPE,OPTIONS,MAJ:MIN'], text=True))
    return {'Namespace': os.stat('/proc/self/ns/mnt').st_ino,
            'ParentNamespace': os.stat(f'/proc/{os.getppid()}/ns/mnt').st_ino,
            'Mounts': mounts, 'MountSource': source, 'MountId': current_mount_id,
            'StatDevice': os.stat(path).st_dev, 'Loop': loop,
            'ImageIdentity': [image_info.st_dev, image_info.st_ino, image_info.st_size, image_info.st_mtime_ns],
            'MountinfoSha256': hashlib.sha256(raw.encode()).hexdigest().upper(),
            'SelectedRawMountinfo': selected_raw, 'Findmnt': findmnt}
