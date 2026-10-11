"""Independent, bounded readback of the fixture's complete offline-agent delta."""
import hashlib
import json
import os
import stat
import sys

from target_files import beneath, mount_id
from agent_offline import CONFIG, LINK, UNIT, WORKER, decode, digest, identity, parent, require, source


def scan_tree(root):
    """No-follow whole-fixture walk; no extraction, devices or mount crossing."""
    original = identity(root)
    entries = {}
    total = 0

    def visit(directory, prefix):
        nonlocal total
        for name in sorted(os.listdir(directory)):
            require(name not in ('.', '..') and '\x00' not in name, 'AgentFixtureName')
            path = prefix + '/' + name
            require(len(entries) < 256 and len(path) <= 512, 'AgentFixtureTraversalBound')
            info = os.stat(name, dir_fd=directory, follow_symlinks=False)
            base = {'Owner': info.st_uid, 'Group': info.st_gid, 'Mode': stat.S_IMODE(info.st_mode),
                    'Links': info.st_nlink}
            if stat.S_ISDIR(info.st_mode):
                child = beneath(directory, name, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    require(os.fstat(child).st_dev == original[0] and mount_id(child) == original[2],
                            'AgentFixtureMountCrossing')
                    base['Xattrs'] = sorted((key, digest(os.getxattr(child, key))) for key in os.listxattr(child))
                    entries[path] = {**base, 'Kind': 'Directory'}
                    visit(child, path)
                finally: os.close(child)
            elif stat.S_ISREG(info.st_mode):
                require(info.st_nlink == 1 and info.st_dev == original[0] and info.st_size <= 1024*1024,
                        'AgentFixtureFileUnsupported')
                file = beneath(directory, name, os.O_RDONLY)
                try:
                    start = os.fstat(file); h = hashlib.sha256(); length = 0
                    while data := os.read(file, 65536):
                        h.update(data); length += len(data); total += len(data)
                        require(total <= 4*1024*1024, 'AgentFixtureBytesBound')
                    end = os.fstat(file)
                    require((start.st_dev, start.st_ino, start.st_size, start.st_mtime_ns, start.st_ctime_ns) ==
                            (end.st_dev, end.st_ino, end.st_size, end.st_mtime_ns, end.st_ctime_ns) and
                            length == start.st_size, 'AgentFixtureReadChanged')
                    base['Xattrs'] = sorted((key, digest(os.getxattr(file, key))) for key in os.listxattr(file))
                    entries[path] = {**base, 'Kind': 'File', 'Length': length, 'Sha256': h.hexdigest().upper()}
                finally: os.close(file)
            elif stat.S_ISLNK(info.st_mode):
                entries[path] = {**base, 'Kind': 'SymbolicLink',
                                 'Target': os.readlink(name, dir_fd=directory), 'Xattrs': []}
            else:
                raise ValueError('AgentFixtureSpecialObject')
    visit(root, '')
    return entries


def observe(root, plan, worker, unit, before):
    actual = scan_tree(root)
    additions = {'/usr/lib/igloo', WORKER, UNIT}
    require(set(actual) == set(before) | additions and not additions & set(before), 'AgentFixtureDeltaPaths')
    for path, value in before.items():
        if path == '/usr/lib' and value['Kind'] == 'Directory':
            # The one declared new child directory increments this parent's
            # link count. All other recorded metadata remains identical.
            expected = {**value, 'Links': value['Links'] + 1}
        else:
            expected = value
        require(actual[path] == expected, 'AgentFixtureComplementChanged')
    require(actual['/usr/lib/igloo']['Kind'] == 'Directory' and
            (actual['/usr/lib/igloo']['Owner'], actual['/usr/lib/igloo']['Group'],
             actual['/usr/lib/igloo']['Mode'], actual['/usr/lib/igloo']['Xattrs']) == (0, 0, 0o755, []),
            'AgentCreatedParentChanged')
    files = []
    for spec, raw in zip(plan['Files'], (worker, unit)):
        observed = actual[spec['Destination']]
        require(observed['Kind'] == 'File' and observed['Links'] == 1 and observed['Xattrs'] == [] and
                (observed['Owner'], observed['Group'], observed['Mode'], observed['Length'], observed['Sha256']) ==
                (0, 0, 0o644, len(raw), digest(raw)), 'AgentInstalledFileChanged')
        files.append(spec)
    # Reopen the installed unit independently, preserving exact bytes as well
    # as its digest. The worker and link are never executed or activated here.
    systemd = parent(root, 'etc/systemd/system')
    try:
        fd = beneath(systemd, 'igloo-deployment.service', os.O_RDONLY)
        try: require(os.read(fd, len(unit)+1) == unit, 'AgentUnitBytesChanged')
        finally: os.close(fd)
    finally: os.close(systemd)
    require(CONFIG not in actual and LINK not in actual, 'AgentPrematureActivation')
    return {'GenerationId': plan['GenerationId'], 'OperationId': plan['OperationId'],
            'TargetRootId': plan['TargetRootId'], 'Files': files,
            'ConfigurationAbsent': True, 'EnableLinkAbsent': True,
            'SourceAndUnrelatedContentUnchanged': True,
            'WholeTreeEntriesBefore': len(before), 'WholeTreeEntriesAfter': len(actual),
            'WholeTreeDelta': sorted(additions)}


def main():
    if len(sys.argv) != 4:
        raise SystemExit('usage: agent_offline_observer.py ROOT PACKET BEFORE')
    packet = decode(open(sys.argv[2], 'rb').read())
    plan, worker, unit = source(packet)
    before = decode(open(sys.argv[3], 'rb').read())
    root = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try: result = observe(root, plan, worker, unit, before)
    finally: os.close(root)
    print(json.dumps(result, separators=(',', ':'), ensure_ascii=False))


if __name__ == '__main__':
    main()
